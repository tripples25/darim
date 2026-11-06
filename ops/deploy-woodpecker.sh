#!/bin/sh
set -eu
[ "${CI_PIPELINE_EVENT:-}" = deployment ] && \
  [ "${CI_PIPELINE_DEPLOY_TARGET:-}" = production ] && \
  [ "${CI_COMMIT_BRANCH:-}" = main ] || { echo 'Only production deployments from main are allowed'; exit 1; }
: "${SSHPASS:?Configure production_ssh_password for deployment events only}"
: "${DEPLOY_KNOWN_HOSTS:?Configure production_known_hosts}"
: "${DEPLOY_HOST:?}" "${DEPLOY_USER:?}" "${DEPLOY_PATH:?}" "${DEPLOY_HEALTH_URL:?}"
: "${CI_COMMIT_SHA:?}" "${CI_PIPELINE_NUMBER:?}"
case "$CI_COMMIT_SHA" in *[!a-f0-9]*|'') echo 'Invalid commit SHA'; exit 1;; esac
case "$CI_PIPELINE_NUMBER" in *[!0-9]*|'') echo 'Invalid pipeline number'; exit 1;; esac
case "$DEPLOY_PATH" in /opt/darim) ;; *) echo 'Unexpected deployment path'; exit 1;; esac
umask 077
ssh_dir=$(mktemp -d)
trap 'rm -rf "$ssh_dir"' EXIT
printf '%s\n' "$DEPLOY_KNOWN_HOSTS" > "$ssh_dir/known_hosts"
export SSHPASS
target="$DEPLOY_USER@$DEPLOY_HOST"
release="$DEPLOY_PATH/releases/$CI_COMMIT_SHA-$CI_PIPELINE_NUMBER"
# Fail before image transfer if application secrets have not been configured.
sshpass -e ssh -o StrictHostKeyChecking=yes -o UserKnownHostsFile="$ssh_dir/known_hosts" -o ConnectTimeout=15 "$target" "test -f '$DEPLOY_PATH/.env' && mkdir -p '$release'"
mkdir -p artifacts/deployment
docker save "darim:$CI_COMMIT_SHA" | gzip > artifacts/deployment/image.tar.gz
cp compose.yaml compose.woodpecker.yaml artifacts/deployment/
cp -r ops artifacts/deployment/
tar -czf artifacts/deployment.tar.gz -C artifacts/deployment .
sha256sum artifacts/deployment.tar.gz | sed 's|artifacts/||' > artifacts/deployment.tar.gz.sha256
sshpass -e scp -o StrictHostKeyChecking=yes -o UserKnownHostsFile="$ssh_dir/known_hosts" artifacts/deployment.tar.gz artifacts/deployment.tar.gz.sha256 "$target:$release/"
sshpass -e ssh -o StrictHostKeyChecking=yes -o UserKnownHostsFile="$ssh_dir/known_hosts" "$target" "bash -s -- '$DEPLOY_PATH' '$release' '$CI_COMMIT_SHA'" <<'REMOTE'
set -euo pipefail
root=$1
release=$2
export DARIM_IMAGE="darim:$3"
cd "$release"
sha256sum -c deployment.tar.gz.sha256
tar -xzf deployment.tar.gz
docker load -i image.tar.gz
compose=(docker compose --project-name darim --env-file "$root/.env" -f "$release/compose.yaml" -f "$release/compose.woodpecker.yaml")
"${compose[@]}" config --quiet
if [[ -n "$("${compose[@]}" ps --all --quiet app)" ]]; then
  mkdir -p "$root/backups"
  backup="$root/backups/pre-deploy-$(date -u +%Y%m%dT%H%M%SZ)-$3.dump"
  "${compose[@]}" exec -T db pg_dump -U wishlist -d wishlist --format=custom > "$backup.partial"
  mv "$backup.partial" "$backup"
fi
"${compose[@]}" up -d --wait db
"${compose[@]}" run --rm migrate
"${compose[@]}" up -d --no-build app
REMOTE
curl --fail --silent --show-error --retry 18 --retry-all-errors --retry-delay 5 --connect-timeout 10 --max-time 20 "$DEPLOY_HEALTH_URL"
sshpass -e ssh -o StrictHostKeyChecking=yes -o UserKnownHostsFile="$ssh_dir/known_hosts" "$target" "printf '%s\n' '$CI_COMMIT_SHA-$CI_PIPELINE_NUMBER' > '$DEPLOY_PATH/current-release'"
