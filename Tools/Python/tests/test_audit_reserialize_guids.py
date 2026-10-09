"""audit_reserialize_guids.py: lost references, rename pairing, and dangling guids of vanished `.meta` files."""
import os
import tempfile
import unittest

from helpers import TempRepo, run_script, unity_meta

SCRIPT = "audit_reserialize_guids.py"
FOO, BAR, REF, PREFAB, NEW_PREFAB = ("a" * 32, "b" * 32, "c" * 32, "e" * 32, "f" * 32)


def prefab(*guids):
    lines = "".join(f"ref{i}: {{fileID: 1, guid: {g}, type: 2}}\n" for i, g in enumerate(guids))
    return "%YAML 1.1\n" + lines


class GitModeTests(unittest.TestCase):
    def setUp(self):
        self.repo = TempRepo()
        self.repo.write("Foo.cs.meta", unity_meta(FOO))
        self.repo.write("Old.prefab", prefab(REF))
        self.repo.write("Old.prefab.meta", unity_meta(PREFAB))
        self.repo.commit("base")

    def tearDown(self):
        self.repo.close()

    def audit(self):
        return self.repo.run(SCRIPT, "--base", "HEAD~1", "--head", "HEAD")

    def test_nulled_reference_in_place_is_lost(self):
        self.repo.write("Old.prefab", "%YAML 1.1\nref0: {fileID: 0}\n")
        self.repo.commit("reserialize")
        code, out = self.audit()
        self.assertEqual(code, 1, out)
        self.assertIn("LOST  Old.prefab", out)

    def test_unchanged_references_pass(self):
        self.repo.write("Old.prefab", prefab(REF) + "extra: 1\n")
        self.repo.commit("format churn")
        code, out = self.audit()
        self.assertEqual(code, 0, out)

    def test_working_tree_mode(self):
        self.repo.write("Old.prefab", "%YAML 1.1\nref0: {fileID: 0}\n")
        code, out = self.repo.run(SCRIPT)
        self.assertEqual(code, 1, out)

    def test_unrelated_delete_and_add_are_not_paired_into_a_false_loss(self):
        # Two script metas differ only in the guid line, so git pairs them as a rename.
        self.repo.git("rm", "-q", "Foo.cs.meta")
        self.repo.write("Bar.cs.meta", unity_meta(BAR))
        self.repo.commit("delete Foo, add Bar")
        code, out = self.audit()
        self.assertEqual(code, 0, out)
        self.assertIn("DEL   Foo.cs.meta", out)
        self.assertNotIn("LOST", out)

    def test_deleted_script_still_used_by_a_scene_is_dangling(self):
        self.repo.write("Main.unity", f"%YAML 1.1\nm_Script: {{fileID: 11500000, guid: {FOO}, type: 3}}\n")
        self.repo.commit("scene uses Foo")
        self.repo.git("rm", "-q", "Foo.cs.meta")
        self.repo.commit("delete Foo")
        code, out = self.audit()
        self.assertEqual(code, 1, out)
        self.assertIn("DANGLING Foo.cs.meta", out)
        self.assertIn("Main.unity", out)

    def test_meta_regenerated_by_a_move_outside_unity_is_dangling(self):
        self.repo.write("Main.unity", f"%YAML 1.1\nprefab: {{fileID: 1, guid: {PREFAB}, type: 3}}\n")
        self.repo.commit("scene uses the prefab")
        self.repo.git("mv", "Old.prefab", "New.prefab")
        self.repo.git("rm", "-q", "Old.prefab.meta")
        self.repo.write("New.prefab.meta", unity_meta(NEW_PREFAB))
        self.repo.commit("moved outside Unity")
        code, out = self.audit()
        self.assertEqual(code, 1, out)
        self.assertIn("DANGLING Old.prefab.meta", out)

    def test_unity_move_that_loses_a_reference_is_lost(self):
        # The rewrite makes the small prefab unrecognizable to git's rename detection; the moved
        # `.meta` (same guid) is what re-pairs it.
        self.repo.git("mv", "Old.prefab", "New.prefab")
        self.repo.git("mv", "Old.prefab.meta", "New.prefab.meta")
        self.repo.write("New.prefab", "%YAML 1.1\nref0: {fileID: 0}\n")
        self.repo.commit("move + reserialize")
        code, out = self.audit()
        self.assertEqual(code, 1, out)
        self.assertIn("LOST  Old.prefab -> New.prefab", out)

    def test_unity_move_without_loss_passes(self):
        self.repo.git("mv", "Old.prefab", "New.prefab")
        self.repo.git("mv", "Old.prefab.meta", "New.prefab.meta")
        self.repo.commit("clean move")
        code, out = self.audit()
        self.assertEqual(code, 0, out)


class CompareModeTests(unittest.TestCase):
    def test_compare_flags_a_drop_and_passes_identity(self):
        with tempfile.TemporaryDirectory() as directory:
            old, same, new = (os.path.join(directory, n) for n in ("old.prefab", "same.prefab", "new.prefab"))
            for path, text in ((old, prefab(REF, REF)), (same, prefab(REF, REF)), (new, prefab(REF))):
                with open(path, "w", encoding="utf-8", newline="\n") as handle:
                    handle.write(text)
            self.assertEqual(run_script(SCRIPT, "--compare", old, same)[0], 0)
            code, out = run_script(SCRIPT, "--compare", old, new)
            self.assertEqual(code, 1, out)
            self.assertIn(f"guid {REF}: 2 -> 1", out)


if __name__ == "__main__":
    unittest.main()
