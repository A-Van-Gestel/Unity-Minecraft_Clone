"""Share the primary checkout's local-only files with a new git worktree, and undo it again.

WHY
    The `.git/info/exclude` set (`_REFERENCES/` plan docs, `notes.md`, …) is per-checkout, so a fresh
    worktrunk worktree starts without it and the plans and notes written in the primary checkout are invisible
    there. This script links those paths into the worktree, so the same files are live in both checkouts
    (a directory junction / hard link is one object, not a copy). Run it as a worktrunk pre-start hook.

USAGE
    python Tools/Python/wt_link_local.py <primary_checkout> <worktree>
    python Tools/Python/wt_link_local.py --unlink <worktree>   # a worktrunk pre-remove hook

    `--unlink` removes the links it made, because `git worktree remove` does not follow an NTFS junction
    but leaves it behind, and therefore leaves the whole folder behind too. It deletes only the link names
    in the worktree, never the primary's files. Re-running link mode is idempotent: what is already
    linked, copied or missing is left alone.

READS / WRITES   git in both checkouts; WRITES junctions, hard links (a copy only across volumes) inside
    <worktree>, plus one pattern per linked folder in the COMMON git dir's `info/exclude`. Nothing else.

EXIT CODES
    0  links made, or nothing left to link (including both paths naming the same checkout)
    2  bad usage, a path that is not a git checkout (the message names it), or --unlink on the main checkout
"""
import argparse
import os
import re
import shutil
import subprocess
import sys

if os.name == "nt":
    import _winapi  # Windows-only junction API, used by make_dir_link

HEADER = "# wt_link_local.py: worktree links to local-only dirs"
# Kept out of the links: per-checkout state, secrets and folders other tooling owns (the `.claude`
# junctions belong to setup_agent_links.py and `.worktreeinclude`, which recreate them per checkout).
SKIP = re.compile(r"^(\.claude/|\.codegraph/|\.gsd/|\.bg-shell/|\.dotnet/|Library/|Logs/|Temp/|UserSettings/|"
                  r"Tools/Python/output/)", re.IGNORECASE)


def is_link(path):
    return os.path.islink(path) or (hasattr(os.path, "isjunction") and os.path.isjunction(path))


def git_output(args, cwd):
    """One git command's stdout, or None when it fails (a path git rejects counts as not a checkout)."""
    result = subprocess.run(["git", *args], cwd=cwd, capture_output=True)
    if result.returncode != 0:
        return None
    return result.stdout.decode("utf-8", "replace").strip()


def common_dir(path):
    """The git dir every worktree of a checkout shares; None when path is not inside a git checkout."""
    try:
        return git_output(["-C", path, "rev-parse", "--path-format=absolute", "--git-common-dir"], path)
    except OSError:
        return None  # git cannot even start in a folder that is not there


def local_only_paths(primary, exclude_path):
    """The primary's ignored-by-info/exclude paths, git collapsing an all-ignored folder to `name/`."""
    listing = git_output(["-C", primary, "ls-files", "--others", "--ignored", "--directory", "-z",
                          f"--exclude-from={exclude_path}"], primary)
    if listing is None:
        return []
    paths = []
    for raw in listing.split("\0"):
        if raw and not SKIP.match(raw.rstrip("/") + "/"):
            paths.append(raw.rstrip("/"))
    return paths


def covered_by_parent_link(destination, worktree):
    """True when a folder above destination is already a link, so this path needs no link of its own."""
    current = os.path.dirname(destination)
    root_norm = os.path.normcase(worktree)
    while os.path.normcase(current) != root_norm and len(current) > len(worktree):
        if is_link(current):
            return True
        parent = os.path.dirname(current)
        if parent == current:
            break
        current = parent
    return False


def make_dir_link(link, target):
    """A directory link: an NTFS junction (no admin rights needed) or a symlink on other platforms."""
    if os.name == "nt":
        _winapi.CreateJunction(target, link)
    else:
        os.symlink(target, link, target_is_directory=True)


