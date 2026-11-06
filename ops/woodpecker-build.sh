#!/bin/sh
set -eu
docker build -f ops/Dockerfile.ci -t darim-ci-tools:dotnet10 .
# Host networking refers to the isolated CI daemon, not the VPS host.
docker run --rm --network host --volumes-from "$HOSTNAME" \
  -v /run/ci/docker.sock:/var/run/docker.sock \
  -w "$PWD" -e CI_PIPELINE_NUMBER \
  darim-ci-tools:dotnet10 sh ops/verify.sh
docker build -t "darim:${CI_COMMIT_SHA:?}" .
