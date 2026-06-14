using System.Text;
using Editor.Validation.Lighting.Framework;
using UnityEditor;
using UnityEngine;
using Random = System.Random;

namespace Editor.Validation.Lighting
{
    /// <summary>
    /// Property-based geometry fuzz layer for Bug 05 ("persistent chunk-border shadow patches in dense
    /// biomes" — fidelity finding C2). The minimal Bug-05 repro attempt (B8: a single diagonal sky well
    /// under a full slab) converges to the oracle in the production two edge-check rounds, so it guards as
    /// a baseline instead of reproducing the bug. The bug entry names the untested case: <b>dense,
    /// multi-pocket canopies</b> whose under-canopy pockets depend on light paths that cross several chunk
    /// borders — where <c>CheckEdges</c> (cardinal borders only) plus <c>RemainingEdgeCheckRounds = 2</c>
    /// may run out of rounds before a pocket is reconciled.
    /// <para>
    /// This layer randomizes the GEOMETRY as a pure function of the seed — canopy height/thickness, the
    /// number and placement of sky wells, and the under-canopy opaque dividers that partition the space
    /// into pockets and force winding cross-chunk paths — then runs the production-faithful wave-parallel
    /// initial lighting (<see cref="LightingTestWorld.RunInitialLightingParallel"/>: one concurrent wave of
    /// column recalcs from stale neighbor snapshots, then the two edge-check rounds) and asserts the
    /// converged field matches the borderless <see cref="LightingOracle"/>. The oracle is trustworthy under
    /// arbitrary geometry now that finding A4 added independent hand-derived probes (B35–B39), so a
    /// mismatch is an engine round-budget defect (Bug 05), not an oracle artifact.
    /// </para>
    /// <para>
    /// Diagnosis aid: when a seed fails the production-rounds run, the sweep re-runs it with extra forced
    /// edge-check rounds (<see cref="LightingTestWorld.RunInitialLightingParallelForcedEdgeRounds"/>). If
    /// the field then converges, the failure is specifically the 2-round budget (the Bug-05 mechanism) and
    /// not a genuinely unreachable pocket (which would be dark in the oracle too).
    /// </para>
    /// </summary>
    public static partial class LightingValidationSuite
    {
        /// <summary>Interactive (per-suite) seed count — initial lighting over 25 chunks is heavier than the Bug-09 edits, so this stays small.</summary>
        private const int BUG05_CANOPY_BASELINE_ITERATIONS = 20;

        /// <summary>Nightly seed count for the dedicated menu item — explores far more of the geometry space.</summary>
        private const int BUG05_CANOPY_NIGHTLY_ITERATIONS = 200;

        /// <summary>Forced edge-check rounds used to diagnose a failing seed (well above production's 2).</summary>
        private const int BUG05_CANOPY_DIAGNOSIS_ROUNDS = 8;

        /// <summary>The test grid edge length (chunks per axis) — 5 gives room for multi-chunk diagonal light paths.</summary>
        private const int BUG05_CANOPY_GRID = 5;

        /// <summary>Y of the stone floor's top surface; the under-canopy air gap starts at <c>FLOOR_TOP + 1</c>.</summary>
        private const int BUG05_CANOPY_FLOOR_TOP = 10;

