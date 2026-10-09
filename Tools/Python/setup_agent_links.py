"""Create (or check) the `.claude/` links into the tracked `.agents/` tree on this machine.

WHY
    Skills, rules and subagents are authored once under `.agents/` (tracked, tool-neutral) and
    reach Claude Code through `.claude/skills`, `.claude/rules` and `.claude/agents`. Those three are
    directory LINKS, local to each checkout: git does not carry them (`core.symlinks` is false on
    Windows) and `.git/info/exclude` hides them. A fresh clone therefore has none of them — no
    skills, and no `committer` / `validation-runner` / `doc-sweeper` for CLAUDE.md to delegate to —
    until this script runs once.

WHAT IT DOES
    For each name: a missing link is created (a directory junction on Windows, which needs no admin
    rights; a symlink elsewhere), and `/.claude/<name>/` is added to `.git/info/exclude`. A link that
    already resolves to the right `.agents/` folder is left alone. A real directory or a link to
    somewhere else is reported and NEVER replaced. Restart Claude Code after creating a link: its
    file watcher only covers directories that existed when the session started.

READS / WRITES   `.claude/`, `.git/info/exclude`. `--check` writes nothing.

RUN
    python Tools/Python/setup_agent_links.py            # create what is missing
    python Tools/Python/setup_agent_links.py --check    # report only

EXIT CODES
    0  every link present and correct (or created)
    1  --check found a missing link or exclude entry
    2  a conflict: a real directory or a foreign link sits where a link belongs
"""
import argparse
import os
import subprocess
import sys

LINK_NAMES = ["skills", "rules", "agents"]


def repo_root():
    return os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))


def is_link(path):
    return os.path.islink(path) or (hasattr(os.path, "isjunction") and os.path.isjunction(path))


def create_link(link, target):
    if os.name == "nt":
        result = subprocess.run(["cmd", "/c", "mklink", "/J", link, target], capture_output=True)
        if result.returncode != 0:
            raise OSError(result.stderr.decode("mbcs", "replace").strip() or "mklink /J failed")
    else:
        os.symlink(target, link, target_is_directory=True)


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description="Create or check the .claude/ links into .agents/.")
    parser.add_argument("--check", action="store_true", help="report only, write nothing")
    args = parser.parse_args()

    root = repo_root()
    # In a linked worktree or submodule `.git` is a file, so ask git where info/exclude lives.
    git_path = subprocess.run(["git", "rev-parse", "--git-path", "info/exclude"], cwd=root,
                              capture_output=True, text=True, check=True).stdout.strip()
    exclude_path = os.path.normpath(os.path.join(root, git_path))
    exclude_text = ""
    if os.path.exists(exclude_path):
        with open(exclude_path, encoding="utf-8") as handle:
            exclude_text = handle.read()
    exclude_lines = set(exclude_text.splitlines())

    status, created, new_excludes = 0, [], []
    if not args.check:
        os.makedirs(os.path.join(root, ".claude"), exist_ok=True)
    for name in LINK_NAMES:
        target = os.path.join(root, ".agents", name)
        link = os.path.join(root, ".claude", name)
        entry = f"/.claude/{name}/"
        if entry not in exclude_lines:
            new_excludes.append(entry)
        if not os.path.isdir(target):
            print(f"SKIP  .agents/{name} does not exist")
            continue
        if os.path.lexists(link):
            if is_link(link) and os.path.realpath(link) == os.path.realpath(target):
                print(f"OK    .claude/{name} -> .agents/{name}")
            else:
                kind = "a link to " + os.path.realpath(link) if is_link(link) else "a real directory/file"
                print(f"CONFLICT .claude/{name} is {kind}; left untouched")
                status = 2
            continue
        if args.check:
            print(f"MISSING .claude/{name}")
            status = max(status, 1)
            continue
        create_link(link, target)
        created.append(name)
        print(f"MADE  .claude/{name} -> .agents/{name}")

    if new_excludes:
        if args.check:
            print(f"MISSING exclude entries: {', '.join(new_excludes)}")
            status = max(status, 1)
        else:
            os.makedirs(os.path.dirname(exclude_path), exist_ok=True)
            with open(exclude_path, "a", encoding="utf-8") as handle:
                prefix = "" if exclude_text.endswith("\n") or not exclude_text else "\n"
                handle.write(prefix + "\n".join(new_excludes) + "\n")
            print(f"ADDED exclude entries: {', '.join(new_excludes)}")
    if created:
        print("Restart Claude Code so it picks up the new link(s).")
    return status


if __name__ == "__main__":
    sys.exit(main())
