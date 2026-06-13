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
    }
}
