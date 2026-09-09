# Jellyfin PostgreSQL Plugin

Plugin to replace Jellyfin's SQLite database with PostgreSQL.

**Custom prerelease: `1.1.3.0-jf13-custom`.** Use the custom Docker image or install its PostgreSQL plugin together with the paired custom Jellyfin 13 server. The [prerelease assets](https://github.com/nintwentydo/jellyfin-plugin-postgresql/releases/tag/1.1.3.0-jf13-custom) contain the plugin ZIP, self-contained Linux ARM64/x64 server bundles, checksums and source references. This plugin requires the paired core fixes; it is incompatible with stock Jellyfin 12. The stable branch, plugin catalogue and `latest` Docker image remain unchanged.

**Fresh installs only**. There is no SQLite migration tool implemented in this project and I don't know if I'll provide one. This is a hobby project.

## Requirements

- The paired custom Jellyfin 13 server and plugin `1.1.3.0` (ABI `13.0.0.0`)
- PostgreSQL 15–18
- `pg_dump` and `psql` matching PostgreSQL's major version on Jellyfin's `PATH`
- For manual installs, supply Jellyfin web assets and FFmpeg separately from the server bundle

## Install

### Docker

Use `ghcr.io/nintwentydo/jellyfin-postgres:1.1.3.0-jf13-custom` for `linux/amd64` or `linux/arm64`. It bundles the paired server and plugin; follow the [Docker setup guide](docs/docker.md) for PostgreSQL, credentials and storage.

### Manual

Download the paired [prerelease assets](https://github.com/nintwentydo/jellyfin-plugin-postgresql/releases/tag/1.1.3.0-jf13-custom). The server bundles include the .NET runtime; web assets, FFmpeg and PostgreSQL client tools are separate requirements.

1. Create an empty PostgreSQL database and a login role that owns it. For example, run as a PostgreSQL administrator:

   ```sql
   CREATE ROLE jellyfin LOGIN PASSWORD 'replace-with-a-password';
   CREATE DATABASE jellyfin OWNER jellyfin;
   ```

2. Stop Jellyfin. Verify the prerelease checksums and install the server bundle for your architecture in a separate directory. Extract the paired plugin ZIP into a `PostgreSQL` folder in Jellyfin's plugins directory.
3. Save [the database configuration](docs/configuration.md#databasexml) as `database.xml` in Jellyfin's configuration directory. Set the connection details and install the client tools above.
4. Start the paired custom server with your configuration/data paths, web directory and FFmpeg configured. It creates the schema automatically. Finish setup in the web interface.

The paired plugin must be on disk before Jellyfin starts with PostgreSQL selected. The stock dashboard catalogue does not supply this custom pair. See [operations](docs/operations.md) for backup and upgrade limits and [Docker limitations](docs/docker.md#known-limitations) for the retained Jellyfin 12 web shell.

## Guides

- [Configuration](docs/configuration.md) — connection settings and environment variables.
- [Operations](docs/operations.md) — backups, upgrades, and troubleshooting.
- [Development](docs/development.md) — build, test, and release.
- [Provider behaviour](docs/behaviour.md) — how the plugin adapts Jellyfin's queries.
- [Performance](docs/postgres-tuning.md) — optional tuning.

## Credits

Inspired by [Jellyfin.Pgsql](https://github.com/JPVenson/Jellyfin.Pgsql)'s backup approach and [jellyfin-plugin-mysql](https://github.com/canepan/jellyfin-plugin-mysql)'s EF service replacements. See [LICENSE](LICENSE).
