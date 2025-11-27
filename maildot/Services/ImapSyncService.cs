using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using maildot.Data;
using maildot.Models;
using maildot.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.UI.Dispatching;
using MimeKit;
using Windows.UI;
using System.Diagnostics;
using Npgsql;
using System.Globalization;
using Float16 = Microsoft.ML.OnnxRuntime.Float16;

namespace maildot.Services;

public sealed class ImapSyncService : IAsyncDisposable
{
    private const int PageSize = 40;

    private readonly MailboxViewModel _viewModel;
    private readonly DispatcherQueue _dispatcher;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly Dictionary<string, IMailFolder> _folderCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _folderNextEndIndex = new(StringComparer.OrdinalIgnoreCase);

    private ImapClient? _client;
    private AccountSettings? _settings;
    private string? _password;
    private PostgresSettings? _pgSettings;
    private string? _pgPassword;
    private Task? _backgroundTask;
    private Task? _embeddingTask;

    private const int EmbeddingBatchSize = 256;
    private static readonly TimeSpan EmbeddingIdleDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan EmbeddingActiveDelay = TimeSpan.FromSeconds(5);
    private const int EmbeddingDim = 1024;

    public ImapSyncService(MailboxViewModel viewModel, DispatcherQueue dispatcher)
    {
        _viewModel = viewModel;
        _dispatcher = dispatcher;
    }

    public async Task StartAsync(AccountSettings settings, string password)
    {
        _settings = settings;
        _password = password;

        if (!await ConnectAsync("Connecting to IMAP…"))
        {
            return;
        }

        if (_viewModel.SelectedFolder is { } initialFolder)
        {
            await LoadFolderAsync(initialFolder.Id);
        }
    }

    public Task LoadFolderAsync(string folderId) => LoadFolderInternalAsync(folderId, allowReconnect: true);

    private async Task LoadFolderInternalAsync(string folderId, bool allowReconnect)
    {
        if (string.IsNullOrEmpty(folderId))
        {
            return;
        }

        IMailFolder? folder = null;
        string folderDisplay = folderId;
        Exception? failure = null;
        var shouldRetry = false;

        await _semaphore.WaitAsync(_cts.Token);
        try
        {
            if (_client == null)
            {
                throw new ServiceNotConnectedException();
            }

            if (!_folderCache.TryGetValue(folderId, out folder))
            {
                throw new InvalidOperationException("Folder could not be found on the server.");
            }

            folderDisplay = string.IsNullOrEmpty(folder.Name) ? folderId : folder.Name;
            await ReportStatusAsync($"Loading {folderDisplay}…", true);
            if (!folder.IsOpen)
            {
                await folder.OpenAsync(FolderAccess.ReadOnly, _cts.Token);
            }

            var messageCount = folder.Count;
            if (messageCount == 0)
            {
                _folderNextEndIndex[folderId] = -1;
                await EnqueueAsync(() =>
                {
                    _viewModel.SetMessages(folderDisplay, Array.Empty<EmailMessageViewModel>());
                    _viewModel.SetStatus("Folder is empty.", false);
                    _viewModel.SetLoadMoreAvailability(false);
                    _viewModel.SetRetryVisible(false);
                });
                return;
            }

            var endIndex = messageCount - 1;
            var startIndex = Math.Max(0, endIndex - PageSize + 1);
            var emailItems = await FetchMessagesAsync(folder, startIndex, endIndex);

            _folderNextEndIndex[folderId] = startIndex - 1;

            await EnqueueAsync(() =>
            {
                _viewModel.SetMessages(folderDisplay, emailItems);
                _viewModel.SetStatus("Mailbox is up to date.", false);
                _viewModel.SetLoadMoreAvailability(startIndex > 0);
                _viewModel.SetRetryVisible(false);
            });
            return;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            failure = ex;
            shouldRetry = allowReconnect && IsRecoverable(ex);
        }
        finally
        {
            try
            {
                if (folder?.IsOpen == true)
                {
                    await folder.CloseAsync(false, _cts.Token);
                }
            }
            catch
            {
            }

            _semaphore.Release();
        }

        if (shouldRetry && await TryReconnectAsync())
        {
            await LoadFolderInternalAsync(folderId, false);
            return;
        }

        if (failure != null)
        {
            await ReportStatusAsync($"Unable to load messages: {failure.Message}", false);
            await EnqueueAsync(() => _viewModel.SetRetryVisible(true));
        }
    }

