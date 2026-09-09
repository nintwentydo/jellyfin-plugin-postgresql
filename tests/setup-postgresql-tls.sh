#!/bin/sh
# Test fixture only: replaces authentication rules in the supplied disposable container.
# Usage: sh tests/setup-postgresql-tls.sh CONTAINER NEW_CERTIFICATE_DIRECTORY
set -eu
container=${1:?Pass the disposable PostgreSQL container ID}
certificates=${2:?Pass a new certificate directory}
mkdir -m 700 "$certificates"
certificates=$(cd "$certificates" && pwd)
umask 077

openssl req -x509 -newkey rsa:2048 -nodes -days 2 -subj '/CN=Jellyfin test CA' \
  -keyout "$certificates/ca.key" -out "$certificates/ca.crt" >/dev/null 2>&1
openssl req -newkey rsa:2048 -nodes -subj '/CN=postgres' \
  -keyout "$certificates/server.key" -out "$certificates/server.csr" >/dev/null 2>&1
printf 'subjectAltName=DNS:localhost,DNS:postgres\nextendedKeyUsage=serverAuth\n' > "$certificates/server.ext"
openssl x509 -req -days 2 -in "$certificates/server.csr" \
  -CA "$certificates/ca.crt" -CAkey "$certificates/ca.key" -CAcreateserial \
  -extfile "$certificates/server.ext" -out "$certificates/server.crt" >/dev/null 2>&1
openssl req -newkey rsa:2048 -nodes -subj '/CN=jellyfin_test' \
  -keyout "$certificates/client.key" -out "$certificates/client.csr" >/dev/null 2>&1
printf 'extendedKeyUsage=clientAuth\n' > "$certificates/client.ext"
openssl x509 -req -days 2 -in "$certificates/client.csr" \
  -CA "$certificates/ca.crt" -CAkey "$certificates/ca.key" -CAcreateserial \
  -extfile "$certificates/client.ext" -out "$certificates/client.crt" >/dev/null 2>&1
# Npgsql uses an encrypted PFX; native tools inherit separate PEM paths.
openssl pkcs12 -export -in "$certificates/client.crt" -inkey "$certificates/client.key" \
  -out "$certificates/client.pfx" -passout pass:test-client-key >/dev/null 2>&1
openssl req -x509 -newkey rsa:2048 -nodes -days 2 -subj '/CN=Untrusted test CA' \
  -keyout "$certificates/untrusted.key" -out "$certificates/untrusted.crt" >/dev/null 2>&1

docker exec "$container" mkdir /tmp/jellyfin-test-tls
for file in ca.crt server.crt server.key; do
  docker cp "$certificates/$file" "$container:/tmp/jellyfin-test-tls/$file"
done
docker exec "$container" chown -R postgres:postgres /tmp/jellyfin-test-tls
docker exec "$container" chmod 600 /tmp/jellyfin-test-tls/server.key
docker exec -i "$container" sh -c 'cat >> "$PGDATA/postgresql.conf"' <<'CONFIG'
ssl = on
ssl_cert_file = '/tmp/jellyfin-test-tls/server.crt'
ssl_key_file = '/tmp/jellyfin-test-tls/server.key'
ssl_ca_file = '/tmp/jellyfin-test-tls/ca.crt'
CONFIG
docker exec -i "$container" sh -c 'cat > "$PGDATA/pg_hba.conf"' <<'HBA'
local all all trust
hostssl all all all scram-sha-256 clientcert=verify-full
hostnossl all all all reject
HBA
docker exec -u postgres "$container" psql --no-psqlrc --set=ON_ERROR_STOP=1 \
  --command 'SELECT pg_reload_conf();'
