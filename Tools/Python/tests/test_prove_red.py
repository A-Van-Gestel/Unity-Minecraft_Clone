"""prove_red.py: argument handling, the single-match mutation, the byte-for-byte restore on every path, and the
batch's stops, verdicts and summary.

The Editor half (recompile, the suite run) needs a live Unity Editor, so it is stubbed here; what is tested is
everything that decides whether the user's file comes back intact and what the exit code says.
"""
import contextlib
import hashlib
import io
import json
import tempfile
import unittest
from pathlib import Path
from unittest import mock

import helpers  # noqa: F401 — puts Tools/Python on sys.path for the import below
import prove_red

ORIGINAL = b"line one\r\nint value = 1;\r\nline three\r\n"
SUITE = "Minecraft Clone/Dev/Validate X"
GREEN = ["[PASS] B7", "ALL 9 X BASELINE TESTS PASSED."]
RED = ["[FAIL] B7 broke", "1 OF 9 X BASELINE TESTS FAILED — REGRESSION."]
# Validate All's verdict when the per-suite [FAIL] lines have rolled out of the console tail.
VALIDATE_ALL_RED = ["<color=red>VALIDATE ALL: REGRESSION — 1 of 400 baselines failed across 1 suite(s).</color> (3m)"]
VALIDATE_ALL_GREEN = ["<color=green>VALIDATE ALL: all 400 baselines across 9 suites PASSED.</color> (3m)"]


class MutateTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.path = Path(self.directory.name) / "Target.cs"

    def tearDown(self):
        self.directory.cleanup()

    def test_replaces_exactly_one_match_and_keeps_crlf(self):
        self.path.write_bytes(ORIGINAL)
        prove_red.mutate(self.path, "int value = 1;\nline three", "int value = 2;\nline three")
        self.assertEqual(self.path.read_bytes(), ORIGINAL.replace(b"value = 1", b"value = 2"))

    def test_zero_or_several_matches_abort_without_writing(self):
        self.path.write_bytes(ORIGINAL)
        for old in ("absent", "line"):
            with self.assertRaises(RuntimeError):
                prove_red.mutate(self.path, old, "x")
        self.assertEqual(self.path.read_bytes(), ORIGINAL)

    def test_snippet_needs_exactly_one_source(self):
        self.assertEqual(prove_red.read_snippet("a", None, "old"), "a")
        self.assertEqual(prove_red.read_snippet("", None, "new"), "")  # '' deletes, and is a valid value
        for value, file_value in (("a", "f.txt"), (None, None)):
            with self.assertRaises(RuntimeError):
                prove_red.read_snippet(value, file_value, "old")

    def test_json_payload_skips_leading_text(self):
        self.assertEqual(prove_red.json_payload('warning: x\n{"errors": 0}'), {"errors": 0})
        self.assertEqual(prove_red.json_payload("no json here"), {})

    def test_load_batch_rejects_malformed_entries(self):
        batch = Path(self.directory.name) / "batch.json"
        for content in ([], [{"file": "a", "old": "b", "new": "c"}],
                        [{"file": "a", "old": "b", "new": "c", "suite": "s", "expect": 3}], {"file": "a"}):
            batch.write_text(json.dumps(content), encoding="utf-8")
            with self.assertRaises(RuntimeError, msg=content):
                prove_red.load_batch(batch)

    def test_a_run_without_a_verdict_raises(self):
        for messages in (["[PASS] B1"], ["X: NO SCENARIOS RAN — nothing was validated.", *GREEN],
                         ["⚠️ STALE-CODE WARNING: validation may be running against out-of-date compiled code.",
                          *GREEN]):
            with contextlib.redirect_stdout(io.StringIO()), self.assertRaises(RuntimeError, msg=messages):
                prove_red.suite_verdict(messages, "run")

    def test_the_summary_line_decides_red(self):
        cases = [(VALIDATE_ALL_RED, True), (VALIDATE_ALL_GREEN, False), (RED, True), (GREEN, False),
                 (["[FAIL] B7 inside a known-bug scenario", *GREEN], False)]
        for messages, red in cases:
            with contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(prove_red.suite_verdict(messages, "run")[0], red, messages)

    def test_a_validate_all_run_where_a_suite_ran_nothing_has_no_verdict(self):
        with contextlib.redirect_stdout(io.StringIO()), self.assertRaises(RuntimeError):
            prove_red.suite_verdict(["VALIDATE ALL: no baseline failed, but a suite ran nothing — treat as "
                                     "suspicious. (3m total)"], "run")

    def test_evidence_includes_the_failed_baselines_list(self):
        messages = ["1 OF 9 X BASELINE TESTS FAILED — REGRESSION.", "Failed baselines (1):\n  • B7 broke (2 ms)"]
        with contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(prove_red.suite_verdict(messages, "run")[1], ["• B7 broke (2 ms)"])


