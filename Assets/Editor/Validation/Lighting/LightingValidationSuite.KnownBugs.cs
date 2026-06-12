using System.Collections.Generic;
using Editor.Validation.Lighting.Framework;
using UnityEngine;

namespace Editor.Validation.Lighting
{
    /// <summary>
    /// Known-bug reproduction scenarios — the test-first encodings of the open bugs in
    /// <c>Documentation/Bugs/LIGHTING_BUGS.md</c>. Each scenario asserts the CORRECT behavior, so it
    /// is EXPECTED TO FAIL until its bug is fixed; the suite runner reports these as warnings, not
    /// regressions. When one starts passing, fix-confirm in-game and archive the bug entry.
    /// </summary>
    public static partial class LightingValidationSuite
    {
        // NOTE on Bug 05: a minimal repro was attempted (5×5 grid, full slab with a single diagonal
        // sky well, wave-parallel initial lighting via RunInitialLightingParallel) but the engine
        // converges to the oracle field — it now lives as baseline B8. Bug 05 likely requires
        // denser multi-pocket canopy patterns or mod-loss timing the minimal case doesn't hit;
        // a faithful repro remains TODO before that bug's fix can be test-driven.
        static partial void AddKnownBugScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario("K06: Generated emissive block illuminates surroundings on initial lighting", KnownBug06_GeneratedEmissiveSeedsBfs, "Bug 06"));
        }

        /// <summary>
        /// K06 (Bug 06): A lamp written by GENERATION (raw voxel write, no queue seeding — the
        /// <c>SetBlock</c> path). The initial lighting pass stamps the lamp's own emission via
        /// <c>SyncEmissionToLightArray</c> but never enqueues it for propagation, so the
        /// surroundings stay dark until something else touches the area.
        /// </summary>
        private static bool KnownBug06_GeneratedEmissiveSeedsBfs()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(10, TestBlockPalette.Stone);
            world.SetBlock(new Vector3Int(24, 11, 24), TestBlockPalette.LampWhite);
            world.RecalculateHeightmaps();

            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "K06: initial lighting converges");

            passed &= LightingAssert.IsTrue(world.GetBlocklightRGB(new Vector3Int(25, 11, 24)) == (14, 14, 14),
                "K06: generated lamp illuminates its neighbor voxel",
                $"Expected (14,14,14) next to the lamp, got {world.GetBlocklightRGB(new Vector3Int(25, 11, 24))} — emission was stamped but never propagated");

            passed &= LightingAssert.MatchesOracle(world, LightingOracle.Solve(world), "K06: field matches oracle");
            return passed;
        }
    }
}
