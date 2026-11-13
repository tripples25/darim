import json
import os
from pathlib import Path
import subprocess
import unittest

ROOT = Path(__file__).resolve().parents[2]


def monitoring_compose():
    env = dict(
        os.environ,
        DARIM_SCRAPE_TOKEN_FILE="/tmp/test-scrape-token",
        DARIM_GRAFANA_PASSWORD_FILE="/tmp/test-grafana-password",
        DARIM_MONITORING_NETWORK="darim_private",
    )
    result = subprocess.run(
        [
            "docker", "compose", "--profile", "*",
            "-f", str(ROOT / "ops/monitoring/compose.yaml"),
            "-f", str(ROOT / "ops/monitoring/coolify.compose.yaml"),
            "config", "--format", "json",
        ],
        cwd=ROOT,
        env=env,
        capture_output=True,
        text=True,
    )
    if result.returncode:
        raise AssertionError(f"docker compose config failed: {result.stderr}")
    return json.loads(result.stdout)


class SystemMetricsTests(unittest.TestCase):
    def test_exporter_is_host_scoped_private_and_resource_bounded(self):
        exporter = monitoring_compose()["services"]["node-exporter"]
        command = exporter["command"]

        self.assertEqual("quay.io/prometheus/node-exporter:v1.12.1", exporter["image"])
        self.assertEqual("host", exporter["network_mode"])
        self.assertEqual("host", exporter["pid"])
        self.assertFalse(exporter.get("ports"))
        self.assertEqual("65534:65534", exporter["user"])
        self.assertTrue(exporter["read_only"])
        self.assertEqual(["ALL"], exporter["cap_drop"])
        self.assertIn("no-new-privileges:true", exporter["security_opt"])
        self.assertLessEqual(int(exporter["mem_limit"]), 128 * 1024 * 1024)
        self.assertLessEqual(exporter["cpus"], 0.25)
        self.assertLessEqual(exporter["pids_limit"], 64)
        self.assertIn("--web.listen-address=172.28.45.1:9100", command)
        self.assertIn("--path.rootfs=/host", command)
        self.assertIn("--path.procfs=/host/proc", command)
        self.assertIn("--path.sysfs=/host/sys", command)
        self.assertFalse(any("SYS_TIME" in arg for arg in command))
        self.assertFalse(exporter.get("networks"))

        mounts = {mount["target"]: mount for mount in exporter["volumes"]}
        self.assertTrue(mounts["/host"]["read_only"])
        self.assertEqual("rslave", mounts["/host"]["bind"]["propagation"])
        self.assertTrue(mounts["/host/proc"]["read_only"])
        self.assertTrue(mounts["/host/sys"]["read_only"])

    def test_prometheus_and_dashboard_use_system_job_and_existing_datasource(self):
        prometheus = (ROOT / "ops/monitoring/prometheus.yml").read_text()
        self.assertIn("job_name: darim-system", prometheus)
        self.assertIn("172.28.45.1:9100", prometheus)

        dashboard = json.loads((ROOT / "ops/monitoring/grafana/dashboards/system.json").read_text(encoding="utf-8"))
        self.assertEqual("darim-system", dashboard["uid"])
        self.assertTrue(dashboard["panels"])
        for panel in dashboard["panels"]:
            with self.subTest(panel=panel["title"]):
                self.assertEqual("darim-prometheus", panel["datasource"]["uid"])
                self.assertTrue(panel["targets"])

        queries = "\n".join(target["expr"] for panel in dashboard["panels"] for target in panel["targets"])
        self.assertIn("node_memory_MemAvailable_bytes", queries)
        self.assertIn("node_memory_SwapTotal_bytes", queries)
        self.assertIn('mountpoint=\"/\"', queries)
        self.assertIn("node_filesystem_files_free", queries)
        self.assertIn("node_disk_read_bytes_total", queries)
        self.assertIn("node_disk_io_time_seconds_total", queries)
        self.assertIn("en[[:alnum:]_.-]+", queries)


if __name__ == "__main__":
    unittest.main()
