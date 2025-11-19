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

internal static class CredentialManager
{
    public static void SavePassword(AccountSettings account, string password)
    {
        if (account == null) throw new ArgumentNullException(nameof(account));

        var resource = GetResourceName(account);
        var vault = new PasswordVault();
        RemoveExisting(vault, resource, account.Username);

        var credential = new PasswordCredential(resource, account.Username, password);
        vault.Add(credential);
    }

    public static Task<CredentialAccessResponse> RequestPasswordAsync(AccountSettings account)
    {
        if (account == null) throw new ArgumentNullException(nameof(account));

        var resource = GetResourceName(account);
        try
        {
            var vault = new PasswordVault();
            var credential = vault.Retrieve(resource, account.Username);
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

    private static string GetResourceName(AccountSettings account)
    {
        var host = account.Server?.Trim() ?? "unknown";
        var username = account.Username?.Trim() ?? "user";
        return $"IMAP:{host}:{username}";
    }
}
