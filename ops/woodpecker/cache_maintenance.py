#!/usr/bin/env python3
"""Conservatively reclaim space from the isolated Woodpecker Docker daemon."""

from __future__ import annotations

from pathlib import Path
import shutil
import sqlite3
import subprocess
import re
import sys
from contextlib import closing


WORKER_VOLUME = "darim-ci_worker_data"
WORKER_SOCKET = "unix:///run/ci/docker.sock"
WOODPECKER_DB = Path("/var/lib/docker/volumes/darim-ci_server_data/_data/woodpecker.sqlite")
MIN_FREE_RATIO = 0.20
CACHE_BUDGET_BYTES = 6_000_000_000


def count_active_pipelines(database: Path = WOODPECKER_DB) -> int:
    """Treat unknown statuses or an unreadable database as active (fail closed)."""
    uri = f"file:{database}?mode=ro"
    with closing(sqlite3.connect(uri, uri=True, timeout=5)) as connection:
        row = connection.execute(
            "SELECT COUNT(*) FROM pipelines "
            "WHERE status NOT IN ('success', 'failure', 'error', 'killed', 'declined')"
        ).fetchone()
    if row is None:
        raise RuntimeError("Could not read Woodpecker pipeline status")
    return int(row[0])


def free_ratio(path: str = "/") -> float:
    disk = shutil.disk_usage(path)
    return disk.free / disk.total


def docker_size_bytes(value: str) -> int:
    match = re.fullmatch(r"\s*(\d+(?:\.\d+)?)\s*(B|KB|MB|GB|TB)\s*", value, re.IGNORECASE)
    if not match:
        raise ValueError("Unrecognized Docker size")
    power = {"B": 0, "KB": 1, "MB": 2, "GB": 3, "TB": 4}[match.group(2).upper()]
    return int(float(match.group(1)) * (1000 ** power))


def cache_size_bytes(docker: list[str]) -> int:
    rows = run(docker + ["system", "df", "--format", "{{.Type}}|{{.Size}}"])
    for row in rows.splitlines():
        name, separator, size = row.partition("|")
        if separator and name == "Build Cache":
            return docker_size_bytes(size)
    raise RuntimeError("Docker did not report build cache size")


def should_prune(cache_bytes: int, available_ratio: float) -> bool:
    return cache_bytes > CACHE_BUDGET_BYTES or available_ratio < MIN_FREE_RATIO


def run(command: list[str]) -> str:
    result = subprocess.run(command, check=True, text=True, capture_output=True)
    return result.stdout.strip()


def main() -> int:
    try:
        import fcntl

        workers = run([
                "docker", "ps", "--filter", f"volume={WORKER_VOLUME}",
                "--format", "{{.Names}}",
        ]).splitlines()
        if len(workers) != 1:
            print("Expected exactly one running CI Docker worker; skipping")
            return 0

        worker = workers[0]
        docker = ["docker", "exec", worker, "docker", "-H", WORKER_SOCKET]
        volume_path = Path(run([
            "docker", "volume", "inspect", "--format", "{{.Mountpoint}}",
            "darim-ci_worker_socket",
        ]))
        lock_path = volume_path / "cache-maintenance.lock"
        if not lock_path.is_file():
            raise RuntimeError("The shared cache lock file is missing")
        with lock_path.open("r+") as lock:
            try:
                fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
            except BlockingIOError:
                print("A CI build holds the shared cache lock; skipping")
                return 0

            if count_active_pipelines():
                print("Woodpecker has an active or unknown pipeline state; skipping")
                return 0
            if run(docker + ["ps", "-q"]):
                print("CI Docker daemon has running containers; skipping")
                return 0

            cache_bytes = cache_size_bytes(docker)
            available = free_ratio()
            if not should_prune(cache_bytes, available):
                print(f"Cache is within its 6 GB budget and root has {available:.0%} free")
                return 0

            # The exclusive lock prevents a build from starting after this gate.
            if count_active_pipelines() or run(docker + ["ps", "-q"]):
                print("A pipeline or CI container appeared during preflight; skipping")
                return 0

            output = run(docker + [
                "builder", "prune", "--force", "--all",
                "--max-used-space", "6GB",
                "--reserved-space", "4GB",
                "--min-free-space", "12GB",
            ])
            print(output or "Build cache already within its budget")
            print("Only the isolated CI build cache was eligible; images and volumes were retained")
            return 0
    except (OSError, ValueError, sqlite3.Error, RuntimeError, subprocess.CalledProcessError) as error:
        # Do not print command output or database contents; those are not needed for this gate.
        print(f"CI cache cleanup skipped: {type(error).__name__}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
