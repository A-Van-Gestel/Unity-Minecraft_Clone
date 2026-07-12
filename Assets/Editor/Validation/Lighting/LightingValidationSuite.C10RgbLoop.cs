using System.Collections.Generic;
using Editor.Validation.Lighting.Framework;
using UnityEngine;
using Scenario = Editor.Validation.Framework.Scenario;

namespace Editor.Validation.Lighting
{
    /// <summary>
    /// Baseline <b>B90</b> — fidelity finding <b>C10</b> / <b>Bug 18</b> (the RGB twin of the Bug 12
    /// sourceless cross-seam loop), promoted from known-bug repro K18a after the July 2026 initiator fix.
    /// Authored test-first, it reproduced red: RGB blocklight had the independent-support removal <b>veto</b>
    /// (<see cref="CrossChunkLightModApplier.ComputeBlocklight"/> per channel, Bug 17) but <b>no
    /// initiator</b> — two <b>equal-color</b> lamps feeding each other's shared seam columns, both broken in
    /// the same wave, left a stable-but-wrong over-bright red residue with no collapse path, and the Bug 17
    /// veto actively <i>protected</i> the stale mutual support (B53's RGB twin, filed Bug 18).
    /// <para>
    /// <b>Fixed</b> by mirroring the Bug 12 cross-seam removal initiator to RGB per channel
    /// (<c>NeighborhoodLightingJob.EmitCrossChunkBlocklightRemoval</c>, emitted from
    /// <c>PropagateDarknessRGB</c> at the 2-cycle signature), adjudicated by the existing Bug 17 veto. This
    /// baseline is the B53 geometry translated to blocklight (a sealed corridor straddling the x15|16 seam,
    /// equal-color lamps as the ONLY sources, simultaneous same-wave break via the wave-parallel model, full
    /// oracle compare) and must STAY green. Prove-red: neuter the new emit and only this baseline reds
    /// (confirmed 2026-07-12). The over-correction tripwires B86–B88 (Bug 16/17) and the sky Bug 12 family
    /// B50–B53 guard the opposite failure — a fix that over-clears legitimate cross-seam light.
    /// </para>
    /// </summary>
    public static partial class LightingValidationSuite
    {
        // --- Shared geometry (B53's sealed-corridor structure, but blocklight-only: NO sky opening, so two
        // equal-color lamps are the corridor's only light). A solid rock slab with a 1-wide E-W corridor
        // straddling the x15|16 seam; a red lamp sits one step from each shared seam column (x14 in chunk
        // (0,1), x17 in chunk (1,1)), so both seam voxels settle at the SAME red level and each appears lit
        // "by" the other across the boundary — the mutually-equal precondition for a sourceless cross-seam
        // support loop (the RGB mirror of B53's 15/15 sky seam). ---
        private const int C10_SLAB_MIN_Y = 58; // solid rock slab, low bound (fully encloses the corridor)
        private const int C10_SLAB_MAX_Y = 68; // solid rock slab, high bound
        private const int C10_CORRIDOR_Y = 63; // the carved 1-wide corridor (inside the slab)
        private const int C10_CORRIDOR_Z = 24; // corridor runs along x at this z (chunk row cz=1)
        private const int C10_CORRIDOR_MIN_X = 10; // corridor spans several voxels each side of the seam
        private const int C10_CORRIDOR_MAX_X = 21;
        private const int C10_WEST_SEAM_X = 15; // shared border column owned by chunk (0,1)
        private const int C10_EAST_SEAM_X = 16; // shared border column owned by chunk (1,1)
        private const int C10_WEST_LAMP_X = 14; // red lamp one step west of the seam (chunk (0,1))
        private const int C10_EAST_LAMP_X = 17; // red lamp one step east of the seam (chunk (1,1))

