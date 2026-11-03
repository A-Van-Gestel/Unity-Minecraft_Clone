"""wt_link_local.py: linking the primary checkout's local-only files into a worktree, and unlinking them.

Both modes run as real processes; `git worktree add` / `git worktree remove` run against a throwaway
repository under the system temp directory, never against this checkout.
"""
import os
import unittest

from helpers import TempRepo, run_script

HEADER = "# wt_link_local.py: worktree links to local-only dirs"


def is_link(path):
    """A directory link (NTFS junction or symlink) or a file symlink: what --unlink has to remove."""
    return os.path.islink(path) or (hasattr(os.path, "isjunction") and os.path.isjunction(path))


class WtLinkTests(unittest.TestCase):
    def setUp(self):
        self.repo = TempRepo()
        # The primary's per-checkout set, in the shapes `.git/info/exclude` really uses. `/wt/` keeps the
        # nested worktree folder out of the primary's status (and makes it an ignored candidate path).
        self.repo.write(".git/info/exclude",
                        "# comment line\n/_REFERENCES/**/*\n/notes.md\n/.codegraph/\n/.claude/\n/wt/\n")
        self.repo.write("tracked.cs", "class T {}\n")
        self.repo.commit("init")
        self.repo.write("_REFERENCES/Plans/README.md", "plan one\n")
        self.repo.write("notes.md", "note one\n")
        self.repo.write(".codegraph/codegraph.db", "index\n")
        self.repo.write(".claude/settings.local.json", "{}\n")
        self.repo.git("worktree", "add", "--detach", "wt")

    def tearDown(self):
        self.repo.close()

    def link(self, *args):
        return run_script("wt_link_local.py", *args)

    def status(self, worktree=None):
        return self.repo.git("-C", worktree or ".", "status", "--porcelain")

    def exclude_lines(self):
        return self.repo.read(".git/info/exclude").splitlines()

    def test_local_only_paths_are_linked_and_neither_checkout_looks_dirty(self):
        code, out = self.link(self.repo.root, self.repo.path("wt"))
        self.assertEqual(code, 0, out)
        self.assertIn("wt_link_local: linked 2, copied 0 local-only paths into", out)

        self.assertTrue(is_link(self.repo.path("wt/_REFERENCES")))
        self.assertEqual(open(self.repo.path("wt/_REFERENCES/Plans/README.md"), "rb").read(), b"plan one\n")
        primary_note, linked_note = os.stat(self.repo.path("notes.md")), os.stat(self.repo.path("wt/notes.md"))
        self.assertEqual(primary_note.st_ino, linked_note.st_ino)
        self.assertEqual(primary_note.st_nlink, 2)

        self.assertEqual(self.status(), "")
        self.assertEqual(self.status("wt"), "")
        self.assertIn(HEADER, self.exclude_lines())
        self.assertIn("/_REFERENCES", self.exclude_lines())

    def test_tool_state_is_never_linked(self):
        self.assertEqual(self.link(self.repo.root, self.repo.path("wt"))[0], 0)
        self.assertTrue(os.path.isfile(self.repo.path(".claude/settings.local.json")))
        self.assertFalse(os.path.exists(self.repo.path("wt/.codegraph")))
        self.assertFalse(os.path.exists(self.repo.path("wt/.claude/settings.local.json")))

    def test_a_second_run_links_nothing_new(self):
        self.assertEqual(self.link(self.repo.root, self.repo.path("wt"))[0], 0)
        code, out = self.link(self.repo.root, self.repo.path("wt"))
        self.assertEqual(code, 0, out)
        self.assertIn("linked 0, copied 0", out)
        lines = self.exclude_lines()
        self.assertEqual(lines.count(HEADER), 1)
        self.assertEqual(lines.count("/_REFERENCES"), 1)
        self.assertEqual(len(lines), len(set(lines)), "\n".join(lines))

    def test_an_edit_in_the_linked_folder_is_seen_by_the_primary(self):
        self.assertEqual(self.link(self.repo.root, self.repo.path("wt"))[0], 0)
        self.repo.write("wt/_REFERENCES/Plans/README.md", "plan two\n")
        self.assertEqual(self.repo.read("_REFERENCES/Plans/README.md"), "plan two\n")
        self.assertIn(HEADER, self.exclude_lines())
        self.assertIn("/_REFERENCES", self.exclude_lines())

    def test_unlink_leaves_every_target_for_worktree_remove(self):
        self.assertEqual(self.link(self.repo.root, self.repo.path("wt"))[0], 0)
        code, out = self.link("--unlink", self.repo.path("wt"))
        self.assertEqual(code, 0, out)
        self.assertIn("unlinked 1 links in", out)
        self.assertFalse(is_link(self.repo.path("wt/_REFERENCES")))

        # The hard-linked notes.md stays in the worktree: removing the worktree must drop only that name.
        self.repo.git("worktree", "remove", "--force", "wt")
        self.assertFalse(os.path.exists(self.repo.path("wt")))
        self.assertEqual(self.repo.read("_REFERENCES/Plans/README.md"), "plan one\n")
        self.assertEqual(self.repo.read("notes.md"), "note one\n")
        self.assertEqual(self.status(), "")

    def test_the_same_path_for_both_arguments_links_nothing(self):
        before = self.exclude_lines()
        code, out = self.link(self.repo.path("wt"), self.repo.path("wt"))
        self.assertEqual(code, 0, out)
        self.assertIn("same checkout, nothing linked", out)
        self.assertEqual(self.exclude_lines(), before)
        self.assertFalse(os.path.exists(self.repo.path("wt/_REFERENCES")))

    def test_unlink_refuses_the_main_checkout(self):
        """The main checkout's own .claude junctions are links too; --unlink there would strip them."""
        code, out = self.link("--unlink", self.repo.root)
        self.assertEqual(code, 2, out)
        self.assertIn("main checkout", out)

    def test_a_path_that_is_not_a_checkout_is_refused(self):
        outside = os.path.dirname(self.repo.root)
        code, out = self.link(self.repo.root, outside)
        self.assertEqual(code, 2, out)
        self.assertIn("not a git checkout", out)
        self.assertIn(os.path.realpath(outside), out)
        code, out = self.link("--unlink", outside)
        self.assertEqual(code, 2, out)

    def test_a_link_needs_a_folder_that_exists(self):
        code, out = self.link(self.repo.root, os.path.join(self.repo.root, "gone"))
        self.assertEqual(code, 2, out)
        self.assertIn("gone", out)


if __name__ == "__main__":
    unittest.main()
