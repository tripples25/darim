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

mkdir -p "$root/backups"
backup=$(mktemp "$root/backups/pre-deploy-$sha-$pipeline-XXXXXXXX.partial")
pause_file="$root/pause-writes-release"
paused=false
promotion_attempted=false
cleanup() {
  rm -f "$backup"
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

# Use Coolify's service action so Compose and its environment remain managed by Coolify.
promotion_attempted=true
if ! result=$(docker exec -e DARIM_SERVICE_UUID="$uuid" -e DARIM_RELEASE_IMAGE="$image" coolify \
  php artisan tinker --execute='
    $service = \App\Models\Service::whereUuid(getenv("DARIM_SERVICE_UUID"))->whereName("Darim")->firstOrFail();
    $service->environment_variables()->where("is_preview", false)->where("key", "DARIM_IMAGE")->firstOrFail()->update(["value" => getenv("DARIM_RELEASE_IMAGE")]);
    \App\Actions\Service\StartService::run($service);
    echo "COOLIFY_DEPLOY_REQUESTED";
  '); then
  echo 'Coolify deployment request failed after promotion may have started' >&2
  exit 20
fi
[[ $result == *COOLIFY_DEPLOY_REQUESTED* ]] || { echo 'Coolify deployment request failed after promotion may have started'; exit 20; }

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
