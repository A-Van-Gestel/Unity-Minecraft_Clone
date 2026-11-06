"""check_perf_coverage_map.py: the §4.9 coverage map checked against the code it maps.

The script resolves every path against its repo root, so these tests import it and point `REPO_ROOT` at a
throwaway tree: one enum file, three per-frame scripts, two job structs and a design doc with a §4.9 section.
"""
import contextlib
import io
import os
import shutil
import tempfile
import unittest
from unittest import mock

import helpers  # noqa: F401 - puts Tools/Python on sys.path for the import below
import check_perf_coverage_map as coverage

DOC = "Documentation/Design/PERFORMANCE_MONITOR_AND_LOGGER_OVERHAUL.md"

ENUM = """namespace Diagnostics
{
    public enum PerfSlot : byte
    {
        /// <summary>The tick.</summary>
        Tick = 0,
        Apply = 1,
        MeshProcess = 2,
        LeftOver = 3,

        /// <summary>Number of slots; not a slot.</summary>
        Count,
    }
}
"""

WORLD = """class World
{
    void Update()
    {
        var a = PerfStore.Begin(PerfSlot.Tick);
        var b = PerfStore.Begin(PerfSlot.Apply);
    }
}
"""

MESH_APPLIER = """class MeshApplier
{
    void LateUpdate()
    {
        var c = PerfStore.Begin(PerfSlot.MeshProcess);
    }
}
"""

ANIMATION = """class ChunkLoadAnimation
{
    void Update()
    {
    }
}
"""

CHURN_JOB = """namespace Jobs
{
    public struct ChurnJob : IJob
    {
        public JobBusyTimer Timer;
    }
}
"""

IDLE_JOB = """namespace Jobs
{
    public struct IdleJob : IJobFor
    {
    }
}
"""

BASE_DOC = """## 4. Architecture

Some earlier section.

### 4.9 Coverage map

*As of today; derived from the code.*

**Frame level.**

| Measure | Source |
|---------|--------|
| Wall time | `CommitFrame` |

**Main thread - slots (Systems tier, or forced).** Inside `World.Update` the slots are disjoint:

| Slot(s) | Covers |
|---------|--------|
| `Tick` | `ProcessTickUpdates` |
| `Apply` | `ApplyModifications` |

Outside `World.Update` (a slot here may contain work it triggers):

| Slot | Probed in |
|------|-----------|
| `MeshProcess` | `MeshApplier` |

**Worker jobs (Systems tier).**

| Job | Timed |
|-----|-------|
| `ChurnJob` | yes |
| `IdleJob` | no |

**Counters (Systems tier).** 2, sampled in `World.SamplePerfCounters`: `TickListUs` and `MeshApplier.cs`.

## 5. Prerequisites

`MeshProcess` and `IdleJob` and `LeftOver` are named again here, which §4.9 cannot borrow.
"""


class CoverageMapTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.root = self.directory.name
        self.write("Assets/Scripts/Diagnostics/PerfSlot.cs", ENUM)
        self.write("Assets/Scripts/World.cs", WORLD)
        self.write("Assets/Scripts/Effects/MeshApplier.cs", MESH_APPLIER)
        self.write("Assets/Scripts/ChunkLoadAnimation.cs", ANIMATION)
        self.write("Assets/Scripts/Jobs/ChurnJob.cs", CHURN_JOB)
        self.write("Assets/Scripts/Jobs/IdleJob.cs", IDLE_JOB)
        self.write(DOC, BASE_DOC)

    def tearDown(self):
        self.directory.cleanup()

    def write(self, relative, text):
        full = os.path.join(self.root, *relative.split("/"))
        os.makedirs(os.path.dirname(full), exist_ok=True)
        with open(full, "w", encoding="utf-8", newline="\n") as handle:
            handle.write(text)
        return full

    def doc_with(self, *swaps):
        """Re-write the doc with each (old, new) swap applied; an anchor that moved fails instead of passing vacuously."""
        text = BASE_DOC
        for old, new in swaps:
            self.assertEqual(text.count(old), 1, f"anchor not found once: {old!r}")
            text = text.replace(old, new)
        self.write(DOC, text)

    def run_main(self, *args):
        out = io.StringIO()
        with mock.patch.object(coverage, "REPO_ROOT", self.root), \
                contextlib.redirect_stdout(out), contextlib.redirect_stderr(out):
            code = coverage.main(list(args))
        return code, out.getvalue()

    def test_clean_map_passes_and_counts_what_it_found(self):
        code, out = self.run_main()
        self.assertEqual(code, 0, out)
        self.assertIn("slots 4, probed slots 3, per-frame scripts 3 (2 probed), job structs 2, timed 1", out)
        self.assertIn("excluded as non-slot: Count", out)
        # 3 slot-table names is the proof the two anchor paragraphs resolved; 0 would judge an empty universe green.
        self.assertIn("(3 in slot tables)", out)

    def test_probed_slot_missing_from_the_doc_is_flagged(self):
        self.doc_with(("| `MeshProcess` | `MeshApplier` |\n", ""))
        code, out = self.run_main()
        self.assertEqual(code, 1, out)
        self.assertIn("SLOT-MISSING-IN-DOC  MeshProcess", out)

    def test_doc_slot_that_is_not_an_enum_member_is_flagged(self):
        self.doc_with(("| `MeshProcess` | `MeshApplier` |", "| `StaleSlot` | `MeshApplier` |"))
        code, out = self.run_main()
        self.assertEqual(code, 1, out)
        self.assertIn("SLOT-UNKNOWN-IN-DOC  StaleSlot", out)

    def test_counter_file_and_method_names_are_not_slot_candidates(self):
        # A dotted span, a wildcard and a bare file name all name something other than a slot, in the table and out.
        self.doc_with(("| `MeshProcess` | `MeshApplier` |",
                       "| `MeshProcess` | `MeshApplier` |\n| `Tick*`, `ChunkPoolManager.Update`, `MeshApplier.cs` | x |"))
        code, out = self.run_main()
        self.assertEqual(code, 0, out)
        self.assertNotIn("SLOT-UNKNOWN-IN-DOC", out)
        self.assertNotIn("SLOT-MISSING-IN-DOC", out)

    def test_job_struct_missing_from_the_doc_is_flagged(self):
        self.doc_with(("| `IdleJob` | no |\n", ""))
        code, out = self.run_main()
        self.assertEqual(code, 1, out)
        self.assertIn("JOB-MISSING-IN-DOC     IdleJob", out)

    def test_list_marks_timed_and_untimed_structs_apart(self):
        code, out = self.run_main("--list")
        self.assertIn("struct ChurnJob  TIMED", out)
        self.assertIn("struct IdleJob  UNTIMED", out)
        self.assertIn("probed    Assets/Scripts/World.cs", out)
        self.assertIn("UNPROBED  Assets/Scripts/ChunkLoadAnimation.cs", out)
        self.assertNotIn("Tick = 0", out)  # S1 prints slot names, not the enum source
        self.assertEqual(code, 0, out)

    def test_slot_no_code_probes_is_informational_not_a_failure(self):
        code, out = self.run_main()
        self.assertEqual(code, 0, out)
        self.assertIn("PROBED-SLOT-UNUSED   LeftOver", out)
        self.assertIn("1 informational", out)
        self.assertNotIn("SLOT-MISSING-IN-DOC", out)

    def test_a_later_section_does_not_count_as_naming_the_slot(self):
        # `MeshProcess` stays named by §5 while leaving both slot tables, which is the section-boundary rule.
        self.doc_with(("| `MeshProcess` | `MeshApplier` |\n", ""),
                      ("`MeshProcess` and `IdleJob`", "`MeshProcess` stays only in §5"))
        code, out = self.run_main()
        self.assertEqual(code, 1, out)
        self.assertIn("SLOT-MISSING-IN-DOC  MeshProcess", out)

    def test_a_level_four_subsection_is_still_inside_the_section(self):
        self.doc_with(("| `IdleJob` | no |\n", ""),
                      ("\n**Counters (Systems tier).**",
                       "\n#### 4.9.1 Job notes\n\n`IdleJob` is described here, inside §4.9.\n\n**Counters (Systems tier).**"))
        code, out = self.run_main()
        self.assertEqual(code, 0, out)
        self.assertNotIn("JOB-MISSING-IN-DOC", out)

    def test_a_moved_heading_or_missing_table_exits_2_not_green(self):
        code, out = self.run_main()
        self.assertEqual(code, 0, out)

        self.doc_with(("### 4.9 Coverage map", "### Coverage map"))
        code, out = self.run_main()
        self.assertEqual(code, 2, out)
        self.assertIn("no '### 4.9' heading", out)

        self.doc_with(("Outside `World.Update` (a slot here may contain work it triggers):", "Nothing here either:"))
        code, out = self.run_main()
        self.assertEqual(code, 2, out)
        self.assertIn("slot tables yielded no rows", out)

    def test_a_repo_without_the_code_dir_exits_2(self):
        shutil.rmtree(os.path.join(self.root, "Assets"))
        code, out = self.run_main()
        self.assertEqual(code, 2, out)
        self.assertIn("code dir not found", out)


if __name__ == "__main__":
    unittest.main()
