using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Editor.Validation.Meshing
{
    /// <summary>
    /// Entry point and runner for the meshing engine validation suite (the
    /// <see cref="Jobs.MeshGenerationJob"/> standard-cube path).
    /// <para>
    /// Scenarios come in two categories with different semantics, mirroring the lighting suite:
    /// <list type="bullet">
    /// <item><b>Baseline</b> (regression) scenarios must always pass — a failure is a regression in
    /// the mesher (or in a performance optimization that was supposed to preserve output).</item>
    /// <item><b>Known-bug</b> scenarios reproduce documented mesher bugs test-first: they are
    /// EXPECTED to fail until the bug is fixed, and do not fail the suite.</item>
    /// </list>
    /// Scenario implementations live in the partial files
    /// (<c>MeshingValidationSuite.Baseline.cs</c>, and a future <c>.KnownBugs.cs</c>).
    /// </para>
    /// </summary>
    public static partial class MeshingValidationSuite
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
        /// Runs every registered scenario and prints a categorized summary.
        /// Baseline failures mark the suite red; known-bug reproductions are reported as warnings.
        /// </summary>
        [MenuItem("Minecraft Clone/Dev/Validate Meshing")]
        public static void RunAll()
        {
            Debug.Log("--- Starting Meshing Engine Validation ---");

            List<Scenario> scenarios = new List<Scenario>();
            AddBaselineScenarios(scenarios);
            AddRendererScenarios(scenarios);
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
                Debug.Log($"<color=green>ALL {baselinePassed} MESHING BASELINE TESTS PASSED.</color>");
            }
            else
            {
                Debug.LogError($"<color=red>{baselineFailed} OF {baselinePassed + baselineFailed} MESHING BASELINE TESTS FAILED — REGRESSION.</color>");
            }

            if (bugsReproduced > 0)
                Debug.Log($"{bugsReproduced} known-bug scenario(s) still reproduce their documented bug (expected).");

            if (bugsFixCandidates > 0)
                Debug.Log($"<color=cyan>{bugsFixCandidates} known-bug scenario(s) now pass — fix candidates!</color>");
        }

        /// <summary>Registers the baseline regression scenarios (implemented in MeshingValidationSuite.Baseline.cs).</summary>
        static partial void AddBaselineScenarios(List<Scenario> scenarios);

        /// <summary>
        /// Registers the renderer apply-path baselines (MH-6, implemented in MeshingValidationSuite.Renderer.cs).
        /// These exercise <see cref="SectionRenderer.UpdateMeshNative"/> via a separate fixture, not the
        /// meshing-job <see cref="Framework.MeshingTestWorld"/>; they count as baselines (must stay green).
        /// </summary>
        static partial void AddRendererScenarios(List<Scenario> scenarios);

        /// <summary>Registers the known-bug reproduction scenarios (none yet).</summary>
        static partial void AddKnownBugScenarios(List<Scenario> scenarios);
    }
}
