namespace Helpers
{
    /// <summary>
    /// The scheduling-drain host: the two live-state reads the drain policy needs from its owner
    /// (production: <c>World</c>). Kept as a cached interface rather than per-frame delegates so the
    /// hot drain allocates nothing (the <c>IJobCompletionDriver</c> precedent).
    /// </summary>
    public interface IMeshDrainHost
    {
        /// <summary>Live count of mesh jobs currently in flight (production: <c>MeshJobs.Count</c>).
        /// Read every iteration because a successful schedule grows it (production: <c>ScheduleMeshing</c>).</summary>
        int InFlightCount { get; }

        /// <summary>Attempts to schedule a mesh job for the chunk, returning true when the caller
        /// should dequeue it (production: <c>WorldJobManager.ScheduleMeshing</c>).</summary>
        /// <param name="chunk">The queued chunk to attempt.</param>
        /// <returns>True when the chunk was scheduled and should be removed from the queue; false to leave it
        /// queued for a later frame (dependencies not ready, or a job is already in flight — the MP-3 retry).</returns>
        bool TrySchedule(Chunk chunk);
    }

    /// <summary>
    /// Pure per-frame policy for draining the mesh-build queue: the loop that <c>World.Update</c>
    /// runs and the meshing validation suite replays, sharing one implementation so a policy change
    /// (stop conditions, purge, remove-vs-leave, priority order) can never diverge between production
    /// and its baseline. The budget <i>math</i> (<see cref="PipelinePassBudget.ComputeQuota"/> /
    /// <see cref="PipelinePassBudget.Window"/>) is derived by the caller and passed in — this owns
    /// only the loop that consumes it.
    /// </summary>
    /// <summary>
    /// What one <see cref="MeshDrainPolicy.Drain"/> pass did: how many chunks it scheduled, and
    /// <b>why it stopped</b> (FP-2). The reason is returned rather than re-derived by the caller because
    /// re-reading the limits after the loop cannot distinguish them — a pass that broke on quota may also
    /// have an expired window by the time anyone asks, and would then be misreported as ceiling-bound.
    /// </summary>
    public readonly struct DrainResult
    {
        /// <summary>Chunks scheduled this frame.</summary>
        public readonly int Scheduled;

        /// <summary>Why the drain stopped.</summary>
        public readonly PassStopReason Reason;

        /// <summary>Initializes a drain result.</summary>
        /// <param name="scheduled">Chunks scheduled this frame.</param>
        /// <param name="reason">Why the drain stopped.</param>
        public DrainResult(int scheduled, PassStopReason reason)
        {
            Scheduled = scheduled;
            Reason = reason;
        }
    }

    public static class MeshDrainPolicy
    {
        /// <summary>
        /// Walks <paramref name="queue"/> in priority order (head = highest), scheduling ready chunks
        /// until a limit is hit. Stops when the per-frame <paramref name="quota"/> is spent, the time
        /// <paramref name="window"/> expires, or the in-flight <paramref name="cap"/> is reached
        /// (re-checked every iteration against the live <see cref="IMeshDrainHost.InFlightCount"/>, since
        /// each schedule grows it). Null/inactive chunks are purged in place; a scheduled chunk is
        /// removed; a declined chunk is left queued to retry next frame.
        /// </summary>
        /// <param name="queue">The mesh-build queue to drain (mutated: scheduled/purged entries removed).</param>
        /// <param name="quota">Max chunks to schedule this frame (the rate budget; <c>maxMeshRebuildsPerFrame</c> or its FPS-scaled quota).</param>
        /// <param name="window">The time ceiling for this pass (<c>default</c> = unbounded).</param>
        /// <param name="cap">The in-flight mesh-job ceiling (OM-1 memory bound).</param>
        /// <param name="host">The live-state provider + schedule sink (production: <c>World</c>).</param>
        /// <returns>The chunks scheduled and the reason the pass stopped.</returns>
        public static DrainResult Drain(MeshBuildQueue queue, int quota, PipelinePassBudget.Window window,
            int cap, IMeshDrainHost host)
        {
            int scheduled = 0;

            // FP-2: candidates actually examined. Distinguishes "walked a queue and nothing was eligible"
            // (AllDeclined — readiness-bound) from "the queue was empty" (OutOfWork — healthy). Purged
            // null/inactive entries do NOT count: they were never real work.
            int candidatesSeen = 0;

            bool quotaSpent = false;
            bool ceilingExpired = false;
            bool capReached = false;

            MeshBuildQueue.Enumerator it = queue.GetEnumerator();
            while (it.MoveNext())
            {
                if (scheduled >= quota)
                {
                    quotaSpent = true;
                    break;
                }

                if (window.Expired)
                {
                    ceilingExpired = true;
                    break;
                }

                // Re-check the in-flight cap every iteration, not just on entry: each successful schedule
                // grows the in-flight set, and one frame's whole quota must not push it past the cap. On a
                // fast-CPU / low-RAM device the per-frame quota (CPU-scaled) can far exceed the cap
                // (RAM-scaled), so the entry gate alone would let one frame overshoot the OM-1 ceiling.
                if (host.InFlightCount >= cap)
                {
                    capReached = true;
                    break;
                }

                Chunk chunk = it.Current;

                if (chunk is not { IsActive: true })
                {
                    it.RemoveCurrent();
                    continue;
                }

                candidatesSeen++;

                // TrySchedule returns false when deps (neighbors/lighting) aren't ready — leave the chunk
                // queued (in place) to try again next frame.
                if (host.TrySchedule(chunk))
                {
                    it.RemoveCurrent();
                    scheduled++;
                }
            }

            return new DrainResult(scheduled, PipelinePassBudget.ClassifyStop(
                scheduled, candidatesSeen, quotaSpent, ceilingExpired, capReached));
        }
    }
}
