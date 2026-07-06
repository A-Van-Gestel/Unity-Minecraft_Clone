using System.Collections.Generic;
using Editor.Validation.Lighting.Framework;
using UnityEngine;

namespace Editor.Validation.Lighting
{
    /// <summary>
    /// Baseline regression scenarios: lighting behavior that works correctly today and must keep
    /// working while the documented bugs are being fixed. Every scenario converges the engine and
    /// (where applicable) compares the full light field against the borderless
    /// <see cref="LightingOracle"/> solution.
    /// </summary>
    public static partial class LightingValidationSuite
    {
        static partial void AddBaselineScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario("B1: Torch in open air propagates and matches oracle", Baseline_TorchInOpenAir));
            scenarios.Add(new Scenario("B2: Opaque wall blocks, semi-transparent attenuates (RGB lamp)", Baseline_WallAndSemiTransparent));
            scenarios.Add(new Scenario("B3: Cross-chunk roof shadow + light shaft after roof break", Baseline_RoofShadowAndShaft));
            scenarios.Add(new Scenario("B4: Place-then-break lamp in sealed box returns to baseline", Baseline_SealedBoxPlaceBreak));
            scenarios.Add(new Scenario("B5: Emissive spill across border into dark neighbor", Baseline_CrossChunkSpill));
            scenarios.Add(new Scenario("B6: Sealed cavity is pitch black", Baseline_SealedCavity));
            scenarios.Add(new Scenario("B7: Blocklight removal survives an in-flight neighbor job (race)", Baseline_InFlightBlocklightRemovalRace));
            scenarios.Add(new Scenario("B8: Wave-parallel initial lighting of diagonal sky-well converges to oracle", Baseline_DiagonalSkyWellParallelGen));
            scenarios.Add(new Scenario("B9: No blocklight propagation from inside opaque volumes", Baseline_OpaqueVolumeLightContainment));
            scenarios.Add(new Scenario("B10: Two emissive sources blend across the chunk border", Baseline_CrossBorderTwoSourceBlend));
            scenarios.Add(new Scenario("B11: Adjacent opposing lamps at the border converge without flicker", Baseline_AdjacentBorderLampsNoFlicker));
            scenarios.Add(new Scenario("B12: Broken source's area is re-lit by the cross-border independent source", Baseline_CrossBorderRespread));
            scenarios.Add(new Scenario("B13: Sunlight uplift mods survive an in-flight neighbor job (race)", Baseline_InFlightSunlightUpliftRace));
            scenarios.Add(new Scenario("B14: Generated emissive block illuminates surroundings on initial lighting", Baseline_GeneratedEmissiveSeedsBfs));
            // --- Bug-09 cross-chunk fleet (consolidated 2026-06-14; see LIGHTING_VALIDATION_HARNESS_FIDELITY.md §5).
            // The former eleven single-instance permutations (old B15–B25) folded into two deterministic
            // representatives — B15 (direct-harness break+place race, single- then both-in-flight) and B16
            // (fluid break→water→place under a held flight + budget). Every mechanism they exercised is still
            // covered: ContainsKey accumulate / budget / shuffled order by B26–B29, dual-chunk both-in-flight by
            // B28, the cross-chunk/corner geometry axis by B40. B17–B21 and B23–B25 are intentionally retired
            // (unused numbers, kept stable so existing references and commit history stay valid). ---
            scenarios.Add(new Scenario("B15: Direct-harness break+place race — single- then both-chunk in-flight — converges to oracle (Bug 09 guard)", Baseline_DirectHarnessBreakPlaceInFlightRace));
            scenarios.Add(new Scenario("B16: Fluid break→water→place under a held flight + single-slot budget converges to oracle (Bug 09 guard)", Baseline_FluidBreakPlaceHeldFlightRace));
            scenarios.Add(new Scenario("B22: Dual-chunk held flights with interleaved completion converges (Bug 09 guard)", Baseline_MultiFrameDualChunkInterleaved));
            scenarios.Add(new Scenario("B26: Shuffled completion+scheduling with fluid contention converges across 50 seeds (Bug 09 guard)", Baseline_ShuffledFluidContention));
            scenarios.Add(new Scenario("B27: Shuffled scheduling under budget pressure converges across 50 seeds (Bug 09 guard)", Baseline_ShuffledBudgetPressure));
            scenarios.Add(new Scenario("B28: Shuffled dual-chunk interleaved flights converge across 50 seeds (Bug 09 guard)", Baseline_ShuffledDualChunkInterleaved));
            scenarios.Add(new Scenario("B29: Combined stress — all harness layers simultaneously — converges across 50 seeds (Bug 09 guard)", Baseline_CombinedStress));
            scenarios.Add(new Scenario("B30: Cross-chunk blocklight toward an unloaded neighbor persists and replays on load (Bug 08 path-1)", Baseline_PersistReplayCrossChunkBlocklight));
            scenarios.Add(new Scenario("B31: Deferred mods degrade to the pending store when the emitting chunk unloads mid-flight, then replay on load", Baseline_DegradeDeferredOnEmittingChunkUnload));
            scenarios.Add(new Scenario("B32: Freshly-generated chunk discards persisted blocklight and re-derives the spill from its loaded neighbor", Baseline_GeneratedChunkDiscardsPendingBlocklight));
            scenarios.Add(new Scenario("B33: Pool-recycled chunks (real ChunkData.Reset()) re-light to the same field — no stale state", Baseline_PoolRecycleResetsChunkState));
            scenarios.Add(new Scenario("B34: ChunkData.Reset() clears every transient flag/counter/queue on recycle (RemainingEdgeCheckRounds guard)", Baseline_ChunkDataResetClearsTransientState));

            // --- A4 oracle-independence probes (hand-derived constants, NO MatchesOracle) ---
            scenarios.Add(new Scenario("B35: Vertical sunlight reaches the floor through air at full 15 (oracle-independent probe)", Baseline_ProbeVerticalSunlightThroughAir));
            scenarios.Add(new Scenario("B36: Vertical sunlight passes a solid glass column undimmed — opacity, not solidity, blocks light (oracle-independent probe)", Baseline_ProbeVerticalSunlightThroughGlass));
            scenarios.Add(new Scenario("B37: Skylight decays -1/voxel below a leaves cap in a sealed shaft (oracle-independent probe)", Baseline_ProbeSkyAttenuationBelowCanopy));
            scenarios.Add(new Scenario("B38: Horizontal blocklight falls off -1 per air voxel on all channels (oracle-independent probe)", Baseline_ProbeHorizontalBlocklightFalloff));
            scenarios.Add(new Scenario("B39: Opaque face receives source-1 surface light but never propagates inward (oracle-independent probe)", Baseline_ProbeOpaqueSurfaceStamp));
            scenarios.Add(new Scenario("B45: Cumulative attenuation through two DimGlass layers in series — sky column + horizontal blocklight (oracle-independent probe; finding C5)", Baseline_ProbeCumulativeMultiLayerAttenuation));

            // --- C4a sunlight-column persist→replay (the Sun-channel twin of B30) ---
            scenarios.Add(new Scenario("B46: Cross-chunk sunlight toward an unloaded neighbor persists as a column and replays on load (finding C4a)", Baseline_PersistReplayCrossChunkSunlight));

            // --- C4b AddPendingBlocklight placement-after-removal guard (direct store invariant) ---
            scenarios.Add(new Scenario("B47: AddPendingBlocklight placement-after-removal guard holds (finding C4b)", Baseline_PendingBlocklightPlacementAfterRemovalGuard));

            // --- C1 Bug-09 geometry fuzz (randomized border/source/held-chunk/budget across 50 seeds) ---
            scenarios.Add(new Scenario("B40: Bug-09 cross-chunk geometry fuzz converges to the oracle across 50 randomized seeds (Bug 09 guard)", Baseline_Bug09GeometryFuzz));

            // --- B2 neighbors-not-ready scheduling deferral ---
            scenarios.Add(new Scenario("B41: Scheduling defers while neighbors' terrain data is not ready, then converges to the oracle once ready (finding B2)", Baseline_NeighborsNotReadyDefersScheduling));

            // --- C2 Bug-05 dense-canopy generation fuzz (randomized canopy/wells/dividers across seeds) ---
            scenarios.Add(new Scenario("B42: Dense-canopy generation converges to the oracle across randomized seeds (finding C2; surfaced Bug 10)", Baseline_Bug05CanopyFuzz));

            // --- Bug 10 (fixed June 2026, promoted from K10a/K10b): opaque-border light leak ---
            scenarios.Add(new Scenario("B43: No sunlight leaks out of an opaque block across a chunk border (Bug 10 guard)", Baseline_OpaqueBorderSunlightNoLeak));
            scenarios.Add(new Scenario("B44: No surface blocklight leaks out of an opaque block across a chunk border (Bug 10 guard)", Baseline_OpaqueBorderBlocklightNoLeak));

            // --- Bug 11 (fixed June 2026, promoted from K11a): cross-seam sunlight removal oscillation on reload ---
            scenarios.Add(new Scenario("B48: Reload mid-darkness-wave at a mutually-lit seam converges to the oracle — no sunlight removal/replace oscillation (Bug 11 guard)", Baseline_ReloadSeamSunlightNoOscillation));
            scenarios.Add(new Scenario("B49: Cross-chunk sunlight removal into a semi-transparent (DimGlass) seam voxel attenuates support by target opacity — a brighter in-chunk neighbor does not spuriously veto a legitimate removal", Baseline_CrossSeamSunlightRemovalThroughDimGlass));

            // --- Bug 12 family (B50 tripwire, B51/B52 completeness, B53 promoted repro) lives in its own
            // partial file (Baselines/LightingValidationSuite.Baseline.Bug12.cs) and self-registers here.
            // First of the planned Baselines/ split; future groups follow the same Add*BaselineScenarios hook. ---
            AddBug12BaselineScenarios(scenarios);

            // --- C3 cross-chunk sunlight darkening family (B54 in-flight race, B55 steady-state) lives in
            // Baselines/LightingValidationSuite.Baseline.C3Darkening.cs and self-registers here. ---
            AddCrossChunkDarkeningBaselineScenarios(scenarios);

            // --- Bug 13 suspended-slab family (B56/B57 generation-wave, B58/B59 dynamic-stamp fix
            // tripwires; promoted from K13a-K13d) lives in
            // Baselines/LightingValidationSuite.Baseline.Bug13Slab.cs and self-registers here. ---
            AddBug13SlabBaselineScenarios(scenarios);

            // --- Bug 14 fix baselines (B60 halo-node claim contract, B61 promoted ghost repro) live in
            // Baselines/LightingValidationSuite.Baseline.Bug14Ghost.cs and self-register here. ---
            AddBug14GhostBaselineScenarios(scenarios);

            // --- Bug 15 fix baselines (B62 sun stamp, B63 RGB stamp; promoted from K15b/K15c) live in
            // Baselines/LightingValidationSuite.Baseline.Bug15Stamp.cs and self-register here. ---
            AddBug15StampBaselineScenarios(scenarios);

            // --- Bug 05 fix baseline (B64, promoted from K15a after the July 2026 border-edit
            // edge-check re-grant + in-game confirmation): the HF-3 border-heightmap fuzz. One
            // varied-heightmap-at-seam geometry axis guarding two fixes — Bug 15 stamps (all seeds) and
            // Bug 05 edge-round exhaustion (seed 14). Body + private helpers live in
            // LightingValidationSuite.BorderHeightFuzz.cs. ---
            scenarios.Add(new Scenario(
                "B64: Border-heightmap fuzz — varied heights at every seam, seam overhangs, and border edits settle on the oracle across randomized seeds (Bug 15 + Bug 05 guard)",
                Baseline_BorderHeightFuzz));

