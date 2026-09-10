# Development

[Back to README](../README.md)

## Build and test

Install the **.NET 10 SDK** and check out the exact source used by the released base image, then run from the repository root:

```sh
git clone --no-checkout https://github.com/nintwentydo/jellyfin.git .jellyfin-core
git -C .jellyfin-core checkout --detach fd3c85888d100f3a609d11a68bfee736b9b5b0c5
dotnet build
dotnet test
sh docker/test-entrypoint.sh
python3 docker/test-stage-plugin.py
```

The source commit is the `v12.0.0-nintwentydo.1` release. `.jellyfin-core/` is ignored by Git and Docker. `JellyfinSourceRoot` can point to an existing checkout of that commit instead. Plugin, tests, packaging and CodeQL all use these source references; stock NuGet packages do not contain the required interface hook.

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

The non-UTC timezone exercises UTC, Local and Unspecified dates across summer and winter, including nullable/required columns and query parameters. An invalid supplied connection fails the integration tests instead of skipping them. The suite also checks overlapping UserData inserts, composite-key isolation, ordinary constraint failures, synchronous lock waiting, cancellation and native SQL backup/recovery. Six generated-ID restore regressions cover populated and empty tables, quoted and dotted identifiers, custom increments and sequence ownership, rollback after failure, and the required transaction. These tests use EF contexts directly; they do not start Jellyfin, prove cache/preference preservation in core save paths, or validate built-in ZIP restore. Verify startup, scans, and playback flows against a disposable Jellyfin/PostgreSQL installation before releasing provider changes.

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

The candidate is **1.1.4.0**, paired with **Jellyfin v12.0.0-nintwentydo.1**. It remains ABI `12.0.0.0`, but requires the fork's restore hook. This release does not update the existing `manifest.json` catalogue or Docker `latest` tag. GitHub marks it as a prerelease so catalogue generators that exclude prereleases also leave it out.

1. Review the version and changelog in [build.yaml](../build.yaml), [release notes](release-notes.md), the source commit in [checkout-core](../.github/actions/checkout-core/action.yml), and the matching image digest in [Dockerfile](../docker/Dockerfile). Commit the complete recipe before tagging.
2. Push the review branch and manually run **Release Plugin** with **publish unchecked**. This packages one plugin ZIP, runs the PostgreSQL 15–18 TLS suite and migration-model check, and runs CodeQL. Both native Linux runners then build the image from that same ZIP and check the active plugin, payload hashes, PostgreSQL tools, authenticated persistence across restart, a real ZIP restore followed by a generated-ID insert, and direct/HLS decoding of generated test media. Validation-only runs publish no release or container image.
3. Before publication, test an upgrade of a disposable copy of the populated database and Jellyfin files from the version you actually use. Check users, libraries, preferences, scans and playback; retain a matched database/files backup for rollback. The fresh-install image checks do not establish populated upgrade safety, and the provider rollback tests do not establish filesystem rollback during ZIP restore.
4. Create and push a tag matching `build.yaml` (`1.1.4.0`, optionally prefixed with `v`). Run **Release Plugin** on that tag with **publish checked**. It repeats validation, pushes each tested architecture under a unique candidate tag, joins their digests into the versioned image tag, and creates the GitHub prerelease with the shared ZIP, checksums and provenance. Existing versioned image tags are refused; a changed image needs a new plugin version.

A failed architecture prevents the shared tag and GitHub release. If publication fails after the shared image tag was created, inspect the run and finish attaching that run's tested assets manually; do not rebuild over the tag. Run-specific candidate image tags may remain after failed publication runs.

Only the three DLLs listed in `build.yaml`, plus `meta.json`, are shipped. Jellyfin supplies the other runtime assemblies; do not distribute the entire build directory. For local image builds, see [Docker](docker.md#build-the-image).
