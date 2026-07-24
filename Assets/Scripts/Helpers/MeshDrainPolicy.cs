namespace Helpers
{
    /// <summary>
    /// The scheduling-drain host: the two live-state reads the drain policy needs from its owner
    /// (production: <c>World</c>). Kept as a cached interface rather than per-frame delegates so the
    /// hot drain allocates nothing (the <c>ILightingCompletionDriver</c> precedent).
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
        /// <returns>The number of chunks scheduled this frame.</returns>
        public static int Drain(MeshBuildQueue queue, int quota, PipelinePassBudget.Window window,
            int cap, IMeshDrainHost host)
        {
            int scheduled = 0;

            MeshBuildQueue.Enumerator it = queue.GetEnumerator();
            while (it.MoveNext())
            {
                if (scheduled >= quota || window.Expired) break;

                // Re-check the in-flight cap every iteration, not just on entry: each successful schedule
                // grows the in-flight set, and one frame's whole quota must not push it past the cap. On a
                // fast-CPU / low-RAM device the per-frame quota (CPU-scaled) can far exceed the cap
                // (RAM-scaled), so the entry gate alone would let one frame overshoot the OM-1 ceiling.
                if (host.InFlightCount >= cap) break;

                Chunk chunk = it.Current;

                if (chunk is not { IsActive: true })
                {
                    it.RemoveCurrent();
                    continue;
                }

                // TrySchedule returns false when deps (neighbors/lighting) aren't ready — leave the chunk
                // queued (in place) to try again next frame.
                if (host.TrySchedule(chunk))
                {
                    it.RemoveCurrent();
                    scheduled++;
                }
            }

            return scheduled;
        }
    }
}
