#!/bin/sh
set -eu
sha=${CI_COMMIT_SHA:?}
pipeline=${CI_PIPELINE_NUMBER:?}
url=https://wishes.hype-ny.ru/deployment.json
printf 'Current deployment: %s\n' "$url"
printf 'Production: https://wishes.hype-ny.ru\n'

for attempt in $(seq 1 90); do
  state=$(curl -fsS --max-time 5 "$url" 2>/dev/null |
    jq -r --arg sha "$sha" --arg pipeline "$pipeline" \
      'select(.attempt.commit == $sha and .attempt.pipeline == $pipeline) | .attempt.status') || state=
  case "$state" in
    success) printf 'Production is serving %s\n' "$sha"; exit 0 ;;
    failure) printf 'Coolify deployment failed. See %s\n' "$url" >&2; exit 1 ;;
  esac
  sleep 5
done
printf 'Production did not confirm this deployment. See %s\n' "$url" >&2
exit 1
