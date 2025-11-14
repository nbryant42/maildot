using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.Security.Credentials;
using Windows.Security.Credentials.UI;

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
    private const string ResourceName = "maildot.imap";

    public static void SavePassword(string username, string password)
    {
        var vault = new PasswordVault();
        RemoveExisting(vault, username);

        var credential = new PasswordCredential(ResourceName, username, password);
        vault.Add(credential);
    }

    public static async Task<CredentialAccessResponse> RequestPasswordAsync(string username)
    {
        var availability = await UserConsentVerifier.CheckAvailabilityAsync();
        if (availability != UserConsentVerifierAvailability.Available)
        {
            return new CredentialAccessResponse(CredentialAccessResult.MissingHello, null);
        }

        var verification = await UserConsentVerifier.RequestVerificationAsync("Unlock stored IMAP credentials for maildot.");
        if (verification != UserConsentVerificationResult.Verified)
        {
            return new CredentialAccessResponse(CredentialAccessResult.ConsentDenied, null);
        }

        try
        {
            var vault = new PasswordVault();
            var credential = vault.Retrieve(ResourceName, username);
            credential.RetrievePassword();
            return new CredentialAccessResponse(CredentialAccessResult.Success, credential.Password);
        }
        catch
        {
            return new CredentialAccessResponse(CredentialAccessResult.NotFound, null);
        }
    }

    private static void RemoveExisting(PasswordVault vault, string username)
    {
        try
        {
            var existing = vault.FindAllByResource(ResourceName)
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

}
