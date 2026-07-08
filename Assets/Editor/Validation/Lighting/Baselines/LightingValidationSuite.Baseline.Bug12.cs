using System.Collections.Generic;
using Editor.Validation.Lighting.Framework;
using UnityEngine;
using Scenario = Editor.Validation.Framework.Scenario;

namespace Editor.Validation.Lighting
{
    /// <summary>
    /// Baseline regression scenarios for the <b>Bug 12</b> family — the over-bright cross-seam sunlight
    /// loop that survived source removal (fixed June 2026). Grouped into their own partial file (the first
    /// of the planned <c>Baselines/</c> split; <c>LightingValidationSuite.Baseline.cs</c> had outgrown a
    /// single file) and self-registered via the <see cref="AddBug12BaselineScenarios"/> hook called from
    /// <c>AddBaselineScenarios</c>, so this group owns both its definitions and its registration.
    /// <list type="bullet">
    /// <item><b>B53</b> — the promoted repro (formerly known-bug scenario K12a): roofing both shafts of a
    /// mutually-lit seam corridor must darken it to the oracle.</item>
    /// <item><b>B50</b> — over-correction tripwire: roofing only ONE of the two sky-exposed seam columns
    /// must not clear the still-sky-exposed neighbor.</item>
    /// <item><b>B51</b> / <b>B52</b> — completeness guards: asymmetric two-shaft and multi-hop ring
    /// cross-seam source removals converge correctly (they do so even pre-fix; see their docstrings).</item>
    /// </list>
    /// </summary>
    public static partial class LightingValidationSuite
    {
        // --- Shared geometry for the Bug-12 cross-seam loop family (B53 repro + B50 tripwire). A solid rock
        // slab with a 1-wide E-W corridor straddling the x15|16 chunk seam, lit ONLY by sky shafts that open
        // BOTH shared seam columns to the sky. After initial convergence the two seam voxels are mutually
        // equal at sky 15 — the precondition for a sourceless cross-seam support loop. Centralized
        // (constants + builder) so the repro and its tripwire can never drift apart on geometry. ---
        private const int SEAM_LOOP_SLAB_MIN_Y = 58; // solid rock floor/roof slab, low bound
        private const int SEAM_LOOP_SLAB_MAX_Y = 68; // solid rock floor/roof slab, high bound
        private const int SEAM_LOOP_CORRIDOR_Y = 63; // the carved 1-wide corridor (inside the slab)
        private const int SEAM_LOOP_CORRIDOR_Z = 24; // corridor runs along x at this z (chunk row cz=1)
        private const int SEAM_LOOP_CORRIDOR_MIN_X = 10; // corridor spans 6 voxels each side of the seam
        private const int SEAM_LOOP_CORRIDOR_MAX_X = 21;
        private const int SEAM_LOOP_WEST_SEAM_X = 15; // shared border column owned by chunk (0,1)
        private const int SEAM_LOOP_EAST_SEAM_X = 16; // shared border column owned by chunk (1,1)

        // --- B51/B52 geometry (Bug 12 completeness): cross-seam loops that are NOT the symmetric
        // mutually-equal case the Bug 12 fix targets. These reuse the slab/corridor Y above but place the sky
        // shafts AWAY from the seam, so the seam voxels are lit purely horizontally (asymmetrically).
        // Investigation (June 2026) confirmed BOTH converge correctly even on the pre-fix engine — the
        // over-bright stuck loop requires the symmetric mutually-equal seam (neither side has a removal
        // initiator); any gradient is broken by the existing PropagateDarkness "neighbor < removed level"
        // branch (which emits a removal via SetSunlight). So these are NOT fix tripwires (they stay green
        // with the fix neutered); they guard against a FUTURE regression in asymmetric / multi-hop cross-seam
        // source removal and pin the scope of Bug 12. ---
        private const int SEAM_LOOP_ASYM_CORRIDOR_MIN_X = 6; // wider corridor so both shafts sit off the seam
        private const int SEAM_LOOP_ASYM_CORRIDOR_MAX_X = 25;
        private const int SEAM_LOOP_ASYM_WEST_SHAFT_X = 9; // chunk (0,1): 6 voxels from the x15 seam
        private const int SEAM_LOOP_ASYM_EAST_SHAFT_X = 18; // chunk (1,1): 2 voxels from the x16 seam (closer → asymmetric)
        private const int SEAM_LOOP_RING_ZA = 24; // ring's two parallel E-W corridors
        private const int SEAM_LOOP_RING_ZB = 26;
        private const int SEAM_LOOP_RING_MIN_X = 10;
        private const int SEAM_LOOP_RING_MAX_X = 21;
        private const int SEAM_LOOP_RING_SHAFT_X = 10; // single sky shaft, at one corner of the ring

