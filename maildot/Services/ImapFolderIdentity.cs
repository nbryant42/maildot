using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using maildot.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace maildot.Services;

/// <summary>Serializes generation changes with writers of UID-addressed server data.</summary>
public static class ImapFolderIdentity
{
    public static async Task SynchronizeAsync(MailDbContext db, int folderId, uint validity, CancellationToken token)
    {
        if (validity == 0) throw new InvalidOperationException("The selected mailbox has no UIDVALIDITY.");
        await using var tx = await db.Database.BeginTransactionAsync(token);
        var folder = await LockAsync(db, folderId, token);
        if (folder.UidValidity != validity)
        {
            // Keep primary keys and all dependent archive data. Allocate below every existing
            // synthetic UID, including messages previously moved here while offline.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                WITH archived AS (
                    SELECT "Id", LEAST(0, (SELECT MIN("ImapUid") FROM imap_messages WHERE "FolderId" = {folderId}))
                        - ROW_NUMBER() OVER (ORDER BY "Id") AS uid
                    FROM imap_messages WHERE "FolderId" = {folderId} AND "ImapUid" > 0
                )
                UPDATE imap_messages m SET "ImapUid" = a.uid,
                    "Hash" = m."MessageId" || ':' || a.uid::text
                FROM archived a WHERE m."Id" = a."Id"
                """, token);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE imap_folders SET uid_validity = {(long)validity}, last_uid = NULL, "SyncToken" = NULL
                WHERE id = {folderId}
                """, token);
        }
        await tx.CommitAsync(token);
    }

    public static async Task<IDbContextTransaction> BeginWriteAsync(
        MailDbContext db, int folderId, long? expectedValidity, CancellationToken token)
    {
        var tx = await db.Database.BeginTransactionAsync(token);
        try
        {
            var folder = await LockAsync(db, folderId, token);
            if (!Matches(expectedValidity, folder.UidValidity))
                throw new MailboxGenerationChangedException();
            return tx;
        }
        catch
        {
            await tx.DisposeAsync();
            throw;
        }
    }

    internal static bool Matches(long? expected, long? actual) =>
        expected is > 0 && expected == actual;

    public static async Task<IDbContextTransaction> BeginMoveAsync(MailDbContext db, int sourceId,
        int targetId, long? targetValidity, CancellationToken token)
    {
        var tx = await db.Database.BeginTransactionAsync(token);
        try
        {
            foreach (var id in new[] { sourceId, targetId }.Distinct().OrderBy(id => id))
            {
                var folder = await LockAsync(db, id, token);
                if (id == targetId && targetValidity.HasValue && !Matches(targetValidity, folder.UidValidity))
                    throw new MailboxGenerationChangedException();
            }
            return tx;
        }
        catch
        {
            await tx.DisposeAsync();
            throw;
        }
    }

    private static async Task<Models.ImapFolder> LockAsync(MailDbContext db, int folderId, CancellationToken token) =>
        (await db.ImapFolders.FromSqlInterpolated($"SELECT * FROM imap_folders WHERE id = {folderId} FOR UPDATE")
            .AsNoTracking().ToListAsync(token)).Single();
}

public sealed class MailboxGenerationChangedException : InvalidOperationException
{
    public MailboxGenerationChangedException() : base("Mailbox UIDVALIDITY changed; discard stale server data and resync.") { }
}
