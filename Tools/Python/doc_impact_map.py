"""Map changed paths to their candidate owning docs, read live from the docs-sync skill's Step 1 table.

WHAT IT IS FOR
    The doc-impact check of docs-sync Step 1, done mechanically: every changed path is checked against
    the skill's "Code area -> primary doc" table and its "deliberately no owning doc" list, so no path
    is skipped. It proposes candidates only; whether a row's subject really applies is docs-sync
    Step 2, the caller's call.

HOW IT MATCHES
    Only backticked tokens containing '/' in a table row's left column match: a token ending in '/'
    is a folder prefix, one with '*' a glob, anything else an exact path. Bare names (`ChunkData`)
    are prose for the reader. In the no-doc bullets a bare file name (`CLAUDE.md`) matches too. A
    path a table row matches is never also reported as no-doc. `.meta` files are ignored.

READS   the skill file and git. WRITES nothing.

RUN
    python Tools/Python/doc_impact_map.py                      # working tree vs HEAD + untracked
    python Tools/Python/doc_impact_map.py --staged
    python Tools/Python/doc_impact_map.py --range A..B
    python Tools/Python/doc_impact_map.py --paths Assets/Scripts/World.cs CLAUDE.md
    --repo DIR picks the repository (default: the current one); --skill FILE overrides the table.

EXIT CODES
    0  report printed
    1  usage or git error
    2  the table did not parse (no rows, or no row for Assets/Scripts/World.cs): fix the parser,
       never read an empty report as "no docs affected"
    3  report printed, but a map token matches no tracked file or a mapped doc is missing: the
       table has gone stale and that row maps nothing
"""
import argparse
import fnmatch
import os
import re
import subprocess
import sys
from collections import OrderedDict

STEP1_HEADING = "### Step 1"
NO_DOC_LEAD = "**Where there is deliberately no owning doc.**"
TABLE_HEADER = "Code area"
DOCS_ROOT = "Documentation/"
# A map that no longer matches the chunk lifecycle's core file has not really been parsed.
SENTINEL_PATH = "Assets/Scripts/World.cs"
BACKTICK = re.compile(r"`([^`]+)`")
FILE_NAME = re.compile(r"^[\w.-]+\.[A-Za-z0-9]{1,6}$")


class Row:
    def __init__(self, line, left, right):
        self.line, self.left, self.right = line, left, right
        self.patterns = [t for t in BACKTICK.findall(left) if is_path_pattern(t)]
        self.docs = [t for t in BACKTICK.findall(right) if t.endswith((".md", "/"))]


def is_path_pattern(token):
    return "/" in token and " " not in token


def matches(pattern, path):
    if "*" in pattern:
        return fnmatch.fnmatchcase(path, pattern)
    if pattern.endswith("/"):
        return path.startswith(pattern)
    return path == pattern


def step1_lines(text):
    """Yields (1-based line number, line) for the Step 1 section only."""
    inside = False
    for number, line in enumerate(text.splitlines(), 1):
        if line.startswith(("## ", "### ")):
            inside = line.startswith(STEP1_HEADING)
            continue
        if inside:
            yield number, line


def parse_rows(text):
    rows = []
    for number, line in step1_lines(text):
        if not line.startswith("|"):
            continue
        cells = [c.strip() for c in line.strip().strip("|").split("|")]
        if len(cells) != 2 or set(cells[0]) <= set("-: ") or cells[0] == TABLE_HEADER:
            continue
        rows.append(Row(number, cells[0], cells[1]))
    return rows


def parse_no_doc(text):
    """Returns [(line, pattern)] from the bullets under the deliberately-no-doc paragraph."""
    bullets, inside, started = [], False, False
    for number, line in step1_lines(text):
        if line.startswith(NO_DOC_LEAD):
            inside = True
            continue
        if not inside:
            continue
        if line.startswith("- "):
            started = True
            bullets.append([number, line[2:]])
        elif started and line.startswith("  "):
            bullets[-1][1] += " " + line.strip()
        elif started and line.strip():
            break
    patterns = []
    for number, body in bullets:
        for token in BACKTICK.findall(body):
            if is_path_pattern(token) or FILE_NAME.match(token):
                patterns.append((number, token))
    return patterns


def git(repo, *args):
    result = subprocess.run(["git", "-c", "core.quotePath=false", "-c", "diff.relative=false", *args],
                            cwd=repo, capture_output=True)
    if result.returncode != 0:
        raise RuntimeError(f"git {' '.join(args)}: {result.stderr.decode('utf-8', 'replace').strip()}")
    return result.stdout.decode("utf-8", "replace")


def changed_paths(repo, range_=None, staged=False):
    if range_:
        out = git(repo, "diff", "--name-only", "--no-renames", range_)
    elif staged:
        out = git(repo, "diff", "--name-only", "--no-renames", "--cached")
    else:
        out = git(repo, "diff", "--name-only", "--no-renames", "HEAD")
        out += git(repo, "ls-files", "--others", "--exclude-standard")
    return sorted({p for p in out.splitlines() if p.strip()})


