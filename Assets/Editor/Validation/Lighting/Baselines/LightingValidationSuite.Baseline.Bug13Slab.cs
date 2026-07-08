using System.Collections.Generic;
using Editor.Validation.Lighting.Framework;
using UnityEngine;
using Scenario = Editor.Validation.Framework.Scenario;

namespace Editor.Validation.Lighting
{
    /// <summary>
    /// Baseline regression scenarios <b>B56–B59</b> — multi-chunk suspended-slab convergence, guarding
    /// the <b>Bug 13</b> fix (live-locking cross-chunk skylight under a large opaque slab; fixed July
    /// 2026 by extending the Bug-11 removal veto with live third-party cross-chunk support,
    /// <see cref="Helpers.CrossChunkLightModApplier.CrossChunkSunlightSupport"/> — see
    /// <c>Documentation/Bugs/_FIXED_BUGS.md</c>). Promoted from known-bug repros K13a–K13d (roadmap
    /// item AS-1) after in-game confirmation on the fluid-stress opaque-floor config.
    /// <para>
    /// Two geometries throughout, because the harness grid boundary IS the world boundary:
    /// <list type="bullet">
    /// <item><b>Full-grid</b> (grid 3): the slab spans every chunk. Modeling limit — with no world
    /// beyond the grid there is NO lit perimeter, so the under-slab region is uniformly dark. The
    /// cheap lower bound.</item>
    /// <item><b>Inset</b> (grid 5, slab = centre 3×3 chunks): a 16-chunk sky-lit ring feeds light
    /// under the slab from its perimeter, forming the cross-chunk gradient at interior seams that
    /// live-locked pre-fix (`FluidStressController` stamps a 5×5-chunk floor at y100 INSIDE a larger
    /// loaded world; the in-game threshold was ≥ 3 slab chunks). The faithful habitat and the actual
    /// fix tripwire.</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>B56/B57</b> — generation-wave: slab present from the start, wave-parallel initial lighting
    /// terminates on the oracle (green pre-fix too: general convergence guards, not fix tripwires).
    /// <b>B58</b> — dynamic-stamp: converge a slab-less world, then stamp the slab chunk-by-chunk
    /// through the player-edit path with frame ticks between stamps, under unlimited-budget and
    /// single-slot scheduling — the deterministic pre-fix live-lock, the primary fix tripwire.
    /// <b>B59</b> — the B58 stamp under seeded completion-shuffle + seed-derived budgets/cadence,
    /// asserting termination and the oracle across the whole seed space (the space that also exposed
    /// Bug 14's stale-ghost exit, fixed July 2026 and pinned by B60/B61 in
    /// <c>Baselines/LightingValidationSuite.Baseline.Bug14Ghost.cs</c>). A red is classified before it is reported:
    /// budget escalation separates slow convergence from live-lock, the forced-edge-rounds classifier
    /// (C2 precedent) separates round-budget shortfall from genuine wrongness, and a hash-based field
    /// probe detects an exact repeating cycle — the "no fixed point" smoking gun.
    /// </para>
    /// Self-registered via the <see cref="AddBug13SlabBaselineScenarios"/> hook called from
    /// <c>AddBaselineScenarios</c> (the <c>Baselines/</c> group-partial pattern).
    /// </summary>
    public static partial class LightingValidationSuite
    {
        // --- Geometry (shared by the B56-B59 family and Bug 14's K14a) ---
        private const int BUG13_SLAB_Y = 100; // mirrors FluidBenchmarkScenarios.SkyFloorY, the in-game stamp altitude
        private const int BUG13_FLOOR_Y = 10; // superflat stand-in for the terrain/ocean below the in-game region

        private const int BUG13_FULL_GRID = 3; // full-grid config: slab spans the whole grid (no lit perimeter)
        private const int BUG13_FULL_SLAB_MIN = 0;
        private const int BUG13_FULL_SLAB_MAX = 2;

        private const int BUG13_INSET_GRID = 5; // faithful config: slab = centre 3×3 chunks inside a sky-lit ring
        private const int BUG13_INSET_SLAB_MIN = 1;
        private const int BUG13_INSET_SLAB_MAX = 3;