            // --- Completion-pass fault isolation (B65, HF-4 #2 / finding B7): the sim drives the shared
            // LightingCompletionPass skeleton and injects a merge fault to prove per-job isolation. Lives in
            // Baselines/LightingValidationSuite.Baseline.FaultIsolation.cs and self-registers here. ---
            AddFaultIsolationBaselineScenarios(scenarios);
        }

        /// <summary>Hook for the Bug-12 family baselines (implemented in Baselines/LightingValidationSuite.Baseline.Bug12.cs).</summary>
        /// <param name="scenarios">The scenario list to append to.</param>
        static partial void AddBug12BaselineScenarios(List<Scenario> scenarios);

        /// <summary>Hook for the C3 cross-chunk sunlight darkening baselines (implemented in Baselines/LightingValidationSuite.Baseline.C3Darkening.cs).</summary>
        /// <param name="scenarios">The scenario list to append to.</param>
        static partial void AddCrossChunkDarkeningBaselineScenarios(List<Scenario> scenarios);

        /// <summary>Hook for the completion-pass fault-isolation baseline (implemented in Baselines/LightingValidationSuite.Baseline.FaultIsolation.cs).</summary>
        /// <param name="scenarios">The scenario list to append to.</param>
        static partial void AddFaultIsolationBaselineScenarios(List<Scenario> scenarios);

        /// <summary>Hook for the Bug-13 suspended-slab baselines (implemented in Baselines/LightingValidationSuite.Baseline.Bug13Slab.cs).</summary>
        /// <param name="scenarios">The scenario list to append to.</param>
        static partial void AddBug13SlabBaselineScenarios(List<Scenario> scenarios);

        /// <summary>Hook for the Bug-14 fix baselines (implemented in Baselines/LightingValidationSuite.Baseline.Bug14Ghost.cs).</summary>
        /// <param name="scenarios">The scenario list to append to.</param>
        static partial void AddBug14GhostBaselineScenarios(List<Scenario> scenarios);

        /// <summary>Hook for the Bug-15 stamp baselines (implemented in Baselines/LightingValidationSuite.Baseline.Bug15Stamp.cs).</summary>
        /// <param name="scenarios">The scenario list to append to.</param>
        static partial void AddBug15StampBaselineScenarios(List<Scenario> scenarios);

        /// <summary>
        /// B1: A torch placed mid-air in an empty world. Exercises in-chunk RGB placement BFS and
        /// air attenuation, with full sky light everywhere as background.
        /// </summary>
        private static bool Baseline_TorchInOpenAir()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.RecalculateHeightmaps();
            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "B1: initial lighting converges");

            world.PlaceBlock(new Vector3Int(24, 64, 24), TestBlockPalette.Torch);
            passed &= LightingAssert.Converged(world.RunToConvergence(), "B1: post-placement convergence");

            passed &= LightingAssert.IsTrue(world.GetBlocklightRGB(new Vector3Int(24, 64, 24)) == (14, 14, 14),
                "B1: torch voxel holds its emission",
                $"Expected (14,14,14), got {world.GetBlocklightRGB(new Vector3Int(24, 64, 24))}");

            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world), "B1: field matches oracle");
            return passed;
        }

        /// <summary>
        /// B2: A red lamp next to an opaque stone wall and a semi-transparent pane. Exercises
        /// opaque receive-but-don't-propagate, per-channel emission (pure red), and opacity-based
        /// attenuation through DimGlass (opacity 5).
        /// </summary>
        private static bool Baseline_WallAndSemiTransparent()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(10, TestBlockPalette.Stone);

            // Opaque wall two voxels east of the lamp position, and a DimGlass pane to the west.
            world.FillBox(new Vector3Int(22, 11, 18), new Vector3Int(22, 14, 22), TestBlockPalette.Stone);
            world.SetBlock(new Vector3Int(18, 11, 20), TestBlockPalette.DimGlass);
            world.RecalculateHeightmaps();

            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "B2: initial lighting converges");

            world.PlaceBlock(new Vector3Int(20, 11, 20), TestBlockPalette.LampRed);
            passed &= LightingAssert.Converged(world.RunToConvergence(), "B2: post-placement convergence");

            // Behind the wall (straight line) the light must have bent around it — the direct
            // path is blocked, so the value comes from the longer BFS path the oracle also takes.
            passed &= LightingAssert.IsTrue(world.GetBlocklightRGB(new Vector3Int(21, 11, 20)).R == 14,
                "B2: red light reaches the wall face",
                $"Expected R=14, got {world.GetBlocklightRGB(new Vector3Int(21, 11, 20))}");

            // Through DimGlass (opacity 5): 15 -> air step to (19) = 14 -> through pane = 9.
            passed &= LightingAssert.IsTrue(world.GetBlocklightRGB(new Vector3Int(18, 11, 20)).R == 9,
                "B2: DimGlass attenuates by its opacity",
                $"Expected R=9 inside the pane, got {world.GetBlocklightRGB(new Vector3Int(18, 11, 20))}");

            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world), "B2: field matches oracle");
            return passed;
        }

        /// <summary>
        /// B3: A solid roof spanning two chunk borders casts a shadow with side-bleed; breaking a
        /// center roof block opens a full-brightness light shaft. Exercises sunlight column
        /// recalculation, cross-chunk sunlight darkness/uplift mods, and the vertical-sunlight rule.
        /// </summary>
        private static bool Baseline_RoofShadowAndShaft()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(10, TestBlockPalette.Stone);

            // Roof at y=30 spanning the borders between chunks (1,1), (2,1), (1,2) and (2,2).
            world.FillBox(new Vector3Int(24, 30, 24), new Vector3Int(40, 30, 40), TestBlockPalette.Stone);
            world.RecalculateHeightmaps();

            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "B3: initial lighting converges");

            // Deep under the roof center: no direct sky, only side-bleed that decays to 0 within 14 steps.
            passed &= LightingAssert.IsTrue(world.GetSkyLight(new Vector3Int(32, 20, 32)) < 15,
                "B3: roof shadows the volume beneath",
                $"Expected <15 under the roof, got {world.GetSkyLight(new Vector3Int(32, 20, 32))}");

            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world), "B3: shadow field matches oracle");

            // Break one roof block on the border column → vertical shaft of 15 to the floor.
            world.BreakBlock(new Vector3Int(32, 30, 32));
            passed &= LightingAssert.Converged(world.RunToConvergence(), "B3: post-break convergence");

            passed &= LightingAssert.IsTrue(world.GetSkyLight(new Vector3Int(32, 20, 32)) == 15,
                "B3: light shaft reaches below the opened roof",
                $"Expected 15 in the shaft, got {world.GetSkyLight(new Vector3Int(32, 20, 32))}");

            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world), "B3: shaft field matches oracle");
            return passed;
        }

        /// <summary>
        /// B4: A white lamp placed and then broken inside a sealed stone box (light fully contained
        /// within one chunk). The light field must return bit-identically to the pre-placement
        /// baseline — the in-chunk form of the ghost-light invariant (the cross-chunk forms are
        /// the B7/B13 race scenarios).
        /// </summary>
        private static bool Baseline_SealedBoxPlaceBreak()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(10, TestBlockPalette.Stone);

            // Sealed hollow box inside chunk (1,1): outer shell (20,12,20)-(26,18,26).
            world.FillBox(new Vector3Int(20, 12, 20), new Vector3Int(26, 18, 26), TestBlockPalette.Stone);
            world.FillBox(new Vector3Int(21, 13, 21), new Vector3Int(25, 17, 25), TestBlockPalette.Air);
            world.RecalculateHeightmaps();

            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "B4: initial lighting converges");

            Dictionary<Vector2Int, ushort[]> baseline = world.SnapshotLightField();

            world.PlaceBlock(new Vector3Int(23, 15, 23), TestBlockPalette.LampWhite);
            passed &= LightingAssert.Converged(world.RunToConvergence(), "B4: post-placement convergence");

            passed &= LightingAssert.IsTrue(world.GetBlocklightRGB(new Vector3Int(24, 15, 23)) == (14, 14, 14),
                "B4: lamp lights the box interior",
                $"Expected (14,14,14) next to the lamp, got {world.GetBlocklightRGB(new Vector3Int(24, 15, 23))}");

            world.BreakBlock(new Vector3Int(23, 15, 23));
            passed &= LightingAssert.Converged(world.RunToConvergence(), "B4: post-break convergence");

            passed &= LightingAssert.FieldsEqual(baseline, world, "B4: light field returns to baseline");
            return passed;
        }

        /// <summary>
        /// B5: A torch placed on the last column of the center chunk so its light spills deep into
        /// the (dark, empty) neighbor — the working cross-chunk path where the receiving voxels had
        /// no prior blocklight. Exercises cross-chunk mod emission, the shared apply decision logic,
        /// and wake-up node seeding.
        /// </summary>
        private static bool Baseline_CrossChunkSpill()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(10, TestBlockPalette.Stone);
            world.RecalculateHeightmaps();

            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "B5: initial lighting converges");

            world.PlaceBlock(new Vector3Int(31, 11, 24), TestBlockPalette.Torch);
            passed &= LightingAssert.Converged(world.RunToConvergence(), "B5: post-placement convergence");

            // Across the border (chunk (2,1) starts at x=32): 14 at the torch minus one per air step.
            passed &= LightingAssert.IsTrue(world.GetBlocklightRGB(new Vector3Int(33, 11, 24)) == (12, 12, 12),
                "B5: light crosses the chunk border",
                $"Expected (12,12,12) at x=33, got {world.GetBlocklightRGB(new Vector3Int(33, 11, 24))}");

            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world), "B5: field matches oracle");
            return passed;
        }

        /// <summary>
        /// B6: A hollow cavity sealed inside solid stone must be pitch black (sky 0, blocklight 0).
        /// Sanity-checks the sunlight column pass and confirms no light leaks through solid volumes.
        /// </summary>
        private static bool Baseline_SealedCavity()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(40, TestBlockPalette.Stone);
            world.FillBox(new Vector3Int(22, 15, 22), new Vector3Int(26, 19, 26), TestBlockPalette.Air);
            world.RecalculateHeightmaps();

            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "B6: initial lighting converges");

            Vector3Int center = new Vector3Int(24, 17, 24);
            passed &= LightingAssert.IsTrue(world.GetLightData(center) == 0,
                "B6: cavity center is pitch black",
                $"Expected packed light 0, got {world.GetLightData(center)}");

            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world), "B6: field matches oracle");
            return passed;
        }

        /// <summary>
        /// B7: The blocklight twin of the B13 race (formerly K08a) — a lamp at the border is broken while the
        /// neighbor chunk has a job in flight, so the darkness mods are overwritten by the stale
        /// merge. This ghost light self-heals: the surviving wake-up nodes carry real old values for
        /// the lowered channels, which triggers the per-channel seeding force-clear
        /// (<c>cur == old &gt; 0</c>). Planted as a tripwire BEFORE the Bug 07 fix (a naive fix —
        /// dropping the force-clear without re-seeding removals — would surface the Bug 08 race
        /// here, exactly as LIGHTING_BUGS.md predicted); it stayed green through the fix (June 2026).
        /// </summary>
        private static bool Baseline_InFlightBlocklightRemovalRace()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(10, TestBlockPalette.Stone);
            world.RecalculateHeightmaps();

            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "B7: initial lighting converges");

            Dictionary<Vector2Int, ushort[]> baseline = world.SnapshotLightField();

            // Lamp on the border column of chunk (1,1); its light bleeds ~13 voxels into (2,1).
            world.PlaceBlock(new Vector3Int(31, 11, 24), TestBlockPalette.LampWhite);
            passed &= LightingAssert.Converged(world.RunToConvergence(), "B7: lamp converges");

            // Race: (2,1)'s job snapshots BEFORE the lamp is broken; (1,1)'s removal mods are
            // applied to (2,1)'s live data mid-flight and then overwritten by the stale merge.
            LightingTestWorld.LightingJobFlight inFlight = world.BeginLightingJob(new Vector2Int(2, 1));
            world.BreakBlock(new Vector3Int(31, 11, 24));
            world.RunLightingJob(new Vector2Int(1, 1));
            world.CompleteLightingJob(inFlight);

            passed &= LightingAssert.Converged(world.RunToConvergence(), "B7: post-race convergence");
            passed &= LightingAssert.FieldsEqual(baseline, world, "B7: no ghost light survives the race");
            return passed;
        }

        /// <summary>
        /// B8: A solid slab covers a 5×5 grid with a single sky well in a diagonal neighbor of the
        /// center region; ALL chunks run their initial lighting as one concurrent wave (stale,
        /// unlit neighbor snapshots) followed by the production's two edge-check rounds.
        /// This was authored as the minimal Bug 05 repro, but the engine converges to the oracle —
        /// so it guards diagonal initial-generation convergence as a baseline instead. Bug 05 still
        /// needs a faithful repro (denser multi-pocket canopies / different mod-loss timing).
        /// </summary>
        private static bool Baseline_DiagonalSkyWellParallelGen()
        {
            using LightingTestWorld world = new LightingTestWorld(5);
            world.FillSuperflatFloor(10, TestBlockPalette.Stone);

            // Full-grid slab at y=30 with one 1×1 sky well at (49, 30, 49) — inside chunk (3,3),
            // one voxel from the corner it shares diagonally with center chunk (2,2).
            const int worldMax = 5 * VoxelData.ChunkWidth - 1;
            world.FillBox(new Vector3Int(0, 30, 0), new Vector3Int(worldMax, 30, worldMax), TestBlockPalette.Stone);
            world.SetBlock(new Vector3Int(49, 30, 49), TestBlockPalette.Air);
            world.RecalculateHeightmaps();

            bool passed = LightingAssert.Converged(world.RunInitialLightingParallel(), "B8: wave-parallel initial lighting converges");
            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world), "B8: under-slab field matches oracle");
            return passed;
        }

        /// <summary>
        /// B9: Blocklight must never propagate from inside opaque volumes (Bug 09, fixed June 2026 —
        /// promoted from known-bug scenario K09). A torch on a stone floor plus a place/break edit
        /// next to it wakes the surface-lit opaque floor voxel as a BFS node; the opaque-source
        /// guard in <c>PropagateLightRGB</c> must stop it from acting as a source. The floor surface
        /// (y=10) legitimately receives a 1-deep stamp; everything below (y ≤ 9) must stay at
        /// blocklight (0,0,0) across the whole grid.
        /// <para>The full-field oracle compare was restored after the Bug 07 fix (June 2026) — it
        /// was temporarily reduced to the volume-scan invariant while Bug 07's cross-border
        /// removal/re-spread loss contaminated the field.</para>
        /// </summary>
        private static bool Baseline_OpaqueVolumeLightContainment()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(10, TestBlockPalette.Stone);
            world.RecalculateHeightmaps();

            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "B9: initial lighting converges");

            // Torch on the floor; the floor surface around it receives a legitimate surface stamp.
            world.PlaceBlock(new Vector3Int(24, 11, 24), TestBlockPalette.Torch);
            passed &= LightingAssert.Converged(world.RunToConvergence(), "B9: torch converges");

            // The leak trigger: one edit next to the lit floor enqueues the surface-lit opaque
            // floor voxel below it as a BFS wake-up node.
            world.PlaceBlock(new Vector3Int(25, 11, 24), TestBlockPalette.Stone);
            passed &= LightingAssert.Converged(world.RunToConvergence(), "B9: post-place convergence");
            world.BreakBlock(new Vector3Int(25, 11, 24));
            passed &= LightingAssert.Converged(world.RunToConvergence(), "B9: post-break convergence");

            // Depth >= 2 inside the floor (below the surface layer) must be blocklight-free everywhere.
            const int worldMax = 3 * VoxelData.ChunkWidth - 1;
            passed &= LightingAssert.NoBlocklightInVolume(world,
                new Vector3Int(0, 0, 0), new Vector3Int(worldMax, 9, worldMax),
                "B9: floor interior carries no blocklight");

            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world), "B9: field matches oracle");
            return passed;
        }

        /// <summary>
        /// B10: Two emissive sources on opposite sides of a chunk border must blend per-channel
        /// across it (Bug 07, fixed June 2026 — promoted from known-bug scenario K07a). A blue lamp
        /// in chunk (2,1) bleeds across the border; a red lamp is then placed in (1,1) near that
        /// border. Guards against cross-chunk uplift mods being re-interpreted as removals
        /// (hard cut-off at the border).
        /// </summary>
        private static bool Baseline_CrossBorderTwoSourceBlend()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(10, TestBlockPalette.Stone);
            world.RecalculateHeightmaps();

            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "B10: initial lighting converges");

            // First source: blue lamp in chunk (2,1), one voxel east of the border.
            world.PlaceBlock(new Vector3Int(33, 11, 24), TestBlockPalette.LampBlue);
            passed &= LightingAssert.Converged(world.RunToConvergence(), "B10: first source converges");

            // Second source: red lamp in chunk (1,1), against the border.
            world.PlaceBlock(new Vector3Int(30, 11, 24), TestBlockPalette.LampRed);
            passed &= LightingAssert.Converged(world.RunToConvergence(), "B10: second source converges");

            // Probe on the blue side: red must have crossed the border. The straight line
            // 30→34 passes THROUGH the opaque blue lamp at x=33, so the red light detours
            // over it: 30(15) → 31(14) → 32(13) → (32,12)(12) → (33,12)(11) → (34,12)(10)
            // → (34,11)(9). Oracle-verified.
            passed &= LightingAssert.IsTrue(world.GetBlocklightRGB(new Vector3Int(34, 11, 24)).R == 9,
                "B10: red light crosses into the blue lamp's chunk",
                $"Expected R=9 at x=34 (detour around the opaque blue lamp), got {world.GetBlocklightRGB(new Vector3Int(34, 11, 24))} — hard cut-off at the border");

            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world), "B10: blended field matches oracle");
            return passed;
        }

        /// <summary>
        /// B11: Red and blue lamps DIRECTLY adjacent across the border (x=31 / x=32) — the maximal
        /// mutual-interference configuration (Bug 07, fixed June 2026 — promoted from known-bug
        /// scenario K07b). Guards against the documented ping-pong (each side's uplift destructively
        /// re-interpreted by the other), which manifested as non-convergence within the round budget
        /// (flicker) or a stable-but-wrong field (cut-off).
        /// </summary>
        private static bool Baseline_AdjacentBorderLampsNoFlicker()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(10, TestBlockPalette.Stone);
            world.RecalculateHeightmaps();

            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "B11: initial lighting converges");

            world.PlaceBlock(new Vector3Int(31, 11, 24), TestBlockPalette.LampRed);
            passed &= LightingAssert.Converged(world.RunToConvergence(), "B11: first lamp converges");

            world.PlaceBlock(new Vector3Int(32, 11, 24), TestBlockPalette.LampBlue);
            passed &= LightingAssert.Converged(world.RunToConvergence(), "B11: adjacent lamps converge without ping-pong");

            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world), "B11: field matches oracle");
            return passed;
        }

        /// <summary>
        /// B12: Breaking one of two overlapping cross-border sources must re-light the broken
        /// source's area from the surviving source and return the field bit-identically to the
        /// single-source baseline (Bug 07, fixed June 2026 — promoted from known-bug scenario K07c).
        /// Guards the out-of-center re-spread pull in <c>PropagateDarkness</c>/<c>PropagateDarknessRGB</c>
        /// (re-spread seeds for border voxels used to be silently dropped).
        /// </summary>
        private static bool Baseline_CrossBorderRespread()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(10, TestBlockPalette.Stone);
            world.RecalculateHeightmaps();

            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "B12: initial lighting converges");

            // The surviving source: torch in chunk (2,1).
            world.PlaceBlock(new Vector3Int(36, 11, 24), TestBlockPalette.Torch);
            passed &= LightingAssert.Converged(world.RunToConvergence(), "B12: surviving source converges");

            Dictionary<Vector2Int, ushort[]> baseline = world.SnapshotLightField();

            // The doomed source: torch in chunk (1,1), fields overlapping across the border.
            world.PlaceBlock(new Vector3Int(28, 11, 24), TestBlockPalette.Torch);
            passed &= LightingAssert.Converged(world.RunToConvergence(), "B12: both sources converge");

            world.BreakBlock(new Vector3Int(28, 11, 24));
            passed &= LightingAssert.Converged(world.RunToConvergence(), "B12: post-break convergence");

            passed &= LightingAssert.FieldsEqual(baseline, world, "B12: field returns to the single-source baseline");
            return passed;
        }

        /// <summary>
        /// B13: Cross-chunk mods must survive an in-flight lighting job on the receiving chunk
        /// (Bug 08 path 2, fixed June 2026 — promoted from known-bug scenario K08a). A roof spans
        /// the border; chunk (2,1) has a lighting job IN FLIGHT (inputs already snapshotted) when a
        /// roof block is broken in (1,1) and that chunk's job emits sunlight uplift mods into (2,1).
        /// Guards the defer/drain: mods targeting an in-flight chunk are deferred and applied right
        /// after that chunk's merge, instead of being applied to live data and silently reverted by
        /// the stale full-LightMap overwrite (which left the area permanently darker than the oracle).
        /// </summary>
        private static bool Baseline_InFlightSunlightUpliftRace()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(10, TestBlockPalette.Stone);

            // Roof at y=30 spanning the (1,1)/(2,1) border.
            world.FillBox(new Vector3Int(26, 30, 18), new Vector3Int(38, 30, 30), TestBlockPalette.Stone);
            world.RecalculateHeightmaps();

            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "B13: initial lighting converges");

            // Put chunk (2,1)'s job in flight, THEN break a roof block in (1,1) near the border and
            // run (1,1)'s job — its uplift mods target (2,1) mid-flight and must be deferred.
            LightingTestWorld.LightingJobFlight inFlight = world.BeginLightingJob(new Vector2Int(2, 1));

            world.BreakBlock(new Vector3Int(30, 30, 24));
            world.RunLightingJob(new Vector2Int(1, 1));

            // The merge that used to overwrite the uplift; the deferred mods drain right after it.
            world.CompleteLightingJob(inFlight);

            passed &= LightingAssert.Converged(world.RunToConvergence(), "B13: post-race convergence");
            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world), "B13: field matches oracle after the race");
            return passed;
        }

        /// <summary>
        /// B14: A lamp written by GENERATION (raw voxel write, no queue seeding — the
        /// <c>SetBlock</c> path) must illuminate its surroundings during initial lighting
        /// (Bug 06, fixed June 2026 — promoted from known-bug scenario K06). Guards the
        /// placement-queue seeding in <c>SyncEmissionToLightArray</c>: every position whose
        /// emission gets stamped is enqueued for placement BFS, so generation-written emissives
        /// propagate instead of illuminating only their own voxel.
        /// </summary>
        private static bool Baseline_GeneratedEmissiveSeedsBfs()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(10, TestBlockPalette.Stone);
            world.SetBlock(new Vector3Int(24, 11, 24), TestBlockPalette.LampWhite);
            world.RecalculateHeightmaps();

            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "B14: initial lighting converges");

            passed &= LightingAssert.IsTrue(world.GetBlocklightRGB(new Vector3Int(25, 11, 24)) == (14, 14, 14),
                "B14: generated lamp illuminates its neighbor voxel",
                $"Expected (14,14,14) next to the lamp, got {world.GetBlocklightRGB(new Vector3Int(25, 11, 24))} — emission was stamped but never propagated");

            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world), "B14: field matches oracle");
            return passed;
        }

        // --- Bug-09 cross-chunk fleet, consolidated (2026-06-14; see LIGHTING_VALIDATION_HARNESS_FIDELITY.md §5).
        // B15 and B16 are the two deterministic representatives that replaced the former eleven single-instance
        // permutations (old B15–B25). They are the readable, no-seed regression anchors; the broad search lives
        // in B26–B29 (50-seed sweeps) and B40 (geometry fuzz). Every mechanism the retired scenarios exercised —
        // ContainsKey accumulate, single-slot budget, shuffled/reverse order, multi-frame held flights,
        // both-chunks-in-flight (B22/B28), and fluid-opacity contention — is still covered by the survivors.

        /// <summary>
        /// B15 (consolidated; supersedes the old B15/B16 direct-harness pair): the deterministic direct-harness
        /// break+place cross-chunk race. A lamp on chunk A's (1,1) border column spills into chunk B (2,1);
        /// the lamp is broken and re-placed first while only B has a job in flight, then again while BOTH chunks
        /// are in flight — exercising the defer/drain + re-schedule path in both the single- and bidirectional
        /// in-flight cases (the manual <c>Begin/RunLightingJob/CompleteLightingJob</c> path, distinct from the
        /// frame-simulator survivors). After each race the field must reconcile to the borderless oracle.
        /// Authored originally as a Bug 09 repro; the engine converges (consistent with finding B3 — synchronous
        /// interleavings are exhausted), so it guards the cross-chunk defer/drain against regressions.
        /// </summary>
        private static bool Baseline_DirectHarnessBreakPlaceInFlightRace()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(10, TestBlockPalette.Stone);
            world.RecalculateHeightmaps();

            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "B15: initial lighting converges");

            Vector3Int lampPos = new Vector3Int(31, 11, 24);
            Vector2Int chunkA = new Vector2Int(1, 1);
            Vector2Int chunkB = new Vector2Int(2, 1);

            world.PlaceBlock(lampPos, TestBlockPalette.LampWhite);
            passed &= LightingAssert.Converged(world.RunToConvergence(), "B15: initial lamp converges");

            // Phase 1 — single neighbor (B) in flight: B snapshots before the break; the break+place mods are
            // deferred for B and drained after its merge.
            LightingTestWorld.LightingJobFlight flightB = world.BeginLightingJob(chunkB);
            world.BreakBlock(lampPos);
            world.RunLightingJob(chunkA);
            world.PlaceBlock(lampPos, TestBlockPalette.LampWhite);
            world.CompleteLightingJob(flightB);
            passed &= LightingAssert.Converged(world.RunToConvergence(), "B15: single-in-flight race converges");
            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world),
                "B15: field matches oracle after the single-in-flight race");

            // Phase 2 — both chunks in flight: break FIRST so A's held job carries the removal and emits a
            // cross-chunk removal mod toward B while B is also in flight — that mod must be DEFERRED and then
            // drained on B's merge (the bidirectional defer/drain). The re-place lands on A's live data (after
            // its flight began, so it is invisible to the held job) and is reconciled by the trailing convergence.
            world.BreakBlock(lampPos);
            LightingTestWorld.LightingJobFlight flightA2 = world.BeginLightingJob(chunkA); // drains the removal into A's job
            LightingTestWorld.LightingJobFlight flightB2 = world.BeginLightingJob(chunkB);
            world.PlaceBlock(lampPos, TestBlockPalette.LampWhite);
            LightingTestWorld.LightingRunResult aResult = world.CompleteLightingJob(flightA2);
            passed &= LightingAssert.IsTrue(aResult.ModsDeferred > 0,
                "B15: A's removal mod is deferred while B is in flight",
                $"Expected ModsDeferred > 0 (both-in-flight defer path), got {aResult.ModsDeferred}");
            world.CompleteLightingJob(flightB2); // B merges its stale snapshot, then drains the deferred removal mod
            passed &= LightingAssert.Converged(world.RunToConvergence(), "B15: both-in-flight race converges");
            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world),
                "B15: field matches oracle after the both-in-flight race");

            (byte R, byte G, byte B) crossBorder = world.GetBlocklightRGB(new Vector3Int(32, 11, 24));
            passed &= LightingAssert.IsTrue(crossBorder.R >= 13,
                "B15: blocklight still crosses the chunk border after the races",
                $"Expected R >= 13 at x=32 (one step from the lamp), got ({crossBorder.R},{crossBorder.G},{crossBorder.B})");

            return passed;
        }

        /// <summary>
        /// B16 (consolidated; supersedes the old B23/B24/B25 fluid trio): the deterministic fluid-contention
        /// break+place race. An ocean-biome cell — water on the lamp's five non-floor faces — is set up at the
        /// (1,1)/(2,1) border. The lamp is broken (chunk A's removal flight held), a water voxel is placed into
        /// the vacated cell (modeling fluid backfill — the harness has no fluid sim; Air→Water, opacity 0→2,
        /// injecting BFS nodes + an opacity change mid-flight), and the lamp is re-placed, all
        /// under a single-slot job budget. Exercises the opacity-change + fluid-BFS contention path on the
        /// defer/drain/re-schedule machinery; the field must reconcile to the borderless oracle, and blocklight
        /// must still cross the border through the water (opacity 2). Originally a Bug 09 repro; converges.
        /// </summary>
        private static bool Baseline_FluidBreakPlaceHeldFlightRace()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(10, TestBlockPalette.Stone);
            world.RecalculateHeightmaps();

            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "B16: initial lighting converges");

            Vector3Int lampPos = new Vector3Int(31, 11, 24);
            Vector2Int chunkA = new Vector2Int(1, 1);

            // Underwater environment: water on the lamp's five non-floor faces (the ocean-biome case).
            world.PlaceBlock(new Vector3Int(30, 11, 24), TestBlockPalette.Water);
            world.PlaceBlock(new Vector3Int(31, 11, 23), TestBlockPalette.Water);
            world.PlaceBlock(new Vector3Int(31, 11, 25), TestBlockPalette.Water);
            world.PlaceBlock(new Vector3Int(31, 12, 24), TestBlockPalette.Water);
            world.PlaceBlock(new Vector3Int(32, 11, 24), TestBlockPalette.Water);
            passed &= LightingAssert.Converged(world.RunToConvergence(), "B16: water placement converges");

            world.PlaceBlock(lampPos, TestBlockPalette.LampWhite);
            passed &= LightingAssert.Converged(world.RunToConvergence(), "B16: initial lamp converges");

            LightingFrameSimulator sim = new LightingFrameSimulator(world);

            // Break the lamp, place a water voxel into the vacated cell (modeling fluid backfill), then re-place
            // the lamp — all while chunk A's removal flight is held and the budget is throttled to one chunk/frame.
            world.BreakBlock(lampPos);
            sim.RunFrame(budget: 1, completionPredicate: LightingFrameSimulator.ExceptChunks(chunkA));
            world.PlaceBlock(lampPos, TestBlockPalette.Water);
            sim.RunFrame(budget: 1, completionPredicate: LightingFrameSimulator.ExceptChunks(chunkA));
            world.PlaceBlock(lampPos, TestBlockPalette.LampWhite);
            sim.RunFrame(budget: 1);

            int frames = sim.RunToConvergenceSingleSlot();
            passed &= LightingAssert.Converged(frames, "B16: fluid + held-flight + single-slot budget race converges");
            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world),
                "B16: field matches oracle after the fluid race");

            (byte R, byte G, byte B) crossBorder = world.GetBlocklightRGB(new Vector3Int(33, 11, 24));
            passed &= LightingAssert.IsTrue(crossBorder.R >= 11,
                "B16: blocklight crosses the chunk border through water after the fluid race",
                $"Expected R >= 11 at x=33 (through water, opacity 2), got ({crossBorder.R},{crossBorder.G},{crossBorder.B})");

            return passed;
        }

        /// <summary>
        /// B22 (promoted from K09f): Both chunks have in-flight jobs simultaneously, completed in an
        /// interleaved order that maximizes deferred mod accumulation. A completes first (removal mods
        /// DEFERRED for still-in-flight B), then B completes and drains them. Guards bidirectional
        /// defer/drain under multi-frame dual-chunk contention.
        /// </summary>
        private static bool Baseline_MultiFrameDualChunkInterleaved()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(10, TestBlockPalette.Stone);
            world.RecalculateHeightmaps();

            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "B22: initial lighting converges");

            Vector3Int lampPos = new Vector3Int(31, 11, 24);
            Vector2Int chunkA = new Vector2Int(1, 1);
            Vector2Int chunkB = new Vector2Int(2, 1);

            world.PlaceBlock(lampPos, TestBlockPalette.LampWhite);
            passed &= LightingAssert.Converged(world.RunToConvergence(), "B22: initial lamp converges");

            LightingFrameSimulator sim = new LightingFrameSimulator(world);

            // Frame 1: Break lamp → A schedules removal. Hold everything.
            world.BreakBlock(lampPos);
            LightingFrameSimulator.FrameResult f1 = sim.RunFrame(
                completionPredicate: (_, _) => false);
            passed &= LightingAssert.IsTrue(f1.JobsScheduled >= 1,
                "B22: frame 1 schedules removal job",
                $"Expected >= 1 job scheduled, got {f1.JobsScheduled}");

            // Frame 2: Place lamp (queued). B schedules. Hold everything.
            world.PlaceBlock(lampPos, TestBlockPalette.LampWhite);
            sim.RunFrame(completionPredicate: (_, _) => false);

            // Frame 3: Complete A only. A's removal mods for B → DEFERRED. A re-schedules.
            sim.RunFrame(completionPredicate: LightingFrameSimulator.OnlyChunks(chunkA));

            // Frame 4: Complete B. Drains deferred mods. Also complete A's emission if scheduled.
            sim.RunFrame();

            int frames = sim.RunToConvergence();
            passed &= LightingAssert.Converged(frames, "B22: post-race convergence");

            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world),
                "B22: field matches oracle after dual-chunk interleaved completion");

            (byte R, byte G, byte B) crossBorder = world.GetBlocklightRGB(new Vector3Int(32, 11, 24));
            passed &= LightingAssert.IsTrue(crossBorder.R >= 13,
                "B22: blocklight crosses chunk border after interleaved completion",
                $"Expected R >= 13 at x=32, got ({crossBorder.R},{crossBorder.G},{crossBorder.B})");

            return passed;
        }

        // --- Seeded iteration-order randomness baselines (promoted from K09j/k/l — Bug 09 repro attempts, June 2026) ---
        // These exercise Dictionary iteration randomness in ProcessLightingJobs via seeded Fisher-Yates
        // shuffles of both completion and scheduling order. Each scenario runs 50 RNG seeds looking for
        // any that produces an oracle mismatch. All seeds converge correctly, guarding that the
        // defer/drain mechanism is order-independent.

        private const int BASELINE_SEED_SWEEP_ITERATIONS = 50;

        /// <summary>
        /// B26 (promoted from K09j): The "kitchen sink" — combines every production behavior the
        /// simulator models (ContainsKey guard, multi-frame flights, fluid-flow contention) plus
        /// randomized iteration order via <see cref="LightingFrameSimulator.CompletionOrder.Shuffled"/>.
        /// Runs <see cref="BASELINE_SEED_SWEEP_ITERATIONS"/> seeds. Guards that the defer/drain mechanism
        /// converges regardless of Dictionary iteration order under fluid contention.
        /// </summary>
        private static bool Baseline_ShuffledFluidContention()
        {
            Vector3Int lampPos = new Vector3Int(31, 11, 24);
            Vector2Int chunkA = new Vector2Int(1, 1);

            int? failingSeed = LightingFrameSimulator.FindFailingSeed(
                worldFactory: () =>
                {
                    LightingTestWorld world = new LightingTestWorld(3);
                    world.FillSuperflatFloor(10, TestBlockPalette.Stone);
                    world.RecalculateHeightmaps();
                    world.RunInitialLighting();

                    world.PlaceBlock(new Vector3Int(30, 11, 24), TestBlockPalette.Water);
                    world.PlaceBlock(new Vector3Int(31, 11, 23), TestBlockPalette.Water);
                    world.PlaceBlock(new Vector3Int(31, 11, 25), TestBlockPalette.Water);
                    world.PlaceBlock(new Vector3Int(31, 12, 24), TestBlockPalette.Water);
                    world.PlaceBlock(new Vector3Int(32, 11, 24), TestBlockPalette.Water);
                    world.RunToConvergence();

                    world.PlaceBlock(lampPos, TestBlockPalette.LampWhite);
                    world.RunToConvergence();
                    return world;
                },
                scenarioBody: (world, sim) =>
                {
                    world.BreakBlock(lampPos);
                    sim.RunFrame(budget: 2, order: LightingFrameSimulator.CompletionOrder.Shuffled,
                        completionPredicate: LightingFrameSimulator.ExceptChunks(chunkA));

                    world.PlaceBlock(lampPos, TestBlockPalette.Water);
                    sim.RunFrame(budget: 2, order: LightingFrameSimulator.CompletionOrder.Shuffled,
                        completionPredicate: LightingFrameSimulator.ExceptChunks(chunkA));

                    world.PlaceBlock(lampPos, TestBlockPalette.LampWhite);
                    sim.RunFrame(order: LightingFrameSimulator.CompletionOrder.Shuffled);

                    int frames = sim.RunToConvergence(order: LightingFrameSimulator.CompletionOrder.Shuffled);
                    if (frames < 0) return false;

                    return LightingAssert.MatchesOracle(world, LightingOracle.Solve(world),
                        "B26: field matches oracle (silent check)");
                },
                iterations: BASELINE_SEED_SWEEP_ITERATIONS);

            bool passed = LightingAssert.IsTrue(!failingSeed.HasValue,
                $"B26: all {BASELINE_SEED_SWEEP_ITERATIONS} seeds converge — shuffled fluid contention",
                failingSeed.HasValue
                    ? $"REGRESSION: seed {failingSeed.Value} produces oracle mismatch under shuffled fluid contention"
                    : "");

            return passed;
        }

        /// <summary>
        /// B27 (promoted from K09k): Shuffled scheduling under extreme budget pressure (1 job per frame).
        /// Under single-slot budget with shuffled scheduling, a different chunk gets the single slot each
        /// frame depending on the seed. Runs <see cref="BASELINE_SEED_SWEEP_ITERATIONS"/> seeds. Guards
        /// convergence under maximum starvation + shuffled iteration order.
        /// </summary>
        private static bool Baseline_ShuffledBudgetPressure()
        {
            Vector3Int lampPos = new Vector3Int(31, 11, 24);
            Vector2Int chunkA = new Vector2Int(1, 1);

            int? failingSeed = LightingFrameSimulator.FindFailingSeed(
                worldFactory: () =>
                {
                    LightingTestWorld world = new LightingTestWorld(3);
                    world.FillSuperflatFloor(10, TestBlockPalette.Stone);
                    world.RecalculateHeightmaps();
                    world.RunInitialLighting();

                    world.PlaceBlock(new Vector3Int(30, 11, 24), TestBlockPalette.Water);
                    world.PlaceBlock(new Vector3Int(31, 11, 23), TestBlockPalette.Water);
                    world.PlaceBlock(new Vector3Int(31, 11, 25), TestBlockPalette.Water);
                    world.PlaceBlock(new Vector3Int(31, 12, 24), TestBlockPalette.Water);
                    world.PlaceBlock(new Vector3Int(32, 11, 24), TestBlockPalette.Water);
                    world.RunToConvergence();

                    world.PlaceBlock(lampPos, TestBlockPalette.LampWhite);
                    world.RunToConvergence();
                    return world;
                },
                scenarioBody: (world, sim) =>
                {
                    world.BreakBlock(lampPos);
                    sim.RunFrame(budget: 1, order: LightingFrameSimulator.CompletionOrder.Shuffled,
                        completionPredicate: LightingFrameSimulator.ExceptChunks(chunkA));

                    world.PlaceBlock(lampPos, TestBlockPalette.Water);
                    sim.RunFrame(budget: 1, order: LightingFrameSimulator.CompletionOrder.Shuffled,
                        completionPredicate: LightingFrameSimulator.ExceptChunks(chunkA));

                    world.PlaceBlock(lampPos, TestBlockPalette.LampWhite);
                    sim.RunFrame(budget: 1, order: LightingFrameSimulator.CompletionOrder.Shuffled,
                        completionPredicate: LightingFrameSimulator.ExceptChunks(chunkA));

                    sim.RunFrame(budget: 1, order: LightingFrameSimulator.CompletionOrder.Shuffled);
                    sim.RunFrame(budget: 1, order: LightingFrameSimulator.CompletionOrder.Shuffled);

                    int frames = sim.RunToConvergence(budgetPerFrame: 1,
                        order: LightingFrameSimulator.CompletionOrder.Shuffled);
                    if (frames < 0) return false;

                    return LightingAssert.MatchesOracle(world, LightingOracle.Solve(world),
                        "B27: field matches oracle (silent check)");
                },
                iterations: BASELINE_SEED_SWEEP_ITERATIONS);

            bool passed = LightingAssert.IsTrue(!failingSeed.HasValue,
                $"B27: all {BASELINE_SEED_SWEEP_ITERATIONS} seeds converge — shuffled budget pressure",
                failingSeed.HasValue
                    ? $"REGRESSION: seed {failingSeed.Value} produces oracle mismatch under shuffled budget pressure"
                    : "");

            return passed;
        }

        /// <summary>
        /// B28 (promoted from K09l): Both chunks have in-flight jobs simultaneously, completed with
        /// <see cref="LightingFrameSimulator.CompletionOrder.Shuffled"/> — the RNG decides whether A or B
        /// completes first in each frame. Combined with held flights and fluid contention for maximum
        /// interleaving. Runs <see cref="BASELINE_SEED_SWEEP_ITERATIONS"/> seeds. Guards that
        /// bidirectional defer/drain is order-independent.
        /// </summary>
        private static bool Baseline_ShuffledDualChunkInterleaved()
        {
            Vector3Int lampPos = new Vector3Int(31, 11, 24);

            int? failingSeed = LightingFrameSimulator.FindFailingSeed(
                worldFactory: () =>
                {
                    LightingTestWorld world = new LightingTestWorld(3);
                    world.FillSuperflatFloor(10, TestBlockPalette.Stone);
                    world.RecalculateHeightmaps();
                    world.RunInitialLighting();

                    world.PlaceBlock(new Vector3Int(30, 11, 24), TestBlockPalette.Water);
                    world.PlaceBlock(new Vector3Int(31, 11, 23), TestBlockPalette.Water);
                    world.PlaceBlock(new Vector3Int(31, 11, 25), TestBlockPalette.Water);
                    world.PlaceBlock(new Vector3Int(31, 12, 24), TestBlockPalette.Water);
                    world.PlaceBlock(new Vector3Int(32, 11, 24), TestBlockPalette.Water);
                    world.RunToConvergence();

                    world.PlaceBlock(lampPos, TestBlockPalette.LampWhite);
                    world.RunToConvergence();
                    return world;
                },
                scenarioBody: (world, sim) =>
                {
                    world.BreakBlock(lampPos);
                    sim.RunFrame(order: LightingFrameSimulator.CompletionOrder.Shuffled,
                        completionPredicate: (_, _) => false);

                    world.PlaceBlock(lampPos, TestBlockPalette.Water);
                    sim.RunFrame(order: LightingFrameSimulator.CompletionOrder.Shuffled,
                        completionPredicate: (_, _) => false);

                    world.PlaceBlock(lampPos, TestBlockPalette.LampWhite);
                    sim.RunFrame(order: LightingFrameSimulator.CompletionOrder.Shuffled,
                        completionPredicate: (_, _) => false);

                    sim.RunFrame(order: LightingFrameSimulator.CompletionOrder.Shuffled);
                    sim.RunFrame(order: LightingFrameSimulator.CompletionOrder.Shuffled);

                    int frames = sim.RunToConvergence(
                        order: LightingFrameSimulator.CompletionOrder.Shuffled);
                    if (frames < 0) return false;

                    return LightingAssert.MatchesOracle(world, LightingOracle.Solve(world),
                        "B28: field matches oracle (silent check)");
                },
                iterations: BASELINE_SEED_SWEEP_ITERATIONS);

            bool passed = LightingAssert.IsTrue(!failingSeed.HasValue,
                $"B28: all {BASELINE_SEED_SWEEP_ITERATIONS} seeds converge — shuffled dual-chunk interleaved",
                failingSeed.HasValue
                    ? $"REGRESSION: seed {failingSeed.Value} produces oracle mismatch under shuffled dual-chunk interleaved"
                    : "");

            return passed;
        }

        /// <summary>
        /// B29 (promoted from K09m): The definitive combined stress test — layers EVERY production
        /// behavior the harness can model into a single scenario, modeling heavy ocean biome load:
        /// underwater environment, single-slot budget, multi-frame held flights, shuffled
        /// completion+scheduling, fluid-flow contention, two rapid break+fluid+place cycles before
        /// any flight completes, and interleaved chunk release. Runs
        /// <see cref="BASELINE_SEED_SWEEP_ITERATIONS"/> seeds. Guards that the defer/drain mechanism
        /// converges under maximum simultaneous stress regardless of iteration order.
        /// </summary>
        private static bool Baseline_CombinedStress()
        {
            Vector3Int lampPos = new Vector3Int(31, 11, 24);
            Vector2Int chunkA = new Vector2Int(1, 1);
            Vector2Int chunkB = new Vector2Int(2, 1);

            int? failingSeed = LightingFrameSimulator.FindFailingSeed(
                worldFactory: () =>
                {
                    LightingTestWorld world = new LightingTestWorld(3);
                    world.FillSuperflatFloor(10, TestBlockPalette.Stone);
                    world.RecalculateHeightmaps();
                    world.RunInitialLighting();

                    // Underwater environment — water on all 5 non-floor faces around the lamp.
                    world.PlaceBlock(new Vector3Int(30, 11, 24), TestBlockPalette.Water);
                    world.PlaceBlock(new Vector3Int(31, 11, 23), TestBlockPalette.Water);
                    world.PlaceBlock(new Vector3Int(31, 11, 25), TestBlockPalette.Water);
                    world.PlaceBlock(new Vector3Int(31, 12, 24), TestBlockPalette.Water);
                    world.PlaceBlock(new Vector3Int(32, 11, 24), TestBlockPalette.Water);
                    world.RunToConvergence();

                    world.PlaceBlock(lampPos, TestBlockPalette.LampWhite);
                    world.RunToConvergence();
                    return world;
                },
                scenarioBody: (world, sim) =>
                {
                    // === Cycle 1: break + fluid + place, all held, budget=1 ===
                    world.BreakBlock(lampPos);
                    sim.RunFrame(budget: 1, order: LightingFrameSimulator.CompletionOrder.Shuffled,
                        completionPredicate: (_, _) => false);

                    world.PlaceBlock(lampPos, TestBlockPalette.Water);
                    sim.RunFrame(budget: 1, order: LightingFrameSimulator.CompletionOrder.Shuffled,
                        completionPredicate: (_, _) => false);

                    world.PlaceBlock(lampPos, TestBlockPalette.LampWhite);
                    sim.RunFrame(budget: 1, order: LightingFrameSimulator.CompletionOrder.Shuffled,
                        completionPredicate: (_, _) => false);

                    // === Cycle 2: break + fluid + place again, still held ===
                    world.BreakBlock(lampPos);
                    sim.RunFrame(budget: 1, order: LightingFrameSimulator.CompletionOrder.Shuffled,
                        completionPredicate: (_, _) => false);

                    world.PlaceBlock(lampPos, TestBlockPalette.Water);
                    sim.RunFrame(budget: 1, order: LightingFrameSimulator.CompletionOrder.Shuffled,
                        completionPredicate: (_, _) => false);

                    world.PlaceBlock(lampPos, TestBlockPalette.LampWhite);
                    sim.RunFrame(budget: 1, order: LightingFrameSimulator.CompletionOrder.Shuffled,
                        completionPredicate: (_, _) => false);

                    // === Interleaved release under budget pressure ===
                    sim.RunFrame(budget: 1, order: LightingFrameSimulator.CompletionOrder.Shuffled,
                        completionPredicate: LightingFrameSimulator.OnlyChunks(chunkA));

                    sim.RunFrame(budget: 1, order: LightingFrameSimulator.CompletionOrder.Shuffled,
                        completionPredicate: LightingFrameSimulator.OnlyChunks(chunkB));

                    sim.RunFrame(budget: 1, order: LightingFrameSimulator.CompletionOrder.Shuffled);

                    // === Convergence under full stress ===
                    int frames = sim.RunToConvergence(budgetPerFrame: 1,
                        order: LightingFrameSimulator.CompletionOrder.Shuffled);
                    if (frames < 0) return false;

                    return LightingAssert.MatchesOracle(world, LightingOracle.Solve(world),
                        "B29: field matches oracle (silent check)");
                },
                iterations: BASELINE_SEED_SWEEP_ITERATIONS);

            bool passed = LightingAssert.IsTrue(!failingSeed.HasValue,
                $"B29: all {BASELINE_SEED_SWEEP_ITERATIONS} seeds converge — combined stress",
                failingSeed.HasValue
                    ? $"REGRESSION: seed {failingSeed.Value} produces oracle mismatch under combined stress"
                    : "");

            return passed;
        }

        /// <summary>
        /// B30: The cross-chunk persist→replay round-trip (closes the B1 harness gap in
        /// LIGHTING_VALIDATION_HARNESS_FIDELITY.md; the blocklight half of Bug 08, path 1). A torch on
        /// the last column of chunk (1,1) would normally spill into (2,1), but (2,1) is UNLOADED when the
        /// edit converges — so the emitted cross-chunk mod is routed to <c>PersistUndeliverable</c> and
        /// saved in the (in-memory) <c>LightingStateManager</c> instead of applied. While (2,1) is
        /// unloaded its blocklight stays dark; once it is marked loaded, the persisted mod is replayed
        /// through the real <c>CrossChunkLightModApplier.ComputeBlocklight</c> path and BFS reconstructs
        /// the full spill — the converged field must match the all-loaded oracle exactly.
        /// </summary>
        private static bool Baseline_PersistReplayCrossChunkBlocklight()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(10, TestBlockPalette.Stone);
            world.RecalculateHeightmaps();

            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "B30: initial lighting converges");

            // (2,1) is in-world but unloaded: the lamp's cross-chunk mod must be persisted, not applied.
            world.MarkChunkUnloaded(new Vector2Int(2, 1));
            world.PlaceBlock(new Vector3Int(31, 11, 24), TestBlockPalette.Torch);
            passed &= LightingAssert.Converged(world.RunToConvergence(), "B30: persist-while-unloaded converges");

            // The spill never crossed the border — it was persisted instead of applied.
            passed &= LightingAssert.IsTrue(world.GetBlocklightRGB(new Vector3Int(33, 11, 24)) == (0, 0, 0),
                "B30: light stays out of the unloaded neighbor (persisted, not applied)",
                $"Expected (0,0,0) at x=33 while (2,1) unloaded, got {world.GetBlocklightRGB(new Vector3Int(33, 11, 24))}");

            // Reload (2,1): the persisted mod replays and propagation re-runs.
            world.MarkChunkLoaded(new Vector2Int(2, 1), LightingTestWorld.ChunkLoadMode.LoadFromDisk);
            passed &= LightingAssert.Converged(world.RunToConvergence(), "B30: post-replay convergence");

            // Same value the working live cross-chunk path produces (cf. B5): 14 at the torch, −1 per air step.
            passed &= LightingAssert.IsTrue(world.GetBlocklightRGB(new Vector3Int(33, 11, 24)) == (12, 12, 12),
                "B30: replayed light crosses the chunk border",
                $"Expected (12,12,12) at x=33 after replay, got {world.GetBlocklightRGB(new Vector3Int(33, 11, 24))}");

            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world), "B30: replayed field matches oracle");
            return passed;
        }

        /// <summary>
        /// B31: The degrade path (closes the second half of the B1 gap). A cross-chunk mod that was
        /// DEFERRED for chunk (2,1) (because (2,1) had its own job in flight) can no longer be drained
        /// when (2,1)'s job completes while the chunk is UNLOADED — its result is discarded, so the
        /// deferred mods are degraded to the pending store (mirror of
        /// <c>WorldJobManager.DegradeDeferredCrossChunkMods</c>) rather than lost. Marking (2,1) loaded
        /// then replays them, and the converged field must match the all-loaded oracle.
        /// </summary>
        private static bool Baseline_DegradeDeferredOnEmittingChunkUnload()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(10, TestBlockPalette.Stone);
            world.RecalculateHeightmaps();

            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "B31: initial lighting converges");

            // (2,1)'s job is in flight, so (1,1)'s emitted spill mod is DEFERRED for it (not applied).
            LightingTestWorld.LightingJobFlight inFlightB = world.BeginLightingJob(new Vector2Int(2, 1));
            world.PlaceBlock(new Vector3Int(31, 11, 24), TestBlockPalette.Torch);
            LightingTestWorld.LightingRunResult aResult = world.RunLightingJob(new Vector2Int(1, 1));
            passed &= LightingAssert.IsTrue(aResult.ModsDeferred > 0,
                "B31: spill mod is deferred while the neighbor's job is in flight",
                $"Expected ModsDeferred > 0, got {aResult.ModsDeferred}");

            // (2,1) unloads before its job completes: the deferred mods can never be drained, so
            // completing the (now discarded) job must DEGRADE them to the pending store.
            world.MarkChunkUnloaded(new Vector2Int(2, 1));
            LightingTestWorld.LightingRunResult bResult = world.CompleteLightingJob(inFlightB);
            passed &= LightingAssert.IsTrue(bResult.ModsDegraded > 0,
                "B31: inbound deferred mods degrade when the emitting chunk is unloaded",
                $"Expected ModsDegraded > 0, got {bResult.ModsDegraded}");

            // Reload (2,1): the degraded-then-persisted mods replay and propagation re-runs.
            world.MarkChunkLoaded(new Vector2Int(2, 1), LightingTestWorld.ChunkLoadMode.LoadFromDisk);
            passed &= LightingAssert.Converged(world.RunToConvergence(), "B31: post-replay convergence");

            passed &= LightingAssert.IsTrue(world.GetBlocklightRGB(new Vector3Int(33, 11, 24)) == (12, 12, 12),
                "B31: degraded-then-replayed light crosses the chunk border",
                $"Expected (12,12,12) at x=33 after replay, got {world.GetBlocklightRGB(new Vector3Int(33, 11, 24))}");

            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world), "B31: replayed field matches oracle");
            return passed;
        }

        /// <summary>
        /// B32: The freshly-generated discard path (<c>DiscardPendingBlocklight</c>). When a chunk is
        /// regenerated rather than loaded from disk, blocklight mods recorded while it was absent are
        /// obsolete — initial lighting recomputes all blocklight from current neighbor data. Here (2,1)
        /// accumulates a persisted spill from (1,1)'s torch while unloaded, is then marked loaded in
        /// <c>FreshlyGenerated</c> mode (discarding that persisted blocklight), and re-lit: the spill must
        /// be re-derived from the loaded neighbor's border light, so the converged field still matches the
        /// oracle. Guards that discarding never loses light that the neighbor can re-supply.
        /// </summary>
        private static bool Baseline_GeneratedChunkDiscardsPendingBlocklight()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(10, TestBlockPalette.Stone);
            world.RecalculateHeightmaps();

            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "B32: initial lighting converges");

            // Torch spills toward the unloaded (2,1): the mod is persisted (not applied).
            world.MarkChunkUnloaded(new Vector2Int(2, 1));
            world.PlaceBlock(new Vector3Int(31, 11, 24), TestBlockPalette.Torch);
            passed &= LightingAssert.Converged(world.RunToConvergence(), "B32: persist-while-unloaded converges");

            // Regenerate (2,1): the persisted blocklight is DISCARDED, not replayed.
            world.MarkChunkLoaded(new Vector2Int(2, 1), LightingTestWorld.ChunkLoadMode.FreshlyGenerated);

            // Re-light the whole grid (initial-generation convergence + edge-check rounds): the discarded
            // spill must be re-derived from (1,1)'s border light, since the torch is still present there.
            passed &= LightingAssert.Converged(world.RunInitialLighting(), "B32: post-regeneration re-lighting converges");

            passed &= LightingAssert.IsTrue(world.GetBlocklightRGB(new Vector3Int(33, 11, 24)) == (12, 12, 12),
                "B32: discarded spill is re-derived from the loaded neighbor",
                $"Expected (12,12,12) at x=33 after regeneration, got {world.GetBlocklightRGB(new Vector3Int(33, 11, 24))}");

            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world), "B32: re-derived field matches oracle");
            return passed;
        }

        /// <summary>
        /// B46: The cross-chunk SUNLIGHT persist→replay round-trip (closes finding C4a in
        /// LIGHTING_VALIDATION_HARNESS_FIDELITY.md — the sunlight twin of B30, which only covered blocklight).
        /// A roof slab spans the (1,1)/(2,1) border, shadowing the volume beneath. Breaking one roof block on
        /// the (1,1) border column opens a sky shaft whose light would normally spill east into (2,1) as a
        /// Sun-channel cross-chunk mod (the B13 mechanism) — but (2,1) is UNLOADED when the edit converges, so
        /// that mod is routed to <c>PersistUndeliverable</c> and saved as a sunlight COLUMN recalc in the
        /// (in-memory) <c>LightingStateManager</c> (<c>PersistMod</c> <c>Channel == Sun</c> →
        /// <c>AddPending</c>) instead of applied. While (2,1) is unloaded its under-roof sky stays at the
        /// pre-break shadowed value; once it is marked loaded, the persisted column replays through
        /// <c>SunColumnRecalcQueue</c> and the recalc re-derives the spill from (1,1)'s now-lit border — so the
        /// converged field must match the all-loaded oracle exactly. Guards that the Sun-channel persist path
        /// (distinct from B30/B31/B32's blocklight path) neither loses nor double-applies the column.
        /// </summary>
        private static bool Baseline_PersistReplayCrossChunkSunlight()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(10, TestBlockPalette.Stone);

            // Roof at y=30 spanning the (1,1)/(2,1) border (the B13 geometry).
            world.FillBox(new Vector3Int(26, 30, 18), new Vector3Int(38, 30, 30), TestBlockPalette.Stone);
            world.RecalculateHeightmaps();

            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "B46: initial lighting converges");

            // Deep under the roof interior in (2,1): only side-bleed reaches it, so it is shadowed (< 15) and
            // will brighten noticeably once the shaft's spill crosses the border.
            Vector3Int probe = new Vector3Int(32, 20, 24);
            byte shadowed = world.GetSkyLight(probe);

            // (2,1) is in-world but unloaded: the sun uplift mod emitted when the shaft opens must be persisted.
            world.MarkChunkUnloaded(new Vector2Int(2, 1));
            world.BreakBlock(new Vector3Int(30, 30, 24)); // open a sky shaft on the (1,1) border column

            // Run (1,1)'s job explicitly to assert the Sun-channel mod was persisted (not applied).
            LightingTestWorld.LightingRunResult aResult = world.RunLightingJob(new Vector2Int(1, 1));
            passed &= LightingAssert.IsTrue(aResult.ModsPersisted > 0,
                "B46: the sunlight spill mod is persisted while the neighbor is unloaded",
                $"Expected ModsPersisted > 0, got {aResult.ModsPersisted}");
            passed &= LightingAssert.Converged(world.RunToConvergence(), "B46: persist-while-unloaded converges");

            // The spill never crossed the border — it was persisted instead of applied.
            passed &= LightingAssert.IsTrue(world.GetSkyLight(probe) == shadowed,
                "B46: sunlight stays out of the unloaded neighbor (persisted, not applied)",
                $"Expected sky to stay {shadowed} at {probe} while (2,1) unloaded, got {world.GetSkyLight(probe)}");

            // Reload (2,1): the persisted sunlight column replays and the recalc re-derives from (1,1)'s border.
            world.MarkChunkLoaded(new Vector2Int(2, 1), LightingTestWorld.ChunkLoadMode.LoadFromDisk);
            passed &= LightingAssert.Converged(world.RunToConvergence(), "B46: post-replay convergence");

            passed &= LightingAssert.IsTrue(world.GetSkyLight(probe) > shadowed,
                "B46: the replayed sunlight column brightens the shadowed region",
                $"Expected sky > {shadowed} at {probe} after replay, got {world.GetSkyLight(probe)}");

            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world), "B46: replayed field matches oracle");
            return passed;
        }

        /// <summary>
        /// B47: The <c>AddPendingBlocklight</c> placement-after-removal guard (closes finding C4b in
        /// LIGHTING_VALIDATION_HARNESS_FIDELITY.md). B30/B31/B32 exercise the persist store end-to-end but
        /// never pin its order-sensitive overwrite rule: a placement mod must NOT overwrite a pending removal
        /// for the same voxel (the removal's darkness wave must still run on load), while a removal MUST
        /// overwrite a pending placement. Pinned directly against the real <c>LightingStateManager</c>
        /// in-memory store (oracle-free, like B34's <c>Reset()</c> guard) — a regression here is invisible to
        /// every field-comparison baseline. See <see cref="LightingAssert.AssertPendingBlocklightPlacementAfterRemovalGuard"/>.
        /// </summary>
        private static bool Baseline_PendingBlocklightPlacementAfterRemovalGuard()
        {
            return LightingAssert.AssertPendingBlocklightPlacementAfterRemovalGuard(
                "B47: AddPendingBlocklight guard — a placement never overwrites a pending removal; a removal overwrites a placement");
        }

        /// <summary>
        /// B33: Pool-recycle correctness (closes finding B4). A 5×5 slab-with-diagonal-sky-well world
        /// (the B8 geometry, whose under-slab field only converges correctly when the post-generation
        /// edge-check rounds run) is lit, then EVERY chunk's <see cref="ChunkData"/> is recycled through
        /// the real production <c>Reset()</c> (the pool return/acquire path), the identical terrain is
        /// re-authored (Reset wipes voxels), and the world is re-lit. The re-lit field must equal both the
        /// pre-recycle snapshot and the oracle — which only holds if <c>Reset()</c> cleared all transient
        /// state (light, BFS queues, sections, flags) AND restored <c>RemainingEdgeCheckRounds = 2</c>
        /// (a stale 0 would skip the edge rounds and leave the under-slab borders unreconciled).
        /// </summary>
        private static bool Baseline_PoolRecycleResetsChunkState()
        {
            using LightingTestWorld world = new LightingTestWorld(5);

            // Slab at y=30 over a 5×5 floor with a single 1×1 sky well at (49,30,49) — the B8 geometry.
            const int worldMax = 5 * VoxelData.ChunkWidth - 1;

            void BuildWorld()
            {
                world.FillSuperflatFloor(10, TestBlockPalette.Stone);
                world.FillBox(new Vector3Int(0, 30, 0), new Vector3Int(worldMax, 30, worldMax), TestBlockPalette.Stone);
                world.SetBlock(new Vector3Int(49, 30, 49), TestBlockPalette.Air);
                world.RecalculateHeightmaps();
            }

            BuildWorld();
            bool passed = LightingAssert.Converged(world.RunInitialLightingParallel(), "B33: pre-recycle initial lighting converges");

            Dictionary<Vector2Int, ushort[]> baseline = world.SnapshotLightField();
            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world), "B33: pre-recycle field matches oracle");

            // Recycle every chunk through the REAL ChunkData.Reset(), then rebuild + re-light identically.
            world.RecycleAllChunks();
            BuildWorld(); // Reset() wiped voxels — re-author the identical terrain.

            passed &= LightingAssert.Converged(world.RunInitialLightingParallel(), "B33: post-recycle re-lighting converges");
            passed &= LightingAssert.FieldsEqual(baseline, world, "B33: re-lit field equals the pre-recycle snapshot");
            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world), "B33: re-lit field matches oracle");
            return passed;
        }

        /// <summary>
        /// B34: Direct reset-completeness guard for the pooled <see cref="ChunkData"/> (finding B4; the
        /// regression guard for the historical <c>RemainingEdgeCheckRounds</c>-stale-after-recycle bug).
        /// Dirties a real <c>ChunkData</c> across all transient surfaces, recycles it through the real
        /// <c>Reset()</c>, and asserts no stale state remains — with a reflection backstop over every
        /// <c>[NonSerialized]</c> primitive field so a new transient flag/counter added later without a
        /// reset is caught generically. See <see cref="LightingAssert.AssertResetClearsTransientState"/>.
        /// </summary>
        private static bool Baseline_ChunkDataResetClearsTransientState()
        {
            return LightingAssert.AssertResetClearsTransientState(
                "B34: ChunkData.Reset() clears all transient state on pool recycle");
        }

        /// <summary>
        /// B41: The <c>NeighborsNotReady</c> scheduling-deferral path (closes finding B2 in
        /// LIGHTING_VALIDATION_HARNESS_FIDELITY.md — the simulator formerly hardcoded
        /// <c>neighborsDataReady: true</c>, leaving the third arm of <see cref="LightingScheduleDecision"/>
        /// unexercised). A lamp is placed on the +X border of chunk (1,1) while (1,1)'s neighbor terrain
        /// data is marked NOT ready. Production's <c>WorldJobManager.ScheduleLightingUpdate</c> would set
        /// <c>HasLightChangesToProcess = true</c> and decline to schedule; the simulator must do the same:
        /// across several frames the chunk is deferred (no job scheduled, none in flight), its pending
        /// light work is retained, and no blocklight propagates. Once the neighbors are marked ready, the
        /// retained work schedules and the field must converge to the all-ready oracle — proving the
        /// deferral neither loses nor double-applies the work.
        /// </summary>
        /// <summary>
        /// B43 (Bug 10, sunlight — promoted from K10a, fixed June 2026): the cross-chunk edge check must
        /// not propagate an opaque block's surface sky light across the chunk border. An enclosed under-roof
        /// gap with one sky well in chunk (0,1) and a full-gap-height opaque post on the (0,1)/(1,1) shared
        /// border: the post is lit to sky 5 on its well-facing side, but being opaque it must transmit
        /// nothing — the air just inside (1,1) is borderless-correct at 2 (light routed AROUND the post via
        /// z=23/25), not 4. Guards the neighbor-opacity guard added to <c>CheckEdgeVoxel</c>; an add-only
        /// edge check could never reconcile the over-bright surplus away. Likely in-game manifestation: a
        /// diagonal over-bright band along chunk borders at world-height (where the opaque heightmap surface
        /// sits on the borders), visible in the ChunkBorder debug visualization.
        /// </summary>
        private static bool Baseline_OpaqueBorderSunlightNoLeak()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(10, TestBlockPalette.Stone);

            const int worldMax = 3 * VoxelData.ChunkWidth - 1;
            // Enclosing roof at y=14 — the under-roof gap (y=11..13) is dark except the single sky well.
            world.FillBox(new Vector3Int(0, 14, 0), new Vector3Int(worldMax, 14, worldMax), TestBlockPalette.Stone);
            // Sky well in chunk (0,1): remove the one roof voxel so sunlight reaches the gap below.
            world.SetBlock(new Vector3Int(5, 14, 24), TestBlockPalette.Air);
            // Full-gap-height opaque post on the (0,1)/(1,1) shared border.
            for (int y = 11; y <= 13; y++)
                world.SetBlock(new Vector3Int(15, y, 24), TestBlockPalette.Stone);
            world.RecalculateHeightmaps();

            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "B43: initial lighting converges");
            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world),
                "B43: no sunlight leaks across the opaque border post");
            return passed;
        }

        /// <summary>
        /// B44 (Bug 10, blocklight — promoted from K10b, fixed June 2026): the same enclosed geometry lit by
        /// a torch instead of a sky well. An opaque border block must transmit only its OWN emission across
        /// the border (none — stone), never the surface blocklight it received. Guards the opaque-neighbor
        /// emission rule added to <c>CheckEdgeVoxelRGB</c>; the legitimate opaque-EMISSIVE cross-border case
        /// stays covered by baselines B5/B10.
        /// </summary>
        private static bool Baseline_OpaqueBorderBlocklightNoLeak()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(10, TestBlockPalette.Stone);

            const int worldMax = 3 * VoxelData.ChunkWidth - 1;
            // Enclosing roof at y=14 with no well — under-roof sunlight is 0, isolating the blocklight path.
            world.FillBox(new Vector3Int(0, 14, 0), new Vector3Int(worldMax, 14, worldMax), TestBlockPalette.Stone);
            // Torch in chunk (0,1) two voxels from the border; opaque post on the shared border.
            world.SetBlock(new Vector3Int(13, 11, 24), TestBlockPalette.Torch);
            for (int y = 11; y <= 13; y++)
                world.SetBlock(new Vector3Int(15, y, 24), TestBlockPalette.Stone);
            world.RecalculateHeightmaps();

            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "B44: initial lighting converges");
            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world),
                "B44: no surface blocklight leaks across the opaque border post");
            return passed;
        }

        private static bool Baseline_NeighborsNotReadyDefersScheduling()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(10, TestBlockPalette.Stone);
            world.RecalculateHeightmaps();

            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "B41: initial lighting converges");

            LightingFrameSimulator sim = new LightingFrameSimulator(world);
            Vector2Int litChunk = new Vector2Int(1, 1);
            Vector3Int lampPos = new Vector3Int(31, 11, 24);

            // (1,1)'s neighbor terrain is still generating: any scheduling attempt for it must defer.
            world.MarkNeighborsNotReady(litChunk);
            world.PlaceBlock(lampPos, TestBlockPalette.LampWhite);

            // Drive several frames: (1,1) must be deferred every frame — never scheduled, never in flight.
            bool deferredEveryFrame = true;
            for (int i = 0; i < 5; i++)
            {
                LightingFrameSimulator.FrameResult frame = sim.RunFrame();
                deferredEveryFrame &= frame.ChunksNeighborsNotReady == 1 && frame.JobsScheduled == 0;
            }

            passed &= LightingAssert.IsTrue(deferredEveryFrame,
                "B41: chunk is deferred (not scheduled) every frame while its neighbors are not ready",
                "Expected each frame to report ChunksNeighborsNotReady == 1 and JobsScheduled == 0");
            passed &= LightingAssert.IsTrue(sim.InFlightCount == 0,
                "B41: no lighting job is in flight while deferred",
                $"Expected InFlightCount == 0, got {sim.InFlightCount}");
            passed &= LightingAssert.IsTrue(world.ChunkHasLightWork(litChunk),
                "B41: the deferred chunk retains its pending light work",
                "Expected (1,1) to still have light work while deferred");
            passed &= LightingAssert.IsTrue(world.GetBlocklightRGB(lampPos) == (0, 0, 0),
                "B41: no blocklight propagates while scheduling is deferred",
                $"Expected (0,0,0) at the lamp while deferred, got {world.GetBlocklightRGB(lampPos)}");

            // Neighbors are ready now: the retained work schedules and must converge to the oracle.
            world.MarkNeighborsReady(litChunk);
            int frames = sim.RunToConvergence();
            passed &= LightingAssert.Converged(frames, "B41: post-ready frame convergence");

            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world),
                "B41: field matches oracle after deferral resolves");

            (byte R, byte G, byte B) crossBorder = world.GetBlocklightRGB(new Vector3Int(32, 11, 24));
            passed &= LightingAssert.IsTrue(crossBorder.R >= 13,
                "B41: blocklight crosses the chunk border once the deferred work runs",
                $"Expected R >= 13 at x=32, got ({crossBorder.R},{crossBorder.G},{crossBorder.B})");

            return passed;
        }

        // --- B48 geometry (Bug 11): a horizontal sky corridor lit from two symmetric shafts that meet
        // at a chunk seam, so the seam columns are mutually supported from both sides. ---
        private const int B48_FLOOR_Y = 63;
        private const int B48_CORRIDOR_Y = 64;
        private const int B48_ROOF_Y = 65;
        private const int B48_CORRIDOR_Z = 24;
        private const int B48_WEST_SHAFT_X = 5; // chunk (0,1): x 0..15
        private const int B48_EAST_SHAFT_X = 26; // chunk (1,1): x 16..31 — symmetric about the x15|16 seam

        /// <summary>
        /// B48 (promoted from K11a, Bug 11, fixed June 2026): guards against the stale-snapshot sunlight
        /// removal/re-placement oscillation across a chunk seam that stalled the initial load of reloaded
        /// worlds (<c>ForceCompleteDataJobsCoroutine exceeded max iterations</c>).
        /// <para>
        /// Builds a roofed horizontal corridor lit by two symmetric vertical sky shafts (one per seam
        /// chunk) so the shared border columns are fed equally from both sides (each reaches the seam at
        /// distance 10 → sky 5). After convergence, BOTH seam chunks are seeded with a stale sunlight
        /// removal node via <see cref="LightingTestWorld.SeedLoadedSunlightRemoval"/> — the faithful
        /// mirror of two adjacent chunks reloaded from a save written mid-darkness-wave
        /// (<c>ChunkSerializer.ReadLightQueue</c> restores an in-flight <c>SunlightBfsQueue</c> node) —
        /// then the grid is run <b>wave-parallel</b> (all jobs snapshot the same pre-round state).
        /// </para>
        /// <para>
        /// The fix (<c>CrossChunkLightModApplier.ComputeSunlight</c> + <c>InChunkSunlightSupport</c>)
        /// vetoes a cross-chunk sunlight removal that an in-chunk neighbor still independently supports,
        /// so the wave converges and matches the borderless oracle. Before the fix this never converged
        /// and pinned the two seam voxels at sky 4 instead of 5 — a regression here means the veto
        /// weakened or the removal path again clobbers independently-supported seam light.
        /// </para>
        /// </summary>
        private static bool Baseline_ReloadSeamSunlightNoOscillation()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            int width = world.GridSize * VoxelData.ChunkWidth;

            // Solid floor under the whole grid; opaque roof everywhere EXCEPT the two shaft columns,
            // which stay open so vertical skylight reaches the corridor and spreads horizontally.
            world.FillBox(new Vector3Int(0, B48_FLOOR_Y, 0), new Vector3Int(width - 1, B48_FLOOR_Y, width - 1),
                TestBlockPalette.Stone);
            for (int x = 0; x < width; x++)
            {
                for (int z = 0; z < width; z++)
                {
                    bool isShaft = z == B48_CORRIDOR_Z && (x == B48_WEST_SHAFT_X || x == B48_EAST_SHAFT_X);
                    if (!isShaft)
                        world.SetBlock(new Vector3Int(x, B48_ROOF_Y, z), TestBlockPalette.Stone);
                }
            }

            world.RecalculateHeightmaps();
            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "B48: initial lighting converges");

            // The seam columns are mutually lit to sky 5 from the two shafts before the perturbation.
            Vector3Int westSeam = new Vector3Int(15, B48_CORRIDOR_Y, B48_CORRIDOR_Z);
            Vector3Int eastSeam = new Vector3Int(16, B48_CORRIDOR_Y, B48_CORRIDOR_Z);
            passed &= LightingAssert.IsTrue(world.GetSkyLight(westSeam) == 5 && world.GetSkyLight(eastSeam) == 5,
                "B48: seam columns are mutually lit to sky 5 before reload",
                $"Expected 5/5, got {world.GetSkyLight(westSeam)}/{world.GetSkyLight(eastSeam)}");

            // Reload mid-darkness-wave: both seam chunks come back carrying a stale removal seed at their
            // shared border (strength 15, above the live value, so each launches a darkness wave back
            // across the seam against the other's stale snapshot).
            world.SeedLoadedSunlightRemoval(westSeam, 15);
            world.SeedLoadedSunlightRemoval(eastSeam, 15);

            passed &= LightingAssert.Converged(world.RunWaveToConvergence(),
                "B48: reload reconciliation converges (no removal/replace oscillation)");
            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world),
                "B48: reloaded seam matches the oracle field");

            return passed;
        }

        // --- B49 (finding 3 guard): a direct, deterministic test of the cross-chunk sunlight removal veto.
        // We set ONE in-chunk neighbor of the probe to a known sky and read the production guard's support
        // through two test affordances. A full cross-chunk-removal scenario is intentionally NOT used:
        // removing a shaft that feeds a seam voxel with a bright in-chunk neighbor forms a cross-chunk
        // light loop (the seam voxels mutually support each other across the boundary), so no removal mod
        // ever reaches the target — that orphaned-loop limitation is a separate concern that would mask
        // exactly what finding 3 changed (the attenuation formula). ---
        private const byte B49_neighbor_SKY = 12; // the single lit in-chunk neighbor of the probe
        private const byte B49_DIMGLASS_OPACITY = 5; // TestBlockPalette.DimGlass

        /// <summary>
        /// B49 (code-review finding 3, June 2026): guards that the cross-chunk sunlight removal veto
        /// (<c>CrossChunkLightModApplier.InChunkSunlightSupport</c>) attenuates a neighbor's sky by the
        /// <b>target voxel's opacity</b> (<c>max(1, opacity)</c>, matching
        /// <c>NeighborhoodLightingJob.AttenuateLight</c> and the <c>CheckEdgeVoxel</c> cross-chunk guard),
        /// not by a flat air step.
        /// <para>
        /// One in-chunk neighbor of the probe is set to sky <see cref="B49_neighbor_SKY"/> (all other
        /// neighbors stay dark). The guard's support for a DimGlass (opacity 5) target must therefore be
        /// <c>sky − max(1,5) = sky − 5</c>, not the pre-fix flat <c>sky − 1</c>. We then drive the real
        /// decision logic (<c>ComputeSunlight</c>) for a removal of a voxel held over-bright at a value
        /// strictly between those two estimates: the opacity-aware support lets the legitimate removal
        /// through, whereas the flat estimate would have vetoed it (pinning stale over-bright light until a
        /// full relight).
        /// </para>
        /// <para>
        /// A regression that reverts the attenuation to the flat air step flips both the support assertion
        /// and the veto comparison red.
        /// </para>
        /// </summary>
        private static bool Baseline_CrossSeamSunlightRemovalThroughDimGlass()
        {
            using LightingTestWorld world = new LightingTestWorld(1);

            // Set a single lit in-chunk neighbor of the probe directly (no BFS / geometry), so the guard's
            // support is a deterministic function of one known neighbor sky.
            Vector3Int probe = new Vector3Int(8, 64, 8);
            Vector3Int neighbor = new Vector3Int(7, 64, 8);
            world.SetSkyLightAt(neighbor, B49_neighbor_SKY);

            // Finding 3: in-chunk support charges the TARGET voxel's opacity on entry (max(1, opacity)),
            // so DimGlass (opacity 5) support is neighborSky-5 — strictly below the pre-fix flat air step.
            byte supportDim = world.InChunkSunlightSupportAt(probe, B49_DIMGLASS_OPACITY);
            byte supportFlat = world.InChunkSunlightSupportAt(probe, 1);
            bool passed = LightingAssert.IsTrue(
                supportDim == B49_neighbor_SKY - B49_DIMGLASS_OPACITY && supportFlat == B49_neighbor_SKY - 1 && supportDim < supportFlat,
                "B49: in-chunk support charges the target's opacity (neighborSky-5), not the flat air step (neighborSky-1)",
                $"neighborSky={B49_neighbor_SKY}: DimGlass support={supportDim} (expected {B49_neighbor_SKY - B49_DIMGLASS_OPACITY}), flat support={supportFlat} (expected {B49_neighbor_SKY - 1})");

            // A DimGlass voxel held over-bright at a value above its true (opacity-attenuated) support but
            // at/below the flat estimate. The opacity-aware guard applies the legitimate removal; the
            // pre-fix flat estimate would have spuriously vetoed it.
            const byte overBright = B49_neighbor_SKY - 3; // supportDim (sky-5) < overBright (sky-3) <= supportFlat (sky-1)
            passed &= LightingAssert.IsTrue(!LightingTestWorld.CrossChunkSunlightRemovalVetoed(overBright, supportDim),
                "B49: opacity-aware support does NOT veto the legitimate cross-chunk removal (it applies)",
                $"removal of sky {overBright} with opacity-aware support {supportDim} was unexpectedly vetoed");
            passed &= LightingAssert.IsTrue(LightingTestWorld.CrossChunkSunlightRemovalVetoed(overBright, supportFlat),
                "B49: the pre-fix flat-attenuation support WOULD have vetoed the same removal (the bug the fix prevents)",
                $"removal of sky {overBright} with flat support {supportFlat} was expected to be vetoed");

            // A FULLY-OPAQUE in-chunk neighbor cannot propagate sunlight (mirror of
            // PropagateLight's IsOpaque source guard), so even storing a high sky-top value it must
            // contribute ZERO support. A separate probe whose only lit neighbor is an opaque Stone block
            // holding full sky must read 0 — otherwise the guard would over-estimate and veto a legitimate
            // removal of a voxel under a roof/wall.
            Vector3Int opaqueProbe = new Vector3Int(8, 70, 8);
            Vector3Int opaqueNeighbor = new Vector3Int(7, 70, 8);
            world.SetBlock(opaqueNeighbor, TestBlockPalette.Stone);
            world.SetSkyLightAt(opaqueNeighbor, 15);
            byte opaqueSupportDim = world.InChunkSunlightSupportAt(opaqueProbe, B49_DIMGLASS_OPACITY);
            byte opaqueSupportFlat = world.InChunkSunlightSupportAt(opaqueProbe, 1);
            passed &= LightingAssert.IsTrue(opaqueSupportDim == 0 && opaqueSupportFlat == 0,
                "B49: a fully-opaque neighbor (cannot propagate sunlight) contributes zero support, even storing sky 15",
                $"Expected 0 support from an opaque sky-15 neighbor, got dim={opaqueSupportDim}, flat={opaqueSupportFlat}");

            return passed;
        }
    }
}
