"""Test schedule arguments and offline directory activation without touching launchd or user data."""
import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("backups", Path(__file__).with_name("macos-backups.py"))
backups = importlib.util.module_from_spec(spec)
spec.loader.exec_module(backups)


class BackupControllerTests(unittest.TestCase):
    def test_schedule_uses_no_shell_and_daily_calendar(self):
        """Spaces remain one argument, daily runs use local time and loading catches up a missed day."""
        result = backups.make_plist(Path("/Applications/AgMemory.app/bin"), Path("/path with spaces/data"), Path("/other/backups"))
        self.assertEqual(result["StartCalendarInterval"], {"Hour": 3, "Minute": 0})
        self.assertTrue(result["RunAtLoad"])
        self.assertEqual(result["ProgramArguments"][3], "/path with spaces/data")
        self.assertNotIn("sh", result["ProgramArguments"])

    def test_activation_preserves_previous_data(self):
        """Replacing an offline directory keeps old data in a separately named rollback directory."""
        with tempfile.TemporaryDirectory() as root:
            base = Path(root)
            data, prepared, rollback = [base / name for name in ("data", "prepared", "rollback")]
            data.mkdir()
            (data / "old").write_text("old state")
            (prepared / "memory-graph.lancedb").mkdir(parents=True)
            (prepared / "new").write_text("restored state")
            backups.activate_restore(data, prepared, rollback)
            self.assertEqual((rollback / "old").read_text(), "old state")
            self.assertEqual((data / "new").read_text(), "restored state")

    def test_failed_activation_rolls_back_directory_move(self):
        """An OS rename failure never strands the original data under the rollback name."""
        with tempfile.TemporaryDirectory() as root:
            base = Path(root)
            data, prepared, rollback = [base / name for name in ("data", "prepared", "rollback")]
            data.mkdir()
            (data / "old").write_text("old state")
            (prepared / "memory-graph.lancedb").mkdir(parents=True)
            original = Path.rename

            def rename(path, target):
                if path == prepared:
                    raise OSError("fixture rename failure")
                return original(path, target)

            with patch.object(Path, "rename", rename), self.assertRaises(OSError):
                backups.activate_restore(data, prepared, rollback)
            self.assertEqual((data / "old").read_text(), "old state")
            self.assertFalse(rollback.exists())


if __name__ == "__main__":
    unittest.main()
