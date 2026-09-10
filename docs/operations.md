# Operations

[Back to README](../README.md)

## Backups

Back up both the PostgreSQL database and Jellyfin's configuration/data files. A database dump alone does not include plugin files, artwork, or server configuration.

With the [example Compose stack](../docker/compose.example.yml), run from the directory containing `compose.yml`:

```sh
umask 077
backup_file="jellyfin-$(date +%Y%m%d-%H%M%S).dump"
docker compose exec -T postgres pg_dump -U jellyfin -Fc jellyfin > "$backup_file"
```

Check that the command succeeds, copy the dump off the host, and periodically test restoring it. For a matching backup of the database and Jellyfin files, stop Jellyfin while taking both copies. [pg_dump](https://www.postgresql.org/docs/18/app-pgdump.html) itself can take a consistent database snapshot while PostgreSQL is running.

### Restore a dump

This replaces database contents. Use the intended backup filename and the Jellyfin/plugin version that goes with it:

```sh
docker compose stop jellyfin &&
docker compose exec -T postgres pg_restore -U jellyfin -d jellyfin \
  --clean --if-exists --single-transaction < jellyfin-backup.dump
```

[`--single-transaction`](https://www.postgresql.org/docs/18/app-pgrestore.html) rolls back the restore if it fails. After a successful restore, recover the corresponding Jellyfin files if needed, then start Jellyfin:

```sh
docker compose start jellyfin
```

### Jellyfin's backup support

Before schema migrations, the provider makes a temporary SQL dump in Jellyfin's data directory under `PostgresqlBackups/`. Jellyfin can request a restore if migration fails and deletion after success. Native SQL recovery runs `psql` in one transaction and stops on the first SQL error, so a failed restore rolls back its database changes. A missing required dump is reported as an error. These dumps are not scheduled backups.

Install `pg_dump` and `psql` matching the database server's major version. A successful dump from a newer client does not establish that it can be restored to an older server. The provider checks the `pg_dump` major version before creating a migration backup and rejects a mismatch, allowing core to stop before migrating. The default Docker image bundles version 18; use the [matching `PG_MAJOR` build argument](docker.md#build-the-image) for older supported servers.

Jellyfin's built-in ZIP backup uses a separate JSON database import path. The `v12.0.0-nintwentydo.1` fork imports database rows in one transaction and calls this plugin's completion hook before committing. The hook reconciles generated PostgreSQL IDs with the restored rows; a failure rolls back the imported rows and sequence restarts. The release workflow checks an actual ZIP restore and a subsequent generated-ID insert on both image architectures.

ZIP restore still does not roll back copied filesystem content if the database import fails, and the core ZIP format does not include `HomeSections`. Keep a tested PostgreSQL dump and the matching Jellyfin files for complete recovery. Provider regression tests alone do not prove the full server restore path.

Built-in backup transactions hold the [transaction lock](behaviour.md#transaction-locking); schedule them during quiet periods. Native `pg_dump` does not acquire the provider's advisory lock.

## Upgrading

Take a backup of the database and Jellyfin files first. Plugin 1.1.4.0 requires Jellyfin `v12.0.0-nintwentydo.1`; upgrade the server and plugin together. The `12.0.0.0` ABI value cannot distinguish the fork from stock Jellyfin. Test a populated copy before upgrading your server, including when moving from the previous custom Jellyfin 13 pair. Keep the previous image/plugin and matching backups for rollback.

**Docker:** choose the new version tag in `compose.yml`, then run:

```sh
docker compose pull jellyfin
docker compose up -d jellyfin
```

**Manual:** stop Jellyfin and replace the contents of its `PostgreSQL/` plugin directory with the new release. Keep only one `PostgreSQL*` plugin directory: dashboard updates can leave older versioned folders, and the provider loader may select an older copy. Restart and check the logs.

PostgreSQL major upgrades need their own database upgrade procedure. Changing `postgres:18` to a newer major tag is not sufficient.

## Troubleshooting

| Symptom | What to check |
| --- | --- |
| `No PostgreSQL password was configured` | Supply a password in the selected configuration source. Environment variables are ignored when `ConnectionString` is non-empty. |
| Custom database plugin not found | Install the plugin before starting Jellyfin; check the directory, assembly name, and file permissions. |
| Connection refused or authentication failed | Check PostgreSQL availability, host, port, role, and password. In Compose, use the service name `postgres`, not `localhost`. |
| `Could not run 'pg_dump'` | Install PostgreSQL client tools on Jellyfin's `PATH`. |
| `pg_dump` reports a server version mismatch | Install client tools matching the server's major version. Older clients cannot dump newer servers; newer-client dumps may not restore to older servers. |
| `Migration recovery requires pg_dump major version ...` | Put the matching major version of `pg_dump` on Jellyfin's `PATH`, or rebuild the Docker image with the corresponding `PG_MAJOR`. Check `pg_dump --version` inside the Jellyfin environment. |
| Entrypoint warns that `database.xml` is not `PLUGIN_PROVIDER` | An existing configuration was preserved. Review [Docker configuration](docker.md#configuration-and-storage). |
| Slow home screen | Check whether custom connection `Options` removed `-c jit=off`; see [performance](postgres-tuning.md). |

For unresolved errors, [open an issue](https://github.com/nintwentydo/jellyfin-plugin-postgresql/issues) with Jellyfin, plugin, and PostgreSQL versions; installation method; steps to reproduce; and relevant logs. Include the SQLSTATE and constraint name from database errors. Remove credentials and personal data before sharing.
