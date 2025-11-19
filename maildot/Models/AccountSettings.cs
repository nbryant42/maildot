using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace maildot.Models;

/// <summary>
/// Captures the IMAP endpoint details needed to connect, excluding sensitive secrets.
/// </summary>
public sealed class AccountSettings
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string AccountName { get; set; } = string.Empty;
    public string Server { get; set; } = string.Empty;
    public int Port { get; set; } = 993;
    public bool UseSsl { get; set; } = true;
    public string Username { get; set; } = string.Empty;
    [JsonIgnore]
    public bool IsActive { get; set; }

    public bool HasServerInfo =>
        !string.IsNullOrWhiteSpace(Server) &&
        !string.IsNullOrWhiteSpace(Username) &&
        Port > 0;
}

internal sealed class AccountStoreModel
{
    public Guid? ActiveAccountId { get; set; }
    public List<AccountSettings> Accounts { get; set; } = new();
}

internal static class AccountSettingsStore
{
    private static readonly string SettingsPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "maildot",
            "accounts.json");

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    public static IReadOnlyList<AccountSettings> GetAllAccounts()
    {
        var store = LoadStore();
        NormalizeStore(store);
        return store.Accounts
            .OrderBy(a => a.AccountName)
            .ThenBy(a => a.Username)
            .ToList();
    }

    public static AccountSettings? GetActiveAccount()
    {
        var store = LoadStore();
        NormalizeStore(store);
        if (store.ActiveAccountId is Guid id)
        {
            return store.Accounts.FirstOrDefault(a => a.Id == id);
        }

        return store.Accounts.FirstOrDefault();
    }

    public static void AddOrUpdate(AccountSettings settings, bool makeActive = true)
    {
        var store = LoadStore();
        NormalizeStore(store);
        var existingIndex = store.Accounts.FindIndex(a => a.Id == settings.Id);
        if (existingIndex >= 0)
        {
            store.Accounts[existingIndex] = settings;
        }
        else
        {
            store.Accounts.Add(settings);
        }

        if (makeActive)
        {
            store.ActiveAccountId = settings.Id;
        }

        NormalizeStore(store);
        SaveStore(store);
    }

    public static void SetActiveAccount(Guid accountId)
    {
        var store = LoadStore();
        if (store.Accounts.Any(a => a.Id == accountId))
        {
            store.ActiveAccountId = accountId;
            NormalizeStore(store);
            SaveStore(store);
        }
    }

    public static void RemoveAccount(Guid accountId)
    {
        var store = LoadStore();
        var removed = store.Accounts.RemoveAll(a => a.Id == accountId);
        if (removed > 0)
        {
            if (store.ActiveAccountId == accountId)
            {
                store.ActiveAccountId = store.Accounts.FirstOrDefault()?.Id;
            }

            NormalizeStore(store);
            SaveStore(store);
        }
    }

    private static AccountStoreModel LoadStore()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new AccountStoreModel();
            }

            var json = File.ReadAllText(SettingsPath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new AccountStoreModel();
            }

            var store = JsonSerializer.Deserialize<AccountStoreModel>(json);
            if (store != null)
            {
                NormalizeStore(store);
                return store;
            }

            // Migration path from single-account file.
            var legacy = JsonSerializer.Deserialize<AccountSettings>(json);
            if (legacy != null)
            {
                var migrated = new AccountStoreModel
                {
                    Accounts = new List<AccountSettings> { legacy },
                    ActiveAccountId = legacy.Id
                };
                NormalizeStore(migrated);
                return migrated;
            }
        }
        catch
        {
            // Ignore to force reconfiguration.
        }

        var fallback = new AccountStoreModel();
        NormalizeStore(fallback);
        return fallback;
    }

    private static void SaveStore(AccountStoreModel store)
    {
        var directory = Path.GetDirectoryName(SettingsPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(store, SerializerOptions);
        File.WriteAllText(SettingsPath, json);
    }

    private static void NormalizeStore(AccountStoreModel store)
    {
        foreach (var account in store.Accounts)
        {
            account.IsActive = store.ActiveAccountId == account.Id;
        }
    }
}
