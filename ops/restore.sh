#!/bin/sh
# Run from repository root; application must be stopped to prevent writes during restore.
set -eu
if [ "$#" -ne 1 ] || [ ! -f "$1" ]; then echo 'Usage: sh ops/restore.sh /path/to/backup.dump' >&2; exit 1; fi
if docker compose ps --status running --services | grep -qx app; then echo 'Stop app before restore: docker compose stop app' >&2; exit 1; fi
docker compose exec -T db pg_restore -U wishlist -d wishlist --clean --if-exists --no-owner --no-acl --exit-on-error < "$1"
printf 'Restored. Start the app with: docker compose up -d app\n'
