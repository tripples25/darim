#!/usr/bin/env bash
set -euo pipefail
umask 077

uuid=${1:?Coolify service UUID required}
sha=${2:?Commit SHA required}
pipeline=${3:?Pipeline number required}
[[ $uuid =~ ^[a-z0-9]{24}$ && $sha =~ ^[a-f0-9]{40}$ && $pipeline =~ ^[0-9]+$ ]]
image=${4:-darim:$sha}
app="app-$uuid"
db="db-$uuid"
migrate="migrate-$uuid"
root=${DARIM_PROMOTE_ROOT:-/opt/darim}
coolify_api=https://coolify.hype-ny.ru/api/v1
coolify_token_file=${DARIM_COOLIFY_TOKEN_FILE:-/opt/darim/registry-deploy/coolify-token}

exec 9>"$root/.coolify-deploy.lock"
flock -n 9 || { echo 'Another Darim deployment is running'; exit 10; }
if ! expected=$(docker image inspect --format '{{.Id}}' "$image"); then
  echo 'Could not inspect the release image before promotion' >&2
  exit 10
fi
if ! docker inspect "$app" "$db" >/dev/null; then
  echo 'Coolify app or database container is missing' >&2
  exit 20
fi
if [[ $(docker inspect -f '{{.State.Health.Status}}' "$db") != healthy ]]; then
  echo 'Database is not healthy; retry before any promotion request' >&2
  exit 10
fi
test "$(docker inspect -f '{{range .Mounts}}{{if eq .Destination "/var/lib/postgresql"}}{{.Name}}{{end}}{{end}}' "$db")" = darim_postgres

if [[ ! -f $coolify_token_file || -L $coolify_token_file || ! -r $coolify_token_file ]]; then
  echo 'Coolify API token file is unavailable' >&2
  exit 1
fi
token_mode=$(stat -c '%a' "$coolify_token_file")
if (( (8#$token_mode & 077) != 0 )); then
  echo 'Coolify API token file permissions must exclude group and other access' >&2
  exit 1
fi
coolify_token=$(< "$coolify_token_file")
if [[ -z $coolify_token || $coolify_token == *$'\n'* || $coolify_token == *$'\r'* ]]; then
  echo 'Coolify API token file is empty or malformed' >&2
  exit 1
fi

mkdir -p "$root/backups"
backup=$(mktemp "$root/backups/pre-deploy-$sha-$pipeline-XXXXXXXX.partial")
pause_file="$root/pause-writes-release"
paused=false
promotion_attempted=false
api_response=''
cleanup() {
  rm -f "$backup"
  if [[ -n $api_response ]]; then rm -f "$api_response"; fi
  if [[ $paused == true && $promotion_attempted == false ]]; then
    docker start "$app" >/dev/null
  fi
}
trap cleanup EXIT
trap 'rc=$?; if [[ $promotion_attempted == true ]]; then echo "Unexpected failure after Coolify promotion may have started (exit $rc)" >&2; exit 20; fi' ERR
if [[ -f $pause_file && $(< "$pause_file") == "$sha" ]] &&
   [[ $(docker inspect -f '{{.State.Running}}' "$app") == true ]]; then
  docker stop --time 20 "$app" >/dev/null
  paused=true
fi
if ! docker exec "$db" pg_dump -U wishlist -d wishlist -Fc > "$backup"; then
  echo 'Pre-promotion database backup failed; safe to retry before migration' >&2
  exit 10
fi
if ! docker exec -i "$db" pg_restore --list < "$backup" >/dev/null; then
  echo 'Pre-promotion database backup verification failed; safe to retry before migration' >&2
  exit 10
fi
mv "$backup" "${backup%.partial}.dump"

api_response=$(mktemp "$root/.coolify-api-response.XXXXXXXX")
api_request() {
  local method=$1 path=$2 body=$3
  api_http_status=''
  if ! api_http_status=$(printf 'Authorization: Bearer %s\n' "$coolify_token" |
    curl --silent --connect-timeout 5 --max-time 20 --request "$method" \
      --header @- --header 'Content-Type: application/json' \
      --data-raw "$body" --output "$api_response" --write-out '%{http_code}' \
      "$coolify_api$path" 2>/dev/null); then
    api_http_status=''
    return 1
  fi
  return 0
}

# Update the production image through Coolify's public service API.
env_payload=$(jq -cn --arg value "$image" '{data:[{key:"DARIM_IMAGE",value:$value}]}')
if ! api_request PATCH "/services/$uuid/envs/bulk" "$env_payload"; then
  echo 'Coolify environment update request failed before service start' >&2
  exit 10
fi
if [[ $api_http_status != 201 ]]; then
  if [[ $api_http_status =~ ^(429|5[0-9][0-9])$ ]]; then
    echo "Coolify environment update returned HTTP $api_http_status before service start" >&2
    exit 10
  fi
  echo "Coolify environment update was rejected before service start (HTTP ${api_http_status:-unknown})" >&2
  exit 1
fi
if ! jq -e 'type == "array" and any(.[]; .key == "DARIM_IMAGE")' "$api_response" >/dev/null 2>&1; then
  echo 'Coolify environment update response was invalid before service start' >&2
  exit 10
fi

# The POST may be accepted even if its response is lost; failures from here are ambiguous.
promotion_attempted=true
if ! api_request POST "/services/$uuid/start" '{}'; then
  echo 'Coolify service start request failed; promotion may have started' >&2
  exit 20
fi
if [[ $api_http_status != 200 ]]; then
  echo "Coolify service start returned HTTP ${api_http_status:-unknown}; promotion may have started" >&2
  exit 20
fi

for ((attempt=1; attempt<=60; attempt++)); do
  if [[ $(docker inspect -f '{{.Image}} {{.State.Running}}' "$app" 2>/dev/null || true) == "$expected true" &&
        $(docker inspect -f '{{.Image}} {{.State.Status}} {{.State.ExitCode}}' "$migrate" 2>/dev/null || true) == "$expected exited 0" ]] &&
        curl -fsS --max-time 5 https://wishes.hype-ny.ru/health | grep -q '"status":"ok"'; then
    marker=$(mktemp "$root/.coolify-release.XXXXXXXX")
    printf '%s-%s\n' "$sha" "$pipeline" > "$marker"
    mv "$marker" "$root/current-coolify-release"
    if [[ -f $pause_file && $(< "$pause_file") == "$sha" ]]; then rm -f "$pause_file"; fi
    printf 'Released %s through Coolify\n' "$sha"
    exit 0
  fi
  sleep 2
done
echo 'Release did not pass image, migration and HTTPS health checks. Backup retained.' >&2
exit 20
