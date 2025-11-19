using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using maildot.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace maildot.Views;

public sealed partial class SettingsView : UserControl
{
    public ObservableCollection<AccountSettings> Accounts { get; } = new();

    public event EventHandler? AddAccountRequested;
    public event EventHandler<Guid>? SetActiveAccountRequested;
    public event EventHandler<Guid>? ReenterPasswordRequested;

    public SettingsView()
    {
        InitializeComponent();
    }

    public void Initialize(IEnumerable<AccountSettings> accounts, Guid? activeAccountId)
    {
        Accounts.Clear();
        foreach (var account in accounts)
        {
            account.IsActive = account.Id == activeAccountId;
            Accounts.Add(account);
        }

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
}