        /// <summary>
        /// One randomized dense-canopy world, fully determined by its seed: the canopy geometry, the sky
        /// wells that admit light under it, and the opaque under-canopy dividers that carve it into pockets.
        /// Reconstruct with <see cref="FromSeed"/> from the same seed to get an identical case.
        /// </summary>
        private readonly struct Bug05CanopyCase
        {
            /// <summary>Y of the lowest canopy (leaves) layer; the air gap is <c>[FloorTop+1, CanopyBaseY-1]</c>.</summary>
            public readonly int CanopyBaseY;

            /// <summary>Number of stacked leaves layers forming the canopy.</summary>
            public readonly int CanopyThickness;

            /// <summary>Columns punched clear of canopy (air up to the sky) so vertical sunlight reaches the gap.</summary>
            public readonly Vector2Int[] Wells;

            /// <summary>Opaque under-canopy dividers (stone walls spanning the gap height) that force winding paths.</summary>
            public readonly Wall[] Walls;

            private Bug05CanopyCase(int canopyBaseY, int canopyThickness, Vector2Int[] wells, Wall[] walls)
            {
                CanopyBaseY = canopyBaseY;
                CanopyThickness = canopyThickness;
                Wells = wells;
                Walls = walls;
            }

            /// <summary>The inclusive top Y of the air gap (one voxel below the canopy base).</summary>
            public int GapTopY => CanopyBaseY - 1;

            /// <summary>An opaque under-canopy divider: a full-gap-height stone wall along one axis with a single doorway.</summary>
            public readonly struct Wall
            {
                /// <summary>True when the wall runs along Z at a fixed X; false when it runs along X at a fixed Z.</summary>
                public readonly bool IsXWall;

                /// <summary>The fixed coordinate the wall sits on (X for an X-wall, Z otherwise).</summary>
                public readonly int Fixed;

                /// <summary>The single open voxel (doorway) along the wall's length.</summary>
                public readonly int Doorway;

                public Wall(bool isXWall, int fixedCoord, int doorway)
                {
                    IsXWall = isXWall;
                    Fixed = fixedCoord;
                    Doorway = doorway;
                }
            }

            /// <summary>
            /// Builds the case deterministically from a seed. The RNG stream is decorrelated by a fixed
            /// hash so successive seeds produce visibly different geometries.
            /// </summary>
            /// <param name="seed">The iteration seed.</param>
            /// <returns>The fully-specified case.</returns>
            public static Bug05CanopyCase FromSeed(int seed)
            {
                Random rng = new Random(unchecked(seed * 0x27D4EB2D + 1));
                const int worldWidth = BUG05_CANOPY_GRID * VoxelData.ChunkWidth;

                // Air gap of 1..4 voxels under the canopy.
                int gapHeight = 1 + rng.Next(4);
                int canopyBaseY = BUG05_CANOPY_FLOOR_TOP + 1 + gapHeight;
                int canopyThickness = 1 + rng.Next(3);

                // 1..3 sky wells at random columns (the only under-canopy light sources).
                int wellCount = 1 + rng.Next(3);
                Vector2Int[] wells = new Vector2Int[wellCount];
                for (int i = 0; i < wellCount; i++)
                    wells[i] = new Vector2Int(rng.Next(worldWidth), rng.Next(worldWidth));

                // 0..8 opaque dividers, each a full-gap-height wall with one doorway — the maze that forces
                // under-canopy light to thread across chunk borders.
                int wallCount = rng.Next(9);
                Wall[] walls = new Wall[wallCount];
                for (int i = 0; i < wallCount; i++)
                {
                    bool isXWall = rng.Next(2) == 0;
                    int fixedCoord = 1 + rng.Next(worldWidth - 2);
                    int doorway = rng.Next(worldWidth);
                    walls[i] = new Wall(isXWall, fixedCoord, doorway);
                }

                return new Bug05CanopyCase(canopyBaseY, canopyThickness, wells, walls);
            }

            /// <summary>Human-readable one-line description for failure logging / reproduction.</summary>
            public string Describe()
            {
                StringBuilder sb = new StringBuilder();
                sb.Append($"canopy y={CanopyBaseY}..{CanopyBaseY + CanopyThickness - 1} (gap y={BUG05_CANOPY_FLOOR_TOP + 1}..{GapTopY}), ");
                sb.Append("wells=[");
                for (int i = 0; i < Wells.Length; i++)
                    sb.Append(i == 0 ? $"({Wells[i].x},{Wells[i].y})" : $", ({Wells[i].x},{Wells[i].y})");
                sb.Append($"], walls={Walls.Length}");
                return sb.ToString();
            }
        }

