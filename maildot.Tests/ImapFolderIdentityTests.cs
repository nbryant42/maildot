using maildot.Data;
using maildot.Models;
using maildot.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace maildot.Tests;

public class ImapFolderIdentityTests
{
    [Theory]
    [InlineData(null, 1, false)]
    [InlineData(0L, 0L, false)]
    [InlineData(12L, 13L, false)]
    [InlineData(12L, 12L, true)]
    public void OnlyKnownMatchingGenerationsPermitUidOperations(long? expected, long actual, bool allowed)
        => Assert.Equal(allowed, ImapFolderIdentity.Matches(expected, actual));

    [Theory]
    [InlineData(null)]
    [InlineData(100L)]
    public async Task ChangedOrUnknownGenerationPreservesArchiveAndAllowsUidReuse(long? oldValidity)
    {
        await using var db = await OpenTestDatabaseAsync();
        var folder = await SeedAsync(db, oldValidity);
        var original = await db.ImapMessages.SingleAsync(m => m.FolderId == folder.Id && m.ImapUid == 42, TestContext.Current.CancellationToken);
        var id = original.Id;
        await ImapFolderIdentity.SynchronizeAsync(db, folder.Id, 200, TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var archived = await db.ImapMessages.Include(m => m.Body).Include(m => m.Attachments)
            .Include(m => m.LabelLinks).SingleAsync(m => m.Id == id, TestContext.Current.CancellationToken);
        Assert.True(archived.ImapUid < -10);
        Assert.Equal("original body", archived.Body.PlainText);
        Assert.Single(archived.Attachments);
        Assert.Single(archived.LabelLinks);
        Assert.Equal(200, (await db.ImapFolders.FindAsync([folder.Id], TestContext.Current.CancellationToken))!.UidValidity);

        await using (var tx = await ImapFolderIdentity.BeginWriteAsync(db, folder.Id, 200, TestContext.Current.CancellationToken))
        {
            db.ImapMessages.Add(new ImapMessage { FolderId = folder.Id, ImapUid = 42, MessageId = "replacement", Subject = "new message", ReceivedUtc = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            await tx.CommitAsync(TestContext.Current.CancellationToken);
        }
        Assert.Equal(3, await db.ImapMessages.CountAsync(m => m.FolderId == folder.Id, TestContext.Current.CancellationToken));
        Assert.Equal("original body", (await db.MessageBodies.FindAsync([id], TestContext.Current.CancellationToken))!.PlainText);
        await Assert.ThrowsAsync<MailboxGenerationChangedException>(async () =>
        {
            await using var stale = await ImapFolderIdentity.BeginWriteAsync(db, folder.Id, oldValidity, TestContext.Current.CancellationToken);
        });
    }

    [Fact]
    public async Task SameGenerationDoesNotInvalidateMessages()
    {
        await using var db = await OpenTestDatabaseAsync();
        var folder = await SeedAsync(db, 100);
        await ImapFolderIdentity.SynchronizeAsync(db, folder.Id, 100, TestContext.Current.CancellationToken);
        Assert.True(await db.ImapMessages.AnyAsync(m => m.FolderId == folder.Id && m.ImapUid == 42, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GenerationChangeWaitsForInFlightWriterAndArchivesItsResult()
    {
        await using var writer = await OpenTestDatabaseAsync();
        var folder = await SeedAsync(writer, 100);
        await using var other = await OpenTestDatabaseAsync();
        await using var tx = await ImapFolderIdentity.BeginWriteAsync(writer, folder.Id, 100, TestContext.Current.CancellationToken);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var change = ImapFolderIdentity.SynchronizeAsync(other, folder.Id, 200, timeout.Token);
        // A folder lock must prevent a generation change from overtaking the body/header write.
        Assert.NotSame(change, await Task.WhenAny(change, Task.Delay(150, timeout.Token)));
        writer.ImapMessages.Add(new ImapMessage { FolderId = folder.Id, ImapUid = 43, MessageId = "in-flight", ReceivedUtc = DateTimeOffset.UtcNow });
        await writer.SaveChangesAsync(timeout.Token);
        await tx.CommitAsync(timeout.Token);
        await change;
        Assert.False(await other.ImapMessages.AnyAsync(m => m.FolderId == folder.Id && m.ImapUid > 0, TestContext.Current.CancellationToken));
        Assert.Equal(3, await other.ImapMessages.CountAsync(m => m.FolderId == folder.Id, TestContext.Current.CancellationToken));
    }

    private static async Task<MailDbContext> OpenTestDatabaseAsync()
    {
        var connection = Environment.GetEnvironmentVariable("MAILDOT_TEST_PG_CONNECTION");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(connection), "Set MAILDOT_TEST_PG_CONNECTION to a disposable maildot_test_* PostgreSQL database.");
        var cs = new NpgsqlConnectionStringBuilder(connection);
        Assert.StartsWith("maildot_test_", cs.Database);
        var options = new DbContextOptionsBuilder<MailDbContext>().UseNpgsql(connection, n => n.UseVector()).Options;
        var db = new MailDbContext(options);
        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        return db;
    }

    private static async Task<ImapFolder> SeedAsync(MailDbContext db, long? validity)
    {
        var account = new ImapAccount { DisplayName = "Identity test" };
        var folder = new ImapFolder { Account = account, FullName = "INBOX", UidValidity = validity };
        var original = new ImapMessage { Folder = folder, ImapUid = 42, MessageId = "original", ReceivedUtc = DateTimeOffset.UtcNow };
        original.Body = new MessageBody { Message = original, PlainText = "original body" };
        original.Attachments.Add(new MessageAttachment { FileName = "archive.txt", ContentType = "text/plain" });
        original.LabelLinks.Add(new MessageLabel { Label = new Label { Account = account, Name = "Keep" } });
        db.ImapMessages.Add(original);
        db.ImapMessages.Add(new ImapMessage { Folder = folder, ImapUid = -10, MessageId = "local", ReceivedUtc = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return folder;
    }
}
