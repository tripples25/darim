#!/usr/bin/env bash
set -euo pipefail

repo=$(cd "$(dirname "$0")/../../.." && pwd)
test_root=$(mktemp -d)
trap 'rm -rf -- "$test_root"' EXIT
root="$test_root/darim"
bin="$test_root/bin"
mkdir -p "$root" "$bin"
printf 'synthetic compose only\n' > "$root/generated-compose.yaml"

cat > "$bin/docker" <<'DOCKER'
#!/usr/bin/env bash
set -euo pipefail
command=$1
shift
case "$command" in
  inspect)
    if [[ $1 == -f ]]; then
      format=$2; container=${3:-}
      case "$format:$container" in
        *config_files*) printf '%s/generated-compose.yaml\n' "$DARIM_REMOTE_ROOT" ;;
        *com.docker.compose.project*app-*) printf '6o4qlqy50orrzxavuw0c7rp5\n' ;;
        *Config.Image*app-*) printf 'darim/synthetic:fixture\n' ;;
        *Config.Env*app-*) printf 'DataProtection__Path=/keys\n' ;;
        *Config.Env*db-*) printf 'POSTGRES_DB=wishlist\n' ;;
        *Mounts*app-*) printf 'volume darim_keys\n' ;;
        *Mounts*db-*) printf 'volume darim_postgres\n' ;;
        *State.Health.Status*db-*) printf 'healthy\n' ;;
        *) printf 'unexpected inspect request: %s %s\n' "$format" "$container" >&2; exit 41 ;;
      esac
    fi
    exit 0
    ;;
  exec)
    if [[ $1 == -i ]]; then
      shift
      [[ $* == *'pg_restore --list'* ]] || exit 42
      cat >/dev/null
      exit 0
    fi
    [[ $* == *'pg_dump'* ]] || exit 43
    printf 'SYNTHETIC-POSTGRES-CUSTOM-DUMP\n'
    ;;
  cp)
    destination=${2%/}
    mkdir -p "$destination"
    printf 'SYNTHETIC-DP-KEY\n' > "$destination/key-1.xml"
    ;;
  *) printf 'unexpected docker command: %s\n' "$command" >&2; exit 44 ;;
esac
DOCKER
chmod +x "$bin/docker"
cat > "$bin/flock" <<'FLOCK'
#!/usr/bin/env bash
if [[ ${FIXTURE_LOCKED:-0} == 1 ]]; then exit 1; fi
exit 0
FLOCK
chmod +x "$bin/flock"

export DARIM_REMOTE_ROOT="$root"
export PATH="$bin:$PATH"
export FIXTURE_UUID=6o4qlqy50orrzxavuw0c7rp5

set +e
DARIM_REMOTE_ROOT="$root" PATH="$PATH" bash "$repo/ops/windows-backup/remote-export.sh" invalid-uuid >/dev/null 2>&1
uuid_status=$?
set -e
[[ $uuid_status -eq 2 ]] || { printf 'invalid UUID fixture returned %s, expected 2\n' "$uuid_status" >&2; exit 1; }

set +e
FIXTURE_LOCKED=1 DARIM_REMOTE_ROOT="$root" PATH="$PATH" bash "$repo/ops/windows-backup/remote-export.sh" "$FIXTURE_UUID" >/dev/null 2>&1
lock_status=$?
set -e
[[ $lock_status -eq 75 ]] || { printf 'deploy lock fixture returned %s, expected 75\n' "$lock_status" >&2; exit 1; }

archive="$test_root/backup.tar.gz"
FIXTURE_LOCKED=0 DARIM_REMOTE_ROOT="$root" PATH="$PATH" bash "$repo/ops/windows-backup/remote-export.sh" "$FIXTURE_UUID" > "$archive"
tar -tzf "$archive" | grep -Fxq './wishlist.dump'
tar -xOzf "$archive" ./wishlist.dump | grep -Fxq 'SYNTHETIC-POSTGRES-CUSTOM-DUMP'
tar -xOzf "$archive" ./app.env | grep -Fxq 'DataProtection__Path=/keys'
[[ -z $(find /tmp -maxdepth 1 -type d -name 'darim-winbackup.*' -print -quit) ]] || {
  printf 'remote stage directory leaked after fixture run\n' >&2
  exit 1
}
printf 'Remote export synthetic fixture passed.\n'
