"""summarize_perf_session.py: report parsing in any culture, phase routing by frame range, the derived job and I/O
figures, hitch attribution, the counters a session's format can predate, and the cross-check that fails a phase cut
from the wrong rows."""
import os
import re
import shutil
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace

from helpers import TOOLS_DIR, run_script
import summarize_perf_session as summary

PERF_COUNTER_SOURCE = Path(TOOLS_DIR).parents[1] / "Assets" / "Scripts" / "Diagnostics" / "PerfCounter.cs"
FIRST_PER_FRAME_COUNT = "SectionPoolMisses"

SESSION = "PerfSession_2026-10-10_12-00-00-000"
# A session in the format the engine writes today: every counter the summarizer reads a figure from, slots aside.
HEADER = summary.FRAME_COLUMNS + ["Tick_ms", "Apply_ms", "GenerationQueue", "GenerationCompleted",
                                  "GenerationLatencyUs", "GenerationBusyUs", "DiskLoadHits", "DiskLoadMisses",
                                  "DiskReadUs", "DiskReadHitUs", "DeserializeUs", "LoadLockWaitUs", "RegionOpens",
                                  "RegionOpenUs", "DiskLoadBytes", "DiskSaveBytes", "DiskSaves", "SerializeUs",
                                  "DiskWriteUs", "SaveLockWaitUs", "IoQueueWaitUs", "IoBackgroundOps",
                                  "IoLoadQueueWaitUs", "IoLoadOps", "DiskLoadLatencyUs", "DiskLoadLatencyOps",
                                  "LightInFlight", "MeshInFlight", *summary.POOL_MISSES, "JobArrayCapDisposes",
                                  "JobArrayPaddedMisses", "JobArrayAllocUs", "JobArrayFreeUs",
                                  "ViewDistance_ms", "Unload_ms", *summary.XING_PARTS, *summary.UNLOAD_PARTS, "Crossings",
                                  *summary.CROSSING_COUNTS, "SaveReturnUs", "SaveReturns", *summary.LIGHT_SCHEDULES, "LightCompleted",
                                  "RenderSrpBatcherDraws"]
# The counters that joined the format after it first shipped; a session older than them has no column to read.
NEW_COUNTERS = ["DiskReadHitUs", "LoadLockWaitUs", "RegionOpens", "RegionOpenUs", "SaveLockWaitUs",
                "IoLoadQueueWaitUs", "IoLoadOps", "DiskLoadLatencyUs", "DiskLoadLatencyOps",
                "JobArrayCapDisposes", "JobArrayPaddedMisses", "JobArrayAllocUs", "JobArrayFreeUs"]
OLD_HEADER = [c for c in HEADER if c not in NEW_COUNTERS]
# A format without either in-flight gauge: the demand column then has no source at all.
GAUGELESS_HEADER = [c for c in HEADER if c not in summary.JOB_ARRAY_IN_FLIGHT]


def report_text(frames_a="5", frames_b="3"):
    """Two phases in a comma-decimal, space-grouped culture; A = frames [10, 15), B = [15, 18)."""
    return "\n".join([
        "=== Generation Pass — Frame Health (every frame) ===",
        "  Phase    Frames  Wall p50  Wall p99     Worst  CPU p50  CPU p99  GC p99  GCs  Hitches  GPU p99"
        "  First frame  End frame",
        f"  10 m/s   {frames_a:>6}   12,0 ms   14,0 ms   14,0 ms   1,0 ms   1,0 ms  n/a  0  0  n/a  10  15",
        "",
        "=== Loading Pass — Frame Health (every frame) ===",
        "  Phase    Frames  Wall p50  Wall p99     Worst  CPU p50  CPU p99  GC p99  GCs  Hitches  GPU p99"
        "  First frame  End frame",
        f"  50 m/s   {frames_b:>6}   1 046,3 ms   1 046,3 ms   1 046,3 ms   1,0 ms   1,0 ms  n/a  1  0  n/a  15  18",
        "",
    ]).splitlines()


