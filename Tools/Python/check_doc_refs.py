"""Check that every `@Documentation/...` reference in the agent-facing tree resolves to a file.

WHAT THIS GUARDS
    CLAUDE.md, AGENTS.md, the skills under .agents/, and the docs themselves wire several dozen
    `@Documentation/...` references into the doc tree. Nothing validates them: a doc that is
    renamed, moved, or promoted leaves the reference pointing at nothing, and the only symptom is
    an agent that silently loses the context the reference was meant to supply.

    The docs-sync skill runs this every time (Step 3), not only after a rename — most stale refs
    come from moves made OUTSIDE a docs-sync run.

WHY THE FOUND-COUNT IS PRINTED
    "0 unresolved" is meaningless on its own: a scan that matched nothing reports it too. The
    reference count is the corroborating signal — if it collapses, the scan broke, not the tree.

PATH SHAPES HANDLED
    * folders whose names contain spaces (Architecture/World Generation/, Testing Framework/) —
      a reference is read up to its `.md`, not up to the first space
    * trailing prose punctuation and markdown decoration (`` ` ``, `)`, `,`, `.`)

DELIBERATELY IGNORED (documented non-references, not failures)
    * glob patterns      — `@Documentation/Bugs/*.md`
    * placeholders       — `@Documentation/Bugs/{FILE}`
    Anything else that fails to resolve is a real finding.

READS   CLAUDE.md, AGENTS.md, and *.md under .agents/ and Documentation/. WRITES nothing.

RUN
    python Tools/Python/check_doc_refs.py            # check the default roots
    python Tools/Python/check_doc_refs.py --list     # also print every reference and its source
    python Tools/Python/check_doc_refs.py path ...   # check explicit files/directories

EXIT CODES
    0  every reference resolves
    1  at least one reference does not resolve
    2  a supplied path does not exist
"""
import argparse
import os
import re
import sys

# The reference itself. Prefer the form ending in `.md` so paths containing spaces survive;
# fall back to a whitespace/punctuation-delimited token for references to directories.
REF_TO_MD = re.compile(r"""[^\n`)\]"'>]*?\.md""")
REF_FALLBACK = re.compile(r"""[^\s`)\]"',;]*""")
MARKER = '@Documentation/'

# Non-references: a glob or a `{PLACEHOLDER}` standing in for a filename.
IGNORED = re.compile(r'[*{}]')

DEFAULT_ROOTS = ('CLAUDE.md', 'AGENTS.md', '.agents', 'Documentation')

# .../<repo>/Tools/Python/check_doc_refs.py -> <repo>
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
    """Repo-relative path for reporting, falling back to the absolute path off-repo.

    A scan target on another drive (a scratch probe, say) has no relative form on Windows.
    """
    try:
        return os.path.relpath(path, REPO_ROOT).replace(os.sep, '/')
    except ValueError:
        return path.replace(os.sep, '/')


def _extract(line):
    """Yield each `Documentation/...` path referenced on one line, without the `@`."""
    start = 0
    while True:
        hit = line.find(MARKER, start)
        if hit < 0:
            return
        start = hit + len(MARKER)
        tail = line[hit + 1:]  # drop the '@', keep 'Documentation/...'
        match = REF_TO_MD.match(tail) or REF_FALLBACK.match(tail)
        path = match.group(0).rstrip('.,;:') if match else ''
        if path:
            yield path


def scan(roots):
    """Return (references, files_scanned) where references is a list of (path, source, line)."""
    references = []
    files_scanned = 0
    for root in roots:
        for path in _iter_markdown(root):
            files_scanned += 1
            with open(path, 'r', encoding='utf-8', errors='replace') as handle:
                for number, line in enumerate(handle, start=1):
                    for reference in _extract(line):
                        references.append((reference, _display(path), number))
    return references, files_scanned


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument('paths', nargs='*', help='files or directories to scan (default: the agent-facing tree)')
    parser.add_argument('--list', action='store_true', help='print every reference found, with its source')
    args = parser.parse_args()

    roots = []
    for candidate in (args.paths or DEFAULT_ROOTS):
        resolved = candidate if os.path.isabs(candidate) else os.path.join(REPO_ROOT, candidate)
        if not os.path.exists(resolved):
            print('path does not exist: {}'.format(candidate), file=sys.stderr)
            return 2
        roots.append(resolved)

    references, files_scanned = scan(roots)
    unique = sorted({reference for reference, _source, _line in references})
    checked = [path for path in unique if not IGNORED.search(path)]
    ignored = [path for path in unique if IGNORED.search(path)]

    if args.list:
        for reference, source, number in sorted(references):
            print('  {}:{}  @{}'.format(source, number, reference))

    print('Scanned {} markdown files - found {} references ({} unique, {} ignored as glob/placeholder)'
          .format(files_scanned, len(references), len(unique), len(ignored)))

    missing = []
    for path in checked:
        if not os.path.exists(os.path.join(REPO_ROOT, path)):
            sources = sorted({(source, number) for ref, source, number in references if ref == path})
            missing.append((path, sources))

    if not missing:
        print('All {} references resolve.'.format(len(checked)))
        return 0

    print('\n{} unresolved reference(s):'.format(len(missing)))
    for path, sources in missing:
        print('  @{}'.format(path))
        for source, number in sources:
            print('      referenced from {}:{}'.format(source, number))
    return 1


if __name__ == '__main__':
    sys.exit(main())
