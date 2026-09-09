# Docker

[Back to README](../README.md)

Use the custom prerelease image for `linux/amd64` or `linux/arm64`:

```sh
docker pull ghcr.io/nintwentydo/jellyfin-postgres:1.1.3.0-jf13-custom
```

It combines the exact checksummed server/plugin assets from [the prerelease](https://github.com/nintwentydo/jellyfin-plugin-postgresql/releases/tag/1.1.3.0-jf13-custom), plus FFmpeg, a Jellyfin 12 web shell and PostgreSQL 18 client tools. The stable `latest` image and plugin catalogue remain unchanged. The [manual installation](../README.md#manual) remains available.

## Start a new server

1. Save [compose.example.yml](../docker/compose.example.yml) as `compose.yml` in a new directory. Set the Jellyfin service's image to the custom tag:

   ```yaml
   image: ghcr.io/nintwentydo/jellyfin-postgres:1.1.3.0-jf13-custom
   ```

2. Create a private `.env` file beside it with `POSTGRES_PASSWORD='replace-with-a-long-random-password'`, and replace `/path/to/media` in Compose with your media directory.
3. Start the stack:

   ```sh
   docker compose up -d
   docker compose logs -f jellyfin
   ```

4. Open port **8096** on your Docker host and finish setup. Add media libraries from `/data`.

The example uses PostgreSQL 18, matching the image's client tools. For PostgreSQL 15–17, use the manual installation or an image with matching-major `pg_dump` and `psql`. The provider rejects migration backups with mismatched client tools.

## Configuration and storage

The [entrypoint](../docker/entrypoint.sh) installs the bundled plugin into `/config/plugins/PostgreSQL/`, removes older `PostgreSQL_*` folders, and creates `/config/config/database.xml` if it is missing. The generated configuration reads the [environment variables](configuration.md#environment-variables).

**An existing `database.xml` is preserved.** If it selects SQLite, the entrypoint logs a warning and does not switch databases. Use a fresh configuration volume for a new PostgreSQL installation; changing this file does not migrate existing data.

The example persists database, configuration and cache in named volumes and mounts media read-only. PostgreSQL 18 uses `/var/lib/postgresql` for its data-volume mount; 17 and earlier use `/var/lib/postgresql/data`. Changing the mount or image major version does not move or upgrade an existing database.

When running Jellyfin with `user: "1000:1000"`, make `/config` and `/cache` writable by that UID and GID first. The entrypoint also honours `JELLYFIN_DATA_DIR` and `JELLYFIN_CONFIG_DIR` when using custom paths. Keep database credentials outside version control.

## Known limitations

- The image retains a Jellyfin 12 web shell. Prior local ARM64 API browsing, restart, ZIP restoration and direct/HLS playback passed, but full Jellyfin 13 web/UI compatibility and x64 runtime playback remain unvalidated.
- Core ZIP backups omit `HomeSections`, and database rollback does not undo copied configuration/data files. Keep a tested PostgreSQL dump and the corresponding Jellyfin files; see [backup guidance](operations.md#backups).
- This remains a single-server provider with no SQLite migration tool. Use the paired custom server/plugin together; ABI `13.0.0.0` alone does not identify a compatible core build.
