using System.Text;
using Editor.Validation.Lighting.Framework;
using UnityEditor;
using UnityEngine;
using Random = System.Random;

namespace Editor.Validation.Lighting
{
    /// <summary>
    /// Property-based border-heightmap geometry fuzz (roadmap item HF-3, extending fidelity finding C9:
    /// flat scenario worlds never exercise border shadow-casters — the column-recalc branch that seeds
    /// darkness nodes CROSS-BORDER fired at essentially no seam in the pre-B60 corpus, while real
    /// terrain hits it constantly). This layer generates, as a pure function of the seed, the terrain
    /// shape flat worlds never produce: per-column random heights (every cross-seam column pair is a
    /// height step), seam overhangs whose tip touches a chunk boundary (the B60 geometry, generalized),
    /// and border edits on both sides of the seams — then asserts the production wave-parallel initial
    /// lighting and the post-edit pipeline both terminate on the borderless <see cref="LightingOracle"/>.
    /// <para>
    /// With the fail-fast <c>ChunkData</c> accessor assertions in place (fidelity A5 / roadmap HF-1),
    /// this fuzz doubles as a <b>crash-class detector</b>: every out-of-bounds local position an engine
    /// defect produces at any of the sampled seam geometries throws loudly instead of wrong-reading —
    /// the "position lottery" that let B60's original sabotage stay green cannot silently swallow a
    /// violation here. HF-3 was deliberately built after HF-1 for exactly this reason.
    /// </para>
    /// <para>
    /// Tiered like the C1/C2 fuzz layers: <b>K15a</b> sweeps a small seed count on every suite run; the
    /// dedicated <c>Validate Lighting Engine (Border Height Fuzz)</c> menu item sweeps far more seeds
    /// nightly. Every red is classified before it is reported (forced-edge-rounds classifier for
    /// generation-wave failures, the oscillation probe for post-edit non-termination), and a failing
    /// seed reproduces its exact geometry AND scheduling order deterministically.
    /// </para>
    /// <para>
    /// Registered as known-bug repro <b>K15a</b> (expected red): the fuzz's very first seed reproduced
    /// <b>Bug 15</b> — the cross-chunk sunlight surface stamp on opaque seam-face voxels is permanently
    /// wiped by a same-column border edit's sunlight recalculation (see
    /// <c>Documentation/Bugs/LIGHTING_BUGS.md</c>). After the fix is confirmed in-game, promote to
    /// baseline <b>B62</b> and flip <see cref="BORDER_FUZZ_EXPECTED_RED"/>.
    /// </para>
    /// </summary>
    public static partial class LightingValidationSuite
    {
        /// <summary>Interactive (per-suite) seed count — each seed lights a full varied-heightmap grid twice, so this stays modest.</summary>
        private const int BORDER_FUZZ_BASELINE_ITERATIONS = 25;

        /// <summary>Nightly seed count for the dedicated menu item — explores far more of the seam-geometry space.</summary>
        private const int BORDER_FUZZ_NIGHTLY_ITERATIONS = 200;

        /// <summary>The test grid edge length (chunks per axis) — 3 exercises all internal seam segments and corners at the lowest per-seed cost.</summary>
        private const int BORDER_FUZZ_GRID = 3;

        /// <summary>Inclusive lower bound of the per-column terrain height band.</summary>
        private const int BORDER_FUZZ_MIN_HEIGHT = 8;

        /// <summary>Inclusive upper bound of the per-column terrain height band.</summary>
        private const int BORDER_FUZZ_MAX_HEIGHT = 46;

        /// <summary>Generous wave budget so a red means "did not terminate", not "was slow" (suite default is 16).</summary>
        private const int BORDER_FUZZ_WAVE_MAX_ROUNDS = 64;

        /// <summary>Forced edge-check rounds used to classify a failing seed (production caps at 2; the C2 classifier).</summary>
        private const int BORDER_FUZZ_DIAGNOSIS_ROUNDS = 8;

        /// <summary>The frame budget for post-edit convergence under the seeded simulator schedule.</summary>
        private const int BORDER_FUZZ_SIM_MAX_FRAMES = 500;

        /// <summary>How far an overhang strip extends away from the seam its tip touches.</summary>
        private const int BORDER_FUZZ_OVERHANG_LENGTH = 3;

