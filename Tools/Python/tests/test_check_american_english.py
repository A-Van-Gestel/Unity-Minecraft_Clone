"""check_american_english.py: what counts as British, Unity's exempt names, and diff-mode robustness."""
import unittest

from helpers import TempRepo
import check_american_english as checker

SCRIPT = "check_american_english.py"


def flagged(line):
    return [british for british, _ in checker.scan_line(line)]


class ScanLineTests(unittest.TestCase):
    def test_flags_british_forms(self):
        for line, word in (("the colour of light", "colour"), ("center vs centre", "centre"),
                           ("initialise the pool", "initialise"), ("cancelled job", "cancelled"),
                           ("a grey sky", "grey"), ("NeighbourCount", "Neighbour")):
            self.assertIn(word, flagged(line), line)

    def test_american_forms_pass(self):
        for line in ("the color of light", "centered text", "initialize", "canceled", "gray", "neighborCount"):
            self.assertEqual(flagged(line), [], line)

    def test_unity_behaviour_types_are_exempt(self):
        for line in ("class A : MonoBehaviour", "class B : StateMachineBehaviour", "class C : PlayableBehaviour",
                     "class D : Behaviour {}", "GetComponent<Behaviour>()", "if (x is Behaviour b && b.enabled)",
                     "Behaviour.enabled = false;", "typeof(Behaviour)", "var b = (Behaviour)c;",
                     "UnityEngine.Behaviour"):
            self.assertEqual(flagged(line), [], line)

    def test_behaviour_in_prose_or_project_names_is_flagged(self):
        for line in ("## Behaviour", "Behaviour of the fluid is odd.", "| Behaviour | x |",
                     "class D : FluidBehaviour", "the fluid behaviour"):
            self.assertTrue(flagged(line), line)

    def test_suppression_marker(self):
        self.assertEqual(flagged("quoted: colour  // american-english: ignore"), [])


class DiffModeTests(unittest.TestCase):
    def setUp(self):
        self.repo = TempRepo()
        self.repo.write("Release Notes/x.md", "base\n")
        self.repo.write("a.cs", "class A {}\n")
        self.repo.commit("base")

    def tearDown(self):
        self.repo.close()

    def test_path_with_a_space_is_scanned(self):
        self.repo.write("Release Notes/x.md", "base\nthe colour is set\n")
        code, out = self.repo.run(SCRIPT)
        self.assertEqual(code, 1, out)
        self.assertIn("Release Notes/x.md:2: colour", out)

    def test_hostile_diff_settings_still_scan(self):
        for key, value in (("color.diff", "always"), ("diff.mnemonicPrefix", "true"), ("diff.noprefix", "true"),
                           ("diff.relative", "true")):
            self.repo.git("config", key, value)
        self.repo.write("a.cs", "class A {}\n// normalise the input\n")
        code, out = self.repo.run(SCRIPT)
        self.assertEqual(code, 1, out)
        self.assertIn("normalise", out)

    def test_untracked_file_is_scanned(self):
        self.repo.write("new.md", "a favourite example\n")
        code, out = self.repo.run(SCRIPT)
        self.assertEqual(code, 1, out)

    def test_whitespace_only_change_is_not_new_text(self):
        self.repo.write("Release Notes/x.md", "base\n")
        self.repo.write("old.md", "colour\n")
        self.repo.commit("legacy british line")
        self.repo.write("old.md", "colour   \n")
        code, out = self.repo.run(SCRIPT)
        self.assertEqual(code, 0, out)

    def test_its_own_fixtures_are_not_scanned(self):
        self.repo.write("tests/test_check_american_english.py", "FIXTURE = 'colour'\n")
        code, out = self.repo.run(SCRIPT)
        self.assertEqual(code, 0, out)

    def test_pathspec_limits_the_batch(self):
        self.repo.write("a.cs", "class A {}\n// colour\n")
        self.repo.write("Release Notes/x.md", "base\nclean text\n")
        code, out = self.repo.run(SCRIPT, "Release Notes")
        self.assertEqual(code, 0, out)


if __name__ == "__main__":
    unittest.main()
