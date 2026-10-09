"""fold_into_commit.py: what `fixup` accepts and refuses, and the gated `squash`."""
import unittest

from helpers import TempRepo, numbered_lines

SCRIPT = "fold_into_commit.py"
BOTH_ENDS = {1: "ONE", 20: "TWENTY"}


class FixupTests(unittest.TestCase):
    def setUp(self):
        self.repo = TempRepo()
        self.repo.write("root.txt", "a\n")
        self.repo.commit("base")
        self.repo.mark_pushed()
        self.repo.write("sub/f.txt", numbered_lines(20))
        self.target = self.repo.commit("Added: f")
        self.repo.write("root.txt", "a\nb\n")
        self.repo.commit("Updated: root")

    def tearDown(self):
        self.repo.close()

    def fixup(self, *args, cwd=None):
        return self.repo.run(SCRIPT, "fixup", self.target, *args, cwd=cwd)

    def last_subject(self):
        return self.repo.git("log", "-1", "--format=%s").strip()

    def changed_lines_in_last_commit(self):
        return self.repo.git("diff", "--numstat", "HEAD~1", "HEAD").split()[0]

    def stage_first_hunk(self):
        code, out = self.repo.run("stage_hunks.py", "sub/f.txt", "--hunks", "1")
        self.assertEqual(code, 0, out)

    def test_path_given_from_a_subdirectory(self):
        self.repo.write("sub/f.txt", numbered_lines(20, BOTH_ENDS))
        code, out = self.fixup("./f.txt", cwd=self.repo.path("sub"))
        self.assertEqual(code, 0, out)
        self.assertEqual(self.last_subject(), f"fixup! {self.target}")
        self.assertEqual(self.changed_lines_in_last_commit(), "2")

    def test_hunk_selection_with_path_is_refused(self):
        self.repo.write("sub/f.txt", numbered_lines(20, BOTH_ENDS))
        self.stage_first_hunk()
        code, out = self.fixup("sub/f.txt")
        self.assertEqual(code, 2, out)
        self.assertIn("--whole-file", out)

    def test_hunk_selection_without_paths_folds_only_the_selection(self):
        self.repo.write("sub/f.txt", numbered_lines(20, BOTH_ENDS))
        self.stage_first_hunk()
        code, out = self.fixup()
        self.assertEqual(code, 0, out)
        self.assertEqual(self.changed_lines_in_last_commit(), "1")

    def test_whole_file_overrides_the_selection_guard(self):
        self.repo.write("sub/f.txt", numbered_lines(20, BOTH_ENDS))
        self.stage_first_hunk()
        code, out = self.fixup("sub/f.txt", "--whole-file")
        self.assertEqual(code, 0, out)
        self.assertEqual(self.changed_lines_in_last_commit(), "2")

    def test_directory_path_refuses_to_sweep_untracked_files(self):
        self.repo.write("sub/f.txt", numbered_lines(20, BOTH_ENDS))
        self.repo.write("sub/stray.txt", "junk\n")
        code, out = self.fixup("sub")
        self.assertEqual(code, 2, out)
        self.assertIn("sub/stray.txt", out)

    def test_untracked_file_named_explicitly_is_folded(self):
        self.repo.write("sub/f.txt", numbered_lines(20, BOTH_ENDS))
        self.repo.write("sub/stray.txt", "junk\n")
        code, out = self.fixup("sub/f.txt", "sub/stray.txt")
        self.assertEqual(code, 0, out)
        self.assertIn("sub/stray.txt", self.repo.git("show", "--name-only", "--format=", "HEAD"))

    def test_stray_staged_change_outside_paths_is_refused(self):
        self.repo.write("sub/f.txt", numbered_lines(20, BOTH_ENDS))
        self.repo.write("root.txt", "changed\n")
        self.repo.git("add", "root.txt")
        code, out = self.fixup("sub/f.txt")
        self.assertEqual(code, 2, out)
        self.assertIn("root.txt", out)

    def test_pushed_target_is_refused(self):
        pushed = self.repo.git("rev-parse", "HEAD~2").strip()
        self.repo.write("sub/f.txt", numbered_lines(20, BOTH_ENDS))
        code, out = self.repo.run(SCRIPT, "fixup", pushed, "sub/f.txt")
        self.assertEqual(code, 2, out)
        self.assertIn("unpushed", out)

    def test_nothing_to_fold_is_refused(self):
        code, out = self.fixup()
        self.assertEqual(code, 2, out)


class SquashTests(unittest.TestCase):
    def test_squash_folds_into_the_target_and_keeps_the_tree(self):
        with TempRepo() as repo:
            repo.write("root.txt", "a\n")
            repo.commit("base")
            repo.mark_pushed()
            repo.write("f.txt", numbered_lines(5))
            target = repo.commit("Added: f")
            repo.write("g.txt", "g\n")
            repo.commit("Added: g")
            repo.write("f.txt", numbered_lines(5, {3: "THREE"}))
            self.assertEqual(repo.run(SCRIPT, "fixup", target, "f.txt")[0], 0)
            tree_before = repo.git("rev-parse", "HEAD^{tree}").strip()

            code, out = repo.run(SCRIPT, "squash")
            self.assertEqual(code, 0, out)
            self.assertEqual(repo.git("rev-parse", "HEAD^{tree}").strip(), tree_before)
            subjects = repo.git("log", "--format=%s", "origin/main..HEAD").split("\n")
            self.assertEqual([s for s in subjects if s], ["Added: g", "Added: f"])
            self.assertIn("THREE", repo.git("show", "HEAD~1:f.txt"))


if __name__ == "__main__":
    unittest.main()
