using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using maildot.Data;
using maildot.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.ML.OnnxRuntime;
using ModelContextProtocol.Server;
using Npgsql;
using Desc = System.ComponentModel.DescriptionAttribute;

namespace maildot.Services;

// TODO there is too much duplication between MCP and pre-existing search code; refactor to share more logic
// and especially use the same QwenEmbedder instance.
[McpServerToolType]
public static class McpTools
{
    private const int MaxListCount = 200;
    private const int MaxSearchResults = 50;
    private const int EmbeddingDim = 1024;

    [McpServerTool(Name = "list_accounts")]
    [Desc("Lists configured IMAP accounts with basic metadata (id, display_name, server, username, last_synced_at). No secrets returned.")]
    public static async Task<List<AccountResult>> ListAccountsAsync(
        [Desc("Optional cancellation token")] CancellationToken cancellationToken = default)
    {
        await using var db = await CreateDbContextAsync(cancellationToken);

        return await db.ImapAccounts
            .AsNoTracking()
            .OrderBy(a => a.DisplayName)
            .ThenBy(a => a.Username)
            .Take(MaxListCount)
            .Select(a => new AccountResult(
                a.Id,
                a.DisplayName,
                a.Server,
                a.Username,
                a.LastSyncedAt))
            .ToListAsync(cancellationToken);
    }

    [McpServerTool(Name = "list_folders")]
    [Desc("Lists folders for an account (id, full_name, display_name, uid_validity, last_uid); supports optional full-name prefix filter.")]
    public static async Task<List<FolderResult>> ListFoldersAsync(
        [Desc("Account id; defaults to the active/first account when null.")] int? accountId = null,
        [Desc("Optional folder full-name prefix (e.g., \"Inbox\" or \"Inbox/Sub\").")] string? prefix = null,
        [Desc("Optional cancellation token.")] CancellationToken cancellationToken = default)
    {
        await using var db = await CreateDbContextAsync(cancellationToken);
        var account = await ResolveAccountAsync(db, accountId, cancellationToken);

        var query = db.ImapFolders
            .AsNoTracking()
            .Where(f => f.AccountId == account.Id);

        if (!string.IsNullOrWhiteSpace(prefix))
        {
            query = query.Where(f => f.FullName.StartsWith(prefix));
        }

        return await query
            .OrderBy(f => f.FullName)
            .Take(MaxListCount)
            .Select(f => new FolderResult(
                f.Id,
                f.FullName,
                f.DisplayName,
                f.UidValidity,
                f.LastUid))
            .ToListAsync(cancellationToken);
    }

    [McpServerTool(Name = "list_labels")]
    [Desc("Lists labels for an account (id, name, parent_label_id) so callers can reconstruct hierarchy.")]
    public static async Task<List<LabelResult>> ListLabelsAsync(
        [Desc("Account id; defaults to the active/first account when null.")] int? accountId = null,
        [Desc("Optional cancellation token.")] CancellationToken cancellationToken = default)
    {
        await using var db = await CreateDbContextAsync(cancellationToken);
        var account = await ResolveAccountAsync(db, accountId, cancellationToken);

        return await db.Labels
            .AsNoTracking()
            .Where(l => l.AccountId == account.Id)
            .OrderBy(l => l.ParentLabelId)
            .ThenBy(l => l.Name)
            .Take(MaxListCount)
            .Select(l => new LabelResult(l.Id, l.Name, l.ParentLabelId))
            .ToListAsync(cancellationToken);
    }

