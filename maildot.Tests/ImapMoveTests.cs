using System.Reflection;
using MailKit;
using maildot.Services;

namespace maildot.Tests;

public class ImapMoveTests
{
    [Fact]
    public async Task UsesMoveResponseWithoutSearchingDuplicateMessageIds()
    {
        var (source, sourceState) = Folder();
        var (target, targetState) = Folder();
        sourceState.AssignedUid = new UniqueId(100, 50);
        targetState.Validity = 100;
        var synchronized = false;

        var result = await ImapSyncService.MoveWithMappingAsync(source, target, new UniqueId(7),
            (_, _) => { synchronized = true; return Task.CompletedTask; }, TestContext.Current.CancellationToken);

        Assert.Equal((true, (long?)50, (long?)100), result);
        Assert.True(synchronized);
        Assert.Equal(0, targetState.SearchCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrGenerationlessMappingRemainsUnresolved(bool hasUid)
    {
        var (source, sourceState) = Folder();
        var (target, targetState) = Folder();
        sourceState.AssignedUid = hasUid ? new UniqueId(50) : null;

        var result = await ImapSyncService.MoveWithMappingAsync(source, target, new UniqueId(7),
            (_, _) => throw new InvalidOperationException("No mapping to validate"), TestContext.Current.CancellationToken);

        Assert.Equal((true, (long?)null, (long?)null), result);
        Assert.Equal(0, targetState.SearchCalls);
    }

    [Fact]
    public async Task DestinationGenerationChangeDoesNotAttachOldUidToNewMessage()
    {
        var (source, sourceState) = Folder();
        var (target, targetState) = Folder();
        sourceState.AssignedUid = new UniqueId(100, 50);
        targetState.Validity = 200;

        var result = await ImapSyncService.MoveWithMappingAsync(source, target, new UniqueId(7),
            (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);

        Assert.Equal((true, (long?)null, (long?)null), result);
        Assert.Equal(0, targetState.SearchCalls);
    }

    [Fact]
    public async Task FollowUpFailureDoesNotTurnAcknowledgedMoveIntoFailure()
    {
        var (source, sourceState) = Folder();
        var (target, targetState) = Folder();
        sourceState.AssignedUid = new UniqueId(100, 50);
        targetState.OpenError = new IOException("Connection lost after MOVE");

        var result = await ImapSyncService.MoveWithMappingAsync(source, target, new UniqueId(7),
            (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);

        Assert.Equal((true, (long?)null, (long?)null), result);
    }

    [Fact]
    public async Task FailedMoveIsNotReportedAsAcknowledged()
    {
        var (source, sourceState) = Folder();
        var (target, _) = Folder();
        sourceState.MoveError = new IOException("MOVE failed");
        await Assert.ThrowsAsync<IOException>(() => ImapSyncService.MoveWithMappingAsync(source, target, new UniqueId(7),
            (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken));
    }

    private static (IMailFolder Folder, FolderProxy State) Folder()
    {
        var folder = DispatchProxy.Create<IMailFolder, FolderProxy>();
        return (folder, (FolderProxy)(object)folder);
    }

    public class FolderProxy : DispatchProxy
    {
        public UniqueId? AssignedUid { get; set; }
        public uint Validity { get; set; }
        public Exception? OpenError { get; set; }
        public Exception? MoveError { get; set; }
        public int SearchCalls { get; private set; }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name)
            {
                case "get_IsOpen": return false;
                case "get_UidValidity": return Validity;
                case "OpenAsync": return OpenError == null ? Task.FromResult(FolderAccess.ReadOnly) : Task.FromException<FolderAccess>(OpenError);
                case "MoveToAsync": return MoveError == null ? Task.FromResult(AssignedUid) : Task.FromException<UniqueId?>(MoveError);
                case "SearchAsync":
                    SearchCalls++;
                    // A duplicate/substring Message-ID match must never override MOVE's UID 50.
                    return Task.FromResult<IList<UniqueId>>(new List<UniqueId> { new(100, 50), new(100, 51) });
                default: throw new NotSupportedException(method.Name);
            }
        }
    }
}
