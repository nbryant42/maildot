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
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.UI.Dispatching;
using MimeKit;
using Windows.UI;
using System.Diagnostics;
using Npgsql;
using System.Globalization;
using System.Security.Cryptography;
using Float16 = Microsoft.ML.OnnxRuntime.Float16;
using MimeKit.Utils;

namespace maildot.Services;

public sealed class ImapSyncService(MailboxViewModel viewModel, DispatcherQueue dispatcher) : IAsyncDisposable
{
    private const int PageSize = 40;

    private readonly MailboxViewModel _viewModel = viewModel;
    private readonly DispatcherQueue _dispatcher = dispatcher;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly Dictionary<string, IMailFolder> _folderCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _folderNextEndIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _folderNextUnreadOffset = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long?> _folderNextUnlabeledImapUid = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(int AccountId, int LabelId), LearnedLambdaEntry> _folderPriorLambdaCache = [];
    private readonly object _folderPriorLambdaCacheGate = new();
    private bool _offlineFallbackHydrated;

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
    private const double DefaultFolderPriorAlpha = 24.0d;
    private const double DefaultFolderPriorLambda = 0.10d;
    private static readonly TimeSpan FolderPriorLambdaCacheTtl = TimeSpan.FromMinutes(30);

    public async Task StartAsync(AccountSettings settings, string password)
    {
        _settings = settings;
        _password = password;

        if (!await _semaphore.WaitAsync(TimeSpan.FromMinutes(1), _cts.Token))
        {
            Debug.WriteLine("Semaphore timeout: " + Environment.StackTrace);
            return;
        }

        try
        {
            if (!await ConnectAsync("Connecting to IMAP…"))
            {
                return;
            }
        }
        finally
        {
            _semaphore.Release();
        }

        if (_viewModel.SelectedFolder is { } initialFolder)
        {
            await LoadFolderAsync(initialFolder.Id); //reacquires semaphore
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
        var reconnected = false;

        if (!await _semaphore.WaitAsync(TimeSpan.FromMinutes(1), _cts.Token))
        {
            Debug.WriteLine("Semaphore timeout: " + Environment.StackTrace);
            await ReportStatusAsync($"Unable to load messages: semaphore timeout", false);
            await EnqueueAsync(() => _viewModel.SetRetryVisible(true));
            return;
        }

        try
        {
            if (_client == null)
            {
                await LoadFolderFromDatabaseFallbackAsync(folderId, _cts.Token);
                return;
            }

            if (!_folderCache.TryGetValue(folderId, out folder))
            {
                if (allowReconnect && await TryReconnectAsync())
                {
                    reconnected = true;
                    return;
                }

                throw new InvalidOperationException("Folder could not be found on the server.");
            }

            folderDisplay = string.IsNullOrEmpty(folder.Name) ? folderId : folder.Name;
            await ReportStatusAsync($"Loading {folderDisplay}…", true);
            if (!folder.IsOpen)
            {
                await folder.OpenAsync(FolderAccess.ReadOnly, _cts.Token);
            }

            if (_viewModel.FolderUnreadOnly)
            {
                await LoadUnreadFolderPageAsync(folderId, folderDisplay, folder, append: false, combinedOffset: 0, _cts.Token);
                return;
            }

            if (_viewModel.UnlabeledOnly)
            {
                _folderNextUnlabeledImapUid[folderId] = null;
                await LoadUnlabeledFolderPageAsync(folderId, null, append: false, _cts.Token);
                return;
            }

            var messageCount = folder.Count;
            if (messageCount > 0)
            {
                var endIndex = messageCount - 1;
                var startIndex = Math.Max(0, endIndex - PageSize + 1);
                var emailItems = await FetchMessagesAsync(folder, startIndex, endIndex);
                var localOnly = await LoadLocalOnlyMessagesForFolderAsync(folderId, PageSize, _cts.Token);
                var mergedItems = emailItems
                    .Concat(localOnly)
                    .GroupBy(m => m.Id)
                    .Select(g => g.First())
                    .OrderByDescending(m => m.Received)
                    .Take(PageSize)
                    .ToList();

                _folderNextEndIndex[folderId] = startIndex - 1;

                await EnqueueAsync(() =>
                {
                    _viewModel.SetMessages(folderDisplay, mergedItems);
                    _viewModel.SetStatus("Mailbox is up to date.", false);
                    _viewModel.SetLoadMoreAvailability(startIndex > 0);
                    _viewModel.SetRetryVisible(false);
                });
                return;
            }

            _folderNextEndIndex[folderId] = -1;
            var localOnlyMessages = await LoadLocalOnlyMessagesForFolderAsync(folderId, PageSize, _cts.Token);
            await EnqueueAsync(() =>
            {
                _viewModel.SetMessages(folderDisplay, localOnlyMessages);
                _viewModel.SetStatus(localOnlyMessages.Count == 0 ? "Folder is empty." : "Mailbox is up to date.", false);
                _viewModel.SetLoadMoreAvailability(false);
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

            // do this before releasing the semaphore
            if (allowReconnect && IsRecoverable(ex) && await TryReconnectAsync())
            {
                reconnected = true;
            }
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

        // do this after releasing the semaphore
        if (reconnected)
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
        var reconnected = false;

        bool locked;

        try
        {
            locked = await _semaphore.WaitAsync(TimeSpan.FromMinutes(1), _cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!locked)
        {
            Debug.WriteLine("Semaphore timeout: " + Environment.StackTrace);
            await ReportStatusAsync($"Unable to load earlier messages: semaphore timeout", false);
            await EnqueueAsync(() => _viewModel.SetRetryVisible(true));
            return;
        }

        try
        {
            if (_client == null)
            {
                throw new ServiceNotConnectedException();
            }

            if (!_folderCache.TryGetValue(folderId, out folder))
            {
                if (allowReconnect && await TryReconnectAsync())
                {
                    reconnected = true;
                    return;
                }

                throw new InvalidOperationException("Folder could not be found on the server.");
            }

            if (_viewModel.UnlabeledOnly)
            {
                if (!_folderNextUnlabeledImapUid.TryGetValue(folderId, out var nextUid) || nextUid is < 0)
                {
                    await ReportStatusAsync("No more messages to load.", false);
                    await EnqueueAsync(() =>
                    {
                        _viewModel.SetLoadMoreAvailability(false);
                        _viewModel.SetRetryVisible(false);
                    });
                    return;
                }

                await LoadUnlabeledFolderPageAsync(folderId, nextUid, append: true, _cts.Token);
                return;
            }

            if (_viewModel.FolderUnreadOnly)
            {
                if (!_folderNextUnreadOffset.TryGetValue(folderId, out var nextUnreadOffset) || nextUnreadOffset < 0)
                {
                    await ReportStatusAsync("No more messages to load.", false);
                    await EnqueueAsync(() =>
                    {
                        _viewModel.SetLoadMoreAvailability(false);
                        _viewModel.SetRetryVisible(false);
                    });
                    return;
                }

                var unreadFolderDisplay = string.IsNullOrEmpty(folder.Name) ? folderId : folder.Name;
                await ReportStatusAsync($"Loading older messages for {unreadFolderDisplay}â€¦", true);
                await LoadUnreadFolderPageAsync(folderId, unreadFolderDisplay, folder, append: true, combinedOffset: nextUnreadOffset, _cts.Token);
                return;
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

            // do this before releasing the semaphore
            if (allowReconnect && IsRecoverable(ex) && await TryReconnectAsync())
            {
                reconnected = true;
            }
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

        // do this after releasing the semaphore
        if (reconnected)
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
        sql.Append("COALESCE(b.\"Preview\", ''), f.\"FullName\", m.\"IsRead\" ");
        sql.Append("FROM imap_messages m ");
        sql.Append("JOIN imap_folders f ON f.id = m.\"FolderId\" ");
        sql.Append("LEFT JOIN message_bodies b ON b.\"MessageId\" = m.\"Id\" ");
        sql.Append("WHERE f.\"AccountId\" = @accountId ");
        sql.Append("AND m.\"Subject\" ILIKE @subjectPattern ");
        if (sinceUtc.HasValue)
        {
            sql.Append("AND m.\"ReceivedUtc\" >= @sinceUtc ");
        }
        sql.Append("ORDER BY m.\"ImapUid\" DESC LIMIT 50");

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
            var isRead = reader.GetBoolean(8);

            results.Add(new SearchResult(
                MessageId: reader.GetInt32(0),
                ImapUid: reader.GetInt64(1),
                Subject: subject,
                FromName: fromName,
                FromAddress: fromAddress,
                ReceivedUtc: reader.GetFieldValue<DateTimeOffset>(5),
                Preview: preview,
                FolderFullName: folderFullName,
                IsRead: isRead,
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
        sql.Append("COALESCE(b.\"Preview\", ''), f.\"FullName\", m.\"IsRead\" ");
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

        sql.Append("ORDER BY m.\"ImapUid\" DESC LIMIT 50");

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
            var isRead = reader.GetBoolean(8);

            results.Add(new SearchResult(
                MessageId: reader.GetInt32(0),
                ImapUid: reader.GetInt64(1),
                Subject: subject,
                FromName: fromName,
                FromAddress: fromAddress,
                ReceivedUtc: reader.GetFieldValue<DateTimeOffset>(5),
                Preview: preview,
                FolderFullName: folderFullName,
                IsRead: isRead,
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
        vectorSql.Append("ranked.\"ReceivedUtc\", ranked.\"Preview\", ranked.\"FullName\", ranked.\"IsRead\", ranked.negative_inner_product ");
        vectorSql.Append("FROM ( ");
        vectorSql.Append("    SELECT DISTINCT ON (me.\"MessageId\") ");
        vectorSql.Append("           me.\"MessageId\", ");
        vectorSql.Append("           me.\"Vector\" <#> @queryVec::halfvec AS negative_inner_product, ");
        vectorSql.Append("           m.\"ImapUid\", m.\"Subject\", m.\"FromName\", m.\"FromAddress\", m.\"ReceivedUtc\", m.\"IsRead\", ");
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
            var isRead = reader.GetBoolean(8);
            var negativeInnerProduct = reader.IsDBNull(9) ? double.MaxValue : reader.GetDouble(9);

            results.Add(new SearchResult(
                MessageId: reader.GetInt32(0),
                ImapUid: reader.GetInt64(1),
                Subject: subject,
                FromName: fromName,
                FromAddress: fromAddress,
                ReceivedUtc: reader.GetFieldValue<DateTimeOffset>(5),
                Preview: preview,
                FolderFullName: folderFullName,
                IsRead: isRead,
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
            IsLocalOnly = result.ImapUid <= 0,
            FolderId = result.FolderFullName,
            Subject = string.IsNullOrWhiteSpace(result.Subject) ? "(No subject)" : result.Subject,
            Sender = string.IsNullOrWhiteSpace(senderDisplay) ? "(Unknown sender)" : senderDisplay,
            SenderAddress = result.FromAddress ?? string.Empty,
            SenderInitials = SenderInitialsHelper.From(result.FromName, result.FromAddress),
            SenderColor = senderColor,
            Preview = preview ?? string.Empty,
            Received = result.ReceivedUtc.LocalDateTime,
            IsRead = result.IsRead,
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
        var embedder = await QwenEmbedder.GetSharedAsync();
        if (embedder == null)
        {
            Debug.WriteLine("Failed to initialize shared search embedder.");
        }

        return embedder;
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
        Debug.Assert(_semaphore.CurrentCount == 0, "Semaphore should be held when calling ConnectAsync");

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

            // clear _folderCache first so that exceptions can't leave us in an inconsistent state.
            _folderCache.Clear();
            _folderNextEndIndex.Clear();
            _folderNextUnreadOffset.Clear();

            _client?.Dispose();
            _client = new ImapClient();
            await _client.ConnectAsync(_settings.Server, _settings.Port, _settings.UseSsl, _cts.Token);
            await _client.AuthenticateAsync(_settings.Username, _password, _cts.Token);

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

            var labels = await LoadLabelsAsync(_cts.Token);
            await EnqueueAsync(() => _viewModel.SetLabels(labels));

            _backgroundTask ??= Task.Run(() => BackgroundFetchLoopAsync(_cts.Token), _cts.Token);
            _embeddingTask ??= Task.Run(() => BackgroundEmbeddingLoopAsync(_cts.Token), _cts.Token);
            _offlineFallbackHydrated = false;

            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            // Enter explicit offline mode so folder loads use DB fallback instead of reconnect loops.
            _client?.Dispose();
            _client = null;

            if (ShouldHydrateOfflineFallback(connectFailed: true, offlineFallbackHydrated: _offlineFallbackHydrated))
            {
                var fallbackFolders = await LoadFoldersFromDatabaseAsync(_cts.Token);
                var labels = await LoadLabelsAsync(_cts.Token);
                await EnqueueAsync(() =>
                {
                    _viewModel.SetFolders(fallbackFolders);
                    _viewModel.SetLabels(labels);
                    _viewModel.SetRetryVisible(true);
                });
                _offlineFallbackHydrated = true;
            }

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
        var seenFolderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_client == null)
        {
            return folders;
        }

        _folderCache.Clear();

        async Task AddFolderAsync(IMailFolder folder)
        {
            if (!seenFolderNames.Add(folder.FullName))
            {
                return;
            }

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

    private async Task<IReadOnlyList<MailFolderViewModel>> LoadFoldersFromDatabaseAsync(CancellationToken token)
    {
        if (_settings == null)
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
            var folders = await (
                    from f in db.ImapFolders.AsNoTracking()
                    where f.AccountId == _settings.Id
                    select new
                    {
                        f.FullName,
                        f.DisplayName,
                        UnreadCount = db.ImapMessages.Count(m => m.FolderId == f.Id && !m.IsRead)
                    })
                .ToListAsync(token);

            return folders
                .OrderBy(f => GetFolderPriority(f.FullName))
                .ThenBy(f => f.FullName, StringComparer.OrdinalIgnoreCase)
                .Select(f => new MailFolderViewModel(
                    f.FullName,
                    string.IsNullOrWhiteSpace(f.DisplayName) ? f.FullName : f.DisplayName)
                {
                    UnreadCount = f.UnreadCount
                })
                .ToList();
        }
    }

    private async Task<List<LabelViewModel>> LoadLabelsAsync(CancellationToken token)
    {
        if (_settings == null)
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
            var entities = await db.Labels
                .AsNoTracking()
                .Where(l => l.AccountId == _settings.Id)
                .OrderBy(l => l.Name)
                .ToListAsync(token);
            var labelIds = entities.Select(e => e.Id).ToArray();

            var explicitUnread = await (
                    from ml in db.MessageLabels.AsNoTracking()
                    where labelIds.Contains(ml.LabelId)
                    join m in db.ImapMessages.AsNoTracking() on ml.MessageId equals m.Id
                    where !m.IsRead
                    select new { ml.LabelId, m.Id })
                .ToListAsync(token);

            var senderUnread = await (
                    from sl in db.SenderLabels.AsNoTracking()
                    where labelIds.Contains(sl.LabelId)
                    join m in db.ImapMessages.AsNoTracking() on sl.FromAddress equals m.FromAddress.ToLower()
                    join f in db.ImapFolders.AsNoTracking() on m.FolderId equals f.Id
                    where f.AccountId == _settings.Id && !m.IsRead
                    select new { sl.LabelId, m.Id })
                .ToListAsync(token);

            var unreadCounts = explicitUnread
                .Concat(senderUnread)
                .GroupBy(x => x.LabelId)
                .ToDictionary(g => g.Key, g => g.Select(x => x.Id).Distinct().Count());

            return BuildLabelTree(entities, unreadCounts);
        }
    }

    private async Task<List<EmailMessageViewModel>> FetchMessagesAsync(IMailFolder folder, int startIndex, int endIndex)
    {
        var summaries = await folder.FetchAsync(startIndex, endIndex,
            MessageSummaryItems.Envelope | MessageSummaryItems.UniqueId | MessageSummaryItems.InternalDate | MessageSummaryItems.Flags,
            _cts.Token);

        await PersistMessagesAsync(folder, summaries);

        var labelMap = await LoadLabelMapAsync(summaries.Select(s => (long)s.UniqueId.Id));

        var items = summaries
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

                var vm = new EmailMessageViewModel
                {
                    Id = summary.UniqueId.Id.ToString(),
                    FolderId = folder.FullName,
                    Subject = summary.Envelope?.Subject ?? "(No subject)",
                    Sender = senderDisplay,
                    SenderAddress = senderAddress ?? string.Empty,
                    SenderInitials = SenderInitialsHelper.From(senderName, senderAddress),
                    SenderColor = messageColor,
                    Preview = summary.Envelope?.Subject ?? string.Empty,
                    Received = summary.InternalDate?.LocalDateTime ?? DateTimeOffset.UtcNow.LocalDateTime,
                    IsRead = summary.Flags?.HasFlag(MessageFlags.Seen) == true,
                    To = toList,
                    Cc = string.IsNullOrWhiteSpace(ccList) ? null : ccList,
                    Bcc = string.IsNullOrWhiteSpace(bccList) ? null : bccList
                };

                if (labelMap.TryGetValue(summary.UniqueId.Id, out var names))
                {
                    vm.LabelNames = names;
                }

                return vm;
            })
            .ToList();

        return ApplyFolderViewFilter(items);
    }

    private async Task LoadUnlabeledFolderPageAsync(string folderFullName, long? imapUidLessThan, bool append, CancellationToken token)
    {
        if (_settings == null)
        {
            return;
        }

        var db = await CreateDbContextAsync();
        if (db == null)
        {
            await ReportStatusAsync("Unable to load messages (database unavailable).", false);
            await EnqueueAsync(() => _viewModel.SetRetryVisible(true));
            return;
        }

        await using (db)
        {
            var folder = await db.ImapFolders.AsNoTracking()
                .FirstOrDefaultAsync(f => f.AccountId == _settings.Id && f.FullName == folderFullName, token);

            if (folder == null)
            {
                await ReportStatusAsync("Folder could not be found in the database.", false);
                await EnqueueAsync(() => _viewModel.SetRetryVisible(true));
                return;
            }

            var rows = await (
                    from m in db.ImapMessages.AsNoTracking()
                    join b in db.MessageBodies.AsNoTracking() on m.Id equals b.MessageId into bodies
                    from b in bodies.DefaultIfEmpty()
                    where m.FolderId == folder.Id
                          && !db.MessageLabels.Any(ml => ml.MessageId == m.Id)
                          && !db.SenderLabels.Any(sl => sl.FromAddress.ToLower() == m.FromAddress.ToLower())
                          && (imapUidLessThan == null || m.ImapUid < imapUidLessThan.Value)
                    orderby m.ImapUid descending
                    select new
                    {
                        m.ImapUid,
                        m.MessageId,
                        m.IsRead,
                        m.Subject,
                        m.FromName,
                        m.FromAddress,
                        m.ReceivedUtc,
                        Preview = b != null ? b.Preview : null,
                        Folder = folder.FullName
                    })
                .Take(PageSize)
                .ToListAsync(token);

            var dedupedRows = rows
                .GroupBy(r => BuildMessageDedupKey(r.MessageId, r.ImapUid))
                .Select(g => g
                    .OrderByDescending(x => IsPreferredDedupUid(x.ImapUid))
                    .ThenByDescending(x => x.ImapUid)
                    .First())
                .ToList();

            var emailItems = dedupedRows.Select(r =>
            {
                var senderDisplay = !string.IsNullOrWhiteSpace(r.FromName)
                    ? r.FromName!
                    : string.IsNullOrWhiteSpace(r.FromAddress) ? "(Unknown sender)" : r.FromAddress!;

                var color = SenderColorHelper.GetColor(r.FromName, r.FromAddress);
                return new EmailMessageViewModel
                {
                    Id = r.ImapUid.ToString(),
                    IsLocalOnly = r.ImapUid <= 0,
                    FolderId = r.Folder,
                    Subject = string.IsNullOrWhiteSpace(r.Subject) ? "(No subject)" : r.Subject!,
                    Sender = senderDisplay,
                    SenderAddress = r.FromAddress ?? string.Empty,
                    SenderInitials = SenderInitialsHelper.From(r.FromName, r.FromAddress),
                    SenderColor = new Color { A = 255, R = color.R, G = color.G, B = color.B },
                    Preview = string.IsNullOrWhiteSpace(r.Preview) ? r.Subject ?? string.Empty : r.Preview!,
                    Received = r.ReceivedUtc.LocalDateTime,
                    IsRead = r.IsRead,
                    LabelNames = []
                };
            }).ToList();

            var lowestUid = emailItems.Count == 0 ? (long?)null : emailItems.Min(e => long.Parse(e.Id));
            _folderNextUnlabeledImapUid[folderFullName] = lowestUid.HasValue ? lowestUid.Value - 1 : -1;

            await EnqueueAsync(() =>
            {
                if (append)
                {
                    _viewModel.AppendMessages(emailItems);
                }
                else
                {
                    _viewModel.SetMessages(folderFullName, emailItems);
                }

                _viewModel.SetStatus(emailItems.Count == 0 ? "No unlabeled messages." : "Mailbox is up to date.", false);
                _viewModel.SetLoadMoreAvailability(emailItems.Count == PageSize && _folderNextUnlabeledImapUid[folderFullName] > 0);
                _viewModel.SetRetryVisible(false);
            });
        }
    }

    private async Task<List<EmailMessageViewModel>> LoadLocalOnlyMessagesForFolderAsync(string folderFullName, int limit, CancellationToken token)
    {
        if (_settings == null || string.IsNullOrWhiteSpace(folderFullName) || limit <= 0)
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
            var folder = await db.ImapFolders.AsNoTracking()
                .FirstOrDefaultAsync(f => f.AccountId == _settings.Id && f.FullName == folderFullName, token);

            if (folder == null)
            {
                return [];
            }

            var rows = await (
                    from m in db.ImapMessages.AsNoTracking()
                    join b in db.MessageBodies.AsNoTracking() on m.Id equals b.MessageId into bodies
                    from b in bodies.DefaultIfEmpty()
                    where m.FolderId == folder.Id && m.ImapUid <= 0
                    orderby m.ReceivedUtc descending
                    select new
                    {
                        m.ImapUid,
                        m.IsRead,
                        m.Subject,
                        m.FromName,
                        m.FromAddress,
                        m.ReceivedUtc,
                        Preview = b != null ? b.Preview : null
                    })
                .Take(limit)
                .ToListAsync(token);

            return ApplyFolderViewFilter(rows.Select(r =>
            {
                var senderDisplay = !string.IsNullOrWhiteSpace(r.FromName)
                    ? r.FromName!
                    : string.IsNullOrWhiteSpace(r.FromAddress) ? "(Unknown sender)" : r.FromAddress!;

                var color = SenderColorHelper.GetColor(r.FromName, r.FromAddress);
                return new EmailMessageViewModel
                {
                    Id = r.ImapUid.ToString(),
                    IsLocalOnly = true,
                    FolderId = folder.FullName,
                    Subject = string.IsNullOrWhiteSpace(r.Subject) ? "(No subject)" : r.Subject!,
                    Sender = senderDisplay,
                    SenderAddress = r.FromAddress ?? string.Empty,
                    SenderInitials = SenderInitialsHelper.From(r.FromName, r.FromAddress),
                    SenderColor = new Color { A = 255, R = color.R, G = color.G, B = color.B },
                    Preview = string.IsNullOrWhiteSpace(r.Preview) ? r.Subject ?? string.Empty : r.Preview!,
                    Received = r.ReceivedUtc.LocalDateTime,
                    IsRead = r.IsRead
                };
            }).ToList());
        }
    }

    private async Task LoadUnreadFolderPageAsync(
        string folderFullName,
        string folderDisplay,
        IMailFolder folder,
        bool append,
        int combinedOffset,
        CancellationToken token)
    {
        var serverUnreadUids = await folder.SearchAsync(SearchQuery.NotSeen, token);
        var serverUnreadCount = serverUnreadUids.Count;
        var queryLimit = combinedOffset + (PageSize * 3);

        var pagedUnreadUids = serverUnreadUids
            .Reverse()
            .Skip(combinedOffset)
            .Take(PageSize * 2)
            .ToList();

        var serverUnreadMessages = pagedUnreadUids.Count == 0
            ? []
            : await FetchMessagesByUidAsync(folder, pagedUnreadUids, token);

        var localUnreadMessages = await LoadUnreadMessagesForFolderAsync(folderFullName, queryLimit, token);
        var localUnreadCount = await CountUnreadMessagesForFolderAsync(folderFullName, token);

        var merged = serverUnreadMessages
            .Concat(localUnreadMessages)
            .GroupBy(m => m.Id, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderByDescending(m => m.Received)
            .ToList();

        var page = merged
            .Skip(combinedOffset)
            .Take(PageSize)
            .ToList();

        var hasMore = combinedOffset + page.Count < Math.Max(merged.Count, serverUnreadCount + localUnreadCount);
        _folderNextUnreadOffset[folderFullName] = hasMore ? combinedOffset + page.Count : -1;

        await EnqueueAsync(() =>
        {
            if (append)
            {
                _viewModel.AppendMessages(page);
            }
            else
            {
                _viewModel.SetMessages(folderDisplay, page);
            }

            _viewModel.SetStatus(page.Count == 0 && !append ? "No unread messages." : "Mailbox is up to date.", false);
            _viewModel.SetLoadMoreAvailability(hasMore);
            _viewModel.SetRetryVisible(false);
        });
    }

    private async Task<List<EmailMessageViewModel>> FetchMessagesByUidAsync(IMailFolder folder, IList<UniqueId> uids, CancellationToken token)
    {
        var summaries = await folder.FetchAsync(
            uids,
            MessageSummaryItems.Envelope | MessageSummaryItems.UniqueId | MessageSummaryItems.InternalDate | MessageSummaryItems.Flags,
            token);

        await PersistMessagesAsync(folder, summaries);

        var labelMap = await LoadLabelMapAsync(summaries.Select(s => (long)s.UniqueId.Id));

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

                var vm = new EmailMessageViewModel
                {
                    Id = summary.UniqueId.Id.ToString(),
                    FolderId = folder.FullName,
                    Subject = summary.Envelope?.Subject ?? "(No subject)",
                    Sender = senderDisplay,
                    SenderAddress = senderAddress ?? string.Empty,
                    SenderInitials = SenderInitialsHelper.From(senderName, senderAddress),
                    SenderColor = messageColor,
                    Preview = summary.Envelope?.Subject ?? string.Empty,
                    Received = summary.InternalDate?.LocalDateTime ?? DateTimeOffset.UtcNow.LocalDateTime,
                    IsRead = summary.Flags?.HasFlag(MessageFlags.Seen) == true,
                    To = toList,
                    Cc = string.IsNullOrWhiteSpace(ccList) ? null : ccList,
                    Bcc = string.IsNullOrWhiteSpace(bccList) ? null : bccList
                };

                if (labelMap.TryGetValue(summary.UniqueId.Id, out var names))
                {
                    vm.LabelNames = names;
                }

                return vm;
            })
            .ToList();
    }

    private async Task<List<EmailMessageViewModel>> LoadUnreadMessagesForFolderAsync(string folderFullName, int limit, CancellationToken token)
    {
        if (_settings == null || string.IsNullOrWhiteSpace(folderFullName) || limit <= 0)
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
            var folder = await db.ImapFolders.AsNoTracking()
                .FirstOrDefaultAsync(f => f.AccountId == _settings.Id && f.FullName == folderFullName, token);

            if (folder == null)
            {
                return [];
            }

            var rows = await (
                    from m in db.ImapMessages.AsNoTracking()
                    join b in db.MessageBodies.AsNoTracking() on m.Id equals b.MessageId into bodies
                    from b in bodies.DefaultIfEmpty()
                    where m.FolderId == folder.Id && !m.IsRead
                    orderby m.ReceivedUtc descending, m.ImapUid descending
                    select new
                    {
                        m.Id,
                        m.ImapUid,
                        m.IsRead,
                        m.Subject,
                        m.FromName,
                        m.FromAddress,
                        m.ReceivedUtc,
                        Preview = b != null ? b.Preview : null
                    })
                .Take(limit)
                .ToListAsync(token);

            var messageIds = rows.Select(r => r.Id).ToList();
            var explicitLabels = await (
                    from ml in db.MessageLabels.AsNoTracking()
                    join l in db.Labels.AsNoTracking() on ml.LabelId equals l.Id
                    where messageIds.Contains(ml.MessageId)
                    select new { ml.MessageId, l.Name })
                .ToListAsync(token);

            var senderLabels = await (
                    from m in db.ImapMessages.AsNoTracking()
                    where messageIds.Contains(m.Id)
                    join sl in db.SenderLabels.AsNoTracking() on m.FromAddress.ToLower() equals sl.FromAddress
                    join l in db.Labels.AsNoTracking() on sl.LabelId equals l.Id
                    select new { m.Id, l.Name })
                .ToListAsync(token);

            var labelMap = explicitLabels
                .Select(x => new { MessageId = x.MessageId, x.Name })
                .Concat(senderLabels.Select(x => new { MessageId = x.Id, x.Name }))
                .GroupBy(x => x.MessageId)
                .ToDictionary(g => g.Key, g => g.Select(x => x.Name).Distinct().ToList());

            return rows.Select(r =>
            {
                var senderDisplay = !string.IsNullOrWhiteSpace(r.FromName)
                    ? r.FromName!
                    : string.IsNullOrWhiteSpace(r.FromAddress) ? "(Unknown sender)" : r.FromAddress!;

                var color = SenderColorHelper.GetColor(r.FromName, r.FromAddress);
                var vm = new EmailMessageViewModel
                {
                    Id = r.ImapUid.ToString(),
                    IsLocalOnly = r.ImapUid <= 0,
                    FolderId = folder.FullName,
                    Subject = string.IsNullOrWhiteSpace(r.Subject) ? "(No subject)" : r.Subject!,
                    Sender = senderDisplay,
                    SenderAddress = r.FromAddress ?? string.Empty,
                    SenderInitials = SenderInitialsHelper.From(r.FromName, r.FromAddress),
                    SenderColor = new Color { A = 255, R = color.R, G = color.G, B = color.B },
                    Preview = string.IsNullOrWhiteSpace(r.Preview) ? r.Subject ?? string.Empty : r.Preview!,
                    Received = r.ReceivedUtc.LocalDateTime,
                    IsRead = r.IsRead
                };

                if (labelMap.TryGetValue(r.Id, out var names))
                {
                    vm.LabelNames = names;
                }

                return vm;
            }).ToList();
        }
    }

    private async Task<int> CountUnreadMessagesForFolderAsync(string folderFullName, CancellationToken token)
    {
        if (_settings == null || string.IsNullOrWhiteSpace(folderFullName))
        {
            return 0;
        }

        var db = await CreateDbContextAsync();
        if (db == null)
        {
            return 0;
        }

        await using (db)
        {
            return await (
                    from f in db.ImapFolders.AsNoTracking()
                    where f.AccountId == _settings.Id && f.FullName == folderFullName
                    select db.ImapMessages.Count(m => m.FolderId == f.Id && !m.IsRead))
                .FirstOrDefaultAsync(token);
        }
    }

    private async Task LoadFolderFromDatabaseFallbackAsync(string folderFullName, CancellationToken token)
    {
        if (_settings == null || string.IsNullOrWhiteSpace(folderFullName))
        {
            return;
        }

        if (_viewModel.UnlabeledOnly)
        {
            _folderNextUnlabeledImapUid[folderFullName] = null;
            await LoadUnlabeledFolderPageAsync(folderFullName, null, append: false, token);
            await EnqueueAsync(() => _viewModel.SetRetryVisible(true));
            return;
        }

        if (_viewModel.FolderUnreadOnly)
        {
            var unreadMessages = await LoadUnreadMessagesForFolderAsync(folderFullName, PageSize, token);
            _folderNextUnreadOffset[folderFullName] = unreadMessages.Count == PageSize ? PageSize : -1;
            await EnqueueAsync(() =>
            {
                _viewModel.SetMessages(folderFullName, unreadMessages);
                _viewModel.SetStatus(unreadMessages.Count == 0 ? "No unread messages." : "Mailbox is up to date.", false);
                _viewModel.SetLoadMoreAvailability(unreadMessages.Count == PageSize);
                _viewModel.SetRetryVisible(true);
            });
            return;
        }

        var db = await CreateDbContextAsync();
        if (db == null)
        {
            await EnqueueAsync(() => _viewModel.SetRetryVisible(true));
            return;
        }

        await using (db)
        {
            var folder = await db.ImapFolders
                .AsNoTracking()
                .FirstOrDefaultAsync(f => f.AccountId == _settings.Id && f.FullName == folderFullName, token);

            if (folder == null)
            {
                await EnqueueAsync(() => _viewModel.SetRetryVisible(true));
                return;
            }

            var rows = await (
                    from m in db.ImapMessages.AsNoTracking()
                    join b in db.MessageBodies.AsNoTracking() on m.Id equals b.MessageId into bodies
                    from b in bodies.DefaultIfEmpty()
                    where m.FolderId == folder.Id
                    orderby m.ImapUid descending
                    select new
                    {
                        m.Id,
                        m.ImapUid,
                        m.IsRead,
                        m.Subject,
                        m.FromName,
                        m.FromAddress,
                        m.ReceivedUtc,
                        Preview = b != null ? b.Preview : null
                    })
                .Take(PageSize)
                .ToListAsync(token);

            var messageIds = rows.Select(r => r.Id).ToList();
            var explicitLabels = await (
                    from ml in db.MessageLabels.AsNoTracking()
                    join l in db.Labels.AsNoTracking() on ml.LabelId equals l.Id
                    where messageIds.Contains(ml.MessageId)
                    select new { ml.MessageId, l.Name })
                .ToListAsync(token);

            var senderLabels = await (
                    from m in db.ImapMessages.AsNoTracking()
                    where messageIds.Contains(m.Id)
                    join sl in db.SenderLabels.AsNoTracking() on m.FromAddress.ToLower() equals sl.FromAddress
                    join l in db.Labels.AsNoTracking() on sl.LabelId equals l.Id
                    select new { m.Id, l.Name })
                .ToListAsync(token);

            var labelMap = explicitLabels
                .Select(x => new { MessageId = x.MessageId, x.Name })
                .Concat(senderLabels.Select(x => new { MessageId = x.Id, x.Name }))
                .GroupBy(x => x.MessageId)
                .ToDictionary(g => g.Key, g => g.Select(x => x.Name).Distinct().ToList());

            var messages = rows.Select(r =>
            {
                var senderDisplay = !string.IsNullOrWhiteSpace(r.FromName)
                    ? r.FromName!
                    : string.IsNullOrWhiteSpace(r.FromAddress) ? "(Unknown sender)" : r.FromAddress!;
                var color = SenderColorHelper.GetColor(r.FromName, r.FromAddress);
                var vm = new EmailMessageViewModel
                {
                    Id = r.ImapUid.ToString(),
                    IsLocalOnly = r.ImapUid <= 0,
                    FolderId = folder.FullName,
                    Subject = string.IsNullOrWhiteSpace(r.Subject) ? "(No subject)" : r.Subject!,
                    Sender = senderDisplay,
                    SenderAddress = r.FromAddress ?? string.Empty,
                    SenderInitials = SenderInitialsHelper.From(r.FromName, r.FromAddress),
                    SenderColor = new Color { A = 255, R = color.R, G = color.G, B = color.B },
                    Preview = string.IsNullOrWhiteSpace(r.Preview) ? r.Subject ?? string.Empty : r.Preview!,
                    Received = r.ReceivedUtc.LocalDateTime,
                    IsRead = r.IsRead
                };

                if (labelMap.TryGetValue(r.Id, out var names))
                {
                    vm.LabelNames = names;
                }

                return vm;
            }).ToList();

            _folderNextEndIndex[folderFullName] = -1;
            await EnqueueAsync(() =>
            {
                _viewModel.SetMessages(string.IsNullOrWhiteSpace(folder.DisplayName) ? folder.FullName : folder.DisplayName, ApplyFolderViewFilter(messages));
                _viewModel.SetLoadMoreAvailability(false);
                _viewModel.SetRetryVisible(true);
            });
        }
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

            var existingByUid = await db.ImapMessages
                .Where(m => m.FolderId == folderEntity.Id)
                .ToDictionaryAsync(m => m.ImapUid, _cts.Token);

            foreach (var summary in summaries)
            {
                var uid = (long)summary.UniqueId.Id;
                var isRead = summary.Flags?.HasFlag(MessageFlags.Seen) == true;

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

                if (existingByUid.TryGetValue(uid, out var existing))
                {
                    existing.Subject = TextCleaner.CleanNullable(summary.Envelope?.Subject) ?? string.Empty;
                    existing.FromName = senderName;
                    existing.FromAddress = senderAddress;
                    existing.ReceivedUtc = received;
                    existing.IsRead = isRead;
                    existing.Hash = $"{messageId}:{uid}";
                    continue;
                }

                db.ImapMessages.Add(new ImapMessage
                {
                    FolderId = folderEntity.Id,
                    ImapUid = uid,
                    MessageId = messageId,
                    Subject = TextCleaner.CleanNullable(summary.Envelope?.Subject) ?? string.Empty,
                    FromName = senderName,
                    FromAddress = senderAddress,
                    ReceivedUtc = received,
                    Hash = $"{messageId}:{uid}",
                    IsRead = isRead
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
        var delay = TimeSpan.Zero;

        while (!token.IsCancellationRequested)
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, token);
            }

            try
            {
                if (!await _semaphore.WaitAsync(TimeSpan.FromMinutes(1), token))
                {
                    Debug.WriteLine("Semaphore timeout in background fetch loop: " + Environment.StackTrace);
                    delay = TimeSpan.FromMinutes(1);
                    continue;
                }

                try
                {
                    if (_client == null || !_client.IsConnected)
                    {
                        if (!await TryReconnectAsync())
                        {
                            delay = TimeSpan.FromMinutes(1);
                            continue;
                        }
                    }
                }
                finally
                {
                    _semaphore.Release();
                }

                var folders = GetFoldersByPriority();
                foreach (var folder in folders)
                {
                    if (token.IsCancellationRequested)
                    {
                        break;
                    }

                    await FetchMissingBodiesForFolderAsync(folder, token); //reacquires semaphore repeatedly
                }

                delay = TimeSpan.FromMinutes(1);
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

                delay = TimeSpan.FromMinutes(1);
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
        bool IsRead,
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

    private async Task<List<AttachmentInsert>> DownloadAttachmentsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, MimeMessage message, CancellationToken token)
    {
        var parts = message.BodyParts
            .Where(IsAttachmentCandidate)
            .ToList();

        if (parts.Count == 0)
        {
            return [];
        }

        var manager = new PostgresLargeObjectStore(conn, tx);
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

    private async Task<AttachmentInsert?> SaveAttachmentAsync(PostgresLargeObjectStore manager, MimeEntity entity, CancellationToken token)
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

            var oid = await manager.CreateAsync(token);
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

        if (!await _semaphore.WaitAsync(TimeSpan.FromMinutes(1), token))
        {
            Debug.WriteLine("Semaphore timeout: " + Environment.StackTrace);
            return;
        }

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

            serverUids = [.. (await folder.SearchAsync(SearchQuery.All, token))];
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

        if (!await _semaphore.WaitAsync(TimeSpan.FromMinutes(1), token))
        {
            Debug.WriteLine("Semaphore timeout: " + Environment.StackTrace);
            return;
        }

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
                var sanitized = HtmlSanitizer.SanitizeNullable(html);
                var previewSource = string.IsNullOrWhiteSpace(plain) ? message.Subject ?? string.Empty : plain;
                var preview = BuildPreview(previewSource);

                MessageBody mb = new()
                {
                    MessageId = entity.Id,
                    PlainText = plain,
                    HtmlText = html,
                    SanitizedHtml = sanitized,
                    SanitizedHtmlVersion = HtmlSanitizer.CurrentPolicyVersion,
                    Headers = headers,
                    Preview = preview
                };
                db.MessageBodies.Add(mb);
            }

            if (!hasAttachments)
            {
                var conn = (NpgsqlConnection)db.Database.GetDbConnection();
                var attachments = await DownloadAttachmentsAsync(conn, (NpgsqlTransaction)tx.GetDbTransaction(), message, token);
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
        var receivedUtc = ResolveReceivedUtc(message, internalDateFallback: null, existingReceived: null);

        return new ImapMessage
        {
            FolderId = folderId,
            ImapUid = uid,
            MessageId = messageId,
            Subject = TextCleaner.CleanNullable(message.Subject) ?? string.Empty,
            FromName = senderName,
            FromAddress = senderAddress,
            ReceivedUtc = receivedUtc,
            Hash = $"{messageId}:{uid}",
            IsRead = true
        };
    }

    private static void UpdateImapMessage(ImapMessage entity, MimeMessage message)
    {
        var sender = message.From.OfType<MailboxAddress>().FirstOrDefault();
        entity.Subject = TextCleaner.CleanNullable(message.Subject) ?? string.Empty;
        entity.FromName = TextCleaner.CleanNullable(sender?.Name) ?? string.Empty;
        entity.FromAddress = TextCleaner.CleanNullable(sender?.Address) ?? string.Empty;
        entity.ReceivedUtc = ResolveReceivedUtc(message, internalDateFallback: null, existingReceived: entity.ReceivedUtc);
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    private static DateTimeOffset ResolveReceivedUtc(
        MimeMessage message,
        DateTimeOffset? internalDateFallback,
        DateTimeOffset? existingReceived)
    {
        if (TryParseReceivedHeader(message, out var parsed))
        {
            return parsed;
        }

        if (internalDateFallback.HasValue)
        {
            return internalDateFallback.Value;
        }

        if (existingReceived.HasValue)
        {
            return existingReceived.Value;
        }

        return DateTimeOffset.UtcNow;
    }

    private static bool TryParseReceivedHeader(MimeMessage message, out DateTimeOffset receivedUtc)
    {
        receivedUtc = default;
        var receivedHeader = message.Headers?
            .FirstOrDefault(h => h != null && string.Equals(h.Field, "Received", StringComparison.OrdinalIgnoreCase))
            ?.Value;

        if (string.IsNullOrWhiteSpace(receivedHeader))
        {
            return false;
        }

        var semicolonIndex = receivedHeader.LastIndexOf(';');
        var datePortion = semicolonIndex >= 0
            ? receivedHeader[(semicolonIndex + 1)..].Trim()
            : receivedHeader.Trim();

        if (DateUtils.TryParse(datePortion, out var parsed))
        {
            receivedUtc = parsed.ToUniversalTime();
            return true;
        }

        if (DateTimeOffset.TryParse(datePortion, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var dto))
        {
            receivedUtc = dto.ToUniversalTime();
            return true;
        }

        return false;
    }

    private string BuildConnectionString(MailDbContext db)
    {
        if (_pgSettings != null && !string.IsNullOrWhiteSpace(_pgPassword))
        {
            var sslMode = _pgSettings.UseSsl ? "Require" : "Disable";
            var cs = $"Host={_pgSettings.Host};Port={_pgSettings.Port};Database={_pgSettings.Database};Username={_pgSettings.Username};Password={_pgPassword};SSL Mode={sslMode};Maximum Pool Size=10";
            return cs;
        }

        var csFromDb = db.Database.GetDbConnection().ConnectionString;
        return string.IsNullOrWhiteSpace(csFromDb) ? string.Empty : csFromDb;
    }

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

    private async Task<Dictionary<long, List<string>>> LoadLabelMapAsync(IEnumerable<long> uids)
    {
        var result = new Dictionary<long, List<string>>();
        if (_settings == null)
        {
            return result;
        }

        var uidArray = uids.Distinct().ToArray();
        if (uidArray.Length == 0)
        {
            return result;
        }

        var db = await CreateDbContextAsync();
        if (db == null)
        {
            return result;
        }

        await using (db)
        {
            var rows = await (
                from m in db.ImapMessages.AsNoTracking()
                join f in db.ImapFolders.AsNoTracking() on m.FolderId equals f.Id
                where f.AccountId == _settings.Id && uidArray.Contains(m.ImapUid)
                select new
                {
                    m.ImapUid,
                    MessageLabels = (
                        from ml in db.MessageLabels.AsNoTracking()
                        join l in db.Labels.AsNoTracking() on ml.LabelId equals l.Id
                        where ml.MessageId == m.Id
                        select l.Name
                    ).ToList(),
                    SenderLabels = (
                        from sl in db.SenderLabels.AsNoTracking()
                        join l in db.Labels.AsNoTracking() on sl.LabelId equals l.Id
                        where sl.FromAddress == m.FromAddress.ToLower()
                        select l.Name
                    ).ToList()
                }
            ).ToListAsync(_cts.Token);

            foreach (var row in rows)
            {
                var names = row.MessageLabels.Concat(row.SenderLabels).Distinct().ToList();
                if (names.Count > 0)
                {
                    result[row.ImapUid] = names;
                }
            }
        }

        return result;
    }

    private static List<LabelViewModel> BuildLabelTree(IEnumerable<Label> labels, IReadOnlyDictionary<int, int>? unreadCounts = null)
    {
        var map = labels.ToDictionary(l => l.Id, l =>
        {
            var vm = new LabelViewModel(l.Id, l.Name, l.ParentLabelId);
            vm.UnreadCount = unreadCounts != null && unreadCounts.TryGetValue(l.Id, out var count) ? count : 0;
            return vm;
        });
        var roots = new List<LabelViewModel>();

        foreach (var label in labels)
        {
            if (label.ParentLabelId != null && map.TryGetValue(label.ParentLabelId.Value, out var parent))
            {
                parent.Children.Add(map[label.Id]);
            }
            else
            {
                roots.Add(map[label.Id]);
            }
        }

        return roots;
    }

    private sealed record SuggestedResult(ImapMessage Message, Models.ImapFolder Folder, MessageBody? Body,
        double Score);
    private sealed record LearnedLambdaEntry(double Value, DateTimeOffset LearnedAtUtc);
    private readonly record struct FolderPriorTrainingExample(double SemanticScore, double PriorLift, bool IsPositive);
    private readonly record struct SuggestedCandidate(int MessageId, int FolderId, double Score, double Rank, double? DominanceGap);

    private async Task<List<SuggestedResult>> GetSuggestedMessagesAsync(MailDbContext db, int labelId,
        DateTimeOffset? sinceUtc, CancellationToken token)
    {
        const int finalLimit = 20;
        const int candidateLimit = 120;
        const double postDominanceGrace = 0.03d;
        const double dominatedNearTopRankDelta = 0.12d;
        var folderPriorAlpha = DefaultFolderPriorAlpha;

        var connString = BuildConnectionString(db);
        await using var conn = new NpgsqlConnection(connString);
        await conn.OpenAsync(token);

        var centroids = await GetCentroidsAsync(conn, _settings!.Id, token);
        if (!centroids.TryGetValue(labelId, out var centroidArr) || centroidArr.Length == 0)
        {
            return [];
        }

        var folderPriorLambda = await GetLearnedFolderPriorLambdaAsync(conn, _settings.Id, labelId, centroidArr, folderPriorAlpha, token);

        var centroidLiteral = BuildVectorLiteral(centroidArr);
        var scoreExpr = $@"(ml.""Vector"" <#> '{centroidLiteral}')";
        var sinceClause = sinceUtc != null ? @"AND m.""ReceivedUtc"" >= @p0" : string.Empty;

        var otherCentroids = new List<float[]>();
        foreach (var kvp in centroids)
        {
            if (kvp.Key == labelId)
            {
                continue;
            }

            if (kvp.Value.Length > 0)
            {
                otherCentroids.Add(kvp.Value);
            }
        }

        var otherScoreExprs = new List<string>();
        foreach (var other in otherCentroids)
        {
            var otherLiteral = BuildVectorLiteral(other);
            otherScoreExprs.Add($@"(ml.""Vector"" <#> '{otherLiteral}')");
        }

        var bestOtherScoreExpr = otherScoreExprs.Count switch
        {
            0 => "NULL::double precision",
            1 => otherScoreExprs[0],
            _ => $"least({string.Join(", ", otherScoreExprs)})"
        };

        var sql = $@"
WITH label_ids AS (
    SELECT ml0.""LabelId"", ml0.""MessageId""
    FROM ""message_labels"" ml0
    JOIN ""imap_messages"" m0 ON m0.""Id"" = ml0.""MessageId""
    JOIN ""imap_folders"" f0 ON f0.""id"" = m0.""FolderId""
    WHERE f0.""AccountId"" = @accountId
    UNION
    SELECT sl0.""LabelId"", m0.""Id""
    FROM ""sender_labels"" sl0
    JOIN ""labels"" l0 ON l0.""Id"" = sl0.""LabelId""
    JOIN ""imap_messages"" m0 ON sl0.""from_address"" = lower(m0.""FromAddress"")
    JOIN ""imap_folders"" f0 ON f0.""id"" = m0.""FolderId""
    WHERE l0.""AccountId"" = @accountId
      AND f0.""AccountId"" = @accountId
),
global_stats AS (
    SELECT
        count(*)::double precision AS total_count,
        count(*) FILTER (WHERE li.""LabelId"" = @labelId)::double precision AS label_count
    FROM label_ids li
),
folder_stats AS (
    SELECT
        m0.""FolderId"" AS folder_id,
        count(*)::double precision AS total_count,
        count(*) FILTER (WHERE li.""LabelId"" = @labelId)::double precision AS label_count
    FROM label_ids li
    JOIN ""imap_messages"" m0 ON m0.""Id"" = li.""MessageId""
    GROUP BY m0.""FolderId""
),
prior_scores AS (
    SELECT
        fs.folder_id,
        CASE
            WHEN gs.total_count <= 0 OR fs.total_count <= 0 THEN 0.0
            ELSE
                ln(
                    greatest(1e-6, least(1.0 - 1e-6,
                        ((fs.label_count + (@priorAlpha * (gs.label_count / gs.total_count))) / (fs.total_count + @priorAlpha))
                    ))
                    /
                    greatest(1e-6, 1.0 - least(1.0 - 1e-6,
                        ((fs.label_count + (@priorAlpha * (gs.label_count / gs.total_count))) / (fs.total_count + @priorAlpha))
                    ))
                )
                -
                ln(
                    greatest(1e-6, least(1.0 - 1e-6, gs.label_count / gs.total_count))
                    /
                    greatest(1e-6, 1.0 - least(1.0 - 1e-6, gs.label_count / gs.total_count))
                )
        END AS prior_lift
    FROM folder_stats fs
    CROSS JOIN global_stats gs
)
SELECT
    m.""Id"",
    m.""ImapUid"",
    m.""FolderId"",
    m.""ReceivedUtc"",
    {scoreExpr} AS score,
    (-({scoreExpr}) + (@priorLambda * coalesce(ps.prior_lift, 0.0))) AS rank,
    ({bestOtherScoreExpr} - {scoreExpr}) AS dominance_gap
FROM ""message_embeddings"" ml
JOIN ""imap_messages"" m ON m.""Id"" = ml.""MessageId""
JOIN ""imap_folders"" f ON f.""id"" = m.""FolderId""
LEFT JOIN prior_scores ps ON ps.folder_id = m.""FolderId""
WHERE NOT EXISTS (SELECT 1 FROM ""message_labels"" ml2 WHERE ml2.""MessageId"" = m.""Id"")
  AND NOT EXISTS (
      SELECT 1
      FROM ""sender_labels"" sl
      JOIN ""labels"" l ON l.""Id"" = sl.""LabelId""
      WHERE lower(sl.""from_address"") = lower(m.""FromAddress"")
        AND l.""AccountId"" = @accountId)
  AND f.""AccountId"" = @accountId
  AND {scoreExpr} < 0.0
{sinceClause}
ORDER BY rank DESC, score ASC
LIMIT {candidateLimit}";

        var results = new List<SuggestedResult>();
        var dominanceRows = new List<SuggestedCandidate>();

        await using (var cmd = new NpgsqlCommand(sql, conn))
        {
            cmd.Parameters.AddWithValue("accountId", _settings.Id);
            cmd.Parameters.AddWithValue("labelId", labelId);
            cmd.Parameters.AddWithValue("priorAlpha", folderPriorAlpha);
            cmd.Parameters.AddWithValue("priorLambda", folderPriorLambda);
            if (sinceUtc != null)
            {
                cmd.Parameters.AddWithValue("p0", sinceUtc);
            }

            await using var reader = await cmd.ExecuteReaderAsync(token);
            var scoreOrdinal = reader.GetOrdinal("score");
            var rankOrdinal = reader.GetOrdinal("rank");
            var dominanceGapOrdinal = reader.GetOrdinal("dominance_gap");

            var candidateByMessageId = new Dictionary<int, (int FolderId, double Score, double Rank, double? DominanceGap)>();

            while (await reader.ReadAsync(token))
            {
                var id = reader.GetInt32(0);
                var folderId = reader.GetInt32(2);
                var score = reader.GetDouble(scoreOrdinal);
                var rank = reader.GetDouble(rankOrdinal);
                double? dominanceGap = reader.IsDBNull(dominanceGapOrdinal)
                    ? null
                    : reader.GetDouble(dominanceGapOrdinal);

                if (candidateByMessageId.TryGetValue(id, out var existing))
                {
                    var replace = rank > existing.Rank ||
                                  (Math.Abs(rank - existing.Rank) < 1e-9 && score < existing.Score);
                    if (!replace)
                    {
                        continue;
                    }
                }

                candidateByMessageId[id] = (folderId, score, rank, dominanceGap);
            }

            if (candidateByMessageId.Count == 0)
            {
                return [];
            }

            var candidateRows = candidateByMessageId
                .Select(kvp => new
                {
                    MessageId = kvp.Key,
                    kvp.Value.FolderId,
                    kvp.Value.Score,
                    kvp.Value.Rank,
                    kvp.Value.DominanceGap
                })
                .OrderByDescending(c => c.Rank)
                .ThenBy(c => c.Score)
                .ToList();

            var bestRank = candidateRows[0].Rank;
            dominanceRows = candidateRows
                .Where(c =>
                    c.DominanceGap is null ||
                    c.DominanceGap.Value >= -postDominanceGrace ||
                    c.Rank >= bestRank - dominatedNearTopRankDelta)
                .Select(c => new SuggestedCandidate(c.MessageId, c.FolderId, c.Score, c.Rank, c.DominanceGap))
                .ToList();

            if (dominanceRows.Count == 0)
            {
                dominanceRows = candidateRows
                    .Select(c => new SuggestedCandidate(c.MessageId, c.FolderId, c.Score, c.Rank, c.DominanceGap))
                    .ToList();
            }
        }

        if (dominanceRows.Count == 0)
        {
            return [];
        }

        var uniqueRows = await KeepOnlyWinningLabelCandidatesAsync(
            conn,
            _settings.Id,
            labelId,
            folderPriorLambda,
            centroids,
            folderPriorAlpha,
            dominanceRows,
            token);

        var filteredRows = uniqueRows
            .Take(finalLimit)
            .ToList();

        if (filteredRows.Count == 0)
        {
            return [];
        }

        var folderIds = new HashSet<int>();
        var messageIds = new List<int>();
        var scoreMap = new Dictionary<int, double>();
        foreach (var row in filteredRows)
        {
            messageIds.Add(row.MessageId);
            folderIds.Add(row.FolderId);
            scoreMap[row.MessageId] = row.Score;
        }

        var folders = await db.ImapFolders
            .AsNoTracking()
            .Where(f => folderIds.Contains(f.Id))
            .ToListAsync(token);
        var folderMap = folders.ToDictionary(f => f.Id, f => f);

        var messages = await db.ImapMessages
            .AsNoTracking()
            .Where(m => messageIds.Contains(m.Id))
            .ToListAsync(token);
        var messageMap = messages.ToDictionary(m => m.Id, m => m);
        var bodies = await db.MessageBodies
            .AsNoTracking()
            .Where(b => messageIds.Contains(b.MessageId))
            .ToListAsync(token);
        var bodyMap = bodies.ToDictionary(b => b.MessageId, b => b);

        foreach (var row in filteredRows)
        {
            if (!messageMap.TryGetValue(row.MessageId, out var message))
            {
                continue;
            }

            if (!folderMap.TryGetValue(message.FolderId, out var folder))
            {
                continue;
            }

            var score = scoreMap.TryGetValue(message.Id, out var s) ? s : 0.0;
            bodyMap.TryGetValue(message.Id, out var body);
            results.Add(new SuggestedResult(message, folder, body, score));
        }

        return results;
    }

    private async Task<List<SuggestedCandidate>> KeepOnlyWinningLabelCandidatesAsync(
        NpgsqlConnection conn,
        int accountId,
        int targetLabelId,
        double targetLabelLambda,
        Dictionary<int, float[]> centroids,
        double priorAlpha,
        List<SuggestedCandidate> candidates,
        CancellationToken token)
    {
        if (candidates.Count == 0 || centroids.Count <= 1)
        {
            return candidates;
        }

        var messageIds = candidates.Select(c => c.MessageId).Distinct().ToArray();
        var folderIds = candidates.Select(c => c.FolderId).Distinct().ToArray();
        var labelIds = centroids.Keys.OrderBy(id => id).ToArray();

        var embeddingsByMessage = await LoadMessageEmbeddingVectorsAsync(conn, messageIds, token);
        var priorLiftMap = await LoadPriorLiftMapAsync(conn, accountId, folderIds, labelIds, priorAlpha, token);

        var lambdaByLabel = new Dictionary<int, double>(labelIds.Length);
        foreach (var labelId in labelIds)
        {
            if (!centroids.TryGetValue(labelId, out var labelCentroid) || labelCentroid.Length == 0)
            {
                continue;
            }

            // Use the same learned/cached lambda resolution for every competing label
            // so winner selection is symmetric across label views.
            var learned = labelId == targetLabelId
                ? targetLabelLambda
                : await GetLearnedFolderPriorLambdaAsync(conn, accountId, labelId, labelCentroid, priorAlpha, token);
            lambdaByLabel[labelId] = learned;
        }

        var winners = new List<SuggestedCandidate>(candidates.Count);

        foreach (var candidate in candidates)
        {
            if (!embeddingsByMessage.TryGetValue(candidate.MessageId, out var vectors) || vectors.Count == 0)
            {
                continue;
            }

            var winnerLabelId = targetLabelId;
            var winnerScore = double.NegativeInfinity;

            foreach (var labelId in labelIds)
            {
                if (!centroids.TryGetValue(labelId, out var centroid) || centroid.Length == 0)
                {
                    continue;
                }

                var bestSemantic = double.NegativeInfinity;
                foreach (var embedding in vectors)
                {
                    var semantic = DotProduct(embedding, centroid);
                    if (semantic > bestSemantic)
                    {
                        bestSemantic = semantic;
                    }
                }

                var priorLift = priorLiftMap.TryGetValue((labelId, candidate.FolderId), out var lift)
                    ? lift
                    : 0.0d;
                var lambda = lambdaByLabel.TryGetValue(labelId, out var cachedLambda)
                    ? cachedLambda
                    : DefaultFolderPriorLambda;
                var combined = bestSemantic + (lambda * priorLift);

                if (combined > winnerScore ||
                    (Math.Abs(combined - winnerScore) < 1e-9 && labelId < winnerLabelId))
                {
                    winnerScore = combined;
                    winnerLabelId = labelId;
                }
            }

            if (winnerLabelId == targetLabelId)
            {
                winners.Add(candidate);
            }
        }

        return winners;
    }

    private static async Task<Dictionary<int, List<float[]>>> LoadMessageEmbeddingVectorsAsync(
        NpgsqlConnection conn,
        int[] messageIds,
        CancellationToken token)
    {
        var map = new Dictionary<int, List<float[]>>();
        if (messageIds.Length == 0)
        {
            return map;
        }

        const string sql = @"
SELECT me.""MessageId"", me.""Vector""::real[] AS vec
FROM ""message_embeddings"" me
WHERE me.""MessageId"" = ANY(@messageIds)";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("messageIds", messageIds);

        await using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var messageId = reader.GetInt32(0);
            var vec = reader.GetFieldValue<float[]>(1);
            if (!map.TryGetValue(messageId, out var list))
            {
                list = [];
                map[messageId] = list;
            }

            list.Add(vec);
        }

        return map;
    }

    private static async Task<Dictionary<(int LabelId, int FolderId), double>> LoadPriorLiftMapAsync(
        NpgsqlConnection conn,
        int accountId,
        int[] folderIds,
        int[] labelIds,
        double priorAlpha,
        CancellationToken token)
    {
        var map = new Dictionary<(int LabelId, int FolderId), double>();
        if (folderIds.Length == 0 || labelIds.Length == 0)
        {
            return map;
        }

        var sql = @"
WITH label_ids AS (
    SELECT ml0.""LabelId"", ml0.""MessageId""
    FROM ""message_labels"" ml0
    JOIN ""imap_messages"" m0 ON m0.""Id"" = ml0.""MessageId""
    JOIN ""imap_folders"" f0 ON f0.""id"" = m0.""FolderId""
    WHERE f0.""AccountId"" = @accountId
    UNION
    SELECT sl0.""LabelId"", m0.""Id""
    FROM ""sender_labels"" sl0
    JOIN ""labels"" l0 ON l0.""Id"" = sl0.""LabelId""
    JOIN ""imap_messages"" m0 ON sl0.""from_address"" = lower(m0.""FromAddress"")
    JOIN ""imap_folders"" f0 ON f0.""id"" = m0.""FolderId""
    WHERE l0.""AccountId"" = @accountId
      AND f0.""AccountId"" = @accountId
),
global_total AS (
    SELECT count(*)::double precision AS total_count
    FROM label_ids li
),
global_counts AS (
    SELECT li.""LabelId"", count(*)::double precision AS label_count
    FROM label_ids li
    GROUP BY li.""LabelId""
),
folder_counts AS (
    SELECT m0.""FolderId"" AS folder_id, li.""LabelId"", count(*)::double precision AS label_count
    FROM label_ids li
    JOIN ""imap_messages"" m0 ON m0.""Id"" = li.""MessageId""
    WHERE m0.""FolderId"" = ANY(@folderIds)
    GROUP BY m0.""FolderId"", li.""LabelId""
),
folder_totals AS (
    SELECT m0.""FolderId"" AS folder_id, count(*)::double precision AS total_count
    FROM label_ids li
    JOIN ""imap_messages"" m0 ON m0.""Id"" = li.""MessageId""
    WHERE m0.""FolderId"" = ANY(@folderIds)
    GROUP BY m0.""FolderId""
),
grid AS (
    SELECT l.""Id"" AS label_id, f.folder_id
    FROM ""labels"" l
    CROSS JOIN (SELECT unnest(@folderIds)::integer AS folder_id) f
    WHERE l.""AccountId"" = @accountId
      AND l.""Id"" = ANY(@labelIds)
)
SELECT
    g.label_id,
    g.folder_id,
    CASE
        WHEN gt.total_count <= 0 OR ft.total_count <= 0 THEN 0.0
        ELSE
            ln(
                greatest(1e-6, least(1.0 - 1e-6,
                    ((coalesce(fc.label_count, 0.0) + (@priorAlpha * (coalesce(gc.label_count, 0.0) / gt.total_count)))
                    / (ft.total_count + @priorAlpha))
                ))
                /
                greatest(1e-6, 1.0 - least(1.0 - 1e-6,
                    ((coalesce(fc.label_count, 0.0) + (@priorAlpha * (coalesce(gc.label_count, 0.0) / gt.total_count)))
                    / (ft.total_count + @priorAlpha))
                ))
            )
            -
            ln(
                greatest(1e-6, least(1.0 - 1e-6, coalesce(gc.label_count, 0.0) / gt.total_count))
                /
                greatest(1e-6, 1.0 - least(1.0 - 1e-6, coalesce(gc.label_count, 0.0) / gt.total_count))
            )
    END AS prior_lift
FROM grid g
JOIN folder_totals ft ON ft.folder_id = g.folder_id
CROSS JOIN global_total gt
LEFT JOIN global_counts gc ON gc.""LabelId"" = g.label_id
LEFT JOIN folder_counts fc ON fc.folder_id = g.folder_id AND fc.""LabelId"" = g.label_id";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("accountId", accountId);
        cmd.Parameters.AddWithValue("folderIds", folderIds);
        cmd.Parameters.AddWithValue("labelIds", labelIds);
        cmd.Parameters.AddWithValue("priorAlpha", priorAlpha);

        await using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var labelId = reader.GetInt32(0);
            var folderId = reader.GetInt32(1);
            var priorLift = reader.GetDouble(2);
            map[(labelId, folderId)] = priorLift;
        }

        return map;
    }

    private static double DotProduct(float[] a, float[] b)
    {
        var len = Math.Min(a.Length, b.Length);
        double sum = 0.0d;
        for (var i = 0; i < len; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }

    private async Task<double> GetLearnedFolderPriorLambdaAsync(
        NpgsqlConnection conn,
        int accountId,
        int labelId,
        float[] centroid,
        double priorAlpha,
        CancellationToken token)
    {
        var cacheKey = (accountId, labelId);
        var now = DateTimeOffset.UtcNow;

        lock (_folderPriorLambdaCacheGate)
        {
            if (_folderPriorLambdaCache.TryGetValue(cacheKey, out var cached) &&
                now - cached.LearnedAtUtc <= FolderPriorLambdaCacheTtl)
            {
                return cached.Value;
            }
        }

        var examples = await LoadFolderPriorTrainingExamplesAsync(conn, accountId, labelId, centroid, priorAlpha, token);
        var learned = LearnFolderPriorLambda(examples, DefaultFolderPriorLambda);

        lock (_folderPriorLambdaCacheGate)
        {
            _folderPriorLambdaCache[cacheKey] = new LearnedLambdaEntry(learned, now);
        }

        return learned;
    }

    private static async Task<List<FolderPriorTrainingExample>> LoadFolderPriorTrainingExamplesAsync(
        NpgsqlConnection conn,
        int accountId,
        int labelId,
        float[] centroid,
        double priorAlpha,
        CancellationToken token)
    {
        const int trainLimit = 4000;
        var sql = @"
WITH label_ids AS (
    SELECT ml0.""LabelId"", ml0.""MessageId""
    FROM ""message_labels"" ml0
    JOIN ""imap_messages"" m0 ON m0.""Id"" = ml0.""MessageId""
    JOIN ""imap_folders"" f0 ON f0.""id"" = m0.""FolderId""
    WHERE f0.""AccountId"" = @accountId
    UNION
    SELECT sl0.""LabelId"", m0.""Id""
    FROM ""sender_labels"" sl0
    JOIN ""labels"" l0 ON l0.""Id"" = sl0.""LabelId""
    JOIN ""imap_messages"" m0 ON sl0.""from_address"" = lower(m0.""FromAddress"")
    JOIN ""imap_folders"" f0 ON f0.""id"" = m0.""FolderId""
    WHERE l0.""AccountId"" = @accountId
      AND f0.""AccountId"" = @accountId
),
message_targets AS (
    SELECT li.""MessageId"", bool_or(li.""LabelId"" = @labelId) AS is_positive
    FROM label_ids li
    GROUP BY li.""MessageId""
),
global_stats AS (
    SELECT
        count(*)::double precision AS total_count,
        count(*) FILTER (WHERE mt.is_positive)::double precision AS label_count
    FROM message_targets mt
),
folder_stats AS (
    SELECT
        m0.""FolderId"" AS folder_id,
        count(*)::double precision AS total_count,
        count(*) FILTER (WHERE mt.is_positive)::double precision AS label_count
    FROM message_targets mt
    JOIN ""imap_messages"" m0 ON m0.""Id"" = mt.""MessageId""
    GROUP BY m0.""FolderId""
),
prior_scores AS (
    SELECT
        fs.folder_id,
        CASE
            WHEN gs.total_count <= 0 OR fs.total_count <= 0 THEN 0.0
            ELSE
                ln(
                    greatest(1e-6, least(1.0 - 1e-6,
                        ((fs.label_count + (@priorAlpha * (gs.label_count / gs.total_count))) / (fs.total_count + @priorAlpha))
                    ))
                    /
                    greatest(1e-6, 1.0 - least(1.0 - 1e-6,
                        ((fs.label_count + (@priorAlpha * (gs.label_count / gs.total_count))) / (fs.total_count + @priorAlpha))
                    ))
                )
                -
                ln(
                    greatest(1e-6, least(1.0 - 1e-6, gs.label_count / gs.total_count))
                    /
                    greatest(1e-6, 1.0 - least(1.0 - 1e-6, gs.label_count / gs.total_count))
                )
        END AS prior_lift
    FROM folder_stats fs
    CROSS JOIN global_stats gs
),
ranked_messages AS (
    SELECT
        mt.""MessageId"" AS message_id,
        m.""FolderId"" AS folder_id,
        mt.is_positive
    FROM message_targets mt
    JOIN ""imap_messages"" m ON m.""Id"" = mt.""MessageId""
    JOIN ""imap_folders"" f ON f.""id"" = m.""FolderId""
    WHERE f.""AccountId"" = @accountId
    ORDER BY m.""ReceivedUtc"" DESC
    LIMIT @trainLimit
),
semantic_scores AS (
    SELECT
        rm.message_id,
        rm.folder_id,
        rm.is_positive,
        min(me.""Vector"" <#> @centroid::halfvec) AS score
    FROM ranked_messages rm
    JOIN ""message_embeddings"" me ON me.""MessageId"" = rm.message_id
    GROUP BY rm.message_id, rm.folder_id, rm.is_positive
)
SELECT
    -(ss.score) AS semantic_score,
    coalesce(ps.prior_lift, 0.0) AS prior_lift,
    ss.is_positive
FROM semantic_scores ss
LEFT JOIN prior_scores ps ON ps.folder_id = ss.folder_id
WHERE ss.score < 0.0";

        var examples = new List<FolderPriorTrainingExample>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("accountId", accountId);
        cmd.Parameters.AddWithValue("labelId", labelId);
        cmd.Parameters.AddWithValue("priorAlpha", priorAlpha);
        cmd.Parameters.AddWithValue("trainLimit", trainLimit);
        cmd.Parameters.AddWithValue("centroid", BuildVectorLiteral(centroid));

        await using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var semantic = reader.GetDouble(0);
            var priorLift = reader.GetDouble(1);
            var isPositive = reader.GetBoolean(2);
            if (!double.IsFinite(semantic) || !double.IsFinite(priorLift))
            {
                continue;
            }

            examples.Add(new FolderPriorTrainingExample(semantic, priorLift, isPositive));
        }

        return examples;
    }

    private static double LearnFolderPriorLambda(
        List<FolderPriorTrainingExample> examples,
        double fallbackLambda)
    {
        if (examples.Count < 200)
        {
            return fallbackLambda;
        }

        var positives = examples.Count(e => e.IsPositive);
        if (positives < 10 || positives >= examples.Count)
        {
            return fallbackLambda;
        }

        var lambdaGrid = new[] { 0.0d, 0.10d, 0.20d, 0.35d, 0.50d, 0.75d, 1.00d, 1.50d, 2.00d, 3.00d };

        var bestLambda = fallbackLambda;
        var bestLoss = double.PositiveInfinity;

        foreach (var lambda in lambdaGrid)
        {
            var loss = 0.0d;
            foreach (var ex in examples)
            {
                var z = ex.SemanticScore + (lambda * ex.PriorLift);
                var p = Sigmoid(z);
                var y = ex.IsPositive ? 1.0d : 0.0d;
                var clamped = Math.Min(1.0d - 1e-12, Math.Max(1e-12, p));
                loss += -(y * Math.Log(clamped) + ((1.0d - y) * Math.Log(1.0d - clamped)));
            }

            // Small regularizer so we do not over-weight folder prior when training signal is weak.
            loss += 0.02d * lambda * lambda * examples.Count;

            if (loss < bestLoss)
            {
                bestLoss = loss;
                bestLambda = lambda;
            }
        }

        return bestLambda;
    }

    private static double Sigmoid(double x)
    {
        var clamped = Math.Max(-40.0d, Math.Min(40.0d, x));
        return 1.0d / (1.0d + Math.Exp(-clamped));
    }

    private void ClearFolderPriorLambdaCache()
    {
        lock (_folderPriorLambdaCacheGate)
        {
            _folderPriorLambdaCache.Clear();
        }
    }

    private static async Task<Dictionary<int, float[]>> GetCentroidsAsync(NpgsqlConnection conn, int accountId, CancellationToken token)
    {
        var sql = @"
WITH label_ids AS (
    SELECT ml.""LabelId"", ml.""MessageId""
    FROM ""message_labels"" ml
    JOIN ""imap_messages"" m ON m.""Id"" = ml.""MessageId""
    JOIN ""imap_folders"" f ON f.""id"" = m.""FolderId""
    WHERE f.""AccountId"" = @accountId
    UNION
    SELECT sl.""LabelId"", m.""Id""
    FROM ""sender_labels"" sl
    JOIN ""labels"" l ON l.""Id"" = sl.""LabelId""
    JOIN ""imap_messages"" m ON sl.""from_address"" = lower(m.""FromAddress"")
    JOIN ""imap_folders"" f ON f.""id"" = m.""FolderId""
    WHERE l.""AccountId"" = @accountId
      AND f.""AccountId"" = @accountId
)
SELECT lids.""LabelId"", avg(e.""Vector"")::real[] AS centroid
FROM ""message_embeddings"" e
JOIN label_ids lids ON lids.""MessageId"" = e.""MessageId""
GROUP BY lids.""LabelId""";

        var map = new Dictionary<int, float[]>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("accountId", accountId);
        await using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            if (reader.IsDBNull(1))
            {
                continue;
            }

            var labelId = reader.GetInt32(0);
            var vec = reader.GetFieldValue<float[]>(1);
            map[labelId] = NormalizeCentroid(vec);
        }

        return map;
    }

    private static float[] NormalizeCentroid(float[] source)
    {
        if (source.Length == 0)
        {
            return source;
        }

        var normalized = (float[])source.Clone();
        double sumSquares = 0.0d;
        for (var i = 0; i < normalized.Length; i++)
        {
            var value = normalized[i];
            sumSquares += value * value;
        }

        if (sumSquares <= 1e-20d)
        {
            return normalized;
        }

        var invNorm = (float)(1.0d / Math.Sqrt(sumSquares));
        for (var i = 0; i < normalized.Length; i++)
        {
            normalized[i] *= invNorm;
        }

        return normalized;
    }

    private static EmailMessageViewModel CreateEmailViewModel(ImapMessage message, Models.ImapFolder folder, MessageBody? body)
    {
        var displaySender = !string.IsNullOrWhiteSpace(message.FromName)
            ? message.FromName
            : string.IsNullOrWhiteSpace(message.FromAddress) ? "(Unknown sender)" : message.FromAddress;

        var colorComponents = SenderColorHelper.GetColor(message.FromName, message.FromAddress);
        var messageColor = new Color
        {
            A = 255,
            R = colorComponents.R,
            G = colorComponents.G,
            B = colorComponents.B
        };

        var previewSource = body?.Preview ?? message.Subject;

        return new EmailMessageViewModel
        {
            Id = message.ImapUid.ToString(),
            IsLocalOnly = message.ImapUid <= 0,
            FolderId = folder.FullName,
            Subject = message.Subject,
            Sender = displaySender,
            SenderAddress = message.FromAddress ?? string.Empty,
            SenderInitials = SenderInitialsHelper.From(message.FromName, message.FromAddress),
            SenderColor = messageColor,
            Preview = BuildPreview(previewSource),
            Received = message.ReceivedUtc.LocalDateTime,
            IsRead = message.IsRead
        };
    }

    private List<EmailMessageViewModel> ApplyFolderViewFilter(IEnumerable<EmailMessageViewModel> messages)
    {
        var filtered = messages;

        if (_viewModel.UnlabeledOnly)
        {
            filtered = filtered.Where(IsUnlabeled);
        }

        return filtered.ToList();
    }

    private List<EmailMessageViewModel> ApplyLabelViewFilter(IEnumerable<EmailMessageViewModel> messages)
    {
        var filtered = messages;

        if (_viewModel.LabelUnreadOnly)
        {
            filtered = filtered.Where(m => m.IsUnread);
        }
        else if (_viewModel.SuggestionsOnly)
        {
            filtered = filtered.Where(m => m.IsSuggested);
        }

        return filtered.ToList();
    }

    private static bool IsUnlabeled(EmailMessageViewModel message) =>
        message.LabelNames == null || message.LabelNames.Count == 0;

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
            var manager = new PostgresLargeObjectStore(conn, tx);
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

    public async Task LoadLabelMessagesAsync(int labelId, DateTimeOffset? sinceUtc)
    {
        var token = _cts.Token;
        if (_settings == null)
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
            var label = await db.Labels
                .AsNoTracking()
                .FirstOrDefaultAsync(l => l.Id == labelId && l.AccountId == _settings.Id, token);

            if (label == null)
            {
                await ReportStatusAsync("Label not found.", false);
                return;
            }

            await ReportStatusAsync($"Loading {label.Name}…", true);

            try
            {
                var explicitResults = await (
                    from ml in db.MessageLabels.AsNoTracking()
                    where ml.LabelId == labelId
                    join m in db.ImapMessages.AsNoTracking() on ml.MessageId equals m.Id
                    join f in db.ImapFolders.AsNoTracking() on m.FolderId equals f.Id
                    where f.AccountId == _settings.Id
                    join b in db.MessageBodies.AsNoTracking() on m.Id equals b.MessageId into bodies
                    from b in bodies.DefaultIfEmpty()
                    select new { Message = m, Folder = f, Body = b }
                ).ToListAsync(token);

                var explicitSet = explicitResults
                    .Select(r =>
                    {
                    var vm = CreateEmailViewModel(r.Message, r.Folder, r.Body);
                    vm.IsSuggested = false;
                    return vm;
                })
                .ToList();

                var senderResults = await (
                    from sl in db.SenderLabels.AsNoTracking()
                    where sl.LabelId == labelId
                    join m in db.ImapMessages.AsNoTracking() on sl.FromAddress equals m.FromAddress.ToLower()
                    join f in db.ImapFolders.AsNoTracking() on m.FolderId equals f.Id
                    where f.AccountId == _settings.Id
                    join b in db.MessageBodies.AsNoTracking() on m.Id equals b.MessageId into bodies
                    from b in bodies.DefaultIfEmpty()
                    select new { Message = m, Folder = f, Body = b }
                ).ToListAsync(token);

                var senderVms = senderResults.Select(r =>
                {
                    var vm = CreateEmailViewModel(r.Message, r.Folder, r.Body);
                    vm.IsSuggested = false;
                    return vm;
                }).ToList();

                var suggestions = await GetSuggestedMessagesAsync(db, labelId, sinceUtc, token);
                var explicitIds = explicitResults.Select(r => r.Message.Id).ToHashSet();

                foreach (var vm in senderVms)
                {
                    if (!explicitIds.Contains(int.Parse(vm.Id)))
                    {
                        explicitSet.Add(vm);
                        explicitIds.Add(int.Parse(vm.Id));
                    }
                }

                foreach (var suggestion in suggestions)
                {
                    if (explicitIds.Contains(suggestion.Message.Id))
                    {
                        continue;
                    }

                    var vm = CreateEmailViewModel(suggestion.Message, suggestion.Folder, suggestion.Body);
                    vm.IsSuggested = true;
                    // convert negative inner product to cosine distance:
                    // 0 (exact match) .. 1 (orthogonal) .. 2 (opposite meaning)
                    // real world values I've seen are in the range ~0.057 - ~1.1, so SuggestionToBrushConverter
                    // maps 0.0 to soft green and 1.0 to soft orange.
                    vm.SuggestionScore = suggestion.Score + 1.0d;
                    explicitSet.Add(vm);
                }

                var ordered = ApplyLabelViewFilter(explicitSet
                    .OrderByDescending(m => m.Received)
                    .ToList());

                await EnqueueAsync(() =>
                {
                    _viewModel.SetMessages($"Label: {label.Name}", ordered);
                    _viewModel.SetStatus("Mailbox is up to date.", false);
                    _viewModel.SetLoadMoreAvailability(false);
                    _viewModel.SetRetryVisible(false);
                });
            }
            catch (Exception ex)
            {
                await ReportStatusAsync($"Unable to load label: {ex.Message}", false);
                await EnqueueAsync(() => _viewModel.SetRetryVisible(true));
            }
        }
    }

    public async Task<LabelViewModel?> CreateLabelAsync(string name, int? parentLabelId)
    {
        var token = _cts.Token;
        if (_settings == null)
        {
            return null;
        }

        var cleaned = TextCleaner.CleanNullable(name)?.Trim();
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return null;
        }

        var db = await CreateDbContextAsync();
        if (db == null)
        {
            return null;
        }

        await using (db)
        {
            var entity = new Label
            {
                AccountId = _settings.Id,
                Name = cleaned,
                ParentLabelId = parentLabelId
            };

            db.Labels.Add(entity);

            try
            {
                await db.SaveChangesAsync(token);
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                entity = await db.Labels
                    .AsNoTracking()
                    .FirstOrDefaultAsync(l =>
                        l.AccountId == _settings.Id &&
                        l.ParentLabelId == parentLabelId &&
                        l.Name == cleaned, token);

                if (entity == null)
                {
                    throw;
                }
            }

            var vm = new LabelViewModel(entity.Id, entity.Name, entity.ParentLabelId);
            await EnqueueAsync(() => _viewModel.AddLabel(vm, parentLabelId));
            await ReportStatusAsync($"Created label \"{entity.Name}\"", false);
            return vm;
        }
    }

    public async Task<bool> MoveMessageToFolderAsync(string sourceFolderFullName, string messageId, string targetFolderFullName)
    {
        var token = _cts.Token;
        if (_settings == null ||
            string.IsNullOrWhiteSpace(sourceFolderFullName) ||
            string.IsNullOrWhiteSpace(targetFolderFullName) ||
            string.IsNullOrWhiteSpace(messageId))
        {
            return false;
        }

        if (!long.TryParse(messageId, out var sourceUid))
        {
            return false;
        }

        var db = await CreateDbContextAsync();
        if (db == null)
        {
            return false;
        }

        await using (db)
        {
            var sourceFolder = await db.ImapFolders
                .AsNoTracking()
                .FirstOrDefaultAsync(f => f.AccountId == _settings.Id && f.FullName == sourceFolderFullName, token);

            if (sourceFolder == null)
            {
                return false;
            }

            var targetFolder = await db.ImapFolders
                .AsNoTracking()
                .FirstOrDefaultAsync(f => f.AccountId == _settings.Id && f.FullName == targetFolderFullName, token);

            if (targetFolder == null)
            {
                targetFolder = new Models.ImapFolder
                {
                    AccountId = _settings.Id,
                    FullName = targetFolderFullName,
                    DisplayName = targetFolderFullName
                };
                db.ImapFolders.Add(targetFolder);
                await db.SaveChangesAsync(token);
            }

            var message = await db.ImapMessages
                .FirstOrDefaultAsync(m => m.FolderId == sourceFolder.Id && m.ImapUid == sourceUid, token);

            if (message == null)
            {
                return false;
            }

            var movedOnServer = false;
            long? serverAssignedUid = null;
            if (sourceUid > 0)
            {
                var moveResult = await TryMoveMessageOnServerAsync(sourceFolderFullName, targetFolderFullName, sourceUid, message.MessageId, token);
                movedOnServer = moveResult.Moved;
                serverAssignedUid = moveResult.TargetUid;
            }

            var desiredUid = serverAssignedUid ?? await GetNextSyntheticImapUidAsync(db, targetFolder.Id, token);
            var finalUid = await EnsureUniqueImapUidAsync(db, targetFolder.Id, desiredUid, message.Id, token);

            message.FolderId = targetFolder.Id;
            message.ImapUid = finalUid;
            message.Hash = $"{message.MessageId}:{finalUid}";
            await db.SaveChangesAsync(token);

            if (movedOnServer)
            {
                await ReportStatusAsync($"Moved message to {targetFolderFullName}.", false);
            }
            else
            {
                await ReportStatusAsync($"Message not found on server; moved locally to {targetFolderFullName}.", false);
            }

            return true;
        }
    }

    public async Task<bool> SetMessageReadStateAsync(string folderFullName, string messageId, bool isRead)
        => await SetMessageReadStateInternalAsync(folderFullName, messageId, isRead, refreshCounts: true);

    private async Task<bool> SetMessageReadStateInternalAsync(string folderFullName, string messageId, bool isRead, bool refreshCounts)
    {
        var token = _cts.Token;
        if (_settings == null || string.IsNullOrWhiteSpace(folderFullName) || string.IsNullOrWhiteSpace(messageId))
        {
            return false;
        }

        if (!long.TryParse(messageId, out var uid))
        {
            return false;
        }

        var db = await CreateDbContextAsync();
        if (db == null)
        {
            return false;
        }

        var updated = false;

        await using (db)
        {
            var folder = await db.ImapFolders
                .AsNoTracking()
                .FirstOrDefaultAsync(f => f.AccountId == _settings.Id && f.FullName == folderFullName, token);
            if (folder == null)
            {
                return false;
            }

            var message = await db.ImapMessages
                .FirstOrDefaultAsync(m => m.FolderId == folder.Id && m.ImapUid == uid, token);
            if (message == null)
            {
                return false;
            }

            if (message.ImapUid > 0)
            {
                var serverResult = await TrySetMessageReadStateOnServerAsync(folderFullName, message.ImapUid, isRead, token);
                if (serverResult == ServerReadWriteResult.Failed)
                {
                    return false;
                }
            }

            if (message.IsRead != isRead)
            {
                message.IsRead = isRead;
                await db.SaveChangesAsync(token);
                updated = true;
            }
        }

        if (updated)
        {
            await EnqueueAsync(() =>
            {
                if (_viewModel.SelectedMessage != null &&
                    string.Equals(_viewModel.SelectedMessage.FolderId, folderFullName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(_viewModel.SelectedMessage.Id, messageId, StringComparison.Ordinal))
                {
                    _viewModel.SelectedMessage.IsRead = isRead;
                }

                foreach (var visible in _viewModel.Messages)
                {
                    if (string.Equals(visible.FolderId, folderFullName, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(visible.Id, messageId, StringComparison.Ordinal))
                    {
                        visible.IsRead = isRead;
                    }
                }
            });
        }

        if (refreshCounts)
        {
            await RefreshUnreadCountsForReadStateChangeAsync(folderFullName, token);
        }

        return true;
    }

    public async Task<int> MarkAllReadInFolderAsync(string folderFullName)
    {
        var token = _cts.Token;
        Debug.WriteLine($"[MarkAllRead][Service] enter folder={folderFullName}");
        if (_settings == null || string.IsNullOrWhiteSpace(folderFullName))
        {
            Debug.WriteLine($"[MarkAllRead][Service] abort folder={folderFullName} reason=missing-settings-or-folder");
            return 0;
        }

        var db = await CreateDbContextAsync();
        if (db == null)
        {
            Debug.WriteLine($"[MarkAllRead][Service] abort folder={folderFullName} reason=db-null");
            return 0;
        }

        int folderId;
        await using (db)
        {
            var folder = await db.ImapFolders
                .AsNoTracking()
                .FirstOrDefaultAsync(f => f.AccountId == _settings.Id && f.FullName == folderFullName, token);
            if (folder == null)
            {
                Debug.WriteLine($"[MarkAllRead][Service] abort folder={folderFullName} reason=folder-not-found-in-db");
                return 0;
            }

            folderId = folder.Id;
        }

        var serverUnreadUids = await SearchUnreadUidsOnServerAsync(folderFullName, token);
        Debug.WriteLine(
            $"[MarkAllRead][Service] unread-sources folder={folderFullName} serverUnreadCount={serverUnreadUids.Count}");

        if (serverUnreadUids.Count == 0)
        {
            Debug.WriteLine($"[MarkAllRead][Service] server-no-op folder={folderFullName} serverUnreadCount=0");
        }

        Debug.WriteLine($"[MarkAllRead][Service] batch-start folder={folderFullName} serverUnreadCount={serverUnreadUids.Count}");
        var updatedCount = await MarkEntireFolderReadBatchAsync(folderFullName, folderId, serverUnreadUids, token);
        Debug.WriteLine($"[MarkAllRead][Service] batch-finished folder={folderFullName} updatedCount={updatedCount}");
        await RefreshUnreadCountsForReadStateChangeAsync(folderFullName, token);
        Debug.WriteLine($"[MarkAllRead][Service] refresh-finished folder={folderFullName}");
        return updatedCount;
    }

    public async Task<int> MarkAllReadInLabelAsync(int labelId)
    {
        var token = _cts.Token;
        if (_settings == null)
        {
            return 0;
        }

        var db = await CreateDbContextAsync();
        if (db == null)
        {
            return 0;
        }

        List<(int FolderId, string FolderFullName, long ImapUid)> targets;
        await using (db)
        {
            var label = await db.Labels
                .AsNoTracking()
                .FirstOrDefaultAsync(l => l.Id == labelId && l.AccountId == _settings.Id, token);
            if (label == null)
            {
                return 0;
            }

            var explicitTargets = await (
                    from ml in db.MessageLabels.AsNoTracking()
                    where ml.LabelId == labelId
                    join m in db.ImapMessages.AsNoTracking() on ml.MessageId equals m.Id
                    join f in db.ImapFolders.AsNoTracking() on m.FolderId equals f.Id
                    where f.AccountId == _settings.Id && !m.IsRead
                    select new { f.Id, f.FullName, m.ImapUid })
                .ToListAsync(token);

            var senderTargets = await (
                    from sl in db.SenderLabels.AsNoTracking()
                    where sl.LabelId == labelId
                    join m in db.ImapMessages.AsNoTracking() on sl.FromAddress equals m.FromAddress.ToLower()
                    join f in db.ImapFolders.AsNoTracking() on m.FolderId equals f.Id
                    where f.AccountId == _settings.Id && !m.IsRead
                    select new { f.Id, f.FullName, m.ImapUid })
                .ToListAsync(token);

            targets = explicitTargets
                .Concat(senderTargets)
                .GroupBy(x => (x.Id, x.FullName, x.ImapUid))
                .Select(g => g.Key)
                .ToList();
        }

        var updatedCount = 0;
        var affectedFolderNames = targets
            .Select(t => t.FolderFullName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var folderGroup in targets.GroupBy(t => (t.FolderId, t.FolderFullName)))
        {
            var batchCount = await MarkMessagesReadBatchAsync(
                folderGroup.Key.FolderFullName,
                folderGroup.Key.FolderId,
                folderGroup.Select(t => t.ImapUid).ToList(),
                token);
            updatedCount += batchCount;
        }

        await RefreshFolderUnreadCountsAsync(affectedFolderNames, token);
        await RefreshLabelUnreadCountsAsync(token);
        return updatedCount;
    }

    public async Task<bool> FolderExistsAsync(string folderFullName)
    {
        if (string.IsNullOrWhiteSpace(folderFullName))
        {
            return false;
        }

        var token = _cts.Token;
        if (!await _semaphore.WaitAsync(TimeSpan.FromMinutes(1), token))
        {
            Debug.WriteLine("Semaphore timeout: " + Environment.StackTrace);
            return false;
        }

        try
        {
            if (_client == null)
            {
                return false;
            }

            if (_folderCache.ContainsKey(folderFullName))
            {
                return true;
            }

            var refreshed = await LoadFoldersAsync(token);
            return refreshed.Any(f => string.Equals(f.Id, folderFullName, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private enum ServerReadWriteResult
    {
        Success,
        NotFound,
        Failed
    }

    private async Task<ServerReadWriteResult> TrySetMessageReadStateOnServerAsync(
        string folderFullName,
        long uid,
        bool isRead,
        CancellationToken token)
    {
        IMailFolder? folder = null;

        if (!await _semaphore.WaitAsync(TimeSpan.FromMinutes(1), token))
        {
            Debug.WriteLine("Semaphore timeout: " + Environment.StackTrace);
            return ServerReadWriteResult.Failed;
        }

        try
        {
            if (_client == null)
            {
                return ServerReadWriteResult.Failed;
            }

            if (!_folderCache.TryGetValue(folderFullName, out folder))
            {
                return ServerReadWriteResult.Failed;
            }

            if (!folder.IsOpen)
            {
                await folder.OpenAsync(FolderAccess.ReadWrite, token);
            }

            var uniqueId = new UniqueId((uint)uid);
            if (isRead)
            {
                await folder.AddFlagsAsync(uniqueId, MessageFlags.Seen, true, token);
            }
            else
            {
                await folder.RemoveFlagsAsync(uniqueId, MessageFlags.Seen, true, token);
            }

            return ServerReadWriteResult.Success;
        }
        catch (MessageNotFoundException)
        {
            return ServerReadWriteResult.NotFound;
        }
        catch (ImapCommandException ex) when (
            ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("no such message", StringComparison.OrdinalIgnoreCase))
        {
            return ServerReadWriteResult.NotFound;
        }
        catch
        {
            return ServerReadWriteResult.Failed;
        }
        finally
        {
            try
            {
                if (folder?.IsOpen == true)
                {
                    await folder.CloseAsync(false, token);
                }
            }
            catch
            {
            }

            _semaphore.Release();
        }
    }

    private async Task<int> MarkMessagesReadBatchAsync(
        string folderFullName,
        int folderId,
        IReadOnlyCollection<long> unreadUids,
        CancellationToken token)
    {
        if (unreadUids.Count == 0)
        {
            return 0;
        }

        var positiveUids = unreadUids
            .Where(uid => uid > 0)
            .Select(uid => new UniqueId((uint)uid))
            .ToList();
        var localOnlyUids = unreadUids
            .Where(uid => uid <= 0)
            .ToList();

        if (positiveUids.Count > 0)
        {
            var serverResult = await TrySetMessagesReadStateOnServerAsync(folderFullName, positiveUids, isRead: true, token);
            if (serverResult == ServerReadWriteResult.Failed)
            {
                return 0;
            }
        }

        var db = await CreateDbContextAsync();
        if (db == null)
        {
            return 0;
        }

        await using (db)
        {
            var allUids = positiveUids.Select(uid => (long)uid.Id).Concat(localOnlyUids).ToArray();
            var messages = await db.ImapMessages
                .Where(m => m.FolderId == folderId && allUids.Contains(m.ImapUid) && !m.IsRead)
                .ToListAsync(token);

            if (messages.Count == 0)
            {
                return 0;
            }

            foreach (var message in messages)
            {
                message.IsRead = true;
            }

            await db.SaveChangesAsync(token);

            var updatedIds = messages.Select(m => m.ImapUid.ToString(CultureInfo.InvariantCulture)).ToHashSet(StringComparer.Ordinal);
            await EnqueueAsync(() =>
            {
                if (_viewModel.SelectedMessage != null &&
                    string.Equals(_viewModel.SelectedMessage.FolderId, folderFullName, StringComparison.OrdinalIgnoreCase) &&
                    updatedIds.Contains(_viewModel.SelectedMessage.Id))
                {
                    _viewModel.SelectedMessage.IsRead = true;
                }

                foreach (var visible in _viewModel.Messages)
                {
                    if (string.Equals(visible.FolderId, folderFullName, StringComparison.OrdinalIgnoreCase) &&
                        updatedIds.Contains(visible.Id))
                    {
                        visible.IsRead = true;
                    }
                }
            });

            return messages.Count;
        }
    }

    private async Task<int> MarkEntireFolderReadBatchAsync(
        string folderFullName,
        int folderId,
        IReadOnlyCollection<long> serverUnreadUids,
        CancellationToken token)
    {
        var positiveUids = serverUnreadUids
            .Where(uid => uid > 0)
            .Select(uid => new UniqueId((uint)uid))
            .ToList();

        if (positiveUids.Count > 0)
        {
            var serverResult = await TrySetMessagesReadStateOnServerAsync(folderFullName, positiveUids, isRead: true, token);
            if (serverResult == ServerReadWriteResult.Failed)
            {
                return 0;
            }
        }

        var db = await CreateDbContextAsync();
        if (db == null)
        {
            return 0;
        }

        await using (db)
        {
            var messages = await db.ImapMessages
                .Where(m => m.FolderId == folderId && !m.IsRead)
                .ToListAsync(token);

            if (messages.Count == 0)
            {
                return 0;
            }

            foreach (var message in messages)
            {
                message.IsRead = true;
            }

            await db.SaveChangesAsync(token);

            var updatedIds = messages.Select(m => m.ImapUid.ToString(CultureInfo.InvariantCulture)).ToHashSet(StringComparer.Ordinal);
            await EnqueueAsync(() =>
            {
                if (_viewModel.SelectedMessage != null &&
                    string.Equals(_viewModel.SelectedMessage.FolderId, folderFullName, StringComparison.OrdinalIgnoreCase) &&
                    updatedIds.Contains(_viewModel.SelectedMessage.Id))
                {
                    _viewModel.SelectedMessage.IsRead = true;
                }

                foreach (var visible in _viewModel.Messages)
                {
                    if (string.Equals(visible.FolderId, folderFullName, StringComparison.OrdinalIgnoreCase) &&
                        updatedIds.Contains(visible.Id))
                    {
                        visible.IsRead = true;
                    }
                }
            });

            return messages.Count;
        }
    }

    private async Task<ServerReadWriteResult> TrySetMessagesReadStateOnServerAsync(
        string folderFullName,
        IList<UniqueId> uids,
        bool isRead,
        CancellationToken token)
    {
        IMailFolder? folder = null;

        if (uids.Count == 0)
        {
            return ServerReadWriteResult.Success;
        }

        if (!await _semaphore.WaitAsync(TimeSpan.FromMinutes(1), token))
        {
            Debug.WriteLine("Semaphore timeout: " + Environment.StackTrace);
            return ServerReadWriteResult.Failed;
        }

        try
        {
            if (_client == null)
            {
                Debug.WriteLine($"[MarkAllRead][IMAP] skipped folder={folderFullName} reason=client-null uidCount={uids.Count}");
                return ServerReadWriteResult.Failed;
            }

            if (!_folderCache.TryGetValue(folderFullName, out folder))
            {
                Debug.WriteLine($"[MarkAllRead][IMAP] skipped folder={folderFullName} reason=folder-not-cached uidCount={uids.Count}");
                return ServerReadWriteResult.Failed;
            }

            if (!folder.IsOpen)
            {
                await folder.OpenAsync(FolderAccess.ReadWrite, token);
            }

            Debug.WriteLine(
                $"[MarkAllRead][IMAP] start folder={folderFullName} action={(isRead ? "mark-read" : "mark-unread")} uidCount={uids.Count}");

            if (isRead)
            {
                await folder.AddFlagsAsync(uids, MessageFlags.Seen, true, token);
            }
            else
            {
                await folder.RemoveFlagsAsync(uids, MessageFlags.Seen, true, token);
            }

            Debug.WriteLine(
                $"[MarkAllRead][IMAP] success folder={folderFullName} action={(isRead ? "mark-read" : "mark-unread")} uidCount={uids.Count}");
            return ServerReadWriteResult.Success;
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine(
                $"[MarkAllRead][IMAP] canceled folder={folderFullName} action={(isRead ? "mark-read" : "mark-unread")} uidCount={uids.Count}");
            throw;
        }
        catch
        {
            Debug.WriteLine(
                $"[MarkAllRead][IMAP] failed folder={folderFullName} action={(isRead ? "mark-read" : "mark-unread")} uidCount={uids.Count}");
            return ServerReadWriteResult.Failed;
        }
        finally
        {
            try
            {
                if (folder?.IsOpen == true)
                {
                    await folder.CloseAsync(false, token);
                }
            }
            catch
            {
            }

            _semaphore.Release();
        }
    }

    private async Task<List<long>> SearchUnreadUidsOnServerAsync(string folderFullName, CancellationToken token)
    {
        IMailFolder? folder = null;

        if (!await _semaphore.WaitAsync(TimeSpan.FromMinutes(1), token))
        {
            Debug.WriteLine($"[MarkAllRead][IMAP] unread-search-skipped folder={folderFullName} reason=semaphore-timeout");
            return [];
        }

        try
        {
            if (_client == null)
            {
                Debug.WriteLine($"[MarkAllRead][IMAP] unread-search-skipped folder={folderFullName} reason=client-null");
                return [];
            }

            if (!_folderCache.TryGetValue(folderFullName, out folder))
            {
                Debug.WriteLine($"[MarkAllRead][IMAP] unread-search-skipped folder={folderFullName} reason=folder-not-cached");
                return [];
            }

            if (!folder.IsOpen)
            {
                await folder.OpenAsync(FolderAccess.ReadOnly, token);
            }

            Debug.WriteLine($"[MarkAllRead][IMAP] unread-search-start folder={folderFullName}");
            var uids = await folder.SearchAsync(SearchQuery.NotSeen, token);
            Debug.WriteLine($"[MarkAllRead][IMAP] unread-search-success folder={folderFullName} uidCount={uids.Count}");
            return uids.Select(uid => (long)uid.Id).ToList();
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine($"[MarkAllRead][IMAP] unread-search-canceled folder={folderFullName}");
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MarkAllRead][IMAP] unread-search-failed folder={folderFullName} error={ex.Message}");
            return [];
        }
        finally
        {
            try
            {
                if (folder?.IsOpen == true)
                {
                    await folder.CloseAsync(false, token);
                }
            }
            catch
            {
            }

            _semaphore.Release();
        }
    }

    private async Task RefreshNavigationCountsAsync(CancellationToken token)
    {
        IReadOnlyList<MailFolderViewModel> folders;
        try
        {
            folders = _client != null && _client.IsConnected
                ? await LoadFoldersAsync(token)
                : await LoadFoldersFromDatabaseAsync(token);
        }
        catch
        {
            folders = await LoadFoldersFromDatabaseAsync(token);
        }
        var labels = await LoadLabelsAsync(token);

        await EnqueueAsync(() =>
        {
            _viewModel.SetFolders(folders);
            _viewModel.SetLabels(labels);
        });
    }

    private async Task RefreshUnreadCountsForReadStateChangeAsync(string? folderFullName, CancellationToken token)
    {
        if (!string.IsNullOrWhiteSpace(folderFullName))
        {
            await RefreshFolderUnreadCountAsync(folderFullName, token);
        }

        await RefreshLabelUnreadCountsAsync(token);
    }

    private async Task RefreshFolderUnreadCountsAsync(IEnumerable<string> folderFullNames, CancellationToken token)
    {
        var names = folderFullNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var name in names)
        {
            await RefreshFolderUnreadCountAsync(name, token);
        }
    }

    private async Task RefreshFolderUnreadCountAsync(string folderFullName, CancellationToken token)
    {
        if (_settings == null || string.IsNullOrWhiteSpace(folderFullName))
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
            var folderCount = await (
                    from f in db.ImapFolders.AsNoTracking()
                    where f.AccountId == _settings.Id && f.FullName == folderFullName
                    select db.ImapMessages.Count(m => m.FolderId == f.Id && !m.IsRead))
                .FirstOrDefaultAsync(token);

            await EnqueueAsync(() => _viewModel.UpdateFolderCounts(folderFullName, folderCount));
        }
    }

    private async Task RefreshLabelUnreadCountsAsync(CancellationToken token)
    {
        if (_settings == null)
        {
            return;
        }

        var counts = await LoadLabelUnreadCountsAsync(token);
        await EnqueueAsync(() => _viewModel.UpdateAllLabelUnreadCounts(counts));
    }

    private async Task<Dictionary<int, int>> LoadLabelUnreadCountsAsync(CancellationToken token)
    {
        var result = new Dictionary<int, int>();
        if (_settings == null)
        {
            return result;
        }

        var db = await CreateDbContextAsync();
        if (db == null)
        {
            return result;
        }

        await using (db)
        {
            var labelIds = await db.Labels
                .AsNoTracking()
                .Where(l => l.AccountId == _settings.Id)
                .Select(l => l.Id)
                .ToArrayAsync(token);

            if (labelIds.Length == 0)
            {
                return result;
            }

            var explicitUnread = await (
                    from ml in db.MessageLabels.AsNoTracking()
                    where labelIds.Contains(ml.LabelId)
                    join m in db.ImapMessages.AsNoTracking() on ml.MessageId equals m.Id
                    where !m.IsRead
                    select new { ml.LabelId, m.Id })
                .ToListAsync(token);

            var senderUnread = await (
                    from sl in db.SenderLabels.AsNoTracking()
                    where labelIds.Contains(sl.LabelId)
                    join m in db.ImapMessages.AsNoTracking() on sl.FromAddress equals m.FromAddress.ToLower()
                    join f in db.ImapFolders.AsNoTracking() on m.FolderId equals f.Id
                    where f.AccountId == _settings.Id && !m.IsRead
                    select new { sl.LabelId, m.Id })
                .ToListAsync(token);

            return explicitUnread
                .Concat(senderUnread)
                .GroupBy(x => x.LabelId)
                .ToDictionary(g => g.Key, g => g.Select(x => x.Id).Distinct().Count());
        }
    }

    private async Task<(bool Moved, long? TargetUid)> TryMoveMessageOnServerAsync(
        string sourceFolderFullName,
        string targetFolderFullName,
        long sourceUid,
        string messageIdHeader,
        CancellationToken token)
    {
        IMailFolder? sourceFolder = null;
        IMailFolder? targetFolder = null;

        if (!await _semaphore.WaitAsync(TimeSpan.FromMinutes(1), token))
        {
            Debug.WriteLine("Semaphore timeout: " + Environment.StackTrace);
            return (false, null);
        }

        try
        {
            if (_client == null)
            {
                return (false, null);
            }

            if (!_folderCache.TryGetValue(sourceFolderFullName, out sourceFolder) ||
                !_folderCache.TryGetValue(targetFolderFullName, out targetFolder))
            {
                return (false, null);
            }

            if (!sourceFolder.IsOpen)
            {
                await sourceFolder.OpenAsync(FolderAccess.ReadWrite, token);
            }

            await sourceFolder.MoveToAsync(new UniqueId((uint)sourceUid), targetFolder, token);

            if (!targetFolder.IsOpen)
            {
                await targetFolder.OpenAsync(FolderAccess.ReadOnly, token);
            }

            if (!string.IsNullOrWhiteSpace(messageIdHeader))
            {
                var matching = await targetFolder.SearchAsync(
                    SearchQuery.HeaderContains("Message-Id", messageIdHeader),
                    token);

                var resolvedUid = matching.Count > 0
                    ? matching.Max(uid => (long)uid.Id)
                    : (long?)null;

                return (true, resolvedUid);
            }

            return (true, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return (false, null);
        }
        finally
        {
            try
            {
                if (sourceFolder?.IsOpen == true)
                {
                    await sourceFolder.CloseAsync(false, token);
                }
            }
            catch
            {
            }

            try
            {
                if (targetFolder?.IsOpen == true)
                {
                    await targetFolder.CloseAsync(false, token);
                }
            }
            catch
            {
            }

            _semaphore.Release();
        }
    }

    private static async Task<long> GetNextSyntheticImapUidAsync(MailDbContext db, int folderId, CancellationToken token)
    {
        var existingUids = await db.ImapMessages
            .Where(m => m.FolderId == folderId && m.ImapUid <= 0)
            .Select(m => m.ImapUid)
            .ToListAsync(token);

        return ComputeNextSyntheticUid(existingUids);
    }

    private static async Task<long> EnsureUniqueImapUidAsync(
        MailDbContext db,
        int folderId,
        long desiredUid,
        int currentMessageId,
        CancellationToken token)
    {
        var usedUids = await db.ImapMessages
            .Where(m => m.FolderId == folderId && m.Id != currentMessageId)
            .Select(m => m.ImapUid)
            .ToHashSetAsync(token);

        return EnsureUniqueUidCandidate(desiredUid, usedUids);
    }

    internal static long ComputeNextSyntheticUid(IEnumerable<long> existingFolderUids)
    {
        var minExisting = existingFolderUids
            .Where(uid => uid <= 0)
            .DefaultIfEmpty(1)
            .Min();

        return minExisting <= 0 ? minExisting - 1 : -1;
    }

    internal static long EnsureUniqueUidCandidate(long desiredUid, ISet<long> usedUids)
    {
        var uid = desiredUid;
        while (true)
        {
            if (!usedUids.Contains(uid))
            {
                return uid;
            }

            uid = uid <= 0 ? uid - 1 : -1;
        }
    }

    internal static string BuildMessageDedupKey(string? messageId, long imapUid)
    {
        var cleaned = messageId?.Trim();
        return string.IsNullOrWhiteSpace(cleaned)
            ? $"uid:{imapUid}"
            : $"mid:{cleaned}";
    }

    internal static bool IsPreferredDedupUid(long imapUid) => imapUid <= 0;

    internal static bool ShouldHydrateOfflineFallback(bool connectFailed, bool offlineFallbackHydrated) =>
        connectFailed && !offlineFallbackHydrated;

    internal static bool ShouldUseDbBodyFallback(bool clientIsNull, bool clientConnected, bool folderCached) =>
        clientIsNull || !clientConnected || !folderCached;

    internal static string GetOfflineBodyFallbackStatusMessage() =>
        "Showing saved copy from PostgreSQL (offline mode).";

    public async Task<bool> AssignLabelToMessageAsync(int labelId, string folderId, string messageId)
    {
        var token = _cts.Token;
        if (_settings == null || string.IsNullOrWhiteSpace(folderId) || string.IsNullOrWhiteSpace(messageId))
        {
            return false;
        }

        if (!long.TryParse(messageId, out var uid))
        {
            return false;
        }

        var db = await CreateDbContextAsync();
        if (db == null)
        {
            return false;
        }

        await using (db)
        {
            var label = await db.Labels
                .AsNoTracking()
                .FirstOrDefaultAsync(l => l.Id == labelId && l.AccountId == _settings.Id, token);

            if (label == null)
            {
                return false;
            }

            var folderEntity = await db.ImapFolders
                .AsNoTracking()
                .FirstOrDefaultAsync(f => f.AccountId == _settings.Id && f.FullName == folderId, token);

            if (folderEntity == null)
            {
                return false;
            }

            var message = await db.ImapMessages
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.FolderId == folderEntity.Id && m.ImapUid == uid, token);

            if (message == null)
            {
                return false;
            }

            var exists = await db.MessageLabels
                .AnyAsync(ml => ml.LabelId == labelId && ml.MessageId == message.Id, token);

            if (exists)
            {
                await ReportStatusAsync($"Message already has label \"{label.Name}\"", false);
                return true;
            }

            db.MessageLabels.Add(new MessageLabel
            {
                LabelId = labelId,
                MessageId = message.Id
            });

            try
            {
                await db.SaveChangesAsync(token);
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                return true;
            }

            ClearFolderPriorLambdaCache();
            await ReportStatusAsync($"Applied label \"{label.Name}\"", false);
            return true;
        }
    }

    public async Task<bool> AddSenderLabelAsync(int labelId, string fromAddress)
    {
        var token = _cts.Token;
        if (_settings == null || string.IsNullOrWhiteSpace(fromAddress))
        {
            return false;
        }

        var cleaned = TextCleaner.CleanNullable(fromAddress)?.Trim();
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return false;
        }

        var db = await CreateDbContextAsync();
        if (db == null)
        {
            return false;
        }

        await using (db)
        {
            var label = await db.Labels
                .AsNoTracking()
                .FirstOrDefaultAsync(l => l.Id == labelId && l.AccountId == _settings.Id, token);

            if (label == null)
            {
                return false;
            }

            var normalizedAddress = cleaned.ToLowerInvariant();
            var existing = await db.SenderLabels
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.LabelId == labelId && s.FromAddress == normalizedAddress, token);

            if (existing != null)
            {
                await UpdateVisibleMessagesForSenderLabelAsync(cleaned, label.Id, label.Name);
                return true;
            }

            db.SenderLabels.Add(new SenderLabel
            {
                FromAddress = normalizedAddress,
                LabelId = labelId
            });

            try
            {
                await db.SaveChangesAsync(token);
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                await UpdateVisibleMessagesForSenderLabelAsync(cleaned, label.Id, label.Name);
                return true;
            }

            ClearFolderPriorLambdaCache();
            await UpdateVisibleMessagesForSenderLabelAsync(cleaned, label.Id, label.Name);
        }

        await ReportStatusAsync($"Labeled sender {cleaned}", false);
        return true;
    }

    private Task UpdateVisibleMessagesForSenderLabelAsync(string senderAddress, int labelId, string labelName)
    {
        return EnqueueAsync(() => ApplySenderLabelToVisibleMessages(_viewModel, senderAddress, labelId, labelName));
    }

    internal static void ApplySenderLabelToVisibleMessages(MailboxViewModel viewModel, string senderAddress, int labelId, string labelName)
    {
        var normalizedAddress = senderAddress?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedAddress) || string.IsNullOrWhiteSpace(labelName))
        {
            return;
        }

        var inUnlabeledFolderView =
            viewModel.UnlabeledOnly &&
            viewModel.SelectedFolder != null &&
            !viewModel.IsSearchActive &&
            viewModel.SelectedLabelId == null;
        var selectedLabelId = viewModel.SelectedLabelId;
        var inLabelView = selectedLabelId != null && !viewModel.IsSearchActive;

        if (inUnlabeledFolderView)
        {
            for (var i = viewModel.Messages.Count - 1; i >= 0; i--)
            {
                var message = viewModel.Messages[i];
                if (string.Equals(message.SenderAddress?.Trim(), normalizedAddress, StringComparison.OrdinalIgnoreCase))
                {
                    viewModel.Messages.RemoveAt(i);
                }
            }

            return;
        }

        for (var i = viewModel.Messages.Count - 1; i >= 0; i--)
        {
            var message = viewModel.Messages[i];
            if (!string.Equals(message.SenderAddress?.Trim(), normalizedAddress, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (inLabelView && selectedLabelId != labelId && message.IsSuggested)
            {
                viewModel.Messages.RemoveAt(i);
                continue;
            }

            var names = message.LabelNames ?? [];
            if (names.Any(n => string.Equals(n, labelName, StringComparison.OrdinalIgnoreCase)))
            {
                if (inLabelView && selectedLabelId == labelId && message.IsSuggested)
                {
                    message.IsSuggested = false;
                    message.SuggestionScore = Double.NegativeInfinity;
                }
                continue;
            }

            message.LabelNames = [..names, labelName];
            if (inLabelView && selectedLabelId == labelId && message.IsSuggested)
            {
                message.IsSuggested = false;
                message.SuggestionScore = Double.NegativeInfinity;
            }
        }
    }

    private async Task<MessageBodyResult?> LoadMessageBodyFromDatabaseAsync(string folderId, string messageId, CancellationToken token)
    {
        if (_settings == null || !long.TryParse(messageId, out var uid))
        {
            return null;
        }

        var db = await CreateDbContextAsync();
        if (db == null)
        {
            return null;
        }

        await using (db)
        {
            var folderEntity = await db.ImapFolders
                .AsNoTracking()
                .Where(f => f.AccountId == _settings.Id && f.FullName == folderId)
                .FirstOrDefaultAsync(token);

            if (folderEntity == null)
            {
                return null;
            }

            var message = await db.ImapMessages
                .Include(m => m.Body)
                .Where(m => m.FolderId == folderEntity.Id && m.ImapUid == uid)
                .FirstOrDefaultAsync(token);

            if (message?.Body == null)
            {
                return null;
            }

            var html = await HtmlSanitizer.BuildFallbackHtmlAsync(db, message.Body, token);
            var headers = BuildHeaderInfo(message, message.Body);

            return new MessageBodyResult(html, headers);
        }
    }

    private static MessageHeaderInfo BuildHeaderInfo(ImapMessage message, MessageBody body)
    {
        var fromName = TextCleaner.CleanNullable(message.FromName);
        var fromAddress = TextCleaner.CleanNullable(message.FromAddress);
        var from = !string.IsNullOrWhiteSpace(fromName) && !string.IsNullOrWhiteSpace(fromAddress)
            ? $"{fromName} <{fromAddress}>"
            : fromName ?? fromAddress ?? string.Empty;

        var to = JoinHeaderAddresses(body.Headers, "To") ?? string.Empty;
        var cc = JoinHeaderAddresses(body.Headers, "Cc");
        var bcc = JoinHeaderAddresses(body.Headers, "Bcc");

        return new MessageHeaderInfo(
            From: from,
            FromAddress: fromAddress ?? string.Empty,
            To: to,
            Cc: cc,
            Bcc: bcc);
    }

    private static string? JoinHeaderAddresses(Dictionary<string, string[]>? headers, string name)
    {
        if (headers == null)
        {
            return null;
        }

        foreach (var kvp in headers)
        {
            if (string.IsNullOrWhiteSpace(kvp.Key) || !kvp.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var values = kvp.Value?
                .Select(TextCleaner.CleanNullable)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .ToArray();

            if (values != null && values.Length > 0)
            {
                return string.Join(", ", values);
            }
        }

        return null;
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
            var missingFromServer = false;
            string? missingReason = null;

            bool locked;

            try
            {
                locked = await _semaphore.WaitAsync(TimeSpan.FromMinutes(1), _cts.Token);
            }
            catch (OperationCanceledException)
            {
                await ReportStatusAsync($"Unable to load message: operation cancelled", false);
                await EnqueueAsync(() => _viewModel.SetRetryVisible(true));
                return null;
            }

            if (!locked)
            {
                Debug.WriteLine("Semaphore timeout: " + Environment.StackTrace);
                await ReportStatusAsync($"Unable to load message: semaphore timeout", false);
                await EnqueueAsync(() => _viewModel.SetRetryVisible(true));
                return null;
            }

            try
            {
                if (ShouldUseDbBodyFallback(clientIsNull: _client == null, clientConnected: _client?.IsConnected == true, folderCached: true))
                {
                    var cached = await LoadMessageBodyFromDatabaseAsync(folderId, messageId, _cts.Token);
                    if (cached != null)
                    {
                        await ReportStatusAsync(GetOfflineBodyFallbackStatusMessage(), false);
                        await EnqueueAsync(() => _viewModel.SetRetryVisible(true));
                        return cached;
                    }

                    throw new ServiceNotConnectedException();
                }

                if (!_folderCache.TryGetValue(folderId, out folder))
                {
                    if (ShouldUseDbBodyFallback(clientIsNull: false, clientConnected: true, folderCached: false))
                    {
                        var cached = await LoadMessageBodyFromDatabaseAsync(folderId, messageId, _cts.Token);
                        if (cached != null)
                        {
                            await ReportStatusAsync(GetOfflineBodyFallbackStatusMessage(), false);
                            await EnqueueAsync(() => _viewModel.SetRetryVisible(true));
                            return cached;
                        }
                    }

                    if (canRetry && await TryReconnectAsync())
                    {
                        canRetry = false;
                        continue;
                    }

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
            catch (MessageNotFoundException ex)
            {
                missingFromServer = true;
                missingReason = ex.Message;
            }
            catch (Exception ex)
            {
                if (canRetry && IsRecoverable(ex) && await TryReconnectAsync())
                {
                    canRetry = false;
                    continue;
                }

                Debug.WriteLine(ex);

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

            if (missingFromServer)
            {
                var cached = await LoadMessageBodyFromDatabaseAsync(folderId, messageId, _cts.Token);
                if (cached != null)
                {
                    await ReportStatusAsync("Showing saved copy from PostgreSQL.", false);
                    await EnqueueAsync(() => _viewModel.SetRetryVisible(false));
                    return cached;
                }

                await ReportStatusAsync($"Unable to load message: {missingReason}", false);
                await EnqueueAsync(() => _viewModel.SetRetryVisible(true));
                return null;
            }
        }
    }
}
