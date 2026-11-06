"""Check that PERFORMANCE_MONITOR_AND_LOGGER_OVERHAUL.md's §4.9 coverage map matches the code.

WHAT THIS GUARDS
    §4.9 "Coverage map" answers what the performance monitor covers, with tables of
    the slots by region, the per-frame scripts and the job structs with their
    `JobBusyTimer`. The section says of itself that the tables are a snapshot and
    the grep block at its end ("Re-deriving the map") is the check - and nothing
    ever ran that block. A slot or job struct added by a later phase therefore
    reads as covered in a doc that never mentions it, because a probe usually
    lands inside a file the same phase edits for other reasons, so `docs-sync`'s
    path-to-doc mapping points at no doc, and no trigger asks for this section.

WHAT IT CHECKS (one line per problem, stable prefix)
    SLOT-MISSING-IN-DOC   a slot the code passes to `PerfStore.Begin` that §4.9 never names
    SLOT-UNKNOWN-IN-DOC   a name in §4.9's slot tables that is not a `PerfSlot` member
    JOB-MISSING-IN-DOC    a job struct in the code that §4.9 never names
    PROBED-SLOT-UNUSED    a `PerfSlot` member no code passes to `Begin`: printed, never
                        failing, since some slots are recorded another way
                        (`WorldUnattributed` by subtraction in `EndWorldFrame`)

THE DERIVATIONS MIRROR THE DOC'S OWN GREP BLOCK, so a disagreement is a doc problem:
    * `PerfStore.Begin(PerfSlot.<name>` in `Assets/Scripts/**/*.cs` -> the slot plus
      the files that probe it
    * `void Update|LateUpdate|FixedUpdate()` -> the per-frame scripts, probed when the
      file contains `PerfStore.Begin(PerfSlot`
    * `public JobBusyTimer Timer;` -> the timed job files; `struct <Name> : IJob...`
      -> the job universe
    The `PerfSlot` enum, read from its declaration rather than by grep, is the slot
    universe. The four derivations are S1..S4 and `--list` prints them.

WHY THE FOUND-COUNTS ARE PRINTED
    "no findings" is meaningless when a scan matched nothing: a moved heading or a
    renamed folder collapses the scan silently and reports clean. Each derivation prints
    its own count, and a structural lookup that fails (code dir, doc, §4.9 heading,
    either slot table, the enum declaration) exits 2 instead of judging an empty universe.

WHAT IT DOES NOT CHECK
    * An UNPROBED per-frame script is not a finding: §4.9's "Known gaps" row covers
      the small and debug scripts on purpose. --list prints every one so a reviewer
      can judge the list instead of the checker guessing.
    * The counters and their sources: §4.9 lists them as prose, not table rows, so no
      first-column rule reaches them. Their body is `World.SamplePerfCounters`, per
      the section's own closing note.
    * Whether a covered system has had an analysis pass. That is the `AC-*` question
      the section points at.

LIMITS OF THE DOC READING
    Backticks are paired per line (how this doc wraps), and a slot-table cell is read
    as one row of the table directly under its anchor paragraph, so a name in a LATER
    section of the same document never counts as naming it here.

READS    Assets/Scripts/**/*.cs and the §4.9 section of
         Documentation/Design/PERFORMANCE_MONITOR_AND_LOGGER_OVERHAUL.md.
WRITES   nothing; the design doc is never edited to make the check pass.

RUN
    python Tools/Python/check_perf_coverage_map.py            # check the map
    python Tools/Python/check_perf_coverage_map.py --list     # print the S1..S4 derivations
    python Tools/Python/check_perf_coverage_map.py --root PATH  # another checkout (tests patch REPO_ROOT)

EXIT CODES
    0  no failing problem
    1  at least one failing problem
    2  the layout this derives from is not there
"""
import argparse
import os
import re
import sys

CODE_DIR = "Assets/Scripts"
DOC_PATH = "Documentation/Design/PERFORMANCE_MONITOR_AND_LOGGER_OVERHAUL.md"

# The doc's §4.9 runs to the next heading of level 3 or shallower; a level-4 heading
# stays inside the section.
SECTION_START = re.compile(r"^###\s+4\.9\b")
HEADING_STOPS = re.compile(r"^#{2,3}\s")
# A bold label alone on its line starts the next block, so a slot table is not read past it.
LABEL_STOPS = re.compile(r"^\*\*[^*]+\*\*\s*$")

