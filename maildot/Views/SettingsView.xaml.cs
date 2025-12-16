using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using maildot.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace maildot.Views;

public sealed partial class SettingsView : UserControl
{
    private PostgresSettings _postgresSettings = new();
    private McpSettings _mcpSettings = new();
    private TaskCompletionSource<bool>? _mcpWarningCompletion;

    public ObservableCollection<AccountSettings> Accounts { get; } = new();

    public event EventHandler? AddAccountRequested;
    public event EventHandler<int>? SetActiveAccountRequested;
    public event EventHandler<int>? ReenterPasswordRequested;
    public event EventHandler<int>? DeleteAccountRequested;
    public event EventHandler<PostgresSettingsSavedEventArgs>? PostgresSettingsSaved;
    public event EventHandler<McpSettingsSavedEventArgs>? McpSettingsSaved;

    public SettingsView()
    {
        InitializeComponent();
    }

    public void Initialize(IEnumerable<AccountSettings> accounts, int? activeAccountId, PostgresSettings? postgresSettings, McpSettings? mcpSettings, string? postgresStatusMessage = null, bool isError = false)
    {
        Accounts.Clear();
        foreach (var account in accounts)
        {
            account.IsActive = account.Id == activeAccountId;
            Accounts.Add(account);
        }

        _postgresSettings = postgresSettings ?? new PostgresSettings();
        PgHostTextBox.Text = _postgresSettings.Host;
        PgPortTextBox.Text = _postgresSettings.Port.ToString();
        PgDatabaseTextBox.Text = _postgresSettings.Database;
        PgUsernameTextBox.Text = _postgresSettings.Username;
        PgSslCheckBox.IsChecked = _postgresSettings.UseSsl;
        PgPasswordBox.Password = string.Empty;
        SetPostgresStatus(postgresStatusMessage ?? string.Empty, isError);

        _mcpSettings = mcpSettings ?? new McpSettings();
        McpEnabledToggle.IsOn = _mcpSettings.Enabled;
        McpBindAddressTextBox.Text = _mcpSettings.BindAddress;
        McpPortTextBox.Text = _mcpSettings.Port.ToString();
        SetMcpStatus(string.Empty, false);

        Bindings.Update();
    }

    private void OnAddAccountClicked(object sender, RoutedEventArgs e) =>
        AddAccountRequested?.Invoke(this, EventArgs.Empty);

