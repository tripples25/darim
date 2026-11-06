#!/bin/sh
set -eu
export CI=true
export TESTCONTAINERS_HOST_OVERRIDE=127.0.0.1
export TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE=/run/ci/docker.sock
export PLAYWRIGHT_BROWSERS_PATH=/opt/playwright
export COMPOSE_PROJECT_NAME="darim-ci-${CI_PIPELINE_NUMBER:-local}"
cleanup() { docker compose -f compose.dev.yaml down -v --remove-orphans; }
trap cleanup EXIT
dotnet restore --locked-mode
dotnet test Wishlist.slnx --no-restore --logger trx
python3 -m unittest discover -s tests/ops -v
dotnet publish src/Wishlist.Server -c Release --no-restore -o artifacts/publish
docker compose -f compose.dev.yaml up -d --wait
npm ci
DARIM_PUBLISHED_TEST=1 npm run test:e2e
