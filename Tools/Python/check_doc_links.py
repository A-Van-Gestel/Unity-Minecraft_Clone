"""Check that every relative markdown link between docs resolves to a file.

WHAT THIS GUARDS
    The sibling checker, check_doc_refs.py, validates `@Documentation/...` references — the shape
    CLAUDE.md and the skills use. It is blind to the OTHER half of the link graph: ordinary
    markdown links, `[label](../Design/THING.md)`, which is how the docs point at each other.

    That blindness has a specific cost. Deleting or moving a doc leaves its inbound markdown links
    dangling and BOTH existing checkers stay green — check_doc_refs.py because it only reads
    `@`-prefixed references, check_markdown_breaks.py because it only reads trailing whitespace.
    The Documentation/ tree carries well over a hundred such links, including Architecture docs
    that defer detail back into Design/, so a doc lifecycle change (a promotion, an archival, a
    deletion) is exactly the operation this guards and exactly the one nothing else covers.

WHY THE FOUND-COUNT IS PRINTED
    Same reason as check_doc_refs.py: "0 unresolved" is what a broken scan reports too. The link
    count is the corroborating signal — if it collapses, suspect the scan, not the tree.

PATH SHAPES HANDLED
    * percent-encoding — `Architecture/World%20Generation/CAVE_GENERATION.md` is the CORRECT
      markdown spelling of a path with a space, and is decoded before resolution rather than
      reported. Undecoded, these are the single largest source of false positives in this tree.
    * fragments        — `SOME_DOC.md#section-heading` resolves on the file, the anchor is ignored
    * same-file anchors, `[x](#heading)`, are not links to a file and are skipped

DELIBERATELY IGNORED (documented non-links, not failures)
    * absolute URLs        — http://, https://, mailto:
    * non-markdown targets — images, code files; this checker owns the doc graph only
    * placeholders + globs — `../Architecture/<DOC>.md` in the skill templates, `*.md` patterns.
      Same exclusion check_doc_refs.py makes, for the same reason: a template slot is a documented
      non-reference, and counting it as breakage trains everyone to ignore a red result.
    * reference-style link DEFINITIONS are not parsed; inline links are the tree's convention

READS   CLAUDE.md, AGENTS.md, and *.md under .agents/ and Documentation/. WRITES nothing.

RUN
    python Tools/Python/check_doc_links.py            # check the default roots
    python Tools/Python/check_doc_links.py --list     # also print every link and its source
    python Tools/Python/check_doc_links.py path ...   # check explicit files/directories

EXIT CODES
    0  every link resolves
    1  at least one link does not resolve
    2  a supplied path does not exist
"""
import argparse
import os
import re
import sys
from urllib.parse import unquote, urlsplit

# Inline markdown link: [label](target). The label may contain backticks and nested brackets are
# rare enough in this tree that a non-greedy label is the right trade.
LINK = re.compile(r'\[[^\]]*\]\(([^()\s]+)\)')

SKIPPED_SCHEMES = ('http://', 'https://', 'mailto:', 'ftp://')

# Non-links: a `<PLACEHOLDER>` slot in a skill template, or a glob.
PLACEHOLDER = re.compile(r'[<>{}*]')

DEFAULT_ROOTS = ('CLAUDE.md', 'AGENTS.md', '.agents', 'Documentation')

# .../<repo>/Tools/Python/check_doc_links.py -> <repo>
REPO_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))


def _iter_markdown(root):
    """Yield every markdown file under `root` (or `root` itself when it is a file)."""
    if os.path.isfile(root):
        yield root
        return
    for dirpath, _dirnames, filenames in os.walk(root):
        for name in sorted(filenames):
            if name.lower().endswith('.md'):
                yield os.path.join(dirpath, name)


def _display(path):
    """Repo-relative path for reporting, falling back to the absolute path off-repo."""
    try:
        return os.path.relpath(path, REPO_ROOT).replace(os.sep, '/')
    except ValueError:
        return path.replace(os.sep, '/')


def _target_of(raw):
    """Return the decoded, fragment-stripped file path a link points at, or '' to skip it."""
    if raw.startswith(SKIPPED_SCHEMES) or raw.startswith('#'):
        return ''

    # Strip the fragment before decoding: an anchor is a location inside the file, not part of it.
    path = urlsplit(raw).path
    if not path:
        return ''

    path = unquote(path)
    if not path.lower().endswith('.md') or PLACEHOLDER.search(path):
        return ''

    return path


def scan(roots):
    """Return (links, files_scanned, skipped) — links are (target, source_file, line) triples."""
    links = []
    skipped = 0
    files_scanned = 0
    for root in roots:
        for path in _iter_markdown(root):
            files_scanned += 1
            with open(path, 'r', encoding='utf-8', errors='replace') as handle:
                for number, line in enumerate(handle, start=1):
                    for raw in LINK.findall(line):
                        target = _target_of(raw)
                        if target:
                            links.append((target, path, number))
                        elif PLACEHOLDER.search(unquote(urlsplit(raw).path)):
                            skipped += 1
    return links, files_scanned, skipped


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument('paths', nargs='*', help='files or directories to scan (default: the agent-facing tree)')
    parser.add_argument('--list', action='store_true', help='print every link found, with its source')
    args = parser.parse_args()

    roots = []
    for candidate in (args.paths or DEFAULT_ROOTS):
        resolved = candidate if os.path.isabs(candidate) else os.path.join(REPO_ROOT, candidate)
        if not os.path.exists(resolved):
            print('path does not exist: {}'.format(candidate), file=sys.stderr)
            return 2
        roots.append(resolved)

    links, files_scanned, skipped = scan(roots)

    if args.list:
        for target, source, number in sorted(links, key=lambda item: (_display(item[1]), item[2])):
            print('  {}:{}  -> {}'.format(_display(source), number, target))

    print('Scanned {} markdown files - found {} relative markdown links ({} ignored as '
          'placeholder/glob)'.format(files_scanned, len(links), skipped))

    # Resolved against the LINKING file's directory, which is what a relative link means.
    missing = {}
    for target, source, number in links:
        if os.path.exists(os.path.normpath(os.path.join(os.path.dirname(source), target))):
            continue
        missing.setdefault((_display(source), target), []).append(number)

    if not missing:
        print('All {} links resolve.'.format(len(links)))
        return 0

    print('\n{} unresolved link target(s):'.format(len(missing)))
    for (source, target), numbers in sorted(missing.items()):
        lines = ', '.join(str(number) for number in sorted(numbers))
        print('  {}:{}  -> {}'.format(source, lines, target))
    return 1


if __name__ == '__main__':
    sys.exit(main())
