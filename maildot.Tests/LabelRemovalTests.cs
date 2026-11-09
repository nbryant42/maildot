using maildot.Services;
using maildot.ViewModels;

namespace maildot.Tests;

public class LabelRemovalTests
{
    [Fact]
    public void Refresh_PreservesLabelThatStillHasAnotherAssignment()
    {
        var vm = new MailboxViewModel();
        vm.SelectLabel(10);
        var message = new EmailMessageViewModel { Id = "1", LabelNames = ["Work", "Old"] };
        vm.Messages.Add(message);

        var removed = ImapSyncService.ApplyVisibleLabelAssignments(vm, [message],
            new Dictionary<int, List<int>> { [1] = [10] },
            new Dictionary<int, string> { [10] = "Work" });

        Assert.Equal(0, removed);
        Assert.Same(message, Assert.Single(vm.Messages));
        Assert.Equal("Work", Assert.Single(message.LabelNames));
    }

    [Fact]
    public void Refresh_UsesLabelIdentity_WhenDifferentLabelsHaveTheSameName()
    {
        var vm = new MailboxViewModel();
        vm.SelectLabel(10);
        var message = new EmailMessageViewModel { Id = "1", LabelNames = ["Work"] };
        vm.Messages.Add(message);

        var removed = ImapSyncService.ApplyVisibleLabelAssignments(vm, [message],
            new Dictionary<int, List<int>> { [1] = [20] },
            new Dictionary<int, string> { [20] = "Work" });

        Assert.Equal(1, removed);
        Assert.Empty(vm.Messages);
        Assert.Equal("Work", Assert.Single(message.LabelNames));
    }

    [Fact]
    public void Refresh_KeepsUnlabeledMessagesInFolderAndKeepsSuggestionsInLabelView()
    {
        var vm = new MailboxViewModel { SelectedFolder = new MailFolderViewModel("INBOX", "Inbox") };
        var message = new EmailMessageViewModel { Id = "1", LabelNames = ["Work"] };
        vm.Messages.Add(message);

        Assert.Equal(0, ImapSyncService.ApplyVisibleLabelAssignments(vm, [message],
            new Dictionary<int, List<int>>(), new Dictionary<int, string>()));
        Assert.Empty(message.LabelNames);
        Assert.Single(vm.Messages);

        vm.SelectLabel(10);
        message.IsSuggested = true;
        Assert.Equal(0, ImapSyncService.ApplyVisibleLabelAssignments(vm, [message],
            new Dictionary<int, List<int>>(), new Dictionary<int, string>()));
        Assert.Single(vm.Messages);
        Assert.True(message.IsSuggested);
    }

    [Fact]
    public void Refresh_DoesNotModifyMessagesLoadedAfterSnapshot()
    {
        var vm = new MailboxViewModel();
        vm.SelectLabel(10);
        var oldMessage = new EmailMessageViewModel { Id = "1" };
        var currentMessage = new EmailMessageViewModel { Id = "1", LabelNames = ["Work"] };
        vm.Messages.Add(currentMessage);

        ImapSyncService.ApplyVisibleLabelAssignments(vm, [oldMessage],
            new Dictionary<int, List<int>>(), new Dictionary<int, string>());

        Assert.Same(currentMessage, Assert.Single(vm.Messages));
        Assert.Equal("Work", Assert.Single(currentMessage.LabelNames));
    }
}
