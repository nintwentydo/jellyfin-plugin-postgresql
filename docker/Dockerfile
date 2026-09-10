# Jellyfin with PostgreSQL provider baked in
#
# Build from the JPRM release ZIP staged by docker/stage-plugin.py.
# See docs/docker.md for local build arguments.
#
# Released fork: v12.0.0-nintwentydo.1, commit fd3c85888d100f3a609d11a68bfee736b9b5b0c5.
ARG JELLYFIN_BASE=ghcr.io/nintwentydo/jellyfin:12.0.0-nintwentydo.1@sha256:9e6c9327eaed74fa5ae4cbe5e8b7ae14c530b2fc7f54703c0939e01d9a2a2784
FROM ${JELLYFIN_BASE}

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

ARG JELLYFIN_BASE
ARG PLUGIN_VERSION=0.0.0.0
ARG PLUGIN_REVISION=unknown

LABEL org.opencontainers.image.title="Jellyfin with PostgreSQL" \
      org.opencontainers.image.description="Jellyfin with the PostgreSQL provider, pg_dump and psql preinstalled" \
      org.opencontainers.image.source="https://github.com/nintwentydo/jellyfin-plugin-postgresql" \
      org.opencontainers.image.url="https://github.com/nintwentydo/jellyfin-plugin-postgresql" \
      org.opencontainers.image.documentation="https://github.com/nintwentydo/jellyfin-plugin-postgresql#readme" \
      org.opencontainers.image.licenses="GPL-3.0-only" \
      org.opencontainers.image.version="${PLUGIN_VERSION}" \
      org.opencontainers.image.revision="${PLUGIN_REVISION}" \
      org.opencontainers.image.base.name="${JELLYFIN_BASE}"

COPY --chmod=0755 docker/plugin/ /opt/jellyfin-postgres/plugin/
COPY --chmod=0644 docker/database.xml /opt/jellyfin-postgres/database.xml
COPY --chmod=0755 docker/entrypoint.sh /entrypoint.sh

ENTRYPOINT ["/entrypoint.sh"]
CMD ["/jellyfin/jellyfin"]