class RunSuiteTests(unittest.TestCase):
    """run_suite against a stubbed CLI: the wait slices and the dropped-entry check."""

    def run_suite(self, max_wait, waits_still_running, before, after):
        self.timeouts = timeouts = []
        consoles = iter([before, after])
        clock = [0.0]

        def unity(*args, timeout=600):
            if args[:2] == ("job", "wait"):
                seconds = int(args[args.index("--timeout") + 1])
                timeouts.append(seconds)
                clock[0] += seconds
                still_running = len(timeouts) <= waits_still_running
                return mock.Mock(returncode=prove_red.STILL_RUNNING_EXIT_CODE if still_running else 0, stdout="")
            return mock.Mock(returncode=0, stdout='{"jobId": "abc123"}', stderr="")

        with mock.patch.object(prove_red, "unity", unity), \
                mock.patch.object(prove_red, "console_entries", lambda: next(consoles)), \
                mock.patch.object(prove_red.time, "monotonic", lambda: clock[0]):
            return prove_red.run_suite("Menu/Suite", max_wait), timeouts

    def test_a_suite_past_max_wait_times_out_after_a_cut_last_slice(self):
        with self.assertRaises(prove_red.SuiteTimedOut):
            self.run_suite(100, waits_still_running=5, before=[(10, "old")], after=[])
        self.assertEqual(self.timeouts, [90, 10])

    def test_a_suite_that_finishes_in_the_last_slice_returns_its_messages(self):
        messages, timeouts = self.run_suite(100, waits_still_running=1, before=[(10, "old")], after=[(11, "x")])
        self.assertEqual((messages, timeouts), (["x"], [90, 10]))

    def test_a_gap_after_the_last_seen_entry_marks_the_run_dropped(self):
        messages, _ = self.run_suite(100, 0, before=[(10, "old")], after=[(15, "late"), (16, "summary")])
        self.assertEqual(messages, [prove_red.DROPPED_MARKER, "late", "summary"])
        messages, _ = self.run_suite(100, 0, before=[(10, "old")], after=[(10, "old"), (11, "a"), (12, "b")])
        self.assertEqual(messages, ["a", "b"])

    def test_the_suite_wait_leaves_room_for_the_restore_compile(self):
        with mock.patch.object(prove_red.time, "monotonic", lambda: 0.0):
            budget = prove_red.Budget(300, max_wait=600)
            budget.longest_compile = 40
            self.assertEqual(budget.suite_wait(), 260)


class EditorStub:
    """Stands in for the Editor: compiles and runs suites against the files on disk, per test."""

    def __init__(self, root):
        self.root = root
        self.targets = {}  # path relative to the root -> original bytes
        self.compile_errors = []  # popped per recompile; 0 once empty
        self.mutated_messages = RED
        self.baseline_messages = GREEN
        self.suite_runs = []  # (suite, mutated?)
        self.compiled = []  # the targets' bytes at each recompile
        self.on_suite = None

    def add(self, name, content=ORIGINAL):
        path = self.root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(content)
        self.targets[name] = content

    def mutated(self):
        return any((self.root / name).read_bytes() != content for name, content in self.targets.items())

    def recompile(self):
        self.compiled.append({name: (self.root / name).read_bytes() for name in self.targets})
        return self.compile_errors.pop(0) if self.compile_errors else 0

    def run_suite(self, suite, _max_wait):
        mutated = self.mutated()
        self.suite_runs.append((suite, mutated))
        if self.on_suite is not None:
            self.on_suite(mutated)
        return list(self.mutated_messages if mutated else self.baseline_messages)


