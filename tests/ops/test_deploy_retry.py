import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
DIGEST = 'sha256:' + 'a' * 64
SHA = 'b' * 40


class DeployRetryTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.bin = self.root / 'bin'
        self.bin.mkdir()
        self.status = self.root / 'deployment.json'
        self.log = self.root / 'commands.jsonl'
        self.install_mocks()
        self.status.write_text(json.dumps({'current': {'commit': 'c' * 40, 'pipeline': '7', 'digest': 'sha256:' + 'c' * 64}}))

    def tearDown(self):
        self.temp.cleanup()

    def install_mocks(self):
        if os.name == 'nt':
            (self.bin / 'python3').write_text('''#!/usr/bin/env bash
exec py -3 "$@"
''')
        (self.bin / 'curl').write_text('''#!/usr/bin/env python3
import json,os,sys
with open(os.environ['MOCK_LOG'],'a') as f: f.write(json.dumps(['curl',*sys.argv[1:]])+'\\n')
print('Docker-Content-Digest: ' + os.environ['MOCK_DIGEST'])
''')
        (self.bin / 'docker').write_text('''#!/usr/bin/env python3
import json,os,sys
with open(os.environ['MOCK_LOG'],'a') as f: f.write(json.dumps(['docker',*sys.argv[1:]])+'\\n')
if sys.argv[1:3] == ['image','inspect']:
    fmt=sys.argv[4]
    if 'org.opencontainers.image.revision' in fmt: print(os.environ['MOCK_SHA'])
    elif 'io.darim.ci.pipeline' in fmt: print(os.environ['MOCK_PIPELINE'])
''')
        if os.name == 'nt':
            (self.bin / 'jq').write_text('''#!/usr/bin/env python3
import json,sys
a=sys.argv[1:]
if a[0]=='-r':
    data=json.load(open(a[-1])); key=a[1].split()[0]
    val={'.attempt.digest':'attempt.digest','.attempt.status':'attempt.status','.attempt.commit':'attempt.commit','.attempt.pipeline':'attempt.pipeline','.attempt.retryCount':'attempt.retryCount','.attempt.nextRetryAt':'attempt.nextRetryAt','.attempt.nextRetryEpoch':'attempt.nextRetryEpoch','.attempt.terminal':'attempt.terminal'}[key]
    obj=data
    for part in val.split('.'): obj=obj.get(part) if isinstance(obj,dict) else None
    print(json.dumps(obj) if isinstance(obj,(dict,list)) else ('' if obj is None else str(obj)))
elif a[0]=='-c':
    data=json.load(open(a[-1])); print(json.dumps(data.get('current')) if data.get('current') is not None else 'null')
elif a[0]=='-n':
    named={}; i=1
    while i<len(a) and a[i].startswith('--'):
        flag=a[i]; name=a[i+1]
        if flag=='--argjson': value=json.loads(a[i+2]); i+=3
        else: value=a[i+2]; i+=3
        named[name]=value
    now=named['time']; state=named['state']; current=named['current']
    attempt={k:named.get(k) for k in ['commit','pipeline','digest','status','time','retryCount','maxRetries','nextRetryAt','terminal','failureClass','phase','error','alert']}
    out={'attempt':attempt,'current':current}
    if state=='success': out['current']={'commit':named['commit'],'pipeline':named['pipeline'],'digest':named['digest'],'time':now,'woodpecker':'https://ci.hype-ny.ru/repos/1/pipeline/'+named['pipeline'],'url':'https://wishes.hype-ny.ru'}
    print(json.dumps(out))
''')
        (self.bin / 'promote').write_text('''#!/usr/bin/env python3
import json,os,sys
with open(os.environ['MOCK_LOG'],'a') as f: f.write(json.dumps(['promote',*sys.argv[1:]])+'\\n')
if os.environ.get('MOCK_PROMOTE_SLEEP'): __import__('time').sleep(float(os.environ['MOCK_PROMOTE_SLEEP']))
sys.exit(int(os.environ.get('PROMOTE_EXIT','0')))
''')
        (self.bin / 'flock').write_text('''#!/usr/bin/env python3
import os,sys
sys.exit(int(os.environ.get('MOCK_FLOCK_EXIT','0')))
''')
        (self.bin / 'date').write_text('''#!/usr/bin/env python3
import datetime,os,sys
now=int(os.environ['MOCK_NOW'])
args=sys.argv[1:]
if '-d' in args: now=int(args[args.index('-d')+1][1:])
print(str(now) if args==['-u','+%s'] else datetime.datetime.fromtimestamp(now,datetime.timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ'))
''')
        for path in self.bin.iterdir():
            path.chmod(0o755)

    def run_reconcile(self, now, promote_exit=0, args=(), flock_exit=0):
        env = dict(os.environ, PATH=str(self.bin) + os.pathsep + os.environ['PATH'],
                   MOCK_DIGEST=DIGEST, MOCK_SHA=SHA, MOCK_PIPELINE='42',
                   MOCK_LOG=str(self.log), MOCK_NOW=str(now), PROMOTE_EXIT=str(promote_exit), MOCK_FLOCK_EXIT=str(flock_exit),
                   DARIM_DEPLOYMENT_STATUS_FILE=str(self.status),
                   DARIM_RECONCILE_LOCK=str(self.root / 'reconcile.lock'),
                   DARIM_PROMOTE_SCRIPT=str(self.bin / 'promote'))
        return subprocess.run([os.environ.get('DARIM_TEST_BASH', 'bash'), str(ROOT / 'ops/reconcile-registry.sh'), *args], env=env, capture_output=True)

    def read_status(self):
        return json.loads(self.status.read_text())

    def promote_calls(self):
        if not self.log.exists():
            return []
        return [json.loads(line) for line in self.log.read_text().splitlines() if json.loads(line)[0] == 'promote']

    def test_transient_failure_waits_then_retries_once_and_keeps_current(self):
        first = self.run_reconcile(1000, promote_exit=10)
        self.assertEqual(0, first.returncode, first.stderr)
        state = self.read_status()
        self.assertEqual('retrying', state['attempt']['status'])
        self.assertEqual(1, state['attempt']['retryCount'])
        self.assertFalse(state['attempt']['terminal'])
        self.assertEqual('c' * 40, state['current']['commit'])
        self.assertEqual(1, len(self.promote_calls()))

        early = self.run_reconcile(1001, promote_exit=0)
        self.assertEqual(0, early.returncode, early.stderr)
        self.assertEqual(1, len(self.promote_calls()), 'must honor backoff')

        retried = self.run_reconcile(1015, promote_exit=0)
        self.assertEqual(0, retried.returncode, retried.stderr)
        state = self.read_status()
        self.assertEqual('success', state['attempt']['status'])
        self.assertEqual(SHA, state['current']['commit'])
        self.assertEqual(2, len(self.promote_calls()))

    def test_transient_failures_stop_after_bounded_retries_and_manual_retry_resets(self):
        for index, now in enumerate((1000, 1015, 1045)):
            result = self.run_reconcile(now, promote_exit=10)
            self.assertEqual(1 if index == 2 else 0, result.returncode, result.stderr)
        state = self.read_status()
        self.assertEqual('failure', state['attempt']['status'])
        self.assertTrue(state['attempt']['terminal'])
        self.assertTrue(state['attempt']['alert'])
        self.assertEqual('transient', state['attempt']['failureClass'])
        self.assertEqual('c' * 40, state['current']['commit'])
        self.assertEqual(3, len(self.promote_calls()))

        retried = self.run_reconcile(1050, promote_exit=0, args=('--retry',))
        self.assertEqual(0, retried.returncode, retried.stderr)
        self.assertEqual('success', self.read_status()['attempt']['status'])
        self.assertEqual(4, len(self.promote_calls()))

    def test_permanent_failure_is_terminal_without_automatic_retry(self):
        first = self.run_reconcile(1000, promote_exit=20)
        self.assertEqual(1, first.returncode, first.stderr)
        state = self.read_status()
        self.assertEqual('failure', state['attempt']['status'])
        self.assertTrue(state['attempt']['terminal'])
        self.assertTrue(state['attempt']['alert'])
        self.assertEqual('permanent', state['attempt']['failureClass'])
        again = self.run_reconcile(3000, promote_exit=0)
        self.assertEqual(0, again.returncode, again.stderr)
        self.assertEqual(1, len(self.promote_calls()))

    def test_manual_retry_bypasses_backoff_and_resets_retry_budget(self):
        self.status.write_text(json.dumps({
            'attempt': {
                'commit': SHA, 'pipeline': '42', 'digest': DIGEST,
                'status': 'retrying', 'retryCount': 2, 'nextRetryEpoch': 9999,
            },
            'current': {'commit': 'c' * 40, 'pipeline': '7', 'digest': 'sha256:' + 'c' * 64},
        }))
        result = self.run_reconcile(1000, promote_exit=10, args=('--retry',))
        self.assertEqual(0, result.returncode, result.stderr)
        state = self.read_status()
        self.assertEqual('retrying', state['attempt']['status'])
        self.assertEqual(1, state['attempt']['retryCount'])
        self.assertEqual(1, len(self.promote_calls()))
    def test_locked_reconciliation_does_not_overwrite_running_attempt_or_current_release(self):
        self.status.write_text(json.dumps({
            'attempt': {'commit': SHA, 'pipeline': '42', 'digest': DIGEST, 'status': 'running', 'retryCount': 0},
            'current': {'commit': 'c' * 40, 'pipeline': '7', 'digest': 'sha256:' + 'c' * 64},
        }))
        result = self.run_reconcile(1000, flock_exit=1)
        self.assertEqual(0, result.returncode, result.stderr)
        state = self.read_status()
        self.assertEqual('running', state['attempt']['status'])
        self.assertEqual('c' * 40, state['current']['commit'])
        self.assertEqual([], self.promote_calls())


if __name__ == '__main__':
    unittest.main()
