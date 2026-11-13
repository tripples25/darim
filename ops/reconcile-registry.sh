#!/usr/bin/env bash
set -euo pipefail
umask 077

repository=${DARIM_REGISTRY_REPOSITORY:-127.0.0.1:5000/darim}
registry=${DARIM_REGISTRY_URL:-http://127.0.0.1:5000}
status=${DARIM_DEPLOYMENT_STATUS_FILE:-/data/coolify/proxy/caddy/dynamic/darim-deployment.json}
uuid=6o4qlqy50orrzxavuw0c7rp5
promote=${DARIM_PROMOTE_SCRIPT:-/opt/darim/registry-deploy/promote-coolify.sh}
lock=${DARIM_RECONCILE_LOCK:-/run/lock/darim-registry-reconcile.lock}
max_retries=2
manual_retry=false
case ${1:-} in
  '') ;;
  --retry) manual_retry=true ;;
  *) echo 'Usage: reconcile-registry.sh [--retry]' >&2; exit 2 ;;
esac
if (($# > 1)); then echo 'Usage: reconcile-registry.sh [--retry]' >&2; exit 2; fi
exec 8>"$lock"
flock -n 8 || { echo 'Another registry reconciliation is running'; exit 0; }

registry_digest() {
  curl -fsSI --max-time 5 -H 'Accept: application/vnd.oci.image.manifest.v1+json, application/vnd.docker.distribution.manifest.v2+json' \
    "$registry/v2/${repository#*/}/manifests/$1" 2>/dev/null |
    awk 'tolower($1)=="docker-content-digest:" {gsub(/\r/,"",$2); print $2}'
}

write_status() {
  local state=$1 retry_count=$2 next_epoch=$3 terminal=$4 failure_class=$5 phase=$6 error=$7 current temp now next_at alert
  current=$(jq -c '.current // null' "$status" 2>/dev/null || echo null)
  now=$(date -u +%FT%TZ)
  if [[ $next_epoch =~ ^[0-9]+$ ]] && ((next_epoch > 0)); then
    next_at=$(date -u -d "@$next_epoch" +%FT%TZ)
  else
    next_epoch=0
    next_at=''
  fi
  alert=$terminal
  temp=$(mktemp "${status}.XXXXXXXX")
  if ! jq -n --arg commit "$sha" --arg pipeline "$pipeline" --arg digest "$digest" \
    --arg state "$state" --arg time "$now" --argjson retryCount "$retry_count" \
    --argjson maxRetries "$max_retries" --arg nextRetryAt "$next_at" --argjson nextRetryEpoch "$next_epoch" \
    --argjson terminal "$terminal" --arg failureClass "$failure_class" --arg phase "$phase" \
    --arg error "$error" --argjson alert "$alert" --argjson current "$current" \
    '{attempt:{commit:$commit,pipeline:$pipeline,digest:$digest,status:$state,time:$time,
      retryCount:$retryCount,maxRetries:$maxRetries,nextRetryAt:(if $nextRetryAt=="" then null else $nextRetryAt end),
      nextRetryEpoch:(if $nextRetryEpoch==0 then null else $nextRetryEpoch end),terminal:$terminal,
      failureClass:(if $failureClass=="" then null else $failureClass end),phase:(if $phase=="" then null else $phase end),
      error:(if $error=="" then null else $error end),alert:$alert},
      current:(if $state=="success" then
        {commit:$commit,pipeline:$pipeline,digest:$digest,time:$time,
         woodpecker:("https://ci.hype-ny.ru/repos/1/pipeline/"+$pipeline),url:"https://wishes.hype-ny.ru"}
      else $current end)}' > "$temp"; then
    rm -f "$temp"
    return 1
  fi
  chmod 644 "$temp"
  mv "$temp" "$status"
}

