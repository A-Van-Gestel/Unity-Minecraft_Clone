"""Fold changes into older, UNPUSHED commits with fixup commits and a gated autosquash.

WHY
    `git rebase -i` cannot run here (no interactive editor), so "this belongs in the commit three
    back" used to be a hand-run detach / amend / cherry-pick replay. Git's own mechanism does the
    same job in two steps and keeps every intermediate hash stable:

        fixup   commits the change as `fixup! <full target sha>` — addressed by hash, not by subject,
                so two commits sharing a subject (repeated re-serialization commits) cannot swap.
                Run it once per target; nothing is rewritten yet, so later targets keep their hashes.
        squash  one `git rebase --autosquash --autostash` folds every `fixup!` commit into its
                target, with the editor forced off (core.editor / sequence.editor overridden — the
                repo's core.editor would otherwise open Notepad++).

SAFETY
    * Targets must be in the unpushed range (`<upstream>..HEAD`; upstream = @{u}, else
      origin/<branch>). A pushed target is refused.
    * `fixup` with paths refuses when the index already holds staged changes outside those paths,
      so nothing unrelated rides along. Without paths it commits exactly what is staged (pair it
      with stage_hunks.py to fold part of a file).
    * A given path that is staged AND has further unstaged edits is refused (staging it again would
      replace a hunk selection with the whole file) unless `--whole-file` says that is intended.
    * Untracked files are folded only when named exactly: a directory path never sweeps up new,
      unrelated files under it.
    * `squash` resolves each `fixup!` by hash; a subject-form fixup (made by plain
      `git commit --fixup`) is accepted only when exactly one unpushed commit has that subject.
    * `squash` writes a backup branch `fold-backup-<sha>` first. A failing rebase (a conflict) is
      aborted, which restores HEAD exactly; the fixup commits stay in place.
    * After a successful rebase four gates must hold: the final tree equals the pre-rebase tree,
      the commit count dropped by exactly the number of fixups, no `fixup!` subject remains, and
      `git status` (working tree + untracked) is unchanged. A failed gate resets the branch to the
      pre-rebase commit (mixed reset: working-tree files are not touched) and exits 1.

READS / WRITES   commits and refs of the current branch; the working tree only through
                 --autostash, which puts it back.

RUN
    python Tools/Python/fold_into_commit.py fixup <target-rev> [<path> ...] [--whole-file]
    python Tools/Python/fold_into_commit.py squash
    python Tools/Python/fold_into_commit.py squash --keep-backup    # keep the backup branch

EXIT CODES
    0  done (every gate held)
    1  rebase conflict (aborted, nothing changed) or a failed gate (branch reset, see output)
    2  refused before changing anything (pushed target, stray staged changes, nothing to do, ...)
"""
import argparse
import os
import re
import subprocess
import sys

FIXUP_PREFIX = "fixup! "
SHA_PATTERN = re.compile(r"[0-9a-f]{7,40}")
NO_EDITOR = ["-c", "core.editor=false", "-c", "sequence.editor=:", "-c", "color.ui=never",
             "-c", "diff.relative=false"]


class Refused(Exception):
    pass


def git(*args, check=True):
    result = subprocess.run(["git", *NO_EDITOR, *args], capture_output=True)
    out = result.stdout.decode("utf-8", "replace")
    if check and result.returncode != 0:
        raise Refused(f"git {' '.join(args)} failed: {result.stderr.decode('utf-8', 'replace').strip()}")
    return result.returncode, out


def rev(name):
    return git("rev-parse", "--verify", "--quiet", f"{name}^{{commit}}", check=False)[1].strip()


def upstream_ref():
    upstream = git("rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}", check=False)[1].strip()
    if upstream and rev(upstream):
        return upstream
    branch = git("branch", "--show-current")[1].strip()
    fallback = f"origin/{branch}"
    if branch and rev(fallback):
        return fallback
    raise Refused("no upstream (@{u} or origin/<branch>) to tell pushed from unpushed commits")


def unpushed_range(upstream):
    """Unpushed commits, oldest first, as (sha, subject)."""
    out = git("log", "--reverse", "--format=%H%x00%s", f"{upstream}..HEAD")[1]
    return [tuple(line.split("\0", 1)) for line in out.splitlines() if line]


def staged_paths():
    return set(p for p in git("diff", "--cached", "--name-only", "-z")[1].split("\0") if p)


def unstaged_paths():
    return set(p for p in git("diff", "--name-only", "-z")[1].split("\0") if p)


def repo_relative(paths):
    """Command-line paths (relative to cwd, `./x`, absolute, or a directory) as repo-root-relative
    POSIX paths, the form `git diff --name-only` reports."""
    top = git("rev-parse", "--show-toplevel")[1].strip()
    return [os.path.relpath(os.path.abspath(p), top).replace("\\", "/") for p in paths]


def is_covered(path, given):
    return any(g == "." or path == g or path.startswith(g + "/") for g in given)


def untracked_paths():
    return set(p for p in git("ls-files", "--others", "--exclude-standard", "-z", ":(top)")[1].split("\0") if p)