    [McpServerTool(Name = "search_messages")]
    [Desc("Searches mail by subject, sender, and/or embedding relevance, or lists messages when no query is provided. " +
          "Returns up to 50 items with message_id, imap_uid, folder_full_name, subject, from fields, preview, received_utc, score " +
          "(lower is better), source=subject|sender|embedding|list.")]
    public static async Task<List<SearchMessageResult>> SearchMessagesAsync(
        [Desc("Optional search query. If empty/null, returns messages without text/embedding filtering. '*' wildcarding not supported. " +
              "Subject and sender searches are simple substring filters. Content search is via vector embeddings only. " +
              "Boolean AND/OR are not directly supported, but the embeddings model might understand.")] string? query = null,
        [Desc("Search mode: auto | sender | content | all | subject (default: auto).")] string? mode = "auto",
        [Desc("Optional ISO-8601 timestamp filter (e.g., 2025-12-16T00:00:00Z).")] string? sinceUtc = null,
        [Desc("Optional exclusive upper bound on ImapUid for pagination (e.g., request older pages).")] long? imapUidLessThan = null,
        [Desc("Account id; defaults to the active/first account when null.")] int? accountId = null,
        [Desc("Optional cancellation token.")] CancellationToken cancellationToken = default)
    {
        var trimmed = TextCleaner.CleanNullable(query) ?? string.Empty;

        await using var db = await CreateDbContextAsync(cancellationToken);
        var account = await ResolveAccountAsync(db, accountId, cancellationToken);

        DateTimeOffset? since = null;
        if (!string.IsNullOrWhiteSpace(sinceUtc) &&
            DateTimeOffset.TryParse(sinceUtc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            since = parsed;
        }

        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return await ListMessagesAsync(db, account.Id, since, imapUidLessThan, cancellationToken);
        }

        var effectiveMode = ResolveSearchMode(trimmed, mode);

        var subjectResults = ShouldSearchSubject(effectiveMode)
            ? await SearchBySubjectAsync(db, account.Id, trimmed, since, imapUidLessThan, cancellationToken)
            : [];

        var senderResults = ShouldSearchSender(effectiveMode)
            ? await SearchBySenderAsync(db, account.Id, trimmed, since, imapUidLessThan, cancellationToken)
            : [];

        var vectorResults = ShouldSearchVector(effectiveMode)
            ? await SearchByEmbeddingAsync(db, account.Id, trimmed, since, imapUidLessThan, cancellationToken)
            : [];

        return MergeResults(subjectResults, senderResults, vectorResults);
    }

    [McpServerTool(Name = "get_message_body")]
    [Desc("Returns sanitized HTML and basic headers for a message from the database copy (no IMAP fetch).")]
    public static async Task<MessageBodyResult?> GetMessageBodyAsync(
        [Desc("Folder full name (matches search results).")] string folderFullName,
        [Desc("Message IMAP UID within the folder.")] long imapUid,
        [Desc("Account id; defaults to the active/first account when null.")] int? accountId = null,
        [Desc("Optional cancellation token.")] CancellationToken cancellationToken = default)
    {
        await using var db = await CreateDbContextAsync(cancellationToken);
        var account = await ResolveAccountAsync(db, accountId, cancellationToken);

        var folder = await db.ImapFolders
            .AsNoTracking()
            .Where(f => f.AccountId == account.Id && f.FullName == folderFullName)
            .FirstOrDefaultAsync(cancellationToken);

        if (folder == null)
        {
            return null;
        }

        var message = await db.ImapMessages
            .AsNoTracking()
            .Include(m => m.Body)
            .Where(m => m.FolderId == folder.Id && m.ImapUid == imapUid)
            .FirstOrDefaultAsync(cancellationToken);

        if (message?.Body == null)
        {
            return null;
        }

        var headers = new MessageHeaderInfo(
            From: FormatMailbox(message.FromName, message.FromAddress),
            FromAddress: TextCleaner.CleanNullable(message.FromAddress) ?? string.Empty,
            To: string.Empty,
            Cc: null,
            Bcc: null);

        var html = BuildFallbackHtml(message.Body);
        return new MessageBodyResult(html, headers);
    }