        // --- Budgets. Generous relative to the defaults (16 rounds / 200 frames) so a red means
        // "did not terminate", not "was slow"; the escalated classifier budget then separates the two. ---
        private const int BUG13_WAVE_MAX_ROUNDS = 64;
        private const int BUG13_ESCALATED_MAX_ROUNDS = 256;
        private const int BUG13_FORCED_EDGE_ROUNDS = 6; // C2's classifier: production caps at 2
        private const int BUG13_SIM_MAX_FRAMES = 500;
        private const int BUG13_SINGLE_SLOT_MAX_FRAMES = 1500; // 1 job/frame across up to 25 chunks needs headroom
        private const int BUG13_STAMP_FRAMES_BETWEEN = 2; // frame ticks between chunk stamps (deterministic legs)
        private const int BUG13_CYCLE_PROBE_FRAMES = 48; // extra frames hashed for the oscillation probe

        private const int BUG13_SWEEP_SEEDS_FULL = 50; // grid-3 sweep breadth (cheap per seed)
        private const int BUG13_SWEEP_SEEDS_INSET = 25; // grid-5 sweep breadth (costlier per seed)

        // FNV-1a 64-bit parameters for the oscillation probe's light-field hash.
        private const ulong BUG13_FNV_OFFSET_BASIS = 14695981039346656037UL;
        private const ulong BUG13_FNV_PRIME = 1099511628211UL;

