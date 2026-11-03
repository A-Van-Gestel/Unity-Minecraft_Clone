"""session_cost_report.py: turn categorization, the transcript folders of a checkout and its worktrees,
the per-agent-type rollup, and empty-input safety."""
import contextlib
import io
import json
import os
import shutil
import tempfile
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


MILLION = {"input_tokens": 1_000_000, "output_tokens": 1_000_000}  # Haiku 5.5: $0.10 + $0.50


def turn(message_id, model="claude-haiku-5-5", usage=MILLION):
    return {"type": "assistant", "message": {"id": message_id, "model": model, "usage": usage, "content": []}}


class RollupTests(unittest.TestCase):
    def setUp(self):
        self.root = tempfile.mkdtemp(prefix="cost-test-")

    def tearDown(self):
        shutil.rmtree(self.root, ignore_errors=True)

    def write_jsonl(self, relative, records):
        path = os.path.join(self.root, *relative.split("/"))
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as handle:
            handle.writelines(json.dumps(r) + "\n" for r in records)
        return path

    def session(self, name, agents):
        """agents: {agent id: (agentType or None for no meta, records)}"""
        path = self.write_jsonl(f"{name}.jsonl", [turn(f"{name}-main")])
        for agent_id, (agent_type, records) in agents.items():
            self.write_jsonl(f"{name}/subagents/agent-{agent_id}.jsonl", records)
            if agent_type:
                meta = os.path.join(self.root, name, "subagents", f"agent-{agent_id}.meta.json")
                with open(meta, "w", encoding="utf-8") as handle:
                    json.dump({"agentType": agent_type}, handle)
        return path

    def rollup(self, files):
        with contextlib.redirect_stdout(io.StringIO()) as out:
            rows = report.rollup(files)
        return rows, out.getvalue()

    def test_same_type_across_sessions_adds_up(self):
        files = [self.session("s1", {"a": ("validation-runner", [turn("t1"), turn("t2")])}),
                 self.session("s2", {"b": ("validation-runner", [turn("t3")])})]
        rows, _ = self.rollup(files)
        self.assertEqual((rows["validation-runner"]["spawns"], rows["validation-runner"]["turns"]), (2, 3))
        self.assertAlmostEqual(rows["validation-runner"]["cost"], 3 * 0.60)

    def test_missing_meta_groups_as_unknown(self):
        rows, _ = self.rollup([self.session("s1", {"a": (None, [turn("t1")])})])
        self.assertEqual(list(rows), ["?"])

    def test_repeated_message_id_counts_once(self):
        rows, _ = self.rollup([self.session("s1", {"a": ("committer", [turn("t1"), turn("t1")])})])
        self.assertEqual(rows["committer"]["turns"], 1)
        self.assertAlmostEqual(rows["committer"]["cost"], 0.60)

    def test_no_subagents_says_so(self):
        rows, out = self.rollup([self.session("s1", {})])
        self.assertEqual(rows, {})
        self.assertIn("no subagents", out)
        self.assertIn("main threads", out)


class TranscriptDirTests(unittest.TestCase):
    """transcript_files and worktree_of: this checkout's folder, its Claude Code worktree folders, and
    the worktrunk `<repo>.<branch>` sibling folder — but not an unrelated same-prefix project folder."""

    def setUp(self):
        self.root = tempfile.mkdtemp(prefix="cost-test-")
        self.projects = os.path.join(self.root, "projects")
        # The fake checkout the folder names are derived from, so a worktrunk sibling's cwd is predictable.
        self.checkout = os.path.join(self.root, "repo")
        self.main_dir = os.path.join(self.projects, "repo")
        os.makedirs(self.main_dir)

    def tearDown(self):
        shutil.rmtree(self.root, ignore_errors=True)

    def transcript(self, folder_name, name, cwd=None):
        """One main-thread transcript under projects/<folder_name>/, every line carrying its cwd."""
        folder = os.path.join(self.projects, folder_name)
        os.makedirs(folder, exist_ok=True)
        path = os.path.join(folder, name)
        record = dict(turn(f"{name}-main"))
        if cwd is not None:
            record["cwd"] = cwd
        with open(path, "w", encoding="utf-8") as handle:
            handle.write(json.dumps(record) + "\n")
        return path

    def header(self, path, main_dir):
        """The session header line report_session prints for one transcript."""
        with contextlib.redirect_stdout(io.StringIO()) as out:
            report.report_session(path, False, main_dir)
        return out.getvalue()

    def test_claude_worktree_folder_is_included_and_tagged(self):
        path = self.transcript("repo--claude-worktrees-x", "s1.jsonl",
                               cwd=os.path.join(self.checkout, ".claude", "worktrees", "x"))
        self.assertEqual(report.transcript_files(self.main_dir, self.checkout), [path])
        self.assertEqual(report.worktree_of(path, self.main_dir), "x")
        self.assertIn("[worktree x]", self.header(path, self.main_dir))

    def test_worktrunk_sibling_included_but_a_same_prefix_folder_is_not(self):
        included = self.transcript("repo-strata-foo", "w.jsonl", cwd=self.checkout + ".strata-foo")
        main = self.transcript("repo", "m.jsonl")
        excluded = self.transcript("repo-Backup", "b.jsonl", cwd=self.checkout + " Backup")
        files = report.transcript_files(self.main_dir, self.checkout)
        self.assertIn(main, files)
        self.assertIn(included, files)
        self.assertNotIn(excluded, files)
        self.assertEqual(report.worktree_of(included, self.main_dir), "strata-foo")

    def test_order_is_by_mtime_across_folders(self):
        newest = self.transcript("repo", "new.jsonl")
        oldest = self.transcript("repo--claude-worktrees-x", "old.jsonl")
        middle = self.transcript("repo-strata-foo", "mid.jsonl", cwd=self.checkout + ".strata-foo")
        for offset, path in enumerate((oldest, middle, newest)):
            os.utime(path, (1_000_000 + offset * 60, 1_000_000 + offset * 60))
        self.assertEqual(report.transcript_files(self.main_dir, self.checkout), [oldest, middle, newest])

    def test_main_checkout_transcript_has_no_worktree(self):
        path = self.transcript("repo", "m.jsonl")
        self.assertIsNone(report.worktree_of(path, self.main_dir))
        self.assertNotIn("[worktree", self.header(path, self.main_dir))

if __name__ == "__main__":
    unittest.main()