        /// <summary>Minimum air gap between an overhang and the tallest terrain column under or beside it.</summary>
        private const int BORDER_FUZZ_OVERHANG_MIN_GAP = 2;

        /// <summary>
        /// Failure-reporting mode: true while the fuzz is the expected-red Bug-15 repro K15a (failures
        /// log as <c>[EXPECTED-RED]</c> warnings), false once promoted to baseline B62 (failures are
        /// regressions and log as <c>[FAIL]</c> errors).
        /// </summary>
        private const bool BORDER_FUZZ_EXPECTED_RED = true;

        /// <summary>
        /// One randomized border-heightmap world, fully determined by its seed: the per-column terrain
        /// heights, the seam overhangs, and the border-edit sequence. Reconstruct with
        /// <see cref="FromSeed"/> from the same seed to get an identical case.
        /// </summary>
        private readonly struct BorderHeightFuzzCase
        {
            /// <summary>Terrain surface height per world column, indexed <c>x + worldWidth * z</c> (solid stone from y=0 up to and including this).</summary>
            public readonly int[] Heights;

            /// <summary>Overhang strips whose tip touches an internal chunk seam.</summary>
            public readonly Overhang[] Overhangs;

            /// <summary>Player-style border edits applied after initial convergence (Edit 0 is the B60-shaped companion of Overhang 0).</summary>
            public readonly Edit[] Edits;

            private BorderHeightFuzzCase(int[] heights, Overhang[] overhangs, Edit[] edits)
            {
                Heights = heights;
                Overhangs = overhangs;
                Edits = edits;
            }

            /// <summary>
            /// A 1-wide horizontal stone strip perpendicular to an internal seam, its tip in the seam's
            /// border column — so the air voxel under the tip sits at a chunk boundary with an
            /// obstruction above it (the shadow-caster branch's precondition, generalized from B60).
            /// </summary>
            public readonly struct Overhang
            {
                /// <summary>True when the seam is a constant-X plane (the strip runs along X); false for a constant-Z seam.</summary>
                public readonly bool IsXSeam;

                /// <summary>The first world coordinate on the high side of the seam (16 or 32 on a grid-3 world).</summary>
                public readonly int Boundary;

                /// <summary>The world coordinate along the seam the strip sits at.</summary>
                public readonly int Along;

                /// <summary>True when the strip lies on the low-coordinate side (tip at <c>Boundary - 1</c>); false for the high side (tip at <c>Boundary</c>).</summary>
                public readonly bool LowSide;

                /// <summary>The strip's Y (guaranteed above every terrain column under it and across the seam).</summary>
                public readonly int Y;

                public Overhang(bool isXSeam, int boundary, int along, bool lowSide, int y)
                {
                    IsXSeam = isXSeam;
                    Boundary = boundary;
                    Along = along;
                    LowSide = lowSide;
                    Y = y;
                }

                /// <summary>The world coordinate (on the strip's axis) of the tip column touching the seam.</summary>
                public int TipCoord => LowSide ? Boundary - 1 : Boundary;

                /// <summary>The world coordinate (on the strip's axis) of the border column across the seam from the tip.</summary>
                public int AcrossCoord => LowSide ? Boundary : Boundary - 1;

                /// <summary>The world position of the strip voxel at the given distance from the tip (0 = the tip itself).</summary>
                /// <param name="distanceFromTip">Voxels away from the seam, into the strip's own side.</param>
                public Vector3Int StripPos(int distanceFromTip)
                {
                    int coord = LowSide ? TipCoord - distanceFromTip : TipCoord + distanceFromTip;
                    return IsXSeam ? new Vector3Int(coord, Y, Along) : new Vector3Int(Along, Y, coord);
                }

                /// <summary>The world position of the across-seam companion edit (one voxel below the strip, across the boundary).</summary>
                public Vector3Int CompanionEditPos()
                {
                    return IsXSeam ? new Vector3Int(AcrossCoord, Y - 1, Along) : new Vector3Int(Along, Y - 1, AcrossCoord);
                }
            }

            /// <summary>One player-style edit in a border column: a stone placement above the terrain or a terrain-top break.</summary>
            public readonly struct Edit
            {
                /// <summary>The world position of the edit.</summary>
                public readonly Vector3Int Pos;

                /// <summary>True to place stone, false to break the block at <see cref="Pos"/>.</summary>
                public readonly bool IsPlace;

                public Edit(Vector3Int pos, bool isPlace)
                {
                    Pos = pos;
                    IsPlace = isPlace;
                }
            }

            /// <summary>
            /// Builds the case deterministically from a seed. The RNG stream is decorrelated by a fixed
            /// hash so successive seeds produce visibly different geometries.
            /// </summary>
            /// <param name="seed">The iteration seed.</param>
            /// <returns>The fully-specified case.</returns>
            public static BorderHeightFuzzCase FromSeed(int seed)
            {
                Random rng = new Random(unchecked(seed * unchecked((int)0x9E3779B1) + 7));
                const int worldWidth = BORDER_FUZZ_GRID * VoxelData.ChunkWidth;

                // Per-column random heights: plain (unsmoothed) random maximizes cross-seam height
                // steps — every seam column pair is the step geometry flat worlds never produce.
                int[] heights = new int[worldWidth * worldWidth];
                for (int i = 0; i < heights.Length; i++)
                    heights[i] = BORDER_FUZZ_MIN_HEIGHT + rng.Next(BORDER_FUZZ_MAX_HEIGHT - BORDER_FUZZ_MIN_HEIGHT + 1);

                // 2..4 seam overhangs, each guaranteed an air gap over its own strip columns AND the
                // across-seam column (so the companion edit lands in air, not inside terrain).
                int overhangCount = 2 + rng.Next(3);
                Overhang[] overhangs = new Overhang[overhangCount];
                for (int i = 0; i < overhangCount; i++)
                {
                    bool isXSeam = rng.Next(2) == 0;
                    int boundary = VoxelData.ChunkWidth * (1 + rng.Next(BORDER_FUZZ_GRID - 1));
                    int along = rng.Next(worldWidth);
                    bool lowSide = rng.Next(2) == 0;

                    Overhang candidate = new Overhang(isXSeam, boundary, along, lowSide, 0);
                    int tallest = ColumnHeight(heights, worldWidth, candidate.CompanionEditPos());
                    for (int d = 0; d < BORDER_FUZZ_OVERHANG_LENGTH; d++)
                        tallest = Mathf.Max(tallest, ColumnHeight(heights, worldWidth, candidate.StripPos(d)));

                    int y = tallest + BORDER_FUZZ_OVERHANG_MIN_GAP + rng.Next(4);
                    overhangs[i] = new Overhang(isXSeam, boundary, along, lowSide, y);
                }

                // 6..10 border edits. Edit 0 is the deterministic B60 companion: a stone placement
                // across the seam one voxel below Overhang 0's tip, whose column recalc runs the
                // shadow-caster check right next to the partially-lit under-tip voxel. The rest are
                // seeded placements/breaks in border columns on either side of a seeded seam.
                int editCount = 6 + rng.Next(5);
                Edit[] edits = new Edit[editCount];
                edits[0] = new Edit(overhangs[0].CompanionEditPos(), isPlace: true);
                for (int i = 1; i < editCount; i++)
                {
                    bool isXSeam = rng.Next(2) == 0;
                    int boundary = VoxelData.ChunkWidth * (1 + rng.Next(BORDER_FUZZ_GRID - 1));
                    int borderCoord = rng.Next(2) == 0 ? boundary - 1 : boundary;
                    int along = rng.Next(worldWidth);
                    Vector2Int column = isXSeam ? new Vector2Int(borderCoord, along) : new Vector2Int(along, borderCoord);

                    int surface = heights[column.x + worldWidth * column.y];
                    bool isPlace = rng.Next(2) == 0;
                    int y = isPlace ? surface + 1 + rng.Next(3) : surface;
                    edits[i] = new Edit(new Vector3Int(column.x, y, column.y), isPlace);
                }

                return new BorderHeightFuzzCase(heights, overhangs, edits);
            }

            /// <summary>The terrain surface height of the column containing <paramref name="pos"/>.</summary>
            private static int ColumnHeight(int[] heights, int worldWidth, Vector3Int pos)
            {
                return heights[pos.x + worldWidth * pos.z];
            }

            /// <summary>Human-readable one-line description for failure logging / reproduction.</summary>
            public string Describe()
            {
                StringBuilder sb = new StringBuilder();
                sb.Append($"heights={BORDER_FUZZ_MIN_HEIGHT}..{BORDER_FUZZ_MAX_HEIGHT} per-column random, overhangs=[");
                for (int i = 0; i < Overhangs.Length; i++)
                {
                    Overhang o = Overhangs[i];
                    sb.Append(i == 0 ? "" : ", ");
                    sb.Append($"({(o.IsXSeam ? "x" : "z")}-seam@{o.Boundary}, along={o.Along}, {(o.LowSide ? "low" : "high")}, y={o.Y})");
                }

                sb.Append("], edits=[");
                for (int i = 0; i < Edits.Length; i++)
                {
                    Edit e = Edits[i];
                    sb.Append(i == 0 ? "" : ", ");
                    sb.Append($"{(e.IsPlace ? "place" : "break")}@({e.Pos.x},{e.Pos.y},{e.Pos.z})");
                }

                sb.Append(']');
                return sb.ToString();
            }
        }