def row(frame, wall, tick, header=HEADER, collections=0, **counters):
    """One session row: the frame cells, then a cell for every counter column `header` carries, named as kwargs."""
    values = dict(counters, Tick_ms=f"{tick:.3f}")
    cells = [frame, f"{wall:.3f}", "1.000", "", "1", collections, "", "", ""]
    return ",".join(str(c) for c in cells + [values.get(column, 0)
                                            for column in header[len(summary.FRAME_COLUMNS):]])


class FixtureTests(unittest.TestCase):
    def setUp(self):
        self.folder = Path(tempfile.mkdtemp(prefix="perf-session-test-"))
        rows_a = [row(f, 10.0 + (f - 10), 2.0, GenerationCompleted=2, GenerationLatencyUs=4000,
                      GenerationBusyUs=1000, RenderSrpBatcherDraws=100 * f) for f in range(10, 15)]
        rows_b = [row(f, 1046.3, 0.5, DiskLoadHits=1, DiskReadUs=500, DiskReadHitUs=500,
                      collections=1 if f == 15 else 0) for f in range(15, 18)]
        outside = [row(9, 99.0, 0.0), row(18, 99.0, 0.0)]
        part1 = [",".join(HEADER), outside[0]] + rows_a[:3]
        part2 = [",".join(HEADER)] + rows_a[3:] + rows_b + [outside[1]]
        (self.folder / f"{SESSION}.csv").write_text("\n".join(part1) + "\n", encoding="ascii")
        (self.folder / f"{SESSION}_part02.csv").write_text("\n".join(part2) + "\n", encoding="ascii")
        (self.folder / f"{SESSION}_hitch001_frame13.csv").write_text(
            "# hitch frame=13 threshold_ms=33.000 worst_ms=14.000 hitch_frames=1 gc_correlated=false "
            "top=Tick:2.000;Apply:0.000\n", encoding="ascii")
        (self.folder / f"{SESSION}_hitch002_frame16.csv").write_text(
            "# hitch frame=16 threshold_ms=33.000 worst_ms=1046.300 hitch_frames=3 gc_correlated=true "
            "top=Apply:1.000\n", encoding="ascii")

    def tearDown(self):
        shutil.rmtree(self.folder, ignore_errors=True)

    def test_values_parse_in_any_culture(self):
        self.assertEqual(summary.parse_ms("1 046,3 ms"), 1046.3)
        self.assertEqual(summary.parse_ms("1.046,3 ms"), 1046.3)
        self.assertEqual(summary.parse_ms("1,046.3 ms"), 1046.3)
        self.assertEqual(summary.parse_count("46 030"), 46030)

    def test_phases_take_only_their_frame_range(self):
        phases = summary.parse_report(report_text())
        self.assertEqual([(p.first, p.end) for p in phases], [(10, 15), (15, 18)])
        _, parts, hitches = summary.session_files(self.folder, None)
        _, outside = summary.gather(phases, parts)
        self.assertEqual(outside, 2)
        self.assertEqual([p.rows for p in phases], [5, 3])
        self.assertEqual(list(phases[0].values["wall_ms"]), [10.0, 11.0, 12.0, 13.0, 14.0])
        self.assertEqual(phases[1].collections, 1)
        summary.attach_hitches(phases, hitches)
        self.assertEqual([len(p.hitches) for p in phases], [1, 1])
        self.assertEqual(phases[1].hitches[0][2], ["Apply"])

    def test_nearest_rank_matches_the_store(self):
        values = [float(v) for v in range(1, 101)]
        self.assertEqual(summary.nearest_rank(values, 50), 50.0)
        self.assertEqual(summary.nearest_rank(values, 99), 99.0)
        self.assertEqual(summary.nearest_rank([3.0, 1.0, 2.0], 99), 3.0)

    def test_markdown_carries_derived_figures_and_passes_the_cross_check(self):
        markdown, problems = summary.summarize(report_text(), self.folder)
        self.assertEqual(problems, [])
        # 10 jobs over 5 frames: 20 ms latency and 5 ms busy in total -> 2.000 ms latency, 0.500 ms busy per job.
        self.assertIn("| Generation Pass / 10 m/s | Generation | 10 | 2.00 | 0.500 |", markdown)
        # Three loads, all hits: 1 500 µs read over them is 500.0 µs per load and 0.500 ms per hit, and with
        # nothing missed the per-miss and per-open rates have no divisor to divide by.
        self.assertIn("| Loading Pass / 50 m/s | 3 | 0 | 500.0 | 0.500 | n/a | 0.0 | 0 | n/a | 0.000 |", markdown)
        self.assertIn("| Tick | 2.000 |", markdown)
        self.assertIn("| Generation Pass / 10 m/s | 1 | 14.0 | 0 | Tick 1 |", markdown)

    def test_render_counters_are_gauges_not_totals(self):
        phases = summary.parse_report(report_text())
        _, parts, _ = summary.session_files(self.folder, None)
        summary.gather(phases, parts)
        self.assertNotIn("RenderSrpBatcherDraws", phases[0].sums)
        # Frames 10..14 draw 1000..1400: mean 1200, p99 and max 1400.
        markdown, _ = summary.summarize(report_text(), self.folder)
        self.assertIn("| RenderSrpBatcherDraws | 1200.0 / 1400 / 1400 |", markdown)

    def test_overview_counts_frames_over_the_fixed_bars(self):
        # Strictly over each bar: 33.0 counts for neither, 33.5 and 50.0 only for 33, 50.5 for both.
        phase = SimpleNamespace(label="P", rows=5, collections=0,
                                values={"wall_ms": [10.0, 33.0, 33.5, 50.0, 50.5], "cpu_ms": [1.0] * 5})
        lines = summary.phase_overview([phase]).splitlines()
        cells = dict(zip((c.strip() for c in lines[0].strip("|").split("|")),
                         (c.strip() for c in lines[2].strip("|").split("|"))))
        self.assertEqual((cells["> 33 ms"], cells["> 50 ms"]), ("3", "1"))

    def test_a_frame_count_mismatch_fails(self):
        _, problems = summary.summarize(report_text(frames_a="6"), self.folder)
        self.assertEqual(len(problems), 1)
        self.assertIn("5 session rows, report counts 6", problems[0])

    def test_script_exit_codes(self):
        report = self.folder / "BenchmarkRun_test.log"
        report.write_text("\n".join(report_text()), encoding="utf-8")
        code, out = run_script("summarize_perf_session.py", "--report", str(report), "--folder", str(self.folder))
        self.assertEqual(code, 0, out)
        report.write_text("\n".join(report_text(frames_b="4")), encoding="utf-8")
        code, out = run_script("summarize_perf_session.py", "--report", str(report), "--folder", str(self.folder))
        self.assertEqual(code, 1, out)
        self.assertIn("MISMATCH", out)