# Anchors of the two slot tables, loose on the dash so a re-titled block still resolves.
SLOT_TABLE_ANCHORS = (
    re.compile(r"^\*\*Main thread\b.*\bslots"),
    re.compile(r"^Outside `World\.Update`"),
)
ROW_START = re.compile(r"^\|")
SEPARATOR_ROW = re.compile(r"[\s|:\-]+")

BEGIN_SLOT = re.compile(r"PerfStore\.Begin\(PerfSlot\.([A-Za-z][A-Za-z0-9_]*)")
PROBE_MARK = "PerfStore.Begin(PerfSlot"
PER_FRAME = re.compile(r"void (Update|LateUpdate|FixedUpdate)\(\)")
JOB_TIMER = re.compile(r"public JobBusyTimer Timer;")
JOB_STRUCT = re.compile(r"\bstruct\s+(\w+)\s*:\s*(IJob\w*)")
ENUM_DECL = re.compile(r"\benum\s+PerfSlot\b[^{]*\{")
LINE_COMMENT = re.compile(r"//[^\n]*")
BLOCK_COMMENT = re.compile(r"/\*.*?\*/", re.S)
MEMBER_HEAD = re.compile(r"\s*([A-Za-z_]\w*)\s*(=|$)")

BACKTICK = re.compile(r"`([^`\n]+)`")
IDENT_TOKEN = re.compile(r"[A-Za-z_][A-Za-z0-9_]*")
SLOT_NAME_SHAPE = re.compile(r"^[A-Za-z][A-Za-z0-9_]*$")

# The enum's trailing member counts the slots for the fixed-size arrays; it is not a slot.
NON_SLOT_MEMBERS = ("Count",)

# .../<repo>/Tools/Python/check_perf_coverage_map.py -> <repo>
REPO_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))


def _display(path, root):
    """Repo-relative path with forward slashes, as the doc's grep block prints it."""
    return os.path.relpath(path, root).replace(os.sep, "/")


def _iter_cs_files(root):
    """Yield every .cs file under `root`, in a walk order that does not vary by platform."""
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames.sort()
        for name in sorted(filenames):
            if name.endswith(".cs"):
                yield os.path.join(dirpath, name)


def _read_text(path):
    """Read one source file as UTF-8; a stray byte must not abort the scan of a thousand."""
    with open(path, "r", encoding="utf-8", errors="replace") as handle:
        return handle.read()


def scan_code(code_dir, root):
    """Walk Assets/Scripts and derive S1..S4.

    Returns a dict keyed ``members`` (the enum's declared slot names, in declaration
    order), ``probes`` (slot -> sorted ``path:line`` sites), ``frames`` (list of
    ``(display_path, probed)``), ``structs`` (list of ``(name, display_path, line,
    timed)``), ``timed_files`` and ``cs_files`` (how many files were read).
    """
    members = []
    probes = {}
    frames = []
    structs = []
    timed_files = set()
    cs_files = 0

    for path in _iter_cs_files(code_dir):
        display = _display(path, root)
        cs_files += 1
        text = _read_text(path)

        if JOB_TIMER.search(text):
            timed_files.add(display)

        if PER_FRAME.search(text):
            frames.append((display, PROBE_MARK in text))

        if not members:
            # Only the enum declaration builds the slot universe; a second one would
            # be a finding to raise with, and there is none here.
            match = ENUM_DECL.search(text)
            if match:
                members = list(_enum_members(_strip_comments(text[match.end():])))

        for line_number, line in enumerate(text.splitlines(), start=1):
            # finditer, like grep -o: one line can hold two probe sites.
            for hit in BEGIN_SLOT.finditer(line):
                probes.setdefault(hit.group(1), []).append(f"{display}:{line_number}")
            struct = JOB_STRUCT.search(line)
            if struct:
                structs.append((struct.group(1), display, line_number, display in timed_files))

    frames.sort()
    return {"members": members, "probes": probes, "frames": frames, "structs": structs,
            "timed_files": timed_files, "cs_files": cs_files}


