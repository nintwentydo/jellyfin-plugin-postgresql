using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Plugin.Postgresql.Database;
using MediaBrowser.Common.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Jellyfin.Plugin.Postgresql.Tests;

// Every destructive operation below targets the fixture's freshly created database. The supplied
// connection is used only to create/drop that database, never as the database under test.
[Trait("Category", "PostgreSQL")]
public sealed class PostgresqlIntegrationTests(PostgresqlFixture database) : IClassFixture<PostgresqlFixture>
{
    [PostgresqlFact]
    public async Task Like_matches_case_insensitively_and_preserves_explicit_escaping()
    {
        await using var context = database.CreateContext();
        const string type = "integration-search";
        context.BaseItems.AddRange(
            new BaseItemEntity { Id = Guid.NewGuid(), Type = type, OriginalTitle = "The Matrix" },
            new BaseItemEntity { Id = Guid.NewGuid(), Type = type, OriginalTitle = "100%_Match" },
            new BaseItemEntity { Id = Guid.NewGuid(), Type = type, OriginalTitle = "100xxMatch" });
        await context.SaveChangesAsync();

        var items = context.BaseItems.Where(item => item.Type == type);
        Assert.Equal("The Matrix", await items.Where(item => EF.Functions.Like(item.OriginalTitle!, "%matrix%"))
            .Select(item => item.OriginalTitle).SingleAsync());
        Assert.Equal("100%_Match", await items.Where(item => EF.Functions.Like(item.OriginalTitle!, "100!%!_match", "!"))
            .Select(item => item.OriginalTitle).SingleAsync());
    }

    [PostgresqlFact]
    public async Task Null_sort_order_and_datetime_round_trips_match_the_provider_contract()
    {
        await using var context = database.CreateContext();
        const string type = "integration-order-dates";
        var utc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var local = utc.ToLocalTime();
        context.BaseItems.AddRange(
            new BaseItemEntity { Id = Guid.NewGuid(), Type = type, CommunityRating = null, DateCreated = null },
            new BaseItemEntity { Id = Guid.NewGuid(), Type = type, CommunityRating = 5, DateCreated = utc },
            new BaseItemEntity { Id = Guid.NewGuid(), Type = type, CommunityRating = 7, DateCreated = local });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var items = context.BaseItems.Where(item => item.Type == type);
        Assert.Equal(new float?[] { null, 5, 7 }, await items.OrderBy(item => item.CommunityRating).Select(item => item.CommunityRating).ToArrayAsync());
        Assert.Equal(new float?[] { 7, 5, null }, await items.OrderByDescending(item => item.CommunityRating).Select(item => item.CommunityRating).ToArrayAsync());
        var dates = await items.OrderBy(item => item.CommunityRating).Select(item => item.DateCreated).ToArrayAsync();
        Assert.Null(dates[0]);
        Assert.All(dates.Skip(1), value =>
        {
            Assert.Equal(utc, value);
            Assert.Equal(DateTimeKind.Utc, value!.Value.Kind);
        });
    }

    [PostgresqlFact]
    public async Task Reapplying_migrations_preserves_populated_rows_and_history()
    {
        await using var context = database.CreateContext();
        var item = new BaseItemEntity { Id = Guid.NewGuid(), Type = "integration-migrations" };
        context.BaseItems.Add(item);
        await context.SaveChangesAsync();
        var history = (await context.Database.GetAppliedMigrationsAsync()).ToArray();
        Assert.NotEmpty(history);

        await context.Database.MigrateAsync();

        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.Equal(history, await context.Database.GetAppliedMigrationsAsync());
        Assert.True(await context.BaseItems.AnyAsync(row => row.Id == item.Id));
    }

    [PostgresqlFact]
    public async Task Write_transactions_wait_for_the_advisory_lock_and_release_it()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = deadline.Token;
        await using var first = database.CreateContext();
        await using var second = database.CreateContext();
        await first.Database.OpenConnectionAsync(token);
        await second.Database.OpenConnectionAsync(token);
        var firstPid = ((NpgsqlConnection)first.Database.GetDbConnection()).ProcessID;
        var secondPid = ((NpgsqlConnection)second.Database.GetDbConnection()).ProcessID;
        await using var firstTransaction = await first.Database.BeginTransactionAsync(token);
        var secondTransactionTask = second.Database.BeginTransactionAsync(token);
        try
        {
            // Observe the server's lock state instead of assuming that a delay proves blocking.
            await database.WaitUntilAsync(async () => await database.ScalarAsync<bool>(
                $"SELECT EXISTS (SELECT FROM pg_locks WHERE pid = {secondPid} AND locktype = 'advisory' AND NOT granted)", token), token);
            Assert.False(secondTransactionTask.IsCompleted);

            await firstTransaction.CommitAsync(token);
            await using var secondTransaction = await secondTransactionTask;
            await secondTransaction.RollbackAsync(token);
            Assert.Equal(0L, await database.ScalarAsync<long>(
                $"SELECT count(*) FROM pg_locks WHERE pid IN ({firstPid}, {secondPid}) AND locktype = 'advisory'", token));
        }
        finally
        {
            // Release A even if observing B fails, then await/dispose B before fixture cleanup.
            await firstTransaction.DisposeAsync();
            try
            {
                await using var cleanup = await secondTransactionTask;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
        }
    }

