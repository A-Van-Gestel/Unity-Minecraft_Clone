"""stage_hunks.py: lists and stages chosen hunks, whatever the user's diff output settings are."""
import unittest

from helpers import TempRepo, numbered_lines

SCRIPT = "stage_hunks.py"

# Each of these changes the diff text a parser sees; the script must pin all of them.
HOSTILE_CONFIG = (("color.diff", "always"), ("color.ui", "always"), ("diff.mnemonicPrefix", "true"),
                  ("diff.noprefix", "true"), ("diff.relative", "true"))


class StageHunksTests(unittest.TestCase):
    def setUp(self):
        self.repo = TempRepo()
        self.repo.write("sub/f.txt", numbered_lines(20))
        self.repo.commit("base")
        self.repo.write("sub/f.txt", numbered_lines(20, {1: "ONE", 20: "TWENTY"}))

    def tearDown(self):
        self.repo.close()

    def staged_diff(self):
        return self.repo.git("diff", "--cached", "--no-color", "-U0")

    def test_lists_two_hunks(self):
        code, out = self.repo.run(SCRIPT, "sub/f.txt", "--list")
        self.assertEqual(code, 0, out)
        self.assertIn("[1]", out)
        self.assertIn("[2]", out)

    def test_stages_only_the_chosen_hunk(self):
        code, out = self.repo.run(SCRIPT, "sub/f.txt", "--hunks", "2")
        self.assertEqual(code, 0, out)
        staged = self.staged_diff()
        self.assertIn("+TWENTY", staged)
        self.assertNotIn("+ONE", staged)

    def test_hostile_diff_settings_do_not_break_parsing(self):
        for key, value in HOSTILE_CONFIG:
            self.repo.git("config", key, value)
        code, out = self.repo.run(SCRIPT, "f.txt", "--hunks", "1", cwd=self.repo.path("sub"))
        self.assertEqual(code, 0, out)
        self.assertIn("+ONE", self.staged_diff())

    def test_unknown_hunk_number_is_an_error(self):
        code, _ = self.repo.run(SCRIPT, "sub/f.txt", "--hunks", "9")
        self.assertNotEqual(code, 0)
        self.assertEqual(self.staged_diff(), "")


if __name__ == "__main__":
    unittest.main()
