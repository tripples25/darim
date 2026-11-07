#!/usr/bin/env bash
set -euo pipefail
umask 077

repository=127.0.0.1:5000/darim
status=/data/coolify/proxy/caddy/dynamic/darim-deployment.json
uuid=6o4qlqy50orrzxavuw0c7rp5

registry_digest() {
  curl -fsSI --max-time 5 -H 'Accept: application/vnd.oci.image.manifest.v1+json, application/vnd.docker.distribution.manifest.v2+json' \
    "http://127.0.0.1:5000/v2/darim/manifests/$1" 2>/dev/null |
    awk 'tolower($1)=="docker-content-digest:" {gsub(/\r/,"",$2); print $2}'
}

digest=$(registry_digest production) || exit 0
[[ $digest =~ ^sha256:[a-f0-9]{64}$ ]] || exit 0
previous=$(jq -r '.attempt.digest // ""' "$status" 2>/dev/null || true)
previous_state=$(jq -r '.attempt.status // ""' "$status" 2>/dev/null || true)
if [[ $previous == "$digest" && $previous_state != running ]]; then exit 0; fi
image="$repository@$digest"
docker pull "$image"
sha=$(docker image inspect -f '{{index .Config.Labels "org.opencontainers.image.revision"}}' "$image")
pipeline=$(docker image inspect -f '{{index .Config.Labels "io.darim.ci.pipeline"}}' "$image")
[[ $sha =~ ^[a-f0-9]{40}$ && $pipeline =~ ^[0-9]+$ ]]
[[ $(registry_digest "$sha") == "$digest" ]]

write_status() {
  local state=$1 current temp
  current=$(jq -c '.current // null' "$status" 2>/dev/null || echo null)
  temp=$(mktemp "${status}.XXXXXXXX")
  if ! jq -n --arg commit "$sha" --arg pipeline "$pipeline" --arg digest "$digest" \
    --arg state "$state" --arg time "$(date -u +%FT%TZ)" --argjson current "$current" \
    '{attempt:{commit:$commit,pipeline:$pipeline,digest:$digest,status:$state,time:$time},
      current:(if $state=="success" then
        {commit:$commit,pipeline:$pipeline,digest:$digest,time:$time,
         woodpecker:("https://ci.hype-ny.ru/repos/1/pipeline/"+$pipeline),
         url:"https://wishes.hype-ny.ru"}
      else $current end)}' > "$temp"; then
    rm -f "$temp"
    return 1
  fi
  chmod 644 "$temp"
  mv "$temp" "$status"
}

write_status running
if /opt/darim/registry-deploy/promote-coolify.sh "$uuid" "$sha" "$pipeline" "$image"; then
  write_status success
else
  write_status failure
  exit 1
fi
