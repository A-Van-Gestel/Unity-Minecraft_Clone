"""align_md_tables.py: aligned form, GFM cell splitting, irregular tables, and content safety."""
import os
import tempfile
import unittest

from helpers import run_script
import align_md_tables as aligner

SCRIPT = "align_md_tables.py"


class AlignmentTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.path = os.path.join(self.directory.name, "doc.md")

    def tearDown(self):
        self.directory.cleanup()

    def write(self, text):
        with open(self.path, "w", encoding="utf-8", newline="\n") as handle:
            handle.write(text)

    def read(self):
        with open(self.path, encoding="utf-8", newline="") as handle:
            return handle.read()

    def test_aligns_and_is_idempotent(self):
        self.write("# T\n\n| a | longer header |\n|---|:---:|\n| value | x |\n")
        code, out = run_script(SCRIPT, "--fix", self.path)
        self.assertEqual(code, 0, out)
        self.assertEqual(self.read(), "# T\n\n"
                                      "| a     | longer header |\n"
                                      "|-------|:-------------:|\n"
                                      "| value |       x       |\n")
        self.assertEqual(run_script(SCRIPT, self.path)[0], 0)

    def test_check_mode_reports_without_writing(self):
        original = "| a | b |\n|---|---|\n| long cell | x |\n"
        self.write(original)
        code, out = run_script(SCRIPT, self.path)
        self.assertEqual(code, 1, out)
        self.assertEqual(self.read(), original)

    def test_overfull_row_is_irregular_and_untouched(self):
        original = "| a | b |\n|---|---|\n| 1 | 2 | 3 |\n"
        self.write(original)
        code, out = run_script(SCRIPT, "--fix", self.path)
        self.assertEqual(code, 1, out)
        self.assertIn("IRREGULAR", out)
        self.assertEqual(self.read(), original)

    def test_header_delimiter_mismatch_is_irregular_and_untouched(self):
        original = "| a | b |\n|---|---|---|\n| 1 | 2 | 3 |\n"
        self.write(original)
        code, out = run_script(SCRIPT, "--fix", self.path)
        self.assertEqual(code, 1, out)
        self.assertIn("GFM does not render", out)
        self.assertEqual(self.read(), original)

    def test_fenced_code_is_skipped(self):
        original = "```\n| a | b |\n|---|---|\n| long cell | x |\n```\n"
        self.write(original)
        self.assertEqual(run_script(SCRIPT, "--fix", self.path)[0], 0)
        self.assertEqual(self.read(), original)

    def test_crlf_is_preserved(self):
        with open(self.path, "wb") as handle:
            handle.write(b"| a | b |\r\n|---|---|\r\n| long | x |\r\n")
        run_script(SCRIPT, "--fix", self.path)
        with open(self.path, "rb") as handle:
            self.assertNotIn(b"\n|", handle.read().replace(b"\r\n", b""))


class CellSplittingTests(unittest.TestCase):
    def test_unescaped_pipe_in_code_span_splits_like_gfm(self):
        self.assertEqual(aligner.split_row("| `a|b` | c |"), ["`a", "b`", "c"])

    def test_escaped_pipe_stays_in_its_cell(self):
        self.assertEqual(aligner.split_row(r"| `90° − \|φ\|` | c |"), [r"`90° − \|φ\|`", "c"])

    def test_display_width(self):
        self.assertEqual(aligner.display_width("abc"), 3)
        self.assertEqual(aligner.display_width("界"), 2)          # East Asian wide
        self.assertEqual(aligner.display_width("⚠️"), 2)          # emoji presentation selector
        self.assertEqual(aligner.display_width("é"), 1)     # combining acute accent


if __name__ == "__main__":
    unittest.main()
