#!/usr/bin/env bash
set -euo pipefail
umask 077

root=${DARIM_REMOTE_ROOT:-/opt/darim}
uuid=${1:?Darim Coolify service UUID required}
[[ $uuid =~ ^[a-z0-9]{24}$ ]] || { printf '%s\n' 'Invalid Darim service UUID.' >&2; exit 2; }
exec 9>"$root/.coolify-deploy.lock"
flock -n 9 || { printf '%s\n' 'Darim deployment is in progress.' >&2; exit 75; }

app="app-$uuid"
db="db-$uuid"
docker inspect "$app" "$db" >/dev/null
[[ $(docker inspect -f '{{index .Config.Labels "com.docker.compose.project"}}' "$app") == "$uuid" ]] || {
  printf '%s\n' 'Selected app is not part of the Darim Compose project.' >&2
  exit 1
}
[[ $(docker inspect -f '{{.Config.Image}}' "$app") == *darim* ]] || {
  printf '%s\n' 'Selected app container is not the Darim image.' >&2
  exit 1
}
docker inspect -f '{{range .Config.Env}}{{println .}}{{end}}' "$app" | grep -Fxq 'DataProtection__Path=/keys' || {
  printf '%s\n' 'Selected app container has no Darim key-ring mount.' >&2
  exit 1
}
docker inspect -f '{{range .Config.Env}}{{println .}}{{end}}' "$db" | grep -Fxq 'POSTGRES_DB=wishlist' || {
  printf '%s\n' 'Selected database container is not the Darim database.' >&2
  exit 1
}
key_mount=$(docker inspect -f '{{range .Mounts}}{{if eq .Destination "/keys"}}{{.Type}} {{.Name}}{{end}}{{end}}' "$app")
[[ $key_mount == 'volume darim_keys' ]] || {
  printf '%s\n' 'Darim key ring is not stored in the expected persistent volume.' >&2
  exit 1
}
db_mount=$(docker inspect -f '{{range .Mounts}}{{if eq .Destination "/var/lib/postgresql"}}{{.Type}} {{.Name}}{{end}}{{end}}' "$db")
[[ $db_mount == 'volume darim_postgres' ]] || {
  printf '%s\n' 'Darim database is not stored in the expected persistent volume.' >&2
  exit 1
}
[[ $(docker inspect -f '{{.State.Health.Status}}' "$db") == healthy ]] || {
  printf '%s\n' 'Darim database is not healthy.' >&2
  exit 1
}

stage=$(mktemp -d /tmp/darim-winbackup.XXXXXXXX)
chmod 700 "$stage"
cleanup() { rm -rf -- "$stage"; }
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM
mkdir -m 700 "$stage/keys"

max_payload=33554432
docker exec "$db" pg_dump -U wishlist -d wishlist --format=custom --no-owner --no-acl \
  | head -c "$((max_payload + 1))" > "$stage/wishlist.dump"
chmod 600 "$stage/wishlist.dump"
docker exec -i "$db" pg_restore --list < "$stage/wishlist.dump" >/dev/null || {
  printf '%s\n' 'PostgreSQL dump failed its archive validation.' >&2
  exit 1
}
docker cp "$app:/keys/." "$stage/keys/" >/dev/null
chmod -R go-rwx "$stage/keys"
docker inspect -f '{{range .Config.Env}}{{println .}}{{end}}' "$app" > "$stage/app.env"
docker inspect -f '{{range .Config.Env}}{{println .}}{{end}}' "$db" > "$stage/db.env"
chmod 600 "$stage/app.env" "$stage/db.env"

config_files=$(docker inspect -f '{{index .Config.Labels "com.docker.compose.project.config_files"}}' "$app")
config_file=${config_files%%,*}
[[ -n "$config_file" && -f "$config_file" ]] || {
  printf '%s\n' 'Could not identify the generated Darim Compose file.' >&2
  exit 1
}
cp -- "$config_file" "$stage/compose.yaml"
chmod 600 "$stage/compose.yaml"

stage_bytes=$(find "$stage" -type f -printf '%s\n' | awk '{ total += $1 } END { print total + 0 }')
(( stage_bytes <= max_payload )) || {
  printf '%s\n' 'Darim recovery bundle exceeds the 32 MiB limit.' >&2
  exit 1
}

tar -C "$stage" -czf - .
