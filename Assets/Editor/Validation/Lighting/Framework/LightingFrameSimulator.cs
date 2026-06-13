using System;
using System.Collections.Generic;
using UnityEngine;

namespace Editor.Validation.Lighting.Framework
{
    /// <summary>
    /// Wraps <see cref="LightingTestWorld"/> with a frame-tick orchestration layer that models the
    /// production scheduling behaviors the bare harness omits: the <c>ContainsKey</c> in-flight guard,
    /// per-frame job budget throttling, and controllable completion ordering. This enables deterministic
    /// reproduction of orchestration-layer timing bugs (e.g., Bug 09) that only manifest when scheduling
    /// is rejected or delayed.
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
        }

        private readonly LightingTestWorld _world;
        private readonly List<LightingTestWorld.LightingJobFlight> _pendingFlights = new List<LightingTestWorld.LightingJobFlight>();

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

        /// <summary>
        /// Executes one simulated frame tick, mirroring the production <c>ProcessLightingJobs</c> →
        /// <c>World.Update</c> lighting-scan cycle:
        /// <list type="number">
        /// <item>Complete all pending flights (in the specified order), applying cross-chunk mods via
        /// the existing <see cref="LightingTestWorld.CompleteLightingJob"/> logic.</item>
        /// <item>Schedule new jobs for chunks with pending work, up to the budget, respecting the
        /// <see cref="LightingScheduleDecision"/> in-flight guard.</item>
        /// </list>
        /// </summary>
        /// <param name="budget">Maximum number of new jobs to schedule this frame (mirrors
        /// <c>settings.maxLightJobsPerFrame</c>). Pass <see cref="int.MaxValue"/> for unlimited.</param>
        /// <param name="order">The order in which pending flights are completed. Affects which
        /// cross-chunk mods are deferred vs. applied directly (the <c>_completedLightJobs</c> ordering
        /// dependency in production's <c>ProcessLightingJobs</c>).</param>
        /// <returns>Statistics for this frame tick.</returns>
        public FrameResult RunFrame(int budget = int.MaxValue, CompletionOrder order = CompletionOrder.Fifo)
        {
            FrameResult result = default;

            // --- Phase 1: Complete pending flights ---
            if (_pendingFlights.Count > 0)
            {
                List<LightingTestWorld.LightingJobFlight> toComplete;
                if (order == CompletionOrder.Reverse)
                {
                    toComplete = new List<LightingTestWorld.LightingJobFlight>(_pendingFlights);
                    toComplete.Reverse();
                }
                else
                {
                    toComplete = _pendingFlights;
                }

                foreach (LightingTestWorld.LightingJobFlight flight in toComplete)
                {
                    _world.CompleteLightingJob(flight);
                    result.JobsCompleted++;
                }

                _pendingFlights.Clear();
            }

            // --- Phase 2: Schedule new jobs ---
            int scheduled = 0;
            foreach (Vector2Int coord in _world.AllChunkCoords())
            {
                if (!_world.HasPendingLightWork(coord))
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

                _pendingFlights.Add(_world.BeginLightingJob(coord));
                scheduled++;
            }

            result.JobsScheduled = scheduled;
            return result;
        }

        /// <summary>
        /// Runs frame ticks until all chunks converge (no pending light work and no in-flight jobs)
        /// or the frame budget is exhausted. This is the frame-aware equivalent of
        /// <see cref="LightingTestWorld.RunToConvergence"/>.
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
    }
}
