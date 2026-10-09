"""check_doc_refs.py: `@Documentation/...` extraction (spaces, punctuation, decoration), ignore rules, exit codes.

The script resolves references against its own repo root, so these tests import it and point `REPO_ROOT`
at a throwaway tree instead of running it as a subprocess.
"""
import contextlib
import io
import os
import tempfile
import unittest
from unittest import mock

import helpers  # noqa: F401 — puts Tools/Python on sys.path for the import below
import check_doc_refs as refs


class DocRefTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.root = self.directory.name
        self.write("Documentation/Guides/STYLE.md", "# Style\n")
        self.write("Documentation/Architecture/World Generation/CAVES.md", "# Caves\n")

    def tearDown(self):
        self.directory.cleanup()

    def write(self, relative, text):
        full = os.path.join(self.root, *relative.split("/"))
        os.makedirs(os.path.dirname(full), exist_ok=True)
        with open(full, "w", encoding="utf-8", newline="\n") as handle:
            handle.write(text)
        return full

    def run_main(self, *args):
        out = io.StringIO()
        with mock.patch.object(refs, "REPO_ROOT", self.root), \
                mock.patch("sys.argv", ["check_doc_refs.py", *args]), \
                contextlib.redirect_stdout(out), contextlib.redirect_stderr(out):
            code = refs.main()
        return code, out.getvalue()

    def check(self, text):
        self.write("AGENTS.md", text)
        return self.run_main("AGENTS.md")

    def test_resolving_reference_passes_and_is_counted(self):
        code, out = self.check("Read `@Documentation/Guides/STYLE.md` first.\n")
        self.assertEqual(code, 0, out)
        self.assertIn("found 1 references", out)

    def test_unresolved_reference_names_its_source_line(self):
        code, out = self.check("\nSee @Documentation/Guides/GONE.md.\n")
        self.assertEqual(code, 1, out)
        self.assertIn("@Documentation/Guides/GONE.md", out)
        self.assertIn("referenced from AGENTS.md:2", out)

    def test_folder_with_a_space_is_read_up_to_md(self):
        code, out = self.check("Per @Documentation/Architecture/World Generation/CAVES.md, caves carve.\n")
        self.assertEqual(code, 0, out)

    def test_trailing_punctuation_and_decoration_are_dropped(self):
        code, out = self.check("(@Documentation/Guides/STYLE.md), and `@Documentation/Guides/STYLE.md`.\n")
        self.assertEqual(code, 0, out)
        self.assertIn("found 2 references (1 unique", out)

    def test_directory_reference_resolves_through_the_fallback(self):
        code, out = self.check("Everything under @Documentation/Guides, then stop.\n")
        self.assertEqual(code, 0, out)

    def test_globs_and_placeholders_are_ignored_not_failed(self):
        code, out = self.check("@Documentation/Bugs/*.md and @Documentation/Bugs/{FILE}\n")
        self.assertEqual(code, 0, out)
        self.assertIn("2 ignored as glob/placeholder", out)

    def test_missing_scan_path_exits_2(self):
        code, out = self.run_main("NOPE.md")
        self.assertEqual(code, 2, out)


if __name__ == "__main__":
    unittest.main()
