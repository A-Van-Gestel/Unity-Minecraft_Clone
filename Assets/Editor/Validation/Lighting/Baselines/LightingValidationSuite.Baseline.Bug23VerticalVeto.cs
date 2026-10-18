using System.Collections.Generic;
using Editor.Validation.Lighting.Framework;
using UnityEngine;
using Scenario = Editor.Validation.Framework.Scenario;

namespace Editor.Validation.Lighting
{
    /// <summary>
    /// Bug 23 guards (archived <c>_FIXED_BUGS.md</c> Lighting #27) on the production cross-chunk removal veto
    /// (<c>CrossChunkLightModApplier.InChunkSkylightSupport</c>): the voxel above a target supports a full 15 exactly
    /// when the BFS's vertical-skylight rule would carry it down undimmed, and no more often (B128, the real function
    /// with controlled inputs); and two sky-exposed seam voxels knocked to 14 settle back to 15 instead of re-lighting
    /// each other forever (B129, end to end). B130 repeats B128 on the chunk's top row, where no voxel lies above and the
    /// column pass alone holds a voxel at 15. Self-registered via the <see cref="AddBug23VetoBaselineScenarios"/> hook.
    /// </summary>
    public static partial class LightingValidationSuite
    {
        // B129: a flat stone floor under open sky; the seam pair sits one voxel above it across the x15|16 seam.
        private const int B129_FLOOR_Y = 10;
        private const int B129_SEAM_Y = B129_FLOOR_Y + 1;
        private const int B129_SEAM_Z = 8;
        private const int B129_WEST_SEAM_X = 15;
        private const int B129_EAST_SEAM_X = 16;

        // The livelock state the Master-player watchdog captured: each seam voxel at 14 with the wake node a
        // drained removal leaves behind (old level 15).
        private const byte B129_STALE_SKY = 14;
        private const byte B129_WAKE_OLD_SKY = 15;

        private const int B128_FLOOR_Y = 10;
        private const int B128_PROBE_Y = B128_FLOOR_Y + 1;
        private const int B128_PROBE_Z = 8;
        private const int B128_OPEN_X = 8;
        private const int B128_WATER_ROOF_X = 9;
        private const int B128_DIM_TARGET_X = 10;
        private const byte B128_FULL_SKY = 15;

        // B130: the chunk's top row, open sky with nothing above it.
        private const int TOP_ROW_Y = VoxelData.ChunkHeight - 1;
        private const int B130_SLAB_X = 9;

