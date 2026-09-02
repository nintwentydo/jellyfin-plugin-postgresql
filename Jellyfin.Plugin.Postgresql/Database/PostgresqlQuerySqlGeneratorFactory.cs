// NpgsqlQuerySqlGeneratorFactory and NpgsqlQuerySqlGenerator live in an .Internal namespace
// (EF1001), but they are what Npgsql itself registers, and the generator's constructor is the
// only place the null-ordering flag can be set from outside. If a package bump changes either
// signature the build fails; the resulting SQL is pinned by a test.
#pragma warning disable EF1001

using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.Internal;
using Npgsql.EntityFrameworkCore.PostgreSQL.Query.Internal;

namespace Jellyfin.Plugin.Postgresql.Database;

/// <summary>
/// Creates Npgsql's query SQL generator with reverse null ordering on, so every ORDER BY carries
/// <c>NULLS FIRST</c> ascending or <c>NULLS LAST</c> descending and NULL sorts where SQLite puts it.
/// </summary>
/// <remarks>
/// SQLite treats NULL as smaller than every value, PostgreSQL as larger, so on the same query an
/// unrated item tops a rating-descending list on PostgreSQL instead of closing it. Npgsql already
/// implements the SQLite placement behind <c>ReverseNullOrdering</c> but keeps that switch
/// internal because it does not rebuild indexes to match. That cost is accepted here: the
/// affected b-tree indexes still filter, they just no longer supply the order, and a sort over
/// one filtered library page is milliseconds. Should EXPLAIN on a large library show otherwise,
/// the follow-up is <c>NULLS FIRST</c> on the sort-bearing indexes through
/// <c>SetNullSortOrder</c> in <see cref="PostgresqlModelCustomizer"/> plus a migration.
/// </remarks>
internal sealed class PostgresqlQuerySqlGeneratorFactory : NpgsqlQuerySqlGeneratorFactory
{
    private readonly QuerySqlGeneratorDependencies _dependencies;
    private readonly IRelationalTypeMappingSource _typeMappingSource;
    private readonly INpgsqlSingletonOptions _npgsqlSingletonOptions;

    /// <summary>
    /// Initializes a new instance of the <see cref="PostgresqlQuerySqlGeneratorFactory"/> class.
    /// </summary>
    /// <param name="dependencies">Dependencies supplied by EF Core.</param>
    /// <param name="typeMappingSource">The type mapping source supplied by EF Core.</param>
    /// <param name="npgsqlSingletonOptions">Npgsql's provider options.</param>
    public PostgresqlQuerySqlGeneratorFactory(
        QuerySqlGeneratorDependencies dependencies,
        IRelationalTypeMappingSource typeMappingSource,
        INpgsqlSingletonOptions npgsqlSingletonOptions)
        : base(dependencies, typeMappingSource, npgsqlSingletonOptions)
    {
        _dependencies = dependencies;
        _typeMappingSource = typeMappingSource;
        _npgsqlSingletonOptions = npgsqlSingletonOptions;
    }

    /// <inheritdoc />
    public override QuerySqlGenerator Create()
        => new NpgsqlQuerySqlGenerator(
            _dependencies,
            _typeMappingSource,
            reverseNullOrderingEnabled: true,
            _npgsqlSingletonOptions.PostgresVersion);
}