        /// <summary>
        /// B42 (C2): runs <see cref="BUG05_CANOPY_BASELINE_ITERATIONS"/> randomized dense-canopy generations
        /// and fails if any seed's wave-parallel initial lighting diverges from the oracle. The Bug-05
        /// shadow mechanism never reproduced synchronously (in-range 6-connected paths reconcile within the
        /// production two edge-check rounds); the fuzz's actual catch was Bug 10 (opaque-border light leak),
        /// now fixed. With that fix in, all seeds converge — so this guards dense-canopy generation
        /// convergence as broad regression coverage (the C2 "won't-reproduce → baseline" outcome). The
        /// dedicated <c>Validate Lighting Engine (Bug 05 Canopy Fuzz)</c> menu item runs far more seeds nightly.
        /// </summary>
        private static bool Baseline_Bug05CanopyFuzz()
        {
            int? failingSeed = SweepBug05Canopy(BUG05_CANOPY_BASELINE_ITERATIONS, startSeed: 0);
            return LightingAssert.IsTrue(!failingSeed.HasValue,
                $"B42: all {BUG05_CANOPY_BASELINE_ITERATIONS} dense-canopy seeds converge to the oracle",
                failingSeed.HasValue
                    ? $"seed {failingSeed.Value} diverges from the oracle — {Bug05CanopyCase.FromSeed(failingSeed.Value).Describe()}"
                    : "");
        }

        /// <summary>
        /// Nightly deep run of the Bug-05 dense-canopy geometry fuzz (kept off the interactive suite so it
        /// stays fast). Logs the first failing seed's full case plus whether extra edge rounds resolve it
        /// (confirming the round-budget mechanism), or a green all-pass summary.
        /// </summary>
        [MenuItem("Minecraft Clone/Dev/Validate Lighting Engine (Bug 05 Canopy Fuzz)")]
        public static void RunBug05CanopyFuzz()
        {
            Debug.Log($"--- Bug 05 dense-canopy fuzz: {BUG05_CANOPY_NIGHTLY_ITERATIONS} randomized canopy seeds ---");
            int? failingSeed = SweepBug05Canopy(BUG05_CANOPY_NIGHTLY_ITERATIONS, startSeed: 0);

            if (!failingSeed.HasValue)
            {
                Debug.Log($"<color=green>Bug 05 canopy fuzz: all {BUG05_CANOPY_NIGHTLY_ITERATIONS} seeds converge to the oracle within the production 2 edge-check rounds.</color>\n" +
                          "No synchronous repro found — consistent with the analysis that in-range light paths reconcile within 2 rounds (see LIGHTING_VALIDATION_HARNESS_FIDELITY.md, finding C2).");
                return;
            }

            Bug05CanopyCase failing = Bug05CanopyCase.FromSeed(failingSeed.Value);
            bool resolvesWithMoreRounds = ConvergesWithForcedRounds(failing, BUG05_CANOPY_DIAGNOSIS_ROUNDS);
            Debug.LogError(
                $"<color=red>Bug 05 canopy fuzz FOUND A FAILURE at seed {failingSeed.Value}.</color>\n" +
                $"Case: {failing.Describe()}\n" +
                (resolvesWithMoreRounds
                    ? $"DIAGNOSIS: the field converges to the oracle with {BUG05_CANOPY_DIAGNOSIS_ROUNDS} forced edge-check rounds — this IS the Bug-05 round-budget defect (cardinal-only CheckEdges + 2 rounds insufficient)."
                    : $"DIAGNOSIS: still mismatches with {BUG05_CANOPY_DIAGNOSIS_ROUNDS} forced edge-check rounds — the pocket may be genuinely unreachable (oracle artifact / different bug); inspect before fixing.") +
                "\nRe-run this exact seed to reproduce — the geometry is a pure function of the seed.");
        }

        /// <summary>
        /// Sweeps <paramref name="iterations"/> dense-canopy seeds, returning the first seed whose
        /// production-rounds initial lighting fails to match the oracle (or null if all converge).
        /// </summary>
        /// <param name="iterations">Number of seeds to try.</param>
        /// <param name="startSeed">First seed value.</param>
        /// <returns>The first failing seed, or null when every seed converges to the oracle.</returns>
        private static int? SweepBug05Canopy(int iterations, int startSeed)
        {
            for (int i = 0; i < iterations; i++)
            {
                int seed = startSeed + i;
                Bug05CanopyCase canopyCase = Bug05CanopyCase.FromSeed(seed);
                using LightingTestWorld world = BuildBug05CanopyWorld(canopyCase);

                int rounds = world.RunInitialLightingParallel();
                if (rounds < 0)
                    return seed; // non-convergence (ping-pong) is itself a failure

                if (!LightingAssert.MatchesOracle(world, LightingOracle.Solve(world),
                        $"Bug05 canopy seed {seed}", logPass: false))
                    return seed;
            }

            return null;
        }