    public Task LoadOlderMessagesAsync(string folderId) => LoadOlderMessagesInternalAsync(folderId, allowReconnect: true);

    private async Task LoadOlderMessagesInternalAsync(string folderId, bool allowReconnect)
    {
        if (string.IsNullOrEmpty(folderId))
        {
            return;
        }

        IMailFolder? folder = null;
        Exception? failure = null;
        var shouldRetry = false;

        await _semaphore.WaitAsync(_cts.Token);
        try
        {
            if (_client == null)
            {
                throw new ServiceNotConnectedException();
            }

            if (!_folderCache.TryGetValue(folderId, out folder))
            {
                throw new InvalidOperationException("Folder could not be found on the server.");
            }

            if (!_folderNextEndIndex.TryGetValue(folderId, out var nextEndIndex) || nextEndIndex < 0)
            {
                await ReportStatusAsync("No more messages to load.", false);
                await EnqueueAsync(() =>
                {
                    _viewModel.SetLoadMoreAvailability(false);
                    _viewModel.SetRetryVisible(false);
                });
                return;
            }

            var folderDisplay = string.IsNullOrEmpty(folder.Name) ? folderId : folder.Name;
            await ReportStatusAsync($"Loading older messages for {folderDisplay}…", true);
            if (!folder.IsOpen)
            {
                await folder.OpenAsync(FolderAccess.ReadOnly, _cts.Token);
            }

            var endIndex = nextEndIndex;
            var startIndex = Math.Max(0, endIndex - PageSize + 1);
            var emailItems = await FetchMessagesAsync(folder, startIndex, endIndex);

            _folderNextEndIndex[folderId] = startIndex - 1;

            await EnqueueAsync(() =>
            {
                _viewModel.AppendMessages(emailItems);
                _viewModel.SetStatus("Mailbox is up to date.", false);
                _viewModel.SetLoadMoreAvailability(startIndex > 0);
                _viewModel.SetRetryVisible(false);
            });
            return;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            failure = ex;
            shouldRetry = allowReconnect && IsRecoverable(ex);
        }
        finally
        {
            try
            {
                if (folder?.IsOpen == true)
                {
                    await folder.CloseAsync(false, _cts.Token);
                }
            }
            catch
            {
            }

            _semaphore.Release();
        }

        if (shouldRetry && await TryReconnectAsync())
        {
            await LoadOlderMessagesInternalAsync(folderId, false);
            return;
        }

        if (failure != null)
        {
            await ReportStatusAsync($"Unable to load earlier messages: {failure.Message}", false);
            await EnqueueAsync(() => _viewModel.SetRetryVisible(true));
        }
    }

    private async Task<bool> ConnectAsync(string statusMessage)
    {
        if (_settings == null || _password == null)
        {
            return false;
        }

        try
        {
            await ReportStatusAsync(statusMessage, true);

            _client?.Dispose();
            _client = new ImapClient();
            await _client.ConnectAsync(_settings.Server, _settings.Port, _settings.UseSsl, _cts.Token);
            await _client.AuthenticateAsync(_settings.Username, _password, _cts.Token);

            _folderCache.Clear();
            _folderNextEndIndex.Clear();

            await ReportStatusAsync("Loading folders…", true);
            var folders = await LoadFoldersAsync(_cts.Token);
            await EnqueueAsync(() =>
            {
                _viewModel.SetFolders(folders);
                _viewModel.SetRetryVisible(false);
            });

            _backgroundTask ??= Task.Run(() => BackgroundFetchLoopAsync(_cts.Token), _cts.Token);
            _embeddingTask ??= Task.Run(() => BackgroundEmbeddingLoopAsync(_cts.Token), _cts.Token);

            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            await ReportStatusAsync($"IMAP connection failed: {ex.Message}", false);
            await EnqueueAsync(() => _viewModel.SetRetryVisible(true));
            return false;
        }
    }

