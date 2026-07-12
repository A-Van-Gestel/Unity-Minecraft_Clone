using System.Collections.Generic;
using Editor.Validation.Lighting.Framework;
using UnityEngine;
using Scenario = Editor.Validation.Framework.Scenario;

namespace Editor.Validation.Lighting
{
    /// <summary>
    /// Baseline <b>B89</b> — fidelity finding <b>C12</b>: the RGB mirror of the Bug 14 stale-pull-back
    /// scenario (B60/B61's blocklight twin). The darkness-phase seam pull-back
    /// (<c>NeighborhoodLightingJob.CheckEdgeVoxelRGB</c>, the Bug 07 defect-2 re-light of a just-darkened
    /// border voxel) re-lights that voxel from the cross-seam neighbor's <i>schedule-time snapshot</i> via a
    /// placement write with <b>no <see cref="Jobs.PullBackClaim"/></b> (claims carry <c>WrittenSky</c> only;
    /// <c>PullBackClaimStillSupported</c> is sky-only). C12 asked whether a stale RGB pull-back — whose
    /// supporting neighbor darkened <i>after</i> the snapshot — plants a sourceless ghost that survives
    /// merge, the way Bug 14 did on the sky channel.
    /// <para>
    /// <b>Verdict: it does not.</b> A held-flight probe (break A's source, snapshot A while the cross-seam
    /// neighbor B is still bright, darken B for real, then complete A's stale darkness wave so its pull-back
    /// re-lights the border from the now-stale snapshot) converges to the borderless oracle: the ordinary
    /// asymmetric cross-seam removal branch clears the stale re-light on the following wave (matching the
    /// Bug 17 investigation, which ruled the pull-back out as a planter — "neutering the RGB claim path
    /// changed nothing"). This baseline pins that self-heal, scoping the RGB removal-machinery gap to the
    /// missing <i>initiator</i> (Bug 18) and NOT the missing claim verification — the RGB parallel of Bug 17
    /// adding only the veto.
    /// </para>
    /// <para>
    /// <b>Prove-red (documented, not automated):</b> making the two lamps <i>symmetric</i> (equal distance
    /// from the seam) flips this red — that is exactly repro <b>K18a</b> / Bug 18, the mutually-equal seam
    /// with no removal initiator. So the harness here demonstrably detects a surviving cross-seam RGB ghost;
    /// the asymmetric arrangement staying green is what isolates C12 (pull-back) from Bug 18 (initiator).
    /// </para>
    /// </summary>
    public static partial class LightingValidationSuite
    {
        // --- C12 geometry: a sealed corridor straddling the x15|16 seam, lit by two red lamps at DIFFERENT
        // distances from the seam (x14 in chunk (0,1) is 1 step away; x20 in chunk (1,1) is 4). The
        // asymmetry is load-bearing: unlike Bug 18's mutually-equal seam, an asymmetric gradient always has a
        // strictly-lower side the existing PropagateDarknessRGB "neighbor < removed level" branch can remove,
        // so the stale pull-back's re-light is cleared rather than locked (the B51 lesson, on the RGB
        // channel). Both lamps sit inside the carved corridor. ---
        private const int C12_SLAB_MIN_Y = 58;
        private const int C12_SLAB_MAX_Y = 68;
        private const int C12_CORRIDOR_Y = 63;
        private const int C12_CORRIDOR_Z = 24;
        private const int C12_CORRIDOR_MIN_X = 8;
        private const int C12_CORRIDOR_MAX_X = 25;
        private const int C12_WEST_LAMP_X = 14; // chunk (0,1), 1 step from the x15 seam
        private const int C12_EAST_LAMP_X = 20; // chunk (1,1), 4 steps from the x16 seam (asymmetric)
        private const int C12_WEST_SEAM_X = 15;
        private const int C12_EAST_SEAM_X = 16;

