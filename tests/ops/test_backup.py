import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]

class BackupTests(unittest.TestCase):
    def run_backup(self, fail=False, fail_list=False):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            aws = root / 'aws'
            aws.write_text('''#!/usr/bin/env python3
import os,sys,json
with open(os.environ['COMMAND_LOG'],'a') as log: log.write(json.dumps(sys.argv[1:])+'\\n')
if 'cp' in sys.argv and os.environ.get('FAIL_UPLOAD')=='1':sys.exit(1)
if 'list-objects-v2' in sys.argv and os.environ.get('FAIL_LIST')=='1':sys.exit(1)
if 'list-objects-v2' in sys.argv:print('darim/daily/old.dump\\tNone\\noutside/key.dump')
''')
            dump = root / 'pg_dump'
            dump.write_text('''#!/usr/bin/env python3
import pathlib,sys
pathlib.Path(next(a[7:] for a in sys.argv if a.startswith('--file='))).write_bytes(b'PGDMP-test')
''')
            aws.chmod(0o755)
            dump.chmod(0o755)
            env = dict(os.environ, PATH=str(root)+os.pathsep+os.environ['PATH'],
                       S3_ENDPOINT='https://s3.invalid', S3_BUCKET='test',
                       AWS_ACCESS_KEY_ID='fake', AWS_SECRET_ACCESS_KEY='fake',
                       COMMAND_LOG=str(root/'commands'), FAIL_UPLOAD='1' if fail else '0', FAIL_LIST='1' if fail_list else '0')
            result = subprocess.run(['sh', str(ROOT/'ops/backup.sh')], env=env, capture_output=True)
            commands = [json.loads(line) for line in (root/'commands').read_text().splitlines()]
            upload = next(c for c in commands if 'cp' in c)
            self.assertFalse(Path(upload[4]).exists(), 'temporary dump must be removed')
            return result, commands

    def test_upload_then_retention_only_within_own_prefix(self):
        result, commands = self.run_backup()
        self.assertEqual(0, result.returncode, result.stderr)
        deletes = [c for c in commands if 'delete-object' in c]
        self.assertEqual(1, len(deletes))
        self.assertEqual('darim/daily/old.dump', deletes[0][-1])

    def test_listing_failure_returns_nonzero(self):
        result, commands = self.run_backup(fail_list=True)
        self.assertNotEqual(0, result.returncode)
        self.assertFalse(any('delete-object' in c for c in commands))

    def test_failed_upload_does_not_delete_existing_backups(self):
        result, commands = self.run_backup(fail=True)
        self.assertNotEqual(0, result.returncode)
        self.assertFalse(any('delete-object' in c or 'list-objects-v2' in c for c in commands))

if __name__ == '__main__':
    unittest.main()