def _enum_members(body):
    """Yield each declared enum member name from an enum body, cut at the closing brace."""
    end = body.find("}")
    if end >= 0:
        body = body[:end]
    for part in body.split(","):
        match = MEMBER_HEAD.match(part)
        if match:
            yield match.group(1)


def _strip_comments(text):
    """Drop block and line comments so enum doc-comments cannot pass as member names."""
    text = BLOCK_COMMENT.sub(" ", text)
    return LINE_COMMENT.sub("", text)


def doc_section(lines):
    """Return the §4.9 section as ``[(line_number, text)]``, or None if the heading is gone."""
    start = None
    for number, line in enumerate(lines, start=1):
        if SECTION_START.match(line):
            start = number
            break
    if start is None:
        return None

    body = []
    for number, line in enumerate(lines, start=1):
        if number <= start:
            continue
        if HEADING_STOPS.match(line):
            break
        body.append((number, line))
    return body


def _rows_after(section, pattern):
    """Return the contiguous table rows that follow the paragraph `pattern` opens,
    stopping at any heading or bold label before a table is reached."""
    index = 0
    while index < len(section) and not pattern.match(section[index][1]):
        index += 1
    if index == len(section):
        return []

    index += 1
    while index < len(section) and not ROW_START.match(section[index][1]):
        if LABEL_STOPS.match(section[index][1]) or HEADING_STOPS.match(section[index][1]):
            break
        index += 1

    rows = []
    while index < len(section) and ROW_START.match(section[index][1]):
        rows.append(section[index])
        index += 1
    return rows


def slot_table_names(section):
    """Return (slot-name candidates, tables that yielded no rows) from the slot tables' first column.

    A backticked cell span is a slot-name candidate only when it is a single bare
    identifier: a dotted span names a file, a type or a method, and a wildcard such as
    `Tick*` names a counter family, none of which belong to the slot universe.
    """
    names = []
    empty_tables = 0
    for pattern in SLOT_TABLE_ANCHORS:
        rows = _rows_after(section, pattern)
        if not rows:
            empty_tables += 1
            continue
        for number, row in rows:
            if SEPARATOR_ROW.fullmatch(row):
                continue
            for span in BACKTICK.findall(_first_cell(row)):
                span = span.strip().rstrip("*")
                if SLOT_NAME_SHAPE.match(span):
                    names.append((span, number))
    return names, empty_tables


def _first_cell(row):
    """Return a markdown row's first cell: the text between the first two pipes."""
    parts = row.split("|")
    return parts[1] if len(parts) > 2 else ""


def doc_named_names(section):
    """Return every identifier §4.9 backticks, split on separators so `PerfSlot.Tick`
    and `Tick*` both name `Tick`."""
    tokens = set()
    for _number, line in section:
        for span in BACKTICK.findall(line):
            tokens.update(IDENT_TOKEN.findall(span))
    return tokens


