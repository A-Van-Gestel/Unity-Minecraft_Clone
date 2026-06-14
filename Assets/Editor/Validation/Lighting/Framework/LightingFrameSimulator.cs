using System;
using System.Collections.Generic;
using UnityEngine;
using Random = System.Random;

namespace Editor.Validation.Lighting.Framework
{
    /// <summary>
    /// Wraps <see cref="LightingTestWorld"/> with a frame-tick orchestration layer that models the
    /// production scheduling behaviors the bare harness omits: the <c>ContainsKey</c> in-flight guard,
    /// per-frame job budget throttling, controllable completion ordering, multi-frame job lifetimes,
    /// and seeded iteration-order randomness (Dictionary simulation).
    /// This enables deterministic reproduction of orchestration-layer timing bugs (e.g., Bug 09) that
    /// only manifest when scheduling is rejected or delayed.
    /// <para>
    /// The underlying <see cref="LightingTestWorld"/> remains the execution engine — this class only
    /// decides <b>when</b> to call <see cref="LightingTestWorld.BeginLightingJob"/> and
    /// <see cref="LightingTestWorld.CompleteLightingJob"/>, and in what order.
    /// </para>
    /// </summary>
    public sealed class LightingFrameSimulator
    {
        /// <summary>The order in which pending flights are completed within a single frame tick.</summary>
        public enum CompletionOrder
        {
            /// <summary>FIFO — same order they were scheduled. Deterministic baseline.</summary>
            Fifo,

            /// <summary>Reverse of scheduling order. Exercises the <c>_completedLightJobs</c>
            /// defer-vs-apply ordering dependency in the opposite direction.</summary>
            Reverse,

            /// <summary>Seeded Fisher-Yates shuffle. Models the non-deterministic
            /// <c>Dictionary</c> iteration order in production's <c>ProcessLightingJobs</c>.
            /// Requires a non-null seed in the constructor; throws <see cref="InvalidOperationException"/>
            /// if the simulator was constructed without a seed.</summary>
            Shuffled,
        }

        /// <summary>Per-frame statistics returned by <see cref="RunFrame"/>.</summary>
        public struct FrameResult
        {
            /// <summary>Number of in-flight jobs completed this frame.</summary>
            public int JobsCompleted;

            /// <summary>Number of new jobs scheduled this frame.</summary>
            public int JobsScheduled;

            /// <summary>Chunks that had pending work but could not schedule (in-flight guard or budget exhausted).</summary>
            public int ChunksStarved;

            /// <summary>Flights that remained in-flight because the completion predicate rejected them.</summary>
            public int JobsCarriedOver;
        }

        /// <summary>
        /// Tracks an in-flight job together with the frame on which it was scheduled, enabling
        /// age-based completion predicates that model multi-frame Burst job lifetimes.
        /// </summary>
        private struct PendingFlight
        {
            public LightingTestWorld.LightingJobFlight Flight;
            public int ScheduledOnFrame;
        }

        private readonly LightingTestWorld _world;
        private readonly List<PendingFlight> _pendingFlights = new List<PendingFlight>();
        private readonly Random _rng;
        private int _currentFrame;

        /// <summary>
        /// Constructs a frame simulator wrapping the given test world. The test world must already be
        /// set up (chunks created, initial lighting done if needed).
        /// </summary>
        /// <param name="world">The underlying test world whose Begin/Complete API this simulator orchestrates.</param>
        /// <param name="seed">Optional RNG seed for iteration-order randomness. When set, Phase 2
        /// (scheduling) shuffles the chunk iteration order each frame, modeling production's
        /// non-deterministic <c>HashSet</c>/<c>Dictionary</c> iteration. Use
        /// <see cref="CompletionOrder.Shuffled"/> to also randomize Phase 1 (completion order).
        /// When <c>null</c> (default), both phases use deterministic ordering — preserving backward
        /// compatibility with all existing baselines.</param>
        public LightingFrameSimulator(LightingTestWorld world, int? seed = null)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _rng = seed.HasValue ? new Random(seed.Value) : null;
        }

        /// <summary>The number of in-flight jobs currently held by the simulator (scheduled but not yet completed).</summary>
        public int InFlightCount => _pendingFlights.Count;

