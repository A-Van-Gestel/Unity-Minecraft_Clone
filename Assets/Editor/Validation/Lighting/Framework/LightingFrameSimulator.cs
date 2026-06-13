using System;
using System.Collections.Generic;
using UnityEngine;

namespace Editor.Validation.Lighting.Framework
{
    /// <summary>
    /// Wraps <see cref="LightingTestWorld"/> with a frame-tick orchestration layer that models the
    /// production scheduling behaviors the bare harness omits: the <c>ContainsKey</c> in-flight guard,
    /// per-frame job budget throttling, controllable completion ordering, and multi-frame job lifetimes.
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
        private int _currentFrame;

        /// <summary>
        /// Constructs a frame simulator wrapping the given test world. The test world must already be
        /// set up (chunks created, initial lighting done if needed).
        /// </summary>
        /// <param name="world">The underlying test world whose Begin/Complete API this simulator orchestrates.</param>
        public LightingFrameSimulator(LightingTestWorld world)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
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
                List<PendingFlight> toProcess;
                if (order == CompletionOrder.Reverse)
                {
                    toProcess = new List<PendingFlight>(_pendingFlights);
                    toProcess.Reverse();
                }
                else
                {
                    toProcess = new List<PendingFlight>(_pendingFlights);
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
            int scheduled = 0;
            foreach (Vector2Int coord in _world.AllChunkCoords())
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
    }
}
