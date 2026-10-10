"""summarize_perf_session.py: report parsing in any culture, phase routing by frame range, the derived job and I/O
figures, hitch attribution, and the cross-check that fails a phase cut from the wrong rows."""
import os
import shutil
import tempfile
import unittest
from pathlib import Path

from helpers import run_script
import summarize_perf_session as summary

SESSION = "PerfSession_2026-10-10_12-00-00-000"
HEADER = summary.FRAME_COLUMNS + ["Tick_ms", "Apply_ms", "GenerationQueue", "GenerationCompleted",
                                  "GenerationLatencyUs", "GenerationBusyUs", "DiskLoadHits", "DiskReadUs"]


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


def row(frame, wall, tick, completed=0, latency=0, busy=0, hits=0, read=0, collections=0):
    frame_cells = [frame, f"{wall:.3f}", "1.000", "", "1", collections, "", "", ""]
    return ",".join(str(c) for c in frame_cells + [f"{tick:.3f}", "0.000", 4, completed, latency, busy, hits, read])


class FixtureTests(unittest.TestCase):
    def setUp(self):
        self.folder = Path(tempfile.mkdtemp(prefix="perf-session-test-"))
        rows_a = [row(f, 10.0 + (f - 10), 2.0, completed=2, latency=4000, busy=1000) for f in range(10, 15)]
        rows_b = [row(f, 1046.3, 0.5, hits=1, read=500, collections=1 if f == 15 else 0) for f in range(15, 18)]
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
        # Three hits read in 1.5 ms -> 0.500 ms per hit.
        self.assertIn("| Loading Pass / 50 m/s | 3 | 0 | 0.500 |", markdown)
        self.assertIn("| Tick | 2.000 |", markdown)
        self.assertIn("| Generation Pass / 10 m/s | 1 | 14.0 | 0 | Tick 1 |", markdown)

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


if __name__ == "__main__":
    unittest.main()
