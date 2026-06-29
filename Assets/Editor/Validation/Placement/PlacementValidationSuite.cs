using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Editor.Validation.Placement
{
    /// <summary>
    /// Entry point and runner for the player block-<b>placement</b> validation suite — the tag-driven decisions in
    /// <c>PlayerInteraction.PlaceCursorBlocks</c> (extracted into <see cref="Data.PlacementResolver"/>) composed with
    /// the real <c>World.CheckForVoxel</c> / <c>World.IsCellOccupiedForPlacement</c> seams.
    /// <para>
    /// Scenarios come in three categories, mirroring the lighting/meshing suites:
    /// <list type="bullet">
    /// <item><b>Baseline</b> (regression) scenarios run against a controlled, correctly-configured palette and must
    /// always pass — a failure means the placement mechanism itself regressed.</item>
    /// <item><b>Data-audit</b> scenarios inspect the <b>real</b> shipping <c>BlockDatabase.asset</c> for tag
    /// misconfigurations that break placement; they are reported as known-bug reproductions (EXPECTED to fail until
    /// the data is retuned) so they don't fail the suite.</item>
    /// <item><b>Known-bug</b> scenarios reproduce the player-visible symptom (a block that can't be placed on top of
    /// another) against the real database, test-first.</item>
    /// </list>
    /// Scenario implementations live in the partial files (<c>.Baseline.cs</c>, <c>.DataAudit.cs</c>, <c>.KnownBugs.cs</c>).
    /// </para>
    /// </summary>
    public static partial class PlacementValidationSuite
    {
        /// <summary>A single validation scenario: a named test delegate, optionally tied to a documented bug.</summary>
        private readonly struct Scenario
        {
            /// <summary>The scenario name used in log output.</summary>
            public readonly string Name;

            /// <summary>The test body. Returns true when all of its assertions passed.</summary>
            public readonly Func<bool> Run;

            /// <summary>The documented bug this scenario reproduces, or null for a baseline regression scenario.</summary>
            public readonly string KnownBugId;

            /// <summary>Initializes a scenario.</summary>
            public Scenario(string name, Func<bool> run, string knownBugId = null)
            {
                Name = name;
                Run = run;
                KnownBugId = knownBugId;
            }
        }

        /// <summary>
        /// Runs every registered scenario and prints a categorized summary. Baseline failures mark the suite red;
        /// data-audit and known-bug reproductions are reported as warnings (expected until the tags/logic are fixed).
        /// </summary>
        [MenuItem("Minecraft Clone/Dev/Validate Placement")]
        public static void RunAll()
        {
            Debug.Log("--- Starting Player Placement Validation ---");

            List<Scenario> scenarios = new List<Scenario>();
            AddBaselineScenarios(scenarios);
            AddDataAuditScenarios(scenarios);
            AddKnownBugScenarios(scenarios);

            int baselinePassed = 0;
            int baselineFailed = 0;
            int bugsReproduced = 0;
            int bugsFixCandidates = 0;

            foreach (Scenario scenario in scenarios)
            {
                Debug.Log($"--- Scenario: {scenario.Name} ---");
                bool passed;

                try
                {
                    passed = scenario.Run();
                }
                catch (Exception e)
                {
                    Debug.LogError($"[FAIL] {scenario.Name}\nScenario threw: {e}");
                    passed = false;
                }

                if (scenario.KnownBugId == null)
                {
                    if (passed) baselinePassed++;
                    else baselineFailed++;
                }
                else if (passed)
                {
                    bugsFixCandidates++;
                    Debug.Log($"<color=cyan>✅ {scenario.Name}: known-bug scenario PASSES — {scenario.KnownBugId} may be fixed. Verify in-game, then archive via the archive-fixed-bug workflow.</color>");
                }
                else
                {
                    bugsReproduced++;
                    Debug.LogWarning($"⚠️ {scenario.Name}: reproduces {scenario.KnownBugId} (expected failure until the bug is fixed).");
                }
            }

            // --- Summary ---
            if (baselineFailed == 0)
            {
                Debug.Log($"<color=green>ALL {baselinePassed} PLACEMENT BASELINE TESTS PASSED.</color>");
            }
            else
            {
                Debug.LogError($"<color=red>{baselineFailed} OF {baselinePassed + baselineFailed} PLACEMENT BASELINE TESTS FAILED — REGRESSION.</color>");
            }

            if (bugsReproduced > 0)
                Debug.Log($"{bugsReproduced} known-bug/data-audit scenario(s) still reproduce their documented bug (expected).");

            if (bugsFixCandidates > 0)
                Debug.Log($"<color=cyan>{bugsFixCandidates} known-bug/data-audit scenario(s) now pass — fix candidates!</color>");
        }

        /// <summary>
        /// Logs and evaluates a single assertion. Returns <paramref name="condition"/> so a scenario can AND its
        /// assertions together: <c>bool ok = Expect(...); ok &amp;= Expect(...); return ok;</c>.
        /// </summary>
        /// <param name="condition">The asserted condition.</param>
        /// <param name="message">Description of what was expected (logged on failure).</param>
        /// <returns><paramref name="condition"/>, unchanged.</returns>
        private static bool Expect(bool condition, string message)
        {
            if (!condition)
                Debug.LogError($"  [ASSERT FAILED] {message}");
            return condition;
        }

        /// <summary>Registers the baseline regression scenarios (implemented in PlacementValidationSuite.Baseline.cs).</summary>
        static partial void AddBaselineScenarios(List<Scenario> scenarios);

        /// <summary>Registers the real-database tag-audit scenarios (implemented in PlacementValidationSuite.DataAudit.cs).</summary>
        static partial void AddDataAuditScenarios(List<Scenario> scenarios);

        /// <summary>Registers the known-bug reproduction scenarios (implemented in PlacementValidationSuite.KnownBugs.cs).</summary>
        static partial void AddKnownBugScenarios(List<Scenario> scenarios);
    }
}
