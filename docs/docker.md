# Docker

[Back to README](../README.md)

Custom prerelease **`1.1.3.0-jf13-custom`** ships a plugin ZIP and self-contained Linux ARM64/x64 server bundles, not a Docker image. The published `ghcr.io/nintwentydo/jellyfin-postgres:latest` image and stock plugin catalogue remain unchanged and do not contain this pair. Use the [manual installation](../README.md#install) or build your own image with both prerelease components.

## Build the image

The checked-in [Dockerfile](../docker/Dockerfile) and [Compose example](../docker/compose.example.yml) target the stock server and need adaptation before using this prerelease. A custom image must:

- Replace the entire server directory with the matching architecture's self-contained bundle; do not mix old and new server assemblies.
- Install the paired plugin ZIP and configure the PostgreSQL provider before starting Jellyfin.
- Provide web assets and FFmpeg, plus PostgreSQL client tools matching the database's major version.
- Preserve configuration/data and cache in writable mounts, and mount media read-only unless writes are intended.

The local ARM64 validation retained the base image's Jellyfin 12 web shell separately from the replaced server directory. API browsing, restart, ZIP restoration and native direct/HLS playback passed; this does not establish full Jellyfin 13 web/UI compatibility. No custom image is published by this release.

## Configuration and storage

The reusable [entrypoint](../docker/entrypoint.sh) installs the staged plugin into `/config/plugins/PostgreSQL/`, removes older `PostgreSQL_*` folders, and creates `/config/config/database.xml` if it is missing. The generated configuration reads the [environment variables](configuration.md#environment-variables).

**An existing `database.xml` is preserved.** If it selects SQLite, the entrypoint logs a warning and does not switch databases. Use a fresh configuration volume for a new PostgreSQL installation; changing this file does not migrate existing data.

PostgreSQL 18 uses `/var/lib/postgresql` for its data-volume mount; 17 and earlier use `/var/lib/postgresql/data`. Changing the mount or image major version does not move or upgrade an existing database.

When running Jellyfin with `user: "1000:1000"`, make `/config` and `/cache` writable by that UID and GID first. The entrypoint also honours `JELLYFIN_DATA_DIR` and `JELLYFIN_CONFIG_DIR` when using custom paths. Keep database credentials outside version control.
