# Provider behaviour

[Back to README](../README.md)

Jellyfin's queries assume SQLite behaviour. The plugin adapts the EF Core model and generated SQL for PostgreSQL; these changes apply automatically.

## Query and storage changes

| Area | What the plugin does |
| --- | --- |
| Search | Rewrites `EF.Functions.Like` to `ILIKE` so ASCII case differences do not hide results. |
| Text ordering | Applies `COLLATE "C"` to string columns for bytewise ordering. |
| Missing values | Adds `NULLS FIRST` for ascending sorts and `NULLS LAST` for descending sorts, matching SQLite. |
| Dates | Matches stock SQLite: Local and Unspecified values are interpreted in the process timezone and converted to UTC on write; reads are UTC. |
| Item IDs | Uses native `uuid` columns. The initial migration supplies `min(uuid)` and `max(uuid)` aggregates for grouped item queries when needed. |
| Playback state | Uses `ON CONFLICT DO UPDATE` for `UserData` inserts, avoiding duplicate-key failures from concurrent saves. Other tables retain normal insert behaviour. |

The model and SQL customisations live in [Database/](../Jellyfin.Plugin.Postgresql/Database/). They aim to preserve Jellyfin's expected behaviour, not every SQLite/PostgreSQL edge case.

## Transaction locking

Each EF transaction takes `pg_advisory_xact_lock(hashtext('jellyfin'))`. Transactions queue until the previous holder commits or rolls back, preventing races when library saves check for a row and then insert it. Reads outside an explicit transaction do not acquire this lock.

This deliberately serialises transactions, including read-only transactions used by built-in backups. It relies on PostgreSQL's default `READ COMMITTED` isolation. A transaction waiting for another connection's transaction can block itself; keep that in mind when changing database code. The lock does not by itself make sharing a database between Jellyfin servers supported.

The lock covers EF transaction starts, not every SQL write or earlier application read. Core v12's ordinary `SaveUserData` starts its transaction before checking whether a row exists; imports and direct EF inserts also use the retained UserData upsert. Neither mechanism refreshes stale cached values or protects unrelated fields when core submits a full-row update.

See [WriteSerialisingTransactionInterceptor.cs](../Jellyfin.Plugin.Postgresql/Database/WriteSerialisingTransactionInterceptor.cs).

## Maintenance and performance

- Connections default to `-c jit=off` to avoid compilation overhead on Jellyfin's large generated queries. [Custom connection options](configuration.md#additional-connection-options) can override this.
- The database optimisation task runs `VACUUM ANALYZE` on the model's tables. PostgreSQL autovacuum remains responsible for routine maintenance.
- Shutdown clears Npgsql's connection pools.
- Migration backups use `pg_dump` and `psql`; see [operations](operations.md#jellyfins-backup-support).

[The fast tests](../tests/Jellyfin.Plugin.Postgresql.Tests/PostgresqlMappingTests.cs) check model mappings, generated SQL, and service registration. The optional [PostgreSQL suite](../tests/Jellyfin.Plugin.Postgresql.Tests/PostgresqlIntegrationTests.cs) executes queries, checks transaction locking, and validates native backup/recovery against disposable databases. See [development](development.md#postgresql-integration-tests) for setup and coverage limits.
