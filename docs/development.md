# Development

[Back to README](../README.md)

## Build and test

Install the **.NET 10 SDK**. CI reconstructs the compatible core in `.jellyfin-core` using [checkout-core](../.github/actions/checkout-core/action.yml). Its source pin is:

- Base: `46639d35ffe98b72a280ada4c436aa401728e4fb`, including the restore transaction and completion-hook fixes.
- UserData fix: `42b507efe1ccdf530714e46842e1e1131559c324`.
- ItemValues fix: `2d4850107a4ee60ebbfde662278eb42e45d11c27`.
- People fix: `544b893886f1c623295fa04ccf128cde7f6a6ae6`.

Use the same reconstruction steps locally, applying the fixes in that order. Set the source override from the plugin repository root for every build, test and EF tool command:

```sh
export JellyfinSourceRoot="$(pwd)/.jellyfin-core"
```

The paired core reports ABI `13.0.0.0`; matching the ABI alone does not establish compatibility with the required hook. Released Jellyfin 12 packages do not contain it. A one-command override also works: `dotnet test -p:JellyfinSourceRoot=/absolute/path/to/compatible-jellyfin`.

Then run from the plugin repository root:

```sh
dotnet build
dotnet test
sh docker/test-entrypoint.sh
```

Without `JELLYFIN_POSTGRES_TEST_CONNECTION`, the .NET tests inspect the EF model, generated SQL, connection defaults, and registered services; the database integration tests are skipped. The shell check exercises the entrypoint in a temporary directory and needs no Docker daemon.

### PostgreSQL integration tests

Use a PostgreSQL 15–18 instance dedicated to testing and install matching-major client tools (`pg_dump` and `psql`) on the test process's `PATH`. The tests create and drop uniquely named databases; the supplied role needs `LOGIN` and `CREATEDB`, but does not need superuser access. Do not use a production server.

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

The non-UTC timezone exercises UTC, Local and Unspecified dates across summer and winter, including nullable/required columns and query parameters. An invalid supplied connection fails the integration tests instead of skipping them. The suite also checks overlapping UserData inserts, composite-key isolation, ordinary constraint failures, synchronous lock waiting, cancellation, native SQL backup/recovery, and transactional sequence completion after importing explicit IDs. These tests use EF contexts directly; they do not start Jellyfin, prove cache/preference preservation in core save paths, or exercise actual ZIP serialization. Verify ZIP restore, startup, scans, and playback against the paired custom server before deployment.

The [test workflow](../.github/workflows/test.yaml) runs a PostgreSQL 15, 16, 17 and 18 matrix with this restricted test role, matching client tools, and the pending-model check below. Each database requires verified TLS and a client certificate. Npgsql uses an encrypted PFX; `pg_dump` and `psql` use separate PEM certificate/key files. Tests cover successful native recovery and rejection of an untrusted CA, incorrect hostname and missing native client certificate.

For the same certificate checks locally, [setup-postgresql-tls.sh](../tests/setup-postgresql-tls.sh) takes a disposable PostgreSQL container ID and a new certificate directory. It generates test certificates and replaces that container's authentication rules. Set the TLS connection string, `JELLYFIN_POSTGRES_TEST_TLS_DIRECTORY`, `PGSSLCERT` and `PGSSLKEY` as shown in the workflow before running `dotnet test`. Without the certificate directory variable, the additional TLS cases are skipped; ordinary database tests still run when their connection variable is supplied.

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
dotnet restore
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

The custom release tag is **`1.1.3.0-jf13-custom`**. Plugin metadata keeps the numeric version `1.1.3.0` and target ABI `13.0.0.0`. The tag and release description identify the custom dependency; install the plugin only with its paired server.

The [release workflow](../.github/workflows/release.yaml) uses the pinned reconstructed source and gates publication on tests. It attaches the plugin ZIP, self-contained Linux ARM64/x64 server bundles, checksums and source references as a prerelease. It does not publish a Docker image, update `manifest.json`, or change the stable release channel. Run build, test, model and CodeQL checks against the same source pin before publishing.

Only the three DLLs listed in [build.yaml](../build.yaml), plus plugin metadata, belong in the plugin ZIP. Jellyfin supplies its other runtime assemblies; do not package the entire plugin build directory. Server bundles must use `dotnet publish --self-contained true` for each runtime identifier so they include the .NET runtime. Web assets, FFmpeg and PostgreSQL client tools are separate deployment requirements.

Local runtime validation covered authenticated browsing, saved state, restart, actual ZIP restoration and direct/HLS playback on ARM64. It retained a Jellyfin 12 web shell; full Jellyfin 13 web/UI compatibility and x64 runtime playback have not been validated. See [operations](operations.md) for the remaining ZIP-backup limitations.