schedule_transient_retry() {
  local phase=$1 error=$2 current_retry=$3 next_retry delay now
  if ((current_retry >= max_retries)); then
    write_status failure "$current_retry" 0 true transient "$phase" "$error"
    echo "Deployment reached retry limit ($phase: $error)" >&2
    return 1
  fi
  next_retry=$((current_retry + 1))
  case $next_retry in
    1) delay=15 ;;
    2) delay=30 ;;
    *) delay=60 ;;
  esac
  now=$(date -u +%s)
  write_status retrying "$next_retry" "$((now + delay))" false transient "$phase" "$error"
  echo "Deployment retry $next_retry/$max_retries scheduled in ${delay}s ($phase)"
}

digest=$(registry_digest production) || exit 0
if [[ ! $digest =~ ^sha256:[a-f0-9]{64}$ ]]; then exit 0; fi
previous=$(jq -r '.attempt.digest // ""' "$status" 2>/dev/null || true)
previous_state=$(jq -r '.attempt.status // ""' "$status" 2>/dev/null || true)
previous_sha=$(jq -r '.attempt.commit // ""' "$status" 2>/dev/null || true)
previous_pipeline=$(jq -r '.attempt.pipeline // ""' "$status" 2>/dev/null || true)
previous_retries=$(jq -r '.attempt.retryCount // 0' "$status" 2>/dev/null || echo 0)
previous_next=$(jq -r '.attempt.nextRetryEpoch // 0' "$status" 2>/dev/null || echo 0)
if [[ ! $previous_retries =~ ^[0-9]+$ ]]; then previous_retries=0; fi
if [[ ! $previous_next =~ ^[0-9]+$ ]]; then previous_next=0; fi
if [[ $previous == "$digest" ]]; then
  case $previous_state in
    success) exit 0 ;;
    retrying)
      if [[ $manual_retry == true ]]; then
        previous_retries=0
      elif (( $(date -u +%s) < previous_next )); then
        exit 0
      fi
      ;;
    failure)
      [[ $manual_retry == true ]] || exit 0
      previous_retries=0
      ;;
    running)
      if [[ $manual_retry != true ]]; then
        # An interrupted deployment may have already applied migrations; never replay it automatically.
        sha=$previous_sha
        pipeline=$previous_pipeline
        write_status failure "$previous_retries" 0 true permanent interrupted ambiguous-running-state
        exit 1
      fi
      previous_retries=0
      ;;
  esac
else
  previous_retries=0
fi
image="$repository@$digest"
if ! docker pull "$image"; then
  if [[ $previous == "$digest" ]]; then sha=$previous_sha; pipeline=$previous_pipeline; else sha=''; pipeline=''; fi
  if schedule_transient_retry registry-pull docker-pull-failed "$previous_retries"; then exit 0; else exit 1; fi
fi
sha=$(docker image inspect -f '{{index .Config.Labels "org.opencontainers.image.revision"}}' "$image" 2>/dev/null || echo '')
pipeline=$(docker image inspect -f '{{index .Config.Labels "io.darim.ci.pipeline"}}' "$image" 2>/dev/null || echo '')
if [[ ! $sha =~ ^[a-f0-9]{40}$ || ! $pipeline =~ ^[0-9]+$ ]]; then
  write_status failure 0 0 true permanent verify-image invalid-image-metadata
  exit 1
fi
if [[ $(registry_digest "$sha") != "$digest" ]]; then
  write_status failure 0 0 true permanent verify-image digest-tag-mismatch
  exit 1
fi
write_status running "$previous_retries" 0 false '' '' ''
if "$promote" "$uuid" "$sha" "$pipeline" "$image"; then
  write_status success "$previous_retries" 0 false '' '' ''
else
  rc=$?
  if ((rc == 10)); then
    if schedule_transient_retry deploy-preflight retryable-preflight-failure "$previous_retries"; then exit 0; else exit 1; fi
  fi
  write_status failure "$previous_retries" 0 true permanent coolify-deploy promotion-may-have-started
  exit 1
fi
