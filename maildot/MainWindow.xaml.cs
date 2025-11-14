using System;
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
            var storedSettings = AccountSettingsStore.Load();
            if (storedSettings is { HasServerInfo: true })
            {
                var credentialResponse = await CredentialManager.RequestPasswordAsync(storedSettings.Username);
                if (credentialResponse.Result == CredentialAccessResult.Success && !string.IsNullOrWhiteSpace(credentialResponse.Password))
                {
                    ShowDashboard(storedSettings, credentialResponse.Password);
                    return;
                }

                await ShowAccountSetup(storedSettings, GetStatusMessage(credentialResponse.Result));
                return;
            }

            await ShowAccountSetup(null, "Please enter your IMAP server details to begin.");
        }

        private async Task ShowAccountSetup(AccountSettings? existing, string? statusMessage = null)
        {
            await CleanupImapServiceAsync();

            _accountSetupView ??= new AccountSetupView();
            _accountSetupView.SettingsSaved -= OnAccountSettingsSaved;
            _accountSetupView.SettingsSaved += OnAccountSettingsSaved;
            _accountSetupView.Initialize(existing, statusMessage);
            RootContent.Content = _accountSetupView;
        }

        private void ShowDashboard(AccountSettings settings, string password)
        {
            _mailboxViewModel = new MailboxViewModel();
            _mailboxViewModel.SetAccountSummary($"Connected to {settings.Server} as {settings.Username}");

            _dashboardView ??= new ImapDashboardView();
            _dashboardView.RequestReauthentication -= OnDashboardReauthRequested;
            _dashboardView.RequestReauthentication += OnDashboardReauthRequested;
            _dashboardView.FolderSelected -= OnFolderSelected;
            _dashboardView.FolderSelected += OnFolderSelected;
            _dashboardView.LoadMoreRequested -= OnLoadMoreRequested;
            _dashboardView.LoadMoreRequested += OnLoadMoreRequested;
            _dashboardView.RetryRequested -= OnRetryRequested;
            _dashboardView.RetryRequested += OnRetryRequested;
            _dashboardView.BindViewModel(_mailboxViewModel);
            RootContent.Content = _dashboardView;

            _ = StartImapSyncAsync(settings, password);
        }

        private void OnAccountSettingsSaved(object? sender, AccountSetupResultEventArgs e)
        {
            AccountSettingsStore.Save(e.Settings);
            CredentialManager.SavePassword(e.Settings.Username, e.Password);
            ShowDashboard(e.Settings, e.Password);
        }

        private async void OnDashboardReauthRequested(object? sender, EventArgs e)
        {
            var stored = AccountSettingsStore.Load();
            await ShowAccountSetup(stored, "Please re-enter your password.");
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
            if (_imapService == null || _mailboxViewModel?.SelectedFolder is not MailFolderViewModel folder)
            {
                return;
            }

            _mailboxViewModel.SetRetryVisible(false);
            _ = _imapService.LoadFolderAsync(folder.Id);
        }

        private async Task CleanupImapServiceAsync()
        {
            if (_imapService != null)
            {
                await _imapService.DisposeAsync();
                _imapService = null;
            }
        }

        private static string? GetStatusMessage(CredentialAccessResult result) => result switch
        {
            CredentialAccessResult.ConsentDenied => "Windows Hello verification was cancelled.",
            CredentialAccessResult.MissingHello => "Windows Hello is unavailable; you need to re-enter your password.",
            CredentialAccessResult.NotFound => "Stored credentials are missing; please re-enter your password.",
            _ => null
        };
    }
}
