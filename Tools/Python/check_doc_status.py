"""Check that OPEN_WORK_INDEX.md agrees with each listed document's own `**Status:**` field.

WHAT THIS GUARDS
    `docs-sync` fires on a **code change** or on a **promotion**. Neither happens when a document's
    state moves because ANOTHER document's state moved — a phase closes, and the index still lists
    its doc as owning open work. That drift has no trigger and, until this checker, no detector:
    check_doc_links.py reads links, check_doc_refs.py reads `@`-references and
    check_markdown_breaks.py reads trailing whitespace. **None of them can read a status claim**, so
    all three stay green through it.

    Observed twice on 2026-09-05, hours apart, in documents that arc had just touched — including
    the index going stale for its own phase. Filed as `DG-5` in
    Documentation/Design/DOC_LIFECYCLE_AND_OPEN_WORK_INDEX.md §3.5.

WHAT IT CHECKS
    §2 of the index ("Where the open work lives") — every linked doc must read as OPEN.
    §3 ("Closed arcs retained") — every linked doc must read as CLOSED.
    A row on the wrong side, a doc listed in BOTH sections (the half-finished move: §3 row added,
    §2 row never removed), or a doc this cannot classify, is a finding.

WHAT IT DOES NOT CHECK
    **Prerequisite drift** — doc A's Status describing doc B's state, the other half of `DG-5`.
    That is not mechanically detectable here: `**Next Review:**` exists in 38 docs and only 6 cite a
    resolvable ID, so a resolver would miss ~84 % of triggers while looking authoritative. It is
    covered by a `docs-sync` step instead, not by this tool.

    It also cannot find a doc that owns open work and is **absent** from the index. Omission is
    unbounded — there is no list of what should have been listed.

THE CLASSIFIER, AND WHY IT IS ORDERED
    Ordered keyword rules over the Status text, not a general reading of it. Three tiers, in order:
      1. STRONG_CLOSED — the `⛔` taxonomy symbol only, so an explicitly-superseded doc cannot be
         dragged open by incidental prose.
      2. OPEN — tested before CLOSED, and that order is load-bearing: "Partially implemented"
         contains "implemented", so the other order calls every partially-implemented doc closed.
      3. CLOSED.
    Anything matching no tier is **UNKNOWN and reported**, never quietly passed — a checker that
    silently skips what it cannot parse is the false green this exists to remove.

    Calibrated against every row the index carries: zero mismatches, zero unclassified. **Re-run
    that calibration AND the fixtures before trusting a rule change** — calibration alone passed
    30/30 while four extractor bugs were live, because it exercises `classify` and barely touches
    `_rows`.

    Known limit: the rules cannot read negation. A status saying "nothing remains open" reads as
    OPEN. No current doc is phrased that way, and fixing it needs more than keywords.

STATUS FIELD SHAPES HANDLED (all three occur in this tree)
    * own line, possibly wrapping   — `**Status:** ...`      (51 of 60 docs)
    * blockquoted                   — `> **Status:** ...`    (3)
    * inline beside other fields    — `**Version:** 1.0 **Status:** ...` (3)
    Three docs carry no Status field at all; they are Architecture docs the index does not link, so
    they surface here only if one is ever added to it.

READS   Documentation/Design/OPEN_WORK_INDEX.md and every doc it links. WRITES nothing.

RUN
    python Tools/Python/check_doc_status.py           # check the index
    python Tools/Python/check_doc_status.py --list    # also print every row and its verdict

EXIT CODES
    0  every row agrees with its document
    1  at least one row disagrees, or a linked doc could not be classified
    2  the index could not be parsed (headings moved, or a section yielded no rows)
"""
import argparse
import io
import os
import re
import sys

# Tested BEFORE the open rules, and deliberately only one entry long. Which markers are safe here
# was measured, not assumed: "superseded" appears in the Status of three currently-OPEN docs and
# "arc is complete" in a fourth, so promoting either would invent four false failures.
STRONG_CLOSED_RULES = [r'⛔']

# Ordered rules over the lower-cased Status text — see the module docstring for why.
OPEN_RULES = [
    r'open backlog', r'living backlog', r'active backlog', r'partially implemented',
    r'proposed design', r'\bdraft\b', r'in progress', r'not implemented', r'not started',
    r'unbuilt', r'remains? (open|unbuilt|outstanding|to be)', r'still open', r'⏸️', r'pending',
    r'is open\b', r'are open\b',
]
CLOSED_RULES = [
    r'⛔', r'superseded', r'arc closed', r'fully closed', r'all shipped', r'historical record',
    r'core question is closed', r'arc is complete', r'closed arc', r'\bimplemented\b', r'is closed\b',
]

INDEX = 'Documentation/Design/OPEN_WORK_INDEX.md'
OPEN_SECTION = '## 2.'
CLOSED_SECTION = '## 3.'
END_SECTION = '## 4.'

# .../<repo>/Tools/Python/check_doc_status.py -> <repo>
REPO_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))


