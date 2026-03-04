using maildot.ViewModels;

namespace maildot.Tests;

public class MailboxViewModelReadStateTests
{
    [Fact]
    public void EmailMessageViewModel_IsUnread_ReflectsIsRead()
    {
        var vm = new EmailMessageViewModel();

        vm.IsRead = true;
        Assert.False(vm.IsUnread);

        vm.IsRead = false;
        Assert.True(vm.IsUnread);
    }

    [Fact]
    public void LabelViewModel_UnreadCount_CanBeUpdated()
    {
        var label = new LabelViewModel(1, "Invoices", null);

        label.UnreadCount = 7;

        Assert.Equal(7, label.UnreadCount);
    }
}
