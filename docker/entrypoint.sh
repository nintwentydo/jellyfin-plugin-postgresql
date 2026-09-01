#!/bin/sh
# Put postgres provider on disk before jellyfin starts, then hands over
# plugin cannot be installed from the dashboard on a fresh server
set -eu

data_dir="${JELLYFIN_DATA_DIR:-/config}"
config_dir="${JELLYFIN_CONFIG_DIR:-${data_dir}/config}"
plugin_dir="${data_dir}/plugins/PostgreSQL"

# replace on every start as image tag is the version. Plugin settings unaffected
rm -rf "$plugin_dir"
mkdir -p "$plugin_dir"
cp -a /opt/jellyfin-postgres/plugin/. "$plugin_dir/"

# Clean up second copy of plugin if a dashboard install leaves a versioned directory
rm -rf "${data_dir}"/plugins/PostgreSQL_*

if [ -f "${config_dir}/database.xml" ]; then
    grep -q PLUGIN_PROVIDER "${config_dir}/database.xml" \
        || echo "entrypoint: WARNING ${config_dir}/database.xml is not set to PLUGIN_PROVIDER, Jellyfin will not use PostgreSQL" >&2
else
    mkdir -p "$config_dir"
    cp /opt/jellyfin-postgres/database.xml "${config_dir}/database.xml"
    echo "entrypoint: seeded ${config_dir}/database.xml"
fi

if [ $# -eq 0 ]; then set -- /jellyfin/jellyfin; fi
case "$1" in -*) set -- /jellyfin/jellyfin "$@" ;; esac
exec "$@"
