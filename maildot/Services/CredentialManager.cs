using System;
using System.Linq;
using System.Threading.Tasks;
using maildot.Models;
using Windows.Security.Credentials;

namespace maildot.Services;

public enum CredentialAccessResult
{
    ConsentDenied,
    MissingHello,
    NotFound,
    Success
}

public sealed class CredentialAccessResponse
{
    public CredentialAccessResponse(CredentialAccessResult result, string? password)
    {
        Result = result;
        Password = password;
    }

    public CredentialAccessResult Result { get; }
    public string? Password { get; }
}

public static class CredentialManager
{
    public static void SavePassword(AccountSettings account, string password)
    {
        if (account == null) throw new ArgumentNullException(nameof(account));

        var resource = GetResourceName("IMAP", account.Server, account.Username);
        Save(resource, account.Username, password);
    }

    public static Task<CredentialAccessResponse> RequestPasswordAsync(AccountSettings account)
    {
        if (account == null) throw new ArgumentNullException(nameof(account));

        var resource = GetResourceName("IMAP", account.Server, account.Username);
        return RetrieveAsync(resource, account.Username);
    }

    public static void SavePostgresPassword(PostgresSettings settings, string password)
    {
        var resource = GetResourceName("PG", settings.Host, settings.Username);
        Save(resource, settings.Username, password);
    }

    public static Task<CredentialAccessResponse> RequestPostgresPasswordAsync(PostgresSettings settings)
    {
        var resource = GetResourceName("PG", settings.Host, settings.Username);
        return RetrieveAsync(resource, settings.Username);
    }

    private static void Save(string resource, string username, string password)
    {
        var vault = new PasswordVault();
        RemoveExisting(vault, resource, username);

        var credential = new PasswordCredential(resource, username, password);
        vault.Add(credential);
    }

    public static void RemovePassword(AccountSettings account)
    {
        var vault = new PasswordVault();
        var resource = GetResourceName("IMAP", account.Server, account.Username);
        RemoveExisting(vault, resource, account.Username);
    }

    private static Task<CredentialAccessResponse> RetrieveAsync(string resource, string username)
    {
        try
        {
            var vault = new PasswordVault();
            var credential = vault.Retrieve(resource, username);
            credential.RetrievePassword();
            return Task.FromResult(new CredentialAccessResponse(CredentialAccessResult.Success, credential.Password));
        }
        catch
        {
            return Task.FromResult(new CredentialAccessResponse(CredentialAccessResult.NotFound, null));
        }
    }

    private static void RemoveExisting(PasswordVault vault, string resourceName, string username)
    {
        try
        {
            var existing = vault.FindAllByResource(resourceName)
                .Where(c => string.Equals(c.UserName, username, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var credential in existing)
            {
                vault.Remove(credential);
            }
        }
        catch
        {
            // FindAllByResource throws if nothing is found.
        }
    }

    private static string GetResourceName(string prefix, string? host, string? username)
    {
        var safeHost = string.IsNullOrWhiteSpace(host) ? "unknown" : host.Trim();
        var safeUser = string.IsNullOrWhiteSpace(username) ? "user" : username.Trim();
        return $"{prefix}:{safeHost}:{safeUser}";
    }
}