        /// <summary>The current frame number, incremented at the start of each <see cref="RunFrame"/> call.</summary>
        public int CurrentFrame => _currentFrame;

        /// <summary>
        /// Executes one simulated frame tick, mirroring the production <c>ProcessLightingJobs</c> →
        /// <c>World.Update</c> lighting-scan cycle:
        /// <list type="number">
        /// <item>Complete pending flights that pass the <paramref name="completionPredicate"/> (in the
        /// specified order), applying cross-chunk mods via the existing
        /// <see cref="LightingTestWorld.CompleteLightingJob"/> logic. Flights rejected by the predicate
        /// remain in-flight, keeping their chunk locked by the <c>ContainsKey</c> guard.</item>
        /// <item>Schedule new jobs for chunks with pending work, up to the budget, respecting the
        /// <see cref="LightingScheduleDecision"/> in-flight guard.</item>
        /// </list>
        /// </summary>
        /// <param name="budget">Maximum number of new jobs to schedule this frame (mirrors
        /// <c>settings.maxLightJobsPerFrame</c>). Pass <see cref="int.MaxValue"/> for unlimited.</param>
        /// <param name="order">The order in which pending flights are completed. Affects which
        /// cross-chunk mods are deferred vs. applied directly (the <c>_completedLightJobs</c> ordering
        /// dependency in production's <c>ProcessLightingJobs</c>).</param>
        /// <param name="completionPredicate">Optional predicate controlling which flights complete this
        /// frame. Receives the flight and its age in frames (current frame minus the frame it was
        /// scheduled on). Returns <c>true</c> to complete the flight, <c>false</c> to hold it in-flight.
        /// When <c>null</c> (default), all flights complete — preserving the original behavior.</param>
        /// <returns>Statistics for this frame tick.</returns>
        public FrameResult RunFrame(int budget = int.MaxValue, CompletionOrder order = CompletionOrder.Fifo,
            Func<LightingTestWorld.LightingJobFlight, int, bool> completionPredicate = null)
        {
            _currentFrame++;
            FrameResult result = default;

            // --- Phase 1: Complete pending flights (selectively) ---
            if (_pendingFlights.Count > 0)
            {
                List<PendingFlight> toProcess = new List<PendingFlight>(_pendingFlights);

                if (order == CompletionOrder.Reverse)
                {
                    toProcess.Reverse();
                }
                else if (order == CompletionOrder.Shuffled)
                {
                    if (_rng == null)
                        throw new InvalidOperationException(
                            "CompletionOrder.Shuffled requires a non-null seed in the LightingFrameSimulator constructor.");
                    FisherYatesShuffle(toProcess, _rng);
                }

                List<PendingFlight> carriedOver = new List<PendingFlight>();

                foreach (PendingFlight pending in toProcess)
                {
                    int age = _currentFrame - pending.ScheduledOnFrame;

                    if (completionPredicate != null && !completionPredicate(pending.Flight, age))
                    {
                        carriedOver.Add(pending);
                        result.JobsCarriedOver++;
                        continue;
                    }

                    _world.CompleteLightingJob(pending.Flight);
                    result.JobsCompleted++;
                }

                _pendingFlights.Clear();
                _pendingFlights.AddRange(carriedOver);
            }

            // --- Phase 2: Schedule new jobs ---
            // When a seed is set, shuffle the iteration order to model production's non-deterministic
            // HashSet/Dictionary iteration in World.Update and ProcessLightingJobs.
            IEnumerable<Vector2Int> schedulingOrder;
            if (_rng != null)
            {
                List<Vector2Int> shuffled = new List<Vector2Int>(_world.AllChunkCoords());
                FisherYatesShuffle(shuffled, _rng);
                schedulingOrder = shuffled;
            }
            else
            {
                schedulingOrder = _world.AllChunkCoords();
            }

            int scheduled = 0;
            foreach (Vector2Int coord in schedulingOrder)
            {
                if (!_world.ChunkHasLightWork(coord))
                    continue;

                LightingScheduleDecision.Result decision = LightingScheduleDecision.Evaluate(
                    _world.IsChunkInFlight(coord),
                    neighborsDataReady: true);

                if (decision == LightingScheduleDecision.Result.AlreadyInFlight)
                {
                    result.ChunksStarved++;
                    continue;
                }

                if (scheduled >= budget)
                {
                    result.ChunksStarved++;
                    continue;
                }

                _pendingFlights.Add(new PendingFlight
                {
                    Flight = _world.BeginLightingJob(coord),
                    ScheduledOnFrame = _currentFrame,
                });
                scheduled++;
            }

            result.JobsScheduled = scheduled;
            return result;
        }

