using System.Collections.Generic;
using Editor.Validation.Lighting.Framework;
using UnityEngine;
using Scenario = Editor.Validation.Framework.Scenario;

namespace Editor.Validation.Lighting
{
    /// <summary>
    /// Baseline <b>B94</b> — fidelity finding <b>C13</b> (the mixed-channel extension of the Bug-18 RGB
    /// cross-seam removal initiator, review Finding 1). B90 exercises the initiator with <b>equal-color</b>
    /// (red-only) lamps, so the green/blue channels are always 0 and can never be spuriously cleared. This
    /// scenario adds a SECOND channel: the same red 2-cycle straddling the x15|16 seam, PLUS a green gradient
    /// fed across that seam from a single chunk (an <i>asymmetric</i> source, NOT a loop). When both red
    /// lamps break in the same wave, <c>EmitCrossChunkBlocklightRemoval</c> emits an <b>all-channel</b>
    /// removal mod for the seam neighbor; the green channel that mod also zeroes is backed only by the
    /// <i>emitting</i> chunk, which <c>CrossChunkBlocklightSupport</c> excludes from the Bug-17 veto — so the
    /// veto cannot protect it and the green is momentarily cleared. This baseline proves the field
    /// nonetheless reconverges to the borderless oracle: the still-present green source re-lights the seam
    /// voxel across the boundary, so the over-clear is transient, not a persistent dark seam. A regression
    /// that broke the re-light (or an initiator that stopped adjudicating per channel) reds this baseline.
    /// <para>
    /// Prove-red history: authored to test whether the all-channel mod is a real persistent bug or
    /// self-heals. The green channel IS observably cleared mid-reconciliation (the precondition assert
    /// confirms the mixed-channel setup arms), and the converged field is compared against the full RGB
    /// oracle — so a genuine persistent over-removal would fail here, not pass vacuously.
    /// </para>
    /// </summary>
    public static partial class LightingValidationSuite
    {
        // --- Shared geometry: B90's sealed cross-seam corridor (red 2-cycle), extended with a green lamp one
        // step into chunk (0,1) off the WEST seam voxel, so green enters the corridor at the seam and flows
        // EAST across the boundary as a strictly-decreasing gradient (asymmetric — not a mutual loop). The
        // red lamps flank the seam on the X axis and are opaque, so the green cannot arrive along X; it is
        // injected on the Z axis at the seam column instead. ---
        private const int C13_SLAB_MIN_Y = 58;
        private const int C13_SLAB_MAX_Y = 68;
        private const int C13_CORRIDOR_Y = 63;
        private const int C13_CORRIDOR_Z = 24; // corridor runs along x at this z (chunk row cz=1)
        private const int C13_CORRIDOR_MIN_X = 10;
        private const int C13_CORRIDOR_MAX_X = 21;
        private const int C13_WEST_SEAM_X = 15; // shared border column owned by chunk (0,1)
        private const int C13_EAST_SEAM_X = 16; // shared border column owned by chunk (1,1)
        private const int C13_WEST_LAMP_X = 14; // red lamp one step west of the seam (chunk (0,1))
        private const int C13_EAST_LAMP_X = 17; // red lamp one step east of the seam (chunk (1,1))
        private const int C13_GREEN_LAMP_Z = 23; // green lamp one step −Z from the west seam voxel (chunk (0,1))

