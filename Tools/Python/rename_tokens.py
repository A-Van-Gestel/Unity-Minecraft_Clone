"""Apply an explicit old->new identifier map across the repo, on word boundaries only.

WHAT THIS DOES
    Reads a tab-separated map file of `old<TAB>new` token pairs and rewrites every whole-word
    occurrence across .cs / .shader / .hlsl / .cginc / .compute / .md files. A token absent from
    the map is never touched. Dry-run by default; `--apply` is required to write.

WHY IT EXISTS (and why not sed / Rider)
    Rider's rename refactoring handles C# symbols correctly but reaches none of the surfaces that
    actually break in this engine: HLSL, markdown, string literals, and local variables in bulk.
    A pile of `sed` invocations covers those but has no shared exclusion policy, no dry run, and
    silently mangles CRLF. This tool is the middle ground -- built for LP-7's Sun->Sky sweep
    (~480 identifiers across ~55 files, 2026-08-25).

THE HAZARD THIS TOOL DOES NOT REMOVE
    A map of *pre-classified identifiers* is safe everywhere. A map containing *bare English
    words* (`sun` -> `sky`) is safe ONLY over code whose tokens you have classified first, and
    is actively dangerous over prose: in LP-7 it rewrote ~50 unrelated passages ("the sun rises
    due east" -> "the sky rises due east") before being caught. If your map contains a word that
    is also ordinary English, restrict `--paths` to code and review the result with:

        git diff --word-diff=plain --word-diff-regex='[A-Za-z_][A-Za-z0-9_]*'

    Renaming is not behavior, so no validation suite can witness a rename. The compiler gates C#;
    nothing gates HLSL varyings, string-bound shader globals (`Shader.PropertyToID`), Unity YAML
    serialized keys, or prose. Gate those yourself.

    A second prose case the exclusions cannot cover: a *rename record* names the old symbol on
    purpose. Running a map over one turns "`Old` -> `New`" into the tautology "`New` -> `New`" and
    destroys the history. LP-7 did exactly this to SMOOTH_AND_RGB_LIGHTING.md 3.8.2.2 and had to
    rewrite the section by hand. Write such records AFTER the sweep, or exclude their file.

DEFAULT EXCLUSIONS (historical records -- they describe what things were called *then*)
    Documentation/Bugs/_FIXED_BUGS.md, Documentation/Performance/, Documentation/Release Notes/,
    Documentation/Archived/. Override with --include-historical when the rename is genuinely
    retroactive. Anything else (celestial vs channel naming, migration steps frozen to an on-disk
    format) belongs in your map's design, not here -- just leave those tokens out of the map.

DETERMINISM
    Files are visited in sorted order and the report is sorted by hit count then path, so two runs
    over the same tree produce byte-identical output. Line endings are preserved exactly
    (newline="" on both read and write), so a CRLF file stays CRLF.

RUN
    python Tools/Python/rename_tokens.py <map_file>                  # dry run, prints the report
    python Tools/Python/rename_tokens.py <map_file> --apply          # writes
    python Tools/Python/rename_tokens.py <map_file> --paths Assets/Scripts --apply

    Map file format -- one pair per line, TAB separated; blank lines and #-comments ignored:
        RecalculateSunLightLight<TAB>RecalculateSkylight
        LightChannel.Sun<TAB>LightChannel.Sky
"""

import argparse
import re
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]

DEFAULT_PATHS = ("Assets/Scripts", "Assets/Editor", "Assets/Shaders", "Documentation", ".agents")
EXTENSIONS = {".cs", ".shader", ".hlsl", ".cginc", ".compute", ".md"}

HISTORICAL = (
    "Documentation/Bugs/_FIXED_BUGS.md",
    "Documentation/Performance/",
    "Documentation/Release Notes/",
    "Documentation/Archived/",
)