        /// <summary>
        /// K15a (HF-3): runs <see cref="BORDER_FUZZ_BASELINE_ITERATIONS"/> randomized border-heightmap
        /// seeds and fails if any seed's generation wave or post-edit pipeline fails to terminate on the
        /// borderless oracle. Under the HF-1 accessor assertions, any engine defect that produces an
        /// out-of-bounds local position at these seam geometries reds loudly here instead of
        /// wrong-reading. Currently expected red: seed 0 reproduces Bug 15's seam surface-stamp wipe.
        /// The dedicated <c>Validate Lighting Engine (Border Height Fuzz)</c> menu item runs far more
        /// seeds nightly.
        /// </summary>
        private static bool KnownBug_BorderHeightFuzz()
        {
            int? failingSeed = SweepBorderHeightFuzz(BORDER_FUZZ_BASELINE_ITERATIONS, startSeed: 0);
            if (!failingSeed.HasValue)
            {
                Debug.Log($"[PASS] K15a: all {BORDER_FUZZ_BASELINE_ITERATIONS} border-heightmap seeds settle on the oracle (generation wave + border edits)");
                return true;
            }

            return BorderFuzzFail(
                $"K15a: all {BORDER_FUZZ_BASELINE_ITERATIONS} border-heightmap seeds settle on the oracle (generation wave + border edits)",
                $"seed {failingSeed.Value} fails (details logged by that iteration) — {BorderHeightFuzzCase.FromSeed(failingSeed.Value).Describe()}");
        }

