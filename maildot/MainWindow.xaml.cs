using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using maildot.Models;
using maildot.Services;
using maildot.Views;
using maildot.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace maildot
{
    public sealed partial class MainWindow : Window
    {
        private AccountSetupView? _accountSetupView;
        private ImapDashboardView? _dashboardView;
        private MailboxViewModel? _mailboxViewModel;
        private ImapSyncService? _imapService;
        private readonly List<AccountSettings> _accounts = new();
        private AccountSettings? _activeAccount;
        private bool _startupInitialized;

        public MainWindow()
        {
            InitializeComponent();
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
                XamlRoot = RootContent.XamlRoot
            };

            var result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary ? passwordBox.Password : null;
        }

        private void ShowDashboard(AccountSettings settings, string password)
        {
            _activeAccount = settings;
            EnsureDashboard();
            _ = _dashboardView!.ClearMessageContentAsync();
            _mailboxViewModel!.SetAccountSummary($"{settings.AccountName} ({settings.Username})");
            _ = StartImapSyncAsync(settings, password);
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

            _dashboardView?.ClearMessageContentAsync();
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
            await ShowSettingsDialogAsync();
        }

        private async Task ShowSettingsDialogAsync()
        {
            if (_accounts.Count == 0)
            {
                await ShowAccountSetup(null);
                return;
            }

            var settingsView = new SettingsView();
            settingsView.Initialize(_accounts, _activeAccount?.Id);

            var dialog = new ContentDialog
            {
                Title = "Settings",
                Content = settingsView,
                PrimaryButtonText = "Close",
                XamlRoot = RootContent.XamlRoot
            };

            settingsView.AddAccountRequested += async (_, __) =>
            {
                dialog.Hide();
                await ShowAccountSetup(null);
            };

            settingsView.SetActiveAccountRequested += async (_, id) =>
            {
                dialog.Hide();
                await SwitchActiveAccountAsync(id);
            };

            settingsView.ReenterPasswordRequested += async (_, id) =>
            {
                dialog.Hide();
                await ReenterPasswordAsync(id);
            };

            await dialog.ShowAsync();
        }

        private async Task SwitchActiveAccountAsync(Guid accountId)
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

        private async Task ReenterPasswordAsync(Guid accountId)
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
            if (_imapService == null || _mailboxViewModel?.SelectedFolder == null)
            {
                return;
            }

            _ = LoadAndDisplayMessageAsync(_mailboxViewModel.SelectedFolder.Id, e.Id);
        }

        private async Task LoadAndDisplayMessageAsync(string folderId, string messageId)
        {
            if (_imapService == null || _dashboardView == null)
            {
                return;
            }

            var html = await _imapService.LoadMessageBodyAsync(folderId, messageId);
            if (html != null)
            {
                await _dashboardView.DisplayMessageContentAsync(html);
            }
            else
            {
                await _dashboardView.ClearMessageContentAsync();
            }
        }
    }
}
