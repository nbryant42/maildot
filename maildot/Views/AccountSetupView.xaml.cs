using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using maildot.Models;
using Windows.System;

namespace maildot.Views;

public sealed partial class AccountSetupView : UserControl
{
    private int _accountId;

    public AccountSetupView()
    {
        InitializeComponent();
    }

    public event EventHandler<AccountSetupResultEventArgs>? SettingsSaved;

    public void Initialize(AccountSettings? settings, string? statusMessage = null)
    {
        _accountId = settings?.Id ?? 0;
        AccountNameTextBox.Text = settings?.AccountName ?? string.Empty;
        ServerTextBox.Text = settings?.Server ?? string.Empty;
        PortTextBox.Text = settings is null ? "993" : settings.Port.ToString();
        UsernameTextBox.Text = settings?.Username ?? string.Empty;
        PasswordBox.Password = string.Empty;
        SslCheckBox.IsChecked = settings?.UseSsl ?? true;
        StatusTextBlock.Text = statusMessage ?? string.Empty;
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        var server = ServerTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(server))
        {
            StatusTextBlock.Text = "Server address is required.";
            return;
        }

        if (!int.TryParse(PortTextBox.Text.Trim(), out var port) || port <= 0)
        {
            StatusTextBlock.Text = "Please enter a valid port number.";
            return;
        }

        var username = UsernameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(username))
        {
            StatusTextBlock.Text = "Username is required.";
            return;
        }

        if (string.IsNullOrEmpty(PasswordBox.Password))
        {
            StatusTextBlock.Text = "Password cannot be blank.";
            return;
        }

        var accountName = AccountNameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(accountName))
        {
            accountName = $"{username}@{server}";
        }

        var settings = new AccountSettings
        {
            Id = _accountId,
            AccountName = accountName,
            Server = server,
            Port = port,
            Username = username,
            UseSsl = SslCheckBox.IsChecked == true
        };

        SettingsSaved?.Invoke(this, new AccountSetupResultEventArgs(settings, PasswordBox.Password));
    }

    private void OnPasswordKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            OnSaveClicked(sender, new RoutedEventArgs());
            e.Handled = true;
        }
    }
}

public sealed class AccountSetupResultEventArgs : EventArgs
{
    public AccountSetupResultEventArgs(AccountSettings settings, string password)
    {
        Settings = settings;
        Password = password;
    }

    public AccountSettings Settings { get; }
    public string Password { get; }
}
