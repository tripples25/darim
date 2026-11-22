#!/bin/sh
set -eu
sha=${CI_COMMIT_SHA:?}
printf '%s\n' "$sha" | grep -Eq '^[a-f0-9]{40}$'
test "${CI_PIPELINE_EVENT:-}" = push
test "${CI_COMMIT_BRANCH:-}" = main

repository=127.0.0.1:5000/darim
docker tag "darim:$sha" "$repository:$sha"
docker push "$repository:$sha"

# Move the production pointer only after the build checks and commit-tagged push.
docker tag "$repository:$sha" "$repository:production"
docker push "$repository:production"
