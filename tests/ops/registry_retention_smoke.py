"""Run as root on a Docker host. Uses an isolated registry on a random loopback port."""
import io
import json
import os
from pathlib import Path
import socket
import subprocess
import tarfile
import tempfile
import time
from urllib.error import HTTPError, URLError
from uuid import uuid4

from test_registry_retention import module


def docker(*args, **kwargs):
    return subprocess.check_output(["docker", *args], **kwargs).decode().strip()


def main():
    name = f"darim-retention-test-{uuid4().hex[:10]}"
    tags = []
    with tempfile.TemporaryDirectory() as tmp:
        module.STATUS = Path(tmp) / "deployment.json"
        module.RECOVERY = Path(tmp) / "registry-stopped"
        module.REGISTRY_VOLUME = name
        docker("volume", "create", name)
        try:
            with socket.socket() as sock:
                sock.bind(("127.0.0.1", 0))
                port = sock.getsockname()[1]
            docker("run", "-d", "--name", name, "-p", f"127.0.0.1:{port}:5000",
                   "-v", f"{name}:/var/lib/registry",
                   "-e", "REGISTRY_STORAGE_DELETE_ENABLED=true", "registry:3")
            address = docker("port", name, "5000/tcp")
            module.REGISTRY = "http://" + address
            for attempt in range(30):
                try:
                    module.registry("/v2/")
                    break
                except (URLError, ConnectionError):
                    if attempt == 29:
                        raise
                    time.sleep(1)
            archive = io.BytesIO()
            with tarfile.open(fileobj=archive, mode="w"):
                pass
            revisions = [str(i) * 40 for i in range(1, 5)]
            for index, revision in enumerate(revisions):
                repository = "darim" if index < 3 else "darim-frontend"
                tag = f"{address}/{repository}:{revision}"
                tags.append(tag)
                docker("import", "--change", f"LABEL smoke-revision={revision}",
                       "-", tag, input=archive.getvalue())
                docker("push", tag)
            live_tag = f"{address}/darim:production"
            tags.append(live_tag)
            docker("tag", tags[0], live_tag)
            docker("push", live_tag)
            live, rollback, expired = [module.manifest(tag)[0] for tag in revisions[:3]]
            module.STATUS.write_text(json.dumps({
                "current": {"digest": live},
                "previous": {"digest": rollback},
                "attempt": {"status": "success", "digest": live}
            }))
            mount = Path(docker("volume", "inspect", "-f", "{{.Mountpoint}}", name))
            old = time.time() - 4 * 86400
            for link in mount.glob("docker/registry/v2/repositories/darim/_manifests/tags/*/current/link"):
                os.utime(link, (old, old))
            os.environ["DRY_RUN"] = "1"
            module.main()
            assert module.manifest(expired)[0] == expired
            os.environ.pop("DRY_RUN")
            module.main()
            module.main()
            for digest in [live, rollback]:
                assert module.manifest(digest)[0] == digest
            docker("pull", f"{address}/darim@{rollback}")
            docker("pull", tags[3])
            try:
                module.manifest(expired)
            except HTTPError as error:
                assert error.code == 404
            else:
                raise AssertionError("Expired manifest was retained")
        finally:
            subprocess.run(["docker", "rm", "-f", name], check=False)
            if tags:
                subprocess.run(["docker", "image", "rm", *reversed(tags)], check=False)
            subprocess.run(["docker", "volume", "rm", name], check=False)


if __name__ == "__main__":
    main()
