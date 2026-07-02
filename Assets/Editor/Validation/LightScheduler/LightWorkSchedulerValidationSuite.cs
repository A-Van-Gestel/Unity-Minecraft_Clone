using System;
using System.Collections.Generic;
using Helpers;
using UnityEditor;
using UnityEngine;

namespace Editor.Validation.LightScheduler
{
    /// <summary>
    /// Entry point and runner for the <see cref="LightWorkScheduler"/> validation suite (MT-2). The
    /// scheduler is the whole of the MT-2 behavior change — a pure managed bookkeeping structure with
    /// no Burst, jobs, or world state — so it is tested in isolation here, directly asserting the
    /// ready/waiting split and promotion contract the refactor promised: parked chunks re-enter the
    /// ready set only via a flag callback, a 3×3 neighborhood promotion event, or the fail-safe
    /// <c>PromoteAll</c> backstop.
    /// <para>All scenarios are <b>baselines</b> (must stay green); a failure is a regression in the
    /// scheduler. The known-bug channel is kept for parity with the other suites but is currently
    /// unused.</para>
    /// <para><b>Prove-red:</b> each scenario names, in its docstring, the one-line mutation that should
    /// turn it red (the project's manual prove-red discipline — break it, run, confirm red, revert).</para>
    /// <para>Scenario bodies live in <c>LightWorkSchedulerValidationSuite.Baseline.cs</c>.</para>
    /// </summary>
    public static partial class LightWorkSchedulerValidationSuite
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
        /// Runs every registered scenario and prints a categorized summary. Baseline failures mark the
        /// suite red; known-bug reproductions are reported as warnings.
        /// </summary>
        [MenuItem("Minecraft Clone/Dev/Validate Light Work Scheduler")]
        public static void RunAll()
        {
            Debug.Log("--- Starting Light Work Scheduler Validation (MT-2) ---");

            List<Scenario> scenarios = new List<Scenario>();
            AddBaselineScenarios(scenarios);

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
                    Debug.Log($"<color=cyan>✅ {scenario.Name}: known-bug scenario PASSES — {scenario.KnownBugId} may be implemented. Verify, then promote it to a baseline.</color>");
                }
                else
                {
                    bugsReproduced++;
                    Debug.LogWarning($"⚠️ {scenario.Name}: reproduces {scenario.KnownBugId} (expected failure until implemented).");
                }
            }

            // --- Summary ---
            if (baselineFailed == 0)
            {
                Debug.Log($"<color=green>ALL {baselinePassed} LIGHT WORK SCHEDULER BASELINE TESTS PASSED.</color>");
            }
            else
            {
                Debug.LogError($"<color=red>{baselineFailed} OF {baselinePassed + baselineFailed} LIGHT WORK SCHEDULER BASELINE TESTS FAILED — REGRESSION.</color>");
            }

            if (bugsReproduced > 0)
                Debug.Log($"{bugsReproduced} known-bug scenario(s) still reproduce their documented behavior gap (expected).");

            if (bugsFixCandidates > 0)
                Debug.Log($"<color=cyan>{bugsFixCandidates} known-bug scenario(s) now pass — implementation candidates!</color>");
        }

        /// <summary>Registers the baseline regression scenarios (implemented in LightWorkSchedulerValidationSuite.Baseline.cs).</summary>
        static partial void AddBaselineScenarios(List<Scenario> scenarios);

        // --- Shared fixture & assertion helpers -------------------------------------------------

        /// <summary>Voxel-origin position of the chunk at chunk-grid coordinate (<paramref name="cx"/>, <paramref name="cz"/>).</summary>
        /// <param name="cx">Chunk-grid X.</param>
        /// <param name="cz">Chunk-grid Z.</param>
        /// <returns>The voxel-origin position the scheduler keys on.</returns>
        private static Vector2Int Pos(int cx, int cz) =>
            new Vector2Int(cx * VoxelData.ChunkWidth, cz * VoxelData.ChunkWidth);

        /// <summary>Logs a single assertion as PASS/FAIL and returns its result for AND-chaining.</summary>
        /// <param name="label">Human-readable assertion description.</param>
        /// <param name="condition">The asserted condition.</param>
        /// <returns><paramref name="condition"/>.</returns>
        private static bool Check(string label, bool condition)
        {
            if (condition) Debug.Log($"  [PASS] {label}");
            else Debug.LogError($"  [FAIL] {label}");
            return condition;
        }

        /// <summary>
        /// Asserts a position's membership across both sets in one call: ready, waiting, or fully absent.
        /// </summary>
        /// <param name="label">Assertion description.</param>
        /// <param name="scheduler">The scheduler under test.</param>
        /// <param name="pos">The position to probe.</param>
        /// <param name="expectReady">Expected <see cref="LightWorkScheduler.IsReady"/>.</param>
        /// <param name="expectWaiting">Expected <see cref="LightWorkScheduler.IsWaiting"/>.</param>
        /// <returns>True if both memberships match.</returns>
        private static bool CheckState(string label, LightWorkScheduler scheduler, Vector2Int pos,
            bool expectReady, bool expectWaiting)
        {
            bool ready = scheduler.IsReady(pos);
            bool waiting = scheduler.IsWaiting(pos);
            if (ready == expectReady && waiting == expectWaiting)
                return Check(label, true);

            return Check($"{label} — expected ready={expectReady}/waiting={expectWaiting}, got ready={ready}/waiting={waiting}", false);
        }
    }
}
