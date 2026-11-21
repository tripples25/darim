import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

DEV_CONFIG = Path(__file__).with_name("deploy-dev.yaml").read_text()
IGNORE_CONFIG = "when:\n  - evaluate: 'false'\nsteps: []\n"


class ConfigurationHandler(BaseHTTPRequestHandler):
    def do_POST(self):
        if self.path != "/_darim/ci-config":
            self.send_error(404)
            return

        try:
            length = int(self.headers.get("Content-Length", "0"))
            if not 0 < length <= 4 * 1024 * 1024:
                raise ValueError("Invalid configuration request length")
            request = json.loads(self.rfile.read(length))
            repo = request["repo"]["id"]
            event = request["pipeline"]["event"]
            ref = request["pipeline"]["ref"]
        except (TypeError, ValueError, KeyError):
            self.send_error(400)
            return

        if repo not in (1, 2):
            self.send_error(403)
            return
        if ref == "refs/heads/main" and event in ("push", "manual"):
            self.send_response(204)
            self.end_headers()
            return

        config = DEV_CONFIG if ref == "refs/heads/dev" and event == "push" else IGNORE_CONFIG
        body = json.dumps({"configs": [{"name": "darim-branch.yaml", "data": config}]}).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *_args):
        pass


if __name__ == "__main__":
    ThreadingHTTPServer(("172.28.45.1", 8097), ConfigurationHandler).serve_forever()