def solo_report(first, end, frames):
    """One phase's Frame Health row for frames [first, end), every wall percentile the same value."""
    return "\n".join([
        "=== Solo Pass — Frame Health (every frame) ===",
        "  Phase    Frames  Wall p50  Wall p99     Worst  CPU p50  CPU p99  GC p99  GCs  Hitches  GPU p99"
        "  First frame  End frame",
        f"  10 m/s   {frames:>6}   10,0 ms   10,0 ms   10,0 ms   1,0 ms   1,0 ms  n/a  0  0  n/a  {first}  {end}",
        "",
    ]).splitlines()


def gather_phase(rows, header=HEADER, first=10):
    """Writes `rows` as a one-phase session of its own; returns the phase and the columns its header has."""
    folder = Path(tempfile.mkdtemp(prefix="perf-column-test-"))
    try:
        for line in rows:
            assert len(line.split(",")) == len(header), "a fixture row was built for another header"
        (folder / f"{SESSION}.csv").write_text("\n".join([",".join(header), *rows]) + "\n", encoding="ascii")
        phases = summary.parse_report(solo_report(first, first + len(rows), len(rows)))
        _, parts, _ = summary.session_files(folder, None)
        part_header, outside = summary.gather(phases, parts)
        assert outside == 0, "a fixture row fell outside the phase"
        return phases[0], set(part_header)
    finally:
        shutil.rmtree(folder, ignore_errors=True)


