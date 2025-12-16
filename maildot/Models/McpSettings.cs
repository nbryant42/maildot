using System;
using System.IO;
using System.Text.Json;

namespace maildot.Models;

public sealed class McpSettings
{
    public bool Enabled { get; set; }
    public string BindAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 3001;
}

public static class McpSettingsStore
{
    private static readonly string SettingsPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "maildot",
            "mcp.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true
    };

    public static McpSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new McpSettings();
            }

            var json = File.ReadAllText(SettingsPath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new McpSettings();
            }

            return JsonSerializer.Deserialize<McpSettings>(json) ?? new McpSettings();
        }
        catch
        {
            return new McpSettings();
        }
    }

    public static void Save(McpSettings settings)
    {
        var directory = Path.GetDirectoryName(SettingsPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, Options));
    }
}
