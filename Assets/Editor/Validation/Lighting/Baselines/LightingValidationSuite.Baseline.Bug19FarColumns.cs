using System.Collections.Generic;
using Editor.Validation.Lighting.Framework;
using Helpers;
using UnityEngine;
using Scenario = Editor.Validation.Framework.Scenario;

namespace Editor.Validation.Lighting
{
    /// <summary>
    /// Baseline regression scenarios <b>B95–B96</b> — far-coordinate sunlight column routing, guarding
    /// the <b>Bug 19</b> fix (far-lands sunlight column recalcs crashing on a negative heightmap index,
    /// see <c>Documentation/Bugs/LIGHTING_BUGS.md</c>). The defective seam was
    /// <see cref="SunlightColumnRouting.RouteToChunkOrigin"/>'s pre-fix float round-trip: an int→float
    /// conversion of the global column loses integer precision past ±2²⁴, so border columns route into
    /// an adjacent chunk's queue bucket and drain to chunk-local columns outside [0, ChunkWidth)² —
    /// exactly the negative heightmap index <c>NeighborhoodLightingJob.RecalculateSunlightForColumn</c>
    /// faulted on in-game at ±2×10⁷.
    /// <para>
    /// <b>B95</b> — routing integrity: every column of a 3×3 grid seeded through the production seam
    /// (<see cref="LightingTestWorld.QueueFullSunlightRecalcViaGlobalRouting"/>) must land in its own
    /// chunk's bucket with an in-range local, at the identity anchor (control), just past the ±2²⁴
    /// float boundary, and at ±2×10⁷ (the observed in-game magnitude) in both sign quadrants.
    /// <b>B96</b> — far differential twin: an identical fixture built at the identity anchor and at
    /// +2×10⁷ voxels, both seeded through the seam and wave-converged with the same driver, must
    /// produce bit-identical light fields (grid-relative) — the anchor must be unobservable.
    /// </para>
    /// Self-registered via the <see cref="AddBug19FarColumnsBaselineScenarios"/> hook called from
    /// <c>AddBaselineScenarios</c> (the <c>Baselines/</c> group-partial pattern).
    /// </summary>
    public static partial class LightingValidationSuite
    {
        // --- Geometry ---
        private const int BUG19_GRID = 3;
        private const int BUG19_FLOOR_Y = 10; // superflat stand-in for the terrain below the far region
        private const int BUG19_BOX_Y = 40; // shadow-casting box altitude (creates a non-trivial field)

        // --- Anchors (world CHUNK coordinates of grid cell (0,0)) ---
        // ±2×10⁷ voxels = the /teleport magnitude Bug 19 was observed at (float granularity there is 2).
        private const int BUG19_FAR_ANCHOR_CHUNKS = 20_000_000 / VoxelData.ChunkWidth;

        // One chunk past the exact ±2²⁴ voxel float-precision boundary — the earliest failing magnitude.
        private const int BUG19_BOUNDARY_ANCHOR_CHUNKS = (1 << 24) / VoxelData.ChunkWidth + 1;

        private const int BUG19_MAX_ROUNDS = 64;

