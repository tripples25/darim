#!/bin/sh
set -eu
project="darim-ci-${CI_PIPELINE_NUMBER:?}"
verification="darim-ci-verify-${CI_PIPELINE_NUMBER}"
# A cancelled outer step can leave a sibling docker-run container behind.
# This agent executes one workflow at a time, so any older labelled verifier
# is stale. Only touch containers of this repository inside the CI daemon.
for id in $(docker ps -aq --filter label=darim.ci.verification=true); do
  previous=$(docker inspect --format '{{index .Config.Labels "darim.ci.pipeline"}}' "$id")
  case "$previous" in *[!0-9]*|'') echo 'Invalid stale pipeline label'; exit 1;; esac
  docker rm -f "$id"
  docker compose -p "darim-ci-$previous" -f compose.dev.yaml down -v --remove-orphans
done
# A hard kill may remove the verifier before its database cleanup completes.
# Discover those orphaned Compose projects independently of the verifier.
for stale in $(docker ps -a --filter label=com.docker.compose.project \
  --format '{{.Label "com.docker.compose.project"}}' | sort -u); do
  case "$stale" in darim-ci-*) previous=${stale#darim-ci-};; *) continue;; esac
  case "$previous" in *[!0-9]*|'') continue;; esac
  docker compose -p "$stale" -f compose.dev.yaml down -v --remove-orphans
done
cleanup() {
  docker rm -f "$verification" >/dev/null 2>&1 || true
  docker compose -p "$project" -f compose.dev.yaml down -v --remove-orphans
}
trap cleanup EXIT
trap 'exit 143' TERM
trap 'exit 130' INT
docker build -f ops/Dockerfile.ci -t darim-ci-tools:dotnet10 .
# Host networking refers to the isolated CI daemon, not the VPS host.
docker run --rm --name "$verification" --label darim.ci.verification=true \
  --label "darim.ci.pipeline=$CI_PIPELINE_NUMBER" --network host --volumes-from "$HOSTNAME" \
  -v /run/ci/docker.sock:/var/run/docker.sock \
  -w "$PWD" -e CI_PIPELINE_NUMBER \
  darim-ci-tools:dotnet10 sh ops/verify.sh &
wait "$!"
docker build -t "darim:${CI_COMMIT_SHA:?}" .
