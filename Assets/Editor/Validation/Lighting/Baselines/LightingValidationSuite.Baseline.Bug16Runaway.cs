using System.Collections.Generic;
using Editor.Validation.Lighting.Framework;
using UnityEngine;
using Scenario = Editor.Validation.Framework.Scenario;

namespace Editor.Validation.Lighting
{
    /// <summary>
    /// Baseline scenarios for the <b>Bug 16</b> family — the runaway RGB blocklight removal loop
    /// (infinite per-channel removal↔pull-back cycle inside a single <c>NeighborhoodLightingJob</c>
    /// pass, OOM-crashing the editor). The known-bug repro K16a lives in
    /// <c>LightingValidationSuite.KnownBugs.cs</c> and shares this file's geometry builders.
    /// <list type="bullet">
    /// <item><b>B86</b> — the simple form of the same geometry (overlapping red/green cross-seam
    /// gradients, single clean break, wave-parallel): converges to the oracle today and must stay
    /// green through the Bug 16 fix (over-correction tripwire — a fix that stops the runaway by
    /// suppressing legitimate cross-seam removal/re-light work would flip this red).</item>
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
