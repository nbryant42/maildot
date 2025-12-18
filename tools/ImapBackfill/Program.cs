using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using maildot.Data;
using maildot.Models;
using maildot.Services;
using MailKit;
using MailKit.Net.Imap;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using MimeKit.Utils;
using Npgsql;

namespace ImapBackfill;

internal sealed record BackfillOptions(bool ProcessBodies, bool ProcessAttachments, bool EnvelopeOnly, long? TargetUid);
internal sealed record MessageEnvelope(string Subject, string FromName, string FromAddress, string MessageId, DateTimeOffset ReceivedUtc);

internal static class Program
{
    public static async Task Main(string[] args)
    {
        var options = ParseOptions(args);

        var pgSettings = PostgresSettingsStore.Load();
        var pgPassword = await CredentialManager.RequestPostgresPasswordAsync(pgSettings);
        if (pgPassword.Result != CredentialAccessResult.Success || string.IsNullOrWhiteSpace(pgPassword.Password))
        {
            Console.WriteLine("PostgreSQL credentials are missing. Please configure them in the Mail.Net app first.");
            return;
        }

        await using var db = MailDbContextFactory.CreateDbContext(pgSettings, pgPassword.Password);

        var accounts = await db.ImapAccounts
            .AsNoTracking()
            .Select(a => new AccountSettings
            {
                Id = a.Id,
                AccountName = a.DisplayName,
                Server = a.Server,
                Port = a.Port,
                UseSsl = a.UseSsl,
                Username = a.Username
            })
            .ToListAsync();

        if (accounts.Count == 0)
        {
            Console.WriteLine("No IMAP accounts found.");
            return;
        }

        foreach (var account in accounts)
        {
            var passwordResponse = await CredentialManager.RequestPasswordAsync(account);
            if (passwordResponse.Result != CredentialAccessResult.Success || string.IsNullOrWhiteSpace(passwordResponse.Password))
            {
                Console.WriteLine($"Skipping account {account.AccountName} ({account.Username}): missing IMAP password.");
                continue;
            }

            await ProcessAccountAsync(db, account, passwordResponse.Password, options);
        }
    }

