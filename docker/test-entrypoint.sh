#!/bin/sh
# Self-check for entrypoint.sh. Run: sh docker/test-entrypoint.sh
set -eu

here="$(cd "$(dirname "$0")" && pwd)"
root="$(mktemp -d)"
trap 'rm -rf "$root"' EXIT

# Stand in for the image's staged copies and for the server binary.
mkdir -p "$root/opt/jellyfin-postgres/plugin"
echo v1 > "$root/opt/jellyfin-postgres/plugin/Jellyfin.Plugin.Postgresql.dll"
cp "$here/database.xml" "$root/opt/jellyfin-postgres/database.xml"
printf '#!/bin/sh\necho started\n' > "$root/jellyfin"; chmod +x "$root/jellyfin"

# entrypoint.sh reads its staged copies from absolute /opt paths, so rewrite
# those (and the exec target) to the sandbox rather than needing a container.
sed -e "s#/opt/jellyfin-postgres#$root/opt/jellyfin-postgres#g" \
    -e "s#/jellyfin/jellyfin#$root/jellyfin#g" \
    "$here/entrypoint.sh" > "$root/entrypoint.sh"
chmod +x "$root/entrypoint.sh"

run() { JELLYFIN_DATA_DIR="$root/config" "$root/entrypoint.sh" "$@"; }

mkdir -p "$root/config"
out="$(run 2>&1)"
grep -q started <<EOT || { echo "FAIL: did not exec the server"; exit 1; }
$out
EOT
grep -q PLUGIN_PROVIDER "$root/config/config/database.xml" || { echo "FAIL: database.xml not seeded"; exit 1; }
[ -f "$root/config/plugins/PostgreSQL/Jellyfin.Plugin.Postgresql.dll" ] || { echo "FAIL: plugin not installed"; exit 1; }

# A database.xml the operator already edited must survive.
echo "<custom/>" > "$root/config/config/database.xml"
out="$(run 2>&1)"
[ "$(cat "$root/config/config/database.xml")" = "<custom/>" ] || { echo "FAIL: clobbered existing database.xml"; exit 1; }
case "$out" in *WARNING*) ;; *) echo "FAIL: no warning for a non-provider database.xml"; exit 1 ;; esac

# An upgrade must drop assemblies the new release no longer ships, and clear a
# stale dashboard install that would otherwise load as a second copy.
echo stale > "$root/config/plugins/PostgreSQL/Old.dll"
mkdir -p "$root/config/plugins/PostgreSQL_1.0.0.0"
run >/dev/null 2>&1
[ ! -e "$root/config/plugins/PostgreSQL/Old.dll" ] || { echo "FAIL: stale assembly kept"; exit 1; }
[ ! -e "$root/config/plugins/PostgreSQL_1.0.0.0" ] || { echo "FAIL: versioned plugin dir kept"; exit 1; }

# Bare flags go to the server, a full command replaces it.
case "$(run --nowebclient 2>&1)" in *started*) ;; *) echo "FAIL: flags not passed through"; exit 1 ;; esac
case "$(run /bin/echo override 2>&1)" in *override*) ;; *) echo "FAIL: command not overridable"; exit 1 ;; esac

echo PASS
