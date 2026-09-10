# Docker

[Back to README](../README.md)

`ghcr.io/nintwentydo/jellyfin-postgres` extends the released `ghcr.io/nintwentydo/jellyfin:12.0.0-nintwentydo.1` fork image (pinned by digest) with the plugin and PostgreSQL client tools. Release images support `linux/amd64` and `linux/arm64`.

## Start a new server

1. Save [compose.example.yml](../docker/compose.example.yml) as `compose.yml` in a new directory.
2. Create a `.env` file beside it, replacing the example password:

   ```dotenv
   POSTGRES_PASSWORD='replace-with-a-long-random-password'
   ```

3. Change `/path/to/media` in `compose.yml` to your media directory. Jellyfin sees this mount as `/data`.
4. Start the stack:

   ```sh
   docker compose up -d
   docker compose logs -f jellyfin
   ```

5. Open port **8096** on your Docker host in a browser and complete Jellyfin's setup wizard. Add your library from `/data`.

The example creates named volumes for PostgreSQL, Jellyfin configuration, and cache. Keep `.env` private and out of version control.

To use an existing PostgreSQL server, remove the `postgres` service and Jellyfin's `depends_on` entry, then set Jellyfin's `POSTGRES_*` variables to that server's details. Create an empty database owned by the configured role first. The published image includes PostgreSQL 18 tools; for a PostgreSQL 15–17 server, build an image with the matching `PG_MAJOR` below so migration recovery uses compatible tools.

## Image versions

The example selects the 1.1.4.0 candidate; it becomes available after publication. This fork release leaves `latest` unchanged. Version tags in the [published images](https://github.com/nintwentydo/jellyfin-plugin-postgresql/pkgs/container/jellyfin-postgres) identify both the plugin and Jellyfin build:

```text
ghcr.io/nintwentydo/jellyfin-postgres:1.1.4.0-jf12.0.0-nintwentydo.1
```

The tag identifies both versions. Upgrade the bundled plugin by changing the image tag; dashboard updates are replaced on the next container start. See [upgrading](operations.md#upgrading).

## Configuration and storage

At startup, the image installs its plugin into `/config/plugins/PostgreSQL/`, removes older `PostgreSQL_*` folders, and creates `/config/config/database.xml` if it is missing. The generated configuration reads the [environment variables](configuration.md#environment-variables).

**An existing `database.xml` is preserved.** If it selects SQLite, the image logs a warning and does not switch databases. Use a fresh configuration volume for a new PostgreSQL installation; changing this file does not migrate existing data.

PostgreSQL 18 uses `pgdata:/var/lib/postgresql`; 17 and earlier use `pgdata:/var/lib/postgresql/data`. The example follows the [PostgreSQL 18 image layout](https://github.com/docker-library/postgres/blob/master/18/bookworm/Dockerfile). Changing the mount or image major version does not move or upgrade an existing database.

To run Jellyfin with `user: "1000:1000"`, make `/config` and `/cache` writable by that UID and GID first. The entrypoint also honours `JELLYFIN_DATA_DIR` and `JELLYFIN_CONFIG_DIR` when using custom paths.

## Build the image

From the repository root, stage the release ZIP matching `build.yaml` into a new `docker/plugin/` directory, then run:

```sh
python3 docker/stage-plugin.py /path/to/postgresql_1.1.4.0.zip
docker build -f docker/Dockerfile --build-arg PLUGIN_VERSION=1.1.4.0 -t jellyfin-postgres .
```

The default `JELLYFIN_BASE` is the released fork image with its immutable multiarch digest. Keep it matched to the source used to build the plugin. `PG_MAJOR` defaults to `18`; match the client tools to your PostgreSQL server. `PLUGIN_VERSION` and `PLUGIN_REVISION` label the plugin build. The Dockerfile adds only the plugin, client tools and entrypoint; it does not rebuild or replace Jellyfin.

For example, to use PostgreSQL 16:

```sh
docker build -f docker/Dockerfile --build-arg PLUGIN_VERSION=1.1.4.0 --build-arg PG_MAJOR=16 -t jellyfin-postgres:pg16 .
```

Use that locally built image in Compose. A newer `pg_dump` can read an older server, but its output is not guaranteed to restore to that older version; migration recovery requires both directions. See the [PostgreSQL compatibility notes](https://www.postgresql.org/docs/18/app-pgdump.html).
