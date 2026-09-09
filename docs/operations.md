# Operations

[Back to README](../README.md)

## Backups

Back up both the PostgreSQL database and Jellyfin's configuration/data files. A database dump alone does not include plugin files, artwork, or server configuration.

Using matching PostgreSQL client tools, set `PGHOST`, `PGPORT`, `PGUSER` and `PGDATABASE` for your instance, with credentials in a private PostgreSQL password file. Then run:

```sh
umask 077
backup_file="jellyfin-$(date +%Y%m%d-%H%M%S).dump"
pg_dump --format=custom --file="$backup_file"
```

Check that the command succeeds, copy the dump off the host, and periodically test restoring it. For a matching backup of the database and Jellyfin files, stop Jellyfin while taking both copies. [pg_dump](https://www.postgresql.org/docs/18/app-pgdump.html) itself can take a consistent database snapshot while PostgreSQL is running.

### Restore a dump

This replaces database contents. Stop Jellyfin first. Use the intended backup filename, the connection settings above, and the paired server/plugin version that goes with the backup:

```sh
pg_restore --dbname="$PGDATABASE" --clean --if-exists --single-transaction jellyfin-backup.dump
```

[`--single-transaction`](https://www.postgresql.org/docs/18/app-pgrestore.html) rolls back the restore if it fails. After a successful restore, recover the corresponding Jellyfin files if needed, then start the paired custom server.

### Jellyfin's backup support

Before schema migrations, the provider makes a temporary SQL dump in Jellyfin's data directory under `PostgresqlBackups/`. Jellyfin can request a restore if migration fails and deletion after success. Native SQL recovery runs `psql` in one transaction and stops on the first SQL error, so a failed restore rolls back its database changes. A missing required dump is reported as an error. These dumps are not scheduled backups.

Install `pg_dump` and `psql` matching the database server's major version. A successful dump from a newer client does not establish that it can be restored to an older server. The provider checks the `pg_dump` major version before creating a migration backup and rejects a mismatch, allowing core to stop before migrating. The custom server bundles do not include these tools; install them on the host or in your independently built image.

Jellyfin's built-in ZIP backup uses a separate JSON database import path. This custom branch implements the paired core completion hook: after rows are saved, it restarts sequences owned by mapped tables before the import transaction commits. Both imported rows and sequence restarts roll back if completion fails. This requires the compatible custom server; native SQL recovery tests alone do not establish ZIP restore support.

Two core limitations remain: copied configuration files are outside the database transaction, and ZIP backups omit `HomeSections`. Keep a tested PostgreSQL dump and the corresponding Jellyfin files as your complete recovery path.

Built-in backup transactions hold the [transaction lock](behaviour.md#transaction-locking); schedule them during quiet periods. Native `pg_dump` does not acquire the provider's advisory lock.

## Upgrading

Take a backup first. Custom prerelease `1.1.3.0-jf13-custom` pairs plugin `1.1.3.0` with the exact reconstructed core source described in [development](development.md#build-and-test). ABI `13.0.0.0` alone does not identify that source or guarantee the required hook.

Stop Jellyfin, install the matching server bundle in a separate directory, and replace the contents of its `PostgreSQL/` plugin directory with the paired ZIP. Keep only one `PostgreSQL*` plugin directory so the provider loader cannot select an older copy. Restart with the existing configuration/data paths and check the logs. Keep the previous binaries and matching database/files backup until validation is complete.

This prerelease has no Docker tag or dashboard upgrade route. For an independently built custom image, rebuild and replace both server and plugin together; pulling the stable `latest` image does not install these changes.

PostgreSQL major upgrades need their own database upgrade procedure. Changing `postgres:18` to a newer major tag is not sufficient.

## Troubleshooting

| Symptom | What to check |
| --- | --- |
| `No PostgreSQL password was configured` | Supply a password in the selected configuration source. Environment variables are ignored when `ConnectionString` is non-empty. |
| Custom database plugin not found | Install the plugin before starting Jellyfin; check the directory, assembly name, and file permissions. |
| Connection refused or authentication failed | Check PostgreSQL availability, host, port, role, and password. In Compose, use the service name `postgres`, not `localhost`. |
| `Could not run 'pg_dump'` | Install PostgreSQL client tools on Jellyfin's `PATH`. |
| `pg_dump` reports a server version mismatch | Install client tools matching the server's major version. Older clients cannot dump newer servers; newer-client dumps may not restore to older servers. |
| `Migration recovery requires pg_dump major version ...` | Put the matching major version of `pg_dump` on Jellyfin's `PATH`, including inside an independently built image. Check `pg_dump --version` inside the Jellyfin environment. |
| Entrypoint warns that `database.xml` is not `PLUGIN_PROVIDER` | An existing configuration was preserved. Review [Docker configuration](docker.md#configuration-and-storage). |
| Slow home screen | Check whether custom connection `Options` removed `-c jit=off`; see [performance](postgres-tuning.md). |

For unresolved errors, [open an issue](https://github.com/nintwentydo/jellyfin-plugin-postgresql/issues) with Jellyfin, plugin, and PostgreSQL versions; installation method; steps to reproduce; and relevant logs. Include the SQLSTATE and constraint name from database errors. Remove credentials and personal data before sharing.