    private Task<bool> TryReconnectAsync() => ConnectAsync("Reconnecting to IMAP…");

    private static bool IsRecoverable(Exception ex) =>
        ex is ServiceNotConnectedException ||
        ex is ImapProtocolException ||
        ex is ImapCommandException ||
        ex is IOException;

    private async Task<IReadOnlyList<MailFolderViewModel>> LoadFoldersAsync(CancellationToken token)
    {
        var folders = new List<MailFolderViewModel>();
        if (_client == null)
        {
            return folders;
        }

        _folderCache.Clear();

        async Task AddFolderAsync(IMailFolder folder)
        {
            await folder.StatusAsync(StatusItems.Unread | StatusItems.Count, token);
            _folderCache[folder.FullName] = folder;

            var folderVm = new MailFolderViewModel(folder.FullName, folder.Name ?? folder.FullName)
            {
                UnreadCount = folder.Unread
            };

            folders.Add(folderVm);
        }

        await AddFolderAsync(_client.Inbox);

        var personalRoot = _client.PersonalNamespaces.Count > 0
            ? _client.GetFolder(_client.PersonalNamespaces[0])
            : null;

        if (personalRoot != null)
        {
            var subfolders = await personalRoot.GetSubfoldersAsync(false, token);
            foreach (var folder in subfolders.Where(f => !f.Attributes.HasFlag(FolderAttributes.NonExistent)))
            {
                await AddFolderAsync(folder);
            }
        }

        return folders;
    }

    private async Task<List<EmailMessageViewModel>> FetchMessagesAsync(IMailFolder folder, int startIndex, int endIndex)
    {
        var summaries = await folder.FetchAsync(startIndex, endIndex,
            MessageSummaryItems.Envelope | MessageSummaryItems.UniqueId | MessageSummaryItems.InternalDate,
            _cts.Token);

        await PersistMessagesAsync(folder, summaries);

        return summaries
            .OrderByDescending(s => s.InternalDate?.UtcDateTime ?? DateTime.MinValue)
            .Select(summary =>
            {
                var mailbox = summary.Envelope?.From?.OfType<MailboxAddress>().FirstOrDefault();
                var senderName = mailbox?.Name;
                var senderAddress = mailbox?.Address;
                var senderDisplay = !string.IsNullOrWhiteSpace(senderName)
                    ? senderName!
                    : senderAddress ?? "(Unknown sender)";

                var colorComponents = SenderColorHelper.GetColor(senderName, senderAddress);
                var messageColor = new Color
                {
                    A = 255,
                    R = colorComponents.R,
                    G = colorComponents.G,
                    B = colorComponents.B
                };

                return new EmailMessageViewModel
                {
                    Id = summary.UniqueId.Id.ToString(),
                    Subject = summary.Envelope?.Subject ?? "(No subject)",
                    Sender = senderDisplay,
                    SenderAddress = senderAddress ?? string.Empty,
                    SenderInitials = SenderInitialsHelper.From(senderName, senderAddress),
                    SenderColor = messageColor,
                    Preview = summary.Envelope?.Subject ?? string.Empty,
                    Received = summary.InternalDate?.DateTime ?? DateTime.UtcNow
                };
            })
            .ToList();
    }

