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

Jellyfin's built-in ZIP backup uses a separate JSON database import path. PostgreSQL restore through that path remains unvalidated: core changes are needed for atomic import and provider-specific identity-sequence completion. Use a tested PostgreSQL dump plus the corresponding Jellyfin files for recovery until those changes are available and verified. The native SQL recovery tests do not establish built-in ZIP restore support.

Built-in backup transactions hold the [transaction lock](behaviour.md#transaction-locking); schedule them during quiet periods. Native `pg_dump` does not acquire the provider's advisory lock.

## Upgrading

Take a backup first. Match the plugin release to your Jellyfin version, including any release-candidate suffix; the `12.0.0.0` ABI value alone does not identify an exact build.

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
| `pg_dump` reports a server version mismatch | Update the client tools; they cannot be older than the server. |
| Entrypoint warns that `database.xml` is not `PLUGIN_PROVIDER` | An existing configuration was preserved. Review [Docker configuration](docker.md#configuration-and-storage). |
| Slow home screen | Check whether custom connection `Options` removed `-c jit=off`; see [performance](postgres-tuning.md). |

For unresolved errors, [open an issue](https://github.com/nintwentydo/jellyfin-plugin-postgresql/issues) with Jellyfin, plugin, and PostgreSQL versions; installation method; steps to reproduce; and relevant logs. Include the SQLSTATE and constraint name from database errors. Remove credentials and personal data before sharing.
