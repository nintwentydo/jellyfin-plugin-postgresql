# Development

[Back to README](../README.md)

## Build and test

Install the **.NET 10 SDK**, then run from the repository root:

```sh
dotnet build
dotnet test
sh docker/test-entrypoint.sh
```

Without `JELLYFIN_POSTGRES_TEST_CONNECTION`, the .NET tests inspect the EF model, generated SQL, connection defaults, and registered services; the database integration tests are skipped. The shell check exercises the entrypoint in a temporary directory and needs no Docker daemon.

### PostgreSQL integration tests

Use a PostgreSQL 18 instance dedicated to testing and install PostgreSQL 18 client tools (`pg_dump` and `psql`) on the test process's `PATH`. The tests create and drop uniquely named databases; the supplied role needs `LOGIN` and `CREATEDB`, but does not need superuser access. Do not use a production server.

For example, provision the test role through an administrator connection to the disposable instance:

```sql
CREATE ROLE jellyfin_test WITH LOGIN CREATEDB NOSUPERUSER PASSWORD 'jellyfin_test';
```

Then run the suite from a POSIX shell, replacing the host and port if needed:

```sh
TZ=Australia/Melbourne \
JELLYFIN_POSTGRES_TEST_CONNECTION='Host=localhost;Port=5432;Database=postgres;Username=jellyfin_test;Password=jellyfin_test' \
dotnet test
```

The non-UTC timezone exercises local date conversion. An invalid supplied connection fails the integration tests instead of skipping them. These checks exercise the provider against a real database, including native SQL backup/recovery; they do not start Jellyfin or validate built-in ZIP restore. Verify startup, scans, and playback flows against a disposable Jellyfin/PostgreSQL installation before releasing provider changes.

The [test workflow](../.github/workflows/test.yaml) retains the fast tests and entrypoint check, and adds a PostgreSQL 18 service with this restricted test role, matching client tools, and the pending-model check below.

## Code map

Files below are in [Jellyfin.Plugin.Postgresql/](../Jellyfin.Plugin.Postgresql/).

| File or directory | Responsibility |
| --- | --- |
| `Database/PostgresqlDatabaseProvider.cs` | Provider registration, optimisation, backup, restore, and purge. |
| `Database/PostgresqlConnectionSettings.cs` | Configuration precedence and connection defaults. |
| `Database/PostgresqlModelCustomizer.cs` | Text collation and UTC conversion. |
| `Database/*Query*`, `Database/PostgresqlUpdateSqlGenerator.cs` | Search, null ordering, and playback-state upserts. |
| `Database/WriteSerialisingTransactionInterceptor.cs` | Transaction advisory lock. |
| `Database/PostgresqlDesignTimeJellyfinDbFactory.cs`, `Migrations/` | Schema tooling and migrations. |
| `Plugin.cs`, `Configuration/` | Dashboard identity and read-only configuration page. |

See [provider behaviour](behaviour.md) for the reasons behind the customisations. The SQL generators use Npgsql internals; recheck them when updating that dependency.

## Migrations

Restore the local EF tool and check whether the model has changed:

```sh
dotnet tool restore
dotnet tool run dotnet-ef migrations has-pending-model-changes --project Jellyfin.Plugin.Postgresql
```

To scaffold a migration, replace `DescribeTheChange` with its name:

```sh
dotnet tool run dotnet-ef migrations add DescribeTheChange --project Jellyfin.Plugin.Postgresql --output-dir Migrations
```

Scaffolding uses the same provider setup as runtime and does not connect to PostgreSQL. The design-time factory reads `POSTGRES_CONNECTION_STRING`; otherwise it uses placeholder localhost credentials. Set a connection explicitly for commands that access a database.

The repository currently has one `InitialCreate` migration. Review generated changes and verify an upgrade from an existing PostgreSQL database before shipping a schema change.

## Releases

1. Keep Jellyfin package versions aligned in the plugin and test projects, and match `JELLYFIN_TAG` in [docker/Dockerfile](../docker/Dockerfile).
2. Run the checks above. Bump the four-part version and changelog in [build.yaml](../build.yaml).
3. Commit and publish a GitHub release with a tag matching that version.

The [release workflow](../.github/workflows/release.yaml) packages the plugin, attaches the ZIP and checksum, builds both Docker architectures, publishes versioned and `latest` image tags, and updates `manifest.json`. Pre-releases are excluded.

Only the three DLLs listed in `build.yaml` are shipped. Jellyfin supplies the other runtime assemblies; do not distribute the entire build directory. For local image builds, see [Docker](docker.md#build-the-image).