    private async Task PersistMessagesAsync(IMailFolder folder, IList<IMessageSummary> summaries)
    {
        if (_settings == null || summaries.Count == 0)
        {
            return;
        }

        var db = await CreateDbContextAsync();
        if (db == null)
        {
            return;
        }

        await using (db)
        {
            var folderEntity = await EnsureFolderEntityAsync(db, folder, _cts.Token);
            if (folderEntity == null)
            {
                return;
            }

            var existingUids = await db.ImapMessages
                .Where(m => m.FolderId == folderEntity.Id)
                .Select(m => m.ImapUid)
                .ToHashSetAsync(_cts.Token);

            foreach (var summary in summaries)
            {
                var uid = (long)summary.UniqueId.Id;
                if (existingUids.Contains(uid))
                {
                    continue;
                }

                var mailbox = summary.Envelope?.From?.OfType<MailboxAddress>().FirstOrDefault();
                var senderName = TextCleaner.CleanNullable(mailbox?.Name) ?? string.Empty;
                var senderAddress = TextCleaner.CleanNullable(mailbox?.Address) ?? string.Empty;

                var messageId = summary.Envelope?.MessageId;
                if (string.IsNullOrWhiteSpace(messageId))
                {
                    messageId = $"uid:{uid}@{_settings.Server}";
                }
                messageId = TextCleaner.CleanNullable(messageId) ?? string.Empty;

                var received = summary.InternalDate?.ToUniversalTime() ?? DateTimeOffset.UtcNow;

                db.ImapMessages.Add(new ImapMessage
                {
                    FolderId = folderEntity.Id,
                    ImapUid = uid,
                    MessageId = messageId,
                    Subject = TextCleaner.CleanNullable(summary.Envelope?.Subject) ?? string.Empty,
                    FromName = senderName,
                    FromAddress = senderAddress,
                    ReceivedUtc = received,
                    Hash = $"{messageId}:{uid}"
                });
            }

            try
            {
                await db.SaveChangesAsync(_cts.Token);
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                // Another writer inserted the same message concurrently; safe to ignore.
            }
        }
    }

    private async Task BackgroundFetchLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (_client == null || !_client.IsConnected)
                {
                    if (!await TryReconnectAsync())
                    {
                        await Task.Delay(TimeSpan.FromMinutes(1), token);
                        continue;
                    }
                }

                var folders = GetFoldersByPriority();
                foreach (var folder in folders)
                {
                    if (token.IsCancellationRequested)
                    {
                        break;
                    }

                    await FetchMissingBodiesForFolderAsync(folder, token);
                }

                await Task.Delay(TimeSpan.FromMinutes(1), token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!IsRecoverable(ex))
                {
                    Debug.WriteLine(ex.ToString());
                }

