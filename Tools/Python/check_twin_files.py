"""Check the agent guidance files: CLAUDE.md only imports AGENTS.md, and every file is UTF-8 without a BOM.

WHAT THIS GUARDS
    AGENTS.md holds the project instructions (read by other agent harnesses); CLAUDE.md (read by
    Claude Code) is a stub whose only content is the `@AGENTS.md` import, so both read one text.
    Rules added to CLAUDE.md itself would reach Claude Code only, leaving two agents following
    different rules with no symptom until one of them misbehaves. HTML comments and blank lines
    in the stub are allowed.

    Every file under .agents/ (skills, agents, rules) plus both files must be UTF-8 without a BOM.
    A BOM before a skill's opening `---` breaks its frontmatter, and the skill silently drops out
    of the skills list; PowerShell 5.1 `Out-File`/`Set-Content` and "UTF-8 with BOM" editor
    settings are the usual sources.

FIXING A FAILURE
    NOT A STUB: move the extra text from CLAUDE.md into AGENTS.md, leaving only `@AGENTS.md`.
    An ENCODING line: re-save that file as UTF-8 without BOM.

READS   CLAUDE.md, AGENTS.md and every file under .agents/. WRITES nothing.

RUN
    python Tools/Python/check_twin_files.py

EXIT CODES
    0  the stub only imports its target and every file is UTF-8 without BOM
    1  the stub holds other content (its first such line is printed), or a file has a BOM, is
       UTF-16, or is not valid UTF-8
    2  the stub or its target is missing, or .agents/ holds no files (a scan that saw nothing is not clean)
"""
import os
import re
import sys

IMPORT_STUBS = [("CLAUDE.md", "AGENTS.md")]  # (stub, the file it imports)
HTML_COMMENT = re.compile(r"<!--.*?-->", re.DOTALL)
ENCODING_DIRS = [".agents"]
UTF8_BOM = b"\xef\xbb\xbf"
UTF16_BOMS = (b"\xff\xfe", b"\xfe\xff")


def stub_problem(content, target):
    """Returns the first line of `content` that is not the `@target` import, or None."""
    text = HTML_COMMENT.sub("", content.decode("utf-8", "replace").lstrip("\ufeff"))
    lines = [line.strip() for line in text.splitlines() if line.strip()]
    if lines == [f"@{target}"]:
        return None
    extra = [line for line in lines if line != f"@{target}"]
    return extra[0] if extra else f"(no @{target} line)"


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
    """The stubs, their targets and every file under ENCODING_DIRS (links are not followed), sorted."""
    files = [os.path.join(repo_root, name) for pair in IMPORT_STUBS for name in pair]
    for directory in ENCODING_DIRS:
        for folder, _, names in os.walk(os.path.join(repo_root, directory)):
            files += [os.path.join(folder, name) for name in names]
    return sorted(f for f in files if os.path.isfile(f))


def check_encodings(repo_root):
    pair_files = {os.path.join(repo_root, name) for pair in IMPORT_STUBS for name in pair}
    files = guidance_files(repo_root)
    if not [f for f in files if f not in pair_files]:
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
    for stub, target in IMPORT_STUBS:
        paths = [os.path.join(repo_root, name) for name in (stub, target)]
        missing = [p for p in paths if not os.path.exists(p)]
        if missing:
            print(f"MISSING: {', '.join(missing)}")
            status = max(status, 2)
            continue
        with open(paths[0], "rb") as handle:
            problem = stub_problem(handle.read(), target)
        if problem is None:
            print(f"OK: {stub} imports {target}")
        else:
            print(f"NOT A STUB: {stub} must hold only `@{target}` (+ HTML comments); found: {problem[:80]}")
            status = max(status, 1)
    return max(status, check_encodings(repo_root))


if __name__ == "__main__":
    sys.exit(main())
