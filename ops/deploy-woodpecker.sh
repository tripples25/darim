#!/bin/sh
set -eu
[ "${CI_PIPELINE_EVENT:-}" = deployment ] &&
  [ "${CI_PIPELINE_DEPLOY_TARGET:-}" = production ] &&
  [ "${CI_COMMIT_BRANCH:-}" = main ] || { echo 'Only production deployments from main are allowed'; exit 1; }
: "${SSHPASS:?Configure production_ssh_password for deployment events only}"
: "${DEPLOY_KNOWN_HOSTS:?Configure production_known_hosts}"
: "${DEPLOY_HOST:?}" "${DEPLOY_USER:?}" "${COOLIFY_SERVICE_UUID:?}"
: "${CI_COMMIT_SHA:?}" "${CI_PIPELINE_NUMBER:?}"
printf '%s\n' "$CI_COMMIT_SHA" | grep -Eq '^[a-f0-9]{40}$'
printf '%s\n' "$COOLIFY_SERVICE_UUID" | grep -Eq '^[a-z0-9]{24}$'
printf '%s\n' "$CI_PIPELINE_NUMBER" | grep -Eq '^[0-9]+$'

image="darim:$CI_COMMIT_SHA"
docker image inspect "$image" >/dev/null
umask 077
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
printf '%s\n' "$DEPLOY_KNOWN_HOSTS" > "$work/known_hosts"
export SSHPASS
remote() {
  sshpass -e ssh -o StrictHostKeyChecking=yes -o UserKnownHostsFile="$work/known_hosts" \
    -o ConnectTimeout=15 "$DEPLOY_USER@$DEPLOY_HOST" "$@"
}

# Save first so an export failure can never send a partial image to production.
docker save -o "$work/image.tar" "$image"
remote 'docker load' < "$work/image.tar"
remote "bash -s -- '$COOLIFY_SERVICE_UUID' '$CI_COMMIT_SHA' '$CI_PIPELINE_NUMBER'" < ops/promote-coolify.sh