                await Task.Delay(TimeSpan.FromMinutes(1), token);
                // next iteration of main loop will reconnect.
            }
        }
    }

    private async Task BackgroundEmbeddingLoopAsync(CancellationToken token)
    {
        QwenEmbedder? embedder = null;

        try
        {
            while (!token.IsCancellationRequested)
            {
                var db = await CreateDbContextAsync();
                if (db == null)
                {
                    await Task.Delay(EmbeddingIdleDelay, token);
                    continue;
                }

                List<MessageBody> bodies;
                await using (db)
                {
                    bodies = await db.MessageBodies
                        .AsNoTracking()
                        .Include(b => b.Message)
                        .Where(b => !db.MessageEmbeddings.Any(e => e.MessageId == b.MessageId))
                        .OrderByDescending(b => b.Message.ReceivedUtc)
                        .Take(EmbeddingBatchSize)
                        .ToListAsync(token);
                }

                var texts = bodies
                    .Select(b => (b.MessageId, Text: EmbeddingTextBuilder.BuildCombinedText(b.Message, b)))
                    .Where(t => !string.IsNullOrWhiteSpace(t.Text))
                    .ToList();

                if (texts.Count == 0)
                {
                    await Task.Delay(EmbeddingIdleDelay, token);
                    continue;
                }

                embedder ??= await QwenEmbedder.Build(QwenEmbedder.ModelId);
                var embeddings = embedder.EmbedBatch(texts.Select(t => t.Text));

                if (embeddings.Length == 0)
                {
                    await Task.Delay(EmbeddingIdleDelay, token);
                    continue;
                }

                var now = DateTimeOffset.UtcNow;
                var records = new List<EmbeddingInsert>(embeddings.Length);
                for (int i = 0; i < embeddings.Length && i < texts.Count; i++)
                {
                    var row = embeddings[i];
                    var vector = NormalizeVector(row);

                    records.Add(new EmbeddingInsert(
                        MessageId: texts[i].MessageId,
                        ChunkIndex: 0,
                        Vector: vector,
                        ModelVersion: QwenEmbedder.ModelId,
                        CreatedAt: now));
                }

                await InsertEmbeddingsAsync(records, token);

                await Task.Delay(EmbeddingActiveDelay, token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Embedding loop error: {ex}");
        }
        finally
        {
            embedder?.Dispose();
        }
    }

    private record EmbeddingInsert(int MessageId, int ChunkIndex, float[] Vector, string ModelVersion, DateTimeOffset CreatedAt);

    private static float[] NormalizeVector(Float16[] source)
    {
        var dim = EmbeddingDim;
        if (source.Length != dim)
        {
            throw new InvalidOperationException($"Embedding dimension mismatch: expected {dim}, got {source.Length}.");
        }

        var result = new float[dim];
        for (int i = 0; i < dim; i++) result[i] = (float)source[i];
        return result;
    }

    private async Task InsertEmbeddingsAsync(List<EmbeddingInsert> records, CancellationToken token)
    {
        if (records.Count == 0)
        {
            return;
        }

        var db = await CreateDbContextAsync();
        if (db == null)
        {
            return;
        }

        await using (db)
        {
            var conn = (NpgsqlConnection)db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
            {
                await conn.OpenAsync(token);
            }

            await using var tx = await conn.BeginTransactionAsync(token);
            foreach (var rec in records)
            {
                var vectorLiteral = "[" +
                    string.Join(",", rec.Vector.Select(v => v.ToString("G9", CultureInfo.InvariantCulture))) + "]";

                await using var cmd = new NpgsqlCommand(
                    "INSERT INTO message_embeddings " +
                    "(\"MessageId\", \"ChunkIndex\", \"Vector\", \"ModelVersion\", \"CreatedAt\") " +
                    "VALUES (@mid, @chunk, @vec::halfvec, @ver, @created) ON CONFLICT DO NOTHING",
                    conn, tx);

                cmd.Parameters.AddWithValue("mid", rec.MessageId);
                cmd.Parameters.AddWithValue("chunk", rec.ChunkIndex);
                cmd.Parameters.AddWithValue("ver", rec.ModelVersion ?? string.Empty);
                cmd.Parameters.AddWithValue("created", rec.CreatedAt);
                cmd.Parameters.AddWithValue("vec", vectorLiteral);

                await cmd.ExecuteNonQueryAsync(token);
            }

            await tx.CommitAsync(token);
        }
    }

    private List<IMailFolder> GetFoldersByPriority() =>
        _folderCache.Values
            .OrderBy(f => GetFolderPriority(f.FullName))
            .ThenBy(f => f.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static int GetFolderPriority(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return 1;
        }

        var upper = name.Trim().ToUpperInvariant();
        if (upper == "INBOX")
        {
            return 0;
        }

        if (upper.StartsWith("DELETED", StringComparison.OrdinalIgnoreCase) ||
            upper.StartsWith("TRASH", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        return 1;
    }

    private async Task FetchMissingBodiesForFolderAsync(IMailFolder folder, CancellationToken token)
    {
        if (_settings == null)
        {
            return;
        }
        var db = await CreateDbContextAsync();
        if (db == null)
        {
            return;
        }

        Models.ImapFolder? folderEntity;
        List<(long ImapUid, int Id)> knownMessages;
        HashSet<int> bodiesPresent;

        await using (db)
        {
            folderEntity = await EnsureFolderEntityAsync(db, folder, token);
            if (folderEntity == null)
            {
                return;
            }

            knownMessages = (await db.ImapMessages
                    .Where(m => m.FolderId == folderEntity.Id)
                    .Select(m => new { m.ImapUid, m.Id })
                    .ToListAsync(token))
                .Select(m => (m.ImapUid, m.Id))
                .ToList();

            var messageIds = knownMessages.Select(m => m.Id).ToList();

            bodiesPresent = await db.MessageBodies
                .Where(b => messageIds.Contains(b.MessageId))
                .Select(b => b.MessageId)
                .ToHashSetAsync(token);
        }

        List<UniqueId> serverUids;
        await _semaphore.WaitAsync(token);
        try
        {
            if (_client == null)
            {
                return;
            }

            if (!folder.IsOpen)
            {
                await folder.OpenAsync(FolderAccess.ReadOnly, token);
            }

            serverUids = (await folder.SearchAsync(SearchQuery.All, token)).ToList();
        }
        finally
        {
            try
            {
                if (folder.IsOpen)
                {
                    await folder.CloseAsync(false, token);
                }
            }
            catch
            {
            }

            _semaphore.Release();
        }

        var knownUids = knownMessages.Select(m => m.ImapUid).ToHashSet();

        var missingBodyUids = knownMessages
            .Where(m => !bodiesPresent.Contains(m.Id))
            .Select(m => m.ImapUid);

        var missingMessages = serverUids.Select(u => (long)u.Id).Where(uid => !knownUids.Contains(uid));

        var targets = missingBodyUids
            .Concat(missingMessages)
            .Distinct()
            .OrderByDescending(uid => uid)
            .ToList();

        if (folderEntity == null)
        {
            return;
        }

        var folderId = folderEntity.Id;

        foreach (var uid in targets)
        {
            if (token.IsCancellationRequested)
            {
                break;
            }

            await FetchAndPersistBodyAsync(folder, folderId, uid, token);
        }
    }

    private async Task FetchAndPersistBodyAsync(IMailFolder folder, int folderId, long uid, CancellationToken token)
    {
        MimeMessage? message = null;
        var uniqueId = new UniqueId((uint)uid);

        await _semaphore.WaitAsync(token);
        try
        {
            if (_client == null)
            {
                return;
            }

            if (!folder.IsOpen)
            {
                await folder.OpenAsync(FolderAccess.ReadOnly, token);
            }

            message = await folder.GetMessageAsync(uniqueId, token);
        }
        catch
        {
        }
        finally
        {
            try
            {
                if (folder.IsOpen)
                {
                    await folder.CloseAsync(false, token);
                }
            }
            catch
            {
            }

            _semaphore.Release();
        }

        if (message == null)
        {
            return;
        }

        var db = await CreateDbContextAsync();
        if (db == null)
        {
            return;
        }

        await using (db)
        {
            var entity = await UpsertImapMessageAsync(db, folderId, message, uid, token);

            var hasBody = await db.MessageBodies.AnyAsync(b => b.MessageId == entity.Id, token);
            if (hasBody)
            {
                return;
            }

            var headers = message.Headers
                .GroupBy(h => h.Field)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(x => TextCleaner.CleanNullable(x.Value) ?? string.Empty).ToArray());

            var plain = TextCleaner.CleanNullable(message.TextBody);
            var html = TextCleaner.CleanNullable(message.HtmlBody);
            var sanitized = string.IsNullOrWhiteSpace(html) ? null : TextCleaner.CleanNullable(HtmlSanitizer.Sanitize(html).Html);
            var previewSource = string.IsNullOrWhiteSpace(plain) ? message.Subject ?? string.Empty : plain;
            var preview = BuildPreview(previewSource);

            MessageBody mb = new()
            {
                MessageId = entity.Id,
                PlainText = plain,
                HtmlText = html,
                SanitizedHtml = sanitized,
                Headers = headers,
                Preview = preview
            };
            db.MessageBodies.Add(mb);

            try
            {
                await db.SaveChangesAsync(token);
            }
            catch (DbUpdateException ex)
            {
                Debug.WriteLine(
                    $"Exception occurred while persisting message UID {uid} in folder {folder.FullName} from " +
                    $"{entity.FromAddress} dated {entity.ReceivedUtc:o} with subject \"{entity.Subject}\": {ex}");
            }
        }
    }

    private async Task<Models.ImapFolder?> EnsureFolderEntityAsync(MailDbContext db, IMailFolder folder, CancellationToken token)
    {
        if (_settings == null)
        {
            return null;
        }

        var fullName = TextCleaner.CleanNullable(folder.FullName);
        if (string.IsNullOrWhiteSpace(fullName))
        {
            return null;
        }

        var displayName = TextCleaner.CleanNullable(folder.Name) ?? fullName;
        var folderEntity = await db.ImapFolders
            .AsNoTracking()
            .Where(f => f.AccountId == _settings.Id && f.FullName == fullName)
            .OrderBy(f => f.Id)
            .FirstOrDefaultAsync(token);

        if (folderEntity != null)
        {
            return folderEntity;
        }

        folderEntity = new Models.ImapFolder
        {
            AccountId = _settings.Id,
            FullName = fullName,
            DisplayName = displayName
        };

        db.ImapFolders.Add(folderEntity);

        try
        {
            await db.SaveChangesAsync(token);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Another writer created the folder concurrently; re-query and return the existing row.
            folderEntity = await db.ImapFolders
                .AsNoTracking()
                .Where(f => f.AccountId == _settings.Id && f.FullName == fullName)
                .OrderBy(f => f.Id)
                .FirstOrDefaultAsync(token);
        }

        return folderEntity;
    }

    private ImapMessage CreateImapMessage(int folderId, MimeMessage message, long uid)
    {
        var sender = message.From.OfType<MailboxAddress>().FirstOrDefault();
        var senderName = TextCleaner.CleanNullable(sender?.Name) ?? string.Empty;
        var senderAddress = TextCleaner.CleanNullable(sender?.Address) ?? string.Empty;
        var rawMessageId = string.IsNullOrWhiteSpace(message.MessageId)
            ? $"uid:{uid}@{_settings?.Server}"
            : message.MessageId;
        var messageId = TextCleaner.CleanNullable(rawMessageId) ?? string.Empty;

        return new ImapMessage
        {
            FolderId = folderId,
            ImapUid = uid,
            MessageId = messageId,
            Subject = TextCleaner.CleanNullable(message.Subject) ?? string.Empty,
            FromName = senderName,
            FromAddress = senderAddress,
            ReceivedUtc = message.Date.ToUniversalTime(),
            Hash = $"{messageId}:{uid}"
        };
    }

    private static void UpdateImapMessage(ImapMessage entity, MimeMessage message)
    {
        var sender = message.From.OfType<MailboxAddress>().FirstOrDefault();
        entity.Subject = TextCleaner.CleanNullable(message.Subject) ?? string.Empty;
        entity.FromName = TextCleaner.CleanNullable(sender?.Name) ?? string.Empty;
        entity.FromAddress = TextCleaner.CleanNullable(sender?.Address) ?? string.Empty;
        entity.ReceivedUtc = message.Date.ToUniversalTime();
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    private async Task<ImapMessage> UpsertImapMessageAsync(MailDbContext db, int folderId, MimeMessage message, long uid, CancellationToken token)
    {
        var existing = await db.ImapMessages
            .FirstOrDefaultAsync(m => m.FolderId == folderId && m.ImapUid == uid, token);

        if (existing != null)
        {
            UpdateImapMessage(existing, message);
            await db.SaveChangesAsync(token);
            return existing;
        }

        var entity = CreateImapMessage(folderId, message, uid);
        db.ImapMessages.Add(entity);

        try
        {
            await db.SaveChangesAsync(token);
            return entity;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            db.Entry(entity).State = EntityState.Detached;
            var reloaded = await db.ImapMessages
                .FirstAsync(m => m.FolderId == folderId && m.ImapUid == uid, token);
            UpdateImapMessage(reloaded, message);
            await db.SaveChangesAsync(token);
            return reloaded;
        }
    }

    private static string BuildPreview(string? text)
    {
        if (text == null)
        {
            return string.Empty;
        }

        var trimmed = text.Replace("\r", " ").Replace("\n", " ").Trim();
        if (trimmed.Length > 240)
        {
            trimmed = trimmed[..240];
        }
        trimmed = TextCleaner.CleanNullable(trimmed);
        return string.IsNullOrWhiteSpace(trimmed) ? string.Empty : trimmed;
    }

    private async Task<MailDbContext?> CreateDbContextAsync()
    {
        try
        {
            _pgSettings ??= PostgresSettingsStore.Load();

            if (_pgSettings == null)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(_pgPassword))
            {
                var passwordResponse = await CredentialManager.RequestPostgresPasswordAsync(_pgSettings);
                if (passwordResponse.Result != CredentialAccessResult.Success ||
                    string.IsNullOrWhiteSpace(passwordResponse.Password))
                {
                    return null;
                }

                _pgPassword = passwordResponse.Password;
            }

            return MailDbContextFactory.CreateDbContext(_pgSettings, _pgPassword);
        }
        catch
        {
            return null;
        }
    }

    private Task ReportStatusAsync(string message, bool busy) =>
        EnqueueAsync(() => _viewModel.SetStatus(message, busy));

    private Task EnqueueAsync(Action action)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var enqueued = _dispatcher.TryEnqueue(() =>
        {
            try
            {
                action();
                completion.SetResult(true);
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });

        if (!enqueued)
        {
            completion.SetResult(true);
        }

        return completion.Task;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();

        if (_backgroundTask != null)
        {
            try { await _backgroundTask.ConfigureAwait(false); }
            catch { }
        }
        if (_embeddingTask != null)
        {
            try { await _embeddingTask.ConfigureAwait(false); }
            catch { }
        }

        _semaphore.Dispose();
        _pgPassword = null;
        _pgSettings = null;
        if (_client != null)
        {
            try
            {
                if (_client.IsConnected)
                {
                    await _client.DisconnectAsync(true);
                }
            }
            catch
            {
            }

            _client.Dispose();
            _client = null;
        }

        _settings = null;
        _password = null;
        _cts.Dispose();
    }

    public Task<string?> LoadMessageBodyAsync(string folderId, string messageId) =>
        LoadMessageBodyInternalAsync(folderId, messageId, true);

    private async Task<string?> LoadMessageBodyInternalAsync(string folderId, string messageId, bool allowReconnect)
    {
        if (string.IsNullOrEmpty(folderId) || string.IsNullOrEmpty(messageId))
        {
            return null;
        }

        var canRetry = allowReconnect;

        while (true)
        {
            IMailFolder? folder = null;
            await _semaphore.WaitAsync(_cts.Token);
            try
            {
                if (_client == null)
                {
                    throw new ServiceNotConnectedException();
                }

                if (!_folderCache.TryGetValue(folderId, out folder))
                {
                    return null;
                }

                if (!uint.TryParse(messageId, out var idValue))
                {
                    return null;
                }

                if (!folder.IsOpen)
                {
                    await folder.OpenAsync(FolderAccess.ReadOnly, _cts.Token);
                }

                var uniqueId = new UniqueId(idValue);
                var message = await folder.GetMessageAsync(uniqueId, _cts.Token);

                var html = message.HtmlBody;
                var plain = message.TextBody;

                string htmlToRender;
                if (!string.IsNullOrWhiteSpace(html))
                {
                    htmlToRender = HtmlSanitizer.Sanitize(html).Html;
                }
                else if (!string.IsNullOrWhiteSpace(plain))
                {
                    htmlToRender = $"<html><body><pre>{System.Net.WebUtility.HtmlEncode(plain)}</pre></body></html>";
                }
                else
                {
                    htmlToRender = "<html><body></body></html>";
                }

                return htmlToRender;
            }
            catch (Exception ex)
            {
                if (canRetry && IsRecoverable(ex) && await TryReconnectAsync())
                {
                    canRetry = false;
                    continue;
                }

                await ReportStatusAsync($"Unable to load message: {ex.Message}", false);
                await EnqueueAsync(() => _viewModel.SetRetryVisible(true));
                return null;
            }
            finally
            {
                try
                {
                    if (folder?.IsOpen == true)
                    {
                        await folder.CloseAsync(false, _cts.Token);
                    }
                }
                catch
                {
                }

                _semaphore.Release();
            }
        }
    }
}