        /// <summary>Registers the Bug 23 veto baseline (called from <c>AddBaselineScenarios</c>).</summary>
        /// <param name="scenarios">The scenario list to append to.</param>
        static partial void AddBug23VetoBaselineScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario(
                "B128: the cross-chunk removal veto credits a full 15 from the sky column above an open voxel — and only through cost-free faces (water above or a dim-glass target still dims it) (Bug 23)",
                Baseline_VetoCreditsVerticalSkyColumn));
            scenarios.Add(new Scenario(
                "B129: two sky-exposed seam voxels left at a stale 14 with pending wake nodes settle back to 15 under concurrent flights — no cross-seam re-light livelock (Bug 23; promoted from K23a)",
                Baseline_SkyExposedSeamPairSettles));
            scenarios.Add(new Scenario(
                "B130: on the chunk's top row the veto credits a full 15 to an open voxel the column pass holds at 15 — but not to a floor slab, which the heightmap counts (Bug 23, top row)",
                Baseline_VetoCreditsTopRowSky));
        }

        /// <summary>
        /// B130: on a lit superflat, an open air voxel on the chunk's top row gets in-chunk support 15 and a cross-chunk
        /// removal of it is vetoed. A floor half slab there enters for free through its top face but stops the column
        /// below it, so the heightmap counts it and the column pass does not hold it at 15: its support stays below 15.
        /// </summary>
        /// <returns>True when both supports match the column pass.</returns>
        private static bool Baseline_VetoCreditsTopRowSky()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(B128_FLOOR_Y, TestBlockPalette.Stone);
            world.RecalculateHeightmaps();

            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "B130: initial lighting converges");

            Vector3Int open = new Vector3Int(B128_OPEN_X, TOP_ROW_Y, B128_PROBE_Z);
            byte openSupport = world.DirectionalInChunkSkylightSupportAt(open);
            passed &= LightingAssert.IsTrue(openSupport == B128_FULL_SKY,
                "B130: an open top-row voxel has in-chunk support 15",
                $"Expected 15, got {openSupport.ToString()}");
            passed &= LightingAssert.IsTrue(
                LightingTestWorld.CrossChunkSkylightRemovalVetoed(world.GetSkylight(open), openSupport),
                "B130: a cross-chunk removal of that voxel is vetoed");

            Vector3Int slab = new Vector3Int(B130_SLAB_X, TOP_ROW_Y, B128_PROBE_Z);
            world.SetBlock(slab, TestBlockPalette.HalfSlab);
            byte slabSupport = world.DirectionalInChunkSkylightSupportAt(slab);
            passed &= LightingAssert.IsTrue(slabSupport < B128_FULL_SKY,
                "B130: a top-row floor slab does not get a full 15",
                $"Expected < 15, got {slabSupport.ToString()}");

            return passed;
        }

        /// <summary>
        /// B129 (promoted from repro K23a, October 2026): from the livelock state a Master-player watchdog captured —
        /// both sky-exposed seam voxels held at a stale 14 with a pending old-15 wake node each — the two seam chunks,
        /// lit as concurrent flights (production's defer and drain routing), settle within the round budget on the
        /// borderless oracle's 15.
        /// </summary>
        /// <returns>True when the pair converges to the oracle.</returns>
        private static bool Baseline_SkyExposedSeamPairSettles()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(B129_FLOOR_Y, TestBlockPalette.Stone);
            world.RecalculateHeightmaps();

            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "B129: initial lighting converges");

            Vector3Int west = new Vector3Int(B129_WEST_SEAM_X, B129_SEAM_Y, B129_SEAM_Z);
            Vector3Int east = new Vector3Int(B129_EAST_SEAM_X, B129_SEAM_Y, B129_SEAM_Z);
            passed &= LightingAssert.IsTrue(world.GetSkylight(west) == B128_FULL_SKY && world.GetSkylight(east) == B128_FULL_SKY,
                "B129: both seam voxels start at full sky",
                $"Expected 15/15, got {world.GetSkylight(west).ToString()}/{world.GetSkylight(east).ToString()}");

            world.SetSkylightAt(west, B129_STALE_SKY);
            world.SetSkylightAt(east, B129_STALE_SKY);
            world.SeedLoadedSkylightRemoval(west, B129_WAKE_OLD_SKY);
            world.SeedLoadedSkylightRemoval(east, B129_WAKE_OLD_SKY);

            passed &= LightingAssert.Converged(world.RunWaveToConvergence(),
                "B129: the seam pair settles under concurrent flights");
            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world),
                "B129: the settled seam matches the borderless oracle");

            return passed;
        }

        /// <summary>
        /// B128: on a lit superflat, an air voxel under open sky gets in-chunk support 15 and a cross-chunk removal of
        /// it is vetoed. With a sky-15 water voxel above instead (its bottom face costs light) or a dim-glass target (its
        /// top face costs light) the column dims, so the support stays below 15.
        /// </summary>
        /// <returns>True when all three supports match the vertical rule.</returns>
        private static bool Baseline_VetoCreditsVerticalSkyColumn()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(B128_FLOOR_Y, TestBlockPalette.Stone);
            world.RecalculateHeightmaps();

            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "B128: initial lighting converges");

            Vector3Int open = new Vector3Int(B128_OPEN_X, B128_PROBE_Y, B128_PROBE_Z);
            byte openSupport = world.DirectionalInChunkSkylightSupportAt(open);
            passed &= LightingAssert.IsTrue(openSupport == B128_FULL_SKY,
                "B128: an air voxel under open sky has in-chunk support 15",
                $"Expected 15, got {openSupport.ToString()}");
            passed &= LightingAssert.IsTrue(
                LightingTestWorld.CrossChunkSkylightRemovalVetoed(world.GetSkylight(open), openSupport),
                "B128: a cross-chunk removal of that voxel is vetoed");

            // Controlled inputs, no relight: the cell above holds full sky but is water, whose bottom face costs light.
            Vector3Int waterRoofed = new Vector3Int(B128_WATER_ROOF_X, B128_PROBE_Y, B128_PROBE_Z);
            Vector3Int waterAbove = waterRoofed + Vector3Int.up;
            world.SetBlock(waterAbove, TestBlockPalette.Water);
            world.SetSkylightAt(waterAbove, B128_FULL_SKY);
            byte waterSupport = world.DirectionalInChunkSkylightSupportAt(waterRoofed);
            passed &= LightingAssert.IsTrue(waterSupport < B128_FULL_SKY,
                "B128: a sky-15 water voxel above does not support a full 15",
                $"Expected < 15, got {waterSupport.ToString()}");

            Vector3Int dimTarget = new Vector3Int(B128_DIM_TARGET_X, B128_PROBE_Y, B128_PROBE_Z);
            world.SetBlock(dimTarget, TestBlockPalette.DimGlass);
            byte dimSupport = world.DirectionalInChunkSkylightSupportAt(dimTarget);
            passed &= LightingAssert.IsTrue(dimSupport < B128_FULL_SKY,
                "B128: a dim-glass target under open sky does not get a full 15",
                $"Expected < 15, got {dimSupport.ToString()}");

            return passed;
        }
    }
}