def print_derivations(code):
    """Print the S1..S4 derivations in the shape the doc's re-deriving block prints them."""
    non_slots = [name for name in code["members"] if name in NON_SLOT_MEMBERS]
    slots = [name for name in code["members"] if name not in NON_SLOT_MEMBERS]

    header = f"--- S1 PerfSlot members ({len(slots)} slots"
    if non_slots:
        header += f", plus {len(non_slots)} non-slot"
    print(header + ") ---")
    for name in slots:
        print(f"  {name}")
    if non_slots:
        print(f"  (not slots: {', '.join(non_slots)})")

    grep_begin = 'grep -rnoE "PerfStore\\.Begin\\(PerfSlot\\.[A-Za-z]+" Assets/Scripts'
    print(f"--- S2 slot probe sites ({grep_begin}) ---")
    for name in sorted(code["probes"]):
        for site in code["probes"][name]:
            print(f"{site}:PerfStore.Begin(PerfSlot.{name}")

    print("--- S3 per-frame scripts (probed / UNPROBED, sorted) ---")
    for display, probed in code["frames"]:
        print(f"{'probed   ' if probed else 'UNPROBED '} {display}")

    print('--- S4 timed job files (grep -rln "public JobBusyTimer Timer;") ---')
    for display in sorted(code["timed_files"]):
        print(display)

    print('--- S4 job structs (grep -rnE "struct \\w+ : IJob\\w*") ---')
    for name, display, line_number, timed in code["structs"]:
        marker = "TIMED" if timed else "UNTIMED"
        print(f"{display}:{line_number}  struct {name}  {marker}")


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--list", action="store_true",
                       help="print the S1..S4 derivations as well as the findings")
    parser.add_argument("--root", help="repo root to check (default: this script's repo root)")
    args = parser.parse_args(argv)

    root = os.path.abspath(args.root or REPO_ROOT)
    code_dir = os.path.join(root, CODE_DIR)
    doc_path = os.path.join(root, DOC_PATH)
    if not os.path.isdir(code_dir):
        print(f"code dir not found: {CODE_DIR}", file=sys.stderr)
        return 2
    if not os.path.isfile(doc_path):
        print(f"doc not found: {DOC_PATH}", file=sys.stderr)
        return 2

    with open(doc_path, "r", encoding="utf-8", errors="replace") as handle:
        doc_lines = handle.read().splitlines()

    section = doc_section(doc_lines)
    if section is None:
        print(f"cannot parse {DOC_PATH}: no '### 4.9' heading", file=sys.stderr)
        return 2

    code = scan_code(code_dir, root)
    if not code["members"]:
        print(f"cannot derive the slot universe: no 'enum PerfSlot' under {CODE_DIR}",
              file=sys.stderr)
        return 2

    slot_names, empty_tables = slot_table_names(section)
    if empty_tables:
        print(f"cannot parse {DOC_PATH}: {empty_tables} of {len(SLOT_TABLE_ANCHORS)} "
              f"slot tables yielded no rows",
              file=sys.stderr)
        return 2

    if args.list:
        print_derivations(code)

    named = doc_named_names(section)
    real_slots = {name for name in code["members"] if name not in NON_SLOT_MEMBERS}
    non_slots = [name for name in code["members"] if name in NON_SLOT_MEMBERS]
    probed = sorted(code["probes"])
    probed_files = sum(1 for _display, probed_here in code["frames"] if probed_here)
    timed = sum(1 for _n, _d, _l, is_timed in code["structs"] if is_timed)

    excluded = f"; excluded as non-slot: {', '.join(non_slots)}" if non_slots else ""
    print(f"Scanned {code['cs_files']} .cs files and {len(section)} doc lines - "
          f"slots {len(real_slots)}, probed slots {len(probed)}, per-frame scripts "
          f"{len(code['frames'])} ({probed_files} probed), job structs {len(code['structs'])}, "
          f"timed {timed}, doc names {len(named)} ({len(slot_names)} in slot tables)"
          f"{excluded}")

    problems = []
    for name in probed:
        if name not in named:
            sites = code["probes"][name]
            extra = f" (+{len(sites) - 1} more)" if len(sites) > 1 else ""
            problems.append(f"SLOT-MISSING-IN-DOC  {name:<22} probed at {sites[0]}{extra}")
    for name, line_number in slot_names:
        if name not in real_slots:
            problems.append(f"SLOT-UNKNOWN-IN-DOC  {name:<22} {DOC_PATH}:{line_number} "
                            f"is not a PerfSlot member")
    for name in sorted({name for name, _d, _l, _t in code["structs"]} - named):
        site = next(f"{display}:{line_number}" for n, display, line_number, _t in code["structs"]
                    if n == name)
        problems.append(f"JOB-MISSING-IN-DOC     {name:<22} {site} is not named in the section")

    unused = sorted(real_slots - set(probed))
    notes = [f"PROBED-SLOT-UNUSED   {name:<22} no PerfStore.Begin call site "
             f"(recorded another way)" for name in unused]

    if problems:
        print(f"\n{len(problems)} problem(s) where the map and the code disagree:")
        for line in problems:
            print(f"  {line}")
    else:
        print("\nNo doc/code disagreement found.")

    if notes:
        print(f"\n{len(notes)} informational (non-failing):")
        for line in notes:
            print(f"  {line}")

    verdict = "FAIL" if problems else "PASS"
    print(f"\n{verdict}: {len(problems)} problem(s), {len(notes)} informational.")
    return 1 if problems else 0


if __name__ == "__main__":
    # Console and pipe encodings differ per machine; the § in the header line must not depend on them.
    sys.stdout.reconfigure(encoding="utf-8")
    sys.exit(main())
