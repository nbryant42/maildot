using System;
using System.IO;
using System.Text.Json;

namespace maildot.Models;

/// <summary>
/// Captures the IMAP endpoint details needed to connect, excluding sensitive secrets.
/// </summary>
public sealed class AccountSettings
{
    public string Server { get; set; } = string.Empty;
    public int Port { get; set; } = 993;
    public bool UseSsl { get; set; } = true;
    public string Username { get; set; } = string.Empty;

    public bool HasServerInfo =>
        !string.IsNullOrWhiteSpace(Server) &&
        !string.IsNullOrWhiteSpace(Username) &&
        Port > 0;
}

internal static class AccountSettingsStore
{
    private static readonly string SettingsPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "maildot",
            "account.json");

    public static AccountSettings? Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return null;
            }

            var json = File.ReadAllText(SettingsPath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            return JsonSerializer.Deserialize<AccountSettings>(json);
        }
        catch
        {
            // Ignore to force reconfiguration.
            return null;
        }
    }

    public static void Save(AccountSettings settings)
    {
        var directory = Path.GetDirectoryName(SettingsPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(settings);
        File.WriteAllText(SettingsPath, json);
    }

    public static void Clear()
    {
        if (File.Exists(SettingsPath))
        {
            File.Delete(SettingsPath);
        }
    }
}
