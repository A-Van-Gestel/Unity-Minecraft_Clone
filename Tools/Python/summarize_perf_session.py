"""Summarize a Capture-tier performance session per benchmark phase: slots, counters, jobs, disk I/O and hitches.

WHAT THIS DOES
    Joins one benchmark report (`BenchmarkRun_*.log`) with the Capture-tier session it recorded
    (`PerfLogs/PerfSession_<stamp>.csv` plus its `_partNN` continuations and `_hitchNNN_frame<F>.csv` files,
    PERFORMANCE_MONITOR_AND_LOGGER_OVERHAUL.md §4.7). The report's "Frame Health (every frame)" tables give each
    phase's frame range [first, end); every session row in a range belongs to that phase. Per phase it prints, as
    Markdown tables:
      - frames, wall / CPU / GPU / present-wait percentiles, frames over fixed 33 / 50 ms bars, managed allocation and
        collections;
      - every slot that recorded time: mean ms per frame, p99 and worst;
      - the gauges (queues, jobs in flight, residency, pools): mean, p99 and max;
      - per job type: jobs completed, mean latency, mean worker busy time per job, busy ms per wall second;
      - disk I/O split by load hits and misses (read cost, lock wait, region opens, saves and their load-pool queue)
        and the job-array pools (allocations, frees, churn and peak maps in demand);
      - the behavior tick's six parts and the lighting merge;
      - the two view-distance crossings priced apart: each pass's parts per crossing, and what its passes did per crossing;
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
    uses fixed formatting, so the same inputs always produce byte-identical output. Counters whose columns a header
    lacks sum to an empty 0, so a phase of a session in the older format prints `—` for them rather than a figure
    the format never measured.

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
# Mirrors PerfRenderStatsSource.Bindings: gauges too, reporting the previous rendered frame.
RENDER_GAUGES = ["RenderSrpBatcherDraws", "RenderStandardDraws", "RenderSetPassCalls", "RenderTriangles",
                 "RenderVertices", "RenderVertexUploadBytes", "RenderIndexUploadBytes"]
JOB_TYPES = ["Generation", "Light", "Mesh", "Fluid", "FluidSoundScan"]
TICK_PARTS = ["TickListUs", "TickPrepareUs", "TickScheduleUs", "TickWaitUs", "TickGrassUs", "TickFluidReplayUs"]
# MergeCountsUs runs inside MergeApplyUs, so it is a column of its own rather than a part.
MERGE_PARTS = ["MergeApplyUs", "MergeDrainUs", "MergePullBackUs", "MergeRouteUs", "MergeCascadeUs"]
# The two passes a view-distance crossing runs, each timed by parts charged to one crossing rather than one frame.
XING_PARTS = ["XingCloudsUs", "XingSpiralUs", "XingDeactScanUs", "XingDeactivateUs", "XingActivateUs", "XingActiveSetUs"]
UNLOAD_PARTS = ["UnloadScanUs", "UnloadDecideUs", "UnloadBookkeepUs", "UnloadSaveUs", "UnloadVisualUs",
                "UnloadDataReturnUs"]
XING_NAMES = ["Clouds", "Spiral", "DeactScan", "Deactivate", "Activate", "ActiveSet"]
UNLOAD_NAMES = ["Scan", "Decide", "Bookkeep", "Save", "Visual", "DataReturn"]
CROSSING_COUNTS = ["XingGenEnqueued", "XingActivations", "XingDeactivations", "UnloadCandidates", "UnloadedChunks",
                   "UnloadSaved", "UnloadSavedSections", "UnloadDeferJob", "UnloadDeferStrand"]
CROSSING_COUNT_NAMES = ["Generation enqueued", "Activations", "Deactivations", "Candidates", "Unloaded", "Saved",
                        "Saved sections", "Deferred job", "Deferred strand"]
POOL_MISSES = ["SectionPoolMisses", "DataPoolMisses", "JobArrayMisses", "MeshOutputMisses", "SaveBufferMisses",
               "FluidTickerPoolMisses", "UntimedJobs"]
JOB_ARRAY_COUNTERS = ["JobArrayCapDisposes", "JobArrayPaddedMisses", "JobArrayAllocUs", "JobArrayFreeUs"]
# One job maps one center + eight neighbor maps per type, so a job in flight accounts for nine job arrays.
JOB_ARRAY_MAPS_PER_JOB = 9
JOB_ARRAY_IN_FLIGHT = ["LightInFlight", "MeshInFlight"]

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
# The µs columns carry less precision than the ms ones: their unit is already a thousandth of a millisecond.
US_DECIMALS = 1
BYTES_PER_MB = 1024.0 * 1024.0
MS_PER_SECOND = 1000.0
# Fixed slow-frame bars: the report's hitch threshold scales with the median, so legs with different medians are
# judged against different bars; these stay comparable across legs.
SLOW_FRAME_MS = (33.0, 50.0)
# A counter the session's header has no column for prints this instead of the 0 its empty Counter would give.
DASH = "—"


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
        self.peak_demand = 0.0  # the frame of the phase whose job arrays were in most demand
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
    """Routes every session row into its phase, taking the peak job-array demand row by row; returns the header
    and the count of rows outside every phase."""
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
                # Light and Mesh in flight peak on different frames, so the frame wanting the most job arrays has to
                # be taken row by row here: pool_table could only add the two levels' separate maxima.
                in_flight = {}
                for index, column in counter_columns:
                    if row[index] == "":
                        continue
                    value = int(row[index])
                    if column in GAUGES or column in RENDER_GAUGES:
                        phase.add(column, value)
                        if column in JOB_ARRAY_IN_FLIGHT:
                            in_flight[column] = value
                    else:
                        phase.sums[column] += value
                if len(in_flight) == len(JOB_ARRAY_IN_FLIGHT):
                    phase.peak_demand = max(phase.peak_demand, sum(in_flight.values()) * JOB_ARRAY_MAPS_PER_JOB)
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
                     fmt(max(wall) if wall else math.nan), *(sum(1 for w in wall if w > bar) for bar in SLOW_FRAME_MS),
                     fmt(nearest_rank(cpu, MEDIAN)), fmt(nearest_rank(cpu, HIGH)),
                     fmt(mean(alloc) / 1024.0 if alloc else math.nan, 1), p.collections,
                     fmt(nearest_rank(gpu, MEDIAN)), fmt(nearest_rank(gpu, HIGH)), fmt(nearest_rank(present, MEDIAN))])
    return table(["Phase", "Frames", "Wall s", "Wall p50", "Wall p99", "Worst",
                  *(f"> {bar:g} ms" for bar in SLOW_FRAME_MS), "CPU p50", "CPU p99", "GC KB/frame",
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


def gauge_table(phases, gauges=GAUGES):
    rows = []
    for gauge in gauges:
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


def ms_per(sums, name, ops, present):
    """One counter's ms per unit; `—` for a column the header lacks, `n/a` when the divisor is empty."""
    if name not in present:
        return DASH
    return per_op(sums[name], ops)


