"""session_cost_report.py: turn categorization, the per-agent-type rollup, and empty-input safety."""
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


if __name__ == "__main__":
    unittest.main()
