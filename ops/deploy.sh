#!/bin/sh
set -eu
docker compose config --quiet
docker compose build app
docker compose up -d db
# Migrations are an explicit operation, never executed by production web workers.
docker compose run --rm migrate
docker compose up -d app caddy
