using System;
using System.Linq;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Postgresql.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace Jellyfin.Plugin.Postgresql.Tests;

/// <summary>
/// Guards the two behaviours that make PostgreSQL match SQLite. Both fail silently in production
/// — wrong search results and wrong sort order rather than an exception — so they are worth
/// asserting on. Everything here reads the model or the generated SQL; no server is contacted.
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
    public void Guid_columns_map_to_the_native_uuid_type()
    {
        using var context = CreateContext();

        var id = context.GetService<IDesignTimeModel>().Model
            .FindEntityType(typeof(BaseItemEntity))!
            .GetProperty(nameof(BaseItemEntity.Id));

        Assert.Equal("uuid", id.GetColumnType());
    }
}
