using System;
using System.IO;
using System.Text.Json;

namespace maildot.Models;

public sealed class PostgresSettings
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5432;
    public string Database { get; set; } = "maildot";
    public string Username { get; set; } = string.Empty;
    public bool UseSsl { get; set; } = true;

    public bool HasCredentials =>
        !string.IsNullOrWhiteSpace(Host) &&
        !string.IsNullOrWhiteSpace(Database) &&
        !string.IsNullOrWhiteSpace(Username);
}

public static class PostgresSettingsStore
{
    private static readonly string SettingsPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "maildot",
            "postgres.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true
    };

    public static PostgresSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new PostgresSettings();
            }

            var json = File.ReadAllText(SettingsPath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new PostgresSettings();
            }

            return JsonSerializer.Deserialize<PostgresSettings>(json) ?? new PostgresSettings();
        }
        catch
        {
            return new PostgresSettings();
        }
    }

    public static void Save(PostgresSettings settings)
    {
        var directory = Path.GetDirectoryName(SettingsPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, Options));
    }
}
