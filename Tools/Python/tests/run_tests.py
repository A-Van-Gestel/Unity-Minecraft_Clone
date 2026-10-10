"""Run the helper-script test suite.

WHAT IT COVERS
    The git and Markdown helpers the pre-commit flow and the subagents depend on: fold_into_commit,
    stage_hunks, audit_reserialize_guids, check_american_english, align_md_tables, setup_agent_links,
    check_twin_files, session_cost_report; the doc checkers check_markdown_breaks, check_doc_refs,
    check_doc_links, check_doc_status; rename_tokens; summarize_perf_session; run_player; and prove_red's
    argument and restore logic (its Editor half is stubbed). Each test works in its own throwaway directory or git
    repository under the system temp directory; nothing in this checkout is read or changed.

RUN
    python Tools/Python/tests/run_tests.py            # everything (~30 s)
    python Tools/Python/tests/run_tests.py fold       # only test modules whose name contains "fold"

EXIT CODES
    0  all tests passed
    1  a test failed or errored
"""
import os
import sys
import unittest

TESTS_DIR = os.path.dirname(os.path.abspath(__file__))


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    sys.path.insert(0, TESTS_DIR)
    pattern = f"test_*{sys.argv[1]}*.py" if len(sys.argv) > 1 else "test_*.py"
    suite = unittest.defaultTestLoader.discover(TESTS_DIR, pattern=pattern, top_level_dir=TESTS_DIR)
    if suite.countTestCases() == 0:
        print(f"no tests match {pattern}")
        return 1
    result = unittest.TextTestRunner(verbosity=1).run(suite)
    return 0 if result.wasSuccessful() else 1


if __name__ == "__main__":
    sys.exit(main())
