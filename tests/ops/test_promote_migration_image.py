import os
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
EXPECTED = 'sha256:' + 'a' * 64
STALE = 'sha256:' + 'c' * 64
TOKEN = 'migration-test-token'
UUID = 'a' * 24


class PromoteMigrationImageTests(unittest.TestCase):
    def run_promote(self, migration_image):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            fake_bin = root / 'bin'
            fake_bin.mkdir()
            (root / 'app').mkdir()
            script = root / 'promote-coolify.sh'
            source = (ROOT / 'ops/promote-coolify.sh').read_text()
            source = source.replace('attempt<=60', 'attempt<=2')
            script.write_text(source)
            docker = fake_bin / 'docker'
            docker.write_text('''#!/usr/bin/env python3
import os,sys
a=sys.argv[1:]
if a[:2] == ['image','inspect']:
    print(os.environ['EXPECTED_IMAGE'])
elif a[0] == 'inspect' and len(a) >= 4 and a[1] == '-f':
    if 'Health.Status' in a[2]: print('healthy')
    elif 'Mounts' in a[2]: print('darim_postgres')
    elif 'State.Running' in a[2]: print(os.environ['EXPECTED_IMAGE'] + ' true')
    elif 'State.Status' in a[2]: print(os.environ['MIGRATION_IMAGE'] + ' exited 0')
elif a[0] == 'exec' and len(a) > 1 and a[1].startswith('db-'):
    if 'pg_dump' in a: print('backup')
elif a[0] == 'exec' and 'coolify' in a:
    print('COOLIFY_DEPLOY_REQUESTED')
''')
            docker.chmod(0o755)
            curl = fake_bin / 'curl'
            curl.write_text('''#!/usr/bin/env python3
import os,sys
a=sys.argv[1:]
url=a[-1]
if url.endswith('/health'):
    print('{"status":"ok"}')
    raise SystemExit(0)
def arg(name): return a[a.index(name)+1]
method=arg('--request')
if method == 'PATCH':
    open(arg('--output'),'w').write('[{"key":"DARIM_IMAGE"}]')
    sys.stdout.write('201')
else:
    open(arg('--output'),'w').write('{"message":"starting"}')
    sys.stdout.write('200')
''')
            curl.chmod(0o755)
            sleep = fake_bin / 'sleep'
            sleep.write_text('#!/bin/sh\nexit 0\n')
            sleep.chmod(0o755)
            token_file = root / 'coolify-token'
            token_file.write_text(TOKEN)
            token_file.chmod(0o600)
            env = dict(os.environ, PATH=str(fake_bin) + os.pathsep + os.environ['PATH'],
                       EXPECTED_IMAGE=EXPECTED, MIGRATION_IMAGE=migration_image,
                       DARIM_PROMOTE_ROOT=str(root / 'app'),
                       DARIM_COOLIFY_TOKEN_FILE=str(token_file))
            return subprocess.run(['bash', str(script), UUID, 'b' * 40, '42', 'darim:test'],
                                  env=env, capture_output=True, text=True)

    def test_current_release_migration_container_can_succeed(self):
        result = self.run_promote(EXPECTED)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn('Released', result.stdout)

    def test_stale_successful_migration_container_cannot_succeed(self):
        result = self.run_promote(STALE)
        self.assertEqual(20, result.returncode, result.stderr)
        self.assertNotIn('Released', result.stdout)


if __name__ == '__main__':
    unittest.main()
