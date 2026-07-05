using System;
using System.Collections.Generic;
using Helpers;
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

            /// <summary>Chunks that had pending work but were deferred because their neighbor terrain data
            /// was not ready — production's <see cref="LightingScheduleDecision.Result.NeighborsNotReady"/>
            /// arm (<c>WorldJobManager.ScheduleLightingUpdate</c> sets <c>HasLightChangesToProcess = true</c>
            /// and returns without scheduling). The work is retained and retried once
            /// <see cref="LightingTestWorld.MarkNeighborsReady"/> is called. Kept separate from
            /// <see cref="ChunksStarved"/> so budget-pressure baselines stay meaningful.</summary>
            public int ChunksNeighborsNotReady;

            /// <summary>Scheduler mode only: chunks parked into the waiting set this frame — flags remain but a
            /// readiness gate failed or a job is in flight (production's <c>MarkWaiting</c> arms). A parked
            /// chunk re-enters the ready set only via a promotion event or the fail-safe.</summary>
            public int ChunksParked;

            /// <summary>Scheduler mode only: parked chunks promoted back into the ready set this frame by a
            /// completion-driven <c>PromoteNeighborhood</c> (the load-bearing MT-2 hook).</summary>
            public int ChunksPromoted;

            /// <summary>Scheduler mode only: positions dequeued from the scheduler's thread-safe staging queue
            /// this frame (flag callbacks fired by edits / mid-flight re-flags).</summary>
            public int StagingDrained;
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

        // AS-2 scheduler mode: a real LightWorkScheduler drives Phase 2 off a ready/waiting split instead
        // of the pre-MT-2 AllChunkCoords() scan. Null in legacy mode (every existing baseline byte-identical).
        private readonly LightWorkScheduler _scheduler;
        private readonly bool _schedulerMode;

        // Scratch list reused across scheduler-mode frames for the ready-set snapshot (no per-frame alloc).
        private readonly List<Vector2Int> _readyScratch = new List<Vector2Int>();

        /// <summary>
        /// Scheduler mode only: when &gt; 0, the ~1 s <c>PromoteAll</c> fail-safe is simulated every N frames
        /// (a seed scan + <see cref="LightWorkScheduler.PromoteAll"/>). Default 0 = OFF — the load-bearing
        /// AS-2 assertion is that scenarios converge with the fail-safe off; any scenario that needs it on has
        /// found a missing promotion hook. Also drivable manually via <see cref="FailSafePromoteAll"/>.
        /// </summary>
        public int PromoteAllEveryNFrames { get; set; }

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
        /// <param name="schedulerMode">AS-2 opt-in: route Phase 2 through a real <see cref="LightWorkScheduler"/>
        /// (ready/waiting split + promotion events) instead of the pre-MT-2 full grid scan. Wires the world's
        /// <c>OnLightWorkFlagged</c> callback to the scheduler's staging queue and seeds the ready set from any
        /// currently-flagged chunks, so edits and mid-flight re-flags stage automatically. Default <c>false</c>
        /// keeps the legacy scan and leaves every existing baseline byte-identical.</param>
        public LightingFrameSimulator(LightingTestWorld world, int? seed = null, bool schedulerMode = false)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _rng = seed.HasValue ? new Random(seed.Value) : null;
            _schedulerMode = schedulerMode;

            if (schedulerMode)
            {
                _scheduler = new LightWorkScheduler();
                _world.SetLightWorkFlagSink(_scheduler.Flag);
                SeedReadyFromFlags();
            }
        }

        /// <summary>The scheduler backing AS-2 scheduler mode, or <c>null</c> in legacy mode. Diagnostic accessor
        /// (ready/waiting counts) for scenario assertions.</summary>
        public LightWorkScheduler Scheduler => _scheduler;

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
        /// <see cref="LightingScheduleDecision"/> in-flight guard and the neighbor-data-ready guard
        /// (chunks whose neighbors are not ready, per <see cref="LightingTestWorld.MarkNeighborsNotReady"/>,
        /// are deferred — they keep their work for a later frame).</item>
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

                    Vector2Int completedCoord = pending.Flight.ChunkCoord;
                    _world.CompleteLightingJob(pending.Flight);
                    result.JobsCompleted++;

                    // Completion is the load-bearing MT-2 promotion hook: it un-parks the chunk itself (if it
                    // re-flagged mid-flight) and any neighbor whose AreNeighborsReadyAndLit gate this completion
                    // just cleared (production: WorldJobManager.ProcessLightingJobs → PromoteLightWorkNeighborhood).
                    if (_schedulerMode)
                        result.ChunksPromoted += _scheduler.PromoteNeighborhood(ToVoxelOrigin(completedCoord));
                }

                _pendingFlights.Clear();
                _pendingFlights.AddRange(carriedOver);
            }

            // --- Phase 2: Schedule new jobs ---
            if (_schedulerMode)
            {
                RunSchedulerPhase2(budget, ref result);
                return result;
            }

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
                    _world.AreNeighborsDataReady(coord));

                if (decision == LightingScheduleDecision.Result.AlreadyInFlight)
                {
                    result.ChunksStarved++;
                    continue;
                }

                if (decision == LightingScheduleDecision.Result.NeighborsNotReady)
                {
                    // Mirror production WorldJobManager.ScheduleLightingUpdate: leave the chunk's light
                    // work flagged and DON'T schedule. The chunk only reached here because it already has
                    // light work (the ChunkHasLightWork gate above), so the flag is already set — the
                    // production "HasLightChangesToProcess = true" assignment is a no-op here. The work is
                    // retried on a later frame once MarkNeighborsReady is called.
                    result.ChunksNeighborsNotReady++;
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

        #region AS-2 Scheduler Mode

        /// <summary>
        /// AS-2 scheduler-mode Phase 2: drains the staging queue, optionally runs the fail-safe, then
        /// schedules off the ready set ONLY — mirroring the production scan (<c>World.Update</c>) arm-for-arm
        /// via the shared <see cref="LightingScanDecision"/>. Parked chunks are invisible to the scan until a
        /// promotion event re-adds them (completion hook, neighbor load/generation, or the fail-safe).
        /// </summary>
        /// <param name="budget">Maximum new jobs to schedule this frame.</param>
        /// <param name="result">The frame result to accumulate scheduler counters into.</param>
        private void RunSchedulerPhase2(int budget, ref FrameResult result)
        {
            result.StagingDrained = _scheduler.DrainStaging();

            if (PromoteAllEveryNFrames > 0 && _currentFrame % PromoteAllEveryNFrames == 0)
                FailSafePromoteAll();

            _readyScratch.Clear();
            _scheduler.SnapshotReady(_readyScratch);
            if (_rng != null)
                FisherYatesShuffle(_readyScratch, _rng);

            int scheduled = 0;
            foreach (Vector2Int pos in _readyScratch)
            {
                Vector2Int coord = ToGridCoord(pos);

                // Stale entry for a chunk outside the grid (production: worldData.Chunks miss → Remove).
                if (!_world.HasChunk(coord))
                {
                    _scheduler.Remove(pos);
                    continue;
                }

                LightingScanDecision.ScanAction action = LightingScanDecision.EvaluateReadyChunk(
                    _world.IsChunkInFlight(coord),
                    _world.ChunkNeedsInitialLighting(coord),
                    _world.ChunkNeedsEdgeCheck(coord),
                    _world.ChunkHasLightWork(coord),
                    _world.AreNeighborsDataReady(coord),
                    _world.AreNeighborsReadyAndLit(coord));

                switch (action)
                {
                    case LightingScanDecision.ScanAction.Remove:
                        _scheduler.Remove(pos);
                        break;

                    case LightingScanDecision.ScanAction.Park:
                        _scheduler.MarkWaiting(pos);
                        result.ChunksParked++;
                        break;

                    default: // one of the Schedule* arms
                        if (scheduled >= budget)
                        {
                            // Budget exhausted — leave the chunk in the ready set (starved), retried next frame.
                            result.ChunksStarved++;
                            break;
                        }

                        // BeginLightingJob clears the chunk's flags; the entry stays in the ready set and is
                        // parked next frame by the in-flight arm, then un-parked by the completion promotion.
                        bool edgeCheck = action == LightingScanDecision.ScanAction.ScheduleEdge;
                        _pendingFlights.Add(new PendingFlight
                        {
                            Flight = _world.BeginLightingJob(coord, edgeCheck),
                            ScheduledOnFrame = _currentFrame,
                        });
                        scheduled++;
                        break;
                }
            }

            result.JobsScheduled = scheduled;
        }

        /// <summary>
        /// Simulates the production ~1 s fail-safe scan (<c>World.Update</c>): re-seeds the ready set from
        /// every flagged chunk, then promotes every parked chunk. Default OFF in scenarios (see
        /// <see cref="PromoteAllEveryNFrames"/>) — the load-bearing AS-2 assertion is that scenarios converge
        /// WITHOUT it; a scenario that only converges once this runs has found a missing promotion hook.
        /// </summary>
        /// <returns>The number of parked chunks promoted by the backstop (production logs a recurring
        /// non-zero count as a bug).</returns>
        public int FailSafePromoteAll()
        {
            if (!_schedulerMode)
                throw new InvalidOperationException("FailSafePromoteAll requires scheduler mode.");

            SeedReadyFromFlags();
            return _scheduler.PromoteAll();
        }

        /// <summary>
        /// Scheduler-mode-aware wrapper over <see cref="LightingTestWorld.MarkNeighborsReady"/>: marks the
        /// chunk's neighbor terrain data ready AND promotes its 3×3 neighborhood, mirroring production's
        /// neighbor-generation-complete promotion. Use this from scheduler-mode scenarios instead of the world
        /// method directly, so the unblock event carries its promotion.
        /// </summary>
        /// <param name="chunkCoord">The grid coordinate whose neighbors are now ready.</param>
        public void MarkNeighborsReady(Vector2Int chunkCoord)
        {
            _world.MarkNeighborsReady(chunkCoord);
            if (_schedulerMode)
                _scheduler.PromoteNeighborhood(ToVoxelOrigin(chunkCoord));
        }

        /// <summary>
        /// Scheduler-mode-aware wrapper over <see cref="LightingTestWorld.MarkChunkLoaded"/>: loads the chunk
        /// (replaying pending light work) AND promotes its 3×3 neighborhood, mirroring production's disk-load /
        /// generation-complete promotion (<c>World.cs:809</c>).
        /// </summary>
        /// <param name="chunkCoord">The grid coordinate to load.</param>
        /// <param name="mode">Disk-load (replay blocklight) vs. fresh generation (discard blocklight).</param>
        public void MarkChunkLoaded(Vector2Int chunkCoord,
            LightingTestWorld.ChunkLoadMode mode = LightingTestWorld.ChunkLoadMode.LoadFromDisk)
        {
            _world.MarkChunkLoaded(chunkCoord, mode);
            if (_schedulerMode)
                _scheduler.PromoteNeighborhood(ToVoxelOrigin(chunkCoord));
        }

        /// <summary>Seeds the ready set from every currently-flagged chunk (scheduler-mode entry + the
        /// fail-safe's full scan) — mirror of <c>World.Update</c>'s fail-safe AddReady loop.</summary>
        private void SeedReadyFromFlags()
        {
            foreach (Vector2Int coord in _world.AllChunkCoords())
            {
                if (IsChunkFlagged(coord))
                    _scheduler.AddReady(ToVoxelOrigin(coord));
            }
        }

        /// <summary>True if a chunk has any pending lighting flag (the fail-safe scan's AddReady predicate).</summary>
        private bool IsChunkFlagged(Vector2Int coord) =>
            _world.ChunkNeedsInitialLighting(coord) || _world.ChunkHasLightWork(coord) || _world.ChunkNeedsEdgeCheck(coord);

        /// <summary>Grid coordinate → the voxel-origin position the scheduler keys on (<c>ChunkData.Position</c>).</summary>
        private static Vector2Int ToVoxelOrigin(Vector2Int gridCoord) =>
            new Vector2Int(gridCoord.x * VoxelData.ChunkWidth, gridCoord.y * VoxelData.ChunkWidth);

        /// <summary>Voxel-origin position → grid coordinate (inverse of <see cref="ToVoxelOrigin"/>).</summary>
        private static Vector2Int ToGridCoord(Vector2Int voxelOrigin) =>
            new Vector2Int(voxelOrigin.x / VoxelData.ChunkWidth, voxelOrigin.y / VoxelData.ChunkWidth);

        #endregion

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
                {
                    // Quiescent. If a border-column opacity edit re-granted edge-check rounds
                    // (ChunkData.ModifyVoxel / the harness mirror, Bug 05), run one edge round on the
                    // now-SETTLED field and keep converging: the post-edit cross-seam under-report only
                    // reconciles when the edge check reads settled neighbor data, not mid-churn. Mirror of
                    // RunInitialLighting's post-convergence edge loop; add-only and bounded, so it terminates.
                    if (_world.RunReGrantedEdgeCheckRound())
                        continue;

                    return frame;
                }

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
        /// <param name="schedulerMode">Construct each iteration's simulator in AS-2 scheduler mode
        /// (ready/waiting split + promotion events) instead of the legacy full grid scan. Default <c>false</c>.</param>
        public static int? FindFailingSeed(
            Func<LightingTestWorld> worldFactory,
            Func<LightingTestWorld, LightingFrameSimulator, bool> scenarioBody,
            int iterations = 1000,
            int startSeed = 0,
            bool schedulerMode = false)
        {
            for (int i = 0; i < iterations; i++)
            {
                int seed = startSeed + i;
                using LightingTestWorld world = worldFactory();
                LightingFrameSimulator sim = new LightingFrameSimulator(world, seed, schedulerMode);

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
        /// <param name="schedulerMode">Construct each iteration's simulator in AS-2 scheduler mode
        /// (ready/waiting split + promotion events) instead of the legacy full grid scan. Default <c>false</c>.</param>
        public static int? FindFailingSeed(
            Func<int, LightingTestWorld> seededWorldFactory,
            Func<LightingTestWorld, LightingFrameSimulator, int, bool> scenarioBody,
            int iterations = 1000,
            int startSeed = 0,
            bool schedulerMode = false)
        {
            for (int i = 0; i < iterations; i++)
            {
                int seed = startSeed + i;
                using LightingTestWorld world = seededWorldFactory(seed);
                LightingFrameSimulator sim = new LightingFrameSimulator(world, seed, schedulerMode);

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
                (list[i], list[j]) = (list[j], list[i]);
            }
        }
    }
}
