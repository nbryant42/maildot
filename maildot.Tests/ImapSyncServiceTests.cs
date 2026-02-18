using maildot.Services;
using maildot.ViewModels;

namespace maildot.Tests;

public class ImapSyncServiceTests
{
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

        ImapSyncService.ApplySenderLabelToVisibleMessages(vm, "same@example.com", "Clients");

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

        ImapSyncService.ApplySenderLabelToVisibleMessages(vm, "sender@example.com", "Clients");
        ImapSyncService.ApplySenderLabelToVisibleMessages(vm, "sender@example.com", "clients");

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

        ImapSyncService.ApplySenderLabelToVisibleMessages(vm, "same@example.com", "Clients");

        Assert.Single(vm.Messages);
        Assert.Equal(["Clients"], message.LabelNames);
    }
}
