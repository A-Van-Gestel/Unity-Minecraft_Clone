"""Check the agent guidance files: twins byte-identical, and every file UTF-8 without a BOM.

WHAT THIS GUARDS
    CLAUDE.md (read by Claude Code) and AGENTS.md (read by other agent tools) carry the same
    project instructions and are synced by hand. An edit to one that misses the other leaves two
    agents following different rules, with no symptom until one of them misbehaves.

    Every file under .agents/ (skills, agents, rules) plus the twins must be UTF-8 without a BOM.
    A BOM before a skill's opening `---` breaks its frontmatter, and the skill silently drops out
    of the skills list; PowerShell 5.1 `Out-File`/`Set-Content` and "UTF-8 with BOM" editor
    settings are the usual sources.

FIXING A MISMATCH
    Decide which side holds the intended edit, then copy it over the other (`cp CLAUDE.md
    AGENTS.md`) — after checking the other side carries no edit of its own (`git diff` both).
    An ENCODING line: re-save that file as UTF-8 without BOM.

READS   the twin files and every file under .agents/. WRITES nothing.

RUN
    python Tools/Python/check_twin_files.py

EXIT CODES
    0  every pair identical and every file UTF-8 without BOM
    1  a pair differs (the first differing line is printed), or a file has a BOM, is UTF-16, or
       is not valid UTF-8
    2  a file of a pair is missing, or .agents/ holds no files (a scan that saw nothing is not clean)
"""
import os
import sys

TWIN_PAIRS = [("CLAUDE.md", "AGENTS.md")]
ENCODING_DIRS = [".agents"]
UTF8_BOM = b"\xef\xbb\xbf"
UTF16_BOMS = (b"\xff\xfe", b"\xfe\xff")


def first_difference(left, right):
    left_lines, right_lines = left.split(b"\n"), right.split(b"\n")
    for number, (a, b) in enumerate(zip(left_lines, right_lines), 1):
        if a != b:
            return number
    return min(len(left_lines), len(right_lines)) + 1


def encoding_problem(content):
    """Returns why `content` is not UTF-8 without BOM, or None."""
    if content.startswith(UTF8_BOM):
        return "has a UTF-8 BOM"
    if content.startswith(UTF16_BOMS):
        return "is UTF-16"
    try:
        content.decode("utf-8")
    except UnicodeDecodeError as error:
        return f"is not valid UTF-8 (byte {error.start})"
    return None


def guidance_files(repo_root):
    """The twins plus every file under ENCODING_DIRS (links are not followed), sorted."""
    files = [os.path.join(repo_root, name) for pair in TWIN_PAIRS for name in pair]
    for directory in ENCODING_DIRS:
        for folder, _, names in os.walk(os.path.join(repo_root, directory)):
            files += [os.path.join(folder, name) for name in names]
    return sorted(f for f in files if os.path.isfile(f))


def check_encodings(repo_root):
    twins = {os.path.join(repo_root, name) for pair in TWIN_PAIRS for name in pair}
    files = guidance_files(repo_root)
    if not [f for f in files if f not in twins]:
        print(f"MISSING: no files under {', '.join(d + '/' for d in ENCODING_DIRS)}")
        return 2
    status = 0
    for path in files:
        with open(path, "rb") as handle:
            problem = encoding_problem(handle.read())
        if problem:
            relative = os.path.relpath(path, repo_root).replace(os.sep, "/")
            print(f"ENCODING: {relative} {problem}: re-save as UTF-8 without BOM")
            status = 1
    if status == 0:
        print(f"OK: {len(files)} agent guidance files are UTF-8 without BOM")
    return status


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
    return max(status, check_encodings(repo_root))


if __name__ == "__main__":
    sys.exit(main())
