"""Compare the two legs of a player A/B, run by run, for one benchmark phase.

WHAT THIS DOES
    Reads, for every run tag, the Markdown that `summarize_perf_session.py` wrote (`<tag>_summary.md`) and the
    benchmark report it was cut from (`<tag>_report.log`), both in one folder. For the chosen phase it prints one
    Markdown table: a row per metric, a column per run, then each leg's mean and B's change against A. Metrics:
      - frame level: frames, wall p50 / p99 / worst (summary overview), hitch frames (the report's Frame Health row);
      - the `LightMerge`, `LightSchedule` and `MeshSchedule` slots (mean ms per frame);
      - per job: merge and apply ms per merged job, schedule ms per completed light and mesh job;
      - the lighting merge's parts and counts (ES-7 Step 0 counters), and the jobs completed.
    Columns a run's summary lacks (an older capture without the merge counters) read nan, and so do all three slots when
    the summary has no slot table for the phase (a mistyped --phase). A slot missing from a table that is there reads 0:
    the summary leaves out slots below its noise floor.

WHAT IT READS / WRITES
    Reads `<dir>/<tag>_summary.md` and `<dir>/<tag>_report.log` for every tag; writes Markdown to stdout.

WHY IT EXISTS
    A Master A/B alternates legs over several runs; ES-7a's report tables were built this way. Per-job figures of a
    per-frame slot are not comparable when the legs' frame rates differ (a fixed per-frame cost spreads over fewer jobs
    in the faster leg) — read them next to the frames row.

RUN
    python Tools/Python/summarize_ab_legs.py --dir <folder> --phase "Generation Pass / 200 m/s" \\
        --group "Generation Pass" --a a1,a2,a3 --b b1,b2,b3
    --phase is the summary's phase label ("<group> / <phase>"); --group is the report's Frame Health group.
"""

import argparse
import math
import os
import re
import statistics
import sys

MERGE_TABLE = "Lighting merge (ms per frame; counts are phase totals)"


def parse_tables(text):
    """Maps each heading's text to the rows of the Markdown tables under it, as dicts keyed by the header row."""
    out, title, header = {}, None, None
    for line in text.splitlines():
        if line.startswith("#"):
            title, header = line.lstrip("#").strip(), None
            continue
        if not line.startswith("|"):
            header = None
            continue
        cells = [c.strip() for c in line.strip("|").split("|")]
        if header is None:
            header = cells
        elif not set(cells[0]) <= set("-"):
            out.setdefault(title, []).append(dict(zip(header, cells)))
    return out


def num(text):
    """A table cell as a float (decimal comma accepted); nan when it is not a number."""
    try:
        return float(str(text).replace(",", ".").replace(" ", "").replace(" ", ""))
    except ValueError:
        return math.nan


def frame_health_hitches(report_text, group):
    """The Hitches cell of a group's Frame Health row in a benchmark report, or nan."""
    m = re.search(r"=== " + re.escape(group) + r" — Frame Health \(every frame\) ===\n(.*?)\n\n", report_text, re.S)
    if not m:
        return math.nan
    lines = m.group(1).splitlines()
    row = dict(zip(re.split(r"\s{2,}", lines[0].strip()), re.split(r"\s{2,}", lines[1].strip())))
    return num(row.get("Hitches"))


def run_metrics(folder, tag, phase, group):
    """The metric dict of one run for one phase."""
    with open(os.path.join(folder, f"{tag}_summary.md"), encoding="utf-8") as f:
        tables = parse_tables(f.read())
    with open(os.path.join(folder, f"{tag}_report.log"), encoding="utf-8", errors="replace") as f:
        report = f.read()

    overview = next((r for k, rows in tables.items() if k.startswith("Session") for r in rows if r.get("Phase") == phase), {})
    slots = {r["Slot"]: num(r["Mean ms"]) for r in tables.get(phase, [])}
    jobs = {r["Job"]: num(r["Completed"]) for r in tables.get("Jobs", []) if r.get("Phase") == phase}
    merge = next((r for r in tables.get(MERGE_TABLE, []) if r.get("Phase") == phase), {})

    frames = num(overview.get("Frames"))
    merges_per_frame = num(merge.get("Merges/frame"))
    slot_default = 0.0 if phase in tables else math.nan
    light_merge, light_schedule, mesh_schedule = (slots.get(s, slot_default)
                                                  for s in ("LightMerge", "LightSchedule", "MeshSchedule"))
    light_jobs, mesh_jobs = jobs.get("Light", math.nan), jobs.get("Mesh", math.nan)

    def ratio(a, b):
        return a / b if b else math.nan

    return {
        "Frames": frames,
        "Wall p50": num(overview.get("Wall p50")),
        "Wall p99": num(overview.get("Wall p99")),
        "Worst": num(overview.get("Worst")),
        "Hitch frames": frame_health_hitches(report, group),
        "LightMerge": light_merge,
        "LightSchedule": light_schedule,
        "MeshSchedule": mesh_schedule,
        "Merge ms per merge": ratio(light_merge, merges_per_frame),
        "Apply ms per merge": num(merge.get("Apply ms/merge")),
        "Schedule ms per light job": ratio(light_schedule * frames, light_jobs),
        "Schedule ms per mesh job": ratio(mesh_schedule * frames, mesh_jobs),
        "Apply": num(merge.get("Apply")),
        "of Apply: counts": num(merge.get("of Apply: counts")),
        "Route": num(merge.get("Route")),
        "Merges per frame": merges_per_frame,
        "Out-of-band sections/merge": num(merge.get("Out-of-band sections/merge")),
        "Churned/merge": num(merge.get("Churned/merge")),
        "Light jobs completed": light_jobs,
        "Mesh jobs completed": mesh_jobs,
        "Generation jobs completed": jobs.get("Generation", math.nan),
    }


def render(a_runs, b_runs):
    """The comparison table as Markdown lines; legs map tag -> metric dict, in tag order."""
    tags = list(a_runs) + list(b_runs)
    lines = ["| Metric | " + " | ".join(tags) + " | A mean | B mean | B vs A |",
             "|" + "---|" * (len(tags) + 4)]
    for metric in next(iter(a_runs.values())):
        a_values = [a_runs[t][metric] for t in a_runs]
        b_values = [b_runs[t][metric] for t in b_runs]
        a_mean, b_mean = statistics.fmean(a_values), statistics.fmean(b_values)
        change = f"{(b_mean - a_mean) / a_mean * 100:+.1f} %" if a_mean and not math.isnan(a_mean) else "n/a"
        cells = [f"{v:.3f}" for v in a_values + b_values]
        lines.append(f"| {metric} | " + " | ".join(cells) + f" | {a_mean:.3f} | {b_mean:.3f} | {change} |")
    return lines


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--dir", required=True, help="folder holding <tag>_summary.md and <tag>_report.log")
    parser.add_argument("--phase", required=True, help='summary phase label, e.g. "Generation Pass / 200 m/s"')
    parser.add_argument("--group", required=True, help='report Frame Health group, e.g. "Generation Pass"')
    parser.add_argument("--a", required=True, help="comma-separated A-leg tags")
    parser.add_argument("--b", required=True, help="comma-separated B-leg tags")
    args = parser.parse_args(argv)

    a_runs = {t: run_metrics(args.dir, t, args.phase, args.group) for t in args.a.split(",")}
    b_runs = {t: run_metrics(args.dir, t, args.phase, args.group) for t in args.b.split(",")}
    print(f"## {args.phase}  (A = {args.a}; B = {args.b})\n")
    print("\n".join(render(a_runs, b_runs)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
