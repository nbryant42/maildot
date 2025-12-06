using System.Buffers;
using System.Security.Cryptography;
using maildot.Data;
using maildot.Models;
using maildot.Services;
using MailKit;
using MailKit.Net.Imap;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using Npgsql;

namespace ImapBackfill;

internal sealed record BackfillOptions(bool ProcessBodies, bool ProcessAttachments, long? TargetUid);

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

        if (!processBodies && !processAttachments)
        {
            processBodies = true;
            processAttachments = true;
        }

        return new BackfillOptions(processBodies, processAttachments, targetUid);
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
        var messages = await db.ImapMessages
            .AsNoTracking()
            .Include(m => m.Body)
            .Include(m => m.Attachments)
            .Where(m => m.FolderId == folderEntity.Id)
            .ToListAsync();

        var targets = messages
            .Where(m => NeedsRefresh(m, options))
            .Select(m => new { m.ImapUid, m.Id })
            .Where(m => !options.TargetUid.HasValue || m.ImapUid == options.TargetUid.Value)
            .OrderByDescending(m => m.ImapUid)
            .ToList();

        if (targets.Count == 0)
        {
            Console.WriteLine($"  Folder {folderEntity.FullName}: nothing to update.");
            return;
        }

        Console.WriteLine($"  Folder {folderEntity.FullName}: re-downloading {targets.Count} message(s).");

        try
        {
            await folder.OpenAsync(FolderAccess.ReadOnly);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Unable to open folder {folderEntity.FullName}: {ex.Message}");
            return;
        }

        foreach (var target in targets)
        {
            try
            {
                var mime = await folder.GetMessageAsync(new UniqueId((uint)target.ImapUid));
                await PersistMessageAsync(db, server, folderEntity.Id, mime, options, target.ImapUid, CancellationToken.None);
                Console.WriteLine($"    Refreshed UID {target.ImapUid}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    UID {target.ImapUid} failed: {ex.Message}");
            }
        }

        try
        {
            await folder.CloseAsync(false);
        }
        catch
        {
        }
    }

    private static bool NeedsRefresh(ImapMessage message, BackfillOptions options)
    {
        var needsBody = options.ProcessBodies && BodyNeedsRefresh(message.Body);
        var needsAttachments = options.ProcessAttachments && AttachmentsNeedRefresh(message.Attachments);
        return needsBody || needsAttachments;
    }

    private static bool BodyNeedsRefresh(MessageBody? body) =>
        body == null ||
        (string.IsNullOrWhiteSpace(body.PlainText) && string.IsNullOrWhiteSpace(body.HtmlText));

    private static bool AttachmentsNeedRefresh(ICollection<MessageAttachment>? attachments) =>
        attachments == null ||
        attachments.Count == 0 ||
        attachments.Any(a => a.LargeObjectId == 0 || a.SizeBytes == 0);

    private static async Task PersistMessageAsync(
        MailDbContext db,
        string? server,
        int folderId,
        MimeMessage mime,
        BackfillOptions options,
        long uid,
        CancellationToken token)
    {
        var entity = await UpsertImapMessageAsync(db, server, folderId, mime, uid, token);

        var existingBody = await db.MessageBodies
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.MessageId == entity.Id, token);
        var existingAttachments = await db.MessageAttachments
            .AsNoTracking()
            .Where(a => a.MessageId == entity.Id)
            .ToListAsync(token);

        var refreshBody = options.ProcessBodies && BodyNeedsRefresh(existingBody);
        var refreshAttachments = options.ProcessAttachments && AttachmentsNeedRefresh(existingAttachments);

        if (!refreshBody && !refreshAttachments)
        {
            return;
        }

        await db.Database.OpenConnectionAsync(token);
        await using var tx = await db.Database.BeginTransactionAsync(token);

        if (refreshBody)
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

        if (refreshAttachments)
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
        MimeMessage message,
        long uid,
        CancellationToken token)
    {
        var existing = await db.ImapMessages.FirstOrDefaultAsync(m => m.FolderId == folderId && m.ImapUid == uid, token);

        if (existing != null)
        {
            UpdateImapMessage(existing, message);
            await db.SaveChangesAsync(token);
            return existing;
        }

        var entity = CreateImapMessage(server, folderId, message, uid);
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
            UpdateImapMessage(reloaded, message);
            await db.SaveChangesAsync(token);
            return reloaded;
        }
    }

    private static ImapMessage CreateImapMessage(string? server, int folderId, MimeMessage message, long uid)
    {
        var sender = message.From.OfType<MailboxAddress>().FirstOrDefault();
        var senderName = TextCleaner.CleanNullable(sender?.Name) ?? string.Empty;
        var senderAddress = TextCleaner.CleanNullable(sender?.Address) ?? string.Empty;
        var rawMessageId = string.IsNullOrWhiteSpace(message.MessageId)
            ? $"uid:{uid}@{server}"
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
