"""run_player.py: the flags every run carries, the Player.log location, the 'Run finished' parse, the timeout kill, and
the verdict on a fresh, stale or killed-after-report log."""
import io
import os
import shutil
import sys
import tempfile
import time
import unittest
from contextlib import redirect_stdout
from pathlib import Path
from unittest import mock

import helpers  # noqa: F401 (puts Tools/Python on the import path)
import run_player

FINISHED = "[Launch] Run finished: report C:/Users/x/AppData/LocalLow/co/Game\\Benchmarks\\Run 1.log, exit code {code}"


class ArgumentTests(unittest.TestCase):
    def test_required_flags_are_added_once(self):
        args = run_player.player_arguments(["-mc-run", "benchmark", "-mc-quit"])
        self.assertEqual(args, ["-mc-run", "benchmark", "-mc-quit", "-force-d3d11", "-mc-mute"])

    def test_a_chosen_graphics_api_wins(self):
        args = run_player.player_arguments(["-force-vulkan", "-mc-run", "startup-new"])
        self.assertNotIn("-force-d3d11", args)
        self.assertEqual(args[0], "-force-vulkan")


class ParseTests(unittest.TestCase):
    def test_log_path_comes_from_the_project_names(self):
        text = "PlayerSettings:\n  companyName: Some Co\n  productName: My Game\n"
        path = run_player.player_log_path(text, Path("/home/u"))
        self.assertEqual(path, Path("/home/u/AppData/LocalLow/Some Co/My Game/Player.log"))

    def test_missing_names_are_an_error(self):
        with self.assertRaises(ValueError):
            run_player.player_log_path("PlayerSettings:\n  productName: X\n", Path("/h"))

    def test_last_finished_line_wins(self):
        log = "\n".join([FINISHED.format(code=1), "noise", FINISHED.format(code=0)])
        report, code = run_player.parse_run_finished(log)
        self.assertEqual(code, 0)
        self.assertTrue(report.endswith("Run 1.log"))

    def test_no_report_and_no_line(self):
        self.assertEqual(run_player.parse_run_finished("[Launch] Run finished: report (none), exit code 1"), (None, 1))
        self.assertIsNone(run_player.parse_run_finished("nothing here"))


class ProcessTests(unittest.TestCase):
    def test_timeout_kills_the_process(self):
        started = time.time()
        code, killed = run_player.run_process([sys.executable, "-c", "import time; time.sleep(30)"], 0.5)
        self.assertTrue(killed)
        self.assertIsNone(code)
        self.assertLess(time.time() - started, 15)

    def test_exit_code_passes_through(self):
        self.assertEqual(run_player.run_process([sys.executable, "-c", "raise SystemExit(3)"], 30), (3, False))


class MainTests(unittest.TestCase):
    def setUp(self):
        self.folder = Path(tempfile.mkdtemp(prefix="run-player-test-"))
        self.exe = self.folder / "Game.exe"
        self.exe.write_bytes(b"")
        self.log = self.folder / "Player.log"
        self.out = self.folder / "runs"

    def tearDown(self):
        shutil.rmtree(self.folder, ignore_errors=True)

    def run_main(self, fake_process):
        argv = ["--exe", str(self.exe), "--tag", "t1", "--out", str(self.out), "--", "-mc-run", "startup-new"]
        stdout = io.StringIO()
        with mock.patch.object(run_player, "player_log_path", return_value=self.log), \
                mock.patch.object(run_player, "run_process", side_effect=fake_process), redirect_stdout(stdout):
            code = run_player.main(argv)
        return code, stdout.getvalue()

    def writes_log(self, text, result):
        def fake(command, timeout_seconds):
            self.assertIn("-mc-quit", command)
            self.log.write_text(text, encoding="utf-8")
            return result
        return fake

    def test_finished_run_succeeds_and_saves_the_log(self):
        code, out = self.run_main(self.writes_log(FINISHED.format(code=0), (0, False)))
        self.assertEqual(code, 0, out)
        self.assertTrue((self.out / "t1_Player.log").is_file())
        self.assertIn("run exit 0", (self.out / "runs.txt").read_text(encoding="utf-8"))

    def test_a_kill_after_the_report_still_succeeds(self):
        code, out = self.run_main(self.writes_log(FINISHED.format(code=0), (None, True)))
        self.assertEqual(code, 0, out)
        self.assertIn("killed (timeout)", out)

    def test_a_failed_run_prints_the_log_tail(self):
        code, out = self.run_main(self.writes_log("line a\nlast line\n" + FINISHED.format(code=1), (1, False)))
        self.assertEqual(code, 1)
        self.assertIn("last line", out)

    def test_a_log_the_launch_did_not_touch_is_not_read_even_when_just_written(self):
        # Back-to-back runs: the previous player wrote this log a moment before the launch that then died.
        self.log.write_text(FINISHED.format(code=0), encoding="utf-8")
        code, out = self.run_main(lambda command, timeout_seconds: (1, False))
        self.assertEqual(code, 1)
        self.assertIn("not written by this launch", out)

    def test_a_log_older_than_the_launch_is_not_read(self):
        self.log.write_text(FINISHED.format(code=0), encoding="utf-8")
        old = time.time() - 3600
        os.utime(self.log, (old, old))
        code, out = self.run_main(lambda command, timeout_seconds: (1, False))
        self.assertEqual(code, 1)
        self.assertIn("not written by this launch", out)
        self.assertFalse((self.out / "t1_Player.log").exists())


if __name__ == "__main__":
    unittest.main()
