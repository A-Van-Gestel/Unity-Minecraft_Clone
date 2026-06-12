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
        /// baseline — the in-chunk form of the ghost-light invariant (the cross-chunk form is the
        /// Bug 08 scenario).
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
        /// B7: The blocklight twin of the K08a race — a lamp at the border is broken while the
        /// neighbor chunk has a job in flight, so the darkness mods are overwritten by the stale
        /// merge. Today this ghost light self-heals: the surviving wake-up nodes carry
        /// <c>OldBlock &gt; 0</c>, which triggers the seeding force-clear — the very mechanism that is
        /// Bug 07's defect 1. This scenario is a baseline GUARD: it must STAY green when Bug 07 is
        /// fixed. A naive fix (dropping the force-clear without re-seeding removals) surfaces the
        /// Bug 08 race here, exactly as LIGHTING_BUGS.md predicts.
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
        /// <para>Deliberately NOT a full oracle compare: the same edit also triggers Bug 07's
        /// cross-border removal/re-spread loss, which contaminates the full field until that bug is
        /// fixed (covered by K07a–c). This scenario isolates the opaque-containment invariant.</para>
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
            return passed;
        }
    }
}