def link_local(primary, worktree, exclude_path):
    """Link or copy every local-only path the worktree lacks; returns (linked, copied, dir paths)."""
    linked, copied, linked_dirs = 0, 0, []
    for rel in local_only_paths(primary, exclude_path):
        source = os.path.join(primary, *rel.split("/"))
        # A worktree folder inside the primary is itself an ignored path there, and linking it would
        # bury the worktree inside itself.
        if os.path.realpath(source) == worktree or os.path.realpath(source).startswith(worktree + os.sep):
            continue
        destination = os.path.join(worktree, *rel.split("/"))
        if not os.path.exists(source) or os.path.lexists(destination):
            continue
        if covered_by_parent_link(destination, worktree):
            continue
        os.makedirs(os.path.dirname(destination), exist_ok=True)
        if os.path.isdir(source):
            make_dir_link(destination, source)
            linked, linked_dirs = linked + 1, linked_dirs + [rel]
        else:
            try:
                os.link(source, destination)
                linked += 1
            except OSError:
                # Another volume: a hard link is impossible, and the copy is a snapshot that can go stale.
                shutil.copy2(source, destination)
                copied += 1
    return linked, copied, linked_dirs


def add_exclude_patterns(exclude_path, dirs):
    """Append the header plus a bare `/<rel>` per linked folder, once each.

    A trailing-slash `dir/` pattern needs git to read the junction as a directory, which it does not
    always do; the bare form ignores the folder either way. Written with `newline=""` so the file's
    existing line endings survive.
    """
    existing_text = ""
    if os.path.exists(exclude_path):
        with open(exclude_path, encoding="utf-8", errors="replace", newline="") as handle:
            existing_text = handle.read()
    known = set(existing_text.splitlines())
    missing = [line for line in [HEADER] + [f"/{name}" for name in dirs] if line not in known]
    if not missing:
        return
    newline = "\r\n" if "\r\n" in existing_text else "\n"
    prefix = "" if not existing_text or existing_text.endswith("\n") else newline
    with open(exclude_path, "a", encoding="utf-8", newline="") as handle:
        handle.write(prefix + newline.join(missing) + newline)


def unlink_local(worktree):
    """Drop every link the worktree holds — junctions, dir and file symlinks — never their targets.

    Junctions are pruned from the walk so their contents (the primary's real files) are never visited, and
    hard links need nothing: deleting a worktree deletes only that name.
    """
    removed = 0
    for directory, dir_names, file_names in os.walk(worktree):
        for name in list(dir_names):
            path = os.path.join(directory, name)
            if is_link(path):
                if hasattr(os.path, "isjunction") and os.path.isjunction(path):
                    os.rmdir(path)
                else:
                    os.unlink(path)
                removed += 1
                dir_names.remove(name)
        for name in list(file_names):
            path = os.path.join(directory, name)
            if os.path.islink(path):
                os.unlink(path)
                removed += 1
    return removed


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(
        description="Link the primary checkout's local-only files into a git worktree, or undo the links.")
    parser.add_argument("primary", nargs="?", help="the primary checkout the links come from")
    parser.add_argument("worktree", nargs="?", help="the worktree to link into (the path --unlink takes)")
    parser.add_argument("--unlink", action="store_true", help="remove the links, before removing the worktree")
    args = parser.parse_args()
    if args.unlink:
        if not args.primary or args.worktree:
            parser.error("--unlink takes one worktree path")
        worktree = os.path.realpath(args.primary)
        worktree_common = common_dir(worktree)
        if worktree_common is None:
            print(f"wt_link_local: {worktree} is not a git checkout, cannot unlink there")
            return 2
        if os.path.normcase(os.path.realpath(os.path.dirname(worktree_common))) == os.path.normcase(worktree):
            print(f"wt_link_local: {worktree} is the main checkout, not a worktree: nothing unlinked")
            return 2
        print(f"wt_link_local: unlinked {unlink_local(worktree)} links in {worktree}")
        return 0
    if not args.primary or not args.worktree:
        parser.error("needs the primary checkout and the worktree path")

    primary, worktree = os.path.realpath(args.primary), os.path.realpath(args.worktree)
    if primary == worktree:
        print(f"wt_link_local: {primary} is the same checkout, nothing linked")
        return 0
    primary_common = common_dir(primary)
    if primary_common is None:
        print(f"wt_link_local: {primary} is not a git checkout")
        return 2
    if common_dir(worktree) is None:
        print(f"wt_link_local: {worktree} is not a git checkout")
        return 2

    exclude_path = os.path.join(primary_common, "info", "exclude")
    linked, copied, linked_dirs = link_local(primary, worktree, exclude_path)
    add_exclude_patterns(exclude_path, linked_dirs)
    print(f"wt_link_local: linked {linked}, copied {copied} local-only paths into {worktree}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
