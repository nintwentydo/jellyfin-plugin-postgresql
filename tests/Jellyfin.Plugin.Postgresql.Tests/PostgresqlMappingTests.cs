using System;
using System.Linq;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Postgresql.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Update;
using Xunit;

namespace Jellyfin.Plugin.Postgresql.Tests;

/// <summary>
/// Guards the behaviours that make PostgreSQL match SQLite. Most fail silently in production —
/// wrong search results or sort order rather than an exception — so they are worth asserting on.
/// Everything here reads the model, the generated SQL, or the registered services; no server is
/// contacted.
/// </summary>
public class PostgresqlMappingTests
{
    private static JellyfinDbContext CreateContext()
        => new PostgresqlDesignTimeJellyfinDbFactory().CreateDbContext([]);

    [Fact]
    public void EfFunctions_Like_is_translated_to_case_insensitive_ILIKE()
    {
        using var context = CreateContext();

        var sql = context.BaseItems
            .Where(e => EF.Functions.Like(e.OriginalTitle!, "%matrix%"))
            .ToQueryString();

        // PostgreSQL's LIKE is case-sensitive where SQLite's is not, so a plain LIKE here would
        // stop matching "The Matrix" and quietly shrink every search result.
        Assert.Contains("ILIKE", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void String_columns_use_the_C_collation()
    {
        using var context = CreateContext();

        // Collation is a schema-shape annotation, so it lives on the design-time model; the
        // runtime model drops anything the query pipeline does not need.
        var sortName = context.GetService<IDesignTimeModel>().Model
            .FindEntityType(typeof(BaseItemEntity))!
            .GetProperty(nameof(BaseItemEntity.SortName));

        // "C" is byte-ordinal, matching SQLite's BINARY. Without it the column inherits the
        // cluster's LC_COLLATE and the A-Z jump bar buckets differently to every SQLite install.
        Assert.Equal("C", sortName.GetCollation());
    }

    [Fact]
    public void Null_sort_placement_matches_sqlite()
    {
        using var context = CreateContext();

        // SQLite sorts NULL before every value, PostgreSQL after it on ASC and before it on
        // DESC, so without this an unrated item would top a rating-descending list instead of
        // closing it.
        var ascending = context.BaseItems.OrderBy(e => e.CommunityRating).ToQueryString();
        var descending = context.BaseItems.OrderByDescending(e => e.CommunityRating).ToQueryString();

        Assert.Contains("NULLS FIRST", ascending, StringComparison.Ordinal);
        Assert.Contains("NULLS LAST", descending, StringComparison.Ordinal);
    }

    [Fact]
    public void DateTime_columns_round_trip_as_utc()
    {
        using var context = CreateContext();

        var dateCreated = context.Model
            .FindEntityType(typeof(BaseItemEntity))!
            .GetProperty(nameof(BaseItemEntity.DateCreated));

        // Npgsql maps DateTime to `timestamp with time zone`, which rejects any value whose Kind
        // is not Utc.
        var converter = dateCreated.GetValueConverter();
        Assert.NotNull(converter);

        var converted = (DateTime)converter.ConvertFromProvider(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified))!;
        Assert.Equal(DateTimeKind.Utc, converted.Kind);
    }

    [Fact]
    public void UserData_inserts_go_through_the_upsert_generator()
    {
        using var context = CreateContext();

        // Concurrent playback-progress saves race UserDataManager's check-then-insert; the
        // upsert generator turns the losing INSERT into ON CONFLICT DO UPDATE instead of a
        // PK_UserData violation that aborts playback.
        Assert.IsType<PostgresqlUpdateSqlGenerator>(context.GetService<IUpdateSqlGenerator>());
    }

    [Fact]
    public void Transactions_take_the_write_serialising_advisory_lock()
    {
        using var context = CreateContext();

        // Parallel item saves race the read-then-insert in UpdateOrInsertItems; the interceptor
        // queues write transactions behind one advisory lock so the loser waits instead of
        // failing on IX_ItemValues_Type_Value.
        var interceptors = context.GetService<IDbContextOptions>()
            .FindExtension<CoreOptionsExtension>()!
            .Interceptors!;

        Assert.Contains(interceptors, interceptor => interceptor is WriteSerialisingTransactionInterceptor);
    }

    [Fact]
    public void Guid_columns_map_to_the_native_uuid_type()
    {
        using var context = CreateContext();

        var id = context.GetService<IDesignTimeModel>().Model
            .FindEntityType(typeof(BaseItemEntity))!
            .GetProperty(nameof(BaseItemEntity.Id));

        Assert.Equal("uuid", id.GetColumnType());
    }
}
