"""Attribute a startup run's drained -> stable tail: which slot led each frame slow enough to reset the stable window.

WHAT THIS DOES
    Reads one or more Capture-tier session files recorded by `-mc-run startup-new|startup-existing` (each run writes its
    own `PerfLogs/PerfSession_<stamp>.csv`, plus `_partNN` continuations). For each session it:
      - finds the drain the way `StartupTimeline` does: the frame after DRAIN_SETTLE_FRAMES consecutive frames whose
        `GenerationQueue`, `GenerationInFlight`, `LightReady`, `LightInFlight` and `MeshInFlight` are all 0
        (`PipelineDrainPredicate.AreStagesDrained`; the load-square check is approximated by `ResidentChunks` > 0);
      - takes every frame after it, their median wall time, and the stable rule's threshold
        max(STABLE_MEDIAN_FACTOR x median, median + STABLE_SLACK_MS) — a frame above it resets the 2 s settle window;
      - prints how many frames exceeded it, which slot led each (the largest `*_ms` slot; "(no slot > 0.5 ms)" when
        none reached 0.5 ms — GPU, present or OS time), and for Tick-led frames which tick part led;
      - prints the median of the post-drain behavior-tick frames: Tick, Prepare, fluid chunks, fluid voxels, snapshot KB.
    The tail it measures runs to the end of the session, so it matches the run's own drained -> stable time only when
    the run quit at the stable mark (`-mc-run startup-*` does).

WHAT IT READS / WRITES
    Reads the given sessions' CSV files from the PerfLogs folder; writes only to stdout.

WHY IT EXISTS
    The startup probe reports when frame time settled, not why it took that long. ES-29's A/B used this attribution to
    show the behavior tick had stopped leading the tail (13-27 Tick-led slow frames per run before, 3-4 after), and that
    the remaining spread came from LightMerge, MeshSchedule and slot-less frames
    (Documentation/Performance/ENGINE_SCALING_ES29_BEHAVIOR_TICK_IL2CPP_2026-10-11_BENCHMARK.md).

DETERMINISM
    Sessions are processed in the order given and rows in file order; percentiles are nearest-rank medians and every
    figure uses fixed formatting, so the same inputs always produce byte-identical output.

RUN
    python Tools/Python/attribute_startup_tail.py <label>=<PerfSession_stamp> [<label>=<PerfSession_stamp> ...]
        [--folder <PerfLogs>]

    The folder defaults to %USERPROFILE%/AppData/LocalLow/johanaxel007/Minecraft Clone/PerfLogs. The session stamp is
    the file name without `.csv` (the runner's report lists it).
"""

import argparse
import csv
import os
import statistics
import sys
from collections import Counter
from pathlib import Path

DEFAULT_FOLDER = Path(os.path.expandvars(r"%USERPROFILE%")) / "AppData/LocalLow/johanaxel007/Minecraft Clone/PerfLogs"

# Mirrors of StartupTimeline / PipelineDrainPredicate.
DRAIN_SETTLE_FRAMES = 30
STABLE_MEDIAN_FACTOR = 1.25
STABLE_SLACK_MS = 2.0
DRAIN_COLUMNS = ["GenerationQueue", "GenerationInFlight", "LightReady", "LightInFlight", "MeshInFlight"]

# A slot under this is not credited with leading a slow frame.
MIN_LEADING_SLOT_MS = 0.5
NON_SLOT_COLUMNS = {"wall_ms", "cpu_ms", "gpu_ms", "render_thread_ms", "present_wait_ms"}
TICK_PARTS = ["TickPrepareUs", "TickScheduleUs", "TickWaitUs", "TickGrassUs", "TickFluidReplayUs"]
MICROSECONDS_PER_MILLISECOND = 1000.0