    private static BackfillOptions ParseOptions(string[] args)
    {
        var processBodies = args.Any(a => string.Equals(a, "--bodies", StringComparison.OrdinalIgnoreCase));
        var processAttachments = args.Any(a => string.Equals(a, "--attachments", StringComparison.OrdinalIgnoreCase));
        var envelopeOnly = args.Any(a => string.Equals(a, "--envelope", StringComparison.OrdinalIgnoreCase));
        long? targetUid = null;

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, "--id", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "--uid", StringComparison.OrdinalIgnoreCase))
            {
                var next = i + 1 < args.Length ? args[i + 1] : null;
                if (long.TryParse(next, out var parsed) && parsed > 0)
                {
                    targetUid = parsed;
                }
            }
        }

        if (envelopeOnly)
        {
            processBodies = false;
            processAttachments = false;
        }
        else if (!processBodies && !processAttachments)
        {
            processBodies = true;
            processAttachments = true;
        }

        return new BackfillOptions(processBodies, processAttachments, envelopeOnly, targetUid);
    }

    private static async Task ProcessAccountAsync(MailDbContext db, AccountSettings account, string password, BackfillOptions options)
    {
        using var client = new ImapClient();

        try
        {
            await client.ConnectAsync(account.Server, account.Port, account.UseSsl);
            await client.AuthenticateAsync(account.Username, password);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to connect to {account.AccountName}: {ex.Message}");
            return;
        }

        var folders = await db.ImapFolders
            .AsNoTracking()
            .Where(f => f.AccountId == account.Id)
            .ToListAsync();

        foreach (var folderEntity in folders)
        {
            IMailFolder? folder = null;
            try
            {
                folder = await client.GetFolderAsync(folderEntity.FullName);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Skipping folder {folderEntity.FullName}: {ex.Message}");
                continue;
            }

            await ProcessFolderAsync(db, account.Server, folderEntity, folder, options);
        }

        try
        {
            await client.DisconnectAsync(true);
        }
        catch
        {
        }
    }

    private static async Task ProcessFolderAsync(
        MailDbContext db,
        string? server,
        maildot.Models.ImapFolder folderEntity,
        IMailFolder folder,
        BackfillOptions options)
    {
        var existingMessages = await LoadExistingMessagesAsync(db, folderEntity.Id, options);
        if (existingMessages.Count == 0)
        {
            Console.WriteLine($"  Folder {folderEntity.FullName}: no tracked messages to update.");
            return;
        }

        try
        {
            await folder.OpenAsync(FolderAccess.ReadOnly);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Unable to open folder {folderEntity.FullName}: {ex.Message}");
            return;
        }

        var summaryItems = MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.InternalDate;

        try
        {
            IList<IMessageSummary> summaries;
            try
            {
                summaries = options.TargetUid.HasValue
                    ? await folder.FetchAsync([new UniqueId((uint)options.TargetUid.Value)], summaryItems)
                    : await folder.FetchAsync(0, -1, summaryItems);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Unable to enumerate folder {folderEntity.FullName}: {ex.Message}");
                return;
            }

            var targets = summaries
                .Select(s => new { Summary = s, Uid = (long)s.UniqueId!.Id })
                .Where(s => existingMessages.ContainsKey(s.Uid))
                .OrderByDescending(s => s.Uid)
                .ToList();

            if (targets.Count == 0)
            {
                Console.WriteLine($"  Folder {folderEntity.FullName}: nothing to update.");
                return;
            }

            var modeDescription = options.EnvelopeOnly
                ? "envelopes"
                : string.Join(" & ", new[]
                {
                    options.ProcessBodies ? "bodies" : null,
                    options.ProcessAttachments ? "attachments" : null
                }.Where(x => x != null));

            if (string.IsNullOrWhiteSpace(modeDescription))
            {
                modeDescription = "envelopes";
            }

            Console.WriteLine($"  Folder {folderEntity.FullName}: " +
                $"refreshing {targets.Count} message(s) ({modeDescription}).");

            foreach (var target in targets)
            {
                var uid = target.Uid;
                var summary = target.Summary;
                var summaryEnvelope = BuildEnvelope(summary, server, uid);
                var existing = existingMessages[uid];

                if (!options.ProcessBodies && !options.ProcessAttachments)
                {
                    var envelopeChanged =
                        existing.Subject != summaryEnvelope.Subject ||
                        existing.FromName != summaryEnvelope.FromName ||
                        existing.FromAddress != summaryEnvelope.FromAddress ||
                        existing.ReceivedUtc != summaryEnvelope.ReceivedUtc ||
                        options.EnvelopeOnly;

                    if (envelopeChanged)
                    {
                        try
                        {
                            await UpsertImapMessageAsync(db, server, folderEntity.Id, summaryEnvelope, uid,
                                CancellationToken.None);
                            Console.WriteLine($"    Updated envelope UID {uid}");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"    UID {uid} failed: {ex.Message}");
                        }
                    }

                    continue;
                }

                try
                {
                    var mime = await folder.GetMessageAsync(summary.UniqueId);
                    var mimeEnvelope = BuildEnvelope(mime, server, uid, summary.InternalDate?.ToUniversalTime(),
                        existing.ReceivedUtc);
                    await PersistMessageAsync(db, server, folderEntity.Id, mime, mimeEnvelope, options, uid,
                        CancellationToken.None);
                    Console.WriteLine($"    Refreshed UID {uid}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"    UID {uid} failed: {ex.Message}");
                }
            }
        }
        finally
        {
            try
            {
                await folder.CloseAsync(false);
            }
            catch
            {
            }
        }
    }

    private static async Task<Dictionary<long, ImapMessage>> LoadExistingMessagesAsync(
        MailDbContext db,
        int folderId,
        BackfillOptions options)
    {
        IQueryable<ImapMessage> query = db.ImapMessages
            .AsNoTracking()
            .Where(m => m.FolderId == folderId);

        if (options.ProcessBodies)
        {
            query = query.Include(m => m.Body);
        }

        if (options.ProcessAttachments)
        {
            query = query.Include(m => m.Attachments);
        }

        var messages = await query.ToListAsync();
        return messages.ToDictionary(m => m.ImapUid);
    }

    private static MessageEnvelope BuildEnvelope(IMessageSummary summary, string? server, long uid)
    {
        var sender = summary.Envelope?.From?.Mailboxes?.FirstOrDefault();
        var subject = TextCleaner.CleanNullable(summary.Envelope?.Subject) ?? string.Empty;
        var fromName = TextCleaner.CleanNullable(sender?.Name) ?? string.Empty;
        var fromAddress = TextCleaner.CleanNullable(sender?.Address) ?? string.Empty;

        var rawMessageId = string.IsNullOrWhiteSpace(summary.Envelope?.MessageId)
            ? $"uid:{uid}@{server}"
            : summary.Envelope!.MessageId;

        var messageId = TextCleaner.CleanNullable(rawMessageId) ?? $"uid:{uid}@{server}";
        var received = summary.InternalDate?.ToUniversalTime() ?? DateTimeOffset.UtcNow;

        return new MessageEnvelope(subject, fromName, fromAddress, messageId, received);
    }

    private static MessageEnvelope BuildEnvelope(
        MimeMessage message,
        string? server,
        long uid,
        DateTimeOffset? internalDateFallback,
        DateTimeOffset? existingReceived)
    {
        var sender = message.From.OfType<MailboxAddress>().FirstOrDefault();
        var subject = TextCleaner.CleanNullable(message.Subject) ?? string.Empty;
        var fromName = TextCleaner.CleanNullable(sender?.Name) ?? string.Empty;
        var fromAddress = TextCleaner.CleanNullable(sender?.Address) ?? string.Empty;
        var rawMessageId = string.IsNullOrWhiteSpace(message.MessageId)
            ? $"uid:{uid}@{server}"
            : message.MessageId;

        var messageId = TextCleaner.CleanNullable(rawMessageId) ?? $"uid:{uid}@{server}";
        var received = ResolveReceivedUtc(message, internalDateFallback, existingReceived);

        return new MessageEnvelope(subject, fromName, fromAddress, messageId, received);
    }

    private static async Task PersistMessageAsync(
        MailDbContext db,
        string? server,
        int folderId,
        MimeMessage mime,
        MessageEnvelope envelope,
        BackfillOptions options,
        long uid,
        CancellationToken token)
    {
        var entity = await UpsertImapMessageAsync(db, server, folderId, envelope, uid, token);

        var existingBody = await db.MessageBodies
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.MessageId == entity.Id, token);
        var existingAttachments = await db.MessageAttachments
            .AsNoTracking()
            .Where(a => a.MessageId == entity.Id)
            .ToListAsync(token);

        if (!options.ProcessBodies && !options.ProcessAttachments)
        {
            return;
        }

        await db.Database.OpenConnectionAsync(token);
        await using var tx = await db.Database.BeginTransactionAsync(token);

        if (options.ProcessBodies)
        {
            var rebuilt = BuildMessageBody(mime, entity.Id);
            var trackedBody = await db.MessageBodies.FirstOrDefaultAsync(b => b.MessageId == entity.Id, token);
            if (trackedBody == null)
            {
                db.MessageBodies.Add(rebuilt);
            }
            else
            {
                trackedBody.PlainText = rebuilt.PlainText;
                trackedBody.HtmlText = rebuilt.HtmlText;
                trackedBody.SanitizedHtml = rebuilt.SanitizedHtml;
                trackedBody.Headers = rebuilt.Headers;
                trackedBody.Preview = rebuilt.Preview;
            }
        }

        if (options.ProcessAttachments)
        {
            var conn = (NpgsqlConnection)db.Database.GetDbConnection();
            var manager = new NpgsqlLargeObjectManager(conn);

            var trackedAttachments = await db.MessageAttachments.Where(a => a.MessageId == entity.Id).ToListAsync(token);
            if (trackedAttachments.Count > 0)
            {
                foreach (var attachment in trackedAttachments)
                {
                    if (attachment.LargeObjectId != 0)
                    {
                        try { await manager.UnlinkAsync(attachment.LargeObjectId, token); }
                        catch { }
                    }
                }

                db.MessageAttachments.RemoveRange(trackedAttachments);
            }

            var attachments = await DownloadAttachmentsAsync(manager, mime, entity.Id, token);
            if (attachments.Count > 0)
            {
                db.MessageAttachments.AddRange(attachments);
            }
        }

        await db.SaveChangesAsync(token);
        await tx.CommitAsync(token);
        db.ChangeTracker.Clear();
    }

    private static async Task<ImapMessage> UpsertImapMessageAsync(
        MailDbContext db,
        string? server,
        int folderId,
        MessageEnvelope envelope,
        long uid,
        CancellationToken token)
    {
        var existing = await db.ImapMessages.FirstOrDefaultAsync(m => m.FolderId == folderId && m.ImapUid == uid, token);

        if (existing != null)
        {
            UpdateImapMessage(existing, envelope);
            await db.SaveChangesAsync(token);
            return existing;
        }

        var entity = CreateImapMessage(server, folderId, envelope, uid);
        db.ImapMessages.Add(entity);

        try
        {
            await db.SaveChangesAsync(token);
            return entity;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            db.Entry(entity).State = EntityState.Detached;
            var reloaded = await db.ImapMessages.FirstAsync(m => m.FolderId == folderId && m.ImapUid == uid, token);
            UpdateImapMessage(reloaded, envelope);
            await db.SaveChangesAsync(token);
            return reloaded;
        }
    }

    private static ImapMessage CreateImapMessage(string? server, int folderId, MessageEnvelope envelope, long uid)
    {
        var messageId = string.IsNullOrWhiteSpace(envelope.MessageId)
            ? TextCleaner.CleanNullable($"uid:{uid}@{server}") ?? string.Empty
            : envelope.MessageId;

        return new ImapMessage
        {
            FolderId = folderId,
            ImapUid = uid,
            MessageId = messageId,
            Subject = envelope.Subject,
            FromName = envelope.FromName,
            FromAddress = envelope.FromAddress,
            ReceivedUtc = envelope.ReceivedUtc,
            Hash = $"{messageId}:{uid}"
        };
    }

    private static void UpdateImapMessage(ImapMessage entity, MessageEnvelope envelope)
    {
        entity.Subject = envelope.Subject;
        entity.FromName = envelope.FromName;
        entity.FromAddress = envelope.FromAddress;
        entity.ReceivedUtc = envelope.ReceivedUtc;
    }

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

    private static MessageBody BuildMessageBody(MimeMessage message, int messageId)
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

        return new MessageBody
        {
            MessageId = messageId,
            PlainText = plain,
            HtmlText = html,
            SanitizedHtml = sanitized,
            Headers = headers,
            Preview = preview
        };
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

    private static async Task<List<MessageAttachment>> DownloadAttachmentsAsync(
        NpgsqlLargeObjectManager manager,
        MimeMessage message,
        int messageId,
        CancellationToken token)
    {
        var parts = message.BodyParts
            .Where(IsAttachmentCandidate)
            .ToList();

        if (parts.Count == 0)
        {
            return [];
        }

        var results = new List<MessageAttachment>(parts.Count);
        foreach (var part in parts)
        {
            var saved = await SaveAttachmentAsync(manager, part, messageId, token);
            if (saved != null)
            {
                results.Add(saved);
            }
        }

        return results;
    }

    private static async Task<MessageAttachment?> SaveAttachmentAsync(
        NpgsqlLargeObjectManager manager,
        MimeEntity entity,
        int messageId,
        CancellationToken token)
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

            return new MessageAttachment
            {
                MessageId = messageId,
                FileName = fileName,
                ContentType = contentType,
                Disposition = disposition,
                SizeBytes = totalBytes,
                Hash = hash,
                LargeObjectId = oid
            };
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

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