def us_per(sums, name, ops, present):
    """One counter's µs per unit; `—` for a column the header lacks, `n/a` when the divisor is empty."""
    if name not in present:
        return DASH
    return fmt(sums[name] / ops, US_DECIMALS) if ops else "n/a"


def count_per(sums, name, unit, present):
    """A work count's figure per unit; `—` for a column the header lacks, `n/a` when the divisor is empty."""
    if name not in present:
        return DASH
    return fmt(sums[name] / unit, 2) if unit else "n/a"


def us_per_total(sums, names, ops, present):
    """A rate over counters read as one figure; `—` unless the header carries every one of them."""
    if any(name not in present for name in names):
        return DASH
    return fmt(sum(sums[name] for name in names) / ops, US_DECIMALS) if ops else "n/a"


def us_per_remainder(sums, total, part, ops, present):
    """µs per unit for the part of `total` that `part` does not account for; both columns are needed, as a missing
    `part` would otherwise blame every read on the loads that did hit."""
    if total not in present or part not in present:
        return DASH
    return fmt((sums[total] - sums[part]) / ops, US_DECIMALS) if ops else "n/a"


def phase_total(sums, name, present):
    """A counter's phase total; `—` for a column the header lacks, so an empty sum is not read as a measured zero."""
    return DASH if name not in present else sums[name]


