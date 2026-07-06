using System;
using Editor.Validation.Lighting.Framework;
using UnityEditor;
using UnityEngine;
using Random = System.Random;

namespace Editor.Validation.Lighting
{
    /// <summary>
    /// Property-based geometry fuzz layer for the Bug-09 cross-chunk family (fidelity finding C1). The
    /// retired Bug-09 repro fleet and the surviving seed-sweep baselines (B26–B29) all pin ONE geometry —
    /// a source on the (1,1)/(2,1) +X border — and let the seed permute only completion/scheduling order;
    /// B3 (WONTFIX) established that synchronous order-permutation is exhausted for Bug 09. This layer
    /// pivots the search budget to the untested axis: it randomizes the GEOMETRY (which of the four faces
    /// or four corners the source sits on, the source block, the filler block, which chunk is held
    /// in-flight, and the per-frame job budget) as a pure function of the seed, reusing
    /// <see cref="LightingFrameSimulator.FindFailingSeed(System.Func{int,LightingTestWorld},System.Func{LightingTestWorld,LightingFrameSimulator,int,bool},int,int)"/>
    /// so any failing seed reproduces the exact case (geometry + ordering).
    /// <para>
    /// Each case asserts convergence to the borderless <see cref="LightingOracle"/> — trustworthy under
    /// arbitrary geometry now that finding A4 added independent hand-derived probes (B35–B39). A failure
    /// is therefore an engine/harness cross-chunk defect, not an oracle artifact.
    /// </para>
    /// </summary>
    public static partial class LightingValidationSuite
    {
        /// <summary>Interactive (per-suite) seed count — fast enough to run on every suite invocation.</summary>
        private const int BUG09_FUZZ_BASELINE_ITERATIONS = 50;

        /// <summary>Nightly seed count for the dedicated menu item — explores far more of the state space.</summary>
        private const int BUG09_FUZZ_NIGHTLY_ITERATIONS = 2000;

