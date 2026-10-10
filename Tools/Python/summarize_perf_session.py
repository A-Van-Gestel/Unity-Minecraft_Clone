"""Summarize a Capture-tier performance session per benchmark phase: slots, counters, jobs, disk I/O and hitches.

WHAT THIS DOES
    Joins one benchmark report (`BenchmarkRun_*.log`) with the Capture-tier session it recorded
    (`PerfLogs/PerfSession_<stamp>.csv` plus its `_partNN` continuations and `_hitchNNN_frame<F>.csv` files,
    PERFORMANCE_MONITOR_AND_LOGGER_OVERHAUL.md §4.7). The report's "Frame Health (every frame)" tables give each
    phase's frame range [first, end); every session row in a range belongs to that phase. Per phase it prints, as
    Markdown tables:
      - frames, wall / CPU / GPU / present-wait percentiles, managed allocation and collections;
      - every slot that recorded time: mean ms per frame, p99 and worst;
      - the gauges (queues, jobs in flight, residency, pools): mean, p99 and max;
      - per job type: jobs completed, mean latency, mean worker busy time per job, busy ms per wall second;
      - disk I/O per operation, the behavior tick's six parts, and the pool misses;
      - the hitch files whose hitch frame lies in the phase: how many, the worst, and which slot led them.
    It then cross-checks every phase against the report's own Frame Health row: the frame count must match exactly and
    wall p50 / p99 / worst within one printed unit (0.1 ms; the session file rounds to 0.001 ms). Exits 1 on any
    mismatch or when a phase has no rows, 0 otherwise.

WHAT IT READS / WRITES
    Reads the report and the session's CSV files; writes Markdown to stdout, or to --out.

WHY IT EXISTS
    The benchmark report carries frame-level statistics per phase only (PM-6 decision 4); slot, counter and job
    figures per phase exist only in the session file, which runs to hundreds of thousands of rows. ES-0's
    re-measurement reads every phase this way. The cross-check guards against a phase being cut from the wrong rows.

DETERMINISM
    Files are read in sorted name order, percentiles are nearest-rank (the store's own definition) and every figure
    uses fixed formatting, so the same inputs always produce byte-identical output.

RUN
    python Tools/Python/summarize_perf_session.py --report <BenchmarkRun_*.log> [--folder <PerfLogs>]
        [--session PerfSession_<stamp>] [--out <file.md>]

    The folder defaults to %USERPROFILE%/AppData/LocalLow/johanaxel007/Minecraft Clone/PerfLogs; the session defaults
    to the newest one in it.
"""

import argparse
import csv
import math
import re
import sys
from array import array
from collections import Counter
from pathlib import Path

DEFAULT_FOLDER = Path.home() / "AppData" / "LocalLow" / "johanaxel007" / "Minecraft Clone" / "PerfLogs"

# Mirrors PerfSessionExporter.FrameColumns.
FRAME_COLUMNS = ["frame", "wall_ms", "cpu_ms", "gc_alloc_bytes", "gc_state", "gc_collections", "gpu_ms",
                 "render_thread_ms", "present_wait_ms"]
SLOT_SUFFIX = "_ms"

# Mirrors PerfCounter: the gauges hold a level; every other counter is a per-frame count.
GAUGES = ["GenerationQueue", "GenerationInFlight", "LightReady", "LightWaiting", "LightInFlight", "MeshQueue",
          "MeshInFlight", "ModificationQueue", "ResidentChunks", "ActiveChunks", "ActiveSections", "JobArraysPooled",
          "MeshOutputsPooled", "IoInFlight"]
JOB_TYPES = ["Generation", "Light", "Mesh", "Fluid", "FluidSoundScan"]
TICK_PARTS = ["TickListUs", "TickPrepareUs", "TickScheduleUs", "TickWaitUs", "TickGrassUs", "TickFluidReplayUs"]
POOL_MISSES = ["SectionPoolMisses", "DataPoolMisses", "JobArrayMisses", "MeshOutputMisses", "SaveBufferMisses",
               "FluidTickerPoolMisses", "UntimedJobs"]

