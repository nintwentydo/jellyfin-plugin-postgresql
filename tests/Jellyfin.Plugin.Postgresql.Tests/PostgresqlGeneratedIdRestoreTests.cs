using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Entities.Security;
using Jellyfin.Database.Implementations.Locking;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Jellyfin.Plugin.Postgresql.Tests;

[Trait("Category", "PostgreSQL")]
public sealed class PostgresqlGeneratedIdRestoreTests(PostgresqlFixture database) : IClassFixture<PostgresqlFixture>
{
    [PostgresqlFact]
    public async Task Explicit_import_preserves_ids_and_allows_generated_inserts_in_every_identity_table()
    {
        // Fresh and previously populated targets, with generator positions below and above the import.
        foreach (var counter in new[] { 1, 500 })
        {
            await using var context = database.CreateContext();
            await using (var transaction = await context.Database.BeginTransactionAsync())
            {
                await database.Provider.PurgeDatabase(context, Tables(context));
                var imported = AddRows(context, "imported", 100);
                await context.SaveChangesAsync();
                await context.Database.ExecuteSqlRawAsync(counter == 1
                    ? "ALTER SEQUENCE \"ActivityLogs_Id_seq\" RESTART WITH 1"
                    : "ALTER SEQUENCE \"ActivityLogs_Id_seq\" RESTART WITH 500");

                await ((IJellyfinDatabaseProvider)database.Provider).CompleteDatabaseRestoreAsync(context, CancellationToken.None);
                Assert.All(imported, row => Assert.Equal(100, context.Entry(row).Property("Id").CurrentValue));
                await transaction.CommitAsync();
            }

            var next = AddRows(context, "generated", null);
            await context.SaveChangesAsync();
            Assert.Equal(context.Model.GetEntityTypes().SelectMany(entity => entity.GetProperties())
                .Count(property => property.ClrType == typeof(int) && property.ValueGenerated == Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.OnAdd), next.Length);
            Assert.All(next, row => Assert.Equal(101, context.Entry(row).Property("Id").CurrentValue));
        }
    }

    [PostgresqlFact]
    public async Task Empty_import_restores_configured_start_values()
    {
        await using var context = database.CreateContext();
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            await database.Provider.PurgeDatabase(context, Tables(context));
            await context.Database.ExecuteSqlRawAsync("ALTER SEQUENCE \"AccessSchedules_Id_seq\" START WITH 7 INCREMENT BY 3 RESTART WITH 500");
            await ((IJellyfinDatabaseProvider)database.Provider).CompleteDatabaseRestoreAsync(context, CancellationToken.None);
            await transaction.CommitAsync();
        }

