"""Tabulate the Tick-led hitch records of one or more player runs' run-end performance-monitor summaries.

WHAT THIS DOES
    Reads Player.log files written by an unattended benchmark run (`-mc-run benchmark ... -mc-quit`) at the Systems
    detail level (`-mc-set perfMonitorTier=Systems`). In each log it finds the LAST `[Launch] Performance monitor at
    run end:` block and prints, per hitch record whose top slot is `Tick`:
      - the frame's worst wall time, the Tick slot, and the part `/perf hitches` named as leading it;
      - the six timed parts (TickListUs ... TickFluidReplayUs) in ms, and "other" = Tick minus their sum;
      - the counts: fluid chunks prepared, snapshot KB copied, grass voxels, fluid-ticker pool misses, and
        the fluid jobs' summed worker busy time (FluidBusyUs).
    It closes each log with the held-record count and how many records each part led.

WHAT IT READS / WRITES
    Reads only the given log files; writes only to stdout.

WHY IT EXISTS
    The run-end readout prints each hitch record as one long line of counters, so comparing the Tick split across
    dozens of records and several runs by eye is error-prone. PM-8's Master confirmation
    (Documentation/Design/PERFORMANCE_MONITOR_AND_LOGGER_OVERHAUL.md §7.6) was read with this table.

DETERMINISM
    Logs are processed in the order given and records in the order the log prints them (newest first); all
    numbers use fixed formatting, so the same logs always produce byte-identical output.

RUN
    python Tools/Python/tabulate_tick_hitches.py <Player.log> [<Player.log> ...]

    A player's Player.log is overwritten by its next launch, so copy it after each run, e.g. from
    %USERPROFILE%/AppData/LocalLow/johanaxel007/Minecraft Clone/Player.log.
"""

import argparse
import re
import sys
from collections import Counter
from pathlib import Path

RUN_END_MARKER = "Performance monitor at run end"
RECORD_PATTERN = re.compile(r"^\s+frame \d+: worst")

# The timed parts, in tick order, with the labels /perf hitches uses for "Tick led by".
PARTS = [
    ("TickListUs", "List"),
    ("TickPrepareUs", "Prepare"),
    ("TickScheduleUs", "Schedule"),
    ("TickWaitUs", "Wait"),
    ("TickGrassUs", "Grass"),
    ("TickFluidReplayUs", "Replay"),
]

COUNTS = ["TickFluidChunks", "TickSnapshotKb", "TickGrassVoxels", "FluidTickerPoolMisses", "FluidBusyUs"]

MICROSECONDS_PER_MILLISECOND = 1000.0


def read_counter(line, name):
    """Returns a hitch record's counter, or 0 when the record omits it (the readout prints non-zero counters only)."""
    match = re.search(r"\b" + re.escape(name) + r" (\d+)", line)
    return int(match.group(1)) if match else 0


def run_end_lines(path):
    """Returns the lines of the last run-end monitor block in a log, or None when the log has none."""
    lines = path.read_text(encoding="utf-8", errors="replace").splitlines()
    starts = [i for i, line in enumerate(lines) if RUN_END_MARKER in line]
    return lines[starts[-1]:] if starts else None


def format_record(line):
    """Formats one Tick-led hitch record as a table row."""
    worst = float(re.search(r"worst ([\d.]+)", line).group(1))
    tick = float(re.search(r"top Tick ([\d.]+)", line).group(1))
    led = re.search(r"Tick led by (\w+)", line).group(1)
    parts_ms = [read_counter(line, counter) / MICROSECONDS_PER_MILLISECOND for counter, _ in PARTS]
    other = tick - sum(parts_ms)
    chunks, snapshot_kb, grass_voxels, pool_misses, fluid_busy_us = (read_counter(line, name) for name in COUNTS)
    parts_text = " ".join(f"{label.lower()} {ms:6.2f}" for (_, label), ms in zip(PARTS, parts_ms))
    return (f"  worst {worst:7.2f}  Tick {tick:6.2f}  led {led:<8} {parts_text}  other {other:5.2f} | "
            f"chunks {chunks:4}  snapKB {snapshot_kb:7}  grassVox {grass_voxels:6}  poolMiss {pool_misses:3}  "
            f"fluidBusy {fluid_busy_us / MICROSECONDS_PER_MILLISECOND:7.1f} ms")


def tabulate(path):
    """Prints one log's table; returns False when the log holds no run-end block."""
    lines = run_end_lines(path)
    if lines is None:
        print(f"{path}: no '{RUN_END_MARKER}' block — was the run unattended, and did it reach the end?")
        return False

    records = [line for line in lines if RECORD_PATTERN.match(line)]
    tick_led = [line for line in records if "Tick led by" in line]
    print(f"{path}: {len(records)} held records, {len(tick_led)} Tick-led")
    for line in tick_led:
        print(format_record(line))

    leaders = Counter(re.search(r"Tick led by (\w+)", line).group(1) for line in tick_led)
    if leaders:
        print("  led by: " + ", ".join(f"{label} {leaders[label]}" for _, label in PARTS if leaders[label]))
    return True


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("logs", nargs="+", type=Path, help="Player.log files from Systems-tier benchmark runs")
    args = parser.parse_args()

    ok = True
    for path in args.logs:
        ok &= tabulate(path)
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