FRAME_HEALTH_TITLE = re.compile(r"^=== (.+?) — Frame Health \(every frame\) ===$")
COLUMN_SPLIT = re.compile(r"\s{2,}")
MS_VALUE = re.compile(r"^(-?[\d\s.,  ]*?)[.,](\d)\s*ms$")
SESSION_PATTERN = re.compile(r"^(PerfSession_[0-9-]+_[0-9-]+?)(?:_part(\d+))?\.csv$")
HITCH_PATTERN = re.compile(r"^(PerfSession_[0-9-]+_[0-9-]+?)_hitch(\d+)_frame(-?\d+)\.csv$")
HITCH_SUMMARY = re.compile(r"^# hitch frame=(-?\d+) threshold_ms=([\d.]*) worst_ms=([\d.]*) hitch_frames=(\d+) "
                           r"gc_correlated=(true|false) top=(.*)$")

PRINT_TOLERANCE_MS = 0.1 + 1e-6
MIN_SLOT_MEAN_MS = 0.0005
MEDIAN = 50
HIGH = 99
US_PER_MS = 1000.0
BYTES_PER_MB = 1024.0 * 1024.0
MS_PER_SECOND = 1000.0


class Phase:
    """One report phase and everything gathered from its session rows."""

    def __init__(self, group, name, first, end, frames, wall_p50, wall_p99, wall_worst):
        self.group, self.name = group, name
        self.first, self.end = first, end
        self.report_frames = frames
        self.report_wall = (wall_p50, wall_p99, wall_worst)
        self.values = {}      # column -> array('d') of non-empty values
        self.sums = Counter()  # per-frame counter -> sum over the phase
        self.rows = 0
        self.collections = 0
        self.hitches = []

    @property
    def label(self):
        return f"{self.group} / {self.name}"

    def add(self, column, value):
        self.values.setdefault(column, array("d")).append(value)


def parse_count(text):
    """An integer printed with any culture's group separator."""
    digits = re.sub(r"\D", "", text)
    if not digits:
        raise ValueError(f"not a count: {text!r}")
    return int(digits)


def parse_ms(text):
    """A one-decimal millisecond value printed with any culture's separators, e.g. '1 046,3 ms'."""
    match = MS_VALUE.match(text.strip())
    if not match:
        raise ValueError(f"not a millisecond value: {text!r}")
    return float(re.sub(r"\D", "", match.group(1)) + "." + match.group(2))


def parse_report(lines):
    """Reads every Frame Health row that carries a frame range; returns the phases in report order."""
    phases = []
    group, header = None, None
    for line in lines:
        title = FRAME_HEALTH_TITLE.match(line.strip())
        if title:
            group, header = title.group(1), None
            continue
        if group is None:
            continue
        if not line.strip():
            group = None
            continue

        cells = COLUMN_SPLIT.split(line.strip())
        if header is None:
            header = cells
            continue
        if len(cells) != len(header) or "First frame" not in header:
            continue

        row = dict(zip(header, cells))
        phases.append(Phase(group, row["Phase"], int(row["First frame"]), int(row["End frame"]),
                            parse_count(row["Frames"]), parse_ms(row["Wall p50"]), parse_ms(row["Wall p99"]),
                            parse_ms(row["Worst"])))
    return phases


def session_files(folder, session):
    """The session's part files in order and its hitch files by number; the newest session when none is named."""
    parts, hitches = {}, {}
    for path in sorted(folder.iterdir()):
        match = SESSION_PATTERN.match(path.name)
        if match:
            parts.setdefault(match.group(1), []).append((int(match.group(2) or 1), path))
            continue
        match = HITCH_PATTERN.match(path.name)
        if match:
            hitches.setdefault(match.group(1), []).append((int(match.group(2)), path))
    if not parts:
        raise FileNotFoundError(f"no session files in {folder.name}")
    name = session or sorted(parts)[-1]
    if name not in parts:
        raise FileNotFoundError(f"no session named {name}")
    return name, [p for _, p in sorted(parts[name])], [p for _, p in sorted(hitches.get(name, []))]


