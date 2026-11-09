"""Pure launcher protocol tests; no native window, remote network or real user store is opened."""
import io
import json
from pathlib import Path
import subprocess
import sys
import unittest
from unittest.mock import Mock, patch
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'scripts'))
import desktop_launcher as launcher


class LauncherTests(unittest.TestCase):
    def test_readiness_requires_exact_loopback_origin(self):
        self.assertEqual('http://127.0.0.1:8891/', launcher.local_origin('http://127.0.0.1:8891/'))
        for value in ['http://localhost:8891/', 'https://127.0.0.1:8891/', 'http://user@127.0.0.1:8891/', 'http://127.0.0.1:0/', 'http://127.0.0.1:8891/path', 'http://127.0.0.1:8891/?token=x']:
            self.assertIsNone(launcher.local_origin(value))

    def test_bounded_stream_discards_noise_and_rejects_foreign_readiness(self):
        backend = launcher.Backend('/unused')
        data = b'x' * 100_000 + b'\nAGMEMORY_DESKTOP_URL=http://attacker.invalid:8891/\nAGMEMORY_DESKTOP_URL=http://127.0.0.1:1234/\n'
        backend.process = Mock(stdout=io.BytesIO(data))
        backend._read()
        self.assertEqual('http://127.0.0.1:1234/', backend.origin)
        self.assertTrue(backend.ready.is_set())

    def test_identity_is_bounded_and_app_specific(self):
        response = Mock()
        response.__enter__ = Mock(return_value=response)
        response.__exit__ = Mock(return_value=False)
        opener = Mock(); opener.open.return_value = response
        good = {'schemaVersion': 'ag-desktop/widget-v1', 'appId': 'agmemory', 'origin': 'http://127.0.0.1:1234'}
        with patch.object(launcher.urllib.request, 'build_opener', return_value=opener):
            response.read.return_value = json.dumps(good).encode(); launcher.verify_identity('http://127.0.0.1:1234/')
            response.read.assert_called_with(65_537)
            response.read.return_value = b' ' * 65_537
            with self.assertRaises(ValueError): launcher.verify_identity('http://127.0.0.1:1234/')
            response.read.return_value = json.dumps(dict(good, appId='other')).encode()
            with self.assertRaises(ValueError): launcher.verify_identity('http://127.0.0.1:1234/')

    def test_stop_closes_owned_pipe_then_bounds_graceful_and_forced_waits(self):
        backend = launcher.Backend('/unused'); child = Mock(); child.poll.return_value = None; backend.process = child
        child.wait.side_effect = [subprocess.TimeoutExpired('owned', 6), 0]
        self.assertTrue(backend.stop()); child.stdin.close.assert_called_once(); child.kill.assert_called_once()
        self.assertEqual([6,1], [call.kwargs['timeout'] for call in child.wait.call_args_list])

    def test_activity_action_is_origin_bound_without_eval(self):
        window = Mock(); window.get_current_url.return_value = 'https://foreign.invalid/'
        launcher.open_activity(window, 'http://127.0.0.1:8891/')
        window.run_js.assert_not_called()
        window.get_current_url.return_value = 'http://127.0.0.1:8891/memory-reader?area=default'
        launcher.open_activity(window, 'http://127.0.0.1:8891/')
        script = window.run_js.call_args.args[0]
        self.assertIn('window.location.origin===', script)
        self.assertIn('http://127.0.0.1:8891', script)
        self.assertNotIn('eval(', script)
        window.evaluate_js.assert_not_called()

    def test_dead_child_is_never_signalled(self):
        backend = launcher.Backend('/unused'); child = Mock(); child.poll.return_value = 0; backend.process = child
        self.assertTrue(backend.stop()); child.kill.assert_not_called(); child.stdin.close.assert_not_called()


if __name__ == '__main__':
    unittest.main()
