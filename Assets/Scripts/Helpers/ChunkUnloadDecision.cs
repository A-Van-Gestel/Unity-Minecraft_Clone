namespace Helpers
{
    /// <summary>
    /// Pure per-chunk unload decision, mirroring <c>World.UnloadChunks</c>' deferral rules so the policy
    /// is truth-table-testable and its deferral reasons observable (the outer-lifecycle sibling of
    /// <see cref="LightingScanDecision"/> / the meshing schedule decision). The caller gathers the facts
    /// (distance test, job presence, lighting flags, neighbor strand scan) and performs the side effects
    /// (persist / save / pool teardown, or the per-reason CP-1 tally); this is a pure map from a chunk's
    /// current state to the arm the unloader should take.
    /// See Documentation/Design/CHUNK_LIFECYCLE_ORCHESTRATION_REFACTOR.md §4.1 (CP-5),
    /// Documentation/Design/CHUNK_PIPELINE_PERFORMANCE_ANALYSIS.md §3 rec 3 (P-4, the persist-and-unload
    /// arm), and Documentation/Architecture/CHUNK_LIFECYCLE_PIPELINE.md §9.6 (the stranding deadlock guard).
    /// </summary>
    public static class ChunkUnloadDecision
    {
        /// <summary>The action the unloader should take for one out-of-range candidate chunk.</summary>
        public enum Result : byte
        {
            /// <summary>Out of range and unpinned — proceed to persist / save / pool teardown.</summary>
            Unload,

            /// <summary>Within the unload distance — not a candidate this pass.</summary>
            KeepInRange,

            /// <summary>A generation / mesh / lighting job still owns buffers for this chunk.</summary>
            DeferJobRunning,

            /// <summary>
            /// Pending main-thread lighting work with no persist-and-unload path. Retained for the CP-1 tally
            /// and LP-3 coordination; since P-4 rec 3 it is no longer returned for out-of-range chunks (they
            /// take <see cref="UnloadPersistLightPending"/> instead), so its per-pass count now reads ~0 — the
            /// pinned-trail-drained signal.
            /// </summary>
            DeferLightPending,

            /// <summary>A populated, <b>in-range</b> neighbor still needs this chunk's data for lighting
            /// (pipeline §9.6). Out-of-range neighbors do not defer — they are being reclaimed too.</summary>
            DeferWouldStrand,

            /// <summary>
            /// P-4 rec 3: out of range and pinned only by its own pending/initial lighting (no job, no in-range
            /// strand). The caller persists the pending lighting and unloads instead of deferring forever —
            /// draining the pinned trail whose lighting can never complete (missing-neighbor gate).
            /// </summary>
            UnloadPersistLightPending,
        }

        /// <summary>
        /// Value-type facts feeding <see cref="Evaluate"/>. Plain bools + the distance test — no chunk
        /// references — so the decision stays pure and the caller owns all world/pool access.
        /// </summary>
        public readonly struct ChunkUnloadFacts
        {
            /// <summary>Chunk lies beyond the unload boundary (<c>World.IsBeyondUnloadDistance</c>).</summary>
            public readonly bool BeyondUnloadDistance;

            /// <summary>A generation, mesh, or lighting job is currently keyed on this chunk.</summary>
            public readonly bool JobRunning;

            /// <summary>Pending main-thread lighting work: <c>IsAwaitingMainThreadProcess || HasLightChangesToProcess</c>.</summary>
            public readonly bool ProcessingLight;

            /// <summary>
            /// A populated <b>in-range</b> neighbor needs this chunk's data for lighting
            /// (<c>HasLightChangesToProcess || NeedsInitialLighting</c> on any of the 8 neighbors that is
            /// itself within the unload distance). Out-of-range strand neighbors are excluded: they are being
            /// reclaimed on this or a later pass, so stranding them is harmless (P-4 rec 3). This preserves the
            /// §9.6 guard for genuinely-needed neighbors while letting the trail drain.
            /// </summary>
            public readonly bool WouldStrandInRangeNeighbor;

            /// <summary>Creates the fact set for one candidate chunk.</summary>
            /// <param name="beyondUnloadDistance">Chunk lies beyond the unload boundary.</param>
            /// <param name="jobRunning">A job is currently keyed on this chunk.</param>
            /// <param name="processingLight">Pending main-thread lighting work on this chunk.</param>
            /// <param name="wouldStrandInRangeNeighbor">A populated, in-range neighbor still needs this chunk's data.</param>
            public ChunkUnloadFacts(bool beyondUnloadDistance, bool jobRunning, bool processingLight, bool wouldStrandInRangeNeighbor)
            {
                BeyondUnloadDistance = beyondUnloadDistance;
                JobRunning = jobRunning;
                ProcessingLight = processingLight;
                WouldStrandInRangeNeighbor = wouldStrandInRangeNeighbor;
            }
        }

        /// <summary>
        /// Decides the unload arm for one chunk. In-range chunks are kept. Otherwise, a running job pins
        /// first; then the §9.6 strand rule defers for an <b>in-range</b> neighbor that genuinely needs this
        /// chunk's data; then a chunk pinned only by its own pending lighting is persisted-and-unloaded
        /// (P-4 rec 3) rather than deferred forever; a fully-unpinned out-of-range chunk unloads.
        /// </summary>
        /// <remarks>
        /// The precedence of the strand check <b>above</b> the light-pending check is load-bearing: a chunk
        /// that both is light-pending and would strand an in-range neighbor must DEFER (protect the neighbor),
        /// not persist-and-unload. Only when no in-range neighbor needs it is its own unfinishable lighting
        /// safe to shed to persistence.
        /// </remarks>
        /// <param name="facts">The gathered facts for this candidate chunk.</param>
        /// <returns>The arm the unloader should take.</returns>
        public static Result Evaluate(in ChunkUnloadFacts facts)
        {
            if (!facts.BeyondUnloadDistance) return Result.KeepInRange;
            if (facts.JobRunning) return Result.DeferJobRunning;
            if (facts.WouldStrandInRangeNeighbor) return Result.DeferWouldStrand;
            if (facts.ProcessingLight) return Result.UnloadPersistLightPending;
            return Result.Unload;
        }
    }
}
