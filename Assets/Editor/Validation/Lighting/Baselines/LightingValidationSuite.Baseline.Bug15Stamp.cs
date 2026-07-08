using System.Collections.Generic;
using Editor.Validation.Lighting.Framework;
using Jobs.BurstData;
using UnityEngine;
using Scenario = Editor.Validation.Framework.Scenario;

namespace Editor.Validation.Lighting
{
    /// <summary>
    /// Baseline regression scenarios <b>B62/B63</b> guarding the <b>Bug 15</b> fix — cross-chunk
    /// surface stamps on opaque seam faces permanently wiped by border-column edits (fixed July 2026:
    /// <c>CheckEdgeVoxel</c>/<c>CheckEdgeVoxelRGB</c> stamp opaque centers instead of refusing them,
    /// <c>PullBackClaimStillSupported</c> mirrors the write condition, the sun BFS seeding re-spreads
    /// unchanged-but-lit edit nodes, and <c>PullBackDimmerCrossSeamStamp</c> re-derives stamps from
    /// dimmer/zeroed cross-seam neighbors under merge-time claim verification; see
    /// <c>Documentation/Bugs/_FIXED_BUGS.md</c>). Promoted from known-bug repros K15b/K15c after
    /// in-game confirmation (2026-07-05: seam wall face held sky 14 through the cap edit; the pre-fix
    /// build showed the stored-0 wipe in the F3/F7 debug views).
    /// <list type="bullet">
    /// <item><b>B62</b> — sunlight: a seam cliff face's cross-seam stamp survives its own column's
    /// border edit (the column-recalc wipe path).</item>
    /// <item><b>B63</b> — blocklight: a seam wall's stamp re-derives from the surviving cross-seam
    /// torch after the dominant nearer torch breaks (the darkness-wave wipe path, per channel).</item>
    /// </list>
    /// Self-registered via <see cref="AddBug15StampBaselineScenarios"/> (the <c>Baselines/</c>
    /// group-partial pattern). The border-heightmap fuzz that FOUND the bug remains known-bug repro
    /// K15a — its one remaining red is Bug 05's edge-round exhaustion, not this mechanism.
    /// </summary>
    public static partial class LightingValidationSuite
    {
        /// <summary>Superflat floor top for the Bug-15 stamp scenarios.</summary>
        private const int BUG15_FLOOR_Y = 10;

        /// <summary>The seam cliff's top surface (B62).</summary>
        private const int BUG15_CLIFF_TOP_Y = 40;