        /// <summary>
        /// Nightly deep run of the border-heightmap fuzz (kept off the interactive suite so it stays
        /// fast). The failing iteration logs its own phase-specific diagnosis (classifier / oscillation
        /// probe); this wrapper adds the case description and the reproduction pointer.
        /// </summary>
        [MenuItem("Minecraft Clone/Dev/Validate Lighting Engine (Border Height Fuzz)")]
        public static void RunBorderHeightFuzzNightly()
        {
            Debug.Log($"--- Border-heightmap fuzz: {BORDER_FUZZ_NIGHTLY_ITERATIONS} randomized seam-geometry seeds ---");
            int? failingSeed = SweepBorderHeightFuzz(BORDER_FUZZ_NIGHTLY_ITERATIONS, startSeed: 0);

            if (!failingSeed.HasValue)
            {
                Debug.Log($"<color=green>Border-heightmap fuzz: all {BORDER_FUZZ_NIGHTLY_ITERATIONS} seeds settle on the oracle.</color>\n" +
                          "Varied heights at every seam + seam overhangs + border edits all reconcile (see LIGHTING_VALIDATION_HARNESS_FIDELITY.md, finding C9).");
                return;
            }

            string report =
                $"Border-heightmap fuzz failed at seed {failingSeed.Value}.\n" +
                $"Case: {BorderHeightFuzzCase.FromSeed(failingSeed.Value).Describe()}\n" +
                "The failing iteration's entry above carries the phase and classifier/probe verdict. " +
                "Re-run this exact seed to reproduce — geometry AND scheduling order are pure functions of the seed.";
            if (BORDER_FUZZ_EXPECTED_RED)
                Debug.LogWarning($"{report}\nExpected while Bug 15 is open (see LIGHTING_BUGS.md).");
            else
                Debug.LogError($"<color=red>{report}</color>");
        }

