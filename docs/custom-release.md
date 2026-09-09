This custom prerelease pairs PostgreSQL plugin **1.1.3.0** with a custom **Jellyfin 13.0.0** server. Install both from this release. The plugin is incompatible with stock Jellyfin 12, and ABI 13 alone does not establish compatibility with another server build.

The server includes five core fixes: duplicate UserData reattachment, orphan ItemValues cleanup, orphan people cleanup, atomic database restore, and preservation of private/generated IDs with a provider completion hook. The plugin restarts owned PostgreSQL sequences inside the restore transaction and retains its advisory write lock and prior recovery/TLS hardening. No database migration is added.

Assets include the plugin ZIP, self-contained Linux x64 and ARM64 server bundles, `SHA256SUMS`, and exact public source references/reconstruction instructions. The server bundles include .NET; web assets, FFmpeg, PostgreSQL client tools and Linux native dependencies must be supplied separately. Use matching-major `pg_dump` and `psql` for PostgreSQL 15–18.

Validation before publication covered 3,796 passing core tests (eight known/platform skips), 43 plugin tests on each PostgreSQL 15–18, real ZIP restore/rollback checks, actual PostgreSQL reattachment/deletion paths, and ARM64 server startup, saved state, direct/HLS playback and HTTP ZIP restoration. This release workflow also builds both server architectures and gates publication on the PostgreSQL TLS matrix and CodeQL.

Known limits:

- Core ZIP backups still omit HomeSections. Keep a native PostgreSQL dump and corresponding Jellyfin files for complete recovery.
- Database rollback does not undo copied configuration/data files.
- ARM64 runtime validation retained a Jellyfin 12 web shell; full Jellyfin 13 web/UI compatibility and x64 runtime playback remain unvalidated.
- This is a single-server provider and has no SQLite migration tool.

This prerelease does not publish a Docker image, update the plugin catalogue, or replace the stable/latest release. Test in a separate installation before upgrading an existing server.
