using maildot.Data;
using maildot.Models;
using maildot.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.UI.Xaml;
using System;
using System.Text;

namespace maildot;

public enum PostgresMigrationState
{
    NotStarted,
    MissingSettings,
    MissingPassword,
    Failed,
    Success
}

public partial class App : Application
{
    private Window? _window;

    public static PostgresMigrationState PostgresState { get; private set; } = PostgresMigrationState.NotStarted;
    public static string? PostgresError { get; private set; }

    public App()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        ApplyPendingMigrations();
        _window.Activate();
    }

    public static PostgresMigrationState ApplyPendingMigrations()
    {
        var settings = PostgresSettingsStore.Load();
        if (!settings.HasCredentials)
        {
            PostgresState = PostgresMigrationState.MissingSettings;
            PostgresError = "PostgreSQL settings are incomplete.";
            return PostgresState;
        }

        var passwordResponse = CredentialManager.RequestPostgresPasswordAsync(settings)
            .GetAwaiter()
            .GetResult();
        if (passwordResponse.Result != CredentialAccessResult.Success || string.IsNullOrWhiteSpace(passwordResponse.Password))
        {
            PostgresState = PostgresMigrationState.MissingPassword;
            PostgresError = "PostgreSQL password not found. Please re-enter it in Settings.";
            return PostgresState;
        }

        try
        {
            using var db = MailDbContextFactory.CreateDbContext(settings, passwordResponse.Password);
            db.Database.Migrate();
            PostgresState = PostgresMigrationState.Success;
            PostgresError = null;
        }
        catch (Exception ex)
        {
            PostgresState = PostgresMigrationState.Failed;
            PostgresError = ex.Message;
        }

        return PostgresState;
    }
}
