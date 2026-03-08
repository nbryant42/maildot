using maildot.Converters;
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

    [Fact]
    public void MailboxViewModel_UpdateAllLabelUnreadCounts_UpdatesExistingTreeInPlace()
    {
        var mailbox = new MailboxViewModel();
        var root = new LabelViewModel(1, "Root", null);
        var child = new LabelViewModel(2, "Child", 1);
        root.Children.Add(child);
        mailbox.Labels.Add(root);

        mailbox.UpdateAllLabelUnreadCounts(new Dictionary<int, int>
        {
            [1] = 3,
            [2] = 5
        });

        Assert.Equal(3, root.UnreadCount);
        Assert.Equal(5, child.UnreadCount);
    }

    [Fact]
    public void BoolToOpacityConverter_UsesZeroAndOne()
    {
        var converter = new BoolToOpacityConverter();

        Assert.Equal(1.0d, converter.Convert(true, typeof(double), null, string.Empty));
        Assert.Equal(0.0d, converter.Convert(false, typeof(double), null, string.Empty));
    }
}
