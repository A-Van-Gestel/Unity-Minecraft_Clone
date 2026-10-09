"""Audit Unity YAML assets for lost `guid:` references between two versions.

WHAT THIS GUARDS
    A bulk reserialization (Force Reserialize All Assets, a Unity upgrade, an importer bump) can
    silently NULL a [SerializeField] asset reference: `field: {fileID: N, guid: ..., type: 2}`
    becomes `field: {fileID: 0}`. In a diff summary that is indistinguishable from harmless format
    churn, so this script counts every `guid:` reference per file, base vs head, and flags any
    guid whose count DROPPED. Counting (rather than matching fields) also catches losses inside
    arrays. A `.meta` file whose own `guid:` changed shows up the same way.

SCOPE
    Every changed file that is a `.meta` or whose base version starts with `%YAML` (scenes,
    prefabs, ScriptableObjects, materials, ...). Files added in head have no base and are skipped;
    deleted files are reported as deleted, not as losses; a renamed (moved) file is compared old
    path in base against new path in head, so a move cannot hide a lost reference. A rename pair is
    trusted only when the asset kept its own `.meta` guid (a Unity move always does): git also pairs an
    unrelated delete + add whose content is similar, such as two script `.meta` files, and those are
    reported as a deletion instead. A `.meta` that vanishes (deleted, or regenerated with a new guid
    by a move outside Unity) is a failure when any file in head still references its guid.

MODES
    Git mode (default): files changed between --base and --head (working tree when --head is
    omitted, so staged and unstaged edits both count).
    Pair mode (--compare OLD NEW): two files on disk; used to prove the detector red.

READS   git objects and working-tree files. WRITES nothing.

RUN
    python Tools/Python/audit_reserialize_guids.py                       # HEAD vs working tree
    python Tools/Python/audit_reserialize_guids.py --base HEAD~3 --head HEAD   # a commit range
    python Tools/Python/audit_reserialize_guids.py --compare old.prefab new.prefab

EXIT CODES
    0  no guid reference lost
    1  at least one file lost a guid reference, or a vanished .meta's guid is still referenced
    2  git or file error
"""
import argparse
import os
import re
import subprocess
import sys
from collections import Counter

GUID_PATTERN = re.compile(r"guid:\s*([0-9a-f]{32})")
OWN_GUID_PATTERN = re.compile(r"^guid:\s*([0-9a-f]{32})", re.MULTILINE)
YAML_HEADER = "%YAML"


def run_git(repo_root, *args):
    result = subprocess.run(["git", "-c", "color.ui=never", *args], cwd=repo_root,
                            capture_output=True)
    if result.returncode != 0:
        raise RuntimeError(result.stderr.decode("utf-8", "replace").strip())
    return result.stdout


def read_version(repo_root, rev, path):
    """Returns the file text at `rev`, the working-tree text when rev is None, or None if absent."""
    if rev is None:
        full_path = os.path.join(repo_root, path)
        if not os.path.exists(full_path):
            return None
        with open(full_path, "rb") as handle:
            return handle.read().decode("utf-8", "replace")
    try:
        return run_git(repo_root, "show", f"{rev}:{path}").decode("utf-8", "replace")
    except RuntimeError:
        return None


def lost_guids(old_text, new_text):
    old_counts = Counter(GUID_PATTERN.findall(old_text))
    new_counts = Counter(GUID_PATTERN.findall(new_text))
    return {guid: (count, new_counts.get(guid, 0))
            for guid, count in old_counts.items() if new_counts.get(guid, 0) < count}


def own_guid(repo_root, rev, path):
    """The asset's own guid from its `.meta` (the file itself when it is one), or None."""
    meta_path = path if path.endswith(".meta") else path + ".meta"
    text = read_version(repo_root, rev, meta_path)
    match = OWN_GUID_PATTERN.search(text) if text else None
    return match.group(1) if match else None


def is_same_asset(repo_root, base, head, old_path, new_path):
    if old_path == new_path:
        return True
    old_guid = own_guid(repo_root, base, old_path)
    return old_guid is not None and old_guid == own_guid(repo_root, head, new_path)


