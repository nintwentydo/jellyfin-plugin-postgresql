# Jellyfin PostgreSQL Plugin

Replace Jellyfin's database with PostgreSQL.

> [!WARNING]
> Highly experimental. Only built and tested against Jellyfin v12 RC7. Don't use this with data you can't afford to lose.

Inspired by [Jellyfin.Pgsql](https://github.com/JPVenson/Jellyfin.Pgsql), have followed their lead with `pg_dump` backup approach. And also credit to [canepan/jellyfin-plugin-mysql](https://github.com/canepan/jellyfin-plugin-mysql) for their use of `ReplaceService`, have used similar pattern to fix `ILIKE` and collation issues.

Important disclosure / prewarning, I'm pretty inexperienced with C# and .NET so using this as a learning exercise. Used Claude to give me a head start. And there'll probably be some breaking changes as I test this out more and get more confident. So far have tested against a ~13k-item library (scan, search, sort, playback, restart, backup), and sqlite-parity fixes pinned by tests.

## Requirements
- Jellyfin 12 (RC7)
- PostgreSQL 15+ (tested against 17)
- `pg_dump` and `psql` on `PATH` (for Jellyfin to backup the database)

Currently only works on a fresh install. Have not attempted an SQLite->PostgreSQL conversion. Jellyfin's migration routines have SQLite-specific stuff that fails on Postgres.

## Install
Plugin repo: `https://raw.githubusercontent.com/nintwentydo/jellyfin-plugin-postgresql/master/manifest.json`

1. Add the plugin repo in the dashboard, or copy a release into `<config>/plugins/PostgreSQL/`
2. Create a database and a role that owns it
3. Add your config to `<config>/config/database.xml` (see example below)
4. Start Jellyfin. Schema created on first run

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