        /// <summary>Registers the Bug-12 family baseline scenarios (called from <c>AddBaselineScenarios</c>).</summary>
        /// <param name="scenarios">The scenario list to append to.</param>
        static partial void AddBug12BaselineScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario("B53: Roofing both sky shafts of a mutually-lit cross-seam corridor darkens it to the oracle — no over-bright sourceless loop (Bug 12 guard; promoted from K12a)", Baseline_CrossSeamSunlightLoopClearsOnSourceRemoval));
            scenarios.Add(new Scenario("B50: Roofing one of two sky-exposed seam columns darkens only that column — the still-sky-exposed neighbor keeps lighting both sides (Bug 12 fix over-correction tripwire)", Baseline_GenuineSeamSingleRoofKeepsSkyExposedNeighbor));
            scenarios.Add(new Scenario("B51: Roofing two ASYMMETRICALLY-lit shafts (different seam levels, neither at the seam) darkens the cross-seam corridor to the oracle — no over-bright residue (Bug 12 completeness)", Baseline_AsymmetricCrossSeamSourceRemovalConverges));
            scenarios.Add(new Scenario("B52: Roofing the only shaft of a multi-hop ring corridor that crosses the seam twice darkens the whole ring to the oracle — no stuck multi-hop cross-seam loop (Bug 12 completeness)", Baseline_RingMultiHopCrossSeamSourceRemovalConverges));
        }

        /// <summary>
        /// Builds the shared Bug-12 seam-loop world (used by B53 and the B50 tripwire): a solid rock slab
        /// with a 1-wide E-W corridor straddling the x15|16 seam, with BOTH shared seam columns opened to the
        /// sky as the only light source. Heightmaps are recalculated; the world is returned un-lit — the
        /// caller runs the initial lighting pass, applies its perturbation, and disposes the world.
        /// </summary>
        /// <returns>A freshly-built, un-lit gridSize-3 test world.</returns>
        private static LightingTestWorld BuildSeamLoopWorld()
        {
            LightingTestWorld world = new LightingTestWorld(3);
            int width = world.GridSize * VoxelData.ChunkWidth;

            world.FillBox(new Vector3Int(0, SEAM_LOOP_SLAB_MIN_Y, 0), new Vector3Int(width - 1, SEAM_LOOP_SLAB_MAX_Y, width - 1),
                TestBlockPalette.Stone);
            for (int x = SEAM_LOOP_CORRIDOR_MIN_X; x <= SEAM_LOOP_CORRIDOR_MAX_X; x++)
                world.SetBlock(new Vector3Int(x, SEAM_LOOP_CORRIDOR_Y, SEAM_LOOP_CORRIDOR_Z), TestBlockPalette.Air);
            foreach (int seamX in new[] { SEAM_LOOP_WEST_SEAM_X, SEAM_LOOP_EAST_SEAM_X })
                for (int y = SEAM_LOOP_CORRIDOR_Y; y < VoxelData.ChunkHeight; y++)
                    world.SetBlock(new Vector3Int(seamX, y, SEAM_LOOP_CORRIDOR_Z), TestBlockPalette.Air);

            world.RecalculateHeightmaps();
            return world;
        }

        /// <summary>
        /// B53 (Bug 12 guard, promoted from known-bug scenario K12a after the June 2026 fix): the canonical
        /// reproduction of the over-bright cross-seam sunlight loop. A 1-wide corridor straddles the x15|16
        /// seam, lit by a single sky shaft that opens <b>both</b> shared seam columns — after convergence the
        /// two seam voxels are mutually equal at sky 15, each appearing lit "by" the other across the
        /// boundary. Roofing both seam columns (one <see cref="LightingTestWorld.PlaceBlock"/> per chunk, so
        /// both seam chunks carry a sunlight column recalc into the <b>same</b> wave) removes the genuine
        /// source; run wave-parallel (<see cref="LightingTestWorld.RunWaveToConvergence"/>, production's
        /// concurrent-job / schedule-time-snapshot model), the corridor must darken to the borderless oracle.
        /// <para>
        /// Before the fix this settled into a <b>stable-but-wrong</b> over-bright field: neither seam voxel
        /// found a removal initiator (each re-lit from the other's stale snapshot), so the seam pinned one
        /// level below its pre-roof value and stayed bright downstream. The fix supplies the missing
        /// cross-seam removal initiator (<see cref="NeighborhoodLightingJob"/> <c>PropagateDarkness</c>),
        /// adjudicated by the Bug-11 in-chunk-support veto. A regression re-opens the over-bright loop and
        /// flips this red. (A single-side shaft + single roof, or sequential <c>RunToConvergence</c>, does
        /// NOT reproduce — the simultaneous same-wave perturbation of both seam chunks is required.)
        /// </para>
        /// </summary>
        private static bool Baseline_CrossSeamSunlightLoopClearsOnSourceRemoval()
        {
            using LightingTestWorld world = BuildSeamLoopWorld();

            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "B53: initial lighting converges");
            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world),
                "B53: initial field matches the borderless oracle");

            // Precondition: the two seam voxels are mutually equal (sky 15 each, fed from the shared shaft) —
            // each appears lit "by" the other across the boundary, the basis of the sourceless support loop.
            Vector3Int westSeam = new Vector3Int(SEAM_LOOP_WEST_SEAM_X, SEAM_LOOP_CORRIDOR_Y, SEAM_LOOP_CORRIDOR_Z);
            Vector3Int eastSeam = new Vector3Int(SEAM_LOOP_EAST_SEAM_X, SEAM_LOOP_CORRIDOR_Y, SEAM_LOOP_CORRIDOR_Z);
            passed &= LightingAssert.IsTrue(world.GetSkyLight(westSeam) == 15 && world.GetSkyLight(eastSeam) == 15,
                "B53: both seam voxels are mutually lit to sky 15 before the source is removed",
                $"Expected 15/15, got {world.GetSkyLight(westSeam)}/{world.GetSkyLight(eastSeam)}");

            // Remove the dominant cross-seam source: roof BOTH seam columns. One edit per chunk, so both
            // seam chunks carry a sunlight column recalc into the SAME wave.
            world.PlaceBlock(new Vector3Int(SEAM_LOOP_WEST_SEAM_X, SEAM_LOOP_CORRIDOR_Y + 1, SEAM_LOOP_CORRIDOR_Z), TestBlockPalette.Stone);
            world.PlaceBlock(new Vector3Int(SEAM_LOOP_EAST_SEAM_X, SEAM_LOOP_CORRIDOR_Y + 1, SEAM_LOOP_CORRIDOR_Z), TestBlockPalette.Stone);

            // Wave-parallel reconciliation (production's concurrent multi-job frame): must reach a stable
            // field AND match the oracle (the corridor goes dark — no over-bright residue).
            passed &= LightingAssert.Converged(world.RunWaveToConvergence(),
                "B53: post-removal reconciliation reaches a stable field");
            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world),
                "B53: corridor darkens to the borderless oracle after the cross-seam source is removed");

            return passed;
        }

        /// <summary>
        /// B50 (Bug 12 fix tripwire): guards that the cross-seam sunlight removal-mod emission added to fix
        /// Bug 12 (<see cref="NeighborhoodLightingJob"/> <c>PropagateDarkness</c>, adjudicated by the
        /// vertical-sky-aware veto <c>CrossChunkLightModApplier.InChunkSunlightSupport</c>) does NOT
        /// over-correct: when only ONE of two mutually-lit seam columns loses its source, the other — still
        /// directly sky-exposed — must keep its full sky and continue lighting both sides of the seam.
        /// <para>
        /// Shares B53's geometry via <see cref="BuildSeamLoopWorld"/> (a corridor straddling the x15|16
        /// seam, lit by a shaft opening both seam columns to the sky), but only the WEST seam column is
        /// roofed. The EAST seam column stays open, so the borderless oracle keeps it at sky 15 and the west
        /// seam at 14 (re-lit across the boundary), decaying outward. Before the Bug 12 fix this already
        /// converged correctly (the single edit perturbs only one chunk, so the neighbor snapshots the
        /// dropped value); the risk this guards is the fix's new removal mod wrongly clearing the sky-exposed
        /// east column to a black spot — which the vertical-sky support recognition in the veto must prevent.
        /// A regression (veto loses vertical awareness, or the removal mod fires unconditionally) darkens the
        /// east seam and flips this red.
        /// </para>
        /// </summary>
        private static bool Baseline_GenuineSeamSingleRoofKeepsSkyExposedNeighbor()
        {
            using LightingTestWorld world = BuildSeamLoopWorld();

            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "B50: initial lighting converges");
            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world),
                "B50: initial two-shaft field matches the oracle");

            // Roof ONLY the west seam column; the east seam column stays open to the sky.
            world.PlaceBlock(new Vector3Int(SEAM_LOOP_WEST_SEAM_X, SEAM_LOOP_CORRIDOR_Y + 1, SEAM_LOOP_CORRIDOR_Z), TestBlockPalette.Stone);

            passed &= LightingAssert.Converged(world.RunWaveToConvergence(), "B50: single-roof reconciliation converges");

            // The east seam stays sky-exposed (15); the west seam is re-lit from it across the boundary (14).
            Vector3Int eastSeam = new Vector3Int(SEAM_LOOP_EAST_SEAM_X, SEAM_LOOP_CORRIDOR_Y, SEAM_LOOP_CORRIDOR_Z);
            Vector3Int westSeam = new Vector3Int(SEAM_LOOP_WEST_SEAM_X, SEAM_LOOP_CORRIDOR_Y, SEAM_LOOP_CORRIDOR_Z);
            passed &= LightingAssert.IsTrue(world.GetSkyLight(eastSeam) == 15 && world.GetSkyLight(westSeam) == 14,
                "B50: the still-sky-exposed east seam keeps sky 15 and re-lights the roofed west seam to 14 (no black spot)",
                $"Expected east 15 / west 14, got east {world.GetSkyLight(eastSeam)} / west {world.GetSkyLight(westSeam)}");

            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world),
                "B50: single-roof field matches the borderless oracle (sky-exposed neighbor not cleared)");

            return passed;
        }

        /// <summary>
        /// B51 (Bug 12 completeness): a roofed corridor straddling the x15|16 seam lit by TWO sky shafts that
        /// sit off the seam at <b>different distances</b> (x9 in chunk (0,1) is 6 voxels from the seam; x18 in
        /// chunk (1,1) is 2), so the two seam voxels settle at <b>unequal</b> sky levels — unlike B53's
        /// symmetric 15/15. Roofing both shafts in the same wave removes every source; the corridor must
        /// darken to the oracle. Confirmed green even with the Bug 12 fix neutered (an asymmetric gradient
        /// always has a strictly-lower side, which the existing <c>PropagateDarkness</c> "&lt; removed level"
        /// branch removes via <see cref="NeighborhoodLightingJob"/>'s cross-chunk <c>SetSunlight</c>), so this
        /// is a general convergence guard, not a fix tripwire: it pins that Bug 12 is specific to the
        /// symmetric mutually-equal seam and catches a future regression in asymmetric cross-seam removal.
        /// </summary>
        private static bool Baseline_AsymmetricCrossSeamSourceRemovalConverges()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            int width = world.GridSize * VoxelData.ChunkWidth;

            world.FillBox(new Vector3Int(0, SEAM_LOOP_SLAB_MIN_Y, 0), new Vector3Int(width - 1, SEAM_LOOP_SLAB_MAX_Y, width - 1),
                TestBlockPalette.Stone);
            for (int x = SEAM_LOOP_ASYM_CORRIDOR_MIN_X; x <= SEAM_LOOP_ASYM_CORRIDOR_MAX_X; x++)
                world.SetBlock(new Vector3Int(x, SEAM_LOOP_CORRIDOR_Y, SEAM_LOOP_CORRIDOR_Z), TestBlockPalette.Air);
            foreach (int shaftX in new[] { SEAM_LOOP_ASYM_WEST_SHAFT_X, SEAM_LOOP_ASYM_EAST_SHAFT_X })
                for (int y = SEAM_LOOP_CORRIDOR_Y; y < VoxelData.ChunkHeight; y++)
                    world.SetBlock(new Vector3Int(shaftX, y, SEAM_LOOP_CORRIDOR_Z), TestBlockPalette.Air);

            world.RecalculateHeightmaps();

            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "B51: initial lighting converges");
            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world),
                "B51: initial asymmetric two-shaft field matches the oracle");

            // Document the asymmetry that distinguishes this from B53: the two seam voxels are unequal.
            Vector3Int westSeam = new Vector3Int(SEAM_LOOP_WEST_SEAM_X, SEAM_LOOP_CORRIDOR_Y, SEAM_LOOP_CORRIDOR_Z);
            Vector3Int eastSeam = new Vector3Int(SEAM_LOOP_EAST_SEAM_X, SEAM_LOOP_CORRIDOR_Y, SEAM_LOOP_CORRIDOR_Z);
            passed &= LightingAssert.IsTrue(
                world.GetSkyLight(westSeam) > 0 && world.GetSkyLight(eastSeam) > 0 &&
                world.GetSkyLight(westSeam) != world.GetSkyLight(eastSeam),
                "B51: the two seam voxels are lit asymmetrically (unequal) before source removal",
                $"Expected unequal non-zero seam levels, got x15={world.GetSkyLight(westSeam)} x16={world.GetSkyLight(eastSeam)}");

            // Roof BOTH shafts in the same wave: one edit per chunk, so both seam chunks carry a column recalc
            // into the SAME wave-parallel round (the in-flight stale-snapshot condition).
            world.PlaceBlock(new Vector3Int(SEAM_LOOP_ASYM_WEST_SHAFT_X, SEAM_LOOP_CORRIDOR_Y + 1, SEAM_LOOP_CORRIDOR_Z), TestBlockPalette.Stone);
            world.PlaceBlock(new Vector3Int(SEAM_LOOP_ASYM_EAST_SHAFT_X, SEAM_LOOP_CORRIDOR_Y + 1, SEAM_LOOP_CORRIDOR_Z), TestBlockPalette.Stone);

            passed &= LightingAssert.Converged(world.RunWaveToConvergence(), "B51: post-removal reconciliation converges");
            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world),
                "B51: asymmetric corridor darkens to the borderless oracle (no over-bright residue)");

            return passed;
        }

        /// <summary>
        /// B52 (Bug 12 completeness): a multi-hop loop. Two parallel E-W corridors (z24, z26) joined at both
        /// ends (x10, x21) form a rectangular ring in one y-plane that crosses the x15|16 seam TWICE, lit by a
        /// single sky shaft at one corner (x10, z24). Light circulates around the ring (a genuine multi-hop
        /// cross-seam path); roofing the only shaft must darken the whole ring to the oracle. Confirmed green
        /// even with the Bug 12 fix neutered, so — like B51 — this is a general multi-hop convergence guard,
        /// not a fix tripwire: it pins that a circulating cross-seam loop with a single removed source does
        /// not get stuck over-bright, and catches a future regression in multi-hop cross-seam removal.
        /// </summary>
        private static bool Baseline_RingMultiHopCrossSeamSourceRemovalConverges()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            int width = world.GridSize * VoxelData.ChunkWidth;

            world.FillBox(new Vector3Int(0, SEAM_LOOP_SLAB_MIN_Y, 0), new Vector3Int(width - 1, SEAM_LOOP_SLAB_MAX_Y, width - 1),
                TestBlockPalette.Stone);
            // Two parallel corridors...
            for (int x = SEAM_LOOP_RING_MIN_X; x <= SEAM_LOOP_RING_MAX_X; x++)
            {
                world.SetBlock(new Vector3Int(x, SEAM_LOOP_CORRIDOR_Y, SEAM_LOOP_RING_ZA), TestBlockPalette.Air);
                world.SetBlock(new Vector3Int(x, SEAM_LOOP_CORRIDOR_Y, SEAM_LOOP_RING_ZB), TestBlockPalette.Air);
            }

            // ...joined at both ends to close the ring.
            for (int z = SEAM_LOOP_RING_ZA; z <= SEAM_LOOP_RING_ZB; z++)
            {
                world.SetBlock(new Vector3Int(SEAM_LOOP_RING_MIN_X, SEAM_LOOP_CORRIDOR_Y, z), TestBlockPalette.Air);
                world.SetBlock(new Vector3Int(SEAM_LOOP_RING_MAX_X, SEAM_LOOP_CORRIDOR_Y, z), TestBlockPalette.Air);
            }

            // Single sky shaft at one corner — the ring's only light source.
            for (int y = SEAM_LOOP_CORRIDOR_Y; y < VoxelData.ChunkHeight; y++)
                world.SetBlock(new Vector3Int(SEAM_LOOP_RING_SHAFT_X, y, SEAM_LOOP_RING_ZA), TestBlockPalette.Air);

            world.RecalculateHeightmaps();

            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "B52: initial lighting converges");
            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world),
                "B52: initial ring field matches the oracle");

            // Precondition: light actually reaches the far seam crossing (z26 row) — the ring circulates.
            passed &= LightingAssert.IsTrue(world.GetSkyLight(new Vector3Int(SEAM_LOOP_WEST_SEAM_X, SEAM_LOOP_CORRIDOR_Y, SEAM_LOOP_RING_ZB)) > 0,
                "B52: light circulates around the ring to the far (z26) seam crossing before removal",
                $"Expected the far seam crossing lit, got {world.GetSkyLight(new Vector3Int(SEAM_LOOP_WEST_SEAM_X, SEAM_LOOP_CORRIDOR_Y, SEAM_LOOP_RING_ZB))}");

            // Roof the only shaft. The whole ring must go dark.
            world.PlaceBlock(new Vector3Int(SEAM_LOOP_RING_SHAFT_X, SEAM_LOOP_CORRIDOR_Y + 1, SEAM_LOOP_RING_ZA), TestBlockPalette.Stone);

            passed &= LightingAssert.Converged(world.RunWaveToConvergence(), "B52: post-removal reconciliation converges");
            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world),
                "B52: the multi-hop ring darkens to the borderless oracle (no stuck cross-seam loop)");

            return passed;
        }
    }
}
