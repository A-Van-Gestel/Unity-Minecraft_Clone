"""setup_agent_links.py and check_twin_files.py, run from a copy inside a throwaway repo (both locate the
repo root from their own path)."""
import os
import shutil
import subprocess
import sys
import unittest

from helpers import TempRepo, script_path


class CopiedScriptRepo(TempRepo):
    def install(self, name):
        destination = self.path(f"Tools/Python/{name}")
        os.makedirs(os.path.dirname(destination), exist_ok=True)
        shutil.copy2(script_path(name), destination)
        return destination

    def run_copy(self, name, *args):
        result = subprocess.run([sys.executable, self.path(f"Tools/Python/{name}"), *args], cwd=self.root,
                                capture_output=True)
        return result.returncode, (result.stdout + result.stderr).decode("utf-8", "replace")


class SetupAgentLinksTests(unittest.TestCase):
    def setUp(self):
        self.repo = CopiedScriptRepo()
        self.repo.install("setup_agent_links.py")
        for name in ("skills", "rules", "agents"):
            self.repo.write(f".agents/{name}/README.md", name)

    def tearDown(self):
        self.repo.close()

    def test_check_reports_and_writes_nothing(self):
        exclude = self.repo.path(".git/info/exclude")
        before = open(exclude, encoding="utf-8").read() if os.path.exists(exclude) else None
        code, out = self.repo.run_copy("setup_agent_links.py", "--check")
        self.assertEqual(code, 1, out)
        self.assertIn("MISSING .claude/skills", out)
        self.assertFalse(os.path.exists(self.repo.path(".claude")))
        after = open(exclude, encoding="utf-8").read() if os.path.exists(exclude) else None
        self.assertEqual(before, after)

    def test_creates_links_and_excludes_then_checks_clean(self):
        code, out = self.repo.run_copy("setup_agent_links.py")
        self.assertEqual(code, 0, out)
        self.assertTrue(os.path.isfile(self.repo.path(".claude/skills/README.md")))
        self.assertIn("/.claude/agents/", self.repo.read(".git/info/exclude"))
        self.assertEqual(self.repo.git("status", "--porcelain", "--", ".claude"), "")
        code, out = self.repo.run_copy("setup_agent_links.py", "--check")
        self.assertEqual(code, 0, out)

    def test_real_directory_is_a_conflict_never_replaced(self):
        self.repo.write(".claude/skills/mine.md", "keep me")
        code, out = self.repo.run_copy("setup_agent_links.py")
        self.assertEqual(code, 2, out)
        self.assertEqual(self.repo.read(".claude/skills/mine.md"), "keep me")


class TwinFilesTests(unittest.TestCase):
    def setUp(self):
        self.repo = CopiedScriptRepo()
        self.repo.install("check_twin_files.py")
        self.repo.write("CLAUDE.md", "same\nrules\n")
        self.repo.write("AGENTS.md", "same\nrules\n")
        self.repo.write(".agents/skills/demo/SKILL.md", "---\nname: demo\n---\n")

    def tearDown(self):
        self.repo.close()

    def write_bytes(self, relative, content):
        with open(self.repo.path(relative), "wb") as handle:
            handle.write(content)

    def test_identical_differing_and_missing(self):
        code, out = self.repo.run_copy("check_twin_files.py")
        self.assertEqual(code, 0, out)
        self.assertIn("OK: 3 agent guidance files", out)
        self.repo.write("AGENTS.md", "same\nstale\n")
        code, out = self.repo.run_copy("check_twin_files.py")
        self.assertEqual(code, 1, out)
        self.assertIn("line 2", out)
        os.remove(self.repo.path("AGENTS.md"))
        self.assertEqual(self.repo.run_copy("check_twin_files.py")[0], 2)

    def test_bom_in_a_skill_fails_and_names_the_file(self):
        self.write_bytes(".agents/skills/demo/SKILL.md", b"\xef\xbb\xbf---\nname: demo\n---\n")
        code, out = self.repo.run_copy("check_twin_files.py")
        self.assertEqual(code, 1, out)
        self.assertIn("SKILL.md has a UTF-8 BOM", out)
        self.assertIn("OK: CLAUDE.md == AGENTS.md", out)

    def test_bom_on_identical_twins_still_fails(self):
        for name in ("CLAUDE.md", "AGENTS.md"):
            self.write_bytes(name, b"\xef\xbb\xbfsame\n")
        code, out = self.repo.run_copy("check_twin_files.py")
        self.assertEqual(code, 1, out)
        self.assertIn("CLAUDE.md has a UTF-8 BOM", out)

    def test_utf16_and_invalid_utf8_fail(self):
        self.write_bytes(".agents/skills/demo/SKILL.md", "---\n".encode("utf-16"))
        self.repo.write(".agents/agents/bad.md", "ok\n")
        self.write_bytes(".agents/agents/bad.md", b"caf\xe9\n")
        code, out = self.repo.run_copy("check_twin_files.py")
        self.assertEqual(code, 1, out)
        self.assertIn("SKILL.md is UTF-16", out)
        self.assertIn("bad.md is not valid UTF-8 (byte 3)", out)

    def test_empty_agents_tree_is_not_clean(self):
        os.remove(self.repo.path(".agents/skills/demo/SKILL.md"))
        code, out = self.repo.run_copy("check_twin_files.py")
        self.assertEqual(code, 2, out)
        self.assertIn("MISSING: no files under .agents/", out)


if __name__ == "__main__":
    unittest.main()
