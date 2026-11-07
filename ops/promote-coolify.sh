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
root=/opt/darim

exec 9>"$root/.coolify-deploy.lock"
flock -n 9 || { echo 'Another Darim deployment is running'; exit 1; }
expected=$(docker image inspect --format '{{.Id}}' "$image")
docker inspect "$app" "$db" >/dev/null
test "$(docker inspect -f '{{.State.Health.Status}}' "$db")" = healthy
test "$(docker inspect -f '{{range .Mounts}}{{if eq .Destination "/var/lib/postgresql"}}{{.Name}}{{end}}{{end}}' "$db")" = darim_postgres

mkdir -p "$root/backups"
backup=$(mktemp "$root/backups/pre-deploy-$sha-$pipeline-XXXXXXXX.partial")
trap 'rm -f "$backup"' EXIT
docker exec "$db" pg_dump -U wishlist -d wishlist -Fc > "$backup"
docker exec -i "$db" pg_restore --list < "$backup" >/dev/null
mv "$backup" "${backup%.partial}.dump"

# Use Coolify's service action so Compose and its environment remain managed by Coolify.
result=$(docker exec -e DARIM_SERVICE_UUID="$uuid" -e DARIM_RELEASE_IMAGE="$image" coolify \
  php artisan tinker --execute='
    $service = \App\Models\Service::whereUuid(getenv("DARIM_SERVICE_UUID"))->whereName("Darim")->firstOrFail();
    $service->environment_variables()->where("is_preview", false)->where("key", "DARIM_IMAGE")->firstOrFail()->update(["value" => getenv("DARIM_RELEASE_IMAGE")]);
    \App\Actions\Service\StartService::run($service);
    echo "COOLIFY_DEPLOY_REQUESTED";
  ')
[[ $result == *COOLIFY_DEPLOY_REQUESTED* ]] || { echo 'Coolify deployment request failed'; exit 1; }

for ((attempt=1; attempt<=60; attempt++)); do
  if [[ $(docker inspect -f '{{.Image}} {{.State.Running}}' "$app" 2>/dev/null || true) == "$expected true" &&
        $(docker inspect -f '{{.State.Status}} {{.State.ExitCode}}' "$migrate" 2>/dev/null || true) == 'exited 0' ]] &&
        curl -fsS --max-time 5 https://wishes.hype-ny.ru/health | grep -q '"status":"ok"'; then
    marker=$(mktemp "$root/.coolify-release.XXXXXXXX")
    printf '%s-%s\n' "$sha" "$pipeline" > "$marker"
    mv "$marker" "$root/current-coolify-release"
    printf 'Released %s through Coolify\n' "$sha"
    exit 0
  fi
  sleep 2
done
echo 'Release did not pass image, migration and HTTPS health checks. Backup retained.' >&2
exit 1