        /// <summary>
        /// Registers the suspended-slab convergence baselines (B56–B59, promoted from known-bug repros
        /// K13a–K13d after the July 2026 Bug 13 fix; called from <c>AddBaselineScenarios</c>).
        /// </summary>
        /// <param name="scenarios">The scenario list to append to.</param>
        static partial void AddBug13SlabBaselineScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario(
                "B56: A grid-spanning suspended opaque slab terminates generation-wave initial lighting on the oracle (no lit perimeter)",
                Baseline_Bug13GenerationWaveFullGrid));
            scenarios.Add(new Scenario(
                "B57: An inset suspended opaque slab (sky-lit ring, perimeter-fed under-slab gradient) terminates generation-wave initial lighting on the oracle",
                Baseline_Bug13GenerationWaveInsetPerimeter));
            scenarios.Add(new Scenario(
                "B58: Stamping the inset slab onto a settled world settles on the oracle, under unlimited-budget and single-slot scheduling (Bug 13 fix tripwire)",
                Baseline_Bug13DynamicStampDeterministic));
            scenarios.Add(new Scenario(
                "B59: The dynamic slab stamp settles on the borderless oracle on every seed of a completion-shuffle + budget/cadence sweep (Bug 13 + Bug 14 fix tripwire)",
                Baseline_Bug13DynamicStampSweep));
        }

        // --- Scenario bodies ---

        /// <summary>
        /// B56: generation-wave termination on the full-grid slab. See the class docstring for the
        /// modeling limit (no lit perimeter exists at the grid/world boundary).
        /// </summary>
        private static bool Baseline_Bug13GenerationWaveFullGrid()
        {
            return RunBug13GenerationWaveCase("B56 (grid-3 full-grid slab)",
                BUG13_FULL_GRID, BUG13_FULL_SLAB_MIN, BUG13_FULL_SLAB_MAX);
        }

        /// <summary>
        /// B57: generation-wave termination on the faithful inset slab — the sky-lit ring feeds the
        /// perimeter gradient under the slab across the internal chunk seams (Bug 13's habitat).
        /// </summary>
        private static bool Baseline_Bug13GenerationWaveInsetPerimeter()
        {
            return RunBug13GenerationWaveCase("B57 (grid-5 inset slab, perimeter-fed)",
                BUG13_INSET_GRID, BUG13_INSET_SLAB_MIN, BUG13_INSET_SLAB_MAX);
        }

        /// <summary>
        /// B58: the dynamic-stamp variant, faithful to the in-game Bug 13 repro (the stress harness
        /// stamps the floor onto an already-settled world). Converges a slab-less world, then stamps
        /// the slab chunk-by-chunk via the player-edit path with frame ticks between stamps — so stamps
        /// land while neighbor lighting jobs are in flight — and asserts the pipeline settles on the
        /// oracle. Runs under unlimited budget and under single-slot starvation pressure. Pre-fix this
        /// live-locked with a proven period-2 field cycle — the primary fix tripwire. (The in-game
        /// stamp is emitted column-interleaved at 1024 mods/frame; per-chunk stamping is the same
        /// granularity class.)
        /// </summary>
        private static bool Baseline_Bug13DynamicStampDeterministic()
        {
            bool passed = RunBug13DynamicStampDeterministicLeg("B58 (unlimited budget)",
                int.MaxValue, BUG13_SIM_MAX_FRAMES);
            passed &= RunBug13DynamicStampDeterministicLeg("B58 (single-slot starvation)",
                budget: 1, BUG13_SINGLE_SLOT_MAX_FRAMES);
            return passed;
        }

        /// <summary>
        /// B59: the B58 stamp under seeded orders — completion order shuffled, per-frame budget and
        /// stamp cadence derived from the seed — because the pre-fix live-lock's trigger was
        /// order-sensitive. Sweeps both geometries; any failing seed reproduces its exact case
        /// deterministically. Asserts termination AND the borderless oracle: the same seed space also
        /// exposed Bug 14's stale-ghost exit (fixed July 2026), so the oracle assertion doubles as its
        /// sweep-wide guard (B61 pins the original failing seed; this covers the rest of the space).
        /// </summary>
        private static bool Baseline_Bug13DynamicStampSweep()
        {
            int? failingFull = SweepBug13DynamicStamp(BUG13_FULL_GRID,
                BUG13_FULL_SLAB_MIN, BUG13_FULL_SLAB_MAX, BUG13_SWEEP_SEEDS_FULL);
            bool passed = ReportBug13SweepOutcome("B59 (grid-3 full-grid slab)", failingFull, BUG13_SWEEP_SEEDS_FULL);

            int? failingInset = SweepBug13DynamicStamp(BUG13_INSET_GRID,
                BUG13_INSET_SLAB_MIN, BUG13_INSET_SLAB_MAX, BUG13_SWEEP_SEEDS_INSET);
            passed &= ReportBug13SweepOutcome("B59 (grid-5 inset slab)", failingInset, BUG13_SWEEP_SEEDS_INSET);

            return passed;
        }

        // --- Case runners ---

        /// <summary>
        /// Shared generation-wave leg: builds the slab world, runs wave-parallel initial lighting
        /// (the faithful mirror of initial world generation), and asserts termination first, oracle
        /// correctness second. Every red is classified before it is reported (budget escalation for
        /// non-convergence, forced edge rounds for a static mismatch).
        /// </summary>
        /// <param name="label">The console label for this case.</param>
        /// <param name="gridSize">Chunks per horizontal grid axis.</param>
        /// <param name="slabMinChunk">Inclusive minimum slab chunk coordinate on both axes.</param>
        /// <param name="slabMaxChunk">Inclusive maximum slab chunk coordinate on both axes.</param>
        /// <returns>True when lighting terminates and matches the oracle.</returns>
        private static bool RunBug13GenerationWaveCase(string label, int gridSize, int slabMinChunk, int slabMaxChunk)
        {
            using LightingTestWorld world = BuildBug13SlabWorld(gridSize, slabMinChunk, slabMaxChunk, includeSlab: true);

            int waves = world.RunInitialLightingParallel(BUG13_WAVE_MAX_ROUNDS);
            if (waves < 0)
            {
                return Bug13Fail($"{label}: generation-wave initial lighting terminates",
                    $"no convergence within {BUG13_WAVE_MAX_ROUNDS} waves. " +
                    ClassifyBug13WaveNonConvergence(gridSize, slabMinChunk, slabMaxChunk),
                    expectedRed: false);
            }

            Debug.Log($"[PASS] {label}: generation-wave initial lighting terminates ({waves} wave(s))");

            if (!LightingAssert.MatchesOracleQuiet(world, LightingOracle.Solve(world), out string summary))
            {
                return Bug13Fail($"{label}: settled field matches the borderless oracle",
                    $"{summary}. " + ClassifyBug13StaticMismatch(gridSize, slabMinChunk, slabMaxChunk),
                    expectedRed: false);
            }

            Debug.Log($"[PASS] {label}: settled field matches the borderless oracle");
            return true;
        }

        /// <summary>
        /// One deterministic B58 leg on the inset geometry: settle a slab-less world, then stamp and
        /// converge under FIFO completion at the given budget.
        /// </summary>
        /// <param name="label">The console label for this leg.</param>
        /// <param name="budget">Jobs schedulable per simulated frame (<see cref="int.MaxValue"/> = unlimited).</param>
        /// <param name="maxFrames">The frame budget for post-stamp convergence.</param>
        /// <returns>True when the pipeline settles and matches the oracle.</returns>
        private static bool RunBug13DynamicStampDeterministicLeg(string label, int budget, int maxFrames)
        {
            using LightingTestWorld world = BuildBug13SlabWorld(BUG13_INSET_GRID,
                BUG13_INSET_SLAB_MIN, BUG13_INSET_SLAB_MAX, includeSlab: false);
            world.RunInitialLighting();

            LightingFrameSimulator sim = new LightingFrameSimulator(world);
            return RunBug13StampAndSettle(world, sim, BUG13_INSET_SLAB_MIN, BUG13_INSET_SLAB_MAX,
                budget, LightingFrameSimulator.CompletionOrder.Fifo, BUG13_STAMP_FRAMES_BETWEEN,
                maxFrames, label, logPass: true, assertOracle: true, expectedRed: false);
        }

        /// <summary>
        /// Runs one seeded-sweep pass over the dynamic-stamp scenario: geometry fixed, per-frame budget
        /// (1–4) and stamp cadence (0–2 frames) derived from the seed, completion order shuffled.
        /// </summary>
        /// <param name="gridSize">Chunks per horizontal grid axis.</param>
        /// <param name="slabMinChunk">Inclusive minimum slab chunk coordinate on both axes.</param>
        /// <param name="slabMaxChunk">Inclusive maximum slab chunk coordinate on both axes.</param>
        /// <param name="iterations">Number of seeds to try.</param>
        /// <returns>The first failing seed, or null when every seed settles on the oracle.</returns>
        private static int? SweepBug13DynamicStamp(int gridSize, int slabMinChunk, int slabMaxChunk, int iterations)
        {
            return LightingFrameSimulator.FindFailingSeed(
                seededWorldFactory: _ =>
                {
                    LightingTestWorld world = BuildBug13SlabWorld(gridSize, slabMinChunk, slabMaxChunk, includeSlab: false);
                    world.RunInitialLighting();
                    return world;
                },
                scenarioBody: (world, sim, seed) => RunBug13StampAndSettle(world, sim,
                    slabMinChunk, slabMaxChunk,
                    budget: 1 + seed % 4,
                    LightingFrameSimulator.CompletionOrder.Shuffled,
                    framesBetweenStamps: seed % 3,
                    BUG13_SIM_MAX_FRAMES,
                    $"B59 seed {seed}", logPass: false,
                    assertOracle: true, expectedRed: false),
                iterations: iterations);
        }

        /// <summary>
        /// Shared stamp-and-converge body for B58/B59 and Bug 14's K14a: stamps the slab chunk-by-chunk
        /// through the player-edit path (256 <see cref="LightingTestWorld.PlaceBlock"/> calls per chunk
        /// — real removal seeds, opacity-change column recalcs, incremental heightmap), running frame
        /// ticks between stamps so later stamps land while earlier chunks' jobs are in flight, then
        /// asserts the pipeline settles within the frame budget (and, when asserted, matches the
        /// oracle). A non-settling run is handed to the oscillation probe before being reported.
        /// </summary>
        /// <param name="world">The settled slab-less world to stamp.</param>
        /// <param name="sim">The frame simulator driving the world (seeded for shuffled orders).</param>
        /// <param name="slabMinChunk">Inclusive minimum slab chunk coordinate on both axes.</param>
        /// <param name="slabMaxChunk">Inclusive maximum slab chunk coordinate on both axes.</param>
        /// <param name="budget">Jobs schedulable per simulated frame.</param>
        /// <param name="order">Completion order for pending flights.</param>
        /// <param name="framesBetweenStamps">Frame ticks run after each chunk stamp.</param>
        /// <param name="maxFrames">The frame budget for post-stamp convergence.</param>
        /// <param name="label">The console label for this case.</param>
        /// <param name="logPass">When false, suppresses per-case PASS logs (sweep iterations).</param>
        /// <param name="assertOracle">When false, only termination is asserted. All current baselines
        /// pass true (the Bug 14 stale-ghost fix made the oracle reachable across the whole seed
        /// space); false remains available for future known-bug callers that own a weaker property.</param>
        /// <param name="expectedRed">Failure-reporting mode: false for baselines (a failure is a
        /// regression, logged as an error), true for known-bug callers like K14a (an expected
        /// reproduction, logged as a warning).</param>
        /// <returns>True when the pipeline settles (and, when asserted, matches the oracle).</returns>
        private static bool RunBug13StampAndSettle(LightingTestWorld world, LightingFrameSimulator sim,
            int slabMinChunk, int slabMaxChunk, int budget, LightingFrameSimulator.CompletionOrder order,
            int framesBetweenStamps, int maxFrames, string label, bool logPass, bool assertOracle, bool expectedRed)
        {
            for (int cx = slabMinChunk; cx <= slabMaxChunk; cx++)
            {
                for (int cz = slabMinChunk; cz <= slabMaxChunk; cz++)
                {
                    StampBug13SlabChunk(world, cx, cz);
                    for (int frame = 0; frame < framesBetweenStamps; frame++)
                        sim.RunFrame(budget, order);
                }
            }

            int frames = sim.RunToConvergence(maxFrames, budget, order);
            if (frames < 0)
            {
                return Bug13Fail($"{label}: pipeline settles after the slab stamp",
                    $"work still pending after {maxFrames} frames. " + ProbeBug13Oscillation(world, sim, budget, order),
                    expectedRed);
            }

            if (logPass)
                Debug.Log($"[PASS] {label}: pipeline settles after the slab stamp ({frames} frame(s))");

            if (assertOracle)
            {
                if (!LightingAssert.MatchesOracleQuiet(world, LightingOracle.Solve(world), out string summary))
                    return Bug13Fail($"{label}: settled field matches the borderless oracle", summary, expectedRed);

                if (logPass)
                    Debug.Log($"[PASS] {label}: settled field matches the borderless oracle");
            }

            return true;
        }

        // --- World building ---

        /// <summary>
        /// Builds the slab world: a superflat stone floor (the stand-in for the terrain below the
        /// in-game region) and, when <paramref name="includeSlab"/> is set, a 1-voxel-thick opaque
        /// slab at y=<see cref="BUG13_SLAB_Y"/> spanning the given square chunk range. Heightmaps are
        /// recalculated; the world is returned un-lit.
        /// </summary>
        /// <param name="gridSize">Chunks per horizontal grid axis.</param>
        /// <param name="slabMinChunk">Inclusive minimum slab chunk coordinate on both axes.</param>
        /// <param name="slabMaxChunk">Inclusive maximum slab chunk coordinate on both axes.</param>
        /// <param name="includeSlab">Whether the slab is authored now (generation-wave) or stamped later (dynamic).</param>
        /// <returns>A freshly-built, un-lit test world.</returns>
        private static LightingTestWorld BuildBug13SlabWorld(int gridSize, int slabMinChunk, int slabMaxChunk, bool includeSlab)
        {
            LightingTestWorld world = new LightingTestWorld(gridSize);
            world.FillSuperflatFloor(BUG13_FLOOR_Y, TestBlockPalette.Stone);

            if (includeSlab)
            {
                int min = slabMinChunk * VoxelData.ChunkWidth;
                int max = (slabMaxChunk + 1) * VoxelData.ChunkWidth - 1;
                world.FillBox(new Vector3Int(min, BUG13_SLAB_Y, min), new Vector3Int(max, BUG13_SLAB_Y, max),
                    TestBlockPalette.Stone);
            }

            world.RecalculateHeightmaps();
            return world;
        }

        /// <summary>
        /// Stamps one chunk's worth of slab (16×16 stone at y=<see cref="BUG13_SLAB_Y"/>) through the
        /// player-edit path, mirroring the in-game repro's VoxelMod stamp landing on a settled world.
        /// </summary>
        /// <param name="world">The world to stamp into.</param>
        /// <param name="chunkX">The slab chunk's grid X.</param>
        /// <param name="chunkZ">The slab chunk's grid Z.</param>
        private static void StampBug13SlabChunk(LightingTestWorld world, int chunkX, int chunkZ)
        {
            int originX = chunkX * VoxelData.ChunkWidth;
            int originZ = chunkZ * VoxelData.ChunkWidth;
            for (int x = 0; x < VoxelData.ChunkWidth; x++)
            for (int z = 0; z < VoxelData.ChunkWidth; z++)
                world.PlaceBlock(new Vector3Int(originX + x, BUG13_SLAB_Y, originZ + z), TestBlockPalette.Stone);
        }

        // --- Red classification (a -1 or a mismatch is only trusted once it is attributed) ---

        /// <summary>
        /// Classifies a generation-wave non-convergence by re-running a fresh world under a 4× budget:
        /// converging there means round-budget shortfall (slow convergence), still failing means
        /// live-lock evidence (no fixed point within 16× the suite default).
        /// </summary>
        /// <param name="gridSize">Chunks per horizontal grid axis.</param>
        /// <param name="slabMinChunk">Inclusive minimum slab chunk coordinate on both axes.</param>
        /// <param name="slabMaxChunk">Inclusive maximum slab chunk coordinate on both axes.</param>
        /// <returns>A console-readable classifier verdict.</returns>
        private static string ClassifyBug13WaveNonConvergence(int gridSize, int slabMinChunk, int slabMaxChunk)
        {
            using LightingTestWorld world = BuildBug13SlabWorld(gridSize, slabMinChunk, slabMaxChunk, includeSlab: true);
            int waves = world.RunInitialLightingParallel(BUG13_ESCALATED_MAX_ROUNDS);
            return waves >= 0
                ? $"Classifier: converged at {waves} waves under the escalated {BUG13_ESCALATED_MAX_ROUNDS}-wave budget — round-budget shortfall (slow convergence), NOT a live-lock."
                : $"Classifier: still no convergence at {BUG13_ESCALATED_MAX_ROUNDS} waves (16x the suite default) — live-lock evidence (no fixed point).";
        }

        /// <summary>
        /// Classifies a converged-but-wrong field with the C2 classifier: re-runs a fresh world with
        /// <see cref="BUG13_FORCED_EDGE_ROUNDS"/> forced edge rounds (production caps at 2). Reaching
        /// the oracle there means edge-round budget shortfall (the Bug-05 mechanism); still diverging
        /// means the wrongness is not round-limited.
        /// </summary>
        /// <param name="gridSize">Chunks per horizontal grid axis.</param>
        /// <param name="slabMinChunk">Inclusive minimum slab chunk coordinate on both axes.</param>
        /// <param name="slabMaxChunk">Inclusive maximum slab chunk coordinate on both axes.</param>
        /// <returns>A console-readable classifier verdict.</returns>
        private static string ClassifyBug13StaticMismatch(int gridSize, int slabMinChunk, int slabMaxChunk)
        {
            using LightingTestWorld world = BuildBug13SlabWorld(gridSize, slabMinChunk, slabMaxChunk, includeSlab: true);
            int waves = world.RunInitialLightingParallelForcedEdgeRounds(BUG13_FORCED_EDGE_ROUNDS);
            if (waves < 0)
                return $"Classifier: the {BUG13_FORCED_EDGE_ROUNDS}-forced-edge-rounds run did not converge either.";

            return LightingAssert.MatchesOracleQuiet(world, LightingOracle.Solve(world), out string summary)
                ? $"Classifier: reaches the oracle with {BUG13_FORCED_EDGE_ROUNDS} forced edge rounds — edge-round budget shortfall (the Bug-05 mechanism), not an unreachable pocket."
                : $"Classifier: still diverges with {BUG13_FORCED_EDGE_ROUNDS} forced edge rounds ({summary}) — not round-limited.";
        }

        /// <summary>
        /// After a failed convergence, keeps ticking frames and hashes the full light field each frame,
        /// looking for an exact hash repeat while work is still pending — a repeating cycle is the
        /// live-lock smoking gun (the field visits the same state twice, so it can never settle),
        /// whereas settling during the probe means the earlier -1 was only a frame-budget shortfall.
        /// </summary>
        /// <param name="world">The non-converged world.</param>
        /// <param name="sim">The simulator mid-run (in-flight jobs and pending work intact).</param>
        /// <param name="budget">Jobs schedulable per simulated frame (same as the failed run).</param>
        /// <param name="order">Completion order (same as the failed run).</param>
        /// <returns>A console-readable probe verdict.</returns>
        private static string ProbeBug13Oscillation(LightingTestWorld world, LightingFrameSimulator sim,
            int budget, LightingFrameSimulator.CompletionOrder order)
        {
            List<ulong> history = new List<ulong>(BUG13_CYCLE_PROBE_FRAMES);

            for (int frame = 0; frame < BUG13_CYCLE_PROBE_FRAMES; frame++)
            {
                sim.RunFrame(budget, order);
                if (!world.HasPendingLightWork && sim.InFlightCount == 0)
                    return $"Probe: settled after {frame + 1} extra frame(s) — frame-budget shortfall (slow convergence), NOT a live-lock.";

                ulong hash = HashBug13LightField(world);
                int cycleStart = history.IndexOf(hash);
                if (cycleStart >= 0)
                {
                    return $"Probe: OSCILLATION — the light field at probe frame {frame} hash-repeats probe frame {cycleStart} " +
                           $"(cycle length {frame - cycleStart}) while work is still pending. Live-lock, not slow convergence. " +
                           $"Pending chunks: {DescribeBug13PendingChunks(world)}";
                }

                history.Add(hash);
            }

            return $"Probe: no exact field cycle within {BUG13_CYCLE_PROBE_FRAMES} probe frames — the field churns without " +
                   $"repeating (divergent churn or a longer cycle). Pending chunks: {DescribeBug13PendingChunks(world)}";
        }

        /// <summary>
        /// FNV-1a 64-bit hash over the whole grid's packed light values, in deterministic world order.
        /// Two equal hashes are treated as an identical field for probe purposes (collision odds are
        /// negligible for a diagnostic).
        /// </summary>
        /// <param name="world">The world whose light field is hashed.</param>
        /// <returns>The field hash.</returns>
        private static ulong HashBug13LightField(LightingTestWorld world)
        {
            int width = world.GridSize * VoxelData.ChunkWidth;
            ulong hash = BUG13_FNV_OFFSET_BASIS;

            for (int y = 0; y < VoxelData.ChunkHeight; y++)
            for (int x = 0; x < width; x++)
            for (int z = 0; z < width; z++)
            {
                hash ^= world.GetLightData(new Vector3Int(x, y, z));
                hash *= BUG13_FNV_PRIME;
            }

            return hash;
        }

        /// <summary>Lists the grid coordinates still flagged with pending light work, for probe reports.</summary>
        /// <param name="world">The world to inspect.</param>
        /// <returns>A comma-separated coordinate list, or a note that only in-flight jobs remain.</returns>
        private static string DescribeBug13PendingChunks(LightingTestWorld world)
        {
            List<string> pending = new List<string>();
            foreach (Vector2Int coord in world.AllChunkCoords())
            {
                if (world.ChunkHasLightWork(coord))
                    pending.Add(coord.ToString());
            }

            return pending.Count == 0 ? "none (only in-flight jobs remain)" : string.Join(", ", pending);
        }

        /// <summary>
        /// Reports one B59 sweep outcome: a failing seed is a regression (deterministically
        /// reproducible by re-running that seed), PASS otherwise.
        /// </summary>
        /// <param name="label">The console label for the sweep.</param>
        /// <param name="failingSeed">The sweep result.</param>
        /// <param name="iterations">How many seeds the sweep covered.</param>
        /// <returns>True when every seed passed.</returns>
        private static bool ReportBug13SweepOutcome(string label, int? failingSeed, int iterations)
        {
            if (!failingSeed.HasValue)
            {
                Debug.Log($"[PASS] {label}: all {iterations} seeds settle on the oracle");
                return true;
            }

            return Bug13Fail($"{label}: all {iterations} seeds settle on the oracle",
                $"seed {failingSeed.Value} fails (budget {1 + failingSeed.Value % 4}, cadence {failingSeed.Value % 3} — " +
                "re-run this seed to reproduce the exact case; details logged by that iteration).",
                expectedRed: false);
        }

        /// <summary>
        /// Failure reporting shared by the B56–B59 baselines and Bug 14's K14a: a baseline failure is a
        /// regression and logs an error (<c>[FAIL]</c>); a known-bug caller passes
        /// <paramref name="expectedRed"/> to log a warning instead (<c>[EXPECTED-RED]</c>) — the runner
        /// and its readers reserve <c>LogError</c> for regressions.
        /// </summary>
        /// <param name="testName">The failed check's name.</param>
        /// <param name="detail">The failure detail, including any classifier/probe verdict.</param>
        /// <param name="expectedRed">True when the caller is a known-bug scenario whose failure is the
        /// expected reproduction.</param>
        /// <returns>Always false.</returns>
        private static bool Bug13Fail(string testName, string detail, bool expectedRed)
        {
            if (expectedRed)
                Debug.LogWarning($"[EXPECTED-RED] {testName}\n{detail}");
            else
                Debug.LogError($"[FAIL] {testName}\n{detail}");

            return false;
        }
    }
}
