using maildot.Services;
using maildot.ViewModels;

namespace maildot.Tests;

public class ImapSyncServiceTests
{
    [Fact]
    public void BuildMessageDedupKey_UsesMessageId_WhenPresent()
    {
        var key = ImapSyncService.BuildMessageDedupKey(" <abc@id> ", 123);
        Assert.Equal("mid:<abc@id>", key);
    }

    [Fact]
    public void BuildMessageDedupKey_FallsBackToUid_WhenMessageIdMissing()
    {
        var key = ImapSyncService.BuildMessageDedupKey("  ", -3);
        Assert.Equal("uid:-3", key);
    }

    [Fact]
    public void IsPreferredDedupUid_PrefersLocalOnlyNegativeUid()
    {
        Assert.True(ImapSyncService.IsPreferredDedupUid(-1));
        Assert.False(ImapSyncService.IsPreferredDedupUid(42));
    }

    [Fact]
    public void ComputeNextSyntheticUid_StartsAtMinusOne_WhenNoSyntheticUidsExist()
    {
        var next = ImapSyncService.ComputeNextSyntheticUid([55, 12, 1]);
        Assert.Equal(-1, next);
    }

    [Fact]
    public void ComputeNextSyntheticUid_DecrementsBelowLowestSyntheticUid()
    {
        var next = ImapSyncService.ComputeNextSyntheticUid([-1, -2, -7, 11]);
        Assert.Equal(-8, next);
    }

    [Fact]
    public void EnsureUniqueUidCandidate_UsesNextNegative_WhenDesiredUidCollides()
    {
        var next = ImapSyncService.EnsureUniqueUidCandidate(-1, new HashSet<long> { -1, -2, -3 });
        Assert.Equal(-4, next);
    }

    [Fact]
    public void EnsureUniqueUidCandidate_FallsBackToNegative_WhenPositiveUidCollides()
    {
        var next = ImapSyncService.EnsureUniqueUidCandidate(44, new HashSet<long> { 44, -1, -2 });
        Assert.Equal(-3, next);
    }

    [Fact]
    public void ApplySenderLabelToVisibleMessages_RemovesMatchingMessages_InUnlabeledFolderView()
    {
        var vm = new MailboxViewModel
        {
            UnlabeledOnly = true,
            SelectedFolder = new MailFolderViewModel("INBOX", "Inbox")
        };

        vm.Messages.Add(new EmailMessageViewModel { Id = "1", SenderAddress = "same@example.com", LabelNames = [] });
        vm.Messages.Add(new EmailMessageViewModel { Id = "2", SenderAddress = "other@example.com", LabelNames = [] });
        vm.Messages.Add(new EmailMessageViewModel { Id = "3", SenderAddress = "SAME@example.com", LabelNames = [] });

        ImapSyncService.ApplySenderLabelToVisibleMessages(vm, "same@example.com", 10, "Clients");

        Assert.Single(vm.Messages);
        Assert.Equal("2", vm.Messages[0].Id);
    }

    [Fact]
    public void ApplySenderLabelToVisibleMessages_AppendsLabel_AndAvoidsDuplicates_InRegularView()
    {
        var vm = new MailboxViewModel();
        var message = new EmailMessageViewModel
        {
            Id = "1",
            SenderAddress = "sender@example.com",
            LabelNames = ["Existing"]
        };
        vm.Messages.Add(message);

        ImapSyncService.ApplySenderLabelToVisibleMessages(vm, "sender@example.com", 10, "Clients");
        ImapSyncService.ApplySenderLabelToVisibleMessages(vm, "sender@example.com", 10, "clients");

        Assert.Equal(["Existing", "Clients"], message.LabelNames);
    }

    [Fact]
    public void ApplySenderLabelToVisibleMessages_DoesNotRemove_WhenSearchIsActive()
    {
        var vm = new MailboxViewModel();
        vm.EnterSearchMode("hello");
        vm.UnlabeledOnly = true;

        var message = new EmailMessageViewModel
        {
            Id = "1",
            SenderAddress = "same@example.com",
            LabelNames = []
        };
        vm.Messages.Add(message);

        ImapSyncService.ApplySenderLabelToVisibleMessages(vm, "same@example.com", 10, "Clients");

        Assert.Single(vm.Messages);
        Assert.Equal(["Clients"], message.LabelNames);
    }

    [Fact]
    public void ApplySenderLabelToVisibleMessages_RemovesSuggestedMessage_InDifferentLabelView()
    {
        var vm = new MailboxViewModel();
        vm.SelectLabel(1);
        vm.Messages.Add(new EmailMessageViewModel
        {
            Id = "1",
            SenderAddress = "same@example.com",
            IsSuggested = true,
            LabelNames = []
        });

        ImapSyncService.ApplySenderLabelToVisibleMessages(vm, "same@example.com", 2, "Different");

        Assert.Empty(vm.Messages);
    }

    [Fact]
    public void ApplySenderLabelToVisibleMessages_ConvertsSuggestionToExplicit_InSameLabelView()
    {
        var vm = new MailboxViewModel();
        vm.SelectLabel(7);
        var message = new EmailMessageViewModel
        {
            Id = "1",
            SenderAddress = "same@example.com",
            IsSuggested = true,
            SuggestionScore = 0.42d,
            LabelNames = []
        };
        vm.Messages.Add(message);

        ImapSyncService.ApplySenderLabelToVisibleMessages(vm, "same@example.com", 7, "Current");

        Assert.Single(vm.Messages);
        Assert.False(message.IsSuggested);
        Assert.Equal(Double.NegativeInfinity, message.SuggestionScore);
        Assert.Equal(["Current"], message.LabelNames);
    }
}
