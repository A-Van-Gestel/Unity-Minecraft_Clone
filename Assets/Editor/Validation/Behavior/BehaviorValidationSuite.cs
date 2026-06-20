using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Editor.Validation.Behavior
{
    /// <summary>
    /// Entry point and runner for the block-behavior tick validation suite — the regression + parity guard
    /// for the <c>BlockBehavior</c> tick path (<c>Behave</c>/<c>Active</c>) that the <b>TG-4</b> (per-behavior
    /// native collections) and <b>TG-5</b> (Burst function-pointer dispatch) optimizations re-architect.
    /// <para>
    /// Scenario categories mirror the lighting/meshing suites: <b>baseline</b> (regression) scenarios must
    /// always pass; <b>known-bug</b> scenarios reproduce documented bugs test-first and are expected to fail
    /// until fixed. Behavior parity rests on three legs (see
    /// <c>Documentation/Design/BEHAVIOR_VALIDATION_HARNESS_DESIGN.md</c>): golden-master snapshots,
    /// behavioral invariants (determinism, non-vacuity), and — once both code paths exist — the BH-D1
    /// old-vs-new differential.
    /// </para>
    /// </summary>
    public static partial class BehaviorValidationSuite
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
        /// Runs every registered scenario and prints a categorized summary. Baseline failures mark the suite
        /// red; known-bug reproductions are reported as warnings.
        /// </summary>
        [MenuItem("Minecraft Clone/Dev/Validate Behavior")]
        public static void RunAll()
        {
            Debug.Log("--- Starting Block Behavior Validation ---");

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
                Debug.Log($"<color=green>ALL {baselinePassed} BEHAVIOR BASELINE TESTS PASSED.</color>");
            }
            else
            {
                Debug.LogError($"<color=red>{baselineFailed} OF {baselinePassed + baselineFailed} BEHAVIOR BASELINE TESTS FAILED — REGRESSION.</color>");
            }

            if (bugsReproduced > 0)
                Debug.Log($"{bugsReproduced} known-bug scenario(s) still reproduce their documented bug (expected).");

            if (bugsFixCandidates > 0)
                Debug.Log($"<color=cyan>{bugsFixCandidates} known-bug scenario(s) now pass — fix candidates!</color>");
        }

        /// <summary>Registers the baseline regression scenarios (implemented in BehaviorValidationSuite.Baseline.cs).</summary>
        static partial void AddBaselineScenarios(List<Scenario> scenarios);

        /// <summary>Registers the known-bug reproduction scenarios (none yet).</summary>
        static partial void AddKnownBugScenarios(List<Scenario> scenarios);
    }
}