    [PostgresqlFact]
    public async Task Native_restore_recovers_rows_history_and_generated_identity_values()
    {
        int savedId;
        string[] history;
        await using (var context = database.CreateContext())
        {
            var log = new ActivityLog("before backup", "integration-restore", Guid.Empty);
            context.ActivityLogs.Add(log);
            await context.SaveChangesAsync();
            savedId = log.Id;
            history = (await context.Database.GetAppliedMigrationsAsync()).ToArray();
        }

        var key = await database.Provider.MigrationBackupFast(CancellationToken.None);
        try
        {
            await using (var context = database.CreateContext())
            {
                await context.ActivityLogs.Where(log => log.Id == savedId).ExecuteDeleteAsync();
                context.ActivityLogs.Add(new ActivityLog("after backup", "integration-restore", Guid.Empty));
                await context.SaveChangesAsync();
                await context.Database.ExecuteSqlRawAsync("DELETE FROM \"__EFMigrationsHistory\"");
            }

            await database.Provider.RestoreBackupFast(key, CancellationToken.None);

            await using var restored = database.CreateContext();
            Assert.Equal(history, await restored.Database.GetAppliedMigrationsAsync());
            var saved = await restored.ActivityLogs.Where(log => log.Type == "integration-restore").SingleAsync();
            Assert.Equal(savedId, saved.Id);
            Assert.Equal("before backup", saved.Name);
            var next = new ActivityLog("generated after restore", "integration-restore", Guid.Empty);
            restored.ActivityLogs.Add(next);
            await restored.SaveChangesAsync();
            Assert.True(next.Id > savedId);
        }
        finally
        {
            await database.Provider.DeleteBackup(key);
        }
    }

    [PostgresqlFact]
    public async Task Native_restore_failure_rolls_back_earlier_destructive_statements()
    {
        await using var context = database.CreateContext();
        var item = new BaseItemEntity { Id = Guid.NewGuid(), Type = "integration-failed-restore" };
        context.BaseItems.Add(item);
        await context.SaveChangesAsync();
        var history = (await context.Database.GetAppliedMigrationsAsync()).ToArray();
        var key = await database.Provider.MigrationBackupFast(CancellationToken.None);
        try
        {
            // A real dump executes DROP/CREATE/COPY successfully before this failure. Add a row
            // absent from the dump, so its survival proves that those commands were rolled back.
            var afterBackup = new BaseItemEntity { Id = Guid.NewGuid(), Type = "integration-failed-restore-later" };
            context.BaseItems.Add(afterBackup);
            await context.SaveChangesAsync();
            await File.AppendAllTextAsync(database.BackupPath(key), "\nSELECT 1 / 0;\n");

            await Assert.ThrowsAsync<InvalidOperationException>(() => database.Provider.RestoreBackupFast(key, CancellationToken.None));

            Assert.Equal(history, await context.Database.GetAppliedMigrationsAsync());
            Assert.True(await context.BaseItems.AnyAsync(row => row.Id == item.Id));
            Assert.True(await context.BaseItems.AnyAsync(row => row.Id == afterBackup.Id));
        }
        finally
        {
            await database.Provider.DeleteBackup(key);
        }
    }

    [PostgresqlFact]
    public async Task Cancelling_native_restore_stops_the_backend_and_rolls_back_changes()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        await using var context = database.CreateContext();
        var item = new BaseItemEntity { Id = Guid.NewGuid(), Type = "integration-cancel-restore" };
        context.BaseItems.Add(item);
        await context.SaveChangesAsync();
        const string key = "integration-cancel";
        Directory.CreateDirectory(Path.GetDirectoryName(database.BackupPath(key))!);
        // PostgreSQL may notice a disconnected client only when its current statement ends.
        // Keep that statement bounded, while observing PgSleep to prove cancellation happens mid-restore.
        await File.WriteAllTextAsync(database.BackupPath(key),
            $"DELETE FROM \"BaseItems\" WHERE \"Id\" = '{item.Id}'; DO $$ BEGIN PERFORM pg_sleep(5); END $$ /* jf_plugin_restore_cancel */;", deadline.Token);
        var restore = database.Provider.RestoreBackupFast(key, cancellation.Token);
        var pid = 0;
        try
        {
            await database.WaitUntilAsync(async () =>
            {
                pid = await database.ScalarAsync<int>(
                    "SELECT coalesce(max(pid), 0) FROM pg_stat_activity WHERE datname = current_database() "
                    + "AND pid <> pg_backend_pid() AND wait_event = 'PgSleep' AND query LIKE '%jf_plugin_restore_cancel%'", deadline.Token);
                return pid != 0;
            }, deadline.Token);
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => restore);
            await database.WaitUntilAsync(async () => !await database.ScalarAsync<bool>(
                $"SELECT EXISTS (SELECT FROM pg_stat_activity WHERE pid = {pid})", deadline.Token), deadline.Token);
            Assert.True(await context.BaseItems.AnyAsync(row => row.Id == item.Id, deadline.Token));
        }
        finally
        {
            await cancellation.CancelAsync();
            try
            {
                await restore;
            }
            catch (OperationCanceledException)
            {
            }

            await database.Provider.DeleteBackup(key);
        }
    }

    [PostgresqlFact]
    public async Task Missing_native_backup_fails_without_modifying_the_database()
    {
        await using var context = database.CreateContext();
        var history = (await context.Database.GetAppliedMigrationsAsync()).ToArray();
        var count = await context.BaseItems.CountAsync();
        await Assert.ThrowsAsync<FileNotFoundException>(() => database.Provider.RestoreBackupFast("does-not-exist", CancellationToken.None));
        Assert.Equal(history, await context.Database.GetAppliedMigrationsAsync());
        Assert.Equal(count, await context.BaseItems.CountAsync());
    }
}