def gather(phases, part_paths):
    """Routes every session row into its phase; returns the header and the count of rows outside every phase."""
    ordered = sorted(phases, key=lambda p: p.first)
    header, outside = None, 0
    for path in part_paths:
        with open(path, encoding="ascii", newline="") as file:
            reader = csv.reader(file)
            part_header = next(reader)
            header = header or part_header
            slot_columns = [(i, c) for i, c in enumerate(part_header)
                            if i >= len(FRAME_COLUMNS) and c.endswith(SLOT_SUFFIX)]
            counter_columns = [(i, c) for i, c in enumerate(part_header)
                               if i >= len(FRAME_COLUMNS) and not c.endswith(SLOT_SUFFIX)]
            for row in reader:
                frame = int(row[0])
                phase = next((p for p in ordered if p.first <= frame < p.end), None)
                if phase is None:
                    outside += 1
                    continue
                phase.rows += 1
                for index, column in enumerate(FRAME_COLUMNS[1:], start=1):
                    if row[index] != "" and column not in ("gc_state", "gc_collections"):
                        phase.add(column, float(row[index]))
                phase.collections += int(row[FRAME_COLUMNS.index("gc_collections")] or 0)
                for index, column in slot_columns:
                    if row[index] != "":
                        phase.add(column[:-len(SLOT_SUFFIX)], float(row[index]))
                for index, column in counter_columns:
                    if row[index] == "":
                        continue
                    value = int(row[index])
                    if column in GAUGES:
                        phase.add(column, value)
                    else:
                        phase.sums[column] += value
    return header, outside


def attach_hitches(phases, hitch_paths):
    """Adds each hitch file's summary to the phase holding its hitch frame."""
    for path in hitch_paths:
        with open(path, encoding="ascii") as file:
            match = HITCH_SUMMARY.match(file.readline().rstrip("\r\n"))
        if not match:
            raise ValueError(f"{path.name}: no hitch summary line")
        frame = int(match.group(1))
        top = [entry.split(":")[0] for entry in match.group(6).split(";") if entry]
        for phase in phases:
            if phase.first <= frame < phase.end:
                phase.hitches.append((float(match.group(3) or "nan"), match.group(5) == "true", top))
                break


def nearest_rank(values, percentile):
    """The store's percentile: the value at rank ceil(p·n/100) of the sorted window."""
    if not values:
        return math.nan
    ordered = sorted(values)
    rank = (percentile * len(ordered) + 99) // 100
    return ordered[max(rank - 1, 0)]


def mean(values):
    return sum(values) / len(values) if values else math.nan


def fmt(value, decimals=2):
    return "n/a" if value is None or (isinstance(value, float) and math.isnan(value)) else f"{value:.{decimals}f}"


def table(header, rows):
    lines = ["| " + " | ".join(header) + " |", "|" + "|".join("---" for _ in header) + "|"]
    lines += ["| " + " | ".join(str(c) for c in row) + " |" for row in rows]
    return "\n".join(lines)


def phase_overview(phases):
    rows = []
    for p in phases:
        wall, cpu = p.values.get("wall_ms", []), p.values.get("cpu_ms", [])
        gpu, present = p.values.get("gpu_ms", []), p.values.get("present_wait_ms", [])
        alloc = p.values.get("gc_alloc_bytes", [])
        seconds = sum(wall) / MS_PER_SECOND
        rows.append([p.label, p.rows, fmt(seconds, 1), fmt(nearest_rank(wall, MEDIAN)), fmt(nearest_rank(wall, HIGH)),
                     fmt(max(wall) if wall else math.nan), fmt(nearest_rank(cpu, MEDIAN)), fmt(nearest_rank(cpu, HIGH)),
                     fmt(mean(alloc) / 1024.0 if alloc else math.nan, 1), p.collections,
                     fmt(nearest_rank(gpu, MEDIAN)), fmt(nearest_rank(gpu, HIGH)), fmt(nearest_rank(present, MEDIAN))])
    return table(["Phase", "Frames", "Wall s", "Wall p50", "Wall p99", "Worst", "CPU p50", "CPU p99", "GC KB/frame",
                  "GCs", "GPU p50", "GPU p99", "Present p50"], rows)


