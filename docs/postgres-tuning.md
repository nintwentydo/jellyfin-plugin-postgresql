# Performance

[Back to README](../README.md)

Start with PostgreSQL's defaults. The plugin already disables JIT for its connections and implements Jellyfin's database optimisation task. Additional tuning depends on your library, hardware, and other database workloads.

## Before changing settings

1. Check that custom connection `Options` still include `-c jit=off`.
2. After a large scan, run Jellyfin's **Optimize database** task to refresh planner statistics.
3. Identify slow queries with PostgreSQL logs or [pg_stat_statements](https://www.postgresql.org/docs/18/pgstatstatements.html) before changing server settings.

To check disk spills in the `jellyfin` database:

```sql
SELECT temp_files, pg_size_pretty(temp_bytes) AS temp_written
FROM pg_stat_database
WHERE datname = 'jellyfin';
```

These counters are cumulative. Compare readings over the same workload; they do not identify which query spilled.

## Targeted adjustments

If sorts spill frequently and memory is available, try a role-level setting rather than changing every application's connections:

```sql
ALTER ROLE jellyfin SET work_mem = '32MB';
```

Restart Jellyfin to open new connections, then compare the same workload. `work_mem` applies per query operation, so several sorts and concurrent sessions can multiply memory use. See [PostgreSQL memory settings](https://www.postgresql.org/docs/18/runtime-config-resource.html).

To undo the change:

```sql
ALTER ROLE jellyfin RESET work_mem;
```

Restart Jellyfin again for the reset to take effect. If connection pressure is the issue, use `Maximum Pool Size` in [configuration](configuration.md#additional-connection-options). Leave autovacuum and durability settings enabled.

## Prepared statements

Npgsql automatic preparation is disabled by default. If repeated queries spend substantial time planning, try `Max Auto Prepare=16` with the default `Auto Prepare Min Usages=5` in [additional connection options](configuration.md#additional-connection-options), then compare the same workload after warm-up. Restart Jellyfin when applying or undoing the change; `Max Auto Prepare=0` disables it again.

Prepared statements belong to each physical pooled connection. PostgreSQL can still choose a custom plan for every execution, so preparation does not necessarily remove planning costs. A bounded Jellyfin query replay found small movie-browse gains but little improvement for episode browsing or resume queries. Keep the default unless measurements show a useful benefit for your library and query mix. See [Npgsql preparation](https://www.npgsql.org/doc/prepare.html) and [PostgreSQL plan selection](https://www.postgresql.org/docs/18/sql-prepare.html).
