# Jellyfin PostgreSQL Plugin

Plugin to replace Jellyfin's SQLite database with PostgreSQL. Install the plugin yourself or use the bundled Docker image, which includes Jellyfin, the plugin, and postgres tools.

**Fresh installs only**. There is no SQLite migration tool implemented in this project and I don't know if I'll provide one. This is a hobby project.

## Requirements
- Jellyfin 12
- PostgreSQL 15–18
- `pg_dump` and `psql` matching the server's major version on Jellyfin's `PATH` for migration backup and recovery. The bundled Docker image includes version 18; [build with a matching `PG_MAJOR`](docs/docker.md#build-the-image) for an older server.

## Install

### Docker

Use [the Docker setup guide](docs/docker.md) and [example Compose file](docker/compose.example.yml). Set a password, point the media mount at your library, then start the stack. The image installs the plugin and creates its configuration on first start.

### Manual

1. Create an empty PostgreSQL database and a login role that owns it. For example, run as a PostgreSQL administrator:

   ```sql
   CREATE ROLE jellyfin LOGIN PASSWORD 'replace-with-a-password';
   CREATE DATABASE jellyfin OWNER jellyfin;
   ```

2. Stop Jellyfin. Extract the matching [release ZIP](https://github.com/nintwentydo/jellyfin-plugin-postgresql/releases) into a `PostgreSQL` folder in Jellyfin's plugins directory.
3. Save [the database configuration](docs/configuration.md#databasexml) as `database.xml` in Jellyfin's configuration directory. Set the connection details and install the client tools above.
4. Start Jellyfin. It creates the schema automatically. Finish setup in the web interface.

The plugin must be on disk before Jellyfin starts with PostgreSQL selected. To install through the dashboard instead, start a fresh instance on SQLite, add [this plugin repository](https://raw.githubusercontent.com/nintwentydo/jellyfin-plugin-postgresql/master/manifest.json), and install PostgreSQL. Then stop Jellyfin and follow steps 3–4. This does not migrate the SQLite database.

## Guides

- [Configuration](docs/configuration.md) — connection settings and environment variables.
- [Operations](docs/operations.md) — backups, upgrades, and troubleshooting.
- [Development](docs/development.md) — build, test, and release.
- [Provider behaviour](docs/behaviour.md) — how the plugin adapts Jellyfin's queries.
- [Performance](docs/postgres-tuning.md) — optional tuning.

## Credits

Inspired by [Jellyfin.Pgsql](https://github.com/JPVenson/Jellyfin.Pgsql)'s backup approach and [jellyfin-plugin-mysql](https://github.com/canepan/jellyfin-plugin-mysql)'s EF service replacements. See [LICENSE](LICENSE).