def mb(sums, name, present):
    """A bytes counter's megabytes for the phase; `—` for a column the header lacks."""
    if name not in present:
        return DASH
    return fmt(sums[name] / BYTES_PER_MB, 1)


def io_table(phases, present):
    """Disk I/O per phase, with the loads that found their chunk on disk and the ones that did not priced apart."""
    rows = []
    for p in phases:
        s = p.sums
        hits, misses, saves = s["DiskLoadHits"], s["DiskLoadMisses"], s["DiskSaves"]
        loads = hits + misses
        opens = s["RegionOpens"]
        rows.append([p.label, hits, misses,
                     us_per(s, "DiskReadUs", loads, present),
                     ms_per(s, "DiskReadHitUs", hits, present),
                     us_per_remainder(s, "DiskReadUs", "DiskReadHitUs", misses, present),
                     us_per(s, "LoadLockWaitUs", loads, present),
                     phase_total(s, "RegionOpens", present),
                     ms_per(s, "RegionOpenUs", opens, present),
                     ms_per(s, "DeserializeUs", hits, present),
                     mb(s, "DiskLoadBytes", present),
                     saves,
                     ms_per(s, "SerializeUs", saves, present),
                     ms_per(s, "DiskWriteUs", saves, present),
                     ms_per(s, "SaveLockWaitUs", saves, present),
                     mb(s, "DiskSaveBytes", present),
                     ms_per(s, "IoQueueWaitUs", s["IoBackgroundOps"], present),
                     ms_per(s, "IoLoadQueueWaitUs", s["IoLoadOps"], present),
                     ms_per(s, "DiskLoadLatencyUs", s["DiskLoadLatencyOps"], present)])
    return table(["Phase", "Load hits", "Misses", "Read µs/load", "Read ms/hit", "Read µs/miss", "Lock wait µs/load",
                  "Region opens", "Open ms/open", "Deserialize ms/hit", "MB loaded", "Saves", "Serialize ms/save",
                  "Write ms/save", "Save lock wait ms/save", "MB saved", "Pool wait ms/op", "Load pool wait ms/op",
                  "Load latency ms"], rows)


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


def merge_table(phases):
    rows = []
    for p in phases:
        frames = p.rows or 1
        merges = p.sums["MergeJobs"]
        merge = sum(p.values.get("LightMerge", [])) / frames
        parts = [p.sums[part] / frames / US_PER_MS for part in MERGE_PARTS]
        per_merge = [fmt(p.sums[name] / merges, 2) if merges else "n/a"
                     for name in ["MergeOutOfBandSections", "MergeChurnedSections"]]
        rows.append([p.label, fmt(merge, 3)] + [fmt(v, 3) for v in parts] +
                    [fmt(merge - sum(parts), 3), fmt(p.sums["MergeCountsUs"] / frames / US_PER_MS, 3),
                     fmt(merges / frames, 1), per_op(p.sums["MergeApplyUs"], merges),
                     p.sums["MergeStable"], p.sums["MergeStableUnchanged"], p.sums["MergeSelfRemeshes"],
                     p.sums["MergeNeighborRemeshes"], p.sums["MergeUnchangedRemeshes"]] + per_merge +
                    [p.sums["UnloadPendingSkyChunks"], p.sums["UnloadPendingSkyNodes"],
                     p.sums["MergeCountChecks"], p.sums["MergeCountDrifts"],
                     p.sums["MergeGateSkippable"], p.sums["MergeGateDiagonals"],
                     p.sums.get("MergeCeilingDeferred", 0)])
    return table(["Phase", "LightMerge ms", "Apply", "Drain", "PullBack", "Route", "Cascade", "Other",
                  "of Apply: counts", "Merges/frame", "Apply ms/merge", "Stable", "Stable unchanged",
                  "Self remeshes", "Neighbor remeshes", "By unchanged", "Out-of-band sections/merge",
                  "Churned/merge", "Unloads with sky pending", "Their nodes", "Count checks",
                  "Count drifts", "Gate skipped", "Gate diagonals", "Ceiling deferred"], rows)


