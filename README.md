# Jellyfin PostgreSQL Plugin

> [!WARNING]
> Highly experimental. Built and tested against Jellyfin v12 RC7. Don't run on production servers.

Replace Jellyfin's database with PostgreSQL. Available as a plugin for manual install or pre-packaged jellyfin docker image.

Inspired by [Jellyfin.Pgsql](https://github.com/JPVenson/Jellyfin.Pgsql), have followed their lead with `pg_dump` backup approach. And also credit to [canepan/jellyfin-plugin-mysql](https://github.com/canepan/jellyfin-plugin-mysql) for their use of `ReplaceService`, have used similar pattern to fix `ILIKE` and collation issues.

Important disclosure / prewarning, I'm pretty inexperienced with C# and .NET so using this as a learning exercise. Used Claude to give me a head start. And there'll probably be some breaking changes as I test this out more and get more confident. So far have tested against a ~13k-item library (scan, search, sort, playback, restart, backup), and sqlite-parity fixes pinned by tests.

## Requirements
- Jellyfin 12 (RC7)
- PostgreSQL 15+ (tested against 17 and 18)
- `pg_dump` and `psql` on `PATH`, at a major version >= the server's (for Jellyfin to back the database up). Already present if you use the Docker image below

Currently only works on a fresh install. Have not attempted an SQLite->PostgreSQL conversion. Jellyfin's migration routines have SQLite-specific stuff that fails on Postgres.

## Install

### Docker
Example compose stack: [docker/compose.example.yml](docker/compose.example.yml).

`ghcr.io/nintwentydo/jellyfin-postgres` is the official Jellyfin image with the plugin, `pg_dump`, and `psql` baked in. On start it installs the plugin and seeds config file.

Existing `database.xml` config is never overwritten, so swapping between manual installs and this image is safe.

### Manual
Plugin repo: `https://raw.githubusercontent.com/nintwentydo/jellyfin-plugin-postgresql/master/manifest.json`

1. Create a database and a role that owns it
2. Install the plugin, either from the repo above in the dashboard or by copying a release into `<config>/plugins/PostgreSQL/`
3. Add your config to `<config>/config/database.xml` (see below)
4. Start Jellyfin. Schema created on first run

n.b. Jellyfin loads provider assembly during service registration, before the plugin system runs. So it won't boot unless the plugin is already on disk. If you want to install via the UI then you'll need to start on SQLite, install, write `database.xml`, then restart.

### Config example
```xml
<?xml version="1.0" encoding="utf-8"?>
<DatabaseConfigurationOptions>
  <DatabaseType>PLUGIN_PROVIDER</DatabaseType>
  <CustomProviderOptions>
    <PluginName>PostgreSQL</PluginName>
    <PluginAssembly>Jellyfin.Plugin.Postgresql.dll</PluginAssembly>
    <ConnectionString>Host=db;Port=5432;Database=jellyfin;Username=jellyfin;Password=CHANGEME</ConnectionString>
  </CustomProviderOptions>
  <LockingBehavior>NoLock</LockingBehavior>
</DatabaseConfigurationOptions>
```
n.b. Keep `LockingBehavior` on `NoLock` for best performance

### Environment variables
Used when `ConnectionString` is absent/empty.

| Environment variable | Default |
| --- | --- |
| `POSTGRES_HOST` | `localhost` |
| `POSTGRES_PORT` | `5432` |
| `POSTGRES_DB` | `jellyfin` |
| `POSTGRES_USER` | `jellyfin` |
| `POSTGRES_PASSWORD` | (required) |
| `POSTGRES_SSLMODE` | `Prefer` |
| `POSTGRES_COMMAND_TIMEOUT` | 30 |

Additional options can be passed as `CustomProviderOptions/Options` entries in `database.xml`. Refer to [Npgsql docs](https://www.npgsql.org/doc/connection-string-parameters.html).

## Building
```
dotnet build
dotnet test
```

Requires .NET 10 SDK. Releases ship only the assemblies listed in `build.yaml`. Rest of the build output is provided by the server at runtime.

### Migrations
```
dotnet tool restore
dotnet tool run dotnet-ef migrations add <Name> --project Jellyfin.Plugin.Postgresql --output-dir Migrations
```

## Implementation / extra notes
Jellyfin's queries assume SQLite semantics, so a few things need to be fixed to work with Postgres.

Concurrent playback-progress saves race in the server, which SQLite masks in-process but Postgres throws a duplicate key error. So `UserData` inserts are turned into upserts.

`LIKE` queries are case-insensitive in sqlite, so get written to `ILIKE` for Postgres. e.g. searching "mean girl" misses "Mean Girls"

Text compares byte-ordinally in sqlite, so `COLLATE "C"` on each string column in postgres. Prevents reshuffling `SortName` order and the A-Z bar.

`min()` works on any type in sqlite, but `min(uuid)` fails in postgres, so migration defines min/max aggregates for `uuid`

There will probably be more quirks that come out, but at least (so far) haven't had any noticeable problems.
