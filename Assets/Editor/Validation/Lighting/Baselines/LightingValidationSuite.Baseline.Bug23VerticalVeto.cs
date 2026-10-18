using System.Collections.Generic;
using Editor.Validation.Lighting.Framework;
using UnityEngine;
using Scenario = Editor.Validation.Framework.Scenario;

namespace Editor.Validation.Lighting
{
    /// <summary>
    /// Bug 23 guard on the production cross-chunk removal veto (<c>CrossChunkLightModApplier.InChunkSkylightSupport</c>):
    /// the voxel above a target supports a full 15 exactly when the BFS's vertical-skylight rule would carry it down
    /// undimmed, and no more often. Asserted on the real function with controlled inputs, because the end-to-end repro
    /// (K23a) logs quietly while it is a known-bug scenario. Self-registered via the
    /// <see cref="AddBug23VetoBaselineScenarios"/> hook.
    /// </summary>
    public static partial class LightingValidationSuite
    {
        private const int B128_FLOOR_Y = 10;
        private const int B128_PROBE_Y = B128_FLOOR_Y + 1;
        private const int B128_PROBE_Z = 8;
        private const int B128_OPEN_X = 8;
        private const int B128_WATER_ROOF_X = 9;
        private const int B128_DIM_TARGET_X = 10;
        private const byte B128_FULL_SKY = 15;

        /// <summary>Registers the Bug 23 veto baseline (called from <c>AddBaselineScenarios</c>).</summary>
        /// <param name="scenarios">The scenario list to append to.</param>
        static partial void AddBug23VetoBaselineScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario(
                "B128: the cross-chunk removal veto credits a full 15 from the sky column above an open voxel — and only through cost-free faces (water above or a dim-glass target still dims it) (Bug 23)",
                Baseline_VetoCreditsVerticalSkyColumn));
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
