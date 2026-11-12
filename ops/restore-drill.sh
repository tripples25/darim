#!/bin/sh
# Restore a PostgreSQL custom dump into a disposable, network-isolated database.
# This never connects to the production database or changes the source dump.
set -eu

if [ "$#" -ne 1 ] || [ ! -f "$1" ]; then
  echo 'Usage: sh ops/restore-drill.sh /path/to/backup.dump' >&2
  exit 1
fi

dump=$(readlink -f "$1")
stamp=$(date -u +%Y%m%d%H%M%S)-$$
container="darim-restore-drill-$stamp"
volume="darim-restore-drill-$stamp"
cleanup() {
  docker rm -f "$container" >/dev/null 2>&1 || true
  docker volume rm "$volume" >/dev/null 2>&1 || true
}
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

docker volume create "$volume" >/dev/null
docker run -d \
  --name "$container" \
  --memory=256m --memory-swap=256m --cpus=0.5 \
  --network=none \
  --mount "type=bind,source=$dump,target=/restore/input.dump,readonly" \
  --mount "type=volume,source=$volume,target=/var/lib/postgresql" \
  -e POSTGRES_HOST_AUTH_METHOD=trust \
  -e PGDATA=/var/lib/postgresql/data \
  postgres:18-alpine >/dev/null

ready=0
for _ in $(seq 1 60); do
  if docker exec "$container" pg_isready -U postgres -d postgres >/dev/null 2>&1; then
    ready=1
    break
  fi
  sleep 1
done
if [ "$ready" -ne 1 ]; then
  echo 'Temporary PostgreSQL did not become ready within 60 seconds.' >&2
  exit 1
fi

docker exec "$container" pg_restore --username=postgres --list /restore/input.dump >/dev/null
docker exec "$container" pg_restore \
  --username=postgres --dbname=postgres --clean --if-exists --no-owner --no-acl --exit-on-error \
  /restore/input.dump >/dev/null

history=$(docker exec "$container" psql -U postgres -d postgres -Atc \
  'SELECT count(*) FROM "__EFMigrationsHistory"')
case "$history" in
  ''|*[!0-9]*) echo 'Could not verify the EF migration history.' >&2; exit 1 ;;
esac
if [ "$history" -eq 0 ]; then
  echo 'Restored database has no EF migrations.' >&2
  exit 1
fi

required=$(docker exec "$container" psql -U postgres -d postgres -Atc \
  "SELECT count(*) FROM pg_tables WHERE schemaname = 'public' AND tablename IN ('Users', 'Companies', 'Gifts', 'Memberships', 'Shares')")
if [ "$required" -ne 5 ]; then
  echo 'Restored database is missing required business tables.' >&2
  exit 1
fi

# Report only table names and exact row counts; never select or print record contents.
tables=$(docker exec "$container" psql -U postgres -d postgres -Atc \
  "SELECT quote_ident(tablename) FROM pg_tables WHERE schemaname = 'public' ORDER BY tablename")
printf '%s\n' "$tables" | while IFS= read -r table; do
  [ -n "$table" ] || continue
  rows=$(docker exec "$container" psql -U postgres -d postgres -Atc \
    "SELECT count(*) FROM public.$table")
  printf 'table-count %s %s\n' "$table" "$rows"
done

foreign_keys=$(docker exec "$container" psql -U postgres -d postgres -Atc \
  "SELECT count(*) FROM pg_constraint WHERE contype = 'f'")
unvalidated=$(docker exec "$container" psql -U postgres -d postgres -Atc \
  "SELECT count(*) FROM pg_constraint WHERE contype = 'f' AND NOT convalidated")
if [ "$foreign_keys" -eq 0 ] || [ "$unvalidated" -ne 0 ]; then
  echo 'Restored database has missing or unvalidated foreign keys.' >&2
  exit 1
fi
printf 'Foreign key constraints: %s\n' "$foreign_keys"
printf 'Restore drill passed; EF migrations: %s\n' "$history"
