# Jellyfin with PostgreSQL provider baked in
#
# Built from docker/plugin which CI fills with the JPRM release artifact
# To build manually, unzip a release into docker/plugin/, then
#   docker build -f docker/Dockerfile -t jellyfin-postgres .
#
# JELLYFIN_TAG pinned to version used to compile plugin
ARG JELLYFIN_TAG=12.0-rc7
FROM jellyfin/jellyfin:${JELLYFIN_TAG}

# add postgres client to jellyfin container
# make sure postgres client is >= major version used on the postgres container
ARG PG_MAJOR=18
USER root
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl gnupg \
    && curl -fsSL https://www.postgresql.org/media/keys/ACCC4CF8.asc \
        | gpg --dearmor -o /usr/share/keyrings/postgresql.gpg \
    && echo "deb [signed-by=/usr/share/keyrings/postgresql.gpg] http://apt.postgresql.org/pub/repos/apt trixie-pgdg main" \
        > /etc/apt/sources.list.d/pgdg.list \
    && apt-get update \
    && apt-get install -y --no-install-recommends "postgresql-client-${PG_MAJOR}" \
    && apt-get purge -y gnupg \
    && apt-get autoremove -y \
    && rm -rf /var/lib/apt/lists/*

COPY --chmod=0755 docker/plugin/ /opt/jellyfin-postgres/plugin/
COPY --chmod=0644 docker/database.xml /opt/jellyfin-postgres/database.xml
COPY --chmod=0755 docker/entrypoint.sh /entrypoint.sh

ENTRYPOINT ["/entrypoint.sh"]
CMD ["/jellyfin/jellyfin"]