def slot_tables(phases, slots):
    """Mean, p99 and worst per slot, one row per slot, phases as column groups."""
    sections = []
    for p in phases:
        cpu_mean = mean(p.values.get("cpu_ms", []))
        rows = []
        for slot in slots:
            values = p.values.get(slot, [])
            slot_mean = sum(values) / p.rows if p.rows else math.nan
            if not values or slot_mean < MIN_SLOT_MEAN_MS:
                continue
            nonzero = sum(1 for v in values if v > 0)
            share = 100.0 * slot_mean / cpu_mean if cpu_mean and cpu_mean > 0 else math.nan
            rows.append((slot_mean, [slot, fmt(slot_mean, 3), fmt(share, 1), fmt(nearest_rank(values, HIGH), 2),
                                     fmt(max(values), 2), nonzero]))
        rows.sort(key=lambda r: -r[0])
        sections.append(f"#### {p.label}\n\n" + table(["Slot", "Mean ms", "% of CPU", "p99 ms", "Worst ms",
                                                         "Frames > 0"], [r[1] for r in rows]))
    return "\n\n".join(sections)


def gauge_table(phases):
    rows = []
    for gauge in GAUGES:
        cells = [gauge]
        for p in phases:
            values = p.values.get(gauge, [])
            cells.append(f"{fmt(mean(values), 1)} / {fmt(nearest_rank(values, HIGH), 0)} / "
                         f"{fmt(max(values) if values else math.nan, 0)}")
        rows.append(cells)
    return table(["Gauge (mean / p99 / max)"] + [p.label for p in phases], rows)


def job_table(phases):
    rows = []
    for p in phases:
        seconds = sum(p.values.get("wall_ms", [])) / MS_PER_SECOND
        for job in JOB_TYPES:
            completed = p.sums[f"{job}Completed"]
            if not completed:
                continue
            latency = p.sums[f"{job}LatencyUs"] / completed / US_PER_MS
            busy = p.sums[f"{job}BusyUs"] / completed / US_PER_MS
            busy_per_s = p.sums[f"{job}BusyUs"] / US_PER_MS / seconds if seconds else math.nan
            rows.append([p.label, job, completed, fmt(latency), fmt(busy, 3), fmt(busy_per_s, 1)])
    return table(["Phase", "Job", "Completed", "Mean latency ms", "Mean busy ms/job", "Busy ms per wall s"], rows)


def per_op(total_us, ops):
    return fmt(total_us / ops / US_PER_MS, 3) if ops else "n/a"


def io_table(phases):
    rows = []
    for p in phases:
        s = p.sums
        hits, misses, saves = s["DiskLoadHits"], s["DiskLoadMisses"], s["DiskSaves"]
        rows.append([p.label, hits, misses, per_op(s["DiskReadUs"], hits), per_op(s["DeserializeUs"], hits),
                     fmt(s["DiskLoadBytes"] / BYTES_PER_MB, 1), saves, per_op(s["SerializeUs"], saves),
                     per_op(s["DiskWriteUs"], saves), fmt(s["DiskSaveBytes"] / BYTES_PER_MB, 1),
                     per_op(s["IoQueueWaitUs"], s["IoBackgroundOps"])])
    return table(["Phase", "Load hits", "Misses", "Read ms/hit", "Deserialize ms/hit", "MB loaded", "Saves",
                  "Serialize ms/save", "Write ms/save", "MB saved", "Pool wait ms/op"], rows)


def tick_table(phases):
    rows = []
    for p in phases:
        frames = p.rows or 1
        tick = sum(p.values.get("Tick", [])) / frames
        parts = [p.sums[part] / frames / US_PER_MS for part in TICK_PARTS]
        rows.append([p.label, fmt(tick, 3)] + [fmt(v, 3) for v in parts] +
                    [fmt(p.sums["TickFluidChunks"] / frames, 1), fmt(p.sums["TickSnapshotKb"] / frames / 1024.0, 2),
                     fmt(p.sums["TickGrassVoxels"] / frames, 0)])
    return table(["Phase", "Tick ms", "List", "Prepare", "Schedule", "Wait", "Grass", "Replay",
                  "Fluid chunks/frame", "Snapshot MB/frame", "Grass voxels/frame"], rows)