        /// <summary>
        /// Re-runs a case with a forced edge-check round count well above production's two, and reports
        /// whether the field then matches the oracle — the diagnostic that distinguishes a round-budget
        /// shortfall (Bug 05) from a genuinely unreachable pocket.
        /// </summary>
        /// <param name="canopyCase">The case to diagnose.</param>
        /// <param name="forcedRounds">The number of edge-check rounds to force.</param>
        /// <returns>True when the field converges to the oracle with the forced rounds.</returns>
        private static bool ConvergesWithForcedRounds(Bug05CanopyCase canopyCase, int forcedRounds)
        {
            using LightingTestWorld world = BuildBug05CanopyWorld(canopyCase);
            int rounds = world.RunInitialLightingParallelForcedEdgeRounds(forcedRounds);
            if (rounds < 0) return false;
            return LightingAssert.MatchesOracle(world, LightingOracle.Solve(world),
                $"Bug05 canopy diagnosis ({forcedRounds} forced rounds)", logPass: false);
        }

        /// <summary>
        /// Builds the dense-canopy world for a case: a stone floor, a leaves canopy over the whole grid,
        /// opaque under-canopy dividers, and sky wells punched clear of the canopy. The caller runs initial
        /// lighting and compares to the oracle.
        /// </summary>
        /// <param name="canopyCase">The case to build.</param>
        /// <returns>The fully authored test world (heightmaps recalculated, lighting NOT yet run).</returns>
        private static LightingTestWorld BuildBug05CanopyWorld(Bug05CanopyCase canopyCase)
        {
            LightingTestWorld world = new LightingTestWorld(BUG05_CANOPY_GRID);
            const int worldWidth = BUG05_CANOPY_GRID * VoxelData.ChunkWidth;
            const int worldMax = worldWidth - 1;

            world.FillSuperflatFloor(BUG05_CANOPY_FLOOR_TOP, TestBlockPalette.Stone);

            // Canopy: stacked leaves layers over the whole grid (opacity-1 foliage, the Bug-05 material).
            int canopyTopY = canopyCase.CanopyBaseY + canopyCase.CanopyThickness - 1;
            world.FillBox(new Vector3Int(0, canopyCase.CanopyBaseY, 0),
                new Vector3Int(worldMax, canopyTopY, worldMax), TestBlockPalette.Leaves);

            // Opaque under-canopy dividers: full-gap-height stone walls with a single doorway each.
            const int gapBottomY = BUG05_CANOPY_FLOOR_TOP + 1;
            foreach (Bug05CanopyCase.Wall wall in canopyCase.Walls)
            {
                for (int along = 0; along < worldWidth; along++)
                {
                    if (along == wall.Doorway) continue; // leave the doorway open
                    for (int y = gapBottomY; y <= canopyCase.GapTopY; y++)
                    {
                        Vector3Int pos = wall.IsXWall
                            ? new Vector3Int(wall.Fixed, y, along)
                            : new Vector3Int(along, y, wall.Fixed);
                        world.SetBlock(pos, TestBlockPalette.Stone);
                    }
                }
            }

            // Sky wells: clear the canopy (and everything above the gap) at the well columns so vertical
            // sunlight reaches the gap. Cleared last so a well always wins over a divider at the same column.
            foreach (Vector2Int well in canopyCase.Wells)
            {
                for (int y = gapBottomY; y < VoxelData.ChunkHeight; y++)
                    world.SetBlock(new Vector3Int(well.x, y, well.y), TestBlockPalette.Air);
            }

            world.RecalculateHeightmaps();
            return world;
        }
    }
}
