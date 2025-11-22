using System;
using maildot.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace maildot.Data;

public sealed class MailDbContextFactory : IDesignTimeDbContextFactory<MailDbContext>
{
    public MailDbContext CreateDbContext(string[] args)
    {
        var envConnection = Environment.GetEnvironmentVariable("MAILDOT_PG_CONNECTION");
        var connectionString = string.IsNullOrWhiteSpace(envConnection)
            ? "Host=localhost;Username=postgres;Password=postgres;Database=maildot"
            : envConnection;

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
        var dataSourceBuilder = new Npgsql.NpgsqlDataSourceBuilder(connectionString);
        dataSourceBuilder.EnableDynamicJson();
        var dataSource = dataSourceBuilder.Build();

        var builder = new DbContextOptionsBuilder<MailDbContext>();
        builder.UseNpgsql(dataSource, npgsql =>
        {
            npgsql.UseNodaTime();
            npgsql.UseVector();
        });
        return builder;
    }

    private static string BuildConnectionString(PostgresSettings settings, string password)
    {
        var sslMode = settings.UseSsl ? "Require" : "Disable";
        return $"Host={settings.Host};Port={settings.Port};Database={settings.Database};Username={settings.Username};Password={password};SSL Mode={sslMode}";
    }
}
