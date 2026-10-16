using System.Collections.Generic;
using Editor.Validation.Lighting.Framework;
using UnityEngine;
using Scenario = Editor.Validation.Framework.Scenario;

namespace Editor.Validation.Lighting
{
    /// <summary>
    /// Baseline regression scenario for the in-chunk half of finding <b>C7</b> — dynamic column
    /// <b>re-shadow</b> on opaque placement (see
    /// Documentation/Architecture/Testing Framework/LIGHTING_VALIDATION_HARNESS_FIDELITY.md). B3 only
    /// <i>opens</i> a shaft; B123 places an opaque block mid-air in a sky-lit column, then breaks it.
    /// Self-registered via the <see cref="AddInChunkReshadowBaselineScenarios"/> hook called from
    /// <c>AddBaselineScenarios</c> (the <c>Baselines/</c> group-partial pattern).
    /// </summary>
    public static partial class LightingValidationSuite
    {
        private const int C7_RESHADOW_FLOOR_Y = 10; // stone floor top
        private const int C7_RESHADOW_BLOCK_Y = 40; // mid-air placement height
        private const int C7_RESHADOW_X = 24; // chunk (1,1) local x8 — 8 voxels from every chunk border
        private const int C7_RESHADOW_Z = 24; // chunk (1,1) local z8
        private const int C7_RESHADOW_BELOW_Y = 39; // voxel directly under the placed block
        private const int C7_RESHADOW_DEEP_Y = 20; // voxel well below, still in the shadow column

        /// <summary>Registers the C7 in-chunk re-shadow baseline scenarios (called from <c>AddBaselineScenarios</c>).</summary>
        /// <param name="scenarios">The scenario list to append to.</param>
        static partial void AddInChunkReshadowBaselineScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario("B123: Placing an opaque block mid-air in a sky-lit column re-shadows it, and breaking it re-lights the column, both to the oracle (finding C7, in-chunk)", Baseline_InChunkOpaquePlacementReshadow));
        }

        /// <summary>
        /// B123 (finding C7, in-chunk): a single stone block placed mid-air at (24,40,24), inside chunk
        /// (1,1) and away from its borders, queues a skylight column recalc. The voxels below must lose
        /// direct skylight (settling at side-bleed values, &lt; 15) and the whole field must match the
        /// borderless oracle; breaking the block must restore full skylight below and match the oracle again.
        /// </summary>
        private static bool Baseline_InChunkOpaquePlacementReshadow()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(C7_RESHADOW_FLOOR_Y, TestBlockPalette.Stone);
            world.RecalculateHeightmaps();

            Vector3Int placePos = new Vector3Int(C7_RESHADOW_X, C7_RESHADOW_BLOCK_Y, C7_RESHADOW_Z);
            Vector3Int below = new Vector3Int(C7_RESHADOW_X, C7_RESHADOW_BELOW_Y, C7_RESHADOW_Z);
            Vector3Int deep = new Vector3Int(C7_RESHADOW_X, C7_RESHADOW_DEEP_Y, C7_RESHADOW_Z);

            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "B123: initial lighting converges");
            passed &= LightingAssert.IsTrue(world.GetSkylight(below) == 15 && world.GetSkylight(deep) == 15,
                "B123: the open column is fully sky-lit before the placement",
                $"Expected 15 at y{C7_RESHADOW_BELOW_Y} and y{C7_RESHADOW_DEEP_Y}, got {world.GetSkylight(below)} / {world.GetSkylight(deep)}");
            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world), "B123: open-column field matches oracle");

            // Re-shadow: the placement must darken the column below to its side-bleed values.
            world.PlaceBlock(placePos, TestBlockPalette.Stone);
            passed &= LightingAssert.Converged(world.RunToConvergence(), "B123: post-placement convergence");
            passed &= LightingAssert.IsTrue(world.GetSkylight(below) < 15,
                "B123: the voxel directly under the placed block loses direct skylight",
                $"Expected <15 at y{C7_RESHADOW_BELOW_Y}, got {world.GetSkylight(below)}");
            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world), "B123: re-shadowed field matches oracle");

            // Re-light: breaking the block must restore the full column.
            world.BreakBlock(placePos);
            passed &= LightingAssert.Converged(world.RunToConvergence(), "B123: post-break convergence");
            passed &= LightingAssert.IsTrue(world.GetSkylight(below) == 15 && world.GetSkylight(deep) == 15,
                "B123: the column re-lights to full skylight after the block is broken",
                $"Expected 15 at y{C7_RESHADOW_BELOW_Y} and y{C7_RESHADOW_DEEP_Y}, got {world.GetSkylight(below)} / {world.GetSkylight(deep)}");
            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world), "B123: re-lit field matches oracle");

            return passed;
        }
    }
}