def pool_table(phases):
    rows = [[p.label] + [p.sums[name] for name in POOL_MISSES] for p in phases]
    return table(["Phase"] + POOL_MISSES, rows)


def hitch_table(phases):
    rows = []
    for p in phases:
        if not p.hitches:
            rows.append([p.label, 0, "n/a", 0, "—"])
            continue
        leaders = Counter(top[0] for _, _, top in p.hitches if top)
        lead = ", ".join(f"{slot} {count}" for slot, count in sorted(leaders.items(), key=lambda kv: (-kv[1], kv[0])))
        worst = max(w for w, _, _ in p.hitches)
        rows.append([p.label, len(p.hitches), fmt(worst, 1), sum(1 for _, gc, _ in p.hitches if gc), lead or "—"])
    return table(["Phase", "Hitch files", "Worst ms", "GC-correlated", "Led by (records)"], rows)


def cross_check(phases):
    """Frame count exact and wall p50/p99/worst within one printed unit of the report; returns the problem lines."""
    problems = []
    for p in phases:
        if p.rows == 0:
            problems.append(f"{p.label}: no session rows in [{p.first}, {p.end})")
            continue
        wall = p.values.get("wall_ms", [])
        mine = (nearest_rank(wall, MEDIAN), nearest_rank(wall, HIGH), max(wall))
        if p.rows != p.report_frames:
            problems.append(f"{p.label}: {p.rows} session rows, report counts {p.report_frames}")
        for label, ours, theirs in zip(("p50", "p99", "worst"), mine, p.report_wall):
            if abs(ours - theirs) > PRINT_TOLERANCE_MS:
                problems.append(f"{p.label}: wall {label} {ours:.3f} ms, report {theirs:.1f} ms")
    return problems


def summarize(report_lines, folder, session=None):
    """Returns (markdown, problems) for one report and its session."""
    phases = parse_report(report_lines)
    if not phases:
        return "", ["the report has no Frame Health rows with a frame range"]
    name, parts, hitch_paths = session_files(folder, session)
    header, outside = gather(phases, parts)
    attach_hitches(phases, hitch_paths)
    slots = [c[:-len(SLOT_SUFFIX)] for c in header[len(FRAME_COLUMNS):] if c.endswith(SLOT_SUFFIX)]
    problems = cross_check(phases)

    out = [f"### Session {name} — {len(parts)} part(s), {len(hitch_paths)} hitch file(s), "
           f"{outside} rows outside every phase", "",
           "Cross-check against the report: " + ("every phase matches." if not problems else "MISMATCH — see below."),
           "", phase_overview(phases), "", "### Slots per phase (mean over every frame of the phase)", "",
           slot_tables(phases, slots), "", "### Gauges", "", gauge_table(phases), "", "### Jobs", "",
           job_table(phases), "", "### Disk I/O", "", io_table(phases), "", "### Behavior tick (ms per frame)", "",
           tick_table(phases), "", "### Pool misses and untimed jobs (phase totals)", "", pool_table(phases), "",
           "### Hitch records", "", hitch_table(phases)]
    if problems:
        out += ["", "### Cross-check problems", ""] + [f"- {line}" for line in problems]
    return "\n".join(out) + "\n", problems


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("--report", required=True, type=Path, help="the BenchmarkRun_*.log the session recorded")
    parser.add_argument("--folder", type=Path, default=DEFAULT_FOLDER, help="the PerfLogs folder")
    parser.add_argument("--session", help="PerfSession_<stamp> (default: the newest in the folder)")
    parser.add_argument("--out", type=Path, help="write the Markdown here instead of stdout")
    args = parser.parse_args(argv)

    try:
        lines = args.report.read_text(encoding="utf-8", errors="replace").splitlines()
        markdown, problems = summarize(lines, args.folder, args.session)
    except (OSError, ValueError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 2

    if args.out:
        args.out.write_text(markdown, encoding="utf-8")
    else:
        sys.stdout.write(markdown)
    for line in problems:
        print(f"MISMATCH: {line}", file=sys.stderr)
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
