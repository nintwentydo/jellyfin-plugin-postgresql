using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using MediaBrowser.Common.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Update;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Jellyfin.Plugin.Postgresql.Database;

/// <summary>
/// Configures Jellyfin to use a PostgreSQL database.
/// </summary>
[JellyfinDatabaseProviderKey("Jellyfin-PostgreSQL")]
public sealed partial class PostgresqlDatabaseProvider : IJellyfinDatabaseProvider
{
    private const string BackupFolderName = "PostgresqlBackups";

    private readonly IApplicationPaths _applicationPaths;
    private readonly ILogger<PostgresqlDatabaseProvider> _logger;

    private NpgsqlConnectionStringBuilder? _connection;

    /// <summary>
    /// Initializes a new instance of the <see cref="PostgresqlDatabaseProvider"/> class.
    /// </summary>
    /// <param name="applicationPaths">Provides the data directory that backups are written to.</param>
    /// <param name="logger">A logger.</param>
    public PostgresqlDatabaseProvider(IApplicationPaths applicationPaths, ILogger<PostgresqlDatabaseProvider> logger)
    {
        _applicationPaths = applicationPaths;
        _logger = logger;
    }

    /// <inheritdoc />
    public IDbContextFactory<JellyfinDbContext>? DbContextFactory { get; set; }

