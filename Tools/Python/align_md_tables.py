"""Check (and optionally repair) column alignment of Markdown tables in the human-facing docs.

WHAT THIS GUARDS
    House style: every pipe table under Documentation/ is padded so its columns line up in a
    monospace editor. Hand edits break the padding on almost every change, so the rule is only
    sustainable with a tool. Agent-facing files (CLAUDE.md, AGENTS.md, .agents/) are NOT in the
    default scope — padding there costs tokens on every read and buys nothing.

WIDTH MODEL
    Display width per character: East Asian Wide/Fullwidth = 2, a character followed by the emoji
    variation selector U+FE0F = 2 (`⚠️` renders as an emoji), combining marks / U+FE0F / zero-width
    joiner = 0, everything else 1.

ALIGNED FORM
    | Cell  | Cell |        one space of padding each side; columns padded to the widest cell
    |-------|:----:|        separator dashes fill the column; `:` alignment markers are kept
    A column whose separator is `:--:` is centered; every other column is left-aligned.

CONTENT SAFETY
    Cells are split the way GFM renders them: on every unescaped `|`, code spans included (only
    `\\|` stays inside its cell). The delimiter row is always line 2; a later all-dash row is data.
    Only the whitespace around each cell changes. After aligning, every table is re-split and must
    yield the same cell texts as before (rows that had fewer cells gain empty trailing cells, which
    render identically); a mismatch aborts that file untouched. Fenced code blocks (``` and ~~~)
    and indented or blockquoted tables are skipped.
    A row with MORE cells than the header is IRREGULAR: the renderer drops the extra cells, and
    which column is missing is a content decision, so such a table is reported and never touched.
    So is a header whose cell count differs from the delimiter row's: GFM does not render that block
    as a table at all, and padding it would change the rendered document.

READS   *.md under the given roots. WRITES nothing unless --fix is passed.

RUN
    python Tools/Python/align_md_tables.py                       # check Documentation/
    python Tools/Python/align_md_tables.py --fix                 # align them in place
    python Tools/Python/align_md_tables.py path/to/file.md ...   # explicit files or directories
    python Tools/Python/align_md_tables.py FILE --lines 40 80    # only tables starting in 40..80

EXIT CODES
    0  every table aligned and regular (or --fix applied successfully)
    1  misaligned or irregular tables found, or a --fix aborted on a content mismatch
    2  a supplied path does not exist
"""
import argparse
import os
import re
import sys
import unicodedata

DEFAULT_ROOTS = ["Documentation"]
EMOJI_PRESENTATION = "\ufe0f"
ZERO_WIDTH_JOINER = "\u200d"
MIN_COLUMN_WIDTH = 3
SEPARATOR_CELL = re.compile(r":?-+:?")
FENCE = re.compile(r"^\s*(```|~~~)")


def display_width(text):
    width = 0
    for index, char in enumerate(text):
        if char in (EMOJI_PRESENTATION, ZERO_WIDTH_JOINER) or unicodedata.combining(char):
            continue
        followed_by_selector = index + 1 < len(text) and text[index + 1] == EMOJI_PRESENTATION
        if followed_by_selector or unicodedata.east_asian_width(char) in "WF":
            width += 2
        else:
            width += 1
    return width


def split_row(line):
    """Splits `| a | b |` into stripped cell texts the way GFM does: every unescaped `|` splits,
    INCLUDING one inside a backtick code span — only `\\|` keeps a pipe inside its cell."""
    body = line.strip()
    body = body[1:] if body.startswith("|") else body
    if body.endswith("|") and not body.endswith("\\|"):
        body = body[:-1]
    cells, current, index = [], "", 0
    while index < len(body):
        char = body[index]
        if char == "\\" and index + 1 < len(body):
            current += body[index:index + 2]
            index += 2
            continue
        if char == "|":
            cells.append(current.strip())
            current = ""
        else:
            current += char
        index += 1
    cells.append(current.strip())
    return cells


def is_separator(cells):
    return all(SEPARATOR_CELL.fullmatch(cell) for cell in cells)


SEPARATOR_ROW = 1  # GFM: the delimiter row is always the table's second line; a later all-dash row is data


def canonical(rows):
    """The separator row compares by its alignment markers only; its dash count is padding."""
    return [[(cell.startswith(":"), cell.endswith(":") and len(cell) > 1) for cell in row]
            if index == SEPARATOR_ROW else row for index, row in enumerate(rows)]


