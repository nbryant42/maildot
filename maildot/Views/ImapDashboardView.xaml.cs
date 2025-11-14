using System;
using maildot.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace maildot.Views;

public sealed partial class ImapDashboardView : UserControl
{
    public ImapDashboardView()
    {
        InitializeComponent();
    }

    public event EventHandler? RequestReauthentication;
    public event EventHandler<MailFolderViewModel>? FolderSelected;

    public void BindViewModel(MailboxViewModel viewModel)
    {
        DataContext = viewModel;
    }

    private void OnReauthClicked(object sender, RoutedEventArgs e)
    {
        RequestReauthentication?.Invoke(this, EventArgs.Empty);
    }

    private void OnFolderSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FoldersList.SelectedItem is MailFolderViewModel folder)
        {
            FolderSelected?.Invoke(this, folder);
        }
    }
}
