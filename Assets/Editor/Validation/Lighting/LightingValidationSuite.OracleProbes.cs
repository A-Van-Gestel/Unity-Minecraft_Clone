using Editor.Validation.Lighting.Framework;
using UnityEngine;

namespace Editor.Validation.Lighting
{
    /// <summary>
    /// Oracle-independence probes (fidelity finding A4). The borderless <see cref="LightingOracle"/>
    /// is a hand-written spec that, by design, encodes the SAME rules as the engine — the column pass
    /// (15 above the heightmap, attenuate below), the Starlight attenuation
    /// <c>max(0, src - max(1, opacity))</c>, and the byte-identical <c>isVerticalSunlight</c> condition
    /// (compare <c>LightingOracle.SolveSky</c> with <c>NeighborhoodLightingJob.PropagateLight</c> /
    /// <c>RecalculateSunlightForColumn</c>). Where a rule is shared, a SHARED-WRONG assumption passes
    /// <see cref="LightingAssert.MatchesOracle"/> silently — engine and oracle agree on the same defect.
    /// <para>
    /// These scenarios therefore assert <b>hardcoded</b> light levels derived from the lighting spec by
    /// hand (block counting), NOT from <c>LightingOracle.Solve</c>. They deliberately do not call
    /// <see cref="LightingAssert.MatchesOracle"/>: each one pins a specific shared rule to a constant the
    /// oracle never produced, so a formula broken in BOTH the engine and the oracle (which
    /// <c>MatchesOracle</c> would not catch) still flips one of these red. One probe per shared rule.
    /// </para>
    /// </summary>
    public static partial class LightingValidationSuite
    {
        /// <summary>
        /// B35 (A4 probe — vertical sunlight, air): an open column over a stone floor. Sunlight falls
        /// straight down through air with NO depth attenuation, so every air voxel from the floor to the
        /// sky cap reads a full 15. Pins the column pass's "15 above the heightmap" rule and the
        /// heightmap-at-the-floor result to a hand value, independent of the oracle.
        /// </summary>
        private static bool Baseline_ProbeVerticalSunlightThroughAir()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(10, TestBlockPalette.Stone);
            world.RecalculateHeightmaps();
            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "B35: initial lighting converges");

