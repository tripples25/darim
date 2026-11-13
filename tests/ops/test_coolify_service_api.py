import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
UUID = 'a' * 24
SHA = 'b' * 40
IMAGE = '127.0.0.1:5000/darim@sha256:' + 'c' * 64
IMAGE_ID = 'sha256:' + 'd' * 64
TOKEN = 'mock-token-never-log-this'


class CoolifyServiceApiTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.app_root = self.root / 'app'
        self.app_root.mkdir()
        self.bin = self.root / 'bin'
        self.bin.mkdir()
        self.api_log = self.root / 'api.jsonl'
        self.docker_log = self.root / 'docker.jsonl'
        self.token_file = self.root / 'coolify-token'
        self.token_file.write_text(TOKEN)
        self.token_file.chmod(0o600)
        self.promote_script = self.root / 'promote-coolify.sh'
        self.promote_script.write_text((ROOT / 'ops/promote-coolify.sh').read_text())
        self.install_mocks()

    def tearDown(self):
        self.temp.cleanup()

    def install_mocks(self):
        curl = self.bin / 'curl'
        curl.write_text('''#!/usr/bin/env python3
import json,os,sys
a=sys.argv[1:]
url=a[-1]
if url.endswith('/health'):
    print('{"status":"ok"}')
    raise SystemExit(0)
def arg(name): return a[a.index(name)+1]
method=arg('--request')
header=sys.stdin.read().strip()
auth_ok=header == 'Authorization: Bearer ' + os.environ['MOCK_TOKEN']
body=arg('--data-raw')
request={'method':method,'url':url,'body':json.loads(body),'auth_header_seen':auth_ok,
         'token_in_argv':os.environ['MOCK_TOKEN'] in ' '.join(a)}
with open(os.environ['MOCK_API_LOG'],'a') as f: f.write(json.dumps(request)+'\\n')
if os.environ.get('MOCK_CURL_FAIL_METHOD') == method: raise SystemExit(7)
if method == 'PATCH':
    status=int(os.environ.get('MOCK_PATCH_STATUS','201'))
    response=os.environ.get('MOCK_PATCH_BODY','[{"key":"DARIM_IMAGE"}]')
else:
    status=int(os.environ.get('MOCK_START_STATUS','200'))
    response=os.environ.get('MOCK_START_BODY','{"message":"Service starting request queued."}')
with open(arg('--output'),'w') as f: f.write(response)
sys.stdout.write(str(status))
''')
        docker = self.bin / 'docker'
        docker.write_text('''#!/usr/bin/env python3
import json,os,sys
a=sys.argv[1:]
with open(os.environ['MOCK_DOCKER_LOG'],'a') as f: f.write(json.dumps(a)+'\\n')
if a[:2] == ['image','inspect']:
    print(os.environ['MOCK_IMAGE_ID'])
elif a[0] == 'inspect' and len(a) >= 4 and a[1] == '-f':
    template=a[2]
    if 'Health.Status' in template: print('healthy')
    elif 'Mounts' in template: print('darim_postgres')
    elif 'State.Running' in template: print(os.environ['MOCK_IMAGE_ID'] + ' true')
    elif 'State.Status' in template: print(os.environ['MOCK_MIGRATION_IMAGE'] + ' exited 0')
elif a[0] == 'exec' and any('pg_dump' in value for value in a):
    print('mock database backup')
''')
        sleep = self.bin / 'sleep'
        sleep.write_text('#!/bin/sh\nexit 0\n')
        for path in self.bin.iterdir():
            path.chmod(0o755)

    def run_promote(self, **overrides):
        env = dict(os.environ, PATH=str(self.bin) + os.pathsep + os.environ['PATH'],
                   MOCK_API_LOG=str(self.api_log), MOCK_DOCKER_LOG=str(self.docker_log),
                   MOCK_TOKEN=TOKEN, MOCK_IMAGE_ID=IMAGE_ID,
                   MOCK_MIGRATION_IMAGE=IMAGE_ID,
                   DARIM_PROMOTE_ROOT=str(self.app_root),
                   DARIM_COOLIFY_TOKEN_FILE=str(self.token_file))
        env.update({key: str(value) for key, value in overrides.items()})
        return subprocess.run(['bash', str(self.promote_script), UUID, SHA, '42', IMAGE],
                              env=env, capture_output=True, text=True)

    def api_calls(self):
        if not self.api_log.exists():
            return []
        return [json.loads(line) for line in self.api_log.read_text().splitlines()]

    def docker_calls(self):
        if not self.docker_log.exists():
            return []
        return [json.loads(line) for line in self.docker_log.read_text().splitlines()]

    def test_updates_service_image_then_starts_through_documented_api(self):
        result = self.run_promote()
        self.assertEqual(0, result.returncode, result.stderr)
        calls = self.api_calls()
        self.assertEqual(['PATCH', 'POST'], [call['method'] for call in calls])
        self.assertEqual('https://coolify.hype-ny.ru/api/v1/services/' + UUID + '/envs/bulk', calls[0]['url'])
        self.assertEqual({'data': [{'key': 'DARIM_IMAGE', 'value': IMAGE}]}, calls[0]['body'])
        self.assertEqual('https://coolify.hype-ny.ru/api/v1/services/' + UUID + '/start', calls[1]['url'])
        self.assertEqual({}, calls[1]['body'])
        self.assertTrue(all(call['auth_header_seen'] for call in calls))
        self.assertFalse(any(call['token_in_argv'] for call in calls))
        self.assertNotIn(TOKEN, result.stdout + result.stderr)
        self.assertFalse(any('tinker' in str(call) for call in self.docker_calls()))
        self.assertTrue((self.app_root / 'current-coolify-release').exists())

    def test_transient_environment_api_error_is_retryable_before_start(self):
        result = self.run_promote(MOCK_PATCH_STATUS=503)
        self.assertEqual(10, result.returncode, result.stderr)
        self.assertEqual(['PATCH'], [call['method'] for call in self.api_calls()])
        self.assertFalse((self.app_root / 'current-coolify-release').exists())

    def test_environment_api_rejection_is_terminal_before_start(self):
        result = self.run_promote(MOCK_PATCH_STATUS=401,
                                  MOCK_PATCH_BODY='{"error":"private-response-body"}')
        self.assertEqual(1, result.returncode, result.stderr)
        self.assertEqual(['PATCH'], [call['method'] for call in self.api_calls()])
        self.assertNotIn('private-response-body', result.stdout + result.stderr)

    def test_start_timeout_is_ambiguous_and_never_marked_success(self):
        result = self.run_promote(MOCK_CURL_FAIL_METHOD='POST')
        self.assertEqual(20, result.returncode, result.stderr)
        self.assertEqual(['PATCH', 'POST'], [call['method'] for call in self.api_calls()])
        self.assertFalse((self.app_root / 'current-coolify-release').exists())
        self.assertNotIn(TOKEN, result.stdout + result.stderr)

    def test_start_http_error_is_ambiguous_and_never_marked_success(self):
        result = self.run_promote(MOCK_START_STATUS=502,
                                  MOCK_START_BODY='{"error":"private-response-body"}')
        self.assertEqual(20, result.returncode, result.stderr)
        self.assertEqual(['PATCH', 'POST'], [call['method'] for call in self.api_calls()])
        self.assertFalse((self.app_root / 'current-coolify-release').exists())
        self.assertNotIn('private-response-body', result.stdout + result.stderr)

    def test_stale_successful_migration_image_is_not_marked_as_released(self):
        stale_image_id = 'sha256:' + 'e' * 64
        result = self.run_promote(MOCK_MIGRATION_IMAGE=stale_image_id)
        self.assertEqual(20, result.returncode, result.stderr)
        self.assertEqual(['PATCH', 'POST'], [call['method'] for call in self.api_calls()])
        self.assertFalse((self.app_root / 'current-coolify-release').exists())

    def test_group_readable_token_file_is_rejected_before_api_call(self):
        self.token_file.chmod(0o640)
        result = self.run_promote()
        self.assertEqual(1, result.returncode, result.stderr)
        self.assertEqual([], self.api_calls())


if __name__ == '__main__':
    unittest.main()
