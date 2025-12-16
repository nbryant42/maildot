using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using maildot.Models;
using maildot.Services;
using maildot.Views;
using maildot.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MimeKit;
using System.IO;
using Windows.System;
using Windows.UI;
using WinRT.Interop;
using Microsoft.UI;
using System.Net;

namespace maildot;

public sealed partial class MainWindow : Window
{
    private AccountSetupView? _accountSetupView;
    private ImapDashboardView? _dashboardView;
    private MailboxViewModel? _mailboxViewModel;
    private ImapSyncService? _imapService;
    private readonly List<AccountSettings> _accounts = [];
    private AccountSettings? _activeAccount;
    private PostgresSettings _postgresSettings = PostgresSettingsStore.Load();
    private McpSettings _mcpSettings = McpSettingsStore.Load();
    private bool _startupInitialized;
    private SearchMode _searchMode = SearchMode.Auto;
    private AppWindow? _appWindow;
    private DateTimeOffset? _searchSinceUtc = null;
    private const double TitleBarMinHeight = 52;

    public MainWindow()
    {
        InitializeComponent();
        SetWindowIcon();
        InitializeTitleBar();
        Activated += OnWindowActivated;
    }

    private async void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_startupInitialized)
        {
            return;
        }

        _startupInitialized = true;
        Activated -= OnWindowActivated;

        await EvaluateStartupAsync();
    }

    private async Task EvaluateStartupAsync()
    {
        if (!await EnsurePostgresReadyAsync(forceShowSettings: true))
        {
            return;
        }

        RefreshAccounts();

        if (_accounts.Count == 0)
        {
            await ShowAccountSetup(null, "Please enter your IMAP server details to begin.");
            return;
        }

        var activeAccount = AccountSettingsStore.GetActiveAccount() ?? _accounts.First();
        AccountSettingsStore.SetActiveAccount(activeAccount.Id);
        await SignInAsync(activeAccount);
    }

    private void RefreshAccounts()
    {
        _accounts.Clear();
        _accounts.AddRange(AccountSettingsStore.GetAllAccounts());
    }

    private async Task ShowAccountSetup(AccountSettings? existing, string? statusMessage = null)
    {
        await CleanupImapServiceAsync();

        _accountSetupView ??= new AccountSetupView();
        _accountSetupView.SettingsSaved -= OnAccountSettingsSavedAsync;
        _accountSetupView.SettingsSaved += OnAccountSettingsSavedAsync;
        _accountSetupView.Initialize(existing, statusMessage);
        RootContent.Content = _accountSetupView;
    }

    private async void OnAccountSettingsSavedAsync(object? sender, AccountSetupResultEventArgs e)
    {
        AccountSettingsStore.AddOrUpdate(e.Settings);
        RefreshAccounts();
        CredentialManager.SavePassword(e.Settings, e.Password);
        await SignInAsync(e.Settings);
    }

    private async Task SignInAsync(AccountSettings account)
    {
        var response = await CredentialManager.RequestPasswordAsync(account);
        if (response.Result == CredentialAccessResult.Success && !string.IsNullOrWhiteSpace(response.Password))
        {
            ShowDashboard(account, response.Password);
            return;
        }

        var manualPassword = await PromptForPasswordAsync(account);
        if (!string.IsNullOrWhiteSpace(manualPassword))
        {
            CredentialManager.SavePassword(account, manualPassword);
            ShowDashboard(account, manualPassword);
            return;
        }

        await ShowAccountSetup(account, "Password is required to connect.");
    }

    private async Task<string?> PromptForPasswordAsync(AccountSettings account)
    {
        var xamlRoot = GetXamlRoot();
        if (xamlRoot == null)
        {
            return null;
        }

        var passwordBox = new PasswordBox { PlaceholderText = "Password", Width = 300 };
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = $"Enter the password for {account.AccountName ?? account.Username}.",
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(passwordBox);

        var dialog = new ContentDialog
        {
            Title = "Password required",
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Content = content,
            XamlRoot = xamlRoot
        };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary ? passwordBox.Password : null;
    }

    private void ShowDashboard(AccountSettings settings, string password)
    {
        _activeAccount = settings;
        EnsureDashboard();
        _ = _dashboardView!.ClearMessageContentAsync();
        _ = _dashboardView.ClearAttachmentsAsync();
        _mailboxViewModel!.SetAccountSummary($"{settings.AccountName} ({settings.Username})");
        _ = StartImapSyncAsync(settings, password);
    }

    private void SetWindowIcon()
    {
        try
        {
            var hWnd = WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");

            if (File.Exists(iconPath))
            {
                appWindow.SetIcon(iconPath);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to set window icon: {ex}");
        }
    }

    private async Task<bool> EnsurePostgresReadyAsync(bool forceShowSettings = false)
    {
        if (App.PostgresState == PostgresMigrationState.NotStarted)
        {
            App.ApplyPendingMigrations();
        }

        if (App.PostgresState == PostgresMigrationState.Success)
        {
            return true;
        }

        var message = BuildPostgresStatusMessage();
        await ShowSettingsDialogAsync(message, App.PostgresState != PostgresMigrationState.Success, forceShowSettings);

        return App.PostgresState == PostgresMigrationState.Success;
    }

    private void EnsureDashboard()
    {
        _mailboxViewModel ??= new MailboxViewModel();

        if (_dashboardView == null)
        {
            _dashboardView = new ImapDashboardView();
        }

        _dashboardView.FolderSelected -= OnFolderSelected;
        _dashboardView.FolderSelected += OnFolderSelected;
        _dashboardView.LoadMoreRequested -= OnLoadMoreRequested;
        _dashboardView.LoadMoreRequested += OnLoadMoreRequested;
        _dashboardView.RetryRequested -= OnRetryRequested;
        _dashboardView.RetryRequested += OnRetryRequested;
        _dashboardView.SettingsRequested -= OnSettingsRequested;
        _dashboardView.SettingsRequested += OnSettingsRequested;
        _dashboardView.MessageSelected -= OnMessageSelected;
        _dashboardView.MessageSelected += OnMessageSelected;
        _dashboardView.ClearSearchRequested -= OnClearSearchRequested;
        _dashboardView.ClearSearchRequested += OnClearSearchRequested;
        _dashboardView.ComposeRequested -= OnComposeRequested;
        _dashboardView.ComposeRequested += OnComposeRequested;
        _dashboardView.ReplyRequested -= OnReplyRequested;
        _dashboardView.ReplyRequested += OnReplyRequested;
        _dashboardView.ReplyAllRequested -= OnReplyAllRequested;
        _dashboardView.ReplyAllRequested += OnReplyAllRequested;
        _dashboardView.ForwardRequested -= OnForwardRequested;
        _dashboardView.ForwardRequested += OnForwardRequested;
        _dashboardView.RootLabelAddRequested -= OnRootLabelAddRequested;
        _dashboardView.RootLabelAddRequested += OnRootLabelAddRequested;
        _dashboardView.ChildLabelAddRequested -= OnChildLabelAddRequested;
        _dashboardView.ChildLabelAddRequested += OnChildLabelAddRequested;
        _dashboardView.LabelDropRequested -= OnLabelDropRequested;
        _dashboardView.LabelDropRequested += OnLabelDropRequested;
        _dashboardView.LabelSelected -= OnLabelSelected;
        _dashboardView.LabelSelected += OnLabelSelected;
        _dashboardView.SuggestionAccepted -= OnSuggestionAccepted;
        _dashboardView.SuggestionAccepted += OnSuggestionAccepted;
        _dashboardView.LabelSenderRequested -= OnLabelSenderRequested;
        _dashboardView.LabelSenderRequested += OnLabelSenderRequested;

        _dashboardView.BindViewModel(_mailboxViewModel);
        RootContent.Content = _dashboardView;
    }

    private async Task StartImapSyncAsync(AccountSettings settings, string password)
    {
        if (_mailboxViewModel == null)
        {
            return;
        }

        await CleanupImapServiceAsync();
        _imapService = new ImapSyncService(_mailboxViewModel, DispatcherQueue);
        await _imapService.StartAsync(settings, password);
    }

    private void OnFolderSelected(object? sender, MailFolderViewModel folder)
    {
        if (_imapService == null)
        {
            return;
        }

        ClearLabelSelection();
        _mailboxViewModel?.ExitSearchMode();
        _dashboardView?.ClearMessageContentAsync();
        _dashboardView?.ClearAttachmentsAsync();
        _ = _imapService.LoadFolderAsync(folder.Id);
    }

    private void OnLoadMoreRequested(object? sender, EventArgs e)
    {
        if (_imapService == null || _mailboxViewModel?.SelectedFolder is not MailFolderViewModel folder)
        {
            return;
        }

        _ = _imapService.LoadOlderMessagesAsync(folder.Id);
    }

    private void OnRetryRequested(object? sender, EventArgs e)
    {
        if (_imapService != null && _mailboxViewModel?.SelectedFolder is MailFolderViewModel folder)
        {
            _mailboxViewModel.SetRetryVisible(false);
            _ = _imapService.LoadFolderAsync(folder.Id);
            return;
        }

        if (_activeAccount != null)
        {
            _mailboxViewModel?.SetRetryVisible(false);
            _ = SignInAsync(_activeAccount);
        }
    }

    private async void OnSettingsRequested(object? sender, EventArgs e)
    {
        var needsStatus = App.PostgresState != PostgresMigrationState.Success &&
                          App.PostgresState != PostgresMigrationState.NotStarted;
        await ShowSettingsDialogAsync(
            needsStatus ? BuildPostgresStatusMessage() : null,
            needsStatus);
    }

    private void OnPostgresSettingsSaved(object? sender, PostgresSettingsSavedEventArgs e)
    {
        _postgresSettings = e.Settings;
        PostgresSettingsStore.Save(e.Settings);
        CredentialManager.SavePostgresPassword(e.Settings, e.Password);

        var result = App.ApplyPendingMigrations();
        if (sender is SettingsView view)
        {
            view.SetPostgresStatus(BuildPostgresStatusMessage(), result != PostgresMigrationState.Success);
        }
    }

    private void OnMcpSettingsSaved(object? sender, McpSettingsSavedEventArgs e)
    {
        _mcpSettings = e.Settings;
        McpSettingsStore.Save(e.Settings);
        if (sender is SettingsView view)
        {
            view.SetMcpStatus("MCP settings saved.", false);
        }
    }

    private async Task ShowSettingsDialogAsync(string? postgresStatusMessage = null, bool isError = false, bool forceShowWhenNoAccounts = false)
    {
        if (_accounts.Count == 0 && !forceShowWhenNoAccounts)
        {
            await ShowAccountSetup(null);
            return;
        }

        var xamlRoot = GetXamlRoot();
        if (xamlRoot == null)
        {
            // If no XamlRoot yet, fall back to embedding settings in main content.
            var inlineView = new SettingsView();
            inlineView.Initialize(_accounts, _activeAccount?.Id, _postgresSettings, _mcpSettings, postgresStatusMessage, isError);
            inlineView.AddAccountRequested += async (_, __) => await ShowAccountSetup(null);
            inlineView.SetActiveAccountRequested += async (_, id) => await SwitchActiveAccountAsync(id);
            inlineView.ReenterPasswordRequested += async (_, id) => await ReenterPasswordAsync(id);
            inlineView.DeleteAccountRequested += async (_, id) => await DeleteAccountAsync(id);
            inlineView.PostgresSettingsSaved += OnPostgresSettingsSaved;
            inlineView.McpSettingsSaved += OnMcpSettingsSaved;
            RootContent.Content = inlineView;
            return;
        }

        var settingsView = new SettingsView();
        settingsView.Initialize(_accounts, _activeAccount?.Id, _postgresSettings, _mcpSettings, postgresStatusMessage, isError);

        var dialog = new ContentDialog
        {
            Title = "Settings",
            Content = settingsView,
            PrimaryButtonText = "Close",
            XamlRoot = xamlRoot
        };

        EventHandler addAccountHandler = async (_, __) =>
        {
            dialog.Hide();
            await ShowAccountSetup(null);
        };
        EventHandler<int> setActiveHandler = async (_, id) =>
        {
            dialog.Hide();
            await SwitchActiveAccountAsync(id);
        };
        EventHandler<int> reenterHandler = async (_, id) =>
        {
            dialog.Hide();
            await ReenterPasswordAsync(id);
        };
        EventHandler<int> deleteHandler = async (_, id) =>
        {
            dialog.Hide();
            await DeleteAccountAsync(id);
        };

        settingsView.AddAccountRequested += addAccountHandler;
        settingsView.SetActiveAccountRequested += setActiveHandler;
        settingsView.ReenterPasswordRequested += reenterHandler;
        settingsView.DeleteAccountRequested += deleteHandler;
        settingsView.PostgresSettingsSaved += OnPostgresSettingsSaved;
        settingsView.McpSettingsSaved += OnMcpSettingsSaved;

        await dialog.ShowAsync();

        settingsView.AddAccountRequested -= addAccountHandler;
        settingsView.SetActiveAccountRequested -= setActiveHandler;
        settingsView.ReenterPasswordRequested -= reenterHandler;
        settingsView.DeleteAccountRequested -= deleteHandler;
        settingsView.PostgresSettingsSaved -= OnPostgresSettingsSaved;
        settingsView.McpSettingsSaved -= OnMcpSettingsSaved;
    }

    private static string BuildPostgresStatusMessage()
    {
        return App.PostgresState switch
        {
            PostgresMigrationState.Success => "PostgreSQL is ready.",
            PostgresMigrationState.MissingSettings => "PostgreSQL settings are incomplete. Please fill in all required fields.",
            PostgresMigrationState.MissingPassword => "PostgreSQL password not found. Please re-enter it.",
            PostgresMigrationState.Failed => $"PostgreSQL migration failed: {App.PostgresError}",
            _ => "PostgreSQL setup has not run yet."
        };
    }

    private XamlRoot? GetXamlRoot()
    {
        if (RootContent?.XamlRoot != null)
        {
            return RootContent.XamlRoot;
        }

        if (Content is FrameworkElement element)
        {
            return element.XamlRoot;
        }

        return null;
    }

    private async Task SwitchActiveAccountAsync(int accountId)
    {
        RefreshAccounts();
        var account = _accounts.FirstOrDefault(a => a.Id == accountId);
        if (account == null)
        {
            return;
        }

        AccountSettingsStore.SetActiveAccount(accountId);
        await SignInAsync(account);
    }

    private async Task DeleteAccountAsync(int accountId)
    {
        RefreshAccounts();
        var account = _accounts.FirstOrDefault(a => a.Id == accountId);
        if (account == null)
        {
            return;
        }

        var xamlRoot = GetXamlRoot();
        if (xamlRoot == null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "Delete account?",
            Content = $"Are you sure you want to permanently delete the account \"{account.AccountName}\"?",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = xamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        await CleanupImapServiceAsync();
        AccountSettingsStore.RemoveAccount(accountId);
        CredentialManager.RemovePassword(account);
        RefreshAccounts();

        if (_accounts.Count == 0)
        {
            await ShowAccountSetup(null, "Add an account to get started.");
            return;
        }

        var next = AccountSettingsStore.GetActiveAccount() ?? _accounts.First();
        await SignInAsync(next);
    }

    private async Task ReenterPasswordAsync(int accountId)
    {
        var account = _accounts.FirstOrDefault(a => a.Id == accountId);
        if (account == null)
        {
            return;
        }

        var password = await PromptForPasswordAsync(account);
        if (string.IsNullOrWhiteSpace(password))
        {
            return;
        }

        CredentialManager.SavePassword(account, password);
        if (_activeAccount?.Id == account.Id)
        {
            ShowDashboard(account, password);
        }
    }

    private async Task CleanupImapServiceAsync()
    {
        if (_imapService != null)
        {
            await _imapService.DisposeAsync();
            _imapService = null;
        }
    }

    private void OnMessageSelected(object? sender, EmailMessageViewModel e)
    {
        if (_imapService == null)
        {
            return;
        }

        var folderId = string.IsNullOrWhiteSpace(e.FolderId)
            ? _mailboxViewModel?.SelectedFolder?.Id
            : e.FolderId;

        if (string.IsNullOrWhiteSpace(folderId))
        {
            return;
        }

        _ = LoadAndDisplayMessageAsync(folderId, e.Id);
    }

    private async Task LoadAndDisplayMessageAsync(string folderId, string messageId)
    {
        if (_imapService == null || _dashboardView == null)
        {
            return;
        }

        var bodyTask = _imapService.LoadMessageBodyAsync(folderId, messageId);
        var attachmentsTask = _imapService.LoadImageAttachmentsAsync(folderId, messageId);

        var body = await bodyTask;
        List<ImapSyncService.AttachmentContent> attachments = [];
        try
        {
            attachments = await attachmentsTask;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Attachment load failed: {ex}");
        }

        if (body?.Headers != null)
        {
            ApplyMessageHeaders(messageId, body.Headers);
        }

        if (body != null)
        {
            await _dashboardView.DisplayMessageContentAsync(body.Html);
        }
        else
        {
            await _dashboardView.ClearMessageContentAsync();
        }

        if (attachments.Count > 0)
        {
            var attachmentsHtml = BuildAttachmentsHtml(attachments);
            await _dashboardView.DisplayAttachmentsAsync(attachmentsHtml);
        }
        else
        {
            await _dashboardView.ClearAttachmentsAsync();
        }
    }

    private void ApplyMessageHeaders(string messageId, ImapSyncService.MessageHeaderInfo headers)
    {
        if (_mailboxViewModel?.SelectedMessage is not EmailMessageViewModel selected ||
            !string.Equals(selected.Id, messageId, StringComparison.Ordinal))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(headers.From))
        {
            selected.Sender = headers.From;
        }

        if (!string.IsNullOrWhiteSpace(headers.FromAddress))
        {
            selected.SenderAddress = headers.FromAddress;
        }

        selected.To = headers.To;
        selected.Cc = headers.Cc;
        selected.Bcc = headers.Bcc;
    }

    private async void OnComposeRequested(object? sender, EventArgs e)
    {
        var uri = BuildMailToUri(Array.Empty<string>(), null, null);
        await Launcher.LaunchUriAsync(uri);
    }

    private async void OnReplyRequested(object? sender, EmailMessageViewModel message) =>
        await LaunchReplyAsync(message);

    private async void OnReplyAllRequested(object? sender, EmailMessageViewModel message) =>
        await LaunchReplyAsync(message);

    private async Task LaunchReplyAsync(EmailMessageViewModel message)
    {
        var to = ExtractAddress(message);
        if (string.IsNullOrEmpty(to))
        {
            return;
        }

        var body = BuildQuotedBody(message);
        var uri = BuildMailToUri(new[] { to }, $"Re: {message.Subject}", body);
        await Launcher.LaunchUriAsync(uri);
    }

    private async void OnForwardRequested(object? sender, EmailMessageViewModel message)
    {
        var body = $"Forwarded message:\r\nFrom: {message.Sender}\r\nSubject: {message.Subject}\r\n\r\n{message.Preview}";
        var uri = BuildMailToUri(Array.Empty<string>(), $"Fwd: {message.Subject}", body);
        await Launcher.LaunchUriAsync(uri);
    }

    private static Uri BuildMailToUri(IEnumerable<string> recipients, string? subject, string? body)
    {
        var builder = new StringBuilder("mailto:");
        var toList = recipients?
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim())
            .ToList() ?? new List<string>();

        if (toList.Count > 0)
        {
            builder.Append(string.Join(",", toList));
        }

        var parameters = new List<string>();
        if (!string.IsNullOrWhiteSpace(subject))
        {
            parameters.Add($"subject={Uri.EscapeDataString(subject)}");
        }

        if (!string.IsNullOrWhiteSpace(body))
        {
            parameters.Add($"body={Uri.EscapeDataString(body)}");
        }

        if (parameters.Count > 0)
        {
            builder.Append('?');
            builder.Append(string.Join("&", parameters));
        }

        if (Uri.TryCreate(builder.ToString(), UriKind.Absolute, out var uri))
        {
            return uri;
        }

        return new Uri("mailto:");
    }

    private void InitializeTitleBar()
    {
        try
        {
            var hWnd = WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);

            if (_appWindow?.TitleBar is { } appTitleBar)
            {
                appTitleBar.ExtendsContentIntoTitleBar = true;
                appTitleBar.ButtonBackgroundColor = Colors.Transparent;
                appTitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
                appTitleBar.ButtonForegroundColor = null;
                appTitleBar.ButtonInactiveForegroundColor = null;
                UpdateTitleBarMetrics(appTitleBar);
            }

            SetTitleBar(TitleBarDragArea);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to initialize custom title bar: {ex}");
        }
    }

    private void UpdateTitleBarMetrics(AppWindowTitleBar titleBar)
    {
        TitleBarRoot.Height = Math.Max(titleBar.Height, TitleBarMinHeight);
        TitleBarRoot.Padding = new Thickness(titleBar.LeftInset, 0, titleBar.RightInset, 0);
    }

    private async void OnSearchQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var text = args.QueryText?.Trim() ?? string.Empty;
        await ExecuteSearchAsync(text);
    }

    private void OnSearchModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SearchModeCombo.SelectedItem is ComboBoxItem item &&
            item.Tag is string tag &&
            Enum.TryParse<SearchMode>(tag, ignoreCase: true, out var mode))
        {
            _searchMode = mode;
        }
    }

    private void OnSearchSinceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SearchSinceCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            _searchSinceUtc = tag switch
            {
                "7d" => DateTimeOffset.UtcNow.AddDays(-7),
                "30d" => DateTimeOffset.UtcNow.AddDays(-30),
                "90d" => DateTimeOffset.UtcNow.AddDays(-90),
                "365d" => DateTimeOffset.UtcNow.AddDays(-365),
                _ => null
            };
        }
    }

    private async void OnAdvancedSearchClicked(object sender, RoutedEventArgs e)
    {
        var xamlRoot = GetXamlRoot();
        if (xamlRoot == null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "Advanced search",
            Content = "Advanced filters are coming soon. Use the mode dropdown to switch between sender, content, or both.",
            CloseButtonText = "Close",
            XamlRoot = xamlRoot
        };

        await dialog.ShowAsync();
    }

    private async Task ExecuteSearchAsync(string query)
    {
        if (_imapService == null || _mailboxViewModel == null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            _mailboxViewModel.ExitSearchMode();
            if (_mailboxViewModel.SelectedFolder != null)
            {
                await _imapService.LoadFolderAsync(_mailboxViewModel.SelectedFolder.Id);
            }

            return;
        }

        await _imapService.SearchAsync(query, _searchMode, _searchSinceUtc);
    }

    private async void OnClearSearchRequested(object? sender, EventArgs e)
    {
        if (_mailboxViewModel == null)
        {
            return;
        }

        _mailboxViewModel.ExitSearchMode();
    }

    private async void OnRootLabelAddRequested(object? sender, EventArgs e) =>
        await PromptForLabelAsync(null);

    private async void OnChildLabelAddRequested(object? sender, int parentId) =>
        await PromptForLabelAsync(parentId);

    private void OnLabelSelected(object? sender, LabelViewModel label)
    {
        if (_imapService == null || _mailboxViewModel == null)
        {
            return;
        }

        _mailboxViewModel.SelectLabel(label.Id);
        _mailboxViewModel.SelectedFolder = null;
        _mailboxViewModel.ExitSearchMode();
        _ = _dashboardView?.ClearMessageContentAsync();
        _ = _dashboardView?.ClearAttachmentsAsync();
        _ = _imapService.LoadLabelMessagesAsync(label.Id, _searchSinceUtc);
    }

    private void OnLabelDropRequested(object? sender, LabelDropRequest e)
    {
        if (_imapService == null)
        {
            return;
        }

        var messageId = !string.IsNullOrWhiteSpace(e.MessageId)
            ? e.MessageId!
            : _mailboxViewModel?.SelectedMessage?.Id;

        var folderId = !string.IsNullOrWhiteSpace(e.FolderId)
            ? e.FolderId!
            : _mailboxViewModel?.SelectedFolder?.Id;

        if (string.IsNullOrWhiteSpace(messageId) || string.IsNullOrWhiteSpace(folderId))
        {
            return;
        }

        _ = _imapService.AssignLabelToMessageAsync(e.LabelId, folderId!, messageId!);
    }

    private async Task PromptForLabelAsync(int? parentId)
    {
        if (_imapService == null)
        {
            return;
        }

        var xamlRoot = GetXamlRoot();
        if (xamlRoot == null)
        {
            return;
        }

        var nameBox = new TextBox
        {
            PlaceholderText = parentId == null ? "Label name" : "Child label name",
            Width = 260
        };

        var dialog = new ContentDialog
        {
            Title = parentId == null ? "New label" : "New child label",
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Content = nameBox,
            XamlRoot = xamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        await _imapService.CreateLabelAsync(nameBox.Text, parentId);
    }

    private void ClearLabelSelection()
    {
        _mailboxViewModel?.SelectLabel(null);
        if (_dashboardView?.LabelsTreeControl.SelectedNodes.Count > 0)
        {
            _dashboardView.LabelsTreeControl.SelectedNodes.Clear();
        }
    }

    private static IEnumerable<LabelViewModel> FlattenLabels(IEnumerable<LabelViewModel> roots)
    {
        foreach (var l in roots)
        {
            yield return l;
            foreach (var child in FlattenLabels(l.Children))
            {
                yield return child;
            }
        }
    }

    private async void OnSuggestionAccepted(object? sender, EmailMessageViewModel message)
    {
        if (_imapService == null || _mailboxViewModel?.SelectedLabelId is not int labelId)
        {
            return;
        }

        var folderId = string.IsNullOrWhiteSpace(message.FolderId)
            ? _mailboxViewModel.SelectedFolder?.Id
            : message.FolderId;

        if (string.IsNullOrWhiteSpace(folderId))
        {
            return;
        }

        var success = await _imapService.AssignLabelToMessageAsync(labelId, folderId!, message.Id);
        if (success)
        {
            message.IsSuggested = false;
            message.SuggestionScore = Double.NegativeInfinity;
        }
    }

    private async void OnLabelSenderRequested(object? sender, EmailMessageViewModel message)
    {
        if (_imapService == null || _mailboxViewModel == null)
        {
            return;
        }

        var labels = FlattenLabels(_mailboxViewModel.Labels).ToList();
        if (labels.Count == 0)
        {
            return;
        }

        var xamlRoot = GetXamlRoot();
        if (xamlRoot == null)
        {
            return;
        }

        var combo = new ComboBox
        {
            ItemsSource = labels,
            DisplayMemberPath = "Name",
            SelectedItem = labels.FirstOrDefault(),
            Width = 260
        };

        var dialog = new ContentDialog
        {
            Title = $"Label sender: {message.SenderAddress}",
            Content = combo,
            PrimaryButtonText = "Apply",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary || combo.SelectedItem is not LabelViewModel selected)
        {
            return;
        }

        await _imapService.AddSenderLabelAsync(selected.Id, message.SenderAddress);
    }

    private static string? ExtractAddress(EmailMessageViewModel message)
    {
        var address = message.SenderAddress;
        if (!string.IsNullOrWhiteSpace(address))
        {
            return address;
        }

        var display = message.Sender;
        if (MailboxAddress.TryParse(display, out var mailbox) && !string.IsNullOrWhiteSpace(mailbox.Address))
        {
            return mailbox.Address;
        }

        var trimmed = display?.Trim();
        return trimmed != null && trimmed.Contains("@", StringComparison.Ordinal) ? trimmed : null;
    }

    private static string? BuildQuotedBody(EmailMessageViewModel message)
    {
        if (string.IsNullOrWhiteSpace(message.Preview))
        {
            return null;
        }

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine();
        sb.Append("On ");
        sb.Append(message.Received.ToString("g"));
        sb.Append(", ");
        sb.Append(message.Sender);
        sb.AppendLine(" wrote:");
        sb.AppendLine();
        sb.AppendLine(message.Preview);
        return sb.ToString();
    }

    private static string BuildAttachmentsHtml(IEnumerable<ImapSyncService.AttachmentContent> attachments)
    {
        var sb = new StringBuilder();
        sb.Append("""
<html><head><style>
body{font-family:'Segoe UI',sans-serif;background:#f7f7f7;color:#1a1a1a;margin:0;padding:12px;}
.item{margin-bottom:18px;padding:12px;background:#fff;border-radius:8px;box-shadow:0 2px 6px rgba(0,0,0,0.08);}
.meta{font-size:12px;color:#555;margin-bottom:8px;word-break:break-all;}
img{max-width:100%;height:auto;border-radius:6px;display:block;}
</style></head><body>
""");

        foreach (var att in attachments)
        {
            var fileName = WebUtility.HtmlEncode(att.FileName ?? "attachment");
            var disposition = WebUtility.HtmlEncode(att.Disposition ?? string.Empty);
            var size = att.SizeBytes > 0 ? $"{att.SizeBytes:N0} bytes" : string.Empty;
            var metaParts = new[]
            {
                fileName,
                att.ContentType,
                string.IsNullOrWhiteSpace(disposition) ? null : $"Disposition: {disposition}",
                size
            }.Where(p => !string.IsNullOrWhiteSpace(p));

            sb.Append("<div class=\"item\">");
            sb.Append("<div class=\"meta\">");
            sb.Append(string.Join(" · ", metaParts));
            sb.Append("</div>");
            sb.Append("<img src=\"data:");
            sb.Append(att.ContentType);
            sb.Append(";base64,");
            sb.Append(att.Base64Data);
            sb.Append("\" alt=\"");
            sb.Append(fileName);
            sb.Append("\" />");
            sb.Append("</div>");
        }

        sb.Append("</body></html>");
        return sb.ToString();
    }
}
