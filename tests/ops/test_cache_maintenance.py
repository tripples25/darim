import contextlib
import io
import sqlite3
import sys
import tempfile
import types
import unittest
from pathlib import Path
from unittest import mock

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'ops' / 'woodpecker'))
import cache_maintenance


class CacheMaintenanceTests(unittest.TestCase):
    def create_database(self, statuses):
        temporary = tempfile.TemporaryDirectory()
        path = Path(temporary.name) / 'woodpecker.sqlite'
        connection = sqlite3.connect(path)
        try:
            connection.execute('CREATE TABLE pipelines (status TEXT NOT NULL)')
            connection.executemany('INSERT INTO pipelines VALUES (?)', [(s,) for s in statuses])
            connection.commit()
        finally:
            connection.close()
        return temporary, path

    def test_successful_and_failed_pipelines_are_terminal(self):
        temporary, path = self.create_database(['success', 'failure', 'killed', 'canceled'])
        with temporary:
            self.assertEqual(0, cache_maintenance.count_active_pipelines(path))

    def test_running_pending_and_unrecognized_statuses_block_cleanup(self):
        for status in ('running', 'pending', 'skipped', 'new-status'):
            with self.subTest(status=status):
                temporary, path = self.create_database([status])
                with temporary:
                    self.assertEqual(1, cache_maintenance.count_active_pipelines(path))

    def test_missing_database_fails_closed(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'missing.sqlite'
            with self.assertRaises(sqlite3.OperationalError):
                cache_maintenance.count_active_pipelines(path)

    def test_free_ratio_is_measured_from_root_capacity(self):
        class Usage:
            total = 100
            free = 21
            used = 79

        original = cache_maintenance.shutil.disk_usage
        cache_maintenance.shutil.disk_usage = lambda _path: Usage()
        try:
            self.assertEqual(0.21, cache_maintenance.free_ratio())
        finally:
            cache_maintenance.shutil.disk_usage = original

    def test_cache_budget_is_enforced_even_when_disk_has_room(self):
        self.assertTrue(cache_maintenance.should_prune(11_170_000_000, 0.46))
        self.assertFalse(cache_maintenance.should_prune(5_900_000_000, 0.46))
        self.assertTrue(cache_maintenance.should_prune(5_900_000_000, 0.19))

    def test_docker_human_sizes_parse(self):
        self.assertEqual(11_170_000_000, cache_maintenance.docker_size_bytes('11.17GB'))
        self.assertEqual(271_000_000, cache_maintenance.docker_size_bytes('271MB'))

    def run_main(self, *, active, cache_size, create_lock=True):
        with tempfile.TemporaryDirectory() as directory:
            commands = []
            if create_lock:
                (Path(directory) / 'cache-maintenance.lock').touch()

            def fake_run(command):
                commands.append(command)
                if command[:3] == ['docker', 'ps', '--filter']:
                    return 'worker'
                if command[:3] == ['docker', 'volume', 'inspect']:
                    return directory
                if command[-2:] == ['ps', '-q']:
                    return ''
                if command[-3:] == ['df', '--format', '{{.Type}}|{{.Size}}']:
                    return f'Build Cache|{cache_size}'
                if 'builder' in command and 'prune' in command:
                    return 'pruned'
                return ''

            fake_fcntl = types.SimpleNamespace(LOCK_EX=1, LOCK_NB=2, flock=lambda *_: None)
            output = io.StringIO()
            with mock.patch.dict(sys.modules, {'fcntl': fake_fcntl}), \
                 mock.patch.object(cache_maintenance, 'run', side_effect=fake_run), \
                 mock.patch.object(cache_maintenance, 'count_active_pipelines', return_value=active), \
                 mock.patch.object(cache_maintenance, 'free_ratio', return_value=0.46), \
                 contextlib.redirect_stdout(output):
                result = cache_maintenance.main()
            return commands, output.getvalue(), result

    def test_command_gate_skips_running_pipeline_without_pruning(self):
        commands, _, result = self.run_main(active=1, cache_size='11.17GB')
        self.assertEqual(0, result)
        self.assertFalse(any('prune' in command for command in commands))

    def test_command_prunes_only_builder_cache_and_retains_images_and_volumes(self):
        commands, output, result = self.run_main(active=0, cache_size='11.17GB')
        self.assertEqual(0, result)
        prune_commands = [command for command in commands if 'prune' in command]
        self.assertEqual(1, len(prune_commands))
        self.assertIn('builder', prune_commands[0])
        self.assertIn('6GB', prune_commands[0])
        self.assertIn('4GB', prune_commands[0])
        self.assertIn('12GB', prune_commands[0])
        self.assertIn('images and volumes were retained', output)
        self.assertFalse(any('image' in command and 'prune' in command for command in commands))
        self.assertFalse(any('volume' in command and 'rm' in command for command in commands))

    def test_missing_shared_lock_file_fails_closed(self):
        commands, _, result = self.run_main(active=0, cache_size='11.17GB', create_lock=False)
        self.assertEqual(1, result)
        self.assertFalse(any('prune' in command for command in commands))


if __name__ == '__main__':
    unittest.main()
