using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Jellyfin.Database.Implementations.DbConfiguration;
using Npgsql;

namespace Jellyfin.Plugin.Postgresql.Database;

/// <summary>
/// Resolves the PostgreSQL connection from Jellyfin's database configuration.
/// </summary>
internal static class PostgresqlConnectionSettings
{
    /// <summary>
    /// Builds the connection from <c>database.xml</c>, falling back to <c>POSTGRES_*</c>
    /// environment variables when no connection string is configured.
    /// </summary>
    /// <param name="databaseConfiguration">Jellyfin's database configuration.</param>
    /// <returns>A populated connection string builder.</returns>
    public static NpgsqlConnectionStringBuilder Resolve(DatabaseConfigurationOptions databaseConfiguration)
    {
        ArgumentNullException.ThrowIfNull(databaseConfiguration);

        var customProviderOptions = databaseConfiguration.CustomProviderOptions;
        var configuredConnectionString = customProviderOptions?.ConnectionString;

        var builder = string.IsNullOrWhiteSpace(configuredConnectionString)
            ? FromEnvironment()
            : new NpgsqlConnectionStringBuilder(configuredConnectionString);

        ApplyOptions(builder, customProviderOptions?.Options);

        if (string.IsNullOrEmpty(builder.Password))
        {
            throw new InvalidOperationException(
                "No PostgreSQL password was configured. Set ConnectionString in database.xml, or set the POSTGRES_PASSWORD environment variable.");
        }

        builder.ApplicationName ??= BuildApplicationName();

        return builder;
    }

    /// <summary>
    /// Returns the connection string with the password removed, for logging.
    /// </summary>
    /// <param name="builder">The connection to redact.</param>
    /// <returns>A connection string safe to write to the log.</returns>
    public static string Redact(NpgsqlConnectionStringBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return new NpgsqlConnectionStringBuilder(builder.ToString())
        {
            Password = null
        }.ToString();
    }

    private static NpgsqlConnectionStringBuilder FromEnvironment()
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = Environment.GetEnvironmentVariable("POSTGRES_HOST") ?? "localhost",
            Database = Environment.GetEnvironmentVariable("POSTGRES_DB") ?? "jellyfin",
            Username = Environment.GetEnvironmentVariable("POSTGRES_USER") ?? "jellyfin",
            Password = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD")
        };

        var port = Environment.GetEnvironmentVariable("POSTGRES_PORT");
        if (!string.IsNullOrWhiteSpace(port))
        {
            builder.Port = int.Parse(port, CultureInfo.InvariantCulture);
        }

        var sslMode = Environment.GetEnvironmentVariable("POSTGRES_SSLMODE");
        if (!string.IsNullOrWhiteSpace(sslMode))
        {
            builder.SslMode = Enum.Parse<SslMode>(sslMode, true);
        }

        // Npgsql 8 folded certificate trust into SslMode: Require encrypts without validating,
        // VerifyCA and VerifyFull validate. There is no separate TrustServerCertificate any more.
        var commandTimeout = Environment.GetEnvironmentVariable("POSTGRES_COMMAND_TIMEOUT");
        if (!string.IsNullOrWhiteSpace(commandTimeout))
        {
            builder.CommandTimeout = int.Parse(commandTimeout, CultureInfo.InvariantCulture);
        }

        return builder;
    }

    private static void ApplyOptions(NpgsqlConnectionStringBuilder builder, ICollection<CustomDatabaseOption>? options)
    {
        if (options is null)
        {
            return;
        }

        // Anything Npgsql understands can be set as a database.xml option, so operators can tune
        // pooling or timeouts without us having to surface each key individually.
        foreach (var option in options)
        {
            builder[option.Key] = option.Value;
        }
    }

    private static string BuildApplicationName()
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString();

        return version is null ? "jellyfin" : $"jellyfin+{version}";
    }
}
