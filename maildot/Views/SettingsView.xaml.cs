using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using maildot.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace maildot.Views;

public sealed partial class SettingsView : UserControl
{
    private PostgresSettings _postgresSettings = new();

    public ObservableCollection<AccountSettings> Accounts { get; } = new();

    public event EventHandler? AddAccountRequested;
    public event EventHandler<Guid>? SetActiveAccountRequested;
    public event EventHandler<Guid>? ReenterPasswordRequested;
    public event EventHandler<PostgresSettingsSavedEventArgs>? PostgresSettingsSaved;

    public SettingsView()
    {
        InitializeComponent();
    }

    public void Initialize(IEnumerable<AccountSettings> accounts, Guid? activeAccountId, PostgresSettings? postgresSettings)
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
        PgStatusTextBlock.Text = string.Empty;

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
