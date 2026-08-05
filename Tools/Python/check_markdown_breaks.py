"""Check (and optionally repair) Markdown hard line breaks around stacked `**Label:**` fields.

WHAT THIS GUARDS
    Markdown joins consecutive lines into one paragraph. A stacked metadata block therefore
    renders as a single run-on line unless each field is terminated with TWO TRAILING SPACES:

        **Version:** 1.0··          <- two trailing spaces (invisible)
        **Date:** 2026-08-05··
        **Status:** Open backlog.   <- last field in the block needs none

    The convention and its corollaries live in .agents/skills/create-design-doc/SKILL.md
    (Step 3); .editorconfig ([*.md] trim_trailing_whitespace = false) stops editors trimming
    the spaces, and .gitattributes (*.md whitespace=-blank-at-eol) stops git doing it.

TWO DEFECTS ARE DETECTED
    D1  hard break     Prev line and the following `**Label:**` field line sit in the SAME
                       block context, so the renderer joins them.
                       Fix: two trailing spaces on the previous line.
    D2  continuation   An UNQUOTED `**Label:**` field directly follows a blockquote line.
                       CommonMark lazy continuation absorbs the field INTO the quote; trailing
                       spaces cannot fix this.
                       Fix: insert a blank line between them.

DELIBERATELY NARROW
    These docs wrap prose at ~100 chars precisely so the renderer rejoins it. Only the break
    IMMEDIATELY BEFORE a new field is introduced — ordinary wrapped prose is never touched.
    Where a field's value wraps over several lines, only its LAST line is terminated.

EXCLUSIONS (empirical — calibrated against the repo corpus as of 2026-08-05)
    Already-correct or would-be-corrupted contexts, skipped rather than reported:
      * blockquote-blank lines (`>` alone)      — already a paragraph break
      * GitHub alert markers (`> [!NOTE]` ...)  — body already starts a fresh paragraph
      * HTML comment closers (`... -->`)        — comments are not rendered
      * tables, list items, headings, rules, raw HTML
      * lines already ending in two spaces or a backslash
    A novel document shape may need this list extended — which is why --fix is opt-in and the
    default is a report you read first.

FENCED CODE
    Skipped for prose trees (Documentation/), but CHECKED for skill trees (.agents/skills/),
    where the fenced header block is a template that agents copy verbatim and so must itself
    demonstrate the correct form.

READS   *.md under the given roots. WRITES nothing unless --fix is passed.

RUN
    python Tools/Python/check_markdown_breaks.py                 # check the default roots
    python Tools/Python/check_markdown_breaks.py --fix           # repair them in place
    python Tools/Python/check_markdown_breaks.py path/to/docs    # check an explicit path
    python Tools/Python/check_markdown_breaks.py X --include-fenced

EXIT CODES
    0  clean (or --fix applied successfully)
    1  issues found in check mode
    2  a supplied path does not exist
"""
import argparse
import io
import os
import re
import sys

# A line that starts a new metadata field, optionally inside a blockquote.
FIELD = re.compile(r'^(?:[ \t]*(?:>[ \t]*)*)\*\*[^*\n]+:\*\*')
# Structural lines: a break before them is already implicit, or adding one would corrupt syntax.
STRUCT = re.compile(r'^[ \t]*(\||#{1,6}\s|[-*+]\s|\d+[.)]\s|-{3,}\s*$|={3,}\s*$|<)')
FENCE = re.compile(r'^[ \t]*(```|~~~)')
ALERT = re.compile(r'^[ \t]*>[ \t]*\[!\w+\][ \t]*$')
QUOTE_PREFIX = re.compile(r'^[ \t]*((?:>[ \t]*)*)')

HARD_BREAK = '  '
BACKSLASH = '\\'

# (root, include_fenced). Both trees where the defect has actually occurred.
DEFAULT_ROOTS = (
    ('Documentation', False),
    (os.path.join('.agents', 'skills'), True),
)


def _quote_depth(line):
    """Number of leading '>' blockquote markers on a line."""
    return QUOTE_PREFIX.match(line).group(1).count('>')