        /// <summary>Registers the C12 RGB stale-pull-back baseline (called from <c>AddBaselineScenarios</c>).</summary>
        /// <param name="scenarios">The scenario list to append to.</param>
        static partial void AddC12RgbPullbackBaselineScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario(
                "B89: A stale RGB darkness-phase seam pull-back (held-flight snapshot bright, neighbor darkened after) converges to the oracle — the claim-free CheckEdgeVoxelRGB re-light self-heals, no sourceless ghost (Bug 14's RGB mirror; fidelity C12)",
                Baseline_StaleRgbPullBackSelfHeals));
        }

        /// <summary>Builds the C12 sealed asymmetric two-lamp corridor (returned un-lit).</summary>
        /// <returns>A freshly-built, un-lit grid-3 test world.</returns>
        private static LightingTestWorld BuildC12RgbPullBackWorld()
        {
            LightingTestWorld world = new LightingTestWorld(3);
            int width = world.GridSize * VoxelData.ChunkWidth;

            world.FillBox(new Vector3Int(0, C12_SLAB_MIN_Y, 0), new Vector3Int(width - 1, C12_SLAB_MAX_Y, width - 1),
                TestBlockPalette.Stone);
            for (int x = C12_CORRIDOR_MIN_X; x <= C12_CORRIDOR_MAX_X; x++)
                world.SetBlock(new Vector3Int(x, C12_CORRIDOR_Y, C12_CORRIDOR_Z), TestBlockPalette.Air);
            world.SetBlock(new Vector3Int(C12_WEST_LAMP_X, C12_CORRIDOR_Y, C12_CORRIDOR_Z), TestBlockPalette.LampRed);
            world.SetBlock(new Vector3Int(C12_EAST_LAMP_X, C12_CORRIDOR_Y, C12_CORRIDOR_Z), TestBlockPalette.LampRed);

            world.RecalculateHeightmaps();
            return world;
        }

        /// <summary>
        /// B89 (fidelity C12): the stale RGB pull-back self-heals. Converges the asymmetric two-lamp
        /// corridor, then removes both cross-seam sources under a held-flight interleave that forces the west
        /// chunk's darkness wave to run its seam pull-back against a snapshot in which the east neighbor is
        /// still lit (it has since gone dark): break the west lamp, <see cref="LightingTestWorld.BeginLightingJob"/>
        /// the west chunk (snapshotting the bright east border), break the east lamp and run the east chunk's
        /// job for real, then <see cref="LightingTestWorld.CompleteLightingJob"/> the stale west flight — its
        /// <c>CheckEdgeVoxelRGB</c> pull-back re-lights the seam column from the now-stale snapshot with no
        /// claim recorded. The field must still settle on the borderless oracle (fully dark): the asymmetric
        /// removal branch clears the stale re-light, so the claim-free pull-back plants no surviving ghost.
        /// See the type docstring for the prove-red (the symmetric twin is Bug 18 / K18a).
        /// </summary>
        private static bool Baseline_StaleRgbPullBackSelfHeals()
        {
            using LightingTestWorld world = BuildC12RgbPullBackWorld();

            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "B89: initial lighting converges");
            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world),
                "B89: initial asymmetric two-lamp red corridor matches the borderless oracle");

            // Precondition: both seam voxels carry red but at UNEQUAL levels (the asymmetry that keeps the
            // stale pull-back collapsible — distinguishes this from Bug 18's mutually-equal seam).
            Vector3Int westSeam = new Vector3Int(C12_WEST_SEAM_X, C12_CORRIDOR_Y, C12_CORRIDOR_Z);
            Vector3Int eastSeam = new Vector3Int(C12_EAST_SEAM_X, C12_CORRIDOR_Y, C12_CORRIDOR_Z);
            (byte R, byte G, byte B) west = world.GetBlocklightRGB(westSeam);
            (byte R, byte G, byte B) east = world.GetBlocklightRGB(eastSeam);
            passed &= LightingAssert.IsTrue(west.R > 0 && east.R > 0 && west.R != east.R,
                "B89: the two seam voxels are lit asymmetrically (unequal red) before source removal",
                $"Expected unequal non-zero red at the seams, got west R{west.R.ToString()} / east R{east.R.ToString()}");

            // Held-flight interleave: the west chunk snapshots the still-bright east border, then the east
            // source is removed for real before the west chunk's stale darkness wave runs its seam pull-back.
            world.BreakBlock(new Vector3Int(C12_WEST_LAMP_X, C12_CORRIDOR_Y, C12_CORRIDOR_Z));
            LightingTestWorld.LightingJobFlight westFlight = world.BeginLightingJob(new Vector2Int(0, 1));
            world.BreakBlock(new Vector3Int(C12_EAST_LAMP_X, C12_CORRIDOR_Y, C12_CORRIDOR_Z));
            world.RunLightingJob(new Vector2Int(1, 1)); // east darkens for real
            world.CompleteLightingJob(westFlight); // west's stale, claim-free RGB pull-back runs

            passed &= LightingAssert.Converged(world.RunWaveToConvergence(),
                "B89: post-removal reconciliation reaches a stable field");
            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world),
                "B89: corridor darkens to the borderless oracle — the stale RGB pull-back self-heals (no sourceless ghost)");

            return passed;
        }
    }
}