def align_table(rows):
    columns = max(len(row) for row in rows)
    rows = [row + [""] * (columns - len(row)) for row in rows]
    content_rows = [row for index, row in enumerate(rows) if index != SEPARATOR_ROW]
    widths = [max([MIN_COLUMN_WIDTH] + [display_width(row[i]) for row in content_rows])
              for i in range(columns)]
    separator = rows[SEPARATOR_ROW]
    centered = [len(cell) > 1 and cell.startswith(":") and cell.endswith(":") for cell in separator]

    lines = []
    for index, row in enumerate(rows):
        if index == SEPARATOR_ROW:
            cells = []
            for cell, width in zip(row, widths):
                left = ":" if cell.startswith(":") else "-"
                right = ":" if cell.endswith(":") and len(cell) > 1 else "-"
                cells.append(left + "-" * width + right)
        else:
            cells = []
            for cell, width, center in zip(row, widths, centered):
                gap = width - display_width(cell)
                if center:
                    cells.append(" " + " " * (gap // 2) + cell + " " * (gap - gap // 2) + " ")
                else:
                    cells.append(" " + cell + " " * gap + " ")
        lines.append("|" + "|".join(cells) + "|")
    return lines


def find_tables(lines):
    """Yields (start, end) line index ranges of top-level pipe tables outside fenced code."""
    in_fence, index = False, 0
    while index < len(lines):
        if FENCE.match(lines[index]):
            in_fence = not in_fence
        elif not in_fence and lines[index].startswith("|"):
            end = index
            while end < len(lines) and lines[end].startswith("|"):
                end += 1
            if end - index >= 2 and is_separator(split_row(lines[index + 1])):
                yield index, end
            index = end
            continue
        index += 1


def process_file(path, fix, first_line, last_line):
    with open(path, "rb") as handle:
        raw = handle.read().decode("utf-8")
    newline = "\r\n" if "\r\n" in raw else "\n"
    lines = raw.replace("\r\n", "\n").split("\n")

    misaligned, irregular = [], []
    for start, end in find_tables(lines):
        if not first_line <= start + 1 <= last_line:
            continue
        original = [line.rstrip() for line in lines[start:end]]
        rows = [split_row(line) for line in original]
        columns = len(rows[1])
        if len(rows[0]) != columns:
            # GFM recognizes a table only when the header and delimiter rows have the same cell count;
            # padding the header would turn rendered paragraph text into a table.
            print(f"IRREGULAR {path}:{start + 1}: the header has {len(rows[0])} cells, the delimiter row "
                  f"{columns} — GFM does not render this block as a table; fix by hand")
            irregular.append(start + 1)
            continue
        overfull = [start + offset + 1 for offset, row in enumerate(rows) if len(row) > columns]
        if overfull:
            # The renderer drops cells past the header's count; which column is missing is a
            # content decision, so the table is reported and left as it is.
            print(f"IRREGULAR {path}:{start + 1}: row(s) {', '.join(map(str, overfull))} have more "
                  f"than the header's {columns} cells — the extra cells do not render; fix by hand")
            irregular.append(start + 1)
            continue
        aligned = align_table(rows)
        if aligned != original:
            reparsed = canonical([split_row(line) for line in aligned])
            expected = canonical([row + [""] * (len(new) - len(row)) for row, new in zip(rows, reparsed)])
            if reparsed != expected:
                print(f"ABORT {path}:{start + 1}: re-split cells differ after aligning; file left untouched")
                return None
            misaligned.append(start + 1)
            lines[start:end] = aligned

    if misaligned and fix:
        with open(path, "wb") as handle:
            handle.write(newline.join(lines).encode("utf-8"))
    return misaligned, irregular


def collect(paths):
    for root in paths:
        if os.path.isfile(root):
            yield root
            continue
        for directory, _, names in os.walk(root):
            for name in sorted(names):
                if name.endswith(".md"):
                    yield os.path.join(directory, name)


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description="Check or fix Markdown table column alignment.")
    parser.add_argument("paths", nargs="*", help="files or directories (default: Documentation/)")
    parser.add_argument("--fix", action="store_true", help="rewrite misaligned tables in place")
    parser.add_argument("--lines", nargs=2, type=int, metavar=("FIRST", "LAST"),
                        help="only tables starting on these 1-based lines (single file)")
    args = parser.parse_args()

    roots = args.paths or DEFAULT_ROOTS
    missing = [root for root in roots if not os.path.exists(root)]
    if missing:
        print(f"error: path not found: {', '.join(missing)}", file=sys.stderr)
        return 2
    first_line, last_line = args.lines if args.lines else (1, sys.maxsize)

    table_count = file_count = aborted = irregular_count = 0
    for path in collect(roots):
        result = process_file(path, args.fix, first_line, last_line)
        if result is None:
            aborted += 1
            continue
        misaligned, irregular = result
        irregular_count += len(irregular)
        if misaligned:
            file_count += 1
            table_count += len(misaligned)
            verb = "aligned" if args.fix else "misaligned"
            print(f"{path}: {verb} table(s) at line(s) {', '.join(map(str, misaligned))}")

    if args.fix:
        print(f"Aligned {table_count} table(s) in {file_count} file(s); {aborted} file(s) aborted; "
              f"{irregular_count} irregular table(s) left for a hand fix.")
        return 1 if aborted or irregular_count else 0
    if table_count or aborted or irregular_count:
        print(f"{table_count} misaligned table(s) in {file_count} file(s); {aborted} file(s) unparseable; "
              f"{irregular_count} irregular table(s). Re-run with --fix for the misaligned ones.")
        return 1
    print("OK: every table is aligned.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
