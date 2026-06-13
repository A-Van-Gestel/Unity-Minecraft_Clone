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
            scenarios.Add(new Scenario("B15: Rapid break+place at chunk border with neighbor in-flight converges (Bug 09 guard)", Baseline_RapidBreakPlaceCrossChunkInFlight));
            scenarios.Add(new Scenario("B16: Double rapid break+place with both chunks in-flight converges (Bug 09 guard)", Baseline_DoubleRapidBreakPlaceBothInFlight));
            scenarios.Add(new Scenario("B17: ContainsKey-guarded break+place at chunk border converges via frame simulator (Bug 09 guard)", Baseline_FrameSimContainsKeyBreakPlace));
            scenarios.Add(new Scenario("B18: Single-slot budget break+place converges via frame simulator (Bug 09 guard)", Baseline_FrameSimSingleSlotBreakPlace));
            scenarios.Add(new Scenario("B19: Reverse completion order break+place converges via frame simulator (Bug 09 guard)", Baseline_FrameSimReverseOrderBreakPlace));
            scenarios.Add(new Scenario("B20: Stale neighbor snapshot during held removal flight converges (Bug 09 guard)", Baseline_MultiFrameStaleNeighborSnapshot));
            scenarios.Add(new Scenario("B21: Neighbor stabilizes before source re-emits converges (Bug 09 guard)", Baseline_MultiFrameNeighborStabilizesEarly));
            scenarios.Add(new Scenario("B22: Dual-chunk held flights with interleaved completion converges (Bug 09 guard)", Baseline_MultiFrameDualChunkInterleaved));
            scenarios.Add(new Scenario("B23: Fluid fills broken lamp position while removal in-flight converges (Bug 09 guard)", Baseline_FluidFillsDuringRemoval));
            scenarios.Add(new Scenario("B24: Fluid + re-place with held removal flight and budget pressure converges (Bug 09 guard)", Baseline_FluidReplaceHeldFlightBudget));
            scenarios.Add(new Scenario("B25: Repeated break+fluid+place cycles with interleaved completion converges (Bug 09 guard)", Baseline_RepeatedFluidCycles));
            scenarios.Add(new Scenario("B26: Shuffled completion+scheduling with fluid contention converges across 50 seeds (Bug 09 guard)", Baseline_ShuffledFluidContention));
            scenarios.Add(new Scenario("B27: Shuffled scheduling under budget pressure converges across 50 seeds (Bug 09 guard)", Baseline_ShuffledBudgetPressure));
            scenarios.Add(new Scenario("B28: Shuffled dual-chunk interleaved flights converge across 50 seeds (Bug 09 guard)", Baseline_ShuffledDualChunkInterleaved));
            scenarios.Add(new Scenario("B29: Combined stress — all harness layers simultaneously — converges across 50 seeds (Bug 09 guard)", Baseline_CombinedStress));
            scenarios.Add(new Scenario("B30: Cross-chunk blocklight toward an unloaded neighbor persists and replays on load (Bug 08 path-1)", Baseline_PersistReplayCrossChunkBlocklight));
            scenarios.Add(new Scenario("B31: Deferred mods degrade to the pending store when the emitting chunk unloads mid-flight, then replay on load", Baseline_DegradeDeferredOnEmittingChunkUnload));
            scenarios.Add(new Scenario("B32: Freshly-generated chunk discards persisted blocklight and re-derives the spill from its loaded neighbor", Baseline_GeneratedChunkDiscardsPendingBlocklight));
        }

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

        /// <summary>
        /// B15: A lamp on the last column of chunk A (x=31) whose light spills into chunk B (x≥32).
        /// Chunk B has a lighting job in-flight (simulating concurrent voxel-edit contention from fluid
        /// flow). The lamp is broken and immediately re-placed while B is in-flight, so the removal
        /// cross-chunk mods are deferred and the new emission snapshots stale neighbor light. After
        /// convergence, the light field must match the oracle.
        ///
        /// Authored as a Bug 09 repro attempt — the engine converges correctly under this deterministic
        /// interleaving (the defer/drain mechanism handles it). Guards that the break+place race with
        /// a single in-flight neighbor doesn't regress. Bug 09 likely requires production-only timing
        /// (frame-budget throttling, fluid re-scheduling contention) that the harness cannot model;
        /// a faithful repro remains TODO.
        /// </summary>
        private static bool Baseline_RapidBreakPlaceCrossChunkInFlight()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(10, TestBlockPalette.Stone);
            world.RecalculateHeightmaps();

            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "B15: initial lighting converges");

            Vector3Int lampPos = new Vector3Int(31, 11, 24);

            world.PlaceBlock(lampPos, TestBlockPalette.LampWhite);
            passed &= LightingAssert.Converged(world.RunToConvergence(), "B15: initial lamp converges");

            Vector2Int chunkB = new Vector2Int(2, 1);
            Vector2Int chunkA = new Vector2Int(1, 1);
            LightingTestWorld.LightingJobFlight flightB = world.BeginLightingJob(chunkB);

            world.BreakBlock(lampPos);
            world.RunLightingJob(chunkA);

            world.PlaceBlock(lampPos, TestBlockPalette.LampWhite);

            world.CompleteLightingJob(flightB);

            passed &= LightingAssert.Converged(world.RunToConvergence(), "B15: post-race convergence");

            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world), "B15: field matches oracle after rapid break+place race");

            (byte R, byte G, byte B) crossBorder = world.GetBlocklightRGB(new Vector3Int(32, 11, 24));
            passed &= LightingAssert.IsTrue(crossBorder.R >= 13,
                "B15: blocklight crosses chunk border after race",
                $"Expected R >= 13 at x=32 (one step from lamp), got ({crossBorder.R},{crossBorder.G},{crossBorder.B})");

            return passed;
        }

        /// <summary>
        /// B16: Two rapid break+place cycles with both chunks having jobs in-flight during the second
        /// cycle (wave-parallel). Guards that the defer/drain mechanism handles bidirectional in-flight
        /// contention without light loss.
        ///
        /// Authored as a Bug 09 total-emission-loss repro attempt — the engine converges correctly.
        /// Bug 09 likely requires production-only timing; a faithful repro remains TODO.
        /// </summary>
        private static bool Baseline_DoubleRapidBreakPlaceBothInFlight()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(10, TestBlockPalette.Stone);
            world.RecalculateHeightmaps();

            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "B16: initial lighting converges");

            Vector3Int lampPos = new Vector3Int(31, 11, 24);
            Vector2Int chunkA = new Vector2Int(1, 1);
            Vector2Int chunkB = new Vector2Int(2, 1);

            world.PlaceBlock(lampPos, TestBlockPalette.LampWhite);
            passed &= LightingAssert.Converged(world.RunToConvergence(), "B16: initial lamp converges");

            LightingTestWorld.LightingJobFlight flightB1 = world.BeginLightingJob(chunkB);
            world.BreakBlock(lampPos);
            world.RunLightingJob(chunkA);
            world.PlaceBlock(lampPos, TestBlockPalette.LampWhite);
            world.CompleteLightingJob(flightB1);

            world.RunLightingJob(chunkA);
            if (world.HasPendingLightWork)
                world.RunLightingJob(chunkB);

            LightingTestWorld.LightingJobFlight flightA = world.BeginLightingJob(chunkA);
            LightingTestWorld.LightingJobFlight flightB2 = world.BeginLightingJob(chunkB);

            world.BreakBlock(lampPos);
            world.PlaceBlock(lampPos, TestBlockPalette.LampWhite);

            world.CompleteLightingJob(flightA);
            world.CompleteLightingJob(flightB2);

            passed &= LightingAssert.Converged(world.RunToConvergence(), "B16: post-double-race convergence");

            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world), "B16: field matches oracle after double race");

            (byte R, byte G, byte B) atLamp = world.GetBlocklightRGB(new Vector3Int(30, 11, 24));
            passed &= LightingAssert.IsTrue(atLamp.R >= 14,
                "B16: blocklight present in chunk A after double race",
                $"Expected R >= 14 adjacent to lamp in chunk A, got ({atLamp.R},{atLamp.G},{atLamp.B})");

            (byte R, byte G, byte B) crossBorder = world.GetBlocklightRGB(new Vector3Int(32, 11, 24));
            passed &= LightingAssert.IsTrue(crossBorder.R >= 13,
                "B16: blocklight crosses into chunk B after double race",
                $"Expected R >= 13 at x=32 in chunk B, got ({crossBorder.R},{crossBorder.G},{crossBorder.B})");

            return passed;
        }

        // --- Frame-simulator baselines (promoted from K09a/b/c — Bug 09 repro attempts, June 2026) ---
        // These exercise the production scheduling behaviors the direct harness cannot model:
        // ContainsKey rejection, budget throttling, and completion order sensitivity. All converge
        // correctly, guarding the orchestration layer against regressions.

        /// <summary>
        /// B17 (promoted from K09a): Break+place with the ContainsKey scheduling guard rejecting the
        /// re-schedule. The emission BFS nodes accumulate in the managed queue while the removal job
        /// is in-flight; the next frame drains and processes them. Guards that the guard+accumulate
        /// path converges to the oracle.
        /// </summary>
        private static bool Baseline_FrameSimContainsKeyBreakPlace()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(10, TestBlockPalette.Stone);
            world.RecalculateHeightmaps();

            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "B17: initial lighting converges");

            Vector3Int lampPos = new Vector3Int(31, 11, 24);

            world.PlaceBlock(lampPos, TestBlockPalette.LampWhite);
            passed &= LightingAssert.Converged(world.RunToConvergence(), "B17: initial lamp converges");

            LightingFrameSimulator sim = new LightingFrameSimulator(world);

            world.BreakBlock(lampPos);
            LightingFrameSimulator.FrameResult f0 = sim.RunFrame();
            passed &= LightingAssert.IsTrue(f0.JobsScheduled >= 1,
                "B17: frame 0 schedules removal job",
                $"Expected >= 1 job scheduled, got {f0.JobsScheduled}");

            world.PlaceBlock(lampPos, TestBlockPalette.LampWhite);

            int frames = sim.RunToConvergence();
            passed &= LightingAssert.Converged(frames, "B17: post-race frame convergence");

            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world),
                "B17: field matches oracle after ContainsKey-guarded break+place");

            (byte R, byte G, byte B) crossBorder = world.GetBlocklightRGB(new Vector3Int(32, 11, 24));
            passed &= LightingAssert.IsTrue(crossBorder.R >= 13,
                "B17: blocklight crosses chunk border after ContainsKey race",
                $"Expected R >= 13 at x=32, got ({crossBorder.R},{crossBorder.G},{crossBorder.B})");

            return passed;
        }

        /// <summary>
        /// B18 (promoted from K09b): Break+place under extreme budget pressure (1 job per frame).
        /// Guards that cross-chunk mod delivery converges even when chunks take turns.
        /// </summary>
        private static bool Baseline_FrameSimSingleSlotBreakPlace()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(10, TestBlockPalette.Stone);
            world.RecalculateHeightmaps();

            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "B18: initial lighting converges");

            Vector3Int lampPos = new Vector3Int(31, 11, 24);

            world.PlaceBlock(lampPos, TestBlockPalette.LampWhite);
            passed &= LightingAssert.Converged(world.RunToConvergence(), "B18: initial lamp converges");

            LightingFrameSimulator sim = new LightingFrameSimulator(world);

            world.BreakBlock(lampPos);
            sim.RunFrame(budget: 1);

            world.PlaceBlock(lampPos, TestBlockPalette.LampWhite);

            int frames = sim.RunToConvergenceSingleSlot();
            passed &= LightingAssert.Converged(frames, "B18: single-slot convergence");

            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world),
                "B18: field matches oracle after single-slot budget break+place");

            (byte R, byte G, byte B) crossBorder = world.GetBlocklightRGB(new Vector3Int(32, 11, 24));
            passed &= LightingAssert.IsTrue(crossBorder.R >= 13,
                "B18: blocklight crosses chunk border under budget pressure",
                $"Expected R >= 13 at x=32, got ({crossBorder.R},{crossBorder.G},{crossBorder.B})");

            return passed;
        }

        /// <summary>
        /// B19 (promoted from K09c): Break+place with reverse completion order. Guards that the
        /// defer-vs-apply decision is correct regardless of dictionary iteration order in
        /// production's <c>ProcessLightingJobs</c>.
        /// </summary>
        private static bool Baseline_FrameSimReverseOrderBreakPlace()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(10, TestBlockPalette.Stone);
            world.RecalculateHeightmaps();

            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "B19: initial lighting converges");

            Vector3Int lampPos = new Vector3Int(31, 11, 24);

            world.PlaceBlock(lampPos, TestBlockPalette.LampWhite);
            passed &= LightingAssert.Converged(world.RunToConvergence(), "B19: initial lamp converges");

            LightingFrameSimulator sim = new LightingFrameSimulator(world);

            world.BreakBlock(lampPos);
            sim.RunFrame(order: LightingFrameSimulator.CompletionOrder.Reverse);

            world.PlaceBlock(lampPos, TestBlockPalette.LampWhite);

            int frames = sim.RunToConvergence(order: LightingFrameSimulator.CompletionOrder.Reverse);
            passed &= LightingAssert.Converged(frames, "B19: reverse-order convergence");

            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world),
                "B19: field matches oracle after reverse-order break+place");

            (byte R, byte G, byte B) crossBorder = world.GetBlocklightRGB(new Vector3Int(32, 11, 24));
            passed &= LightingAssert.IsTrue(crossBorder.R >= 13,
                "B19: blocklight crosses chunk border with reverse completion",
                $"Expected R >= 13 at x=32, got ({crossBorder.R},{crossBorder.G},{crossBorder.B})");

            return passed;
        }

        // --- Multi-frame flight lifetime baselines (promoted from K09d/e/f — Bug 09 repro attempts, June 2026) ---
        // These exercise completion predicates that hold flights across multiple frame ticks, creating
        // stale-snapshot interleavings the complete-all simulator cannot produce. All converge
        // correctly, guarding the defer/drain + re-schedule path under multi-frame flight lifetimes.

        /// <summary>
        /// B20 (promoted from K09d): Chunk A's removal job stays in-flight for 2 frames while chunk B
        /// schedules and completes its own job with a stale snapshot of A's pre-removal light. When A's
        /// removal finally completes and emits cross-chunk removal mods, B has already merged its stale
        /// result. The emission re-schedule must propagate correctly into B's live data. Guards the
        /// multi-frame stale-snapshot interleaving path.
        /// </summary>
        private static bool Baseline_MultiFrameStaleNeighborSnapshot()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(10, TestBlockPalette.Stone);
            world.RecalculateHeightmaps();

            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "B20: initial lighting converges");

            Vector3Int lampPos = new Vector3Int(31, 11, 24);
            Vector2Int chunkA = new Vector2Int(1, 1);

            world.PlaceBlock(lampPos, TestBlockPalette.LampWhite);
            passed &= LightingAssert.Converged(world.RunToConvergence(), "B20: initial lamp converges");

            LightingFrameSimulator sim = new LightingFrameSimulator(world);

            // Frame 1: Break lamp, schedule removal. Hold chunk A's flight.
            world.BreakBlock(lampPos);
            LightingFrameSimulator.FrameResult f1 = sim.RunFrame(
                completionPredicate: LightingFrameSimulator.ExceptChunks(chunkA));
            passed &= LightingAssert.IsTrue(f1.JobsScheduled >= 1,
                "B20: frame 1 schedules removal job",
                $"Expected >= 1 job scheduled, got {f1.JobsScheduled}");

            // Frame 2: Place lamp (emission nodes queue up). B schedules with stale snapshot. Hold A.
            world.PlaceBlock(lampPos, TestBlockPalette.LampWhite);
            sim.RunFrame(completionPredicate: LightingFrameSimulator.ExceptChunks(chunkA));

            // Frame 3: Release everything.
            sim.RunFrame();

            int frames = sim.RunToConvergence();
            passed &= LightingAssert.Converged(frames, "B20: post-race convergence");

            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world),
                "B20: field matches oracle after stale-snapshot race");

            (byte R, byte G, byte B) crossBorder = world.GetBlocklightRGB(new Vector3Int(32, 11, 24));
            passed &= LightingAssert.IsTrue(crossBorder.R >= 13,
                "B20: blocklight crosses chunk border after multi-frame race",
                $"Expected R >= 13 at x=32, got ({crossBorder.R},{crossBorder.G},{crossBorder.B})");

            return passed;
        }

        /// <summary>
        /// B21 (promoted from K09e): Chunk B snapshots stale data from chunk A AND stabilizes before
        /// chunk A's removal completes. A's removal mods apply directly to B (already completed), then
        /// A re-schedules with emission nodes. Guards that emission propagates into B even when B
        /// stabilized early with stale data.
        /// </summary>
        private static bool Baseline_MultiFrameNeighborStabilizesEarly()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(10, TestBlockPalette.Stone);
            world.RecalculateHeightmaps();

            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "B21: initial lighting converges");

            Vector3Int lampPos = new Vector3Int(31, 11, 24);
            Vector2Int chunkA = new Vector2Int(1, 1);
            Vector2Int chunkB = new Vector2Int(2, 1);

            world.PlaceBlock(lampPos, TestBlockPalette.LampWhite);
            passed &= LightingAssert.Converged(world.RunToConvergence(), "B21: initial lamp converges");

            LightingFrameSimulator sim = new LightingFrameSimulator(world);

            // Frame 1: Break lamp → schedules removal for A. Hold A.
            world.BreakBlock(lampPos);
            sim.RunFrame(completionPredicate: LightingFrameSimulator.ExceptChunks(chunkA));

            // Frame 2: Place lamp (queued). B schedules with stale snapshot. Hold A.
            world.PlaceBlock(lampPos, TestBlockPalette.LampWhite);
            sim.RunFrame(completionPredicate: LightingFrameSimulator.ExceptChunks(chunkA));

            // Frame 3: Complete B only. B may stabilize with stale data.
            sim.RunFrame(completionPredicate: LightingFrameSimulator.OnlyChunks(chunkB));

            // Frame 4: Release A. Removal merges. A re-schedules with emission.
            sim.RunFrame();

            int frames = sim.RunToConvergence();
            passed &= LightingAssert.Converged(frames, "B21: post-race convergence");

            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world),
                "B21: field matches oracle after early-stabilization race");

            (byte R, byte G, byte B) inB = world.GetBlocklightRGB(new Vector3Int(33, 11, 24));
            passed &= LightingAssert.IsTrue(inB.R >= 12,
                "B21: emission propagated into chunk B after delayed source re-emission",
                $"Expected R >= 12 at x=33 in chunk B, got ({inB.R},{inB.G},{inB.B})");

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

        // --- Fluid-flow contention baselines (promoted from K09g/h/i — Bug 09 repro attempts, June 2026) ---
        // These exercise water flowing back into a broken lamp position (Air→Water opacity change +
        // BFS node injection) while a removal job is in-flight, combined with multi-frame flight
        // lifetimes, budget pressure, and repeated cycles. All converge correctly, guarding the
        // defer/drain + opacity-change re-schedule path under fluid contention.

        /// <summary>
        /// B23 (promoted from K09g): Break a lamp at the chunk border in an underwater environment.
        /// Water flows back into the vacated position (Air→Water, opacity 0→2) while the removal job
        /// is held in-flight. The water placement injects BFS nodes and changes opacity mid-flight.
        /// Chunk B snapshots stale pre-removal light. Lamp is re-placed (Water→Lamp). Guards that
        /// the opacity change + fluid BFS contention converges correctly.
        /// </summary>
        private static bool Baseline_FluidFillsDuringRemoval()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(10, TestBlockPalette.Stone);
            world.RecalculateHeightmaps();

            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "B23: initial lighting converges");

            Vector3Int lampPos = new Vector3Int(31, 11, 24);
            Vector2Int chunkA = new Vector2Int(1, 1);

            world.PlaceBlock(new Vector3Int(30, 11, 24), TestBlockPalette.Water);
            world.PlaceBlock(new Vector3Int(31, 11, 23), TestBlockPalette.Water);
            world.PlaceBlock(new Vector3Int(31, 11, 25), TestBlockPalette.Water);
            world.PlaceBlock(new Vector3Int(31, 12, 24), TestBlockPalette.Water);
            world.PlaceBlock(new Vector3Int(32, 11, 24), TestBlockPalette.Water);
            passed &= LightingAssert.Converged(world.RunToConvergence(), "B23: water placement converges");

            world.PlaceBlock(lampPos, TestBlockPalette.LampWhite);
            passed &= LightingAssert.Converged(world.RunToConvergence(), "B23: initial lamp converges");

            LightingFrameSimulator sim = new LightingFrameSimulator(world);

            world.BreakBlock(lampPos);
            sim.RunFrame(completionPredicate: LightingFrameSimulator.ExceptChunks(chunkA));

            world.PlaceBlock(lampPos, TestBlockPalette.Water);
            sim.RunFrame(completionPredicate: LightingFrameSimulator.ExceptChunks(chunkA));

            world.PlaceBlock(lampPos, TestBlockPalette.LampWhite);
            sim.RunFrame();

            int frames = sim.RunToConvergence();
            passed &= LightingAssert.Converged(frames, "B23: post-race convergence");

            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world),
                "B23: field matches oracle after fluid-flow contention");

            (byte R, byte G, byte B) crossBorder = world.GetBlocklightRGB(new Vector3Int(33, 11, 24));
            passed &= LightingAssert.IsTrue(crossBorder.R >= 11,
                "B23: blocklight crosses chunk border through water after fluid contention",
                $"Expected R >= 11 at x=33 (through water, opacity 2), got ({crossBorder.R},{crossBorder.G},{crossBorder.B})");

            return passed;
        }

        /// <summary>
        /// B24 (promoted from K09h): Fluid contention combined with budget pressure (1 job per frame)
        /// and a removal flight held for 3 frames. Water fills the broken position, then the lamp is
        /// re-placed. Under single-slot budget, only one chunk can schedule per frame. Guards
        /// convergence under maximum starvation + fluid contention.
        /// </summary>
        private static bool Baseline_FluidReplaceHeldFlightBudget()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(10, TestBlockPalette.Stone);
            world.RecalculateHeightmaps();

            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "B24: initial lighting converges");

            Vector3Int lampPos = new Vector3Int(31, 11, 24);
            Vector2Int chunkA = new Vector2Int(1, 1);

            world.PlaceBlock(new Vector3Int(30, 11, 24), TestBlockPalette.Water);
            world.PlaceBlock(new Vector3Int(31, 11, 23), TestBlockPalette.Water);
            world.PlaceBlock(new Vector3Int(31, 11, 25), TestBlockPalette.Water);
            world.PlaceBlock(new Vector3Int(31, 12, 24), TestBlockPalette.Water);
            world.PlaceBlock(new Vector3Int(32, 11, 24), TestBlockPalette.Water);
            passed &= LightingAssert.Converged(world.RunToConvergence(), "B24: water placement converges");

            world.PlaceBlock(lampPos, TestBlockPalette.LampWhite);
            passed &= LightingAssert.Converged(world.RunToConvergence(), "B24: initial lamp converges");

            LightingFrameSimulator sim = new LightingFrameSimulator(world);

            world.BreakBlock(lampPos);
            sim.RunFrame(budget: 1, completionPredicate: LightingFrameSimulator.ExceptChunks(chunkA));

            world.PlaceBlock(lampPos, TestBlockPalette.Water);
            sim.RunFrame(budget: 1, completionPredicate: LightingFrameSimulator.ExceptChunks(chunkA));

            world.PlaceBlock(lampPos, TestBlockPalette.LampWhite);
            sim.RunFrame(budget: 1, completionPredicate: LightingFrameSimulator.ExceptChunks(chunkA));

            sim.RunFrame(budget: 1);

            int frames = sim.RunToConvergenceSingleSlot();
            passed &= LightingAssert.Converged(frames, "B24: single-slot convergence after fluid contention");

            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world),
                "B24: field matches oracle after fluid + budget pressure");

            (byte R, byte G, byte B) crossBorder = world.GetBlocklightRGB(new Vector3Int(33, 11, 24));
            passed &= LightingAssert.IsTrue(crossBorder.R >= 11,
                "B24: blocklight crosses chunk border after fluid + budget contention",
                $"Expected R >= 11 at x=33, got ({crossBorder.R},{crossBorder.G},{crossBorder.B})");

            return passed;
        }

        /// <summary>
        /// B25 (promoted from K09i): Two rapid break+fluid+place cycles with interleaved completion
        /// and both chunks held in-flight. Models repeated player interaction underwater — two full
        /// cycles of (break → water flows in → re-place) happen before any jobs complete. Guards that
        /// cross-chunk mods survive through double-cycle fluid contention.
        /// </summary>
        private static bool Baseline_RepeatedFluidCycles()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(10, TestBlockPalette.Stone);
            world.RecalculateHeightmaps();

            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "B25: initial lighting converges");

            Vector3Int lampPos = new Vector3Int(31, 11, 24);
            Vector2Int chunkA = new Vector2Int(1, 1);
            Vector2Int chunkB = new Vector2Int(2, 1);

            world.PlaceBlock(new Vector3Int(30, 11, 24), TestBlockPalette.Water);
            world.PlaceBlock(new Vector3Int(31, 11, 23), TestBlockPalette.Water);
            world.PlaceBlock(new Vector3Int(31, 11, 25), TestBlockPalette.Water);
            world.PlaceBlock(new Vector3Int(31, 12, 24), TestBlockPalette.Water);
            world.PlaceBlock(new Vector3Int(32, 11, 24), TestBlockPalette.Water);
            passed &= LightingAssert.Converged(world.RunToConvergence(), "B25: water placement converges");

            world.PlaceBlock(lampPos, TestBlockPalette.LampWhite);
            passed &= LightingAssert.Converged(world.RunToConvergence(), "B25: initial lamp converges");

            LightingFrameSimulator sim = new LightingFrameSimulator(world);

            // Cycle 1
            world.BreakBlock(lampPos);
            sim.RunFrame(completionPredicate: (_, _) => false);
            world.PlaceBlock(lampPos, TestBlockPalette.Water);
            sim.RunFrame(completionPredicate: (_, _) => false);
            world.PlaceBlock(lampPos, TestBlockPalette.LampWhite);
            sim.RunFrame(completionPredicate: (_, _) => false);

            // Cycle 2
            world.BreakBlock(lampPos);
            sim.RunFrame(completionPredicate: (_, _) => false);
            world.PlaceBlock(lampPos, TestBlockPalette.Water);
            sim.RunFrame(completionPredicate: (_, _) => false);
            world.PlaceBlock(lampPos, TestBlockPalette.LampWhite);
            sim.RunFrame(completionPredicate: LightingFrameSimulator.OnlyChunks(chunkA));

            sim.RunFrame(completionPredicate: LightingFrameSimulator.OnlyChunks(chunkB));
            sim.RunFrame();

            int frames = sim.RunToConvergence();
            passed &= LightingAssert.Converged(frames, "B25: post-double-cycle convergence");

            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world),
                "B25: field matches oracle after repeated fluid cycles");

            (byte R, byte G, byte B) crossBorder = world.GetBlocklightRGB(new Vector3Int(33, 11, 24));
            passed &= LightingAssert.IsTrue(crossBorder.R >= 11,
                "B25: blocklight crosses chunk border after repeated fluid cycles",
                $"Expected R >= 11 at x=33, got ({crossBorder.R},{crossBorder.G},{crossBorder.B})");

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
    }
}
