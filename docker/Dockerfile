# The base supplies Linux dependencies, FFmpeg and the tested Jellyfin 12 web shell.
# The entire server is replaced with the paired, self-contained release bundle.
ARG JELLYFIN_TAG=12.0@sha256:baba630419915985442f315f08b0cf46d9f4c8a0cc4bd38e94a6d35751dd5ef5
FROM jellyfin/jellyfin:${JELLYFIN_TAG}

# add postgres client to jellyfin container
# Match the server major version so migration dumps can also be restored to it.
ARG PG_MAJOR=18
USER root
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl gnupg \
    && curl -fsSL https://www.postgresql.org/media/keys/ACCC4CF8.asc \
        | gpg --dearmor -o /usr/share/keyrings/postgresql.gpg \
    && echo "deb [signed-by=/usr/share/keyrings/postgresql.gpg] https://apt.postgresql.org/pub/repos/apt trixie-pgdg main" \
        > /etc/apt/sources.list.d/pgdg.list \
    && apt-get update \
    && apt-get install -y --no-install-recommends "postgresql-client-${PG_MAJOR}" \
    && apt-get purge -y gnupg \
    && apt-get autoremove -y \
    && rm -rf /var/lib/apt/lists/*

ARG JELLYFIN_TAG
ARG PLUGIN_VERSION=1.1.3.0
ARG IMAGE_REVISION

LABEL org.opencontainers.image.title="Jellyfin with PostgreSQL" \
      org.opencontainers.image.description="Custom Jellyfin 13 with PostgreSQL, a Jellyfin 12 web shell, pg_dump and psql" \
      org.opencontainers.image.source="https://github.com/nintwentydo/jellyfin-plugin-postgresql" \
      org.opencontainers.image.url="https://github.com/nintwentydo/jellyfin-plugin-postgresql" \
      org.opencontainers.image.documentation="https://github.com/nintwentydo/jellyfin-plugin-postgresql#readme" \
      org.opencontainers.image.licenses="GPL-3.0-only" \
      org.opencontainers.image.version="${PLUGIN_VERSION}-jf13-custom" \
      org.opencontainers.image.revision="${IMAGE_REVISION}" \
      org.opencontainers.image.core-tree="abccaa9a1811f6ba9a4493c3bfd9057567397ed6" \
      org.opencontainers.image.base.name="docker.io/jellyfin/jellyfin:${JELLYFIN_TAG}"

RUN test -d /jellyfin/jellyfin-web \
    && mv /jellyfin/jellyfin-web /opt/jellyfin-web-12 \
    && rm -rf /jellyfin
COPY --chmod=0755 docker/server/ /jellyfin/
COPY --chmod=0755 docker/plugin/ /opt/jellyfin-postgres/plugin/
COPY --chmod=0644 docker/database.xml /opt/jellyfin-postgres/database.xml
COPY --chmod=0755 docker/entrypoint.sh /entrypoint.sh

ENV JELLYFIN_WEB_DIR=/opt/jellyfin-web-12

ENTRYPOINT ["/entrypoint.sh"]
CMD ["/jellyfin/jellyfin"]