        /// <summary>
        /// Runs frame ticks until all chunks converge (no pending light work and no in-flight jobs)
        /// or the frame budget is exhausted. This is the frame-aware equivalent of
        /// <see cref="LightingTestWorld.RunToConvergence"/>.
        /// <para>
        /// This method always uses a <c>null</c> completion predicate (complete-all). Scenarios that
        /// need multi-frame job lifetimes should drive the frame loop manually with explicit per-frame
        /// predicates, then call this method for the final convergence phase.
        /// </para>
        /// </summary>
        /// <param name="maxFrames">Maximum number of frame ticks before giving up.</param>
        /// <param name="budgetPerFrame">Jobs per frame (mirrors <c>maxLightJobsPerFrame</c>).</param>
        /// <param name="order">Completion order strategy.</param>
        /// <returns>Number of frames taken to converge, or -1 if not converged within the budget.</returns>
        public int RunToConvergence(int maxFrames = 200, int budgetPerFrame = int.MaxValue,
            CompletionOrder order = CompletionOrder.Fifo)
        {
            for (int frame = 0; frame < maxFrames; frame++)
            {
                if (!_world.HasPendingLightWork && _pendingFlights.Count == 0)
                    return frame;

                RunFrame(budgetPerFrame, order);
            }

            if (!_world.HasPendingLightWork && _pendingFlights.Count == 0)
                return maxFrames;

            return -1;
        }

        /// <summary>
        /// Runs to convergence with a budget of 1 job per frame — maximum starvation pressure.
        /// Models the worst case where other systems consume all but one lighting slot.
        /// </summary>
        /// <param name="maxFrames">Maximum number of frame ticks.</param>
        /// <param name="order">Completion order strategy.</param>
        /// <returns>Number of frames taken, or -1 if not converged.</returns>
        public int RunToConvergenceSingleSlot(int maxFrames = 200, CompletionOrder order = CompletionOrder.Fifo)
        {
            return RunToConvergence(maxFrames, budgetPerFrame: 1, order);
        }

        #region Completion Predicate Factories

        /// <summary>
        /// Creates a predicate that completes flights only after they have been in-flight for at least
        /// <paramref name="minFrames"/> frames. Models Burst jobs that take multiple frames to complete.
        /// </summary>
        /// <param name="minFrames">Minimum age (in frames) before a flight is eligible for completion.</param>
        /// <returns>A completion predicate for use with <see cref="RunFrame"/>.</returns>
        public static Func<LightingTestWorld.LightingJobFlight, int, bool> MinAge(int minFrames)
        {
            return (_, age) => age >= minFrames;
        }

        /// <summary>
        /// Creates a predicate that only completes flights targeting the specified chunk coordinates.
        /// All other flights are held in-flight. Useful for step-by-step scenario scripting where
        /// specific chunks must complete in a controlled order.
        /// </summary>
        /// <param name="coords">The chunk coordinates whose flights should complete.</param>
        /// <returns>A completion predicate for use with <see cref="RunFrame"/>.</returns>
        public static Func<LightingTestWorld.LightingJobFlight, int, bool> OnlyChunks(params Vector2Int[] coords)
        {
            return (flight, _) => Array.IndexOf(coords, flight.ChunkCoord) >= 0;
        }

        /// <summary>
        /// Creates a predicate that completes all flights EXCEPT those targeting the specified chunk
        /// coordinates. Those flights are held in-flight, simulating a long-running Burst job for
        /// specific chunks while others complete normally.
        /// </summary>
        /// <param name="coords">The chunk coordinates whose flights should be held back.</param>
        /// <returns>A completion predicate for use with <see cref="RunFrame"/>.</returns>
        public static Func<LightingTestWorld.LightingJobFlight, int, bool> ExceptChunks(params Vector2Int[] coords)
        {
            return (flight, _) => Array.IndexOf(coords, flight.ChunkCoord) < 0;
        }

        #endregion

        #region Multi-Seed Sweep