        /// <summary>
        /// One randomized Bug-09 cross-chunk scenario, fully determined by its seed. Holds the source
        /// placement/border, the source and filler blocks, the emitting/target/held chunks, and the job
        /// budget. Reconstruct with <see cref="FromSeed"/> from the same seed to get an identical case.
        /// </summary>
        private readonly struct Bug09FuzzCase
        {
            public readonly Vector3Int SourcePos;
            public readonly ushort SourceBlock;
            public readonly ushort FillerBlock;
            public readonly Vector2Int EmittingChunk;
            public readonly Vector2Int TargetChunk;
            public readonly Vector2Int HeldChunk;
            public readonly int Budget;
            public readonly string BorderName;

            private Bug09FuzzCase(Vector3Int sourcePos, ushort sourceBlock, ushort fillerBlock,
                Vector2Int emittingChunk, Vector2Int targetChunk, Vector2Int heldChunk, int budget, string borderName)
            {
                SourcePos = sourcePos;
                SourceBlock = sourceBlock;
                FillerBlock = fillerBlock;
                EmittingChunk = emittingChunk;
                TargetChunk = targetChunk;
                HeldChunk = heldChunk;
                Budget = budget;
                BorderName = borderName;
            }

            /// <summary>
            /// Builds the case deterministically from a seed. The RNG stream is decorrelated from the
            /// simulator's ordering RNG (which is seeded with the raw seed) by a fixed hash, so geometry
            /// and ordering vary independently while both stay a pure function of the seed.
            /// </summary>
            /// <param name="seed">The iteration seed.</param>
            /// <returns>The fully-specified case.</returns>
            public static Bug09FuzzCase FromSeed(int seed)
            {
                Random rng = new Random(unchecked(seed * 0x27D4EB2D + 1));

                // Emit from the interior chunk so all four faces and all four diagonal neighbors exist.
                Vector2Int emitting = new Vector2Int(1, 1);
                int perpendicular = 1 + rng.Next(14); // 1..14 along a cardinal border (avoid the corners)

                int lx, lz;
                Vector2Int delta;
                string border;
                switch (rng.Next(8))
                {
                    case 0:
                        lx = 15;
                        lz = perpendicular;
                        delta = new Vector2Int(1, 0);
                        border = "+X face";
                        break;
                    case 1:
                        lx = 0;
                        lz = perpendicular;
                        delta = new Vector2Int(-1, 0);
                        border = "-X face";
                        break;
                    case 2:
                        lx = perpendicular;
                        lz = 15;
                        delta = new Vector2Int(0, 1);
                        border = "+Z face";
                        break;
                    case 3:
                        lx = perpendicular;
                        lz = 0;
                        delta = new Vector2Int(0, -1);
                        border = "-Z face";
                        break;
                    case 4:
                        lx = 15;
                        lz = 15;
                        delta = new Vector2Int(1, 1);
                        border = "+X+Z corner";
                        break;
                    case 5:
                        lx = 15;
                        lz = 0;
                        delta = new Vector2Int(1, -1);
                        border = "+X-Z corner";
                        break;
                    case 6:
                        lx = 0;
                        lz = 15;
                        delta = new Vector2Int(-1, 1);
                        border = "-X+Z corner";
                        break;
                    default:
                        lx = 0;
                        lz = 0;
                        delta = new Vector2Int(-1, -1);
                        border = "-X-Z corner";
                        break;
                }

                Vector2Int target = emitting + delta;

                ushort[] sources =
                {
                    TestBlockPalette.LampWhite, TestBlockPalette.LampRed, TestBlockPalette.LampGreen,
                    TestBlockPalette.LampBlue, TestBlockPalette.Torch,
                };
                ushort source = sources[rng.Next(sources.Length)];

                // Non-air fillers only, so the "replace the broken source" step is always meaningful
                // (placing Air where the break already left Air would be a no-op).
                ushort[] fillers = { TestBlockPalette.Water, TestBlockPalette.Glass, TestBlockPalette.DimGlass };
                ushort filler = fillers[rng.Next(fillers.Length)];

                // Hold either the emitting chunk or the (possibly diagonal) target chunk in flight.
                Vector2Int held = rng.Next(2) == 0 ? emitting : target;

                int[] budgets = { 1, 2, int.MaxValue };
                int budget = budgets[rng.Next(budgets.Length)];

                Vector3Int worldPos = new Vector3Int(
                    emitting.x * VoxelData.ChunkWidth + lx, 11, emitting.y * VoxelData.ChunkWidth + lz);

                return new Bug09FuzzCase(worldPos, source, filler, emitting, target, held, budget, border);
            }

            /// <summary>Human-readable one-line description for failure logging / reproduction.</summary>
            public string Describe() =>
                $"source={BlockName(SourceBlock)} @ {SourcePos} ({BorderName}, emitting {EmittingChunk} -> target {TargetChunk}), " +
                $"filler={BlockName(FillerBlock)}, held-in-flight={HeldChunk}, " +
                $"budget={(Budget == int.MaxValue ? "unlimited" : Budget.ToString())}";

            private static string BlockName(ushort id) => id switch
            {
                TestBlockPalette.LampWhite => "LampWhite",
                TestBlockPalette.LampRed => "LampRed",
                TestBlockPalette.LampGreen => "LampGreen",
                TestBlockPalette.LampBlue => "LampBlue",
                TestBlockPalette.Torch => "Torch",
                TestBlockPalette.Water => "Water",
                TestBlockPalette.Glass => "Glass",
                TestBlockPalette.DimGlass => "DimGlass",
                _ => $"#{id}",
            };
        }

        /// <summary>
        /// B40 (C1): runs <see cref="BUG09_FUZZ_BASELINE_ITERATIONS"/> randomized cross-chunk geometries
        /// and fails if any seed fails to converge to the oracle. The dedicated
        /// <c>Validate Lighting Engine (Bug 09 Geometry Fuzz)</c> menu item runs far more seeds nightly.
        /// </summary>
        private static bool Baseline_Bug09GeometryFuzz()
        {
            int? failingSeed = SweepBug09Fuzz(BUG09_FUZZ_BASELINE_ITERATIONS, startSeed: 0);
            return LightingAssert.IsTrue(!failingSeed.HasValue,
                $"B40: all {BUG09_FUZZ_BASELINE_ITERATIONS} Bug-09 geometry-fuzz seeds converge to the oracle",
                failingSeed.HasValue
                    ? $"REGRESSION: seed {failingSeed.Value} mismatches the oracle — {Bug09FuzzCase.FromSeed(failingSeed.Value).Describe()}"
                    : "");
        }