public sealed class PostgresqlFactAttribute : FactAttribute
{
    public PostgresqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PostgresqlFixture.EnvironmentVariable)))
        {
            Skip = $"Set {PostgresqlFixture.EnvironmentVariable} to a disposable PostgreSQL server with CREATEDB permission and pg_dump/psql on PATH.";
        }
    }
}

public sealed class PostgresqlFixture : IAsyncLifetime
{
    public const string EnvironmentVariable = "JELLYFIN_POSTGRES_TEST_CONNECTION";
    private readonly string _databaseName = $"jf_plugin_test_{Guid.NewGuid():N}";
    private readonly string _dataPath = Path.Join(Path.GetTempPath(), $"jellyfin postgres tests {Guid.NewGuid():N}");
    private string? _adminConnection;
    private string? _testConnection;
    private DbContextOptions<JellyfinDbContext>? _options;
    private bool _createdDatabase;

    public PostgresqlDatabaseProvider Provider { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var supplied = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(supplied))
        {
            return;
        }

        var connection = new NpgsqlConnectionStringBuilder(supplied) { Pooling = false };
        _adminConnection = connection.ConnectionString;
        try
        {
            await using (var admin = new NpgsqlConnection(_adminConnection))
            {
                await admin.OpenAsync();
                await using var create = new NpgsqlCommand($"CREATE DATABASE \"{_databaseName}\"", admin);
                await create.ExecuteNonQueryAsync();
                _createdDatabase = true;
            }

            connection.Database = _databaseName;
            _testConnection = connection.ConnectionString;
            Provider = new PostgresqlDatabaseProvider(new TestApplicationPaths(_dataPath), NullLogger<PostgresqlDatabaseProvider>.Instance);
            var options = new DbContextOptionsBuilder<JellyfinDbContext>();
            Provider.Initialise(options, new DatabaseConfigurationOptions
            {
                DatabaseType = "PLUGIN_PROVIDER",
                CustomProviderOptions = new CustomDatabaseOptions
                {
                    PluginName = "PostgreSQL",
                    PluginAssembly = "Jellyfin.Plugin.Postgresql.dll",
                    ConnectionString = _testConnection
                }
            });
            _options = options.Options;
            await using var context = CreateContext();
            await context.Database.MigrateAsync();
            Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public JellyfinDbContext CreateContext() => new(
        _options!, NullLogger<JellyfinDbContext>.Instance, Provider,
        new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));

    public string BackupPath(string key) => Path.Join(_dataPath, "PostgresqlBackups", $"{key}_jellyfin.sql");

    public async Task<T> ScalarAsync<T>(string sql, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_testConnection);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task WaitUntilAsync(Func<Task<bool>> condition, CancellationToken cancellationToken)
    {
        while (!await condition())
        {
            await Task.Delay(25, cancellationToken);
        }
    }

    public async Task DisposeAsync()
    {
        if (_createdDatabase)
        {
            await using var admin = new NpgsqlConnection(_adminConnection);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{_databaseName}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
            _createdDatabase = false;
        }

        if (Directory.Exists(_dataPath))
        {
            Directory.Delete(_dataPath, recursive: true);
        }
    }

    private sealed class TestApplicationPaths(string dataPath) : IApplicationPaths
    {
        public string DataPath => dataPath;

        public string ProgramDataPath => dataPath;

        public string WebPath => throw new NotSupportedException();

        public string ProgramSystemPath => throw new NotSupportedException();

        public string ImageCachePath => throw new NotSupportedException();

        public string PluginsPath => throw new NotSupportedException();

        public string PluginConfigurationsPath => throw new NotSupportedException();

        public string LogDirectoryPath => throw new NotSupportedException();

        public string ConfigurationDirectoryPath => throw new NotSupportedException();

        public string SystemConfigurationFilePath => throw new NotSupportedException();

        public string CachePath => throw new NotSupportedException();

        public string TempDirectory => throw new NotSupportedException();

        public string VirtualDataPath => throw new NotSupportedException();

        public string TrickplayPath => throw new NotSupportedException();

        public string BackupPath => throw new NotSupportedException();

        public void MakeSanityCheckOrThrow() => throw new NotSupportedException();

        public void CreateAndCheckMarker(string path, string markerName, bool recursive = false) => throw new NotSupportedException();
    }
}