    [McpServerTool(Name = "list_attachments")]
    [Desc("Lists attachments for a message; optionally returns base64 data (respecting max_bytes). Output: file_name, content_type, size_bytes, disposition, base64_data?.")]
    public static async Task<List<AttachmentResult>> ListAttachmentsAsync(
        [Desc("Folder full name (matches search results).")] string folderFullName,
        [Desc("Message IMAP UID within the folder.")] long imapUid,
        [Desc("Account id; defaults to the active/first account when null.")] int? accountId = null,
        [Desc("Optional content-type prefix filter (default: image/).")] string? contentTypePrefix = "image/",
        [Desc("Include base64 data payloads when true (default: false).")] bool includeData = false,
        [Desc("If includeData=true, max bytes to read per attachment; null reads full object.")] int? maxBytes = null,
        [Desc("Optional cancellation token.")] CancellationToken cancellationToken = default)
    {
        await using var db = await CreateDbContextAsync(cancellationToken);
        var account = await ResolveAccountAsync(db, accountId, cancellationToken);

        var folder = await db.ImapFolders
            .AsNoTracking()
            .Where(f => f.AccountId == account.Id && f.FullName == folderFullName)
            .FirstOrDefaultAsync(cancellationToken);

        if (folder == null)
        {
            return [];
        }

        var message = await db.ImapMessages
            .AsNoTracking()
            .Where(m => m.FolderId == folder.Id && m.ImapUid == imapUid)
            .FirstOrDefaultAsync(cancellationToken);

        if (message == null)
        {
            return [];
        }

        var attachmentsQuery = db.MessageAttachments
            .AsNoTracking()
            .Where(a => a.MessageId == message.Id);

        if (!string.IsNullOrWhiteSpace(contentTypePrefix))
        {
            attachmentsQuery = attachmentsQuery
                .Where(a => a.ContentType.StartsWith(contentTypePrefix, StringComparison.OrdinalIgnoreCase));
        }

        var attachments = await attachmentsQuery
            .OrderBy(a => a.FileName)
            .Take(MaxListCount)
            .ToListAsync(cancellationToken);

        if (!includeData || attachments.Count == 0)
        {
            return attachments
                .Select(a => new AttachmentResult(
                    a.FileName,
                    a.ContentType,
                    a.SizeBytes,
                    a.Disposition,
                    null))
                .ToList();
        }

        var results = new List<AttachmentResult>(attachments.Count);
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync(cancellationToken);
        }

        await using var tx = await conn.BeginTransactionAsync(cancellationToken);
        var manager = new NpgsqlLargeObjectManager(conn);