def analyse(path, include_fenced=False):
    """Return (raw, lines, d1, d2) where d1/d2 are 0-based indices of offending lines."""
    with io.open(path, 'r', encoding='utf-8', newline='') as handle:
        raw = handle.read()

    lines = raw.split('\n')
    d1, d2 = [], []
    in_fence = False

    for i in range(len(lines) - 1):
        cur = lines[i].rstrip('\r')
        nxt = lines[i + 1].rstrip('\r')

        if FENCE.match(cur):
            in_fence = not in_fence
            continue
        if in_fence and not include_fenced:
            continue
        if not cur.strip() or not FIELD.match(nxt):
            continue
        if ALERT.match(cur):
            continue

        body = re.sub(r'^[ \t]*(?:>[ \t]*)*', '', cur)
        if not body.strip() or STRUCT.match(body):
            continue
        if body.rstrip().endswith('-->'):
            continue

        cur_depth, nxt_depth = _quote_depth(cur), _quote_depth(nxt)
        if cur_depth > 0 and nxt_depth == 0:
            d2.append(i)
        elif cur_depth == nxt_depth:
            if cur.endswith(HARD_BREAK) or cur.endswith(BACKSLASH):
                continue
            d1.append(i)

    return raw, lines, d1, d2


def repair(path, include_fenced=False):
    """Apply both fixes in place. Returns (d1_count, d2_count)."""
    raw, lines, d1, d2 = analyse(path, include_fenced)
    if not d1 and not d2:
        return 0, 0

    eol = '\r' if '\r\n' in raw else ''
    breaks, blanks = set(d1), set(d2)
    out = []

    for i, line in enumerate(lines):
        if i in breaks:
            line = line.rstrip('\r').rstrip() + HARD_BREAK + eol
        out.append(line)
        if i in blanks:
            out.append(eol)

    with io.open(path, 'w', encoding='utf-8', newline='') as handle:
        handle.write('\n'.join(out))
    return len(d1), len(d2)


def _display_path(path):
    """Repo-relative path where possible; absolute otherwise.

    On Windows `relpath` raises when the target is on a different drive than the cwd — which
    happens whenever the tool is pointed at a scratch directory outside the repo.
    """
    try:
        return os.path.relpath(path).replace(os.sep, '/')
    except ValueError:
        return path.replace(os.sep, '/')


def iter_markdown(root):
    """Yield every .md path under root, in deterministic order."""
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames.sort()
        for name in sorted(filenames):
            if name.endswith('.md'):
                yield os.path.join(dirpath, name)


def run(roots, apply_fix, verbose):
    total_d1 = total_d2 = total_files = 0

    for root, include_fenced in roots:
        if not os.path.isdir(root):
            sys.stderr.write('error: no such directory: %s\n' % root)
            return 2

        for path in iter_markdown(root):
            _raw, lines, d1, d2 = analyse(path, include_fenced)
            if not d1 and not d2:
                continue

            total_files += 1
            total_d1 += len(d1)
            total_d2 += len(d2)
            print('%s  (D1=%d D2=%d)' % (_display_path(path), len(d1), len(d2)))

            if verbose:
                for i in d1:
                    print('    L%-5d hard break   before: %s' % (i + 2, lines[i + 1].strip()[:64]))
                for i in d2:
                    print('    L%-5d blank line   before: %s' % (i + 2, lines[i + 1].strip()[:64]))

            if apply_fix:
                repair(path, include_fenced)

    if total_files == 0:
        print('OK: no Markdown line-break issues found.')
        return 0

    verb = 'Fixed' if apply_fix else 'Found'
    print('\n%s %d hard break(s) + %d blank line(s) across %d file(s).'
          % (verb, total_d1, total_d2, total_files))

    if apply_fix:
        return 0

    print('Re-run with --fix to apply, after reviewing the list above.')
    return 1


def main():
    parser = argparse.ArgumentParser(
        description='Check Markdown hard line breaks around stacked **Label:** fields.')
    parser.add_argument('paths', nargs='*',
                        help='directories to scan (default: Documentation/ and .agents/skills/)')
    parser.add_argument('--fix', action='store_true',
                        help='repair issues in place instead of only reporting them')
    parser.add_argument('--include-fenced', action='store_true',
                        help='also check inside fenced code blocks (default for skill trees)')
    parser.add_argument('-v', '--verbose', action='store_true',
                        help='print the offending line numbers, not just file totals')
    args = parser.parse_args()

    if args.paths:
        roots = [(p, args.include_fenced) for p in args.paths]
    else:
        roots = list(DEFAULT_ROOTS)

    return run(roots, args.fix, args.verbose)


if __name__ == '__main__':
    sys.exit(main())
