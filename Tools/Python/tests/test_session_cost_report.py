"""session_cost_report.py: turn categorization (the delegation pilot's measure) and empty-input safety."""
import unittest

from helpers import run_script
import session_cost_report as report


def bash(command):
    return report.category("Bash", {"command": command})


class CategoryTests(unittest.TestCase):
    def test_git_forms_this_repo_uses(self):
        for command in ("git status --short", "git -c color.ui=false diff --stat", "git --no-pager log -3",
                        "git -C sub log", "git diff --no-color HEAD", "python Tools/Python/fold_into_commit.py squash",
                        "python Tools/Python/stage_hunks.py f --list"):
            self.assertEqual(bash(command), "git", command)

    def test_non_git_commands(self):
        for command in ("echo digit diff", "gitk", "ls -la"):
            self.assertNotEqual(bash(command), "git", command)

    def test_summarize_tolerates_no_turns(self):
        summary = report.summarize([])
        self.assertEqual((summary["turns"], summary["cost"]), (0, 0))

    def test_last_zero_is_rejected(self):
        code, out = run_script("session_cost_report.py", "--last", "0")
        self.assertNotEqual(code, 0)
        self.assertIn("--last", out)


if __name__ == "__main__":
    unittest.main()
