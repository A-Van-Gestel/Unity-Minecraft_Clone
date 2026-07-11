using System;
using System.Collections.Generic;
using System.Text;
using Editor.Validation.Lighting.Framework;
using Jobs.BurstData;
using UnityEngine;
using Scenario = Editor.Validation.Framework.Scenario;

namespace Editor.Validation.Lighting
{
    /// <summary>
    /// Baseline scenarios for the <b>Bug 16</b> family — the runaway RGB blocklight removal loop
    /// (infinite per-channel removal↔pull-back cycle inside a single <c>NeighborhoodLightingJob</c>
    /// pass, OOM-crashing the editor; fixed July 2026 by removal-node channel masking). The open
    /// Bug 17 repro K17a (<c>LightingValidationSuite.KnownBugs.cs</c>) shares this file's geometry
    /// builders and cycling recipe.
    /// <list type="bullet">
    /// <item><b>B86</b> — the simple form of the geometry (overlapping red/green cross-seam
    /// gradients, single clean break, wave-parallel): must stay green through any Bug 16/17 work
    /// (over-correction tripwire — a fix that suppresses legitimate cross-seam removal/re-light
    /// work would flip this red).</item>
    /// <item><b>B87</b> — the Bug 16 runaway guard (promoted from known-bug scenario K16a after the
    /// July 2026 fix + in-game confirmation): the interrupted-cycling recipe must settle with
    /// bounded removal work.</item>
    /// </list>
    /// </summary>
    public static partial class LightingValidationSuite
    {
        // --- Shared Bug-16 geometry: two different-colored opaque lamps in the two shared border
        // columns of the x15|16 seam (chunk row cz=1), five voxels apart along z? No — directly
        // adjacent across the seam, so BOTH gradients cover both seam columns and each lamp sits in
        // the other's near field. The known-bug recipe (K16a) adds a water volume and an
        // interrupted-reconciliation cycling sequence on top of the same base. ---
        private const int BUG16_FLOOR_Y = 63; // superflat stone floor top
        private const int BUG16_LAMP_Y = 64; // lamps rest on the floor
        private const int BUG16_LAMP_Z = 24; // chunk row cz=1 (interior seam, matches the Bug-12 family)
        private const int BUG16_RED_LAMP_X = 15; // chunk (0,1)'s shared border column
        private const int BUG16_GREEN_LAMP_X = 16; // chunk (1,1)'s shared border column

        // K16a water volume: submerges both lamps and the seam corner region (semi-transparent
        // opacity-2 attenuation builds the mixed-channel plateau values the runaway cycle needs).
        private static readonly Vector3Int s_bug16WaterMin = new Vector3Int(10, 64, 20);
        private static readonly Vector3Int s_bug16WaterMax = new Vector3Int(21, 67, 28);

