"""check_doc_links.py: relative resolution, percent-decoding, exact-case matching, and the skip rules."""
import os
import tempfile
import unittest

from helpers import run_script

SCRIPT = "check_doc_links.py"


class DocLinkTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.root = self.directory.name
        self.write("Design/TARGET.md", "# Target\n")
        self.write("World Generation/CAVES.md", "# Caves\n")

    def tearDown(self):
        self.directory.cleanup()

    def write(self, relative, text):
        full = os.path.join(self.root, *relative.split("/"))
        os.makedirs(os.path.dirname(full), exist_ok=True)
        with open(full, "w", encoding="utf-8", newline="\n") as handle:
            handle.write(text)
        return full

    def check(self, text):
        source = self.write("Architecture/SOURCE.md", text)
        return run_script(SCRIPT, source)

    def test_relative_link_resolves_from_the_linking_file(self):
        code, out = self.check("See [t](../Design/TARGET.md).\n")
        self.assertEqual(code, 0, out)
        self.assertIn("found 1 relative markdown links", out)

    def test_missing_target_is_reported_with_its_line(self):
        code, out = self.check("\n[gone](../Design/GONE.md)\n")
        self.assertEqual(code, 1, out)
        self.assertIn("SOURCE.md:2  -> ../Design/GONE.md", out)

    def test_percent_encoded_space_is_decoded(self):
        code, out = self.check("[c](../World%20Generation/CAVES.md)\n")
        self.assertEqual(code, 0, out)

    def test_raw_space_is_reported_even_when_the_file_exists(self):
        # GFM renders no link for a target with a raw space, so resolving it on disk would be a false green.
        code, out = self.check("[c](../World Generation/CAVES.md)\n")
        self.assertEqual(code, 1, out)
        self.assertIn("raw space", out)

    def test_fragment_is_stripped_and_same_file_anchor_skipped(self):
        code, out = self.check("[a](../Design/TARGET.md#section) [b](#local)\n")
        self.assertEqual(code, 0, out)
        self.assertIn("found 1 relative markdown links", out)

    def test_case_drift_is_a_broken_link(self):
        # Passes os.path.exists on Windows, breaks on GitHub and any case-sensitive checkout.
        code, out = self.check("[t](../design/TARGET.md) [u](../Design/target.md)\n")
        self.assertEqual(code, 1, out)
        self.assertIn("2 unresolved link target(s)", out)

    def test_urls_and_placeholders_are_not_links(self):
        code, out = self.check("[w](https://example.com/X.md) [p](../Architecture/<DOC>.md) [g](*.md)\n")
        self.assertEqual(code, 0, out)
        self.assertIn("found 0 relative markdown links (2 ignored", out)

    def test_missing_path_exits_2(self):
        code, out = run_script(SCRIPT, os.path.join(self.root, "absent.md"))
        self.assertEqual(code, 2, out)


if __name__ == "__main__":
    unittest.main()