        /// <summary>
        /// Sweeps <paramref name="iterations"/> border-heightmap seeds through the seeded
        /// world-factory overload of <see cref="LightingFrameSimulator.FindFailingSeed(System.Func{int,LightingTestWorld},System.Func{LightingTestWorld,LightingFrameSimulator,int,bool},int,int)"/>,
        /// so a returned failing seed reproduces geometry and scheduling order exactly.
        /// </summary>
        /// <param name="iterations">Number of seeds to try.</param>
        /// <param name="startSeed">First seed value.</param>
        /// <returns>The first failing seed, or null when every seed settles on the oracle.</returns>
        private static int? SweepBorderHeightFuzz(int iterations, int startSeed)
        {
            return LightingFrameSimulator.FindFailingSeed(
                seededWorldFactory: seed => BuildBorderHeightFuzzWorld(BorderHeightFuzzCase.FromSeed(seed)),
                scenarioBody: (world, sim, seed) => RunBorderHeightFuzzCase(world, sim,
                    BorderHeightFuzzCase.FromSeed(seed), seed),
                iterations: iterations,
                startSeed: startSeed);
        }

        /// <summary>
        /// One fuzz iteration: production wave-parallel initial lighting over the varied heightmap
        /// (termination + oracle), then the seeded border edits through the player-edit path,
        /// interleaved with simulator frames under a seed-derived budget and shuffled completion
        /// (the B59 discipline — edits land while neighbor jobs are in flight), then post-edit
        /// termination + oracle. Every red is classified before it is reported.
        /// </summary>
        /// <param name="world">The freshly-built (un-lit) case world.</param>
        /// <param name="sim">The seeded frame simulator driving the world.</param>
        /// <param name="fuzzCase">The case being run (for edits and failure descriptions).</param>
        /// <param name="seed">The iteration seed (labels and schedule derivation).</param>
        /// <returns>True when both phases terminate on the oracle.</returns>
        private static bool RunBorderHeightFuzzCase(LightingTestWorld world, LightingFrameSimulator sim,
            BorderHeightFuzzCase fuzzCase, int seed)
        {
            string label = $"K15a seed {seed}";
            int budget = 1 + seed % 4;
            int cadence = seed % 3;

            // --- Phase 1: generation wave over the varied heightmap ---
            int waves = world.RunInitialLightingParallel(BORDER_FUZZ_WAVE_MAX_ROUNDS);
            if (waves < 0)
            {
                return BorderFuzzFail($"{label}: generation-wave initial lighting terminates",
                    $"no convergence within {BORDER_FUZZ_WAVE_MAX_ROUNDS} waves. " +
                    ClassifyBorderFuzzWaveFailure(fuzzCase) + $" Case: {fuzzCase.Describe()}");
            }

            if (!LightingAssert.MatchesOracleQuiet(world, LightingOracle.Solve(world), out string waveSummary))
            {
                return BorderFuzzFail($"{label}: generation-wave field matches the borderless oracle",
                    $"{waveSummary}. " + ClassifyBorderFuzzWaveFailure(fuzzCase) + $" Case: {fuzzCase.Describe()}");
            }

            // --- Phase 2: seeded border edits under the seeded simulator schedule ---
            foreach (BorderHeightFuzzCase.Edit edit in fuzzCase.Edits)
            {
                if (edit.IsPlace)
                    world.PlaceBlock(edit.Pos, TestBlockPalette.Stone);
                else
                    world.BreakBlock(edit.Pos);

                for (int frame = 0; frame < cadence; frame++)
                    sim.RunFrame(budget, LightingFrameSimulator.CompletionOrder.Shuffled);
            }

            int frames = sim.RunToConvergence(BORDER_FUZZ_SIM_MAX_FRAMES, budget,
                LightingFrameSimulator.CompletionOrder.Shuffled);
            if (frames < 0)
            {
                return BorderFuzzFail($"{label}: pipeline settles after the border edits",
                    $"work still pending after {BORDER_FUZZ_SIM_MAX_FRAMES} frames. " +
                    ProbeBug13Oscillation(world, sim, budget, LightingFrameSimulator.CompletionOrder.Shuffled) +
                    $" Case: {fuzzCase.Describe()}");
            }

            if (!LightingAssert.MatchesOracleQuiet(world, LightingOracle.Solve(world), out string editSummary))
            {
                return BorderFuzzFail($"{label}: post-edit field matches the borderless oracle",
                    $"{editSummary}. Case: {fuzzCase.Describe()}");
            }

            return true;
        }