def doc_path(doc):
    return doc if doc.startswith(DOCS_ROOT) else DOCS_ROOT + doc


def by_folder(paths):
    """One line per folder: `dir/ (n): first names, ...`."""
    folders = OrderedDict()
    for path in paths:
        folders.setdefault(os.path.dirname(path) + "/" if "/" in path else "./", []).append(path)
    return [f"{folder} ({len(items)}): " + ", ".join(os.path.basename(p) for p in items[:4])
            + (", ..." if len(items) > 4 else "") for folder, items in folders.items()]


def stale_entries(repo, rows, no_doc, tracked):
    """Map tokens that match no tracked file, and mapped docs that do not exist."""
    stale = []
    for row in rows:
        stale += [f"L{row.line}: `{p}` matches no tracked file" for p in row.patterns
                  if not any(matches(p, t) for t in tracked)]
        stale += [f"L{row.line}: doc `{d}` does not exist" for d in row.docs
                  if not os.path.exists(os.path.join(repo, *doc_path(d).split("/")))]
    stale += [f"L{line}: `{p}` matches no tracked file" for line, p in no_doc
              if not any(matches(p, t) for t in tracked)]
    return stale


def build_report(skill, rows, no_doc, paths, stale):
    paths = [p for p in paths if not p.endswith(".meta")]
    docs_changed = [p for p in paths if p.startswith(DOCS_ROOT)]
    code_paths = [p for p in paths if not p.startswith(DOCS_ROOT)]
    out = [f"Doc-impact map: {len(code_paths)} changed paths (+{len(docs_changed)} docs) against {skill}",
           f"(parsed {len(rows)} map rows, {len(no_doc)} no-doc patterns)", ""]

    hit_rows, mapped = OrderedDict(), set()
    for row in rows:
        hits = [p for p in code_paths if any(matches(pat, p) for pat in row.patterns)]
        if hits:
            hit_rows[row] = hits
            mapped.update(hits)
    out.append("## Candidate docs (a path matched the row)")
    if not hit_rows:
        out.append("  none")
    for row, hits in hit_rows.items():
        docs = ", ".join(d + (" [CHANGED]" if doc_path(d) in docs_changed else "") for d in row.docs)
        out.append(f"- L{row.line}: {docs or row.right}")
        out += [f"    {line}" for line in by_folder(hits)]

    no_doc_hits = OrderedDict()
    for path in code_paths:
        if path in mapped:
            continue
        for line, pattern in no_doc:
            if matches(pattern, path):
                no_doc_hits.setdefault(f"L{line}: {pattern}", []).append(path)
                mapped.add(path)
                break
    out += ["", "## Deliberately no doc (the no-doc list names it)"]
    out += [f"- {key} ({len(v)} paths)" for key, v in no_doc_hits.items()] or ["  none"]

    out += ["", "## Docs changed in this diff"]
    out += [f"- {p}" for p in docs_changed] or ["  none"]

    out += ["", "## Unmapped (no row and no no-doc entry names it: judge it, or add a row)"]
    out += [f"- {line}" for line in by_folder([p for p in code_paths if p not in mapped])] or ["  none"]

    out += ["", "## Rows to judge by subject (they name no path)"]
    out += [f"- L{row.line}: {row.left} -> {', '.join(row.docs) or row.right}"
            for row in rows if not row.patterns] or ["  none"]

    out += ["", "## Stale map entries (fix the row)"]
    out += [f"- {entry}" for entry in stale] or ["  none"]
    return "\n".join(out)


def main(argv=None):
    sys.stdout.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    source = parser.add_mutually_exclusive_group()
    source.add_argument("--range")
    source.add_argument("--staged", action="store_true")
    source.add_argument("--paths", nargs="+")
    parser.add_argument("--repo", default=".")
    parser.add_argument("--skill")
    args = parser.parse_args(argv)
    try:
        repo = git(args.repo, "rev-parse", "--show-toplevel").strip()
        skill = os.path.normpath(args.skill or os.path.join(repo, ".agents", "skills", "docs-sync", "SKILL.md"))
        with open(skill, encoding="utf-8") as handle:
            text = handle.read()
        rows, no_doc = parse_rows(text), parse_no_doc(text)
        if not rows or not any(matches(p, SENTINEL_PATH) for r in rows for p in r.patterns):
            print(f"MAP UNPARSED: {len(rows)} rows from {skill}, none for {SENTINEL_PATH}", file=sys.stderr)
            return 2
        paths = sorted(set(args.paths)) if args.paths else changed_paths(repo, args.range, args.staged)
        tracked = git(repo, "ls-files").splitlines()
    except (OSError, RuntimeError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 1
    stale = stale_entries(repo, rows, no_doc, tracked)
    print(build_report(skill, rows, no_doc, paths, stale))
    return 3 if stale else 0


if __name__ == "__main__":
    sys.exit(main())
