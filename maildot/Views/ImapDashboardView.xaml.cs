using System;
using maildot.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace maildot.Views;

public sealed partial class ImapDashboardView : UserControl
{
    private ScrollViewer? _messagesScrollViewer;
    private bool _hasRequestedMore;

    public ImapDashboardView()
    {
        InitializeComponent();
    }

    public event EventHandler? RequestReauthentication;
    public event EventHandler<MailFolderViewModel>? FolderSelected;
    public event EventHandler? RetryRequested;
    public event EventHandler? LoadMoreRequested;

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
            _hasRequestedMore = false;
        }
    }

    private void OnRetryClicked(object sender, RoutedEventArgs e)
    {
        RetryRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnMessagesListLoaded(object sender, RoutedEventArgs e)
    {
        if (_messagesScrollViewer != null)
        {
            return;
        }

        _messagesScrollViewer = FindScrollViewer(MessagesList);
        if (_messagesScrollViewer != null)
        {
            _messagesScrollViewer.ViewChanged += OnMessagesScrollViewerViewChanged;
        }
    }

    private void OnMessagesScrollViewerViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_messagesScrollViewer == null)
        {
            return;
        }

        if (DataContext is not MailboxViewModel vm || !vm.CanLoadMore)
        {
            _hasRequestedMore = false;
            return;
        }

        var nearBottom = _messagesScrollViewer.ScrollableHeight - _messagesScrollViewer.VerticalOffset <= 48;
        if (nearBottom)
        {
            if (!_hasRequestedMore)
            {
                _hasRequestedMore = true;
                LoadMoreRequested?.Invoke(this, EventArgs.Empty);
            }
        }
        else
        {
            _hasRequestedMore = false;
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv)
        {
            return sv;
        }

        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var result = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (result != null)
            {
                return result;
            }
        }

        return null;
    }
}
