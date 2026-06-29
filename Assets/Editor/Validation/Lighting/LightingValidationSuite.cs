using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Editor.Validation.Lighting
{
    /// <summary>
    /// Entry point and runner for the lighting engine validation suite.
    /// <para>
    /// Scenarios come in two categories with different semantics:
    /// <list type="bullet">
    /// <item><b>Baseline</b> (regression) scenarios must always pass — a failure is a regression.</item>
    /// <item><b>Known-bug</b> scenarios reproduce documented bugs from <c>LIGHTING_BUGS.md</c> test-first:
    /// they are EXPECTED to fail until the bug is fixed, and do not fail the suite. When one starts
    /// passing, the fix is a candidate for in-game confirmation and bug archival.</item>
    /// </list>
    /// Scenario implementations live in the partial files
    /// (<c>LightingValidationSuite.Baseline.cs</c>, <c>LightingValidationSuite.KnownBugs.cs</c>).
    /// </para>
    /// </summary>
    public static partial class LightingValidationSuite
    {
        /// <summary>A single validation scenario: a named test delegate, optionally tied to a documented bug.</summary>
        private readonly struct Scenario
        {
            /// <summary>The scenario name used in log output.</summary>
            public readonly string Name;

            /// <summary>The test body. Returns true when all of its assertions passed.</summary>
            public readonly Func<bool> Run;

            /// <summary>The LIGHTING_BUGS.md bug this scenario reproduces, or null for a baseline regression scenario.</summary>
            public readonly string KnownBugId;

            /// <summary>Initializes a scenario.</summary>
            /// <param name="name">The scenario name used in log output.</param>
            /// <param name="run">The test body.</param>
            /// <param name="knownBugId">The documented bug ID this scenario reproduces, or null for baseline.</param>
            public Scenario(string name, Func<bool> run, string knownBugId = null)
            {
                Name = name;
                Run = run;
                KnownBugId = knownBugId;
            }
        }

        /// <summary>
        /// Runs every registered scenario and prints a categorized summary.
        /// Baseline failures mark the suite red; known-bug reproductions are reported as warnings.
        /// </summary>
        [MenuItem("Minecraft Clone/Dev/Validate Lighting Engine")]
        public static void RunAll()
        {
            Debug.Log("--- Starting Lighting Engine Validation ---");

            List<Scenario> scenarios = new List<Scenario>();
            AddBaselineScenarios(scenarios);
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
                Debug.Log($"<color=green>ALL {baselinePassed} LIGHTING BASELINE TESTS PASSED.</color>");
            }
            else
            {
                Debug.LogError($"<color=red>{baselineFailed} OF {baselinePassed + baselineFailed} LIGHTING BASELINE TESTS FAILED — REGRESSION.</color>");
            }

            if (bugsReproduced > 0)
                Debug.Log($"{bugsReproduced} known-bug scenario(s) still reproduce their documented bug (expected).");

            if (bugsFixCandidates > 0)
                Debug.Log($"<color=cyan>{bugsFixCandidates} known-bug scenario(s) now pass — fix candidates!</color>");
        }

        /// <summary>Registers the baseline regression scenarios (implemented in LightingValidationSuite.Baseline.cs).</summary>
        /// <param name="scenarios">The scenario list to append to.</param>
        static partial void AddBaselineScenarios(List<Scenario> scenarios);

        /// <summary>Registers the known-bug reproduction scenarios (implemented in LightingValidationSuite.KnownBugs.cs).</summary>
        /// <param name="scenarios">The scenario list to append to.</param>
        static partial void AddKnownBugScenarios(List<Scenario> scenarios);
    }
}