    private void OnSetActiveClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is AccountSettings account)
        {
            SetActiveAccountRequested?.Invoke(this, account.Id);
        }
    }

    private void OnReenterPasswordClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is AccountSettings account)
        {
            ReenterPasswordRequested?.Invoke(this, account.Id);
        }
    }

    private void OnDeleteAccountClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is AccountSettings account)
        {
            DeleteAccountRequested?.Invoke(this, account.Id);
        }
    }

    private void OnSavePostgresClicked(object sender, RoutedEventArgs e)
    {
        PgStatusTextBlock.Text = string.Empty;

        var host = PgHostTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            PgStatusTextBlock.Text = "Host is required.";
            return;
        }

        if (!int.TryParse(PgPortTextBox.Text.Trim(), out var port) || port <= 0)
        {
            PgStatusTextBlock.Text = "Port must be a positive number.";
            return;
        }

        var database = PgDatabaseTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(database))
        {
            PgStatusTextBlock.Text = "Database name is required.";
            return;
        }

        var username = PgUsernameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(username))
        {
            PgStatusTextBlock.Text = "Username is required.";
            return;
        }

        var password = PgPasswordBox.Password ?? string.Empty;
        if (string.IsNullOrEmpty(password))
        {
            PgStatusTextBlock.Text = "Password cannot be empty.";
            return;
        }

        var settings = new PostgresSettings
        {
            Host = host,
            Port = port,
            Database = database,
            Username = username,
            UseSsl = PgSslCheckBox.IsChecked == true
        };

        _postgresSettings = settings;
        PgPasswordBox.Password = string.Empty;
        PgStatusTextBlock.Text = "PostgreSQL settings saved.";

        PostgresSettingsSaved?.Invoke(this, new PostgresSettingsSavedEventArgs(settings, password));
    }

    private async void OnSaveMcpClicked(object sender, RoutedEventArgs e)
    {
        SetMcpStatus(string.Empty, false);

        var bindAddress = McpBindAddressTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(bindAddress))
        {
            SetMcpStatus("Bind address is required.", true);
            return;
        }

        if (!int.TryParse(McpPortTextBox.Text.Trim(), out var port) || port < 1 || port > 65535)
        {
            SetMcpStatus("Port must be between 1 and 65535.", true);
            return;
        }

        var enabled = McpEnabledToggle.IsOn;
        if (enabled)
        {
            var localWarningAccepted = await ShowMcpWarningAsync(
                title: "Enable MCP server?",
                message: "This may expose email information to other processes on this machine, including those run by other users.");

            if (!localWarningAccepted)
            {
                return;
            }

            var isLocalhost = string.Equals(bindAddress, "127.0.0.1", StringComparison.Ordinal);
            if (!isLocalhost)
            {
                var remoteWarningAccepted = await ShowMcpWarningAsync(
                    title: "Warning: Remote access risk",
                    message: "Binding to a non-localhost address may allow remote access to your email data. Only proceed if you understand and trust the network environment.");

                if (!remoteWarningAccepted)
                {
                    return;
                }
            }
        }

        var settings = new McpSettings
        {
            Enabled = enabled,
            BindAddress = bindAddress,
            Port = port
        };

        _mcpSettings = settings;
        SetMcpStatus("MCP settings saved.", false);
        McpSettingsSaved?.Invoke(this, new McpSettingsSavedEventArgs(settings));
    }

    public void SetPostgresStatus(string message, bool isError)
    {
        PgStatusTextBlock.Text = message;
        var resource = isError ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush";
        if (Application.Current.Resources.TryGetValue(resource, out var brush) && brush is Brush asBrush)
        {
            PgStatusTextBlock.Foreground = asBrush;
        }
    }

    public void SetMcpStatus(string message, bool isError)
    {
        McpStatusTextBlock.Text = message;
        var resource = isError ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush";
        if (Application.Current.Resources.TryGetValue(resource, out var brush) && brush is Brush asBrush)
        {
            McpStatusTextBlock.Foreground = asBrush;
        }
    }

    private async Task<bool> ShowMcpWarningAsync(string title, string message)
    {
        if (_mcpWarningCompletion != null)
        {
            // prevent overlapping warnings
            return false;
        }

        _mcpWarningCompletion = new TaskCompletionSource<bool>();
        McpWarningTitle.Text = title;
        McpWarningMessage.Text = message;
        McpWarningOverlay.Visibility = Visibility.Visible;

        var result = await _mcpWarningCompletion.Task;
        McpWarningOverlay.Visibility = Visibility.Collapsed;
        _mcpWarningCompletion = null;
        return result;
    }

    private void OnMcpWarningConfirmClicked(object sender, RoutedEventArgs e) =>
        _mcpWarningCompletion?.TrySetResult(true);

    private void OnMcpWarningCancelClicked(object sender, RoutedEventArgs e) =>
        _mcpWarningCompletion?.TrySetResult(false);
}

public sealed class PostgresSettingsSavedEventArgs : EventArgs
{
    public PostgresSettingsSavedEventArgs(PostgresSettings settings, string password)
    {
        Settings = settings;
        Password = password;
    }

    public PostgresSettings Settings { get; }
    public string Password { get; }
}

public sealed class McpSettingsSavedEventArgs : EventArgs
{
    public McpSettingsSavedEventArgs(McpSettings settings)
    {
        Settings = settings;
    }

    public McpSettings Settings { get; }
}