def status_text(path):
    """Return the document's Status field as one line, or None when it has no Status field."""
    with io.open(path, encoding='utf-8', errors='replace') as handle:
        body = handle.read()

    # Own line, wrapping until a blank line or the next **Label:** field.
    match = re.search(r'^\*\*Status:\*\*(.*?)(?=\n\s*\n|\n\*\*[A-Z])', body, re.M | re.S)
    if match:
        return ' '.join(match.group(1).split())

    # Blockquoted, as a few older docs write it.
    match = re.search(r'^> \*\*Status:?\*?\*?(.*?)(?=\n>\s*\n|\n\n)', body, re.M | re.S)
    if match:
        return ' '.join(match.group(1).replace('\n>', ' ').split())

    # Inline, sharing a line with the other header fields.
    match = re.search(r'\*\*Status:\*\*(.*?)(?=\*\*[A-Z]|\n)', body, re.S)
    if match:
        return ' '.join(match.group(1).split())

    return None


def classify(text):
    """Return (verdict, matched_rule) for a Status text: OPEN, CLOSED, UNKNOWN or NO-STATUS."""
    if text is None:
        return 'NO-STATUS', ''

    lowered = text.lower()
    for rule in STRONG_CLOSED_RULES:
        if re.search(rule, lowered):
            return 'CLOSED', rule
    for rule in OPEN_RULES:
        if re.search(rule, lowered):
            return 'OPEN', rule
    for rule in CLOSED_RULES:
        if re.search(rule, lowered):
            return 'CLOSED', rule

    return 'UNKNOWN', ''


def _rows(section):
    """Yield the link target of each table row in one index section, exactly as written.

    Only table rows count. A prose pointer inside the section — the §3 intro links the design doc
    that defines the rule — is not a row, and counting it silently inverts that doc's verdict.

    The target is yielded verbatim rather than reduced to a basename, so the caller resolves it
    relative to the index: `Architecture/X.md` and `Design/X.md` coexist during every promotion, and
    a basename lookup silently reads whichever the directory walk happened to reach first.
    """
    for line in section.splitlines():
        if not line.startswith('|'):
            continue
        # A separator row is ONLY pipes, dashes, colons and whitespace. Testing for '---' anywhere
        # in the line silently dropped any row whose text cell contained a dash run.
        if re.fullmatch(r'[\s|:\-]+', line):
            continue
        # The charset must include '-': Documentation/Performance/ is full of dated filenames, and
        # a row linking one used to match nothing and be skipped without a word.
        match = re.search(r'\]\(([A-Za-z0-9_%./\-]+\.md)\)', line)
        if match:
            yield match.group(1)


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument('--list', action='store_true', help='print every row and its verdict')
    args = parser.parse_args()

    index_path = os.path.join(REPO_ROOT, INDEX)
    if not os.path.exists(index_path):
        print('index not found: {}'.format(INDEX), file=sys.stderr)
        return 2

    with io.open(index_path, encoding='utf-8') as handle:
        index = handle.read()

    if OPEN_SECTION not in index or CLOSED_SECTION not in index or END_SECTION not in index:
        print('cannot parse {}: expected sections {} {} {}'
              .format(INDEX, OPEN_SECTION, CLOSED_SECTION, END_SECTION), file=sys.stderr)
        return 2

    # Collected as a LIST of (target, section), never a dict keyed on the doc. A doc listed in both
    # sections is precisely the half-finished move this tool guards — §3 row added, §2 row not
    # removed — and a dict would let the second write overwrite the first, hiding the contradiction
    # and corrupting the counts along with it.
    listed = [(target, 'OPEN') for target in _rows(
        index.split(OPEN_SECTION)[1].split(CLOSED_SECTION)[0])]
    open_count = len(listed)
    listed += [(target, 'CLOSED') for target in _rows(
        index.split(CLOSED_SECTION)[1].split(END_SECTION)[0])]
    closed_count = len(listed) - open_count

    # A section that yields nothing means the scan broke, not that the tree is clean.
    if not open_count or not closed_count:
        print('cannot parse {}: §2 yielded {} rows, §3 yielded {}'
              .format(INDEX, open_count, closed_count), file=sys.stderr)
        return 2

    findings = []

    # A doc in both sections contradicts itself regardless of what its Status says.
    seen = {}
    for target, section in listed:
        if target in seen and seen[target] != section:
            findings.append((target, 'BOTH', section,
                             'listed in §2 AND §3 — one of the two rows was never removed'))
        seen[target] = section

    index_dir = os.path.dirname(index_path)
    for target, want in listed:
        # Resolved against the index's own directory, which is what the row's relative link means.
        path = os.path.normpath(os.path.join(index_dir, target))
        if not os.path.exists(path):
            findings.append((target, 'MISSING', want, 'the index links a file that does not exist'))
            continue

        verdict, rule = classify(status_text(path))
        if args.list:
            print('  {:52s} {:9s} (index says {})'.format(target, verdict, want))
        if verdict in ('UNKNOWN', 'NO-STATUS'):
            findings.append((target, verdict, want, 'cannot classify this doc\'s Status field'))
        elif verdict != want:
            findings.append((target, verdict, want, 'matched rule: {}'.format(rule)))

    print('Checked {} index rows ({} in §2 open, {} in §3 closed)'
          .format(len(listed), open_count, closed_count))

    if not findings:
        print('Every row agrees with its document.')
        return 0

    print('\n{} row(s) disagree with their document:'.format(len(findings)))
    for name, verdict, want, why in findings:
        if verdict == 'BOTH':
            print('  {}\n      {}'.format(name, why))
        else:
            print('  {}\n      index section says {}, the document reads {} — {}'
                  .format(name, want, verdict, why))
    return 1


if __name__ == '__main__':
    sys.exit(main())
