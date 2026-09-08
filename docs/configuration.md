# Configuration

[Back to README](../README.md)

Configure the provider in `database.xml` and restart Jellyfin to apply changes. Jellyfin selects its database before plugins load, so the plugin's dashboard page is read-only.

## File locations

| Purpose | Bundled Docker image |
| --- | --- |
| Database configuration | `/config/config/database.xml` |
| Plugin files | `/config/plugins/PostgreSQL/` |
| Temporary migration backups | `/config/data/PostgresqlBackups/` |

For other installations, use Jellyfin's configured configuration, plugins, and data paths; they vary by installation method.

## database.xml

Replace the host and credentials with your PostgreSQL details:

```xml
<?xml version="1.0" encoding="utf-8"?>
<DatabaseConfigurationOptions>
  <DatabaseType>PLUGIN_PROVIDER</DatabaseType>
  <CustomProviderOptions>
    <PluginName>PostgreSQL</PluginName>
    <PluginAssembly>Jellyfin.Plugin.Postgresql.dll</PluginAssembly>
    <ConnectionString>Host=localhost;Port=5432;Database=jellyfin;Username=jellyfin;Password=CHANGEME</ConnectionString>
  </CustomProviderOptions>
  <LockingBehavior>NoLock</LockingBehavior>
</DatabaseConfigurationOptions>
```

Use `Host=postgres` for the example Compose stack. Keep `LockingBehavior` set to `NoLock`; the provider handles transaction locking itself. The role must own the database and be able to create tables, functions, and aggregates in its `public` schema.

To use environment variables, omit `ConnectionString` or leave it empty. A non-empty connection string replaces the environment settings entirely; they do not fill in missing fields. XML special characters in credentials must be escaped, such as `&amp;` for `&`.

## Environment variables

Set these in the **Jellyfin process or container** environment:

| Variable | Default |
| --- | --- |
| `POSTGRES_HOST` | `localhost` |
| `POSTGRES_PORT` | `5432` |
| `POSTGRES_DB` | `jellyfin` |
| `POSTGRES_USER` | `jellyfin` |
| `POSTGRES_PASSWORD` | Required |
| `POSTGRES_SSLMODE` | `Prefer` |
| `POSTGRES_COMMAND_TIMEOUT` | `30` seconds |

`POSTGRES_CONNECTION_STRING` is used only by the development tooling, not by a running Jellyfin server.

## Additional connection options

Add Npgsql settings inside `CustomProviderOptions`. These override values from either the connection string or the environment. For example, to limit the connection pool:

```xml
<Options>
  <CustomDatabaseOption>
    <Key>Maximum Pool Size</Key>
    <Value>20</Value>
  </CustomDatabaseOption>
</Options>
```

See the [Npgsql connection parameters](https://www.npgsql.org/doc/connection-string-parameters.html) for accepted keys and values.

Unless explicitly set, the plugin adds `Application Name=jellyfin+<version>` and `Options=-c jit=off`. If you supply your own `Options` value, include `-c jit=off` to retain that default—for example, `-c jit=off -c work_mem=32MB`.

For a database requiring custom TLS settings, configure the PostgreSQL client tools too. Migration backups run `pg_dump` and `psql` with the host, port, database, username, and password; Npgsql-specific TLS options are not forwarded. The tools inherit Jellyfin's environment and accept [libpq environment variables](https://www.postgresql.org/docs/18/libpq-envars.html), such as `PGSSLMODE` and `PGSSLROOTCERT`.

At startup, look for `PostgreSQL connection:` in the Jellyfin log to confirm the provider was selected. The password is removed from this message; successful startup and library use confirm the connection works.