        /// <summary>
        /// Nightly deep run of the Bug-09 geometry fuzz (kept off the interactive suite so it stays fast).
        /// Logs the first failing seed's full case for reproduction, or a green all-pass summary.
        /// </summary>
        [MenuItem("Minecraft Clone/Dev/Validate Lighting Engine (Bug 09 Geometry Fuzz)")]
        public static void RunBug09GeometryFuzz()
        {
            Debug.Log($"--- Bug 09 geometry fuzz: {BUG09_FUZZ_NIGHTLY_ITERATIONS} randomized cross-chunk seeds ---");
            int? failingSeed = SweepBug09Fuzz(BUG09_FUZZ_NIGHTLY_ITERATIONS, startSeed: 0);

            if (!failingSeed.HasValue)
            {
                Debug.Log($"<color=green>Bug 09 geometry fuzz PASSED — all {BUG09_FUZZ_NIGHTLY_ITERATIONS} seeds converge to the oracle.</color>");
            }
            else
            {
                Debug.LogError(
                    $"<color=red>Bug 09 geometry fuzz FOUND A FAILURE at seed {failingSeed.Value}.</color>\n" +
                    $"Case: {Bug09FuzzCase.FromSeed(failingSeed.Value).Describe()}\n" +
                    "Re-run this exact seed to reproduce — geometry and ordering are both a pure function of the seed.");
            }
        }

        /// <summary>
        /// Sweeps <paramref name="iterations"/> seeds of the Bug-09 geometry fuzz, returning the first
        /// failing seed (or null if all converge). Geometry and ordering both derive from the seed, so a
        /// returned seed is a complete, deterministic reproduction.
        /// </summary>
        /// <param name="iterations">Number of seeds to try.</param>
        /// <param name="startSeed">First seed value.</param>
        /// <param name="schedulerMode">Run each iteration's simulator in AS-2 scheduler mode (MT-2 ready/waiting
        /// split + event promotion) instead of the legacy full grid scan. Default false.</param>
        /// <returns>The first failing seed, or null when every seed converges to the oracle.</returns>
        private static int? SweepBug09Fuzz(int iterations, int startSeed, bool schedulerMode = false)
        {
            return LightingFrameSimulator.FindFailingSeed(
                seededWorldFactory: seed => BuildBug09FuzzWorld(Bug09FuzzCase.FromSeed(seed)),
                scenarioBody: (world, sim, seed) => RunBug09FuzzCase(world, sim, Bug09FuzzCase.FromSeed(seed)),
                iterations: iterations,
                startSeed: startSeed,
                schedulerMode: schedulerMode);
        }

        /// <summary>
        /// Builds the steady-state world for a fuzz case: a 3×3 grid with a stone floor, fully lit, with
        /// the emissive source already placed and converged. The dynamic break/replace edits happen in
        /// <see cref="RunBug09FuzzCase"/>.
        /// </summary>
        /// <param name="fuzz">The case to build.</param>
        /// <returns>The fully set-up test world.</returns>
        private static LightingTestWorld BuildBug09FuzzWorld(Bug09FuzzCase fuzz)
        {
            LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(10, TestBlockPalette.Stone);
            world.RecalculateHeightmaps();
            world.RunInitialLighting();

            world.PlaceBlock(fuzz.SourcePos, fuzz.SourceBlock);
            world.RunToConvergence();
            return world;
        }

        /// <summary>
        /// Runs one fuzz case under shuffled completion/scheduling order: break the source (holding the
        /// chosen chunk's flight), drop in a filler, re-emit the source, then release and converge.
        /// Asserts the converged field matches the borderless oracle (quietly — no per-iteration PASS log).
        /// </summary>
        /// <param name="world">The fuzz world from <see cref="BuildBug09FuzzWorld"/>.</param>
        /// <param name="sim">The seeded frame simulator.</param>
        /// <param name="fuzz">The case being run.</param>
        /// <returns>True when the engine converges to the oracle; false on non-convergence or mismatch.</returns>
        private static bool RunBug09FuzzCase(LightingTestWorld world, LightingFrameSimulator sim, Bug09FuzzCase fuzz)
        {
            const LightingFrameSimulator.CompletionOrder shuffled = LightingFrameSimulator.CompletionOrder.Shuffled;
            Func<LightingTestWorld.LightingJobFlight, int, bool> hold =
                LightingFrameSimulator.ExceptChunks(fuzz.HeldChunk);

            world.BreakBlock(fuzz.SourcePos);
            sim.RunFrame(budget: fuzz.Budget, order: shuffled, completionPredicate: hold);

            world.PlaceBlock(fuzz.SourcePos, fuzz.FillerBlock);
            sim.RunFrame(budget: fuzz.Budget, order: shuffled, completionPredicate: hold);

            world.PlaceBlock(fuzz.SourcePos, fuzz.SourceBlock);
            sim.RunFrame(budget: fuzz.Budget, order: shuffled);

            int frames = sim.RunToConvergence(budgetPerFrame: fuzz.Budget, order: shuffled);
            if (frames < 0)
                return false;

            return LightingAssert.MatchesOracle(world, LightingOracle.Solve(world),
                $"Bug09 fuzz @ {fuzz.SourcePos} ({fuzz.BorderName})", logPass: false);
        }
    }
}