        /// <summary>
        /// Registers the Bug-15 stamp baselines (B62 sun, B63 RGB; called from
        /// <c>AddBaselineScenarios</c>).
        /// </summary>
        /// <param name="scenarios">The scenario list to append to.</param>
        static partial void AddBug15StampBaselineScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario(
                "B62: A seam cliff face's cross-seam sunlight surface stamp survives a same-column border edit (Bug 15 guard, promoted from K15b)",
                Baseline_SeamStampSurvivesColumnRecalc));
            scenarios.Add(new Scenario(
                "B63: A seam wall's blocklight surface stamp re-derives from a surviving cross-seam torch after a nearer source breaks (Bug 15 RGB guard, promoted from K15c)",
                Baseline_SeamStampRederivesFromCrossSeamTorch));
        }

        /// <summary>
        /// B62 (promoted from known-bug repro K15b after the July 2026 Bug 15 fix + in-game
        /// confirmation): a solid cliff in the west chunk abuts the x=15|16 seam, so a mid-face voxel's
        /// only transparent neighbor is the east chunk's border air — its sunlight surface stamp
        /// (15 − 1 = 14, the B39 rule) is fed exclusively cross-seam. Placing one block atop the
        /// cliff's border column triggers that column's sunlight recalc, which wipes the stamp; the
        /// removal wave's cross-seam pull-back must re-derive it. Pre-fix, <c>CheckEdgeVoxel</c>
        /// hard-refused opaque centers, so the face permanently darkened.
        /// </summary>
        private static bool Baseline_SeamStampSurvivesColumnRecalc()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(BUG15_FLOOR_Y, TestBlockPalette.Stone);
            world.FillBox(new Vector3Int(8, BUG15_FLOOR_Y + 1, 8), new Vector3Int(15, BUG15_CLIFF_TOP_Y, 12),
                TestBlockPalette.Stone);
            world.RecalculateHeightmaps();

            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "B62: initial lighting converges");

            Vector3Int probe = new Vector3Int(15, 25, 10);
            byte initialStamp = world.GetSkyLight(probe);
            passed &= LightingAssert.IsTrue(initialStamp == 14,
                "B62: the seam-face voxel carries the cross-seam surface stamp after initial lighting",
                $"Expected sky 14 at {probe} (east border air 15 − 1), got {initialStamp}");

            world.PlaceBlock(new Vector3Int(15, BUG15_CLIFF_TOP_Y + 1, 10), TestBlockPalette.Stone);
            passed &= LightingAssert.Converged(world.RunToConvergence(), "B62: post-edit reconciliation converges");

            byte postEditStamp = world.GetSkyLight(probe);
            passed &= LightingAssert.IsTrue(postEditStamp == 14,
                "B62: the cross-seam surface stamp survives the same-column border edit",
                $"Expected sky 14 at {probe} after the cap placement, got {postEditStamp}");

            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world),
                "B62: field matches the borderless oracle after the border-column edit");
            return passed;
        }

        /// <summary>
        /// B63 (promoted from known-bug repro K15c after the July 2026 Bug 15 fix + in-game
        /// confirmation): the blocklight twin. A 1-thick wall sits on the west side of the x=15|16
        /// seam; an east torch feeds the wall's cross-seam surface stamp, a closer west torch dominates
        /// it. Breaking the west torch launches the RGB darkness wave that wipes the (west-valued)
        /// stamp; the cross-seam re-spread pull-back must re-derive the east torch's contribution.
        /// Pre-fix, <c>CheckEdgeVoxelRGB</c> hard-refused opaque centers, so the wall face permanently
        /// darkened. Probe expectations are derived from the oracle, not hand-computed.
        /// </summary>
        private static bool Baseline_SeamStampRederivesFromCrossSeamTorch()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(BUG15_FLOOR_Y, TestBlockPalette.Stone);
            world.FillBox(new Vector3Int(15, BUG15_FLOOR_Y + 1, 8), new Vector3Int(15, 20, 12),
                TestBlockPalette.Stone);
            world.RecalculateHeightmaps();

            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "B63: initial lighting converges");

            world.PlaceBlock(new Vector3Int(18, 15, 10), TestBlockPalette.Torch);
            world.PlaceBlock(new Vector3Int(13, 15, 10), TestBlockPalette.Torch);
            passed &= LightingAssert.Converged(world.RunToConvergence(), "B63: two-torch setup converges");

            Vector3Int probe = new Vector3Int(15, 15, 10);
            (byte r0, byte g0, byte b0) = world.GetBlocklightRGB(probe);
            passed &= LightingAssert.IsTrue(r0 > 0 || g0 > 0 || b0 > 0,
                "B63: the seam wall carries a blocklight surface stamp with both torches lit",
                $"Expected a non-zero stamp at {probe}, got (0,0,0)");

            world.BreakBlock(new Vector3Int(13, 15, 10));
            passed &= LightingAssert.Converged(world.RunToConvergence(), "B63: post-break reconciliation converges");

            OracleLightField oracle = LightingOracle.Solve(world);
            (byte r, byte g, byte b) = world.GetBlocklightRGB(probe);
            byte expectedR = LightBitMapping.GetBlocklightR(oracle.GetLightData(probe));
            passed &= LightingAssert.IsTrue(r == expectedR,
                "B63: the wall's stamp re-derives from the surviving east torch across the seam",
                $"Expected R {expectedR} at {probe} after breaking the west torch, got ({r},{g},{b})");

            passed &= LightingAssert.MatchesOracle(world, oracle,
                "B63: field matches the borderless oracle after the west torch break");
            return passed;
        }
    }
}