def command_fixup(target, paths, whole_file=False):
    upstream = upstream_ref()
    target_sha = rev(target)
    if not target_sha:
        raise Refused(f"'{target}' is not a commit")
    unpushed = {sha for sha, _ in unpushed_range(upstream)}
    if target_sha not in unpushed:
        raise Refused(f"{target_sha[:10]} is not in the unpushed range {upstream}..HEAD")

    if paths:
        given = repo_relative(paths)
        staged = staged_paths()
        stray = {p for p in staged if not is_covered(p, given)}
        if stray:
            raise Refused("the index already holds staged changes outside the given paths: "
                          + ", ".join(sorted(stray)))
        # `git add` would stage the whole file and discard a stage_hunks.py selection.
        partial = {p for p in staged & unstaged_paths() if is_covered(p, given)}
        if partial and not whole_file:
            raise Refused("staged and also edited since — omit the paths to fold exactly what is staged (a "
                          "stage_hunks.py selection), or pass --whole-file to fold every change: "
                          + ", ".join(sorted(partial)))
        swept = {p for p in untracked_paths() if is_covered(p, given) and p not in given}
        if swept:
            raise Refused("untracked files under the given directories would be folded too; name the ones "
                          "that belong explicitly: " + ", ".join(sorted(swept)))
        git("add", "--", *(f":(top){p}" for p in given))  # `given` is root-relative; cwd may be a subdirectory
    if not staged_paths():
        raise Refused("nothing staged to fold")

    git("commit", "--quiet", "-m", f"{FIXUP_PREFIX}{target_sha}")
    print(git("log", "-1", "--format=%h %s")[1].strip())
    print(git("diff", "--stat", "HEAD~1", "HEAD")[1].rstrip())
    return 0


def status_snapshot():
    return git("status", "--porcelain=v1", "-z", "--untracked-files=all")[1]


def command_squash(keep_backup):
    upstream = upstream_ref()
    if staged_paths():
        # --autostash re-applies staged changes as unstaged; commit or unstage them first.
        raise Refused("the index holds staged changes; commit or unstage them before squashing")
    commits = unpushed_range(upstream)
    fixups =[(sha, subject) for sha, subject in commits if subject.startswith(FIXUP_PREFIX)]
    if not fixups:
        raise Refused(f"no fixup! commits in {upstream}..HEAD")

    targets = []
    for sha, subject in fixups:
        wanted = subject[len(FIXUP_PREFIX):].strip()
        earlier = commits[:[c[0] for c in commits].index(sha)]
        if SHA_PATTERN.fullmatch(wanted):
            match = [c for c in earlier if c[0].startswith(wanted)]
        else:
            match = [c for c in earlier if c[1] == wanted]
            if len(match) > 1:
                raise Refused(f"fixup {sha[:10]} names subject '{wanted}', which {len(match)} unpushed "
                              f"commits share; redo it with `fold_into_commit.py fixup <sha>`")
        if not match:
            raise Refused(f"fixup {sha[:10]} targets '{wanted}', which is not an earlier unpushed commit")
        targets.append(match[-1][0])
    oldest_target = next(sha for sha, _ in commits if sha in targets)

    pre_tip = rev("HEAD")
    pre_count = len(commits)
    pre_status = status_snapshot()
    backup = f"fold-backup-{pre_tip[:10]}"
    git("branch", "-f", backup, pre_tip)

    has_parent = bool(rev(f"{oldest_target}^"))
    base_args = [f"{oldest_target}^"] if has_parent else ["--root"]
    code, _ = git("rebase", "--quiet", "--autosquash", "--autostash", *base_args, check=False)
    if code != 0:
        git("rebase", "--abort", check=False)
        restored = rev("HEAD") == pre_tip
        print(f"CONFLICT: the autosquash rebase failed and was aborted; HEAD "
              f"{'is back at' if restored else 'did NOT return to'} {pre_tip[:10]}. "
              f"The fixup commits are still in place. Backup branch: {backup}")
        return 1

    failures = []
    if git("diff", "--quiet", pre_tip, "HEAD", check=False)[0] != 0:
        failures.append("final tree differs from the pre-rebase tree")
    after = unpushed_range(upstream)
    if len(after) != pre_count - len(fixups):
        failures.append(f"commit count {len(after)}, expected {pre_count - len(fixups)}")
    if any(subject.startswith(FIXUP_PREFIX) for _, subject in after):
        failures.append("a fixup! commit survived the rebase")
    if status_snapshot() != pre_status:
        failures.append("git status (working tree / untracked) changed")

    if failures:
        git("reset", "--quiet", "--mixed", pre_tip, check=False)
        print("GATE FAILED: " + "; ".join(failures))
        print(f"Branch reset to {pre_tip[:10]} (working-tree files untouched; staged/unstaged "
              f"split may need redoing). Backup branch: {backup}")
        return 1

    print(f"Folded {len(fixups)} fixup(s); {upstream}..HEAD is now:")
    print(git("log", "--format=%h %s", f"{upstream}..HEAD")[1].rstrip())
    print("Gates: tree identical, commit count, no fixup! left, status unchanged — all held.")
    if keep_backup:
        print(f"Backup branch kept: {backup}")
    else:
        git("branch", "-D", backup)
    return 0


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description="Fold changes into unpushed commits via fixup + autosquash.")
    sub = parser.add_subparsers(dest="command", required=True)
    fixup = sub.add_parser("fixup", help="commit staged changes (or the given paths) as fixup! <target>")
    fixup.add_argument("target")
    fixup.add_argument("paths", nargs="*")
    fixup.add_argument("--whole-file", action="store_true",
                       help="fold every change in the given paths even when part of them is already staged")
    squash = sub.add_parser("squash", help="fold every fixup! commit into its target, gated")
    squash.add_argument("--keep-backup", action="store_true")
    args = parser.parse_args()
    try:
        if args.command == "fixup":
            return command_fixup(args.target, args.paths, args.whole_file)
        return command_squash(args.keep_backup)
    except Refused as error:
        print(f"REFUSED: {error}")
        return 2


if __name__ == "__main__":
    sys.exit(main())