        foreach (var attachment in attachments)
        {
            if (attachment.LargeObjectId == 0)
            {
                continue;
            }

            try
            {
                await using var stream = await manager.OpenReadAsync(attachment.LargeObjectId, cancellationToken);
                using var ms = new MemoryStream();
                if (maxBytes.HasValue && maxBytes.Value > 0)
                {
                    var buffer = new byte[8192];
                    var remaining = maxBytes.Value;
                    int read;
                    while (remaining > 0 &&
                           (read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellationToken)) > 0)
                    {
                        ms.Write(buffer, 0, read);
                        remaining -= read;
                    }
                }
                else
                {
                    await stream.CopyToAsync(ms, cancellationToken);
                }

                var base64 = Convert.ToBase64String(ms.ToArray());
                results.Add(new AttachmentResult(
                    attachment.FileName,
                    attachment.ContentType,
                    attachment.SizeBytes,
                    attachment.Disposition,
                    base64));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to read attachment {attachment.FileName}: {ex}");
            }
        }

        await tx.CommitAsync(cancellationToken);
        return results;
    }

    [McpServerTool(Name = "get_schema_snapshot")]
    [Desc("Returns a high-level table/column snapshot for the mail database (static helper).")]
    public static SchemaSnapshot GetSchemaSnapshot()
    {
        return new SchemaSnapshot(
            Tables: new List<string>
            {
                "imap_accounts (id, display_name, server, port, use_ssl, username, last_synced_at)",
                "imap_folders (id, account_id, full_name, display_name, uid_validity, last_uid, sync_token)",
                "imap_messages (id, folder_id, imap_uid, message_id, subject, from_name, from_address, received_utc, hash, headers)",
                "message_bodies (message_id, plain_text, html_text, sanitized_html, preview)",
                "message_attachments (id, message_id, file_name, content_type, size_bytes, hash, large_object_id)",
                "message_embeddings (message_id, chunk_index, embedding vector(1024), model_version, created_at)",
                "labels (id, account_id, name, parent_label_id)",
                "message_labels (label_id, message_id)",
                "sender_labels (id, from_address, label_id)"
            });
    }

    private static async Task<MailDbContext> CreateDbContextAsync(CancellationToken cancellationToken)
    {
        var settings = PostgresSettingsStore.Load();
        if (!settings.HasCredentials)
        {
            throw new InvalidOperationException("PostgreSQL settings are incomplete.");
        }

        var passwordResponse = await CredentialManager.RequestPostgresPasswordAsync(settings);
        if (passwordResponse.Result != CredentialAccessResult.Success || string.IsNullOrWhiteSpace(passwordResponse.Password))
        {
            throw new InvalidOperationException("PostgreSQL password is unavailable. Re-enter it in Settings.");
        }

        return MailDbContextFactory.CreateDbContext(settings, passwordResponse.Password);
    }

    private static async Task<ImapAccount> ResolveAccountAsync(MailDbContext db, int? accountId, CancellationToken cancellationToken)
    {
        if (accountId.HasValue)
        {
            var match = await db.ImapAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == accountId.Value, cancellationToken);
            if (match != null)
            {
                return match;
            }

            throw new InvalidOperationException($"Account {accountId.Value} not found.");
        }

        var active = AccountSettingsStore.GetActiveAccount();
        if (active != null)
        {
            var match = await db.ImapAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == active.Id, cancellationToken);
            if (match != null)
            {
                return match;
            }
        }

        var first = await db.ImapAccounts.AsNoTracking()
            .OrderBy(a => a.DisplayName)
            .ThenBy(a => a.Username)
            .FirstOrDefaultAsync(cancellationToken);

        if (first == null)
        {
            throw new InvalidOperationException("No accounts are configured.");
        }

        return first;
    }

    private static async Task<List<SearchMessageResult>> ListMessagesAsync(
        MailDbContext db,
        int accountId,
        DateTimeOffset? sinceUtc,
        long? imapUidLessThan,
        CancellationToken token)
    {
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
        {
            await conn.OpenAsync(token);
        }

        var sql = new System.Text.StringBuilder();
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
        if (imapUidLessThan.HasValue)
        {
            sql.Append("AND m.\"ImapUid\" < @imapUidLessThan ");
        }
        sql.Append("ORDER BY m.\"ImapUid\" DESC ");
        sql.Append("LIMIT @limit");

        await using var cmd = new NpgsqlCommand(sql.ToString(), conn);
        cmd.Parameters.AddWithValue("accountId", accountId);
        cmd.Parameters.AddWithValue("limit", MaxSearchResults);
        if (sinceUtc.HasValue)
        {
            cmd.Parameters.AddWithValue("sinceUtc", sinceUtc.Value);
        }
        if (imapUidLessThan.HasValue)
        {
            cmd.Parameters.AddWithValue("imapUidLessThan", imapUidLessThan.Value);
        }

        var results = new List<SearchMessageResult>();
        await using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var subject = TextCleaner.CleanNullable(reader.GetString(2)) ?? string.Empty;
            var fromName = TextCleaner.CleanNullable(reader.GetString(3)) ?? string.Empty;
            var fromAddress = TextCleaner.CleanNullable(reader.GetString(4)) ?? string.Empty;
            var preview = TextCleaner.CleanNullable(reader.IsDBNull(6) ? string.Empty : reader.GetString(6)) ?? string.Empty;
            var folderFullName = TextCleaner.CleanNullable(reader.GetString(7)) ?? string.Empty;

            results.Add(new SearchMessageResult(
                reader.GetInt32(0),
                reader.GetInt64(1),
                folderFullName,
                string.IsNullOrWhiteSpace(subject) ? "(No subject)" : subject,
                string.IsNullOrWhiteSpace(fromName) ? fromAddress : fromName,
                fromAddress,
                string.IsNullOrWhiteSpace(preview) ? subject : preview,
                reader.GetFieldValue<DateTimeOffset>(5),
                0,
                "list"));
        }

        return results;
    }

    private static SearchMode ResolveSearchMode(string query, string? mode)
    {
        if (!Enum.TryParse<SearchMode>(mode ?? string.Empty, true, out var parsed))
        {
            parsed = SearchMode.Auto;
        }

        if (parsed != SearchMode.Auto)
        {
            return parsed;
        }

        return query.Contains("@", StringComparison.Ordinal) ? SearchMode.Sender : SearchMode.All;
    }

    private static bool ShouldSearchSubject(SearchMode mode) =>
        mode == SearchMode.Subject || mode == SearchMode.All || mode == SearchMode.Auto;

    private static bool ShouldSearchSender(SearchMode mode) =>
        mode == SearchMode.Sender || mode == SearchMode.All || mode == SearchMode.Auto;

    private static bool ShouldSearchVector(SearchMode mode) =>
        mode == SearchMode.Content || mode == SearchMode.All || mode == SearchMode.Auto;

    private static async Task<List<SearchResult>> SearchBySubjectAsync(
        MailDbContext db,
        int accountId,
        string query,
        DateTimeOffset? sinceUtc,
        long? imapUidLessThan,
        CancellationToken token)
    {
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync(token);
        }

        var sql = new System.Text.StringBuilder();
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
        if (imapUidLessThan.HasValue)
        {
            sql.Append("AND m.\"ImapUid\" < @imapUidLessThan ");
        }
        sql.Append("ORDER BY m.\"ImapUid\" DESC LIMIT @limit");

        await using var cmd = new NpgsqlCommand(sql.ToString(), conn);
        cmd.Parameters.AddWithValue("accountId", accountId);
        cmd.Parameters.AddWithValue("subjectPattern", $"%{query}%");
        cmd.Parameters.AddWithValue("limit", MaxSearchResults);
        if (sinceUtc.HasValue)
        {
            cmd.Parameters.AddWithValue("sinceUtc", sinceUtc.Value);
        }
        if (imapUidLessThan.HasValue)
        {
            cmd.Parameters.AddWithValue("imapUidLessThan", imapUidLessThan.Value);
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
                reader.GetInt32(0),
                reader.GetInt64(1),
                subject,
                fromName,
                fromAddress,
                reader.GetFieldValue<DateTimeOffset>(5),
                preview,
                folderFullName,
                0,
                "subject"));
        }

        return results;
    }

    private static async Task<List<SearchResult>> SearchBySenderAsync(
        MailDbContext db,
        int accountId,
        string query,
        DateTimeOffset? sinceUtc,
        long? imapUidLessThan,
        CancellationToken token)
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

        var sql = new System.Text.StringBuilder();
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
        if (imapUidLessThan.HasValue)
        {
            sql.Append("AND m.\"ImapUid\" < @imapUidLessThan ");
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

        sql.Append("ORDER BY m.\"ImapUid\" DESC LIMIT @limit");

        await using var cmd = new NpgsqlCommand(sql.ToString(), conn);
        cmd.Parameters.AddWithValue("accountId", accountId);
        cmd.Parameters.AddWithValue("limit", MaxSearchResults);
        if (sinceUtc.HasValue)
        {
            cmd.Parameters.AddWithValue("sinceUtc", sinceUtc.Value);
        }
        if (imapUidLessThan.HasValue)
        {
            cmd.Parameters.AddWithValue("imapUidLessThan", imapUidLessThan.Value);
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
                reader.GetInt32(0),
                reader.GetInt64(1),
                subject,
                fromName,
                fromAddress,
                reader.GetFieldValue<DateTimeOffset>(5),
                preview,
                folderFullName,
                0,
                "sender"));
        }

        return results;
    }

    private static async Task<List<SearchResult>> SearchByEmbeddingAsync(
        MailDbContext db,
        int accountId,
        string query,
        DateTimeOffset? sinceUtc,
        long? imapUidLessThan,
        CancellationToken token)
    {
        var embedder = await EnsureSearchEmbedderAsync() ??
            throw new InvalidOperationException("Embedding model is unavailable.");

        var embedded = embedder.EmbedQuery(query);
        if (embedded == null)
        {
            return [];
        }

        var vector = NormalizeVector(embedded);
        var vectorLiteral = BuildVectorLiteral(vector);

        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
        {
            await conn.OpenAsync(token);
        }

        var vectorSql = new System.Text.StringBuilder();
        vectorSql.Append("SELECT ranked.\"MessageId\", ranked.\"ImapUid\", ranked.\"Subject\", ranked.\"FromName\", ranked.\"FromAddress\", ");
        vectorSql.Append("ranked.\"ReceivedUtc\", ranked.\"Preview\", ranked.\"FullName\", ranked.negative_inner_product + 1 AS cosine_distance ");
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
        if (imapUidLessThan.HasValue)
        {
            vectorSql.Append("AND m.\"ImapUid\" < @imapUidLessThan ");
        }
        vectorSql.Append("    ORDER BY me.\"MessageId\", negative_inner_product ");
        vectorSql.Append(") AS ranked ");
        vectorSql.Append("ORDER BY cosine_distance ");
        vectorSql.Append("LIMIT @limit");

        await using var cmd = new NpgsqlCommand(vectorSql.ToString(), conn);
        cmd.Parameters.AddWithValue("queryVec", vectorLiteral);
        cmd.Parameters.AddWithValue("accountId", accountId);
        cmd.Parameters.AddWithValue("limit", MaxSearchResults);
        if (sinceUtc.HasValue)
        {
            cmd.Parameters.AddWithValue("sinceUtc", sinceUtc.Value);
        }
        if (imapUidLessThan.HasValue)
        {
            cmd.Parameters.AddWithValue("imapUidLessThan", imapUidLessThan.Value);
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
            var cosineDistance = reader.IsDBNull(8) ? double.MaxValue : reader.GetDouble(8);

            results.Add(new SearchResult(
                reader.GetInt32(0),
                reader.GetInt64(1),
                subject,
                fromName,
                fromAddress,
                reader.GetFieldValue<DateTimeOffset>(5),
                preview,
                folderFullName,
                cosineDistance,
                "embedding"));
        }

        return results;
    }

    private static List<SearchMessageResult> MergeResults(params IEnumerable<SearchResult>[] resultSets)
    {
        var combined = new Dictionary<int, SearchResult>();

        void AddRange(IEnumerable<SearchResult> source)
        {
            foreach (var result in source)
            {
                if (combined.TryGetValue(result.MessageId, out var existing))
                {
                    if (result.SourcePriority < existing.SourcePriority ||
                        (result.SourcePriority == existing.SourcePriority && result.Score < existing.Score))
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
            .OrderBy(r => r.SourcePriority)
            .ThenBy(r => r.Score)
            .ThenByDescending(r => r.ImapUid)
            .Take(MaxSearchResults)
            .Select(r => new SearchMessageResult(
                r.MessageId,
                r.ImapUid,
                r.FolderFullName,
                string.IsNullOrWhiteSpace(r.Subject) ? "(No subject)" : r.Subject,
                string.IsNullOrWhiteSpace(r.FromName) ? r.FromAddress : r.FromName,
                r.FromAddress,
                string.IsNullOrWhiteSpace(r.Preview) ? r.Subject : r.Preview,
                r.ReceivedUtc,
                r.Score,
                r.Source))
            .ToList();
    }

    private static (string Name, string Address) ExtractSenderTerms(string query)
    {
        var cleaned = TextCleaner.CleanNullable(query) ?? string.Empty;
        if (MimeKit.MailboxAddress.TryParse(cleaned, out var mailbox))
        {
            var name = TextCleaner.CleanNullable(mailbox?.Name) ?? cleaned;
            var address = TextCleaner.CleanNullable(mailbox?.Address) ?? cleaned;
            return (name, address);
        }

        return (cleaned, cleaned);
    }

    private static string BuildVectorLiteral(float[] vector) =>
        "[" + string.Join(",", vector.Select(v => v.ToString("G9", CultureInfo.InvariantCulture))) + "]";

    private static float[] NormalizeVector(Float16[] source)
    {
        if (source.Length != EmbeddingDim)
        {
            throw new InvalidOperationException($"Embedding dimension mismatch: expected {EmbeddingDim}, got {source.Length}.");
        }

        var result = new float[EmbeddingDim];
        for (int i = 0; i < EmbeddingDim; i++) result[i] = (float)source[i];
        return result;
    }

    private static async Task<QwenEmbedder?> EnsureSearchEmbedderAsync()
    {
        var embedder = await QwenEmbedder.GetSharedAsync();
        if (embedder == null)
        {
            System.Diagnostics.Debug.WriteLine("Failed to initialize shared MCP search embedder.");
        }

        return embedder;
    }

    private static string FormatMailbox(string? name, string? address)
    {
        var cleanedName = TextCleaner.CleanNullable(name) ?? string.Empty;
        var cleanedAddress = TextCleaner.CleanNullable(address) ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(cleanedName) && !string.IsNullOrWhiteSpace(cleanedAddress))
        {
            return $"{cleanedName} <{cleanedAddress}>";
        }

        return string.IsNullOrWhiteSpace(cleanedAddress) ? cleanedName : cleanedAddress;
    }

    private static string BuildFallbackHtml(MessageBody body)
    {
        if (!string.IsNullOrWhiteSpace(body.SanitizedHtml))
        {
            return body.SanitizedHtml;
        }

        if (!string.IsNullOrWhiteSpace(body.HtmlText))
        {
            return HtmlSanitizer.Sanitize(body.HtmlText).Html;
        }

        if (!string.IsNullOrWhiteSpace(body.PlainText))
        {
            return $"<html><body><pre>{System.Net.WebUtility.HtmlEncode(body.PlainText)}</pre></body></html>";
        }

        return "<html><body></body></html>";
    }

    public record AccountResult(int Id, string DisplayName, string Server, string Username, DateTimeOffset? LastSyncedAt);
    public record FolderResult(int Id, string FullName, string DisplayName, long? UidValidity, long? LastUid);
    public record LabelResult(int Id, string Name, int? ParentLabelId);
    public record SearchMessageResult(
        int MessageId,
        long ImapUid,
        string FolderFullName,
        string Subject,
        string FromName,
        string FromAddress,
        string Preview,
        DateTimeOffset ReceivedUtc,
        double Score,
        string Source);
    public record MessageBodyResult(string Html, MessageHeaderInfo Headers);
    public record MessageHeaderInfo(string From, string FromAddress, string To, string? Cc, string? Bcc);
    public record AttachmentResult(string FileName, string ContentType, long SizeBytes, string? Disposition, string? Base64Data);
    public record SchemaSnapshot(List<string> Tables);

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
        string Source)
    {
        public int SourcePriority => Source switch
        {
            "subject" => 0,
            "sender" => 1,
            "embedding" => 2,
            _ => 3
        };
    }
}
