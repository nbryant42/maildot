using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using maildot.Data;
using maildot.Services;
using Microsoft.EntityFrameworkCore;
using System.IO;
using System.Text.Json;

namespace maildot.Models;

/// <summary>
/// Captures the IMAP endpoint details needed to connect, excluding sensitive secrets.
/// </summary>
public sealed class AccountSettings : INotifyPropertyChanged
{
    private string _deleteTargetFolderFullName = string.Empty;
    private string _deleteTargetStatus = string.Empty;

    public int Id { get; set; }
    public string AccountName { get; set; } = string.Empty;
    public string Server { get; set; } = string.Empty;
    public int Port { get; set; } = 993;
    public bool UseSsl { get; set; } = true;
    public string Username { get; set; } = string.Empty;
    public string DeleteTargetFolderFullName
    {
        get => _deleteTargetFolderFullName;
        set => SetProperty(ref _deleteTargetFolderFullName, value ?? string.Empty);
    }
    [JsonIgnore]
    public string DeleteTargetStatus
    {
        get => _deleteTargetStatus;
        set => SetProperty(ref _deleteTargetStatus, value ?? string.Empty);
    }
    [JsonIgnore]
    public bool IsActive { get; set; }

    public bool HasServerInfo =>
        !string.IsNullOrWhiteSpace(Server) &&
        !string.IsNullOrWhiteSpace(Username) &&
        Port > 0;

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

internal static class AccountSettingsStore
{
    private const string ActiveAccountKey = "ActiveAccountId";
    private static readonly string ActiveAccountPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "maildot",
            "active_account.txt");
    private static readonly string DeleteTargetsPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "maildot",
            "delete_targets.json");

    public static IReadOnlyList<AccountSettings> GetAllAccounts()
    {
        using var db = ResolveDbContext();
        var activeId = GetActiveAccountId();
        var deleteTargets = LoadDeleteTargets();

        return db.ImapAccounts
            .AsNoTracking()
            .OrderBy(a => a.DisplayName)
            .ThenBy(a => a.Username)
            .Select(a => MapToSettings(a, deleteTargets))
            .ToList()
            .Select(a =>
            {
                a.IsActive = activeId == a.Id;
                return a;
            })
            .ToList();
    }

    public static AccountSettings? GetActiveAccount()
    {
        using var db = ResolveDbContext();
        var activeId = GetActiveAccountId();
        var deleteTargets = LoadDeleteTargets();
        var query = db.ImapAccounts.AsNoTracking();
        if (activeId.HasValue)
        {
            var match = query.FirstOrDefault(a => a.Id == activeId.Value);
            if (match != null)
            {
                var mapped = MapToSettings(match, deleteTargets);
                mapped.IsActive = true;
                return mapped;
            }
        }

        var first = query.OrderBy(a => a.DisplayName).ThenBy(a => a.Username).FirstOrDefault();
        return first != null ? MapToSettings(first, deleteTargets) : null;
    }

    public static void AddOrUpdate(AccountSettings settings, bool makeActive = true)
    {
        using var db = ResolveDbContext();
        ImapAccount entity;
        if (settings.Id > 0)
        {
            entity = db.ImapAccounts.First(a => a.Id == settings.Id);
            Apply(settings, entity);
        }
        else
        {
            entity = new ImapAccount();
            Apply(settings, entity);
            db.ImapAccounts.Add(entity);
        }

        db.SaveChanges();
        settings.Id = entity.Id;
        SaveDeleteTarget(settings.Id, settings.DeleteTargetFolderFullName);

        if (makeActive)
        {
            SetActiveAccount(entity.Id);
        }
    }

    public static void SetActiveAccount(int accountId)
    {
        try
        {
            var directory = Path.GetDirectoryName(ActiveAccountPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(ActiveAccountPath, accountId.ToString());
        }
        catch
        {
            // Ignore failures; active account will be resolved on next launch.
        }
    }

    public static void RemoveAccount(int accountId)
    {
        using var db = ResolveDbContext();
        var entity = db.ImapAccounts.FirstOrDefault(a => a.Id == accountId);
        if (entity == null)
        {
            return;
        }

        db.ImapAccounts.Remove(entity);
        db.SaveChanges();
        RemoveDeleteTarget(accountId);

        var activeId = GetActiveAccountId();
        if (activeId == accountId)
        {
            var next = db.ImapAccounts.AsNoTracking()
                .OrderBy(a => a.DisplayName)
                .ThenBy(a => a.Username)
                .FirstOrDefault();
            SetActiveAccount(next?.Id ?? 0);
        }
    }

    private static MailDbContext ResolveDbContext()
    {
        var settings = PostgresSettingsStore.Load();
        var passwordResponse = CredentialManager.RequestPostgresPasswordAsync(settings)
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();

        if (passwordResponse.Result != CredentialAccessResult.Success || string.IsNullOrWhiteSpace(passwordResponse.Password))
        {
            throw new InvalidOperationException("PostgreSQL credentials are missing.");
        }

        return MailDbContextFactory.CreateDbContext(settings, passwordResponse.Password);
    }

    private static AccountSettings MapToSettings(ImapAccount entity, IReadOnlyDictionary<int, string> deleteTargets) =>
        new()
        {
            Id = entity.Id,
            AccountName = entity.DisplayName,
            Server = entity.Server,
            Port = entity.Port,
            UseSsl = entity.UseSsl,
            Username = entity.Username,
            DeleteTargetFolderFullName = deleteTargets.TryGetValue(entity.Id, out var target) ? target : string.Empty
        };

    private static void Apply(AccountSettings settings, ImapAccount entity)
    {
        entity.DisplayName = settings.AccountName;
        entity.Server = settings.Server;
        entity.Port = settings.Port;
        entity.UseSsl = settings.UseSsl;
        entity.Username = settings.Username;
    }

    private static int? GetActiveAccountId()
    {
        try
        {
            if (File.Exists(ActiveAccountPath))
            {
                var text = File.ReadAllText(ActiveAccountPath).Trim();
                if (int.TryParse(text, out var parsed) && parsed > 0)
                {
                    return parsed;
                }
            }
        }
        catch
        {
            // Ignore and fall back to first account.
        }

        return null;
    }

    private static Dictionary<int, string> LoadDeleteTargets()
    {
        try
        {
            if (!File.Exists(DeleteTargetsPath))
            {
                return new Dictionary<int, string>();
            }

            var json = File.ReadAllText(DeleteTargetsPath);
            var parsed = JsonSerializer.Deserialize<Dictionary<int, string>>(json);
            return parsed ?? new Dictionary<int, string>();
        }
        catch
        {
            return new Dictionary<int, string>();
        }
    }

    private static void SaveDeleteTargets(Dictionary<int, string> targets)
    {
        try
        {
            var directory = Path.GetDirectoryName(DeleteTargetsPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(targets);
            File.WriteAllText(DeleteTargetsPath, json);
        }
        catch
        {
            // Ignore failures; settings can be re-entered.
        }
    }

    private static void SaveDeleteTarget(int accountId, string? folderFullName)
    {
        var targets = LoadDeleteTargets();
        var cleaned = folderFullName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            targets.Remove(accountId);
        }
        else
        {
            targets[accountId] = cleaned;
        }

        SaveDeleteTargets(targets);
    }

    private static void RemoveDeleteTarget(int accountId)
    {
        var targets = LoadDeleteTargets();
        if (targets.Remove(accountId))
        {
            SaveDeleteTargets(targets);
        }
    }
}