        /// <summary>
        /// Registers the far-coordinate column-routing baselines (B95–B96, the Bug 19 fix guards;
        /// called from <c>AddBaselineScenarios</c>).
        /// </summary>
        /// <param name="scenarios">The scenario list to append to.</param>
        static partial void AddBug19FarColumnsBaselineScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario(
                "B95: Global sunlight-column routing delivers every column to its own chunk with an in-range local at the identity, ±2²⁴-boundary, and ±2×10⁷ anchors (Bug 19 fix tripwire)",
                Baseline_Bug19FarColumnRoutingIntegrity));
            scenarios.Add(new Scenario(
                "B96: An identical fixture seeded through the column-routing seam converges to a bit-identical light field at the identity and +2×10⁷ anchors (far-anchor differential twin)",
                Baseline_Bug19FarAnchorDifferentialTwin));
        }

        // --- Scenario bodies ---

        /// <summary>
        /// B95: seeds every chunk of the fixture through the production routing seam at four anchors
        /// and asserts zero lost columns and zero out-of-range locals. The identity anchor is the
        /// control (green even pre-fix); the far anchors are the fix tripwires — an out-of-range local
        /// here is precisely the job's negative/overflowing heightmap index (Bug 19's crash).
        /// </summary>
        private static bool Baseline_Bug19FarColumnRoutingIntegrity()
        {
            bool passed = RunBug19RoutingIntegrityCase("B95 (identity anchor)", Vector2Int.zero);
            passed &= RunBug19RoutingIntegrityCase("B95 (+2^24 boundary anchor)",
                new Vector2Int(BUG19_BOUNDARY_ANCHOR_CHUNKS, BUG19_BOUNDARY_ANCHOR_CHUNKS));
            passed &= RunBug19RoutingIntegrityCase("B95 (+2e7 far anchor)",
                new Vector2Int(BUG19_FAR_ANCHOR_CHUNKS, BUG19_FAR_ANCHOR_CHUNKS));
            passed &= RunBug19RoutingIntegrityCase("B95 (-2e7 far anchor, negative quadrant)",
                new Vector2Int(-BUG19_FAR_ANCHOR_CHUNKS, -BUG19_FAR_ANCHOR_CHUNKS));
            return passed;
        }

        /// <summary>
        /// B96: the far-anchor differential twin. Builds the identical fixture at the identity anchor
        /// and at +2×10⁷ voxels, seeds both through the routing seam, wave-converges both with the same
        /// driver, and asserts the grid-relative light fields are bit-identical. Fails fast on corrupt
        /// routing rather than running jobs against out-of-range columns (which would fault inside the
        /// Burst job exactly as Bug 19 did in-game, leaking the flight's native containers).
        /// </summary>
        private static bool Baseline_Bug19FarAnchorDifferentialTwin()
        {
            using LightingTestWorld identity = BuildBug19World(Vector2Int.zero);
            if (!SeedBug19ViaRouting(identity, "B96 (identity twin)")) return false;

            using LightingTestWorld far = BuildBug19World(
                new Vector2Int(BUG19_FAR_ANCHOR_CHUNKS, BUG19_FAR_ANCHOR_CHUNKS));
            if (!SeedBug19ViaRouting(far, "B96 (far twin)")) return false;

            int identityRounds = identity.RunWaveToConvergence(BUG19_MAX_ROUNDS);
            int farRounds = far.RunWaveToConvergence(BUG19_MAX_ROUNDS);
            if (identityRounds < 0 || farRounds < 0)
            {
                Debug.LogError($"[FAIL] B96: both twins converge — identity={identityRounds}, far={farRounds} " +
                               $"(-1 = no convergence within {BUG19_MAX_ROUNDS} waves)");
                return false;
            }

            if (!LightingAssert.FieldsEqual(identity.SnapshotLightField(), far,
                    "B96: far-anchored light field is bit-identical to the identity twin"))
                return false;

            Debug.Log($"[PASS] B96: far-anchored light field is bit-identical to the identity twin " +
                      $"(identity {identityRounds} wave(s), far {farRounds} wave(s))");
            return true;
        }

        // --- Case runners ---

        /// <summary>
        /// One B95 leg: builds the fixture at the given anchor, seeds every chunk through the routing
        /// seam, and asserts zero lost columns and zero out-of-range locals.
        /// </summary>
        /// <param name="label">The console label for this case.</param>
        /// <param name="anchorChunk">World chunk coordinate of grid cell (0,0).</param>
        /// <returns>True when every column routes to its own chunk with an in-range local.</returns>
        private static bool RunBug19RoutingIntegrityCase(string label, Vector2Int anchorChunk)
        {
            using LightingTestWorld world = BuildBug19World(anchorChunk);
            if (!SeedBug19ViaRouting(world, label)) return false;

            Debug.Log($"[PASS] {label}: all {BUG19_GRID * BUG19_GRID * 256} columns routed to their own chunk with in-range locals");
            return true;
        }

        /// <summary>
        /// Seeds every grid chunk through <see cref="LightingTestWorld.QueueFullSunlightRecalcViaGlobalRouting"/>
        /// and reports any lost or out-of-range columns as a failure.
        /// </summary>
        /// <param name="world">The world to seed.</param>
        /// <param name="label">The console label used on failure.</param>
        /// <returns>True when the routing was clean for every chunk.</returns>
        private static bool SeedBug19ViaRouting(LightingTestWorld world, string label)
        {
            int totalLost = 0;
            int totalOutOfRange = 0;
            foreach (Vector2Int coord in world.AllChunkCoords())
            {
                totalLost += world.QueueFullSunlightRecalcViaGlobalRouting(coord, out int outOfRange);
                totalOutOfRange += outOfRange;
            }

            if (totalLost == 0 && totalOutOfRange == 0) return true;

            Debug.LogError($"[FAIL] {label}: column routing is corrupt — {totalLost} column(s) routed to no grid chunk, " +
                           $"{totalOutOfRange} drained to out-of-range locals (each is a negative/overflowing " +
                           "heightmap index in NeighborhoodLightingJob.RecalculateSunlightForColumn — the Bug 19 crash)");
            return false;
        }

        // --- World building ---

        /// <summary>
        /// Builds the Bug-19 fixture at the given anchor: a superflat stone floor plus a shadow-casting
        /// stone box spanning the center chunk's -X/-Z seams (so cross-chunk sunlight interaction exists
        /// at the routing-sensitive borders). Heightmaps recalculated; returned un-lit.
        /// </summary>
        /// <param name="anchorChunk">World chunk coordinate of grid cell (0,0).</param>
        /// <returns>A freshly-built, un-lit test world.</returns>
        private static LightingTestWorld BuildBug19World(Vector2Int anchorChunk)
        {
            LightingTestWorld world = new LightingTestWorld(BUG19_GRID, anchorChunk: anchorChunk);
            world.FillSuperflatFloor(BUG19_FLOOR_Y, TestBlockPalette.Stone);

            // An 8×8 box straddling the (0,0)/(1,1) seam corner: grid-relative voxels 12..19 on both axes.
            Vector2Int baseOrigin = world.GridToVoxelOrigin(Vector2Int.zero);
            world.FillBox(
                new Vector3Int(baseOrigin.x + 12, BUG19_BOX_Y, baseOrigin.y + 12),
                new Vector3Int(baseOrigin.x + 19, BUG19_BOX_Y, baseOrigin.y + 19),
                TestBlockPalette.Stone);

            world.RecalculateHeightmaps();
            return world;
        }
    }
}
