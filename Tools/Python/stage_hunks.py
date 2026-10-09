"""Stage chosen hunks of one file's unstaged diff (`git add -p` is unavailable here).

HOW
    `--list` prints the file's hunks (from `git diff -U3`, color forced off) numbered from 1, with
    their header and changed lines. `--hunks 1,3` writes the file header plus exactly those hunks
    to a patch, checks it with `git apply --cached --check`, applies it to the index, and prints the
    staged diff of that file so the result is verified rather than assumed.

    Hunk numbers are only valid for the diff they were listed from: list, then stage, with no edit
    to the file in between. Staging changes the remaining diff, so re-list before a second pick.
    The diff is handled as bytes and split on LF only, so CRLF endings and non-UTF-8 bytes survive.

LIMITS
    Tracked text files only. An untracked, binary or renamed file is staged whole with `git add`.

READS   the working tree and index. WRITES the index only.

RUN
    python Tools/Python/stage_hunks.py <file> --list
    python Tools/Python/stage_hunks.py <file> --hunks 1,3

EXIT CODES
    0  listed, or staged and verified
    1  the patch did not apply (nothing staged)
    2  bad arguments: no diff, unknown hunk number, binary file
"""
import argparse
import os
import subprocess
import sys
import tempfile

PREVIEW_LINES = 6
LF = b"\n"


# Pinned so user config cannot change the patch format: color codes, a/ b/ prefixes, external diff.
GIT_DIFF_CONFIG = ["-c", "color.ui=never", "-c", "diff.noprefix=false", "-c", "diff.mnemonicPrefix=false",
                   "-c", "diff.srcPrefix=a/", "-c", "diff.dstPrefix=b/", "-c", "diff.relative=false"]


def git(*args):
    return subprocess.run(["git", *GIT_DIFF_CONFIG, *args], capture_output=True)


def split_hunks(diff_bytes):
    lines = [line + LF for line in diff_bytes.split(LF)]
    lines[-1] = lines[-1][:-1]
    first = next((i for i, line in enumerate(lines) if line.startswith(b"@@")), None)
    if first is None:
        return None, []
    header, hunks = b"".join(lines[:first]), []
    for line in lines[first:]:
        if line.startswith(b"@@"):
            hunks.append([line])
        else:
            hunks[-1].append(line)
    return header, [b"".join(hunk) for hunk in hunks]


def print_hunks(hunks):
    for number, hunk in enumerate(hunks, 1):
        text_lines = [line.rstrip("\r") for line in hunk.decode("utf-8", "replace").split("\n")]
        changed = [line for line in text_lines[1:] if line[:1] in ("+", "-")]
        print(f"[{number}] {text_lines[0]}")
        for line in changed[:PREVIEW_LINES]:
            print(f"      {line[:150]}")
        if len(changed) > PREVIEW_LINES:
            print(f"      … {len(changed) - PREVIEW_LINES} more changed line(s)")


def apply_cached(patch):
    with tempfile.NamedTemporaryFile("wb", suffix=".patch", delete=False) as handle:
        handle.write(patch)
        patch_path = handle.name
    try:
        for extra in (["--check"], []):
            result = git("apply", "--cached", *extra, patch_path)
            if result.returncode != 0:
                return " ".join(["git apply --cached", *extra]) + " refused the patch:\n" + \
                    result.stderr.decode("utf-8", "replace").strip()
    finally:
        os.unlink(patch_path)
    return None


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description="Stage chosen hunks of one file.")
    parser.add_argument("file")
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument("--list", action="store_true", help="print the numbered hunks")
    group.add_argument("--hunks", help="comma-separated 1-based hunk numbers to stage")
    args = parser.parse_args()

    diff = git("diff", "--no-ext-diff", "--no-color", "-U3", "--", args.file).stdout
    header, hunks = split_hunks(diff)
    # Only the header (before any @@) can say "Binary files"; a hunk line may quote the phrase.
    if header is None and any(line.startswith(b"Binary files ") for line in diff.split(LF)):
        print("REFUSED: binary file; stage it whole with git add")
        return 2
    if not hunks:
        print(f"REFUSED: no unstaged diff for {args.file}")
        return 2

    if args.list:
        print_hunks(hunks)
        return 0

    try:
        chosen = sorted({int(part) for part in args.hunks.split(",") if part.strip()})
    except ValueError:
        print(f"REFUSED: --hunks must be numbers, got '{args.hunks}'")
        return 2
    unknown = [n for n in chosen if not 1 <= n <= len(hunks)]
    if unknown or not chosen:
        print(f"REFUSED: hunk number(s) {unknown or '(none)'} outside 1..{len(hunks)}")
        return 2

    error = apply_cached(header + b"".join(hunks[n - 1] for n in chosen))
    if error:
        print(f"FAILED: {error}\nNothing staged.")
        return 1
    print(f"Staged hunk(s) {', '.join(map(str, chosen))} of {len(hunks)}. Staged diff of {args.file}:")
    print(git("diff", "--no-ext-diff", "--no-color", "--cached", "--", args.file).stdout.decode("utf-8", "replace").rstrip())
    return 0


if __name__ == "__main__":
    sys.exit(main())
