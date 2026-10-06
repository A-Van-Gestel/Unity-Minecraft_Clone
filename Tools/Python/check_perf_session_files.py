"""Check the structure of the performance monitor's Capture-tier session and hitch files.

WHAT THIS DOES
    Reads a PerfLogs folder (Capture tier, PERFORMANCE_MONITOR_AND_LOGGER_OVERHAUL.md §4.7) and, per session:
      - session files (`PerfSession_<stamp>.csv` plus its `_partNN` continuations): every part carries the same
        header, which starts with the frame columns; every row has the header's column count; frame indices run
        consecutively across the parts. Reports rows, the frame range and how many rows have no GPU time;
      - hitch files (`<session>_hitchNNN_frame<F>.csv`): a `# hitch frame=<F>` summary line matching the file name,
        the session header plus a `mark` column, at most one window of rows with the header's column count, and
        exactly one `hitch` mark on the row of frame <F> and one `worst` mark.
    Exits 1 when any check fails, 0 otherwise.

WHAT IT READS / WRITES
    Reads only the CSV files in the given folder; writes only to stdout (file names, never absolute paths).

WHY IT EXISTS
    A session file can hold hundreds of thousands of rows across parts, so a dropped column, a gap in the frames or a
    mis-marked hitch row cannot be seen by eye. PM-6's Editor and Master confirmation checked their files this way.

DETERMINISM
    Files are read in sorted name order and every figure is an integer, so the same folder always produces
    byte-identical output.

RUN
    python Tools/Python/check_perf_session_files.py [<PerfLogs folder>] [--columns N]

    The folder defaults to %USERPROFILE%/AppData/LocalLow/johanaxel007/Minecraft Clone/PerfLogs, where the Editor and
    a Windows player write. `--columns` also requires every header to have exactly N columns (96 at PM-6: 9 frame
    columns, 31 slots, 56 counters).
"""

import argparse
import re
import sys
from pathlib import Path

DEFAULT_FOLDER = Path.home() / "AppData" / "LocalLow" / "johanaxel007" / "Minecraft Clone" / "PerfLogs"

# Mirrors PerfSessionExporter.FrameColumns, MarkColumn, HitchMark and WorstMark.
FRAME_COLUMNS = "frame,wall_ms,cpu_ms,gc_alloc_bytes,gc_state,gc_collections,gpu_ms,render_thread_ms,present_wait_ms"
MARK_COLUMN = "mark"
HITCH_MARK = "hitch"
WORST_MARK = "worst"
GPU_COLUMN = FRAME_COLUMNS.split(",").index("gpu_ms")

# Mirrors PerfHitchDetector.WindowFrames (120 before + the hitch + 30 after).
HITCH_WINDOW_FRAMES = 151

SESSION_PATTERN = re.compile(r"^(PerfSession_[0-9-]+_[0-9-]+?)(?:_part(\d+))?\.csv$")
HITCH_PATTERN = re.compile(r"^(PerfSession_[0-9-]+_[0-9-]+?)_hitch(\d+)_frame(-?\d+)\.csv$")
SUMMARY_PATTERN = re.compile(r"^# hitch frame=(-?\d+) ")


def read_lines(path):
    """Returns the file's lines without line endings."""
    with open(path, encoding="ascii") as file:
        return file.read().splitlines()


def check_session(name, parts, expected_columns):
    """Checks one session's part files in part order; returns (header, problems)."""
    problems = []
    header = None
    rows = 0
    rows_without_gpu = 0
    first_frame = None
    last_frame = None
    gaps = 0

    for part_number, path in parts:
        lines = read_lines(path)
        if not lines:
            problems.append(f"{path.name}: empty file")
            continue

        part_header = lines[0]
        if header is None:
            header = part_header
            if not header.startswith(FRAME_COLUMNS + ","):
                problems.append(f"{path.name}: header does not start with the frame columns")
            if expected_columns is not None and len(header.split(",")) != expected_columns:
                problems.append(f"{path.name}: header has {len(header.split(','))} columns, expected {expected_columns}")
        elif part_header != header:
            problems.append(f"{path.name}: header differs from the session's first part")

        columns = len(header.split(","))
        for line_number, line in enumerate(lines[1:], start=2):
            cells = line.split(",")
            if len(cells) != columns:
                problems.append(f"{path.name}:{line_number}: {len(cells)} cells, expected {columns}")
                continue

            frame = int(cells[0])
            if last_frame is not None and frame != last_frame + 1:
                gaps += 1
            if first_frame is None:
                first_frame = frame
            last_frame = frame
            rows += 1
            if cells[GPU_COLUMN] == "":
                rows_without_gpu += 1

    if gaps:
        problems.append(f"{name}: {gaps} gaps in the frame indices")

    frame_range = f"frames {first_frame}..{last_frame}" if rows else "no rows"
    print(f"{name}: {len(parts)} part(s), {rows} rows, {frame_range}, {rows_without_gpu} without GPU time")
    return header, problems


