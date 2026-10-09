"""prove_red.py: argument handling, the single-match mutation, and the byte-for-byte restore on every path.

The Editor half (recompile, the suite run) needs a live Unity Editor, so it is stubbed here; what is tested is
everything that decides whether the user's file comes back intact and what the exit code says.
"""
import contextlib
import hashlib
import io
import tempfile
import unittest
from pathlib import Path
from unittest import mock

import helpers  # noqa: F401 — puts Tools/Python on sys.path for the import below
import prove_red

ORIGINAL = b"line one\r\nint value = 1;\r\nline three\r\n"


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


class RestoreTests(unittest.TestCase):
    """main() with the Editor stubbed: the target must come back byte for byte, whatever happens."""

    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.root = Path(self.directory.name)
        self.target = self.root / "Assets" / "Target.cs"
        self.target.parent.mkdir(parents=True)
        self.target.write_bytes(ORIGINAL)
        self.recompiles = []

    def tearDown(self):
        self.directory.cleanup()

    def run_main(self, old, suite_messages=(), compile_errors=(0, 0), expect=None):
        errors = iter(compile_errors)

        def recompile():
            self.recompiles.append(self.target.read_bytes())
            return next(errors)

        argv = ["prove_red.py", "--file", "Assets/Target.cs", "--old", old, "--new", "int value = 2;",
                "--suite", "Minecraft Clone/Dev/Validate X"]
        if expect:
            argv += ["--expect", expect]
        out = io.StringIO()
        with mock.patch.object(prove_red, "REPO_ROOT", self.root), \
                mock.patch.object(prove_red, "BACKUP_DIR", self.root / "backup"), \
                mock.patch.object(prove_red, "recompile", recompile), \
                mock.patch.object(prove_red, "run_suite", lambda *_: list(suite_messages)), \
                mock.patch("sys.argv", argv), contextlib.redirect_stdout(out):
            code = prove_red.main()
        return code, out.getvalue()

    def assert_restored(self, out):
        self.assertEqual(hashlib.sha256(self.target.read_bytes()).hexdigest(),
                         hashlib.sha256(ORIGINAL).hexdigest(), out)
        self.assertIn("checksum IDENTICAL", out)

    def test_red_run_exits_0_and_restores(self):
        code, out = self.run_main("int value = 1;", ["  [FAIL] B7 broke", "1 OF 9 BASELINE TESTS FAILED"])
        self.assertEqual(code, 0, out)
        self.assertIn(b"value = 2", self.recompiles[0])  # the suite ran against the mutation
        self.assertEqual(self.recompiles[-1], ORIGINAL)  # and the final compile against the restore
        self.assert_restored(out)

    def test_survived_run_exits_1_and_restores(self):
        code, out = self.run_main("int value = 1;", ["ALL 9 BASELINE TESTS PASSED"])
        self.assertEqual(code, 1, out)
        self.assertIn("SURVIVED", out)
        self.assert_restored(out)

    def test_expect_narrows_what_counts_as_red(self):
        code, out = self.run_main("int value = 1;", ["  [FAIL] B3 unrelated"], expect=r"\bB7\b")
        self.assertEqual(code, 1, out)
        self.assert_restored(out)

    def test_mutation_that_does_not_compile_exits_2_and_restores(self):
        code, out = self.run_main("int value = 1;", compile_errors=(3, 0))
        self.assertEqual(code, 2, out)
        self.assertIn("does not compile", out)
        self.assert_restored(out)

    def test_old_text_not_found_exits_2_and_leaves_the_file(self):
        code, out = self.run_main("absent text")
        self.assertEqual(code, 2, out)
        self.assert_restored(out)

    def test_restored_code_that_fails_to_compile_exits_2(self):
        code, out = self.run_main("int value = 1;", ["  [FAIL] B7"], compile_errors=(0, 1))
        self.assertEqual(code, 2, out)
        self.assertIn("restored code does not compile", out)


if __name__ == "__main__":
    unittest.main()