    /// <inheritdoc />
    public void Initialise(DbContextOptionsBuilder options, DatabaseConfigurationOptions databaseConfiguration)
    {
        ArgumentNullException.ThrowIfNull(options);

        _connection = PostgresqlConnectionSettings.Resolve(databaseConfiguration);

        var redactedConnection = PostgresqlConnectionSettings.Redact(_connection);
        LogConnection(redactedConnection);

        options
            .UseNpgsql(
                _connection.ToString(),
                npgsqlOptions => npgsqlOptions.MigrationsAssembly(GetType().Assembly.FullName))
            .ReplaceService<IQuerySqlGeneratorFactory, PostgresqlQuerySqlGeneratorFactory>()
            .ReplaceService<IModelCustomizer, PostgresqlModelCustomizer>()
            // Core uses AsSplitQuery where it wants it and its own SQLite provider silences this
            // advisory for the rest; without it PostgreSQL users get four warnings per start.
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.MultipleCollectionIncludeWarning))
            .ReplaceService<IQueryTranslationPreprocessorFactory, CaseInsensitiveLikeQueryTranslationPreprocessorFactory>()
            .AddInterceptors(new WriteSerialisingTransactionInterceptor())
            .ReplaceService<IUpdateSqlGenerator, PostgresqlUpdateSqlGenerator>();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Jellyfin calls this before the entity model is fully configured, so the PostgreSQL mapping
    /// is applied from <see cref="PostgresqlModelCustomizer"/> instead.
    /// </remarks>
    public void OnModelCreating(ModelBuilder modelBuilder)
    {
    }

    /// <inheritdoc />
    public void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
    }

    /// <inheritdoc />
    public async Task RunScheduledOptimisation(CancellationToken cancellationToken)
    {
        if (DbContextFactory is null)
        {
            return;
        }

        var context = await DbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            // Autovacuum handles reclamation; this refreshes planner statistics after a large
            // library scan, which is when Jellyfin's query plans go stale. Named tables only: a
            // bare VACUUM also visits the shared catalogs, which only a superuser may vacuum, and
            // PostgreSQL logs a warning for each one it skips.
            var tables = context.Model.GetEntityTypes()
                .Select(entityType => entityType.GetSchemaQualifiedTableName())
                .OfType<string>()
                .Distinct()
                .Select(QuoteTable);
            var sql = $"VACUUM ANALYZE {string.Join(", ", tables)}";

#pragma warning disable EF1002 // Table names come from the EF model, not from user input.
            await context.Database.ExecuteSqlRawAsync(sql, cancellationToken).ConfigureAwait(false);
#pragma warning restore EF1002
        }

        LogOptimised();
    }

    /// <inheritdoc />
    public Task RunShutdownTask(CancellationToken cancellationToken)
    {
        NpgsqlConnection.ClearAllPools();

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<string> MigrationBackupFast(CancellationToken cancellationToken)
    {
        var connection = RequireConnection();
        var key = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var backupFile = GetBackupPath(key);

        Directory.CreateDirectory(Path.GetDirectoryName(backupFile)!);

        LogBackupStarting(backupFile);

        await RunPostgresToolAsync(
            "pg_dump",
            $"--host={connection.Host} --port={connection.Port} --username={connection.Username} --dbname={connection.Database} --file=\"{backupFile}\" --no-password --clean --if-exists",
            connection.Password!,
            cancellationToken).ConfigureAwait(false);

        LogBackupComplete();

        return key;
    }

    /// <inheritdoc />
    public async Task RestoreBackupFast(string key, CancellationToken cancellationToken)
    {
        var connection = RequireConnection();
        var backupFile = GetBackupPath(key);

        if (!File.Exists(backupFile))
        {
            LogBackupMissingForRestore(key);
            return;
        }

        NpgsqlConnection.ClearAllPools();

        LogRestoreStarting(backupFile);

        await RunPostgresToolAsync(
            "psql",
            $"--host={connection.Host} --port={connection.Port} --username={connection.Username} --dbname={connection.Database} --file=\"{backupFile}\" --no-password --quiet --set=ON_ERROR_STOP=1",
            connection.Password!,
            cancellationToken).ConfigureAwait(false);

        LogRestoreComplete();
    }

    /// <inheritdoc />
    public Task DeleteBackup(string key)
    {
        var backupFile = GetBackupPath(key);

        if (!File.Exists(backupFile))
        {
            LogBackupMissingForDelete(key);
            return Task.CompletedTask;
        }

        File.Delete(backupFile);
        LogBackupDeleted(key);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task PurgeDatabase(JellyfinDbContext dbContext, IEnumerable<string>? tableNames)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(tableNames);

        var quoted = tableNames.Select(QuoteTable).ToList();

        if (quoted.Count == 0)
        {
            return;
        }

        // One TRUNCATE over every table: CASCADE settles the foreign keys between them, which
        // avoids needing a delete order or disabling constraint checks.
        var sql = $"TRUNCATE TABLE {string.Join(", ", quoted)} RESTART IDENTITY CASCADE;";

#pragma warning disable EF1002 // Table names come from the EF model, not from user input.
        await dbContext.Database.ExecuteSqlRawAsync(sql).ConfigureAwait(false);
#pragma warning restore EF1002

        LogPurged(quoted.Count);
    }

    /// <summary>
    /// Quotes a table name for raw SQL.
    /// </summary>
    /// <param name="tableName">A bare or schema-qualified table name from the EF model.</param>
    /// <returns>The name with each dot-separated part double-quoted.</returns>
    /// <remarks>
    /// <c>BackupService</c> passes <c>GetSchemaQualifiedTableName()</c>, so a schema, should core
    /// ever set one, arrives as one dotted string. Quoting the parts keeps the dot a separator.
    /// </remarks>
    internal static string QuoteTable(string tableName)
        => string.Join('.', tableName
            .Split('.', 2)
            .Select(part => $"\"{part.Replace("\"", "\"\"", StringComparison.Ordinal)}\""));

    private NpgsqlConnectionStringBuilder RequireConnection()
        => _connection ?? throw new InvalidOperationException(
            "The PostgreSQL provider has not been initialised, so no connection is available.");

    private string GetBackupPath(string key)
        => Path.Join(_applicationPaths.DataPath, BackupFolderName, $"{key}_jellyfin.sql");

    private async Task RunPostgresToolAsync(
        string fileName,
        string arguments,
        string password,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                Environment = { ["PGPASSWORD"] = password },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not run '{fileName}'. The PostgreSQL client tools must be on PATH for Jellyfin to back up "
                + "the database before a schema migration. Install the postgresql-client package for your platform.",
                ex);
        }

        // Both pipes must be drained while the tool runs: neither is large in the happy path, but
        // a redirected pipe that fills up blocks the child process forever.
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await standardOutput.ConfigureAwait(false);
        var error = await standardError.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            LogToolFailed(fileName, process.ExitCode, error);

            throw new InvalidOperationException($"{fileName} failed with exit code {process.ExitCode}: {error}");
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "PostgreSQL connection: {ConnectionString}")]
    private partial void LogConnection(string connectionString);

    [LoggerMessage(Level = LogLevel.Information, Message = "PostgreSQL database optimised")]
    private partial void LogOptimised();

    [LoggerMessage(Level = LogLevel.Information, Message = "Backing up PostgreSQL database to {BackupFile}")]
    private partial void LogBackupStarting(string backupFile);

    [LoggerMessage(Level = LogLevel.Information, Message = "PostgreSQL backup complete")]
    private partial void LogBackupComplete();

    [LoggerMessage(Level = LogLevel.Critical, Message = "Tried to restore a backup that does not exist: {Key}")]
    private partial void LogBackupMissingForRestore(string key);

    [LoggerMessage(Level = LogLevel.Information, Message = "Restoring PostgreSQL database from {BackupFile}")]
    private partial void LogRestoreStarting(string backupFile);

    [LoggerMessage(Level = LogLevel.Information, Message = "PostgreSQL restore complete")]
    private partial void LogRestoreComplete();

    [LoggerMessage(Level = LogLevel.Critical, Message = "Tried to delete a backup that does not exist: {Key}")]
    private partial void LogBackupMissingForDelete(string key);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted PostgreSQL backup {Key}")]
    private partial void LogBackupDeleted(string key);

    [LoggerMessage(Level = LogLevel.Information, Message = "Purged {TableCount} PostgreSQL tables")]
    private partial void LogPurged(int tableCount);

    [LoggerMessage(Level = LogLevel.Error, Message = "{FileName} failed with exit code {ExitCode}: {Error}")]
    private partial void LogToolFailed(string fileName, int exitCode, string error);
}