def load_map(path):
    """Parses a `old<TAB>new` map file into pairs sorted longest-first.

    Longest-first matters: it stops a short token from shadowing a longer one that contains it.
    Duplicate sources are rejected -- two rules for one token make the result order-dependent.
    """
    pairs, seen = [], {}
    for lineno, raw in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        if "\t" not in line:
            raise SystemExit(f"{path}:{lineno}: expected 'old<TAB>new', got: {line!r}")
        old, new = line.split("\t", 1)
        old, new = old.strip(), new.strip()
        if not old or not new:
            raise SystemExit(f"{path}:{lineno}: empty side in mapping")
        if old in seen:
            raise SystemExit(f"{path}:{lineno}: duplicate source token {old!r} (first at line {seen[old]})")
        seen[old] = lineno
        pairs.append((old, new))
    if not pairs:
        raise SystemExit(f"{path}: no mappings found")
    pairs.sort(key=lambda p: -len(p[0]))
    return pairs


def build_pattern(pairs):
    """Builds one alternation guarded by non-word lookarounds.

    `\\b` is wrong here: a token may legitimately start or end with a non-word character (e.g.
    `_skyLightOverDay`, `LightChannel.Sun`), and \\b's behaviour flips depending on that character.
    Explicit lookarounds for [A-Za-z0-9_] give the same answer regardless of the token's edges.
    """
    body = "|".join(re.escape(old) for old, _ in pairs)
    return re.compile(r"(?<![A-Za-z0-9_])(" + body + r")(?![A-Za-z0-9_])")


def iter_files(root, rel_paths, include_historical):
    """Yields every candidate file under the given roots, sorted, minus excluded records."""
    for rel in rel_paths:
        base = root / rel
        if not base.is_dir():
            continue
        for path in sorted(base.rglob("*")):
            if path.suffix not in EXTENSIONS or not path.is_file():
                continue
            posix = path.relative_to(root).as_posix()
            if not include_historical and any(posix.startswith(h) for h in HISTORICAL):
                continue
            yield path, posix


def run(map_file, root, rel_paths, include_historical, apply):
    pairs = load_map(map_file)
    pattern = build_pattern(pairs)
    table = dict(pairs)

    total, touched, skipped = 0, [], []
    for path, posix in iter_files(root, rel_paths, include_historical):
        try:
            text = path.read_text(encoding="utf-8", newline="")
        except UnicodeDecodeError:
            skipped.append(posix)
            continue
        new_text, hits = pattern.subn(lambda m: table[m.group(1)], text)
        if not hits:
            continue
        total += hits
        touched.append((hits, posix))
        if apply:
            path.write_text(new_text, encoding="utf-8", newline="")

    for hits, posix in sorted(touched, key=lambda t: (-t[0], t[1])):
        print(f"{hits:6d}  {posix}")
    for posix in sorted(skipped):
        print(f"  SKIP (not utf-8): {posix}")

    mode = "APPLIED" if apply else "DRY RUN (pass --apply to write)"
    print(f"\n{mode}: {total} replacement(s) across {len(touched)} file(s), {len(pairs)} mapping(s)")
    return 0 if total or not apply else 1


def main(argv=None):
    parser = argparse.ArgumentParser(
        description="Apply a word-boundary identifier rename map across the repo.",
        epilog="Dry-run by default. See the module docstring for the prose hazard.",
    )
    parser.add_argument("map_file", type=Path, help="tab-separated old<TAB>new mapping file")
    parser.add_argument("--apply", action="store_true", help="write changes (default: dry run)")
    parser.add_argument("--root", type=Path, default=REPO_ROOT, help="repo root (default: inferred from this script)")
    parser.add_argument("--paths", nargs="+", default=list(DEFAULT_PATHS),
                        help=f"repo-relative roots to scan (default: {' '.join(DEFAULT_PATHS)})")
    parser.add_argument("--include-historical", action="store_true",
                        help="also rewrite _FIXED_BUGS.md, Performance/, Release Notes/, Archived/")
    args = parser.parse_args(argv)

    if not args.map_file.is_file():
        raise SystemExit(f"map file not found: {args.map_file}")
    return run(args.map_file, args.root.resolve(), args.paths, args.include_historical, args.apply)


if __name__ == "__main__":
    sys.exit(main())
