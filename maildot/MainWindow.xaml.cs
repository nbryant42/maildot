using System;
using System.Threading.Tasks;
using maildot.Models;
using maildot.Services;
using maildot.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace maildot
{
    public sealed partial class MainWindow : Window
    {
        private AccountSetupView? _accountSetupView;
        private ImapDashboardView? _dashboardView;
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
                    ShowDashboard(storedSettings);
                    return;
                }

                ShowAccountSetup(storedSettings, GetStatusMessage(credentialResponse.Result));
                return;
            }

            ShowAccountSetup(null, "Please enter your IMAP server details to begin.");
        }

        private void ShowAccountSetup(AccountSettings? existing, string? statusMessage = null)
        {
            _accountSetupView ??= new AccountSetupView();
            _accountSetupView.SettingsSaved -= OnAccountSettingsSaved;
            _accountSetupView.SettingsSaved += OnAccountSettingsSaved;
            _accountSetupView.Initialize(existing, statusMessage);
            RootContent.Content = _accountSetupView;
        }

        private void ShowDashboard(AccountSettings settings)
        {
            _dashboardView ??= new ImapDashboardView();
            _dashboardView.RequestReauthentication -= OnDashboardReauthRequested;
            _dashboardView.RequestReauthentication += OnDashboardReauthRequested;
            _dashboardView.UpdateAccountInfo(settings);
            RootContent.Content = _dashboardView;
        }

        private void OnAccountSettingsSaved(object? sender, AccountSetupResultEventArgs e)
        {
            AccountSettingsStore.Save(e.Settings);
            CredentialManager.SavePassword(e.Settings.Username, e.Password);
            ShowDashboard(e.Settings);
        }

        private void OnDashboardReauthRequested(object? sender, EventArgs e)
        {
            var stored = AccountSettingsStore.Load();
            ShowAccountSetup(stored, "Windows Hello verification failed or expired; please re-enter your password.");
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