def crossing_table(phases, present):
    """The two passes one crossing runs, priced per crossing: a crossing, unlike a frame, only happens when the
    player leaves a chunk, so its cost belongs to that event rather than divided by the phase's frame count."""
    rows = []
    for p in phases:
        s = p.sums
        frames = p.rows or 1
        crossings = s["Crossings"]

        view = sum(p.values.get("ViewDistance", [])) / crossings if crossings else math.nan
        unload = sum(p.values.get("Unload", [])) / crossings if crossings else math.nan
        view_parts = [s[part] / crossings / US_PER_MS if crossings else math.nan for part in XING_PARTS]
        unload_parts = [s[part] / crossings / US_PER_MS if crossings else math.nan for part in UNLOAD_PARTS]

        rows.append([p.label, phase_total(s, "Crossings", present), fmt(view, 3)] + [fmt(v, 3) for v in view_parts] +
                    [fmt(view - sum(view_parts), 3), fmt(unload, 3)] + [fmt(v, 3) for v in unload_parts] +
                    [fmt(unload - sum(unload_parts), 3)] +
                    [count_per(s, name, crossings, present) for name in CROSSING_COUNTS] +
                    [ms_per(s, "SaveReturnUs", frames, present),
                     us_per(s, "SaveReturnUs", s["SaveReturns"], present)])
    return table(["Phase", "Crossings", "ViewDistance ms/crossing", *XING_NAMES, "View Other", "Unload ms/crossing",
                  *UNLOAD_NAMES, "Unload Other", *(f"{name}/crossing" for name in CROSSING_COUNT_NAMES),
                  "Save return ms/frame", "Save return µs/return"], rows)


def pool_table(phases, present):
    """Pool misses and job-array churn per phase, plus the maps the phase's most demanding frame wanted."""
    # The demand column comes from the two in-flight gauges, so a format without them has nothing to peak on.
    gauges = all(name in present for name in JOB_ARRAY_IN_FLIGHT)
    rows = []
    for p in phases:
        s = p.sums
        frames = p.rows or 1
        rows.append([p.label] + [phase_total(s, name, present) for name in POOL_MISSES + JOB_ARRAY_COUNTERS] +
                    [us_per_total(s, ["JobArrayAllocUs", "JobArrayFreeUs"], frames, present),
                     fmt(p.peak_demand, 0) if gauges else DASH])
    return table(["Phase"] + POOL_MISSES + JOB_ARRAY_COUNTERS + ["Churn µs/frame", "Peak demand/type"], rows)


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
    # The counters this session's format wrote at all; a column an older session lacks prints `—`, not an empty 0.
    present = set(header)
    problems = cross_check(phases)

    out = [f"### Session {name} — {len(parts)} part(s), {len(hitch_paths)} hitch file(s), "
           f"{outside} rows outside every phase", "",
           "Cross-check against the report: " + ("every phase matches." if not problems else "MISMATCH — see below."),
           "", phase_overview(phases), "", "### Slots per phase (mean over every frame of the phase)", "",
           slot_tables(phases, slots), "", "### Gauges", "", gauge_table(phases), "",
           "### Render (previous rendered frame)", "", gauge_table(phases, RENDER_GAUGES), "", "### Jobs", "",
           job_table(phases), "", "### Disk I/O", "", io_table(phases, present), "",
           "### Behavior tick (ms per frame)", "", tick_table(phases), "",
           "### Lighting merge (ms per frame; counts are phase totals)", "", merge_table(phases)]
    # A session predating the crossing columns has no crossings to divide by, and printing zeros for them would
    # read as crossings that cost nothing.
    if "Crossings" in present:
        out += ["", "### View-distance crossings (ms per crossing; counts are per crossing)", "",
                crossing_table(phases, present)]
    out += ["", "### Pool misses, churn and peak demand (phase totals)", "", pool_table(phases, present), "",
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