class BatchTestCase(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.root = Path(self.directory.name)
        self.backups = self.root / "backup"
        self.editor = EditorStub(self.root)
        self.editor.add("Assets/Target.cs")

    def tearDown(self):
        self.directory.cleanup()

    def run_argv(self, *argv, statuses=("", "")):
        statuses = iter(statuses)
        out = io.StringIO()
        with mock.patch.object(prove_red, "REPO_ROOT", self.root), \
                mock.patch.object(prove_red, "BACKUP_DIR", self.backups), \
                mock.patch.object(prove_red, "recompile", self.editor.recompile), \
                mock.patch.object(prove_red, "run_suite", self.editor.run_suite), \
                mock.patch.object(prove_red, "tree_status", lambda: next(statuses)), \
                mock.patch("sys.argv", ["prove_red.py", *argv]), contextlib.redirect_stdout(out):
            code = prove_red.main()
        return code, out.getvalue()

    def run_batch(self, entries, *extra, **kwargs):
        batch = self.root / "batch.json"
        batch.write_text(json.dumps(entries), encoding="utf-8")
        return self.run_argv("--batch", str(batch), *extra, **kwargs)

    def assert_restored(self, out):
        for name, content in self.editor.targets.items():
            self.assertEqual(hashlib.sha256((self.root / name).read_bytes()).hexdigest(),
                             hashlib.sha256(content).hexdigest(), out)
        self.assertIn("checksum IDENTICAL", out)

    @staticmethod
    def entry(old="int value = 1;", file="Assets/Target.cs", suite=SUITE, **extra):
        return {"file": file, "old": old, "new": "int value = 2;", "suite": suite, **extra}


class RestoreTests(BatchTestCase):
    """main() with the Editor stubbed: the target must come back byte for byte, whatever happens."""

    def run_main(self, old, expect=None):
        argv = ["--file", "Assets/Target.cs", "--old", old, "--new", "int value = 2;", "--suite", SUITE]
        return self.run_argv(*argv, *(["--expect", expect] if expect else []))

    def test_red_run_exits_0_and_restores(self):
        code, out = self.run_main("int value = 1;")
        self.assertEqual(code, 0, out)
        self.assertEqual(self.editor.suite_runs, [(SUITE, False), (SUITE, True)])  # baseline, then the mutation
        self.assertIn(b"value = 2", self.editor.compiled[1]["Assets/Target.cs"])
        self.assertEqual(self.editor.compiled[-1]["Assets/Target.cs"], ORIGINAL)  # final compile is the restore
        self.assert_restored(out)

    def test_survived_run_exits_1_and_restores(self):
        self.editor.mutated_messages = GREEN
        code, out = self.run_main("int value = 1;")
        self.assertEqual(code, 1, out)
        self.assertIn("SURVIVED", out)
        self.assert_restored(out)

    def test_expect_narrows_what_counts_as_red(self):
        self.editor.mutated_messages = ["[FAIL] B3 unrelated", "1 OF 9 X BASELINE TESTS FAILED"]
        code, out = self.run_main("int value = 1;", expect=r"\bB7\b")
        self.assertEqual(code, 1, out)
        self.assertIn("none matched --expect", out)
        self.assert_restored(out)

    def test_a_red_summary_without_fail_lines_is_red(self):
        self.editor.mutated_messages = VALIDATE_ALL_RED
        code, out = self.run_main("int value = 1;")
        self.assertEqual(code, 0, out)
        self.assert_restored(out)

    def test_fail_lines_under_a_green_summary_survive(self):
        self.editor.mutated_messages = ["[FAIL] B7 inside a known-bug scenario", *GREEN]
        code, out = self.run_main("int value = 1;")
        self.assertEqual(code, 1, out)
        self.assertIn("summary does not count", out)

    def test_expect_on_a_red_run_with_dropped_entries_proves_nothing(self):
        self.editor.mutated_messages = [prove_red.DROPPED_MARKER, *VALIDATE_ALL_RED]
        code, out = self.run_main("int value = 1;", expect=r"\bB7\b")
        self.assertEqual(code, 2, out)
        self.assertIn("--expect cannot be checked", out)
        self.assert_restored(out)

    def test_mutation_that_does_not_compile_exits_2_and_restores(self):
        self.editor.compile_errors = [0, 3, 0]  # the start-of-run compile, the mutation, the restore
        code, out = self.run_main("int value = 1;")
        self.assertEqual(code, 2, out)
        self.assertIn("does not compile", out)
        self.assert_restored(out)

    def test_old_text_not_found_exits_2_and_leaves_the_file(self):
        code, out = self.run_main("absent text")
        self.assertEqual(code, 2, out)
        self.assert_restored(out)

    def test_restored_code_that_fails_to_compile_exits_2(self):
        self.editor.compile_errors = [0, 0, 1]
        code, out = self.run_main("int value = 1;")
        self.assertEqual(code, 2, out)
        self.assertIn("STOPPED: the restored code does not compile", out)

    def test_a_mutated_run_without_a_verdict_exits_2(self):
        self.editor.mutated_messages = ["[FAIL] B7 broke"]  # the summary line never arrived
        code, out = self.run_main("int value = 1;")
        self.assertEqual(code, 2, out)
        self.assertIn("no suite summary line", out)
        self.assert_restored(out)

    def test_backup_is_deleted_after_a_good_restore(self):
        code, out = self.run_main("int value = 1;")
        self.assertEqual(code, 0, out)
        self.assertEqual(list(self.backups.glob("*.bak")), [], out)

    def test_an_unexpected_error_exits_2_not_1(self):
        def explode(*_):
            if not self.editor.mutated():
                return list(GREEN)
            raise KeyError("boom")

        with mock.patch.object(self.editor, "run_suite", explode):
            code, out = self.run_main("int value = 1;")
        self.assertEqual(code, 2, out)
        self.assertIn("KeyError", out)
        self.assert_restored(out)


class BatchTests(BatchTestCase):
    def test_a_leftover_backup_stops_before_touching_anything(self):
        self.backups.mkdir()
        (self.backups / "Assets__Target.cs.20261016T120000000000.bak").write_bytes(b"old")
        code, out = self.run_batch([self.entry()])
        self.assertEqual(code, 2, out)
        self.assertIn("STOPPED: PREVIOUS RUN WAS KILLED", out)
        self.assertEqual(self.editor.compiled, [])
        self.assertEqual((self.root / "Assets/Target.cs").read_bytes(), ORIGINAL)

    def test_same_named_files_get_separate_backups(self):
        self.editor.add("Assets/Other/Target.cs", b"int value = 1;\n")
        seen = []
        self.editor.on_suite = lambda mutated: seen.extend(p.name for p in self.backups.glob("*.bak"))
        code, out = self.run_batch([self.entry(), self.entry(file="Assets/Other/Target.cs")])
        self.assertEqual(code, 0, out)
        self.assertTrue(any(name.startswith("Assets__Target.cs.") for name in seen), seen)
        self.assertTrue(any(name.startswith("Assets__Other__Target.cs.") for name in seen), seen)
        self.assert_restored(out)

    def test_summary_lists_every_entry_and_exits_with_the_worst(self):
        self.editor.mutated_messages = GREEN
        code, out = self.run_batch([self.entry(label="survivor"), self.entry(old="absent", label="no match")])
        self.assertEqual(code, 2, out)
        self.assertIn("=== prove-red summary ===", out)
        self.assertRegex(out, r"SURVIVED\s+survivor")
        self.assertRegex(out, r"NO PROOF\s+no match")

    def test_one_baseline_per_distinct_suite(self):
        code, out = self.run_batch([self.entry(), self.entry(), self.entry(suite="Minecraft Clone/Dev/Validate Y")])
        self.assertEqual(code, 0, out)
        baselines = [suite for suite, mutated in self.editor.suite_runs if not mutated]
        self.assertEqual(baselines, [SUITE, "Minecraft Clone/Dev/Validate Y"])

    def test_a_red_baseline_proves_nothing_and_is_not_rerun(self):
        self.editor.baseline_messages = RED
        code, out = self.run_batch([self.entry(), self.entry()])
        self.assertEqual(code, 2, out)
        self.assertIn("baseline is already red", out)
        self.assertEqual(self.editor.suite_runs, [(SUITE, False)])

    def test_a_baseline_with_only_a_red_summary_is_red(self):
        self.editor.baseline_messages = VALIDATE_ALL_RED
        code, out = self.run_batch([self.entry()])
        self.assertEqual(code, 2, out)
        self.assertIn("baseline is already red", out)

    def test_no_baseline_skips_the_unmutated_run(self):
        code, out = self.run_batch([self.entry()], "--no-baseline")
        self.assertEqual(code, 0, out)
        self.assertEqual(self.editor.suite_runs, [(SUITE, True)])

    def test_a_restore_mismatch_stops_the_batch(self):
        def corrupt_backup(mutated):
            if mutated:
                for backup in self.backups.glob("*.bak"):
                    backup.write_bytes(b"not the original")

        self.editor.on_suite = corrupt_backup
        code, out = self.run_batch([self.entry(label="first"), self.entry(label="second")])
        self.assertEqual(code, 2, out)
        self.assertIn("checksum MISMATCH", out)
        self.assertIn("STOPPED:", out)
        self.assertNotIn("[2/2]", out)
        self.assertIn("1 entries not run", out)
        self.assertEqual(len(list(self.backups.glob("*.bak"))), 1, out)  # kept as the way back

    def test_a_suite_that_outlives_its_wait_stops_without_recompiling(self):
        def time_out(suite, _max_wait):
            if self.editor.mutated():
                raise prove_red.SuiteTimedOut("still running")
            return list(GREEN)

        with mock.patch.object(self.editor, "run_suite", time_out):
            code, out = self.run_batch([self.entry(), self.entry()])
        self.assertEqual(code, 2, out)
        self.assertIn("STOPPED: still running", out)
        self.assertEqual(len(self.editor.compiled), 2)  # start + mutation; the busy Editor gets no restore compile
        self.assert_restored(out)

    def test_entries_past_the_budget_are_not_run(self):
        clock = iter(range(0, 10_000, 100))
        with mock.patch.object(prove_red.time, "monotonic", lambda: next(clock)):
            code, out = self.run_batch([self.entry(label="a"), self.entry(label="b"), self.entry(label="c")],
                                       "--budget", "1000")
        self.assertEqual(code, 2, out)
        self.assertIn("STOPPED: time budget", out)
        self.assertRegex(out, r"NOT RUN\s+c")

    def test_a_changed_tree_is_reported(self):
        code, out = self.run_batch([self.entry()], statuses=("", " M Assets/Elsewhere.cs\n"))
        self.assertEqual(code, 0, out)
        self.assertIn("TREE CHANGED", out)
        self.assertIn("Assets/Elsewhere.cs", out)

    def test_an_unchanged_tree_is_not_reported(self):
        code, out = self.run_batch([self.entry()], statuses=(" M a.cs\n", " M a.cs\n"))
        self.assertEqual(code, 0, out)
        self.assertNotIn("TREE CHANGED", out)


if __name__ == "__main__":
    unittest.main()
