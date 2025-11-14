using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using maildot.Models;

namespace maildot.Views;

public sealed partial class ImapDashboardView : UserControl
{
    public ImapDashboardView()
    {
        InitializeComponent();
    }

    public event EventHandler? RequestReauthentication;

    public void UpdateAccountInfo(AccountSettings settings)
    {
        AccountSummaryTextBlock.Text = $"Connected to {settings.Server} as {settings.Username}.";
    }

    private void OnReauthClicked(object sender, RoutedEventArgs e)
    {
        RequestReauthentication?.Invoke(this, EventArgs.Empty);
    }
}
