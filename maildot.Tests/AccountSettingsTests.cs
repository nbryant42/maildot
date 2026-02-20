using maildot.Models;

namespace maildot.Tests;

public class AccountSettingsTests
{
    [Fact]
    public void DeleteTargetStatus_RaisesPropertyChanged()
    {
        var settings = new AccountSettings();
        string? changed = null;
        settings.PropertyChanged += (_, e) => changed = e.PropertyName;

        settings.DeleteTargetStatus = "PASS: Folder found on IMAP.";

        Assert.Equal(nameof(AccountSettings.DeleteTargetStatus), changed);
    }

    [Fact]
    public void DeleteTargetFolderFullName_RaisesPropertyChanged()
    {
        var settings = new AccountSettings();
        string? changed = null;
        settings.PropertyChanged += (_, e) => changed = e.PropertyName;

        settings.DeleteTargetFolderFullName = "Deleted";

        Assert.Equal(nameof(AccountSettings.DeleteTargetFolderFullName), changed);
    }
}
