"""rename_tokens.py: dry run vs --apply, word-boundary matching, longest-first, exclusions, map validation."""
import os
import tempfile
import unittest

from helpers import run_script

SCRIPT = "rename_tokens.py"


class RenameTokenTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.root = self.directory.name
        self.map_path = os.path.join(self.root, "map.tsv")

    def tearDown(self):
        self.directory.cleanup()

    def write(self, relative, text, newline="\n"):
        full = os.path.join(self.root, *relative.split("/"))
        os.makedirs(os.path.dirname(full), exist_ok=True)
        with open(full, "w", encoding="utf-8", newline=newline) as handle:
            handle.write(text)
        return full

    def read_bytes(self, relative):
        with open(os.path.join(self.root, *relative.split("/")), "rb") as handle:
            return handle.read()

    def rename(self, mapping, *args):
        with open(self.map_path, "w", encoding="utf-8", newline="\n") as handle:
            handle.write(mapping)
        return run_script(SCRIPT, self.map_path, "--root", self.root, "--paths", "src", "Documentation", *args)

    def test_dry_run_reports_without_writing(self):
        self.write("src/A.cs", "int Sun = 1;\n")
        code, out = self.rename("Sun\tSky\n")
        self.assertEqual(code, 0, out)
        self.assertIn("DRY RUN", out)
        self.assertIn("1 replacement(s) across 1 file(s)", out)
        self.assertEqual(self.read_bytes("src/A.cs"), b"int Sun = 1;\n")

    def test_only_whole_tokens_are_renamed(self):
        self.write("src/A.cs", "Sun SunLight _Sun Sun_x x.Sun Sun2 Sun\n")
        code, out = self.rename("Sun\tSky\n", "--apply")
        self.assertEqual(code, 0, out)
        self.assertEqual(self.read_bytes("src/A.cs"), b"Sky SunLight _Sun Sun_x x.Sky Sun2 Sky\n")

    def test_longer_token_wins_and_dotted_tokens_match(self):
        self.write("src/A.cs", "LightChannel.Sun and Sun\n")
        self.rename("Sun\tSky\nLightChannel.Sun\tLightChannel.Skylight\n", "--apply")
        self.assertEqual(self.read_bytes("src/A.cs"), b"LightChannel.Skylight and Sky\n")

    def test_crlf_is_preserved(self):
        self.write("src/A.cs", "Sun\r\nSun\r\n", newline="")
        self.rename("Sun\tSky\n", "--apply")
        self.assertEqual(self.read_bytes("src/A.cs"), b"Sky\r\nSky\r\n")

    def test_historical_records_are_excluded_unless_asked(self):
        self.write("Documentation/Bugs/_FIXED_BUGS.md", "Sun\n")
        self.write("Documentation/Archived/OLD.md", "Sun\n")
        self.write("Documentation/LIVE.md", "Sun\n")
        self.rename("Sun\tSky\n", "--apply")
        self.assertEqual(self.read_bytes("Documentation/Bugs/_FIXED_BUGS.md"), b"Sun\n")
        self.assertEqual(self.read_bytes("Documentation/Archived/OLD.md"), b"Sun\n")
        self.assertEqual(self.read_bytes("Documentation/LIVE.md"), b"Sky\n")

        self.rename("Sun\tSky\n", "--apply", "--include-historical")
        self.assertEqual(self.read_bytes("Documentation/Bugs/_FIXED_BUGS.md"), b"Sky\n")

    def test_other_extensions_and_non_utf8_files_are_left_alone(self):
        self.write("src/notes.txt", "Sun\n")
        with open(os.path.join(self.root, "src", "Bad.cs"), "wb") as handle:
            handle.write(b"Sun \xff\n")
        code, out = self.rename("Sun\tSky\n", "--apply")
        self.assertIn("SKIP (not utf-8): src/Bad.cs", out)
        self.assertEqual(self.read_bytes("src/notes.txt"), b"Sun\n")
        self.assertEqual(code, 1, out)  # --apply with zero replacements is a failure

    def test_bad_maps_are_rejected(self):
        self.write("src/A.cs", "Sun\n")
        for mapping, message in (("Sun Sky\n", "expected 'old<TAB>new'"),
                                 ("Sun\tSky\nSun\tStar\n", "duplicate source token"),
                                 ("# only a comment\n", "no mappings found")):
            code, out = self.rename(mapping, "--apply")
            self.assertNotEqual(code, 0, out)
            self.assertIn(message, out)
        self.assertEqual(self.read_bytes("src/A.cs"), b"Sun\n")


if __name__ == "__main__":
    unittest.main()
