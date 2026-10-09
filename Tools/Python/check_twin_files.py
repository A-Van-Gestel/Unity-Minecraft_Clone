"""Check that files kept as byte-identical twins really are identical.

WHAT THIS GUARDS
    CLAUDE.md (read by Claude Code) and AGENTS.md (read by other agent tools) carry the same
    project instructions and are synced by hand. An edit to one that misses the other leaves two
    agents following different rules, with no symptom until one of them misbehaves.

FIXING A MISMATCH
    Decide which side holds the intended edit, then copy it over the other (`cp CLAUDE.md
    AGENTS.md`) — after checking the other side carries no edit of its own (`git diff` both).

READS   the twin files. WRITES nothing.

RUN
    python Tools/Python/check_twin_files.py

EXIT CODES
    0  every pair identical
    1  a pair differs (the first differing line is printed)
    2  a file of a pair is missing
"""
import os
import sys

TWIN_PAIRS = [("CLAUDE.md", "AGENTS.md")]


def first_difference(left, right):
    left_lines, right_lines = left.split(b"\n"), right.split(b"\n")
    for number, (a, b) in enumerate(zip(left_lines, right_lines), 1):
        if a != b:
            return number
    return min(len(left_lines), len(right_lines)) + 1


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    repo_root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    status = 0
    for left, right in TWIN_PAIRS:
        paths = [os.path.join(repo_root, name) for name in (left, right)]
        missing = [p for p in paths if not os.path.exists(p)]
        if missing:
            print(f"MISSING: {', '.join(missing)}")
            status = max(status, 2)
            continue
        contents = []
        for path in paths:
            with open(path, "rb") as handle:
                contents.append(handle.read())
        if contents[0] == contents[1]:
            print(f"OK: {left} == {right}")
        else:
            line = first_difference(*contents)
            print(f"DIFFER: {left} and {right} first differ at line {line}")
            status = max(status, 1)
    return status


if __name__ == "__main__":
    sys.exit(main())