        /// <summary>
        /// Runs a scenario body repeatedly with different RNG seeds, looking for the first seed that
        /// produces a failure. Each iteration creates a fresh <see cref="LightingTestWorld"/> via the
        /// factory, constructs a <see cref="LightingFrameSimulator"/> seeded with
        /// <c>startSeed + i</c>, executes the scenario body, and disposes the world.
        /// <para>
        /// The scenario body receives the world and simulator and must return <c>true</c> for pass,
        /// <c>false</c> for fail. When a seed fails, the scenario is deterministically reproducible —
        /// re-running with that exact seed produces the same frame-by-frame permutation sequence.
        /// </para>
        /// </summary>
        /// <param name="worldFactory">Factory that creates a fully set-up <see cref="LightingTestWorld"/>
        /// (chunks, initial lighting, block placement) for each iteration.</param>
        /// <param name="scenarioBody">The scenario to run. Returns <c>true</c> if the engine converges
        /// correctly, <c>false</c> if the seed reveals a failure.</param>
        /// <param name="iterations">Number of seeds to try.</param>
        /// <param name="startSeed">First seed value; subsequent seeds are <c>startSeed + 1</c>,
        /// <c>startSeed + 2</c>, etc.</param>
        /// <returns>The first failing seed, or <c>null</c> if all seeds pass.</returns>
        public static int? FindFailingSeed(
            Func<LightingTestWorld> worldFactory,
            Func<LightingTestWorld, LightingFrameSimulator, bool> scenarioBody,
            int iterations = 1000,
            int startSeed = 0)
        {
            for (int i = 0; i < iterations; i++)
            {
                int seed = startSeed + i;
                using LightingTestWorld world = worldFactory();
                LightingFrameSimulator sim = new LightingFrameSimulator(world, seed);

                if (!scenarioBody(world, sim))
                    return seed;
            }

            return null;
        }

        /// <summary>
        /// Geometry-fuzzing variant of <see cref="FindFailingSeed(System.Func{LightingTestWorld},System.Func{LightingTestWorld,LightingFrameSimulator,bool},int,int)"/>:
        /// the world factory itself receives the seed, so each iteration can randomize the GEOMETRY
        /// (border location, source block, held-in-flight chunk, edit sequence, …) as a pure function of
        /// the seed — not just the simulator's completion/scheduling order. The scenario body likewise
        /// receives the seed so it can reconstruct the same case. Because both geometry and ordering are
        /// derived deterministically from the seed, re-running a returned failing seed reproduces the
        /// exact case (the harness blind spot called out as finding C1 in
        /// LIGHTING_VALIDATION_HARNESS_FIDELITY.md).
        /// </summary>
        /// <param name="seededWorldFactory">Factory that builds a fully set-up <see cref="LightingTestWorld"/>
        /// from the iteration's seed (terrain, initial lighting, source placement). Geometry must be a pure
        /// function of the seed for reproducibility.</param>
        /// <param name="scenarioBody">The scenario to run; receives the world, the seeded simulator, and the
        /// seed (to reconstruct the case). Returns <c>true</c> on convergence-to-oracle, <c>false</c> on a
        /// revealed failure.</param>
        /// <param name="iterations">Number of seeds to try.</param>
        /// <param name="startSeed">First seed value; subsequent seeds increment by 1.</param>
        /// <returns>The first failing seed, or <c>null</c> if all seeds pass.</returns>
        public static int? FindFailingSeed(
            Func<int, LightingTestWorld> seededWorldFactory,
            Func<LightingTestWorld, LightingFrameSimulator, int, bool> scenarioBody,
            int iterations = 1000,
            int startSeed = 0)
        {
            for (int i = 0; i < iterations; i++)
            {
                int seed = startSeed + i;
                using LightingTestWorld world = seededWorldFactory(seed);
                LightingFrameSimulator sim = new LightingFrameSimulator(world, seed);

                if (!scenarioBody(world, sim, seed))
                    return seed;
            }

            return null;
        }

        #endregion

        /// <summary>
        /// In-place Fisher-Yates shuffle using the provided seeded RNG.
        /// </summary>
        private static void FisherYatesShuffle<T>(List<T> list, Random rng)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                T tmp = list[i];
                list[i] = list[j];
                list[j] = tmp;
            }
        }
    }
}