def session_rows(folder, session):
    """Yields every row of a session (its base file and `_partNN` continuations, never hitch files), in file order."""
    files = sorted(p for p in folder.glob(session + "*.csv") if "_hitch" not in p.name)
    if not files:
        raise SystemExit(f"no session files for {session} in {folder}")
    for path in files:
        with path.open(newline="", encoding="utf-8") as fh:
            yield from csv.DictReader(fh)


def num(row, column):
    """A row's value as a float; empty or missing reads 0."""
    value = row.get(column) or ""
    try:
        return float(value)
    except ValueError:
        return 0.0


def drain_index(rows):
    """Index of the first frame after DRAIN_SETTLE_FRAMES consecutive drained frames, or None if the run never drained."""
    streak = 0
    for i, row in enumerate(rows):
        drained = num(row, "ResidentChunks") > 0 and all(num(row, c) == 0 for c in DRAIN_COLUMNS)
        streak = streak + 1 if drained else 0
        if streak == DRAIN_SETTLE_FRAMES:
            return i + 1
    return None


def median_or_zero(values):
    return statistics.median(values) if values else 0.0


def attribute(label, rows):
    """Prints one session's tail attribution."""
    start = drain_index(rows)
    if start is None:
        print(f"{label}: never drained")
        return

    post = rows[start:]
    slots = [c for c in rows[0] if c.endswith("_ms") and c not in NON_SLOT_COLUMNS]
    walls = [num(r, "wall_ms") for r in post]
    median = median_or_zero(walls)
    threshold = max(STABLE_MEDIAN_FACTOR * median, median + STABLE_SLACK_MS)

    leaders, tick_parts = Counter(), Counter()
    worst_tick = 0.0
    for row in post:
        if num(row, "wall_ms") <= threshold:
            continue
        top = max(slots, key=lambda c: num(row, c))
        if num(row, top) < MIN_LEADING_SLOT_MS:
            leaders["(no slot > 0.5 ms)"] += 1
            continue
        leaders[top[:-len("_ms")]] += 1
        if top == "Tick_ms":
            worst_tick = max(worst_tick, num(row, top))
            tick_parts[max(TICK_PARTS, key=lambda c: num(row, c))[len("Tick"):-len("Us")]] += 1

    tick_rows = [r for r in post if num(r, "TickFluidChunks") > 0 or num(r, "TickGrassVoxels") > 0]
    print(f"{label}: drained at frame {start}; {len(post)} frames / {sum(walls) / 1000:.1f} s after; "
          f"median {median:.2f} ms, threshold {threshold:.2f} ms; {sum(leaders.values())} slow frames")
    print("  led by: " + ", ".join(f"{name} {count}" for name, count in leaders.most_common()))
    if tick_parts:
        print(f"  Tick-led frames by part: " + ", ".join(f"{name} {count}" for name, count in tick_parts.most_common())
              + f"; worst Tick {worst_tick:.2f} ms")
    if tick_rows:
        print(f"  tick frames after the drain: {len(tick_rows)}; median Tick {median_or_zero([num(r, 'Tick_ms') for r in tick_rows]):.2f} ms, "
              f"Prepare {median_or_zero([num(r, 'TickPrepareUs') for r in tick_rows]) / MICROSECONDS_PER_MILLISECOND:.2f} ms, "
              f"fluid chunks {median_or_zero([num(r, 'TickFluidChunks') for r in tick_rows]):.0f}, "
              f"fluid voxels {median_or_zero([num(r, 'TickFluidVoxels') for r in tick_rows]):.0f}, "
              f"snapshot KB {median_or_zero([num(r, 'TickSnapshotKb') for r in tick_rows]):.0f}")


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("sessions", nargs="+", help="<label>=<PerfSession_stamp>, e.g. before1=PerfSession_2026-10-11_11-04-40-970")
    parser.add_argument("--folder", type=Path, default=DEFAULT_FOLDER, help="the PerfLogs folder")
    args = parser.parse_args()

    for item in args.sessions:
        label, sep, session = item.partition("=")
        if not sep:
            label, session = item, item
        attribute(label, list(session_rows(args.folder, session)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
