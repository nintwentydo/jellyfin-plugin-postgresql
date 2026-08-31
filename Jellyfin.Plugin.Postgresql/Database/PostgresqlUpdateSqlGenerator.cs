// NpgsqlUpdateSqlGenerator lives in an .Internal namespace (EF1001), but it is also what Npgsql
// itself registers as the IUpdateSqlGenerator, and deriving from it is the only way to add an
// ON CONFLICT clause without reimplementing the whole generator. If a package bump changes the
// overridden signature the build fails; the registration itself is pinned by a test.
#pragma warning disable EF1001

using System;
using System.Linq;
using System.Text;
using Microsoft.EntityFrameworkCore.Update;
using Npgsql.EntityFrameworkCore.PostgreSQL.Update.Internal;

namespace Jellyfin.Plugin.Postgresql.Database;

/// <summary>
/// Generates <c>INSERT ... ON CONFLICT DO UPDATE</c> for the <c>UserData</c> table.
/// </summary>
/// <remarks>
/// Jellyfin's <c>UserDataManager.SaveUserData</c> decides between INSERT and UPDATE with a
/// check-then-insert (<c>Any()</c> then <c>Add</c>). Playback fires progress saves concurrently —
/// a seek raises progress and stop events together — and two racing saves both pass the check,
/// so the loser dies with <c>23505 duplicate key value violates unique constraint "PK_UserData"</c>
/// and the client abandons playback. The race exists on SQLite too, but in-process the window is
/// microseconds; over TCP it is routinely hit. Turning the INSERT into an upsert makes the loser
/// overwrite instead of throw, which is the last-writer-wins outcome the server's UPDATE path
/// produces anyway. Scoped to <c>UserData</c> only: elsewhere a duplicate key is a real bug that
/// should keep throwing.
/// </remarks>
internal sealed class PostgresqlUpdateSqlGenerator : NpgsqlUpdateSqlGenerator
{
    private const string UserDataTable = "UserData";

    /// <summary>
    /// Initializes a new instance of the <see cref="PostgresqlUpdateSqlGenerator"/> class.
    /// </summary>
    /// <param name="dependencies">Dependencies supplied by EF Core.</param>
    public PostgresqlUpdateSqlGenerator(UpdateSqlGeneratorDependencies dependencies)
        : base(dependencies)
    {
    }

    /// <inheritdoc />
    public override ResultSetMapping AppendInsertOperation(
        StringBuilder commandStringBuilder,
        IReadOnlyModificationCommand command,
        int commandPosition,
        bool overridingSystemValue,
        out bool requiresTransaction)
    {
        // Npgsql's three-argument IUpdateSqlGenerator method delegates to this overload, so
        // overriding it covers every insert path.
        var operations = command.ColumnModifications;
        var writeOperations = operations.Where(o => o.IsWrite).ToList();
        var updateOperations = writeOperations.Where(o => !o.IsKey).ToList();

        // Fall back to the stock INSERT for store-generated columns too (none on UserData
        // today): the base emits a RETURNING clause for them that this rewrite drops.
        if (!string.Equals(command.TableName, UserDataTable, StringComparison.Ordinal)
            || updateOperations.Count == 0
            || operations.Any(o => o.IsRead))
        {
            return base.AppendInsertOperation(commandStringBuilder, command, commandPosition, overridingSystemValue, out requiresTransaction);
        }

        var keyOperations = operations.Where(o => o.IsKey).ToList();

        AppendInsertCommandHeader(commandStringBuilder, command.TableName, command.Schema, writeOperations);
        AppendValuesHeader(commandStringBuilder, writeOperations);
        AppendValues(commandStringBuilder, command.TableName, command.Schema, writeOperations);

        commandStringBuilder.Append(" ON CONFLICT (");
        commandStringBuilder.AppendJoin(", ", keyOperations.Select(o => SqlGenerationHelper.DelimitIdentifier(o.ColumnName)));
        commandStringBuilder.Append(") DO UPDATE SET ");
        commandStringBuilder.AppendJoin(", ", updateOperations.Select(o =>
            $"{SqlGenerationHelper.DelimitIdentifier(o.ColumnName)} = EXCLUDED.{SqlGenerationHelper.DelimitIdentifier(o.ColumnName)}"));
        commandStringBuilder.AppendLine(SqlGenerationHelper.StatementTerminator);

        // DO UPDATE reports one affected row whether it inserted or updated, so EF's
        // rows-affected check passes on both sides of the race.
        requiresTransaction = false;
        return ResultSetMapping.NoResults;
    }
}