        /// <summary>
        /// Builds the case's world: solid stone columns at the per-column random heights, plus the seam
        /// overhang strips. Heightmaps are recalculated; the world is returned un-lit (edits are the
        /// scenario body's phase 2, not authored here).
        /// </summary>
        /// <param name="fuzzCase">The case to build.</param>
        /// <returns>The fully authored test world.</returns>
        private static LightingTestWorld BuildBorderHeightFuzzWorld(BorderHeightFuzzCase fuzzCase)
        {
            LightingTestWorld world = new LightingTestWorld(BORDER_FUZZ_GRID);
            const int worldWidth = BORDER_FUZZ_GRID * VoxelData.ChunkWidth;

            for (int x = 0; x < worldWidth; x++)
            for (int z = 0; z < worldWidth; z++)
                world.FillBox(new Vector3Int(x, 0, z),
                    new Vector3Int(x, fuzzCase.Heights[x + worldWidth * z], z), TestBlockPalette.Stone);

            foreach (BorderHeightFuzzCase.Overhang overhang in fuzzCase.Overhangs)
            {
                for (int d = 0; d < BORDER_FUZZ_OVERHANG_LENGTH; d++)
                    world.SetBlock(overhang.StripPos(d), TestBlockPalette.Stone);
            }

            world.RecalculateHeightmaps();
            return world;
        }

        /// <summary>
        /// Classifies a generation-wave failure with the C2 classifier: re-runs a fresh case world with
        /// <see cref="BORDER_FUZZ_DIAGNOSIS_ROUNDS"/> forced edge-check rounds (production caps at 2).
        /// Reaching the oracle there means edge-round budget shortfall (the Bug-05 mechanism); still
        /// diverging means the wrongness is not round-limited.
        /// </summary>
        /// <param name="fuzzCase">The failing case.</param>
        /// <returns>A console-readable classifier verdict.</returns>
        private static string ClassifyBorderFuzzWaveFailure(BorderHeightFuzzCase fuzzCase)
        {
            using LightingTestWorld world = BuildBorderHeightFuzzWorld(fuzzCase);
            int waves = world.RunInitialLightingParallelForcedEdgeRounds(BORDER_FUZZ_DIAGNOSIS_ROUNDS);
            if (waves < 0)
                return $"Classifier: the {BORDER_FUZZ_DIAGNOSIS_ROUNDS}-forced-edge-rounds run did not converge either.";

            return LightingAssert.MatchesOracleQuiet(world, LightingOracle.Solve(world), out string summary)
                ? $"Classifier: reaches the oracle with {BORDER_FUZZ_DIAGNOSIS_ROUNDS} forced edge rounds — edge-round budget shortfall (the Bug-05 mechanism), not an unreachable region."
                : $"Classifier: still diverges with {BORDER_FUZZ_DIAGNOSIS_ROUNDS} forced edge rounds ({summary}) — not round-limited.";
        }

        /// <summary>
        /// Failure reporting for the fuzz sweep iterations, switched by
        /// <see cref="BORDER_FUZZ_EXPECTED_RED"/>: an expected Bug-15 reproduction logs an
        /// <c>[EXPECTED-RED]</c> warning; once promoted to a baseline a failure is a regression and
        /// logs a <c>[FAIL]</c> error (the runner and its readers reserve <c>LogError</c> for regressions).
        /// </summary>
        /// <param name="testName">The failed check's name (includes the seed label).</param>
        /// <param name="detail">The failure detail, including any classifier/probe verdict and the case description.</param>
        /// <returns>Always false.</returns>
        private static bool BorderFuzzFail(string testName, string detail)
        {
            if (BORDER_FUZZ_EXPECTED_RED)
                Debug.LogWarning($"[EXPECTED-RED] {testName}\n{detail}");
            else
                Debug.LogError($"[FAIL] {testName}\n{detail}");

            return false;
        }
    }
}
