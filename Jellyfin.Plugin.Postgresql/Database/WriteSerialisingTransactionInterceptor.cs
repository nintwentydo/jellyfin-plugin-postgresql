using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Jellyfin.Plugin.Postgresql.Database;

/// <summary>
/// Serialises Jellyfin's write transactions with a PostgreSQL advisory lock.
/// </summary>
/// <remarks>
/// Jellyfin was written against SQLite's single writer and its save paths rely on it:
/// <c>ItemPersistenceService.UpdateOrInsertItems</c> reads which <c>ItemValues</c> rows exist,
/// then inserts the missing ones, and the library scheduler runs those saves on
/// <c>ProcessorCount - 3</c> workers at once. PostgreSQL lets them all run, so two saves that
/// introduce the same new tag both insert it and the loser dies with <c>23505</c> on
/// <c>IX_ItemValues_Type_Value</c>, or the pair deadlocks on that index. The whole save is lost
/// each time. One transaction-scoped advisory lock taken as each transaction opens puts the
/// single writer back at the database: transactions queue, plain reads never wait, and the lock
/// goes with the commit or rollback, across processes too. Relies on READ COMMITTED, the server
/// default core never overrides, so every statement after the lock sees the previous writer's
/// commit.
/// <para>
/// Known costs: a full-system backup reads every table inside one transaction and holds the lock
/// for the duration; and a nested transaction on a second pooled connection would wait on itself
/// forever, which no core path does today. Should one appear, prepend
/// <c>SET LOCAL lock_timeout = '...';</c> to the lock statement to turn the hang into an error.
/// </para>
/// </remarks>
internal sealed class WriteSerialisingTransactionInterceptor : DbTransactionInterceptor
{
    // ponytail: one lock for the whole database; per-table keys would let unrelated writers
    // overlap if scan throughput ever measurably suffers.
    private const string AcquireLock = "SELECT pg_advisory_xact_lock(hashtext('jellyfin'))";

    /// <inheritdoc />
    public override DbTransaction TransactionStarted(DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
    {
        using var command = connection.CreateCommand();
        command.Transaction = result;
        command.CommandText = AcquireLock;
        command.ExecuteNonQuery();

        return base.TransactionStarted(connection, eventData, result);
    }

    /// <inheritdoc />
    public override async ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection, TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.Transaction = result;
            command.CommandText = AcquireLock;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return await base.TransactionStartedAsync(connection, eventData, result, cancellationToken).ConfigureAwait(false);
    }
}
