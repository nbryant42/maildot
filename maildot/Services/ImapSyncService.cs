using System;
using System.Buffers;
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
using System.Security.Cryptography;
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
    private QwenEmbedder? _searchEmbedder;

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

        _ = EnsureSearchEmbedderAsync();

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

    public Task SearchAsync(string query, SearchMode mode, DateTimeOffset? sinceUtc = null) =>
        SearchInternalAsync(query, mode, sinceUtc);

    private async Task SearchInternalAsync(string query, SearchMode mode, DateTimeOffset? sinceUtc)
    {
        var trimmed = TextCleaner.CleanNullable(query) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return;
        }

        if (_settings == null)
        {
            await ReportStatusAsync("Connect an account to search mail.", false);
            return;
        }

        await ReportStatusAsync($"Searching \"{trimmed}\"...", true);

        try
        {
            var db = await CreateDbContextAsync();
            if (db == null)
            {
                await ReportStatusAsync("PostgreSQL is required for search. Check settings.", false);
                return;
            }

            var effectiveMode = ResolveSearchMode(trimmed, mode);
            List<SearchResult> subjectResults = [];
            List<SearchResult> senderResults = [];
            List<SearchResult> vectorResults = [];

            await using (db)
            {
                if (ShouldSearchSubject(effectiveMode))
                {
                    subjectResults = await SearchBySubjectAsync(db, trimmed, sinceUtc, _cts.Token);
                }

                if (ShouldSearchSender(effectiveMode))
                {
                    senderResults = await SearchBySenderAsync(db, trimmed, sinceUtc, _cts.Token);
                }

                if (ShouldSearchVector(effectiveMode))
                {
                    vectorResults = await SearchByEmbeddingAsync(db, trimmed, sinceUtc, _cts.Token);
                }
            }

            var merged = MergeResults(subjectResults, senderResults, vectorResults);
            var status = merged.Count == 0
                ? "No matches found."
                : $"Showing {merged.Count} result{(merged.Count == 1 ? string.Empty : "s")}.";

            await EnqueueAsync(() =>
            {
                _viewModel.EnterSearchMode(trimmed);
                _viewModel.SetMessages($"Search: {trimmed}", merged);
                _viewModel.SetStatus(status, false);
                _viewModel.SetLoadMoreAvailability(false);
                _viewModel.SetRetryVisible(false);
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            await ReportStatusAsync($"Search failed: {ex.Message}", false);
        }
    }

    private static SearchMode ResolveSearchMode(string query, SearchMode mode)
    {
        if (mode != SearchMode.Auto)
        {
            return mode;
        }

        var hasAt = query.Contains("@", StringComparison.Ordinal);
        if (hasAt)
        {
            return SearchMode.Sender;
        }

        return SearchMode.All;
    }

    private static bool ShouldSearchSubject(SearchMode mode) =>
        mode == SearchMode.Subject || mode == SearchMode.All || mode == SearchMode.Auto;

    private static bool ShouldSearchSender(SearchMode mode) =>
        mode == SearchMode.Sender || mode == SearchMode.All || mode == SearchMode.Auto;

    private static bool ShouldSearchVector(SearchMode mode) =>
        mode == SearchMode.Content || mode == SearchMode.All || mode == SearchMode.Auto;

    private const int SubjectPriority = 0;
    private const int SenderPriority = 1;
    private const int VectorPriority = 2;

    private async Task<List<SearchResult>> SearchBySubjectAsync(MailDbContext db, string query, DateTimeOffset? sinceUtc, CancellationToken token)
    {
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync(token);
        }

        var subjectPattern = $"%{query}%";
        var sql = new StringBuilder();
        sql.Append("SELECT m.\"Id\", m.\"ImapUid\", m.\"Subject\", m.\"FromName\", m.\"FromAddress\", m.\"ReceivedUtc\", ");
        sql.Append("COALESCE(b.\"Preview\", ''), f.\"FullName\" ");
        sql.Append("FROM imap_messages m ");
        sql.Append("JOIN imap_folders f ON f.id = m.\"FolderId\" ");
        sql.Append("LEFT JOIN message_bodies b ON b.\"MessageId\" = m.\"Id\" ");
        sql.Append("WHERE f.\"AccountId\" = @accountId ");
        sql.Append("AND m.\"Subject\" ILIKE @subjectPattern ");
        if (sinceUtc.HasValue)
        {
            sql.Append("AND m.\"ReceivedUtc\" >= @sinceUtc ");
        }
        sql.Append("ORDER BY m.\"ReceivedUtc\" DESC LIMIT 50");

        await using var cmd = new NpgsqlCommand(sql.ToString(), conn);
        cmd.Parameters.AddWithValue("accountId", _settings!.Id);
        cmd.Parameters.AddWithValue("subjectPattern", subjectPattern);
        if (sinceUtc.HasValue)
        {
            cmd.Parameters.AddWithValue("sinceUtc", sinceUtc.Value);
        }

        var results = new List<SearchResult>();
        await using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var subject = TextCleaner.CleanNullable(reader.GetString(2)) ?? string.Empty;
            var fromName = TextCleaner.CleanNullable(reader.GetString(3)) ?? string.Empty;
            var fromAddress = TextCleaner.CleanNullable(reader.GetString(4)) ?? string.Empty;
            var preview = TextCleaner.CleanNullable(reader.IsDBNull(6) ? string.Empty : reader.GetString(6)) ?? string.Empty;
            var folderFullName = TextCleaner.CleanNullable(reader.GetString(7)) ?? string.Empty;

            results.Add(new SearchResult(
                MessageId: reader.GetInt32(0),
                ImapUid: reader.GetInt64(1),
                Subject: subject,
                FromName: fromName,
                FromAddress: fromAddress,
                ReceivedUtc: reader.GetFieldValue<DateTimeOffset>(5),
                Preview: preview,
                FolderFullName: folderFullName,
                Score: 0,
                Priority: SubjectPriority));
        }

        return results;
    }

    private async Task<List<SearchResult>> SearchBySenderAsync(MailDbContext db, string query, DateTimeOffset? sinceUtc, CancellationToken token)
    {
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync(token);
        }

        var terms = ExtractSenderTerms(query);

        var addressPattern = string.IsNullOrWhiteSpace(terms.Address) ? null : $"%{terms.Address}%";
        var namePattern = string.IsNullOrWhiteSpace(terms.Name) ? null : $"%{terms.Name}%";

        if (addressPattern == null && namePattern == null)
        {
            return [];
        }

        var sql = new StringBuilder();
        sql.Append("SELECT m.\"Id\", m.\"ImapUid\", m.\"Subject\", m.\"FromName\", m.\"FromAddress\", m.\"ReceivedUtc\", ");
        sql.Append("COALESCE(b.\"Preview\", ''), f.\"FullName\" ");
        sql.Append("FROM imap_messages m ");
        sql.Append("JOIN imap_folders f ON f.id = m.\"FolderId\" ");
        sql.Append("LEFT JOIN message_bodies b ON b.\"MessageId\" = m.\"Id\" ");
        sql.Append("WHERE f.\"AccountId\" = @accountId ");

        if (sinceUtc.HasValue)
        {
            sql.Append("AND m.\"ReceivedUtc\" >= @sinceUtc ");
        }

        var clauses = new List<string>();
        if (addressPattern != null)
        {
            clauses.Add("m.\"FromAddress\" ILIKE @addressPattern");
        }
        if (namePattern != null)
        {
            clauses.Add("m.\"FromName\" ILIKE @namePattern");
        }

        if (clauses.Count > 0)
        {
            sql.Append("AND (");
            sql.Append(string.Join(" OR ", clauses));
            sql.Append(") ");
        }

        sql.Append("ORDER BY m.\"ReceivedUtc\" DESC LIMIT 50");

        await using var cmd = new NpgsqlCommand(sql.ToString(), conn);
        cmd.Parameters.AddWithValue("accountId", _settings!.Id);
        if (sinceUtc.HasValue)
        {
            cmd.Parameters.AddWithValue("sinceUtc", sinceUtc.Value);
        }
        if (addressPattern != null)
        {
            cmd.Parameters.AddWithValue("addressPattern", addressPattern);
        }
        if (namePattern != null)
        {
            cmd.Parameters.AddWithValue("namePattern", namePattern);
        }

        var results = new List<SearchResult>();
        await using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var subject = TextCleaner.CleanNullable(reader.GetString(2)) ?? string.Empty;
            var fromName = TextCleaner.CleanNullable(reader.GetString(3)) ?? string.Empty;
            var fromAddress = TextCleaner.CleanNullable(reader.GetString(4)) ?? string.Empty;
            var preview = TextCleaner.CleanNullable(reader.IsDBNull(6) ? string.Empty : reader.GetString(6)) ?? string.Empty;
            var folderFullName = TextCleaner.CleanNullable(reader.GetString(7)) ?? string.Empty;

            results.Add(new SearchResult(
                MessageId: reader.GetInt32(0),
                ImapUid: reader.GetInt64(1),
                Subject: subject,
                FromName: fromName,
                FromAddress: fromAddress,
                ReceivedUtc: reader.GetFieldValue<DateTimeOffset>(5),
                Preview: preview,
                FolderFullName: folderFullName,
                Score: 0,
                Priority: SenderPriority));
        }

        return results;
    }

    private async Task<List<SearchResult>> SearchByEmbeddingAsync(MailDbContext db, string query, DateTimeOffset? sinceUtc, CancellationToken token)
    {
        var embedder = await EnsureSearchEmbedderAsync();
        if (embedder == null)
        {
            await ReportStatusAsync("Embedding model is unavailable.", false);
            return [];
        }

        var embedded = embedder.EmbedQuery(query);
        if (embedded == null)
        {
            return [];
        }

        var vector = NormalizeVector(embedded);
        var vectorLiteral = BuildVectorLiteral(vector);

        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync(token);
        }

        // This doesn't look like it will benefit from an index as written, due to the ORDER BY in the subquery,
        // but for now it's performing adequately.
        var vectorSql = new StringBuilder();
        vectorSql.Append("SELECT ranked.\"MessageId\", ranked.\"ImapUid\", ranked.\"Subject\", ranked.\"FromName\", ranked.\"FromAddress\", ");
        vectorSql.Append("ranked.\"ReceivedUtc\", ranked.\"Preview\", ranked.\"FullName\", ranked.negative_inner_product ");
        vectorSql.Append("FROM ( ");
        vectorSql.Append("    SELECT DISTINCT ON (me.\"MessageId\") ");
        vectorSql.Append("           me.\"MessageId\", ");
        vectorSql.Append("           me.\"Vector\" <#> @queryVec::halfvec AS negative_inner_product, ");
        vectorSql.Append("           m.\"ImapUid\", m.\"Subject\", m.\"FromName\", m.\"FromAddress\", m.\"ReceivedUtc\", ");
        vectorSql.Append("           COALESCE(b.\"Preview\", '') AS \"Preview\", f.\"FullName\" ");
        vectorSql.Append("    FROM message_embeddings me ");
        vectorSql.Append("    JOIN imap_messages m ON m.\"Id\" = me.\"MessageId\" ");
        vectorSql.Append("    JOIN imap_folders f ON f.id = m.\"FolderId\" ");
        vectorSql.Append("    LEFT JOIN message_bodies b ON b.\"MessageId\" = m.\"Id\" ");
        vectorSql.Append("    WHERE f.\"AccountId\" = @accountId ");
        if (sinceUtc.HasValue)
        {
            vectorSql.Append("AND m.\"ReceivedUtc\" >= @sinceUtc ");
        }
        vectorSql.Append("    ORDER BY me.\"MessageId\", negative_inner_product ");
        vectorSql.Append(") AS ranked ");
        vectorSql.Append("ORDER BY ranked.negative_inner_product ");
        vectorSql.Append("LIMIT 50");

        await using var cmd = new NpgsqlCommand(vectorSql.ToString(), conn);

        cmd.Parameters.AddWithValue("queryVec", vectorLiteral);
        cmd.Parameters.AddWithValue("accountId", _settings!.Id);
        if (sinceUtc.HasValue)
        {
            cmd.Parameters.AddWithValue("sinceUtc", sinceUtc.Value);
        }

        var results = new List<SearchResult>();
        await using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var subject = TextCleaner.CleanNullable(reader.GetString(2)) ?? string.Empty;
            var fromName = TextCleaner.CleanNullable(reader.GetString(3)) ?? string.Empty;
            var fromAddress = TextCleaner.CleanNullable(reader.GetString(4)) ?? string.Empty;
            var preview = TextCleaner.CleanNullable(reader.GetString(6)) ?? string.Empty;
            var folderFullName = TextCleaner.CleanNullable(reader.GetString(7)) ?? string.Empty;
            var negativeInnerProduct = reader.IsDBNull(8) ? double.MaxValue : reader.GetDouble(8);

            results.Add(new SearchResult(
                MessageId: reader.GetInt32(0),
                ImapUid: reader.GetInt64(1),
                Subject: subject,
                FromName: fromName,
                FromAddress: fromAddress,
                ReceivedUtc: reader.GetFieldValue<DateTimeOffset>(5),
                Preview: preview,
                FolderFullName: folderFullName,
                Score: negativeInnerProduct,
                Priority: VectorPriority));
        }

        return results;
    }

    private List<EmailMessageViewModel> MergeResults(params IEnumerable<SearchResult>[] resultSets)
    {
        var combined = new Dictionary<int, SearchResult>();

        void AddRange(IEnumerable<SearchResult> source)
        {
            foreach (var result in source)
            {
                if (combined.TryGetValue(result.MessageId, out var existing))
                {
                    if (result.Priority < existing.Priority ||
                        (result.Priority == existing.Priority && result.Score < existing.Score))
                    {
                        combined[result.MessageId] = result;
                    }

                    continue;
                }

                combined[result.MessageId] = result;
            }
        }

        foreach (var results in resultSets)
        {
            AddRange(results);
        }

        return combined.Values
            .OrderBy(r => r.Priority)
            .ThenBy(r => r.Score)
            .ThenByDescending(r => r.ReceivedUtc)
            .Take(50)
            .Select(BuildEmailViewModel)
            .ToList();
    }

    private EmailMessageViewModel BuildEmailViewModel(SearchResult result)
    {
        var color = SenderColorHelper.GetColor(result.FromName, result.FromAddress);
        var senderColor = new Color
        {
            A = 255,
            R = color.R,
            G = color.G,
            B = color.B
        };

        var senderDisplay = !string.IsNullOrWhiteSpace(result.FromName)
            ? result.FromName
            : result.FromAddress;

        var preview = string.IsNullOrWhiteSpace(result.Preview) ? result.Subject : result.Preview;

        return new EmailMessageViewModel
        {
            Id = result.ImapUid.ToString(),
            FolderId = result.FolderFullName,
            Subject = string.IsNullOrWhiteSpace(result.Subject) ? "(No subject)" : result.Subject,
            Sender = string.IsNullOrWhiteSpace(senderDisplay) ? "(Unknown sender)" : senderDisplay,
            SenderAddress = result.FromAddress ?? string.Empty,
            SenderInitials = SenderInitialsHelper.From(result.FromName, result.FromAddress),
            SenderColor = senderColor,
            Preview = preview ?? string.Empty,
            Received = result.ReceivedUtc.LocalDateTime,
            To = string.Empty,
            Cc = null,
            Bcc = null
        };
    }

    private static string FormatMailbox(MailboxAddress? mailbox)
    {
        if (mailbox == null)
        {
            return string.Empty;
        }

        var name = TextCleaner.CleanNullable(mailbox.Name) ?? string.Empty;
        var address = TextCleaner.CleanNullable(mailbox.Address) ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(address))
        {
            return $"{name} <{address}>";
        }

        return string.IsNullOrWhiteSpace(address) ? name : address;
    }

    private static IEnumerable<MailboxAddress> FlattenAddresses(InternetAddressList? addresses)
    {
        if (addresses == null)
        {
            yield break;
        }

        foreach (var address in addresses)
        {
            if (address is MailboxAddress mailbox)
            {
                yield return mailbox;
            }
            else if (address is GroupAddress group && group.Members != null)
            {
                foreach (var member in group.Members.OfType<MailboxAddress>())
                {
                    yield return member;
                }
            }
        }
    }

    private static string FormatAddressList(InternetAddressList? addresses)
    {
        var formatted = FlattenAddresses(addresses)
            .Select(FormatMailbox)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        return formatted.Count == 0 ? string.Empty : string.Join(", ", formatted);
    }

    private static MessageHeaderInfo BuildHeaderInfo(MimeMessage message)
    {
        var sender = message.From.OfType<MailboxAddress>().FirstOrDefault();

        return new MessageHeaderInfo(
            From: FormatMailbox(sender),
            FromAddress: TextCleaner.CleanNullable(sender?.Address) ?? string.Empty,
            To: FormatAddressList(message.To),
            Cc: FormatAddressList(message.Cc),
            Bcc: FormatAddressList(message.Bcc));
    }

    private async Task<QwenEmbedder?> EnsureSearchEmbedderAsync()
    {
        if (_searchEmbedder != null)
        {
            return _searchEmbedder;
        }

        try
        {
            _searchEmbedder = await QwenEmbedder.Build(QwenEmbedder.ModelId);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to initialize search embedder: {ex}");
            _searchEmbedder = null;
        }

        return _searchEmbedder;
    }

    private static (string Name, string Address) ExtractSenderTerms(string query)
    {
        var cleaned = TextCleaner.CleanNullable(query) ?? string.Empty;
        if (MailboxAddress.TryParse(cleaned, out var mailbox))
        {
            var name = TextCleaner.CleanNullable(mailbox?.Name) ?? cleaned;
            var address = TextCleaner.CleanNullable(mailbox?.Address) ?? cleaned;
            return (name, address);
        }

        return (cleaned, cleaned);
    }

    private static string BuildVectorLiteral(float[] vector) =>
        "[" + string.Join(",", vector.Select(v => v.ToString("G9", CultureInfo.InvariantCulture))) + "]";

    private async Task<bool> ConnectAsync(string statusMessage)
    {
        if (_settings == null || _password == null)
        {
            return false;
        }

        try
        {
            if (!_viewModel.IsSearchActive)
            {
                await ReportStatusAsync(statusMessage, true);
            }

            _client?.Dispose();
            _client = new ImapClient();
            await _client.ConnectAsync(_settings.Server, _settings.Port, _settings.UseSsl, _cts.Token);
            await _client.AuthenticateAsync(_settings.Username, _password, _cts.Token);

            _folderCache.Clear();
            _folderNextEndIndex.Clear();

            if (!_viewModel.IsSearchActive)
            {
                await ReportStatusAsync("Loading folders…", true);
            }

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

                var toList = FormatAddressList(summary.Envelope?.To);
                var ccList = FormatAddressList(summary.Envelope?.Cc);
                var bccList = FormatAddressList(summary.Envelope?.Bcc);

                return new EmailMessageViewModel
                {
                    Id = summary.UniqueId.Id.ToString(),
                    FolderId = folder.FullName,
                    Subject = summary.Envelope?.Subject ?? "(No subject)",
                    Sender = senderDisplay,
                    SenderAddress = senderAddress ?? string.Empty,
                    SenderInitials = SenderInitialsHelper.From(senderName, senderAddress),
                    SenderColor = messageColor,
                    Preview = summary.Envelope?.Subject ?? string.Empty,
                    Received = summary.InternalDate?.DateTime ?? DateTime.UtcNow,
                    To = toList,
                    Cc = string.IsNullOrWhiteSpace(ccList) ? null : ccList,
                    Bcc = string.IsNullOrWhiteSpace(bccList) ? null : bccList
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

                var embedder = await EnsureSearchEmbedderAsync();

                if (embedder == null)
                {
                    await ReportStatusAsync("Embedding model is unavailable.", false);
                    await Task.Delay(EmbeddingIdleDelay, token);
                    continue;
                }

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
    }

    private record EmbeddingInsert(int MessageId, int ChunkIndex, float[] Vector, string ModelVersion, DateTimeOffset CreatedAt);
    private record AttachmentInsert(string FileName, string ContentType, string? Disposition, long SizeBytes, string Hash, uint LargeObjectId);
    public record AttachmentContent(string FileName, string ContentType, string? Disposition, string Base64Data, long SizeBytes);
    public record MessageHeaderInfo(string From, string FromAddress, string To, string? Cc, string? Bcc);
    public record MessageBodyResult(string Html, MessageHeaderInfo Headers);
    private record SearchResult(
        int MessageId,
        long ImapUid,
        string Subject,
        string FromName,
        string FromAddress,
        DateTimeOffset ReceivedUtc,
        string Preview,
        string FolderFullName,
        double Score,
        int Priority);

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

    private async Task<List<AttachmentInsert>> DownloadAttachmentsAsync(NpgsqlConnection conn, MimeMessage message, CancellationToken token)
    {
        var parts = message.BodyParts
            .Where(IsAttachmentCandidate)
            .ToList();

        if (parts.Count == 0)
        {
            return [];
        }

        var manager = new NpgsqlLargeObjectManager(conn);
        var results = new List<AttachmentInsert>(parts.Count);

        foreach (var part in parts)
        {
            var saved = await SaveAttachmentAsync(manager, part, token);
            if (saved != null)
            {
                results.Add(saved);
            }
        }

        return results;
    }

    private async Task<AttachmentInsert?> SaveAttachmentAsync(NpgsqlLargeObjectManager manager, MimeEntity entity, CancellationToken token)
    {
        var fileName = GetAttachmentFileName(entity);
        var contentType = TextCleaner.CleanNullable(entity.ContentType?.MimeType) ?? "application/octet-stream";
        var disposition = entity.Headers?
            .FirstOrDefault(h => h != null && string.Equals(h.Field, "Content-Disposition", StringComparison.OrdinalIgnoreCase))
            ?.Value;

        Stream? contentStream = null;
        try
        {
            switch (entity)
            {
                case MimePart mimePart when mimePart.Content != null:
                    contentStream = mimePart.Content.Open();
                    break;
                case MessagePart messagePart when messagePart.Message != null:
                    var buffer = new MemoryStream();
                    await messagePart.Message.WriteToAsync(buffer, token);
                    buffer.Position = 0;
                    contentStream = buffer;
                    break;
                default:
                    return null;
            }

            if (contentStream == null)
            {
                return null;
            }

            var oid = await manager.CreateAsync(0u, token);
            await using var loStream = await manager.OpenReadWriteAsync(oid, token);

            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var rented = ArrayPool<byte>.Shared.Rent(81920);
            long totalBytes = 0;

            try
            {
                int read;
                while ((read = await contentStream.ReadAsync(rented.AsMemory(0, rented.Length), token)) > 0)
                {
                    await loStream.WriteAsync(rented.AsMemory(0, read), token);
                    sha.AppendData(rented, 0, read);
                    totalBytes += read;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }

            var hash = Convert.ToHexString(sha.GetHashAndReset());

            return new AttachmentInsert(
                FileName: fileName,
                ContentType: contentType,
                Disposition: disposition,
                SizeBytes: totalBytes,
                Hash: hash,
                LargeObjectId: oid);
        }
        finally
        {
            if (contentStream is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }
            else
            {
                contentStream?.Dispose();
            }
        }
    }

    private static string GetAttachmentFileName(MimeEntity entity)
    {
        var dispositionName = TextCleaner.CleanNullable(entity.ContentDisposition?.FileName);
        if (!string.IsNullOrWhiteSpace(dispositionName))
        {
            return dispositionName!;
        }

        var typeName = TextCleaner.CleanNullable(entity.ContentType?.Name);
        if (!string.IsNullOrWhiteSpace(typeName))
        {
            return typeName!;
        }

        return "attachment";
    }

    private static bool IsAttachmentCandidate(MimeEntity entity)
    {
        if (entity is MimePart part)
        {
            if (part.ContentType?.IsMimeType("text", "plain") == true ||
                part.ContentType?.IsMimeType("text", "html") == true)
            {
                return false;
            }

            if (part.ContentDisposition != null || part.IsAttachment)
            {
                return true;
            }
        }

        if (entity is MessagePart)
        {
            return true;
        }

        return false;
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
            var hasAttachments = await db.MessageAttachments.AnyAsync(a => a.MessageId == entity.Id, token);
            var needsWork = !hasBody || !hasAttachments;
            if (!needsWork)
            {
                return;
            }

            await db.Database.OpenConnectionAsync(token);
            await using var tx = await db.Database.BeginTransactionAsync(token);

            if (!hasBody)
            {
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
            }

            if (!hasAttachments)
            {
                var conn = (NpgsqlConnection)db.Database.GetDbConnection();
                var attachments = await DownloadAttachmentsAsync(conn, message, token);
                if (attachments.Count > 0)
                {
                    foreach (var attachment in attachments)
                    {
                        db.MessageAttachments.Add(new MessageAttachment
                        {
                            MessageId = entity.Id,
                            FileName = attachment.FileName,
                            ContentType = attachment.ContentType,
                            Disposition = attachment.Disposition,
                            SizeBytes = attachment.SizeBytes,
                            Hash = attachment.Hash,
                            LargeObjectId = attachment.LargeObjectId
                        });
                    }
                }
            }

            try
            {
                await db.SaveChangesAsync(token);
                await tx.CommitAsync(token);
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
        _searchEmbedder?.Dispose();
        _searchEmbedder = null;

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

    public Task<MessageBodyResult?> LoadMessageBodyAsync(string folderId, string messageId) =>
        LoadMessageBodyInternalAsync(folderId, messageId, true);

    public async Task<List<AttachmentContent>> LoadImageAttachmentsAsync(string folderId, string messageId)
    {
        var token = _cts.Token;
        if (_settings == null)
        {
            return [];
        }

        if (!long.TryParse(messageId, out var uid))
        {
            return [];
        }

        var db = await CreateDbContextAsync();
        if (db == null)
        {
            return [];
        }

        await using (db)
        {
            var folderEntity = await db.ImapFolders
                .AsNoTracking()
                .Where(f => f.AccountId == _settings.Id && f.FullName == folderId)
                .FirstOrDefaultAsync(token);

            if (folderEntity == null)
            {
                return [];
            }

            var messageEntity = await db.ImapMessages
                .AsNoTracking()
                .Where(m => m.FolderId == folderEntity.Id && m.ImapUid == uid)
                .FirstOrDefaultAsync(token);

            if (messageEntity == null)
            {
                return [];
            }

            var attachments = await db.MessageAttachments
                .AsNoTracking()
                .Where(a => a.MessageId == messageEntity.Id)
                .ToListAsync(token);

            attachments = attachments
                .Where(a => !string.IsNullOrWhiteSpace(a.ContentType) &&
                            a.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (attachments.Count == 0)
            {
                return [];
            }

            var conn = (NpgsqlConnection)db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
            {
                await conn.OpenAsync(token);
            }

            await using var tx = await conn.BeginTransactionAsync(token);
            var manager = new NpgsqlLargeObjectManager(conn);
            var results = new List<AttachmentContent>(attachments.Count);

            foreach (var attachment in attachments)
            {
                if (attachment.LargeObjectId == 0)
                {
                    continue;
                }

                try
                {
                    await using var stream = await manager.OpenReadAsync(attachment.LargeObjectId, token);
                    using var ms = new MemoryStream();
                    await stream.CopyToAsync(ms, token);
                    var base64 = Convert.ToBase64String(ms.ToArray());
                    results.Add(new AttachmentContent(
                        FileName: attachment.FileName,
                        ContentType: attachment.ContentType,
                        Disposition: attachment.Disposition,
                        Base64Data: base64,
                        SizeBytes: attachment.SizeBytes));
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to read attachment {attachment.FileName} ({attachment.LargeObjectId}): {ex}");
                }
            }

            await tx.CommitAsync(token);
            return results;
        }
    }

    private async Task<MessageBodyResult?> LoadMessageBodyInternalAsync(string folderId, string messageId, bool allowReconnect)
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
                var headers = BuildHeaderInfo(message);

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

                return new MessageBodyResult(htmlToRender, headers);
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