def first_row_cells(table_text):
    """The first data row of a table, keyed by that table's header, so a figure cannot be found in a wrong column."""
    lines = table_text.splitlines()
    return dict(zip((c.strip() for c in lines[0].strip("|").split("|")),
                    (c.strip() for c in lines[2].strip("|").split("|"))))


class CounterColumnTests(unittest.TestCase):
    """The disk I/O and pool columns, on sessions whose format can predate the counters they would read."""

    def test_read_per_load_divides_by_the_misses_too(self):
        # 12 hits against 2 500 misses, as the miss-dominated generation phases measure: the read total belongs to
        # every load, so dividing it by the hit count alone prices one load at hundreds of microseconds.
        rows = [row(10, 10.0, 0.5, DiskLoadHits=12, DiskLoadMisses=1, DiskReadUs=6280, DiskReadHitUs=3960)]
        rows += [row(11 + i, 10.0, 0.5, DiskLoadMisses=1, DiskReadUs=1) for i in range(2499)]
        phase, present = gather_phase(rows)
        cells = first_row_cells(summary.io_table([phase], present))
        self.assertEqual((cells["Load hits"], cells["Misses"]), ("12", "2500"))
        self.assertEqual(cells["Read µs/load"], "3.5")       # 8 779 µs over 2 512 loads
        self.assertEqual(cells["Read ms/hit"], "0.330")      # the cache hits' own 3 960 µs over 12 hits
        self.assertEqual(cells["Read µs/miss"], "1.9")       # the 4 819 µs left over, over the misses alone

    def test_a_session_older_than_the_counters_prints_the_dash(self):
        # The counters the format already had keep their figures; the ones it has no column for print `—`, and the
        # summarizer never invents a zero for them.
        rows = [row(10, 10.0, 0.5, header=OLD_HEADER, DiskLoadHits=4, DiskLoadMisses=6, DiskReadUs=2000,
                    DeserializeUs=2000, DiskLoadBytes=1048576, DiskSaves=1, SerializeUs=500,
                    IoQueueWaitUs=100, IoBackgroundOps=2, SectionPoolMisses=7)]
        phase, present = gather_phase(rows, header=OLD_HEADER)
        io_row, pool_row = (first_row_cells(summary.io_table([phase], present)),
                            first_row_cells(summary.pool_table([phase], present)))
        self.assertEqual((io_row["Read µs/load"], io_row["Deserialize ms/hit"], io_row["MB loaded"],
                          io_row["Pool wait ms/op"]), ("200.0", "0.500", "1.0", "0.050"))
        # A column the old format does have, left at zero by this phase, prints its zero rather than the dash.
        self.assertEqual((io_row["Write ms/save"], io_row["MB saved"]), ("0.000", "0.0"))
        # A column of the older format keeps its count in the table it is now grouped with.
        self.assertEqual(pool_row["SectionPoolMisses"], "7")
        for column in ("Read ms/hit", "Read µs/miss", "Lock wait µs/load", "Region opens", "Open ms/open",
                       "Save lock wait ms/save", "Load pool wait ms/op", "Load latency ms"):
            self.assertEqual(io_row[column], "—", column)
        for column in ("JobArrayCapDisposes", "JobArrayPaddedMisses", "JobArrayAllocUs", "JobArrayFreeUs",
                       "Churn µs/frame"):
            self.assertEqual(pool_row[column], "—", column)

    def test_churn_is_the_maps_allocated_and_freed_per_frame(self):
        rows = [row(10, 10.0, 0.5, JobArrayAllocUs=900, JobArrayFreeUs=100), row(11, 10.0, 0.5)]
        phase, present = gather_phase(rows)
        cells = first_row_cells(summary.pool_table([phase], present))
        self.assertEqual((cells["JobArrayAllocUs"], cells["JobArrayFreeUs"]), ("900", "100"))
        self.assertEqual(cells["Churn µs/frame"], "500.0")

    def test_peak_demand_is_the_maps_one_frame_wanted(self):
        rows = [row(10, 10.0, 0.5, LightInFlight=7, MeshInFlight=2), row(11, 10.0, 0.5, LightInFlight=2,
                    MeshInFlight=7), row(12, 10.0, 0.5, LightInFlight=1, MeshInFlight=1)]
        phase, present = gather_phase(rows)
        cells = first_row_cells(summary.pool_table([phase], present))
        # Both of the first two frames hold nine jobs in flight, so 81 maps were wanted at once: each level's own
        # maximum would claim 126, which no frame ever asked for.
        self.assertEqual(cells["Peak demand/type"], "81")

    def test_peak_demand_prints_the_dash_without_its_gauges(self):
        rows = [row(10, 10.0, 0.5, header=GAUGELESS_HEADER, JobArrayAllocUs=900, JobArrayFreeUs=100),
                row(11, 10.0, 0.5, header=GAUGELESS_HEADER)]
        phase, present = gather_phase(rows, header=GAUGELESS_HEADER)
        cells = first_row_cells(summary.pool_table([phase], present))
        self.assertEqual(cells["Peak demand/type"], "—")
        self.assertEqual(cells["Churn µs/frame"], "500.0")

    def test_crossings_price_each_pass_per_crossing(self):
        # Four crossings over one frame: every part belongs to a crossing, so it divides by 4 and not by the frame
        # count; the two slots' remainders are what their parts leave of the crossing's own measured time.
        rows = [row(10, 10.0, 0.5, Crossings=4, ViewDistance_ms=210.0, Unload_ms=60.0, XingCloudsUs=20_000,
                    XingSpiralUs=140_000, XingDeactScanUs=12_000, XingDeactivateUs=16_000, XingActivateUs=8_000,
                    XingActiveSetUs=4_000, XingGenEnqueued=40, XingActivations=8, XingDeactivations=4,
                    UnloadScanUs=4_000, UnloadDecideUs=20_000, UnloadBookkeepUs=800,
                    UnloadSaveUs=8_000, UnloadVisualUs=4_000, UnloadDataReturnUs=4_000, UnloadCandidates=12,
                    UnloadedChunks=6, UnloadSaved=4, UnloadSavedSections=20, UnloadDeferJob=2, UnloadDeferStrand=4,
                    SaveReturnUs=6_000, SaveReturns=3)]
        phase, present = gather_phase(rows)
        cells = first_row_cells(summary.crossing_table([phase], present))
        self.assertEqual((cells["Crossings"], cells["ViewDistance ms/crossing"], cells["Clouds"], cells["Spiral"],
                          cells["DeactScan"], cells["Deactivate"], cells["Activate"], cells["ActiveSet"],
                          cells["View Other"]),
                         ("4", "52.500", "5.000", "35.000", "3.000", "4.000", "2.000", "1.000", "2.500"))
        self.assertEqual((cells["Unload ms/crossing"], cells["Scan"], cells["Decide"], cells["Bookkeep"], cells["Save"],
                          cells["Visual"], cells["DataReturn"], cells["Unload Other"]),
                         ("15.000", "1.000", "5.000", "0.200", "2.000", "1.000", "1.000", "4.800"))
        self.assertEqual((cells["Generation enqueued/crossing"], cells["Activations/crossing"],
                          cells["Deactivations/crossing"], cells["Candidates/crossing"], cells["Unloaded/crossing"],
                          cells["Saved/crossing"], cells["Saved sections/crossing"], cells["Deferred job/crossing"],
                          cells["Deferred strand/crossing"]),
                         ("10.00", "2.00", "1.00", "3.00", "1.50", "1.00", "5.00", "0.50", "1.00"))
        # The save snapshot returns to the pool in a later frame, outside the crossing, so it is charged per frame and
        # per return: three returns costing 6 ms in total is 6 ms of that frame and 2 000 µs per return.
        self.assertEqual((cells["Save return ms/frame"], cells["Save return µs/return"]), ("6.000", "2000.0"))

    def test_a_phase_that_never_crossed_prints_no_per_crossing_figure(self):
        # A phase whose player never left a chunk has crossings to divide by nothing: neither the passes' parts nor
        # their per-crossing counts read as a measured zero.
        phase, present = gather_phase([row(10, 10.0, 0.5)])
        cells = first_row_cells(summary.crossing_table([phase], present))
        self.assertEqual((cells["Crossings"], cells["ViewDistance ms/crossing"], cells["Clouds"], cells["Unload Other"],
                          cells["Candidates/crossing"], cells["Save return µs/return"]),
                         ("0", "n/a", "n/a", "n/a", "n/a", "n/a"))
        self.assertEqual(cells["Save return ms/frame"], "0.000")

    def test_light_schedules_split_by_arm_with_the_initial_share(self):
        # Ten frames, 40 jobs: the two initial arms (scan + disk load) are 10 of them, a 25 % share.
        rows = [row(10 + i, 10.0, 0.5, LightSchedInitial=1 if i < 6 else 0, LightSchedEdge=2, LightSchedRegular=1,
                    LightSchedLoadInitial=1 if i < 4 else 0, LightCompleted=4) for i in range(10)]
        phase, present = gather_phase(rows)
        cells = first_row_cells(summary.light_schedule_table([phase]))
        self.assertEqual((cells["Initial/frame"], cells["Edge/frame"], cells["Regular/frame"],
                          cells["Load initial/frame"], cells["Initial share %"], cells["Scheduled"], cells["Completed"]),
                         ("0.60", "2.00", "1.00", "0.40", "25.0", "40", "40"))

    def test_a_phase_that_scheduled_no_lighting_has_no_initial_share(self):
        phase, present = gather_phase([row(10, 10.0, 0.5)])
        self.assertEqual(first_row_cells(summary.light_schedule_table([phase]))["Initial share %"], "n/a")

    def test_lock_wait_and_queue_rates_on_exact_counters(self):
        rows = [row(10, 10.0, 0.5, DiskLoadHits=2, DiskLoadMisses=3, DiskReadUs=1000, DiskReadHitUs=400,
                    LoadLockWaitUs=1500, RegionOpens=2, RegionOpenUs=300, DiskSaves=1, SaveLockWaitUs=250,
                    IoLoadOps=4, IoLoadQueueWaitUs=200, DiskLoadLatencyUs=7000, DiskLoadLatencyOps=5)]
        phase, present = gather_phase(rows)
        cells = first_row_cells(summary.io_table([phase], present))
        self.assertEqual(cells["Lock wait µs/load"], "300.0")
        self.assertEqual((cells["Read µs/load"], cells["Read ms/hit"], cells["Read µs/miss"]),
                         ("200.0", "0.200", "200.0"))
        self.assertEqual((cells["Region opens"], cells["Open ms/open"]), ("2", "0.150"))
        self.assertEqual(cells["Save lock wait ms/save"], "0.250")
        self.assertEqual(cells["Load pool wait ms/op"], "0.050")
        self.assertEqual(cells["Load latency ms"], "1.400")


class GaugeParityTests(unittest.TestCase):
    def test_gauge_lists_mirror_the_perf_counter_gauge_group(self):
        # A gauge missing here would be summed per phase instead of read as a level.
        body = PERF_COUNTER_SOURCE.read_text(encoding="utf-8").split("enum PerfCounter")[1]
        members = re.findall(r"^\s*([A-Z]\w*),\s*$", body, re.M)
        self.assertEqual(summary.GAUGES + summary.RENDER_GAUGES, members[:members.index(FIRST_PER_FRAME_COUNT)])


if __name__ == "__main__":
    unittest.main()