        /// <summary>Registers the Bug-16 family baseline scenarios (called from <c>AddBaselineScenarios</c>).</summary>
        /// <param name="scenarios">The scenario list to append to.</param>
        static partial void AddBug16RunawayBaselineScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario(
                "B86: Breaking a red lamp whose gradient overlaps a live cross-seam green gradient converges wave-parallel to the oracle (Bug 16 family, simple form + fix over-correction tripwire)",
                Baseline_RgbSeamBlendCleanBreakConverges));
            scenarios.Add(new Scenario(
                "B87: Interrupted underwater break/re-place cycling of a seam lamp settles with bounded removal work — no runaway RGB removal loop (Bug 16 guard; promoted from K16a)",
                Baseline_InterruptedCyclingSettlesWithBoundedRemovalWork));
        }

        /// <summary>
        /// Runs the shared Bug 16/17 interrupted-reconciliation recipe on an already-converged
        /// water-world blend: <paramref name="cycles"/> rounds of (hold the green chunk's job on a
        /// pre-edit snapshot → break the red lamp → run the red chunk's removal pass → water re-flows
        /// into the hole → complete the stale green flight → under-budgeted wave → re-place the lamp
        /// → under-budgeted wave), then a final break with water re-flow. Leaves the grid with
        /// pending work; the caller runs the settling convergence and asserts. Shared by B87 and the
        /// Bug 17 repro K17a.
        /// </summary>
        /// <param name="world">The converged Bug-16 water world.</param>
        /// <param name="cycles">How many interrupted break/re-place rounds to run (the Bug 17 ghost island needs ≥2).</param>
        private static void RunBug16InterruptedCyclingRecipe(LightingTestWorld world, int cycles)
        {
            Vector3Int redPos = new Vector3Int(BUG16_RED_LAMP_X, BUG16_LAMP_Y, BUG16_LAMP_Z);

            for (int i = 0; i < cycles; i++)
            {
                LightingTestWorld.LightingJobFlight greenFlight = world.BeginLightingJob(new Vector2Int(1, 1));
                world.BreakBlock(redPos);
                world.RunLightingJob(new Vector2Int(0, 1)); // removal pass against the held green snapshot
                world.PlaceBlock(redPos, TestBlockPalette.Water); // fluid re-flow into the hole mid-reconciliation
                world.CompleteLightingJob(greenFlight); // stale pre-break merge + deferred-mod drain
                world.RunWaveToConvergence(1); // deliberately under-budgeted: next edit lands mid-reconciliation
                world.PlaceBlock(redPos, TestBlockPalette.LampRed);
                world.RunWaveToConvergence(1);
            }

            world.BreakBlock(redPos);
            world.PlaceBlock(redPos, TestBlockPalette.Water);
        }

        /// <summary>
        /// B87 (Bug 16 guard; promoted from known-bug scenario K16a after the July 2026 fix,
        /// in-game confirmed 2026-07-11): the deterministic runaway-removal recipe. Before the fix,
        /// interrupted reconciliation left non-monotone mixed R/G plateau values at the seam corners
        /// and the final break drove a blocklight removal phase into an infinite per-channel re-zero
        /// ↔ seam pull-back cycle inside a single job — OOM-crashing the editor without the
        /// <c>NeighborhoodLightingJob.MAX_BFS_NODES_PER_PASS</c> fail-safe. The fix masks re-enqueued
        /// removal nodes to the channels actually zeroed (per-channel strict-decrease termination).
        /// Asserts Bug 16's invariants: no job hits the fail-safe cap, reconciliation converges, and
        /// the field matches the oracle up to Bug 17's independent ghost residue (dated exemption in
        /// <see cref="MatchesOracleExceptBug17Ghost"/> — the open Bug 17 / K17a owns that defect).
        /// </summary>
        private static bool Baseline_InterruptedCyclingSettlesWithBoundedRemovalWork()
        {
            const int cycles = 3;

            using LightingTestWorld world = BuildBug16RgbSeamBlendWorld(withWater: true);
            bool passed = SetUpBug16InitialBlend(world, "B87");

            using (WorkCapAbortListener capAborts = new WorkCapAbortListener())
            {
                RunBug16InterruptedCyclingRecipe(world, cycles);

                passed &= LightingAssert.Converged(world.RunWaveToConvergence(),
                    "B87: post-cycling reconciliation reaches a stable field");
                passed &= LightingAssert.IsTrue(capAborts.Count == 0,
                    "B87: no lighting job hit the BFS work-cap fail-safe (bounded removal work)",
                    $"{capAborts.Count.ToString()} work-cap abort(s) logged — the runaway removal cycle is back");
            }

            passed &= MatchesOracleExceptBug17Ghost(world, LightingOracle.Solve(world),
                "B87: the settled field matches the oracle up to Bug 17 ghost residue (red gone, green intact)");
            return passed;
        }

        /// <summary>
        /// Builds the shared Bug-16 world: a superflat stone floor with an opaque red lamp (emission
        /// 15,0,0) and an opaque green lamp (0,15,0) in the two shared seam border columns, gradients
        /// fully overlapping across the x15|16 seam. Returned un-lit; the caller runs initial
        /// lighting, applies its perturbation, and disposes the world.
        /// </summary>
        /// <param name="withWater">True to submerge the lamp region in the K16a water volume.</param>
        /// <returns>A freshly-built, un-lit grid-3 test world.</returns>
        private static LightingTestWorld BuildBug16RgbSeamBlendWorld(bool withWater)
        {
            LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(BUG16_FLOOR_Y, TestBlockPalette.Stone);
            if (withWater)
                world.FillBox(s_bug16WaterMin, s_bug16WaterMax, TestBlockPalette.Water);
            world.SetBlock(new Vector3Int(BUG16_RED_LAMP_X, BUG16_LAMP_Y, BUG16_LAMP_Z), TestBlockPalette.LampRed);
            world.SetBlock(new Vector3Int(BUG16_GREEN_LAMP_X, BUG16_LAMP_Y, BUG16_LAMP_Z), TestBlockPalette.LampGreen);
            world.RecalculateHeightmaps();
            return world;
        }

        /// <summary>
        /// Converges the Bug-16 world's initial lighting and asserts the overlap precondition: both
        /// shared seam columns carry BOTH the red and the green channel, so a red removal wave must
        /// work through the live green field (and the seam) — the contamination surface of the
        /// runaway removal cycle.
        /// </summary>
        /// <param name="world">The freshly-built Bug-16 world.</param>
        /// <param name="tag">The scenario tag used in assertion names (e.g. "B86").</param>
        /// <returns>True when the initial field converged, matches the oracle, and overlaps as required.</returns>
        private static bool SetUpBug16InitialBlend(LightingTestWorld world, string tag)
        {
            bool passed = LightingAssert.Converged(world.RunInitialLighting(), $"{tag}: initial lighting converges");
            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world),
                $"{tag}: initial two-lamp RGB blend matches the borderless oracle");

            (byte R, byte G, byte B) west = world.GetBlocklightRGB(new Vector3Int(BUG16_RED_LAMP_X, BUG16_LAMP_Y, BUG16_LAMP_Z + 1));
            (byte R, byte G, byte B) east = world.GetBlocklightRGB(new Vector3Int(BUG16_GREEN_LAMP_X, BUG16_LAMP_Y, BUG16_LAMP_Z + 1));
            passed &= LightingAssert.IsTrue(west.R > 0 && west.G > 0 && east.R > 0 && east.G > 0,
                $"{tag}: both seam columns carry both channels (overlapping cross-seam gradients)",
                $"Expected R>0 and G>0 beside both lamps, got west (R{west.R.ToString()} G{west.G.ToString()}) / east (R{east.R.ToString()} G{east.G.ToString()})");
            return passed;
        }

        /// <summary>
        /// Counts <c>[LightingJob DIAG]</c> work-cap abort errors logged while in scope — the direct
        /// signal of a runaway BFS pass (Bug 16). Subscribes to <see cref="Application.logMessageReceived"/>
        /// on construction and unsubscribes on dispose, so a scenario can assert "no lighting job hit
        /// the fail-safe during this run".
        /// </summary>
        private sealed class WorkCapAbortListener : IDisposable
        {
            /// <summary>How many work-cap abort errors were logged while this listener was active.</summary>
            public int Count { get; private set; }

            public WorkCapAbortListener()
            {
                // UDR0004 is a false positive here: the analyzer only recognizes the OnDisable
                // deregistration pattern, but this listener deregisters in Dispose and every use
                // site is a `using` scope.
#pragma warning disable UDR0004
                Application.logMessageReceived += OnLog;
#pragma warning restore UDR0004
            }

            public void Dispose()
            {
                Application.logMessageReceived -= OnLog;
            }

            private void OnLog(string condition, string stackTrace, LogType type)
            {
                if (type == LogType.Error && condition.Contains("[LightingJob DIAG]"))
                    Count++;
            }
        }

        /// <summary>
        /// Full-field oracle compare that exempts the <b>Bug 17</b> residue class: over-bright RED
        /// voxels (actual R &gt; oracle R with sky/G/B exact) are tolerated and reported, everything
        /// else (any sky/G/B mismatch, or under-bright R) fails. B87's field assertion runs through
        /// this so it owns only Bug 16's invariants; K17a asserts the unexempted full compare.
        /// RESTORE the plain <see cref="LightingAssert.MatchesOracle"/> in B87 once Bug 17 is fixed
        /// (note dated 2026-07-11).
        /// </summary>
        /// <param name="world">The test world after convergence.</param>
        /// <param name="expected">The borderless oracle solution.</param>
        /// <param name="testName">The assertion name for console output.</param>
        /// <returns>True when the field matches the oracle up to the exempted Bug 17 ghost class.</returns>
        private static bool MatchesOracleExceptBug17Ghost(LightingTestWorld world, OracleLightField expected, string testName)
        {
            const int maxReported = 12;
            int failures = 0;
            int exempted = 0;
            StringBuilder diffs = new StringBuilder();

            int width = world.GridSize * VoxelData.ChunkWidth;
            for (int x = 0; x < width; x++)
            for (int y = 0; y < VoxelData.ChunkHeight; y++)
            for (int z = 0; z < width; z++)
            {
                Vector3Int pos = new Vector3Int(x, y, z);
                ushort act = world.GetLightData(pos);
                ushort exp = expected.GetLightData(pos);
                if (act == exp) continue;

                bool onlyRedOverbright =
                    LightBitMapping.GetSkyLight(act) == LightBitMapping.GetSkyLight(exp) &&
                    LightBitMapping.GetBlocklightG(act) == LightBitMapping.GetBlocklightG(exp) &&
                    LightBitMapping.GetBlocklightB(act) == LightBitMapping.GetBlocklightB(exp) &&
                    LightBitMapping.GetBlocklightR(act) > LightBitMapping.GetBlocklightR(exp);
                if (onlyRedOverbright)
                {
                    exempted++;
                    continue;
                }

                failures++;
                if (failures <= maxReported)
                {
                    diffs.AppendLine($"  {pos.ToString()}: sky {LightBitMapping.GetSkyLight(exp).ToString()}/{LightBitMapping.GetSkyLight(act).ToString()}, " +
                                     $"R {LightBitMapping.GetBlocklightR(exp).ToString()}/{LightBitMapping.GetBlocklightR(act).ToString()}, " +
                                     $"G {LightBitMapping.GetBlocklightG(exp).ToString()}/{LightBitMapping.GetBlocklightG(act).ToString()}, " +
                                     $"B {LightBitMapping.GetBlocklightB(exp).ToString()}/{LightBitMapping.GetBlocklightB(act).ToString()} (expected/actual)");
                }
            }

            if (failures > 0)
            {
                Debug.LogError($"[FAIL] {testName}\n{failures.ToString()} voxel(s) differ beyond the Bug 17 exemption:\n{diffs}");
                return false;
            }

            Debug.Log($"[PASS] {testName}{(exempted > 0 ? $" ({exempted.ToString()} over-bright-R voxel(s) exempted as Bug 17 ghost residue)" : "")}");
            return true;
        }

        /// <summary>
        /// B86 (Bug 16 family): the SIMPLE form of the K16a geometry — dry world, one clean break of
        /// the red lamp from a fully converged field, wave-parallel reconciliation. Converges to the
        /// oracle today (the runaway needs the interrupted-reconciliation plateau state K16a builds;
        /// a clean break's monotone gradients cannot arm the cycle) and must STAY green through the
        /// Bug 16 fix: it guards the legitimate cross-seam removal + re-light work a naive fix
        /// (e.g. suppressing the darkness-phase seam pull-back entirely) would break.
        /// </summary>
        private static bool Baseline_RgbSeamBlendCleanBreakConverges()
        {
            using LightingTestWorld world = BuildBug16RgbSeamBlendWorld(withWater: false);
            bool passed = SetUpBug16InitialBlend(world, "B86");

            world.BreakBlock(new Vector3Int(BUG16_RED_LAMP_X, BUG16_LAMP_Y, BUG16_LAMP_Z));

            passed &= LightingAssert.Converged(world.RunWaveToConvergence(),
                "B86: post-break wave-parallel reconciliation reaches a stable field");
            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world),
                "B86: red field clears and the live green field survives intact");
            return passed;
        }
    }
}
