using System;
using System.Threading.Tasks;
using maildot.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;

namespace maildot.Views;

public sealed partial class ImapDashboardView : UserControl
{
    private ScrollViewer? _messagesScrollViewer;
    private bool _hasRequestedMore;
    private bool _attachmentsInitialized;

    public TreeView LabelsTreeControl => LabelsTree;

    public ImapDashboardView()
    {
        InitializeComponent();
    }

    public event EventHandler<MailFolderViewModel>? FolderSelected;
    public event EventHandler<EmailMessageViewModel>? MessageSelected;
    public event EventHandler? RetryRequested;
    public event EventHandler? LoadMoreRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? ComposeRequested;
    public event EventHandler<EmailMessageViewModel>? ReplyRequested;
    public event EventHandler<EmailMessageViewModel>? ReplyAllRequested;
    public event EventHandler<EmailMessageViewModel>? ForwardRequested;
    public event EventHandler? ClearSearchRequested;
    public event EventHandler? RootLabelAddRequested;
    public event EventHandler<int>? ChildLabelAddRequested;
    public event EventHandler<LabelDropRequest>? LabelDropRequested;
    public event EventHandler<LabelViewModel>? LabelSelected;

    public void BindViewModel(MailboxViewModel viewModel)
    {
        DataContext = viewModel;
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

    private void OnSettingsClicked(object sender, RoutedEventArgs e)
    {
        SettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnComposeClicked(object sender, RoutedEventArgs e)
    {
        ComposeRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnClearSearchClicked(object sender, RoutedEventArgs e)
    {
        ClearSearchRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnReplyClicked(object sender, RoutedEventArgs e)
    {
        if (MessagesList.SelectedItem is EmailMessageViewModel message)
        {
            ReplyRequested?.Invoke(this, message);
        }
    }

    private void OnReplyAllClicked(object sender, RoutedEventArgs e)
    {
        if (MessagesList.SelectedItem is EmailMessageViewModel message)
        {
            ReplyAllRequested?.Invoke(this, message);
        }
    }

    private void OnForwardClicked(object sender, RoutedEventArgs e)
    {
        if (MessagesList.SelectedItem is EmailMessageViewModel message)
        {
            ForwardRequested?.Invoke(this, message);
        }
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

    private void OnMessagesSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MessagesList.SelectedItem is EmailMessageViewModel message)
        {
            MessageSelected?.Invoke(this, message);
        }
    }

    private void OnMessagesDragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (e.Items.Count == 0 || e.Items[0] is not EmailMessageViewModel message)
        {
            return;
        }

        e.Data.SetText(message.Id);
        e.Data.Properties["FolderId"] = message.FolderId;
        e.Data.RequestedOperation = DataPackageOperation.Copy;
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

    public async Task DisplayMessageContentAsync(string html)
    {
        await EnsureWebViewAsync();
        MessageWebView.NavigateToString(string.IsNullOrWhiteSpace(html) ? "<html><body></body></html>" : html);
    }

    public async Task ClearMessageContentAsync()
    {
        await EnsureWebViewAsync();
        MessageWebView.NavigateToString("<html><body></body></html>");
    }

    public async Task DisplayAttachmentsAsync(string html)
    {
        await EnsureAttachmentsWebViewAsync();
        AttachmentsHeader.Visibility = Visibility.Visible;
        AttachmentsWebView.Visibility = Visibility.Visible;
        AttachmentsHeaderRow.Height = new GridLength(1, GridUnitType.Auto);
        AttachmentsContentRow.Height = new GridLength(1, GridUnitType.Star);
        AttachmentsWebView.NavigateToString(string.IsNullOrWhiteSpace(html) ? "<html><body></body></html>" : html);
    }

    public async Task ClearAttachmentsAsync()
    {
        await EnsureAttachmentsWebViewAsync();
        AttachmentsWebView.NavigateToString("<html><body></body></html>");
        AttachmentsHeader.Visibility = Visibility.Collapsed;
        AttachmentsWebView.Visibility = Visibility.Collapsed;
        AttachmentsHeaderRow.Height = new GridLength(0);
        AttachmentsContentRow.Height = new GridLength(0);
    }

    private async Task EnsureWebViewAsync()
    {
        if (MessageWebView.CoreWebView2 != null)
        {
            return;
        }

        await MessageWebView.EnsureCoreWebView2Async();
        var settings = MessageWebView.CoreWebView2!.Settings;
        settings.IsScriptEnabled = false;
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreDevToolsEnabled = false;
    }

    private async Task EnsureAttachmentsWebViewAsync()
    {
        if (_attachmentsInitialized && AttachmentsWebView.CoreWebView2 != null)
        {
            return;
        }

        await AttachmentsWebView.EnsureCoreWebView2Async();
        _attachmentsInitialized = true;
        var settings = AttachmentsWebView.CoreWebView2!.Settings;
        settings.IsScriptEnabled = false;
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreDevToolsEnabled = false;
    }

    private void OnAddRootLabelClicked(object _, RoutedEventArgs e) =>
        RootLabelAddRequested?.Invoke(this, EventArgs.Empty);

    private void OnAddChildLabelClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is int id)
        {
            ChildLabelAddRequested?.Invoke(this, id);
        }
    }

    private void OnLabelDragEnter(object sender, DragEventArgs e) => SetLabelDragOperation(e);

    private void OnLabelDragOver(object sender, DragEventArgs e) => SetLabelDragOperation(e);

    private void OnLabelDragLeave(object sender, DragEventArgs e)
    {
    }

    private async void OnLabelDrop(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not LabelViewModel label)
        {
            return;
        }

        if (!e.DataView.Contains(StandardDataFormats.Text))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        var text = await e.DataView.GetTextAsync();
        var folderId = e.DataView.Properties.TryGetValue("FolderId", out var folder)
            ? folder as string
            : null;

        LabelDropRequested?.Invoke(this, new LabelDropRequest(label.Id, text, folderId));
        e.AcceptedOperation = DataPackageOperation.Copy;
    }

    private void OnLabelInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is LabelViewModel label)
        {
            LabelSelected?.Invoke(this, label);
        }
    }

    private static void SetLabelDragOperation(DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.Text))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;
        }
    }
}

public sealed record LabelDropRequest(int LabelId, string? MessageId, string? FolderId);