def check_hitch(path, session_header, file_frame, expected_columns):
    """Checks one hitch file; returns its problems."""
    problems = []
    lines = read_lines(path)
    if len(lines) < 3:
        return [f"{path.name}: fewer than a summary line, a header and one row"]

    summary = SUMMARY_PATTERN.match(lines[0])
    if summary is None:
        problems.append(f"{path.name}: first line is not a '# hitch frame=' summary")
    elif int(summary.group(1)) != file_frame:
        problems.append(f"{path.name}: summary frame {summary.group(1)} differs from the file name's {file_frame}")

    header = lines[1]
    if session_header is not None and header != session_header + "," + MARK_COLUMN:
        problems.append(f"{path.name}: header is not the session header plus '{MARK_COLUMN}'")
    elif not header.endswith("," + MARK_COLUMN):
        problems.append(f"{path.name}: header does not end with '{MARK_COLUMN}'")
    if expected_columns is not None and len(header.split(",")) != expected_columns + 1:
        problems.append(f"{path.name}: header has {len(header.split(','))} columns, expected {expected_columns + 1}")

    rows = lines[2:]
    if len(rows) > HITCH_WINDOW_FRAMES:
        problems.append(f"{path.name}: {len(rows)} rows, more than a {HITCH_WINDOW_FRAMES}-frame window")

    columns = len(header.split(","))
    hitch_frames = []
    worst_count = 0
    for line_number, line in enumerate(rows, start=3):
        cells = line.split(",")
        if len(cells) != columns:
            problems.append(f"{path.name}:{line_number}: {len(cells)} cells, expected {columns}")
            continue

        marks = cells[-1].split()
        if HITCH_MARK in marks:
            hitch_frames.append(int(cells[0]))
        if WORST_MARK in marks:
            worst_count += 1

    if hitch_frames != [file_frame]:
        problems.append(f"{path.name}: hitch mark on frames {hitch_frames}, expected exactly [{file_frame}]")
    if worst_count != 1:
        problems.append(f"{path.name}: {worst_count} rows marked '{WORST_MARK}', expected 1")
    return problems


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("folder", nargs="?", type=Path, default=DEFAULT_FOLDER, help="the PerfLogs folder")
    parser.add_argument("--columns", type=int, default=None, help="require every session header to have N columns")
    args = parser.parse_args()

    if not args.folder.is_dir():
        print(f"Not a folder: {args.folder}")
        return 1

    sessions = {}
    hitches = {}
    unrecognized = []
    for path in sorted(args.folder.glob("*.csv")):
        hitch = HITCH_PATTERN.match(path.name)
        if hitch:
            hitches.setdefault(hitch.group(1), []).append((int(hitch.group(2)), int(hitch.group(3)), path))
            continue

        session = SESSION_PATTERN.match(path.name)
        if session:
            part_number = int(session.group(2)) if session.group(2) else 1
            sessions.setdefault(session.group(1), []).append((part_number, path))
        else:
            unrecognized.append(path.name)

    problems = [f"{name}: not a session or hitch file name" for name in unrecognized]
    hitch_files = 0
    for name in sorted(set(sessions) | set(hitches)):
        header = None
        if name in sessions:
            header, session_problems = check_session(name, sorted(sessions[name]), args.columns)
            problems.extend(session_problems)
        else:
            print(f"{name}: hitch files only, no session file")

        for _, file_frame, path in sorted(hitches.get(name, [])):
            problems.extend(check_hitch(path, header, file_frame, args.columns))
            hitch_files += 1

    for problem in problems:
        print(f"PROBLEM {problem}")
    print(f"{len(sessions)} session(s), {hitch_files} hitch file(s), {len(problems)} problem(s)")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
