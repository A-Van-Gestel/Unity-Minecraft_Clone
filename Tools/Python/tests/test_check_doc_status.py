"""check_doc_status.py: classifier tier order, the three Status shapes, row extraction, and index findings.

The script reads `Documentation/Design/OPEN_WORK_INDEX.md` under its own repo root, so these tests import it
and point `REPO_ROOT` at a throwaway tree.
"""
import contextlib
import io
import os
import tempfile
import unittest
from unittest import mock

import helpers  # noqa: F401 — puts Tools/Python on sys.path for the import below
import check_doc_status as status

OPEN_DOC = "**Version:** 1.0  \n**Status:** Open backlog — two items left.  \n**Date:** 2026-10-09\n"
CLOSED_DOC = "**Status:** Implemented (Stable).\n"


def index(open_rows, closed_rows):
    def table(rows):
        return "| Area | Doc |\n|------|-----|\n" + "".join(f"| x | [d]({row}) |\n" for row in rows)
    return ("# Index\n\n## 2. Where the open work lives\n\n" + table(open_rows) +
            "\n## 3. Closed arcs retained\n\nRule: see [the rule](RULE.md).\n\n" + table(closed_rows) +
            "\n## 4. Maintenance\n")


class ClassifierTests(unittest.TestCase):
    def test_open_tier_is_tested_before_closed(self):
        # "Partially implemented" contains "implemented"; the other order calls it closed.
        self.assertEqual(status.classify("Partially implemented — phase 2 next.")[0], "OPEN")

    def test_strong_closed_symbol_beats_open_prose(self):
        self.assertEqual(status.classify("⛔ Superseded; its open backlog moved elsewhere.")[0], "CLOSED")

    def test_unclassifiable_and_absent_status_are_distinct(self):
        self.assertEqual(status.classify("Some prose.")[0], "UNKNOWN")
        self.assertEqual(status.classify(None)[0], "NO-STATUS")


class StatusTextTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.path = os.path.join(self.directory.name, "doc.md")

    def tearDown(self):
        self.directory.cleanup()

    def text_of(self, body):
        with open(self.path, "w", encoding="utf-8", newline="\n") as handle:
            handle.write(body)
        return status.status_text(self.path)

    def test_own_line_field_wraps_until_the_next_label(self):
        self.assertEqual(self.text_of("**Status:** first line\nsecond line\n**Date:** x\n"),
                         "first line second line")

    def test_blockquoted_field(self):
        self.assertEqual(self.text_of("> **Status:** quoted\n> and wrapped\n\nbody\n"), "quoted and wrapped")

    def test_inline_field_beside_other_fields(self):
        self.assertEqual(self.text_of("**Version:** 1.0 **Status:** inline **Date:** x\n"), "inline")

    def test_no_status_field(self):
        self.assertIsNone(self.text_of("# Title\n\nNo header fields.\n"))


class IndexTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.root = self.directory.name
        self.write("Design/OPEN.md", OPEN_DOC)
        self.write("Design/CLOSED.md", CLOSED_DOC)
        self.write("Design/RULE.md", OPEN_DOC)  # linked from §3 prose only, so it must not count as a row
        self.write("Architecture/Testing Framework/HARNESS.md", OPEN_DOC)

    def tearDown(self):
        self.directory.cleanup()

    def write(self, relative, text):
        full = os.path.join(self.root, "Documentation", *relative.split("/"))
        os.makedirs(os.path.dirname(full), exist_ok=True)
        with open(full, "w", encoding="utf-8", newline="\n") as handle:
            handle.write(text)

    def run_index(self, text):
        self.write("Design/OPEN_WORK_INDEX.md", text)
        out = io.StringIO()
        with mock.patch.object(status, "REPO_ROOT", self.root), \
                mock.patch("sys.argv", ["check_doc_status.py"]), \
                contextlib.redirect_stdout(out), contextlib.redirect_stderr(out):
            code = status.main()
        return code, out.getvalue()

    def test_agreeing_index_passes_and_prose_links_are_not_rows(self):
        code, out = self.run_index(index(["OPEN.md", "../Architecture/Testing%20Framework/HARNESS.md"],
                                         ["CLOSED.md"]))
        self.assertEqual(code, 0, out)
        self.assertIn("Checked 3 index rows (2 in §2 open, 1 in §3 closed)", out)

    def test_row_on_the_wrong_side_is_a_finding(self):
        code, out = self.run_index(index(["OPEN.md", "CLOSED.md"], ["RULE.md"]))
        self.assertEqual(code, 1, out)
        self.assertIn("index section says OPEN, the document reads CLOSED", out)

    def test_doc_in_both_sections_is_reported(self):
        code, out = self.run_index(index(["OPEN.md"], ["OPEN.md", "CLOSED.md"]))
        self.assertEqual(code, 1, out)
        self.assertIn("listed in §2 AND §3", out)

    def test_missing_target_is_reported(self):
        code, out = self.run_index(index(["OPEN.md", "GONE.md"], ["CLOSED.md"]))
        self.assertEqual(code, 1, out)
        self.assertIn("the index links a file that does not exist", out)

    def test_dashes_in_a_text_cell_do_not_drop_the_row(self):
        text = index(["OPEN.md"], ["CLOSED.md"]).replace("| x | [d](CLOSED.md) |", "| a --- b | [d](CLOSED.md) |")
        code, out = self.run_index(text)
        self.assertEqual(code, 0, out)
        self.assertIn("1 in §3 closed", out)

    def test_moved_heading_or_empty_section_exits_2(self):
        self.assertEqual(self.run_index(index(["OPEN.md"], ["CLOSED.md"]).replace("## 4.", "## Four"))[0], 2)
        self.assertEqual(self.run_index(index(["OPEN.md"], []))[0], 2)


if __name__ == "__main__":
    unittest.main()