            // Hand-derived: floor top is y=10 (Stone, the only obstruction), so the whole air column
            // above it is full-bright. Depth must not dim sunlight — y=11 and y=120 both read 15.
            passed &= LightingAssert.IsTrue(world.GetSkyLight(new Vector3Int(24, 11, 24)) == 15,
                "B35: open-air column is full-bright just above the floor",
                $"Expected sky=15 at y=11, got {world.GetSkyLight(new Vector3Int(24, 11, 24))} (vertical sunlight attenuating with depth?)");
            passed &= LightingAssert.IsTrue(world.GetSkyLight(new Vector3Int(24, 120, 24)) == 15,
                "B35: open-air column is full-bright at altitude",
                $"Expected sky=15 at y=120, got {world.GetSkyLight(new Vector3Int(24, 120, 24))}");
            return passed;
        }

        /// <summary>
        /// B36 (A4 probe — vertical transparency, the highest-risk shared rule per A4): a tall column of
        /// <see cref="TestBlockPalette.Glass"/> (opacity 0 but <i>solid</i>) over a stone floor. Because
        /// light-obstruction is keyed on opacity (<c>IsLightObstructing = opacity &gt; 0</c>) — never on
        /// solidity — the glass must neither lower the heightmap nor attenuate skylight: the floor stays
        /// full-bright through the glass. Pins that only opacity, not solidity, blocks light.
        /// </summary>
        private static bool Baseline_ProbeVerticalSunlightThroughGlass()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(10, TestBlockPalette.Stone);

            // A 30-tall transparent SOLID column. Opacity 0 → not light-obstructing → heightmap stays at
            // the floor → the column is treated exactly like air for skylight.
            world.FillBox(new Vector3Int(24, 11, 24), new Vector3Int(24, 40, 24), TestBlockPalette.Glass);
            world.RecalculateHeightmaps();
            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "B36: initial lighting converges");

            passed &= LightingAssert.IsTrue(world.GetSkyLight(new Vector3Int(24, 11, 24)) == 15,
                "B36: skylight reaches the floor through the glass column undimmed",
                $"Expected sky=15 at the bottom of the glass column, got {world.GetSkyLight(new Vector3Int(24, 11, 24))} (solid glass wrongly attenuating?)");
            passed &= LightingAssert.IsTrue(world.GetSkyLight(new Vector3Int(24, 30, 24)) == 15,
                "B36: skylight is undimmed mid glass column",
                $"Expected sky=15 mid glass column, got {world.GetSkyLight(new Vector3Int(24, 30, 24))}");
            return passed;
        }

        /// <summary>
        /// B37 (A4 probe — sky attenuation below an obstruction): a sealed 1×1 stone shaft capped by a
        /// single <see cref="TestBlockPalette.Leaves"/> block (opacity 1), open to the sky above the cap.
        /// The stone walls block all horizontal entry, so the only light source is the cap, and the
        /// column decays by exactly 1 per voxel downward. Pins the PASS-2 downward attenuation and the
        /// opacity-1 step to an exact hand-counted chain.
        /// </summary>
        private static bool Baseline_ProbeSkyAttenuationBelowCanopy()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(10, TestBlockPalette.Stone);

            // Seal a 1-wide vertical shaft at column (24,24): stone walls on all four sides, y=11..20.
            world.FillBox(new Vector3Int(23, 11, 24), new Vector3Int(23, 20, 24), TestBlockPalette.Stone);
            world.FillBox(new Vector3Int(25, 11, 24), new Vector3Int(25, 20, 24), TestBlockPalette.Stone);
            world.FillBox(new Vector3Int(24, 11, 23), new Vector3Int(24, 20, 23), TestBlockPalette.Stone);
            world.FillBox(new Vector3Int(24, 11, 25), new Vector3Int(24, 20, 25), TestBlockPalette.Stone);

            // Leaves cap at the top of the shaft; air below it down to the floor.
            world.SetBlock(new Vector3Int(24, 20, 24), TestBlockPalette.Leaves);
            world.RecalculateHeightmaps();
            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "B37: initial lighting converges");

            // Hand-derived chain: heightmap top = leaves at y=20. The cap reads 15, then attenuates by
            // the leaves' opacity (1) into y=19 = 14, and by 1 (air) per voxel below: 14,13,...,6 at y=11.
            passed &= LightingAssert.IsTrue(world.GetSkyLight(new Vector3Int(24, 19, 24)) == 14,
                "B37: one voxel below the leaves cap = 14",
                $"Expected sky=14 at y=19, got {world.GetSkyLight(new Vector3Int(24, 19, 24))}");
            passed &= LightingAssert.IsTrue(world.GetSkyLight(new Vector3Int(24, 15, 24)) == 10,
                "B37: five voxels below the leaves cap = 10",
                $"Expected sky=10 at y=15, got {world.GetSkyLight(new Vector3Int(24, 15, 24))}");
            passed &= LightingAssert.IsTrue(world.GetSkyLight(new Vector3Int(24, 11, 24)) == 6,
                "B37: floor of the sealed shaft = 6 (linear -1/voxel decay)",
                $"Expected sky=6 at y=11, got {world.GetSkyLight(new Vector3Int(24, 11, 24))} (walls leaking, or wrong attenuation step?)");
            return passed;
        }

        /// <summary>
        /// B38 (A4 probe — horizontal blocklight falloff, the air step): a single
        /// <see cref="TestBlockPalette.Torch"/> (emission 14, opacity 0) in open air. Blocklight decays by
        /// exactly 1 per voxel of air (the <c>max(1, opacity)</c> rule) on every RGB channel. Pins the
        /// air-step = 1 and per-channel symmetry to an exact hand-counted falloff.
        /// </summary>
        private static bool Baseline_ProbeHorizontalBlocklightFalloff()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(10, TestBlockPalette.Stone);
            world.SetBlock(new Vector3Int(24, 12, 24), TestBlockPalette.Torch);
            world.RecalculateHeightmaps();
            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "B38: initial lighting converges");

            // Hand-derived: torch holds its emission 14; each air voxel costs 1 (Manhattan distance):
            // x=24 → 14, x=25 → 13, x=28 (4 away) → 10. White torch → all three channels equal.
            passed &= LightingAssert.IsTrue(world.GetBlocklightRGB(new Vector3Int(24, 12, 24)) == (14, 14, 14),
                "B38: torch voxel holds its emission",
                $"Expected (14,14,14), got {world.GetBlocklightRGB(new Vector3Int(24, 12, 24))}");
            passed &= LightingAssert.IsTrue(world.GetBlocklightRGB(new Vector3Int(25, 12, 24)) == (13, 13, 13),
                "B38: one air voxel from the torch = 13",
                $"Expected (13,13,13) at x=25, got {world.GetBlocklightRGB(new Vector3Int(25, 12, 24))}");
            passed &= LightingAssert.IsTrue(world.GetBlocklightRGB(new Vector3Int(28, 12, 24)) == (10, 10, 10),
                "B38: four air voxels from the torch = 10 (-1 per voxel, all channels)",
                $"Expected (10,10,10) at x=28, got {world.GetBlocklightRGB(new Vector3Int(28, 12, 24))}");
            return passed;
        }

        /// <summary>
        /// B39 (A4 probe — opaque receive-but-don't-propagate, exact values): a solid 5×5×5 stone cube with
        /// a <see cref="TestBlockPalette.Torch"/> against one face. The face voxel receives a surface stamp
        /// of exactly source−1 (=13) but never re-propagates it inward, so the fully-enclosed center stays
        /// pitch black. Tighter than B9 (which only asserts the interior is dark): this pins the surface
        /// stamp's exact magnitude as well.
        /// </summary>
        private static bool Baseline_ProbeOpaqueSurfaceStamp()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(10, TestBlockPalette.Stone);

            // Solid stone cube (24..28, 11..15, 24..28); torch against its west face at (23,13,26).
            world.FillBox(new Vector3Int(24, 11, 24), new Vector3Int(28, 15, 28), TestBlockPalette.Stone);
            world.SetBlock(new Vector3Int(23, 13, 26), TestBlockPalette.Torch);
            world.RecalculateHeightmaps();
            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "B39: initial lighting converges");

            // Hand-derived: the lit face voxel (24,13,26) is opaque and adjacent to the torch (14), so it
            // receives a surface stamp of 14-1 = 13 — and, being opaque, propagates none of it onward.
            passed &= LightingAssert.IsTrue(world.GetBlocklightRGB(new Vector3Int(24, 13, 26)).R == 13,
                "B39: opaque face receives surface light = source-1",
                $"Expected R=13 on the lit face, got {world.GetBlocklightRGB(new Vector3Int(24, 13, 26))}");
            // The enclosed center is sealed by stone on all six sides — opaque blocks never propagate,
            // so no light can reach it.
            passed &= LightingAssert.IsTrue(world.GetBlocklightRGB(new Vector3Int(26, 13, 26)) == (0, 0, 0),
                "B39: enclosed cube center stays pitch black",
                $"Expected (0,0,0) at the cube center, got {world.GetBlocklightRGB(new Vector3Int(26, 13, 26))} (opaque wrongly propagating inward?)");
            return passed;
        }

        /// <summary>
        /// B45 (A4 probe — cumulative multi-layer attenuation, fidelity finding C5): every other attenuation
        /// probe pins a SINGLE obstruction (B2 one DimGlass pane, B37 one leaves cap), so a shared engine+oracle
        /// bug in how per-step attenuation COMPOSES across layers passes <see cref="LightingAssert.MatchesOracle"/>
        /// silently. This probe forces light through stacked opacity-5 <see cref="TestBlockPalette.DimGlass"/>
        /// layers in series — on both light types — and asserts the cumulative result against hand-counted
        /// constants (no oracle call). Opacity 5 (not the leaves' opacity 1) is what makes each layer
        /// distinguishable from the −1 air step, so each layer's contribution is observable.
        /// <para>
        /// Attenuation is charged on ENTERING a voxel (the destination's opacity), reducing to
        /// <c>max(0, src − max(1, opacity))</c> — confirmed against <c>NeighborhoodLightingJob.PropagateLight</c>.
        /// For the vertical column the sky-exposed top block reads 15 "for free" (it is the heightmap surface),
        /// so a 3-block DimGlass cap is needed to charge two cumulative −5 steps (10 → 5); the horizontal case
        /// charges both panes because the lamp source is not free (15 → 10 → 5).
        /// </para>
        /// </summary>
        private static bool Baseline_ProbeCumulativeMultiLayerAttenuation()
        {
            bool passed = true;

            // --- Part A: vertical SKYLIGHT through two stacked DimGlass layers (column-descent attenuation) ---
            using (LightingTestWorld world = new LightingTestWorld(3))
            {
                world.FillSuperflatFloor(10, TestBlockPalette.Stone);

                // Seal a 1-wide vertical shaft at column (24,24): stone walls on all four sides, y=11..20,
                // so the ONLY light entry is straight down through the cap (cf. B37).
                world.FillBox(new Vector3Int(23, 11, 24), new Vector3Int(23, 20, 24), TestBlockPalette.Stone);
                world.FillBox(new Vector3Int(25, 11, 24), new Vector3Int(25, 20, 24), TestBlockPalette.Stone);
                world.FillBox(new Vector3Int(24, 11, 23), new Vector3Int(24, 20, 23), TestBlockPalette.Stone);
                world.FillBox(new Vector3Int(24, 11, 25), new Vector3Int(24, 20, 25), TestBlockPalette.Stone);

                // Three-block DimGlass cap (opacity 5 each) at the top of the shaft; air below to the floor.
                // Attenuation is charged on ENTERING a voxel (destination opacity) and the sky-exposed top
                // block reads 15 "for free" (it is the heightmap surface), so two STACKED layers would charge
                // only once — a third layer is needed to observe two cumulative −5 steps.
                world.SetBlock(new Vector3Int(24, 20, 24), TestBlockPalette.DimGlass);
                world.SetBlock(new Vector3Int(24, 19, 24), TestBlockPalette.DimGlass);
                world.SetBlock(new Vector3Int(24, 18, 24), TestBlockPalette.DimGlass);
                world.RecalculateHeightmaps();
                passed &= LightingAssert.Converged(world.RunInitialLighting(), "B45A: initial lighting converges");

                // Hand-derived (light enters each voxel attenuated by the DESTINATION's opacity; the cap is the
                // free sky surface). heightmap top = top DimGlass at y=20 → reads 15.
                //   y=19 = 15 − opacity(entering DimGlass = 5) = 10
                //   y=18 = 10 − opacity(entering DimGlass = 5) =  5   ← two layers charged cumulatively (one layer → 10)
                //   y=17 =  5 − opacity(entering air = 1)      =  4
                passed &= LightingAssert.IsTrue(world.GetSkyLight(new Vector3Int(24, 20, 24)) == 15,
                    "B45A: DimGlass cap at the heightmap reads full sky",
                    $"Expected sky=15 at the cap, got {world.GetSkyLight(new Vector3Int(24, 20, 24))}");
                passed &= LightingAssert.IsTrue(world.GetSkyLight(new Vector3Int(24, 19, 24)) == 10,
                    "B45A: entering one DimGlass layer attenuates the column by 5",
                    $"Expected sky=10 below the first charged layer, got {world.GetSkyLight(new Vector3Int(24, 19, 24))}");
                passed &= LightingAssert.IsTrue(world.GetSkyLight(new Vector3Int(24, 18, 24)) == 5,
                    "B45A: entering two DimGlass layers attenuates the column by 10 cumulatively",
                    $"Expected sky=5 below the second charged layer, got {world.GetSkyLight(new Vector3Int(24, 18, 24))} (per-layer attenuation not composing?)");
                passed &= LightingAssert.IsTrue(world.GetSkyLight(new Vector3Int(24, 17, 24)) == 4,
                    "B45A: air below the cap resumes the -1/voxel step",
                    $"Expected sky=4 one voxel below the foliage, got {world.GetSkyLight(new Vector3Int(24, 17, 24))}");
            }

            // --- Part B: horizontal BLOCKLIGHT through two DimGlass panes (BFS attenuation), all channels ---
            using (LightingTestWorld world = new LightingTestWorld(3))
            {
                world.FillSuperflatFloor(10, TestBlockPalette.Stone);

                // A solid stone block with a 1-voxel tunnel carved straight through it, so the ONLY blocklight
                // path is east through the two panes (no go-around path to pollute the hand-derived chain).
                world.FillBox(new Vector3Int(19, 11, 23), new Vector3Int(23, 13, 25), TestBlockPalette.Stone);
                world.SetBlock(new Vector3Int(20, 12, 24), TestBlockPalette.LampWhite); // emission 15
                world.SetBlock(new Vector3Int(21, 12, 24), TestBlockPalette.DimGlass);
                world.SetBlock(new Vector3Int(22, 12, 24), TestBlockPalette.DimGlass);
                world.SetBlock(new Vector3Int(23, 12, 24), TestBlockPalette.Air);
                world.RecalculateHeightmaps();
                passed &= LightingAssert.Converged(world.RunInitialLighting(), "B45B: initial lighting converges");

                // Hand-derived (BFS attenuates by the DESTINATION voxel's opacity); white lamp → all channels equal:
                //   (21) DimGlass = 15 − 5 = 10
                //   (22) DimGlass = 10 − 5 = 5    ← second layer composes (single pane from a lamp would be 10)
                //   (23) air      =  5 − 1 = 4
                passed &= LightingAssert.IsTrue(world.GetBlocklightRGB(new Vector3Int(21, 12, 24)) == (10, 10, 10),
                    "B45B: one DimGlass pane attenuates blocklight by 5 on all channels",
                    $"Expected (10,10,10) inside the first pane, got {world.GetBlocklightRGB(new Vector3Int(21, 12, 24))}");
                passed &= LightingAssert.IsTrue(world.GetBlocklightRGB(new Vector3Int(22, 12, 24)) == (5, 5, 5),
                    "B45B: two DimGlass panes attenuate blocklight by 10 cumulatively",
                    $"Expected (5,5,5) inside the second pane, got {world.GetBlocklightRGB(new Vector3Int(22, 12, 24))} (per-layer attenuation not composing?)");
                passed &= LightingAssert.IsTrue(world.GetBlocklightRGB(new Vector3Int(23, 12, 24)) == (4, 4, 4),
                    "B45B: air beyond the panes resumes the -1/voxel step",
                    $"Expected (4,4,4) beyond the second pane, got {world.GetBlocklightRGB(new Vector3Int(23, 12, 24))}");
            }

            return passed;
        }
    }
}