        var rows = AddRows(context, "empty-import", null);
        await context.SaveChangesAsync();
        await context.Database.ExecuteSqlRawAsync("ALTER SEQUENCE \"AccessSchedules_Id_seq\" START WITH 1 INCREMENT BY 1");
        Assert.All(rows, row => Assert.Equal(row is AccessSchedule ? 7 : 1, context.Entry(row).Property("Id").CurrentValue));
    }

    [PostgresqlFact]
    public async Task Completion_handles_quoted_schema_and_sequence_names_and_custom_increments()
    {
        foreach (var descending in new[] { false, true })
        {
            await using var context = database.CreateContext();
            await using var transaction = await context.Database.BeginTransactionAsync();
            await database.Provider.PurgeDatabase(context, Tables(context));
            await context.Database.ExecuteSqlRawAsync("""
                CREATE SCHEMA "Restore ""schema";
                ALTER TABLE "ActivityLogs" SET SCHEMA "Restore ""schema";
                ALTER SEQUENCE "Restore ""schema"."ActivityLogs_Id_seq" RENAME TO "Id ""sequence";
                SET LOCAL search_path = "Restore ""schema", public;
                """);
            await context.Database.ExecuteSqlRawAsync(descending
                ? """ALTER SEQUENCE "Restore ""schema"."Id ""sequence" MINVALUE -2147483648 MAXVALUE -1 INCREMENT BY -3 START WITH -7 RESTART WITH -7"""
                : """ALTER SEQUENCE "Restore ""schema"."Id ""sequence" INCREMENT BY 3 START WITH 7 RESTART WITH 7""");
            var imported = AddRows(context, "quoted-import", 100);
            if (descending)
            {
                context.Entry(imported.Single(row => row is ActivityLog)).Property("Id").CurrentValue = -100;
            }

            await context.SaveChangesAsync();
            await ((IJellyfinDatabaseProvider)database.Provider).CompleteDatabaseRestoreAsync(context, CancellationToken.None);
            var next = AddRows(context, "quoted-generated", null);
            await context.SaveChangesAsync();
            Assert.Equal(descending ? -103 : 103, Assert.IsType<ActivityLog>(next.Single(row => row is ActivityLog)).Id);
            await transaction.RollbackAsync();
        }
    }

    [PostgresqlFact]
    public async Task Completion_resolves_dotted_identifiers_and_only_restarts_owned_sequences_on_mapped_tables()
    {
        await using var original = database.CreateContext();
        var (provider, options) = database.CreateProvider(new NpgsqlConnectionStringBuilder(database.ConnectionString));
        await using var context = new DottedTableContext(options, provider);
        await using var transaction = await context.Database.BeginTransactionAsync();
        await provider.PurgeDatabase(context, Tables(original));
        await context.Database.ExecuteSqlRawAsync("""
            CREATE SCHEMA "Restore.""schema";
            ALTER TABLE "ActivityLogs" SET SCHEMA "Restore.""schema";
            ALTER TABLE "Restore.""schema"."ActivityLogs" RENAME TO "Activity.Logs";
            ALTER TABLE "Restore.""schema"."Activity.Logs" ALTER COLUMN "Id" DROP IDENTITY;
            CREATE SEQUENCE "Restore.""schema"."Serial.""sequence"
                OWNED BY "Restore.""schema"."Activity.Logs"."Id";
            ALTER TABLE "Restore.""schema"."Activity.Logs" ALTER COLUMN "Id"
                SET DEFAULT nextval('"Restore.""schema"."Serial.""sequence"'::regclass);
            CREATE SEQUENCE "Restore.""schema"."Unowned.sequence" START WITH 37;
            CREATE TABLE "Restore.""schema"."Unmapped.table" ("Id" integer);
            CREATE SEQUENCE "Restore.""schema"."Unmapped.sequence" START WITH 53
                OWNED BY "Restore.""schema"."Unmapped.table"."Id";
            INSERT INTO "Restore.""schema"."Unmapped.table" ("Id") VALUES (100);
            """);
        AddRows(context, "serial-import", 100);
        await context.SaveChangesAsync();

        await ((IJellyfinDatabaseProvider)provider).CompleteDatabaseRestoreAsync(context, CancellationToken.None);
        var next = AddRows(context, "serial-generated", null);
        await context.SaveChangesAsync();
        Assert.Equal(101, Assert.IsType<ActivityLog>(next.Single(row => row is ActivityLog)).Id);
        Assert.Equal(37L, await context.Database.SqlQueryRaw<long>("""
            SELECT nextval('"Restore.""schema"."Unowned.sequence"'::regclass) AS "Value"
            """).SingleAsync());
        Assert.Equal(53L, await context.Database.SqlQueryRaw<long>("""
            SELECT nextval('"Restore.""schema"."Unmapped.sequence"'::regclass) AS "Value"
            """).SingleAsync());
        await transaction.RollbackAsync();
    }

    [PostgresqlFact]
    public async Task Completion_failure_rolls_back_import_and_prior_sequence_changes()
    {
        await using var context = database.CreateContext();
        await using (var setup = await context.Database.BeginTransactionAsync())
        {
            await database.Provider.PurgeDatabase(context, Tables(context));
            await context.Database.ExecuteSqlRawAsync("ALTER SEQUENCE \"AccessSchedules_Id_seq\" START WITH 1 INCREMENT BY 1");
            AddRows(context, "preserved", 41);
            await context.SaveChangesAsync();
            await ((IJellyfinDatabaseProvider)database.Provider).CompleteDatabaseRestoreAsync(context, CancellationToken.None);
            await setup.CommitAsync();
        }

        context.ChangeTracker.Clear();
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            await database.Provider.PurgeDatabase(context, Tables(context));
            var imported = AddRows(context, "failed-import", 100);
            context.Entry(imported.Single(row => row is ActivityLog)).Property("Id").CurrentValue = int.MaxValue;
            await context.SaveChangesAsync();
            // AccessSchedules sorts first and is restarted successfully before ActivityLogs exceeds its maximum.
            var error = await Assert.ThrowsAsync<PostgresException>(
                () => ((IJellyfinDatabaseProvider)database.Provider).CompleteDatabaseRestoreAsync(context, CancellationToken.None));
            Assert.Equal(PostgresErrorCodes.InvalidParameterValue, error.SqlState);
            await transaction.RollbackAsync();
        }

        context.ChangeTracker.Clear();
        Assert.Equal(41, await context.ActivityLogs.Select(row => row.Id).SingleAsync());
        var next = AddRows(context, "after-failure", null);
        await context.SaveChangesAsync();
        Assert.All(next, row => Assert.Equal(42, context.Entry(row).Property("Id").CurrentValue));
    }

    [PostgresqlFact]
    public async Task Completion_without_an_import_transaction_fails_before_modifying_sequences()
    {
        await using var context = database.CreateContext();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ((IJellyfinDatabaseProvider)database.Provider).CompleteDatabaseRestoreAsync(context, CancellationToken.None));
        Assert.Contains("active import transaction", error.Message, StringComparison.Ordinal);
    }

    private static string[] Tables(JellyfinDbContext context) => context.Model.GetEntityTypes()
        .Select(entity => entity.GetSchemaQualifiedTableName()!).Distinct().ToArray();

    private static object[] AddRows(JellyfinDbContext context, string label, int? id)
    {
        var user = new User($"{label}-{Guid.NewGuid():N}", "test", "test");
        var item = new BaseItemEntity { Id = Guid.NewGuid(), Type = "test" };
        var preferences = new DisplayPreferences(user.Id, item.Id, label);
        var home = new HomeSection();
        preferences.HomeSections.Add(home);
        object[] rows =
        [
            new AccessSchedule(default, 0, 24, user.Id),
            new ActivityLog(label, "generated-id-restore", user.Id),
            new CustomItemDisplayPreferences(user.Id, item.Id, label, "key", "value"),
            preferences, home, new ImageInfo(label),
            new ItemDisplayPreferences(user.Id, item.Id, label),
            new Permission(default, true) { UserId = user.Id },
            new Preference(default, "value") { UserId = user.Id },
            new ApiKey(label), new Device(user.Id, "app", "1", label, Guid.NewGuid().ToString()),
            new DeviceOptions(Guid.NewGuid().ToString())
        ];
        context.AddRange(user, item);
        context.AddRange(rows);
        if (id.HasValue)
        {
            foreach (var row in rows)
            {
                context.Entry(row).Property("Id").CurrentValue = id.Value;
            }
        }

        return rows;
    }

    private sealed class DottedTableContext(DbContextOptions<JellyfinDbContext> options, IJellyfinDatabaseProvider provider)
        : JellyfinDbContext(options, NullLogger<JellyfinDbContext>.Instance, provider,
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance))
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<ActivityLog>().ToTable("Activity.Logs", "Restore.\"schema");
        }
    }
}
