using System;
using System.Collections.Concurrent;
using maildot.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace maildot.Data;

public sealed class MailDbContextFactory : IDesignTimeDbContextFactory<MailDbContext>
{
    private static readonly ConcurrentDictionary<string, Npgsql.NpgsqlDataSource> DataSources = new();

    public MailDbContext CreateDbContext(string[] args)
    {
        var envConnection = Environment.GetEnvironmentVariable("MAILDOT_PG_CONNECTION");
        var connectionString = string.IsNullOrWhiteSpace(envConnection)
            ? "Host=localhost;Username=postgres;Password=postgres;Database=maildot;Maximum Pool Size=10"
            : AppendPoolLimit(envConnection);

        var builder = BuildOptions(connectionString);
        return new MailDbContext(builder.Options);
    }

    public static MailDbContext CreateDbContext(PostgresSettings settings, string password)
    {
        var connectionString = BuildConnectionString(settings, password);
        var builder = BuildOptions(connectionString);
        return new MailDbContext(builder.Options);
    }

    private static DbContextOptionsBuilder<MailDbContext> BuildOptions(string connectionString)
    {
        var dataSource = DataSources.GetOrAdd(connectionString, cs =>
        {
            var builder = new Npgsql.NpgsqlDataSourceBuilder(cs);
            builder.EnableDynamicJson();
            return builder.Build();
        });

        var builder = new DbContextOptionsBuilder<MailDbContext>();
        builder.UseNpgsql(dataSource, npgsql =>
        {
            npgsql.UseNodaTime();
            npgsql.UseVector();
        });
        builder.ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning));
        return builder;
    }

    private static string BuildConnectionString(PostgresSettings settings, string password)
    {
        var sslMode = settings.UseSsl ? "Require" : "Disable";
        var baseCs = $"Host={settings.Host};Port={settings.Port};Database={settings.Database};Username={settings.Username};Password={password};SSL Mode={sslMode}";
        return AppendPoolLimit(baseCs);
    }

    private static string AppendPoolLimit(string connectionString)
    {
        if (connectionString.IndexOf("Maximum Pool Size", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return connectionString;
        }

        var separator = connectionString.EndsWith(";") ? string.Empty : ";";
        return $"{connectionString}{separator}Maximum Pool Size=10";
    }
}