def dangling_references(repo_root, head, old_meta_path, old_meta_text):
    """Files in head that still reference a vanished `.meta`'s own guid (empty when the asset lives on
    under another `.meta`, e.g. a move git did not pair). A deleted script a scene still uses, or an
    asset whose `.meta` was regenerated with a new guid, both land here."""
    if not old_meta_path.endswith(".meta"):
        return []
    match = OWN_GUID_PATTERN.search(old_meta_text)
    if not match:
        return []
    guid = match.group(1)
    where = [head] if head else ["--untracked"]
    result = subprocess.run(["git", "grep", "-l", "-F", "-e", guid, *where, "--"], cwd=repo_root,
                            capture_output=True)
    if result.returncode not in (0, 1):
        raise RuntimeError(result.stderr.decode("utf-8", "replace").strip())
    prefix = f"{head}:" if head else ""
    hits = [line[len(prefix):] for line in result.stdout.decode("utf-8", "replace").splitlines()]
    for path in hits:
        if path.endswith(".meta") and own_guid(repo_root, head, path) == guid:
            return []
    return [path for path in hits if not path.endswith(".meta")]


def report(path, losses):
    print(f"LOST  {path}")
    for guid, (old_count, new_count) in sorted(losses.items()):
        print(f"      guid {guid}: {old_count} -> {new_count}")


def audit_pair(old_path, new_path):
    try:
        with open(old_path, "rb") as old_handle, open(new_path, "rb") as new_handle:
            old_text = old_handle.read().decode("utf-8", "replace")
            new_text = new_handle.read().decode("utf-8", "replace")
    except OSError as error:
        print(f"error: {error}", file=sys.stderr)
        return 2
    losses = lost_guids(old_text, new_text)
    if losses:
        report(new_path, losses)
        return 1
    print(f"OK    {new_path}: no guid reference lost")
    return 0


def changed_pairs(name_status):
    """(base path, head path) per changed file; a rename pairs the old path with the new one, so
    a reference lost while an asset was moved is still compared against its pre-move version."""
    fields = [f for f in name_status.split("\0") if f]
    pairs, index = [], 0
    while index < len(fields):
        status = fields[index]
        if status[0] in "RC":
            pairs.append((fields[index + 1], fields[index + 2]))
            index += 3
        else:
            pairs.append((fields[index + 1], fields[index + 1]))
            index += 2
    return pairs


def audit_git(base, head):
    try:
        repo_root = run_git(".", "rev-parse", "--show-toplevel").decode().strip()
        diff_args = ["diff", "--name-status", "-z", "-M", "--no-ext-diff", "--no-color", base] + ([head] if head else []) + ["--"]
        changed = changed_pairs(run_git(repo_root, *diff_args).decode("utf-8"))
    except RuntimeError as error:
        print(f"error: {error}", file=sys.stderr)
        return 2

    # A Unity move carries the `.meta` unchanged, so git always pairs it, but it may not pair the asset
    # itself when the move also rewrote it heavily. Follow the `.meta` to re-pair the asset.
    moved_assets = {old[:-len(".meta")]: new[:-len(".meta")] for old, new in changed
                    if old != new and old.endswith(".meta") and new.endswith(".meta")
                    and is_same_asset(repo_root, base, head, old, new)}
    changed = [(old, moved_assets.get(old, new)) if old == new else (old, new) for old, new in changed]

    audited = lost_files = 0
    for old_path, new_path in changed:
        old_text = read_version(repo_root, base, old_path)
        if old_text is None:
            continue
        if not (old_path.endswith(".meta") or old_text.startswith(YAML_HEADER)):
            continue
        new_text = read_version(repo_root, head, new_path)
        if new_text is None or not is_same_asset(repo_root, base, head, old_path, new_path):
            print(f"DEL   {old_path}")
            dangling = dangling_references(repo_root, head, old_path, old_text)
            if dangling:
                lost_files += 1
                print(f"DANGLING {old_path}: its guid is gone from head but still referenced by")
                for path in dangling[:10]:
                    print(f"      {path}")
            continue
        audited += 1
        losses = lost_guids(old_text, new_text)
        if losses:
            lost_files += 1
            report(new_path if new_path == old_path else f"{old_path} -> {new_path}", losses)

    head_label = head or "working tree"
    print(f"Audited {audited} Unity YAML/.meta file(s) of {len(changed)} changed, "
          f"{base} -> {head_label}: {lost_files} with lost guid references.")
    return 1 if lost_files else 0


def main():
    parser = argparse.ArgumentParser(description="Flag Unity YAML files that lost guid references.")
    parser.add_argument("--base", default="HEAD", help="base revision (default: HEAD)")
    parser.add_argument("--head", default=None, help="head revision (default: working tree)")
    parser.add_argument("--compare", nargs=2, metavar=("OLD", "NEW"),
                        help="compare two files on disk instead of git versions")
    args = parser.parse_args()
    if args.compare:
        return audit_pair(*args.compare)
    return audit_git(args.base, args.head)


if __name__ == "__main__":
    sys.exit(main())