        /// <summary>Registers the C13 mixed-channel cross-seam removal baseline B94 (called from <c>AddBaselineScenarios</c>).</summary>
        /// <param name="scenarios">The scenario list to append to.</param>
        static partial void AddC13RgbMixedChannelScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario(
                "B94: A red cross-seam 2-cycle broken in the same wave, with an independent green gradient fed across the seam from one chunk, reconverges to the oracle — the all-channel Bug-18 removal mod does not persistently over-clear the green (fidelity C13; Bug-18 mixed-channel guard)",
                Baseline_MixedChannelSeamRemovalPreservesGreen));
        }

        /// <summary>
        /// Builds the B94 world: B90's two equal-color red lamps mutually lighting the sealed x15|16 seam
        /// corridor, plus a green lamp placed one step −Z from the WEST seam voxel (inside chunk (0,1)). The
        /// green lamp lights the west seam voxel, whose green then flows east across the seam into chunk
        /// (1,1) as an attenuating gradient — the east seam voxel's green is sourced entirely from across the
        /// boundary. The world is returned un-lit; the caller runs the initial pass.
        /// </summary>
        /// <returns>A freshly-built, un-lit grid-3 test world.</returns>
        private static LightingTestWorld BuildC13MixedSeamWorld()
        {
            LightingTestWorld world = new LightingTestWorld(3);
            int width = world.GridSize * VoxelData.ChunkWidth;

            world.FillBox(new Vector3Int(0, C13_SLAB_MIN_Y, 0), new Vector3Int(width - 1, C13_SLAB_MAX_Y, width - 1),
                TestBlockPalette.Stone);
            for (int x = C13_CORRIDOR_MIN_X; x <= C13_CORRIDOR_MAX_X; x++)
                world.SetBlock(new Vector3Int(x, C13_CORRIDOR_Y, C13_CORRIDOR_Z), TestBlockPalette.Air);

            // Red 2-cycle: a red lamp one step from each seam column (B90's mutually-equal precondition).
            world.SetBlock(new Vector3Int(C13_WEST_LAMP_X, C13_CORRIDOR_Y, C13_CORRIDOR_Z), TestBlockPalette.LampRed);
            world.SetBlock(new Vector3Int(C13_EAST_LAMP_X, C13_CORRIDOR_Y, C13_CORRIDOR_Z), TestBlockPalette.LampRed);

            // Independent GREEN source in chunk (0,1), off the west seam column on the −Z face — so green
            // enters at the seam and crosses into chunk (1,1) as a one-sided gradient (its only path east is
            // through the seam; the flanking red lamps are opaque).
            world.SetBlock(new Vector3Int(C13_WEST_SEAM_X, C13_CORRIDOR_Y, C13_GREEN_LAMP_Z), TestBlockPalette.LampGreen);

            world.RecalculateHeightmaps();
            return world;
        }

        /// <summary>
        /// B94 (fidelity C13, Bug-18 mixed-channel guard): a red cross-seam 2-cycle and an independent green
        /// gradient share the seam. Breaking both red lamps in the same wave fires the all-channel
        /// <c>EmitCrossChunkBlocklightRemoval</c> initiator; the mod also zeroes the seam neighbor's green,
        /// whose only support is the excluded emitting chunk — yet the field must reconverge to the borderless
        /// oracle, the green re-lit across the seam from its still-present source. Fails if the green is left
        /// persistently dark (a genuine over-removal) or if the red loop fails to collapse.
        /// </summary>
        private static bool Baseline_MixedChannelSeamRemovalPreservesGreen()
        {
            using LightingTestWorld world = BuildC13MixedSeamWorld();

            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "B94: initial lighting converges");
            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world),
                "B94: initial mixed red-loop + green-gradient corridor matches the borderless oracle");

            // Precondition: the mixed-channel setup is armed — both seam voxels carry equal red (the loop) AND
            // the east seam voxel carries a strictly-lower green sourced across the seam (the gradient).
            Vector3Int westSeam = new Vector3Int(C13_WEST_SEAM_X, C13_CORRIDOR_Y, C13_CORRIDOR_Z);
            Vector3Int eastSeam = new Vector3Int(C13_EAST_SEAM_X, C13_CORRIDOR_Y, C13_CORRIDOR_Z);
            (byte R, byte G, byte B) west = world.GetBlocklightRGB(westSeam);
            (byte R, byte G, byte B) east = world.GetBlocklightRGB(eastSeam);
            passed &= LightingAssert.IsTrue(west.R > 0 && east.R > 0 && west.R == east.R,
                "B94: both seam voxels are mutually lit to the same red level (the 2-cycle precondition)",
                $"expected equal non-zero red, got west R{west.R.ToString()} / east R{east.R.ToString()}");
            passed &= LightingAssert.IsTrue(east.G > 0 && east.G < west.G,
                "B94: the east seam voxel carries a strictly-lower green fed across the seam (the independent gradient)",
                $"expected 0 < east.G < west.G, got west G{west.G.ToString()} / east G{east.G.ToString()}");

            // Break BOTH red sources in the same wave (one edit per chunk → both seam chunks carry a blocklight
            // removal into the SAME wave-parallel round against a stale snapshot). The green lamp is untouched.
            world.BreakBlock(new Vector3Int(C13_WEST_LAMP_X, C13_CORRIDOR_Y, C13_CORRIDOR_Z));
            world.BreakBlock(new Vector3Int(C13_EAST_LAMP_X, C13_CORRIDOR_Y, C13_CORRIDOR_Z));

            passed &= LightingAssert.Converged(world.RunWaveToConvergence(),
                "B94: post-removal reconciliation reaches a stable field");
            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world),
                "B94: red collapses and the cross-seam green survives — the converged field matches the borderless oracle");

            // Sharp assert: the east seam voxel's green (sourced across the seam from the still-present lamp)
            // is NOT left dark by the all-channel removal mod.
            (byte R, byte G, byte B) eastAfter = world.GetBlocklightRGB(eastSeam);
            passed &= LightingAssert.IsTrue(eastAfter.R == 0 && eastAfter.G > 0,
                "B94: after the break the east seam voxel is red-dark but keeps its cross-seam green",
                $"expected R0 and G>0, got R{eastAfter.R.ToString()} / G{eastAfter.G.ToString()}");

            return passed;
        }
    }
}
