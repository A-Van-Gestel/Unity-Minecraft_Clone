"""check_markdown_breaks.py: the two defects, their repairs, the exclusions, and line-ending safety."""
import os
import tempfile
import unittest

from helpers import run_script

SCRIPT = "check_markdown_breaks.py"


class MarkdownBreakTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.root = self.directory.name
        self.path = os.path.join(self.root, "doc.md")

    def tearDown(self):
        self.directory.cleanup()

    def write(self, text, newline="\n"):
        with open(self.path, "w", encoding="utf-8", newline=newline) as handle:
            handle.write(text)

    def read_bytes(self):
        with open(self.path, "rb") as handle:
            return handle.read()

    def test_stacked_field_without_break_is_reported_and_fixed(self):
        self.write("**Version:** 1.0\n**Status:** Open.\n")
        code, out = run_script(SCRIPT, self.root)
        self.assertEqual(code, 1, out)
        self.assertIn("D1=1 D2=0", out)

        self.assertEqual(run_script(SCRIPT, "--fix", self.root)[0], 0)
        self.assertEqual(self.read_bytes(), b"**Version:** 1.0  \n**Status:** Open.\n")
        self.assertEqual(run_script(SCRIPT, self.root)[0], 0)  # idempotent: the fix leaves nothing to report

    def test_unquoted_field_after_blockquote_gets_a_blank_line(self):
        self.write("> A quoted note.\n**Status:** Open.\n")
        code, out = run_script(SCRIPT, self.root)
        self.assertEqual(code, 1, out)
        self.assertIn("D1=0 D2=1", out)

        run_script(SCRIPT, "--fix", self.root)
        self.assertEqual(self.read_bytes(), b"> A quoted note.\n\n**Status:** Open.\n")

    def test_only_the_line_before_a_field_is_touched(self):
        # Wrapped prose is supposed to rejoin; only the break before the next field matters.
        self.write("**Status:** a value that\nwraps onto a second line\n**Next:** x\n")
        run_script(SCRIPT, "--fix", self.root)
        self.assertEqual(self.read_bytes(),
                         b"**Status:** a value that\nwraps onto a second line  \n**Next:** x\n")

    def test_correct_and_structural_contexts_are_not_reported(self):
        self.write("**Version:** 1.0  \n**Date:** x\\\n**Status:** y\n\n"
                   "| a | b |\n**Label:** after a table\n\n"
                   "- item\n**Label:** after a list\n\n"
                   "# Heading\n**Label:** after a heading\n\n"
                   "> [!NOTE]\n> **Label:** inside an alert\n\n"
                   "<!-- comment -->\n**Label:** after a comment\n")
        code, out = run_script(SCRIPT, self.root)
        self.assertEqual(code, 0, out)

    def test_fenced_code_is_skipped_unless_requested(self):
        self.write("```\n**Version:** 1.0\n**Status:** Open.\n```\n")
        self.assertEqual(run_script(SCRIPT, self.root)[0], 0)
        self.assertEqual(run_script(SCRIPT, "--include-fenced", self.root)[0], 1)

    def test_crlf_files_keep_crlf_after_a_fix(self):
        self.write("**Version:** 1.0\r\n**Status:** Open.\r\n", newline="")
        run_script(SCRIPT, "--fix", self.root)
        self.assertEqual(self.read_bytes(), b"**Version:** 1.0  \r\n**Status:** Open.\r\n")

    def test_missing_path_exits_2(self):
        code, out = run_script(SCRIPT, os.path.join(self.root, "absent"))
        self.assertEqual(code, 2, out)


if __name__ == "__main__":
    unittest.main()
