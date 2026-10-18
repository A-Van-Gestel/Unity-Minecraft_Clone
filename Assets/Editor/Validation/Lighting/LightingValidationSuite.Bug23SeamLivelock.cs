using Editor.Validation.Lighting.Framework;
using UnityEngine;

namespace Editor.Validation.Lighting
{
    /// <summary>
    /// Lighting Bug 23: two chunks re-light each other's seam voxel every frame forever. Both seam voxels sit under
    /// open sky, so each owner holds its voxel at 15 through the BFS's vertical-skylight rule. A neighbor job cannot
    /// see that rule across the seam: its darkness wave zeroes the cross-seam voxel (a stale 14 below its removal
    /// level) and re-lights it sideways to 14. The cross-chunk removal veto credits the voxel's own sky-15 neighbor
    /// above with only 14 of support, so the removal applies, the owner is woken, restores 15 and sends the mirror
    /// removal back, so neither job ever ends stable.
    /// </summary>
    public static partial class LightingValidationSuite
    {
        // A flat stone floor under open sky; the seam pair sits one voxel above it across the x15|16 seam.
        private const int BUG23_FLOOR_Y = 10;
        private const int BUG23_SEAM_Y = BUG23_FLOOR_Y + 1;
        private const int BUG23_SEAM_Z = 8;
        private const int BUG23_WEST_SEAM_X = 15;
        private const int BUG23_EAST_SEAM_X = 16;

        // The livelock state the Master-player watchdog captured: each seam voxel at 14 with the wake node a
        // drained removal leaves behind (old level 15).
        private const byte BUG23_STALE_SKY = 14;
        private const byte BUG23_WAKE_OLD_SKY = 15;

        /// <summary>
        /// K23a (Bug 23): from the captured livelock state — both sky-exposed seam voxels held at a stale 14 with a
        /// pending old-15 wake node each — the two seam chunks, lit as concurrent flights (production's defer and
        /// drain routing), must settle within the round budget and restore the borderless oracle's 15. Before the fix
        /// both jobs end unstable every round and the seam stays at 14.
        /// </summary>
        /// <returns>True when the pair converges to the oracle.</returns>
        private static bool KnownBug_Bug23SkyExposedSeamPairSettles()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(BUG23_FLOOR_Y, TestBlockPalette.Stone);
            world.RecalculateHeightmaps();

            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "K23a: initial lighting converges");

            Vector3Int west = new Vector3Int(BUG23_WEST_SEAM_X, BUG23_SEAM_Y, BUG23_SEAM_Z);
            Vector3Int east = new Vector3Int(BUG23_EAST_SEAM_X, BUG23_SEAM_Y, BUG23_SEAM_Z);
            passed &= LightingAssert.IsTrue(world.GetSkylight(west) == 15 && world.GetSkylight(east) == 15,
                "K23a: both seam voxels start at full sky",
                $"Expected 15/15, got {world.GetSkylight(west).ToString()}/{world.GetSkylight(east).ToString()}");

            world.SetSkylightAt(west, BUG23_STALE_SKY);
            world.SetSkylightAt(east, BUG23_STALE_SKY);
            world.SeedLoadedSkylightRemoval(west, BUG23_WAKE_OLD_SKY);
            world.SeedLoadedSkylightRemoval(east, BUG23_WAKE_OLD_SKY);

            // Quiet from here: these are the expected-red assertions of a known-bug scenario, and the runner
            // reserves LogError for baseline regressions.
            int rounds = world.RunWaveToConvergence();
            bool finalMatch = LightingAssert.MatchesOracleQuiet(world, LightingOracle.Solve(world), out string summary);
            if (rounds >= 0 && finalMatch)
            {
                Debug.Log($"[PASS] K23a: the sky-exposed seam pair settles in {rounds.ToString()} round(s) and matches the oracle");
            }
            else
            {
                Debug.Log($"K23a: seam pair did not settle (rounds {rounds.ToString()}, west {world.GetSkylight(west).ToString()}, " +
                          $"east {world.GetSkylight(east).ToString()}) — {summary}");
            }

            return passed && rounds >= 0 && finalMatch;
        }
    }
}
