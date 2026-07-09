using System.Collections.Generic;
using Editor.Validation.Framework;
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
        /// <summary>
        /// Runs every registered scenario and prints a categorized summary via the shared
        /// <see cref="ValidationSuiteRunner"/>. Baseline failures mark the suite red; known-bug
        /// reproductions are reported as warnings.
        /// </summary>
        [MenuItem("Minecraft Clone/Dev/Validate Light Work Scheduler")]
        public static void RunAll() => Execute();

        /// <summary>
        /// Builds and runs the scheduler scenarios, returning the categorized result (the headless/CI entry
        /// point). Uses the <see cref="KnownBugChannel.Unimplemented"/> channel because the known-bug slot
        /// here pins not-yet-implemented behavior — a pass means "promote to a baseline", not "archive a fix".
        /// </summary>
        /// <param name="logToConsole">When false, runs silently and only returns the result (for headless/CI use).</param>
        /// <param name="showProgress">When false, suppresses this suite's own progress bar (the aggregate runner drives one).</param>
        /// <returns>The categorized, timed result of the run.</returns>
        public static ValidationRunResult Execute(bool logToConsole = true, bool showProgress = true)
        {
            List<Scenario> scenarios = new List<Scenario>();
            AddBaselineScenarios(scenarios);
            return ValidationSuiteRunner.Execute("Light Work Scheduler", scenarios, KnownBugChannel.Unimplemented, logToConsole, showProgress);
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
