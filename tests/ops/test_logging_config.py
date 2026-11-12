import json
import os
import subprocess
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def compose_config(files, variables):
    env = dict(os.environ, **variables)
    command = ['docker', 'compose', '--profile', '*']
    for file in files:
        command.extend(['-f', str(ROOT / file)])
    command.extend(['config', '--format', 'json'])
    result = subprocess.run(command, cwd=ROOT, env=env, text=True, capture_output=True)
    if result.returncode:
        raise AssertionError(f'docker compose config failed: {result.stderr}')
    return json.loads(result.stdout)


def assert_rotated(test, service):
    test.assertEqual('json-file', service['logging']['driver'])
    test.assertEqual('10m', service['logging']['options']['max-size'])
    test.assertEqual('3', service['logging']['options']['max-file'])


class LoggingConfigTests(unittest.TestCase):
    def test_darim_app_database_and_migration_logs_are_rotated(self):
        config = compose_config(
            ['compose.yaml', 'compose.dev.yaml'],
            {
                'POSTGRES_PASSWORD': 'test-only',
                'AUTH_SIGNING_KEY': 'test-only',
                'DOMAIN': 'example.invalid',
            },
        )
        for name in ('app', 'db', 'migrate', 'caddy', 'backup'):
            with self.subTest(service=name):
                assert_rotated(self, config['services'][name])

    def test_woodpecker_and_nested_job_logs_are_rotated(self):
        config = compose_config(
            ['ops/woodpecker/compose.yaml'],
            {
                'WOODPECKER_GITHUB_CLIENT': 'test-only',
                'WOODPECKER_GITHUB_SECRET': 'test-only',
                'WOODPECKER_AGENT_SECRET': 'test-only',
            },
        )
        for name in ('server', 'worker-docker', 'agent', 'registry'):
            with self.subTest(service=name):
                assert_rotated(self, config['services'][name])

        command = config['services']['worker-docker']['command']
        self.assertIn('--log-driver=json-file', command)
        self.assertIn('--log-opt=max-size=10m', command)
        self.assertIn('--log-opt=max-file=3', command)


if __name__ == '__main__':
    unittest.main()
