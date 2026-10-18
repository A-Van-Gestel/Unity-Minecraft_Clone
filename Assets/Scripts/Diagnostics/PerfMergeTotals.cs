namespace Diagnostics
{
    /// <summary>
    /// Running totals of the lighting merge's main-thread parts — applying the job's light map (with the section recount
    /// inside it), draining deferred cross-chunk mods, verifying pull-back claims, routing the job's outbound mods, and the
    /// stability, cascade and re-mesh bookkeeping — with the work counts behind them, plus the chunks the unload pass
    /// tears down with skylight nodes still queued. The owner samples them into the per-frame <c>Merge*</c> and
    /// <c>Unload*</c> <see cref="PerfCounter"/>s.
    /// <para>
    /// Timed with <see cref="PerfTickTotals.Lap"/> from a <see cref="PerfStore.Begin()"/> start, and counted only when
    /// that start is non-zero, so below <see cref="PerfTier.Systems"/> each probe costs one static read. Main thread only.
    /// </para>
    /// </summary>
    public struct PerfMergeTotals
    {
        /// <summary>Extracting the job's band and merging it into the sections (<see cref="CountsTicks"/> included), in ticks.</summary>
        public long ApplyTicks;

        /// <summary>The per-section block recount inside the apply, in ticks.</summary>
        public long CountsTicks;

        /// <summary>Draining the cross-chunk mods other jobs deferred for the merged chunk, in ticks.</summary>
        public long DrainTicks;

        /// <summary>Re-verifying the job's pull-back claims against live neighbors, in ticks.</summary>
        public long PullBackTicks;

        /// <summary>Routing the job's outbound cross-chunk mods, support scans included, in ticks.</summary>
        public long RouteTicks;

        /// <summary>Stability, the edge-check cascade and the merge's mesh requests, in ticks.</summary>
        public long CascadeTicks;

        /// <summary>Lighting jobs merged.</summary>
        public long Merges;

        /// <summary>Merges that left their chunk stable — each requests its own and its cardinal neighbors' meshes.</summary>
        public long StableMerges;

        /// <summary>Stable merges whose apply reported no light change.</summary>
        public long StableUnchangedMerges;

        /// <summary>Mesh rebuilds a stable merge added to the queue for its own chunk; re-requests of a queued chunk are not counted.</summary>
        public long SelfRemeshes;

        /// <summary>Mesh rebuilds a stable merge added to the queue for its cardinal neighbors.</summary>
        public long NeighborRemeshes;

        /// <summary>The <see cref="SelfRemeshes"/> and <see cref="NeighborRemeshes"/> requested by unchanged stable merges.</summary>
        public long UnchangedRemeshes;

        /// <summary>Sections the apply walked that lie entirely outside the job's gathered band.</summary>
        public long OutOfBandSections;

        /// <summary>Absent sections the apply rented for uniform light only to compact and return them in the same merge.</summary>
        public long ChurnedSections;

        /// <summary>Chunks the unload pass tore down with skylight nodes still queued.</summary>
        public long UnloadPendingSkyChunks;

        /// <summary>The skylight nodes those chunks held.</summary>
        public long UnloadPendingSkyNodes;
    }
}
