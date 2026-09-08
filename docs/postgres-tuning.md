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