        /// <summary>Registers the Bug-18 RGB sourceless-loop baseline B90 (called from <c>AddBaselineScenarios</c>).</summary>
        /// <param name="scenarios">The scenario list to append to.</param>
        static partial void AddBug18RgbLoopBaselineScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario(
                "B90: Breaking two EQUAL-color (red) lamps that mutually light a sealed cross-seam corridor in the same wave darkens it to the oracle — no sourceless RGB support loop (Bug 18 guard; promoted from K18a; Bug 12's RGB twin)",
                Baseline_RgbSeamLoopClearsOnEqualLampRemoval));
        }

        /// <summary>
        /// Builds the shared B90 world: a solid rock slab with a 1-wide corridor straddling the x15|16 seam,
        /// lit ONLY by two equal-color red lamps placed symmetrically one step from each shared seam column.
        /// Heightmaps are recalculated; the world is returned un-lit — the caller runs the initial lighting
        /// pass, applies its perturbation, and disposes the world.
        /// </summary>
        /// <returns>A freshly-built, un-lit grid-3 test world.</returns>
        private static LightingTestWorld BuildC10SealedSeamLampWorld()
        {
            LightingTestWorld world = new LightingTestWorld(3);
            int width = world.GridSize * VoxelData.ChunkWidth;

            world.FillBox(new Vector3Int(0, C10_SLAB_MIN_Y, 0), new Vector3Int(width - 1, C10_SLAB_MAX_Y, width - 1),
                TestBlockPalette.Stone);
            for (int x = C10_CORRIDOR_MIN_X; x <= C10_CORRIDOR_MAX_X; x++)
                world.SetBlock(new Vector3Int(x, C10_CORRIDOR_Y, C10_CORRIDOR_Z), TestBlockPalette.Air);

            // Equal-color sources: a red lamp one step from each seam column, so both seam voxels reach the
            // same red level (the symmetric mutually-equal case Bug 12/18 targets — asymmetric gradients
            // always have a strictly-lower side the existing removal branch handles).
            world.SetBlock(new Vector3Int(C10_WEST_LAMP_X, C10_CORRIDOR_Y, C10_CORRIDOR_Z), TestBlockPalette.LampRed);
            world.SetBlock(new Vector3Int(C10_EAST_LAMP_X, C10_CORRIDOR_Y, C10_CORRIDOR_Z), TestBlockPalette.LampRed);

            world.RecalculateHeightmaps();
            return world;
        }

        /// <summary>
        /// B90 (Bug 18 guard, promoted from known-bug repro K18a after the July 2026 initiator fix): the
        /// sourceless RGB seam loop clears on equal-color source removal. A sealed corridor straddles the
        /// x15|16 seam, lit only by two equal-color (red) lamps one step from each shared seam column — after
        /// convergence the two seam voxels carry the same red level, each appearing lit "by" the other across
        /// the boundary. Breaking BOTH lamps in the same wave (one edit per chunk, so both seam chunks carry a
        /// blocklight removal into the SAME wave-parallel round against a schedule-time snapshot that still
        /// shows the other side lit) removes every genuine source; run wave-parallel, the corridor must darken
        /// to the borderless oracle.
        /// <para>
        /// Before the fix this settled STABLE-BUT-WRONG: with an RGB removal veto but no cross-seam removal
        /// initiator, neither seam voxel found a node to start removal from (each re-lit from the other's
        /// stale snapshot) and the Bug 17 veto protected the mutual support (~38-voxel over-bright red
        /// residue, worst R13 at the seam). The fix supplies the missing initiator
        /// (<c>NeighborhoodLightingJob.EmitCrossChunkBlocklightRemoval</c>); a regression re-opens the loop and
        /// flips this red.
        /// </para>
        /// </summary>
        private static bool Baseline_RgbSeamLoopClearsOnEqualLampRemoval()
        {
            using LightingTestWorld world = BuildC10SealedSeamLampWorld();

            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "B90: initial lighting converges");
            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world),
                "B90: initial two-lamp red corridor matches the borderless oracle");

            // Precondition: both seam voxels carry red and are mutually equal — the sourceless-loop surface.
            Vector3Int westSeam = new Vector3Int(C10_WEST_SEAM_X, C10_CORRIDOR_Y, C10_CORRIDOR_Z);
            Vector3Int eastSeam = new Vector3Int(C10_EAST_SEAM_X, C10_CORRIDOR_Y, C10_CORRIDOR_Z);
            (byte R, byte G, byte B) west = world.GetBlocklightRGB(westSeam);
            (byte R, byte G, byte B) east = world.GetBlocklightRGB(eastSeam);
            passed &= LightingAssert.IsTrue(west.R > 0 && east.R > 0 && west.R == east.R,
                "B90: both seam voxels are mutually lit to the same red level before the sources are removed",
                $"Expected equal non-zero red at both seams, got west R{west.R.ToString()} / east R{east.R.ToString()}");

            // Remove BOTH equal-color sources in the same wave: one edit per chunk, so both seam chunks carry
            // a blocklight removal into the SAME wave-parallel round (the in-flight stale-snapshot condition).
            world.BreakBlock(new Vector3Int(C10_WEST_LAMP_X, C10_CORRIDOR_Y, C10_CORRIDOR_Z));
            world.BreakBlock(new Vector3Int(C10_EAST_LAMP_X, C10_CORRIDOR_Y, C10_CORRIDOR_Z));

            passed &= LightingAssert.Converged(world.RunWaveToConvergence(),
                "B90: post-removal reconciliation reaches a stable field");
            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world),
                "B90: corridor darkens to the borderless oracle after both equal-color sources are removed (no sourceless RGB loop)");

            return passed;
        }
    }
}
