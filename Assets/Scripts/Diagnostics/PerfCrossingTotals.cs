namespace Diagnostics
{
    /// <summary>
    /// Running totals of a view-distance crossing's two passes: the <see cref="PerfSlot.ViewDistance"/> pass (clouds, the
    /// spiral scan, the deactivate scan, deactivation, activation and the active-set update) and the
    /// <see cref="PerfSlot.Unload"/> pass (candidate scan, unload decision, bookkeeping, save, visual teardown and
    /// data return), each with the work counts behind it. The owner samples them into the per-frame <c>Xing*</c>
    /// and <c>Unload*</c> <see cref="PerfCounter"/>s.
    /// </summary>
    public struct PerfCrossingTotals
    {
        /// <summary>The player's chunk changed and both passes ran: the divisor of every per-crossing figure.</summary>
        public long Crossings;

        /// <summary>Updating the clouds before the crossing test, in ticks.</summary>
        public long CloudsTicks;

        /// <summary>Scanning the load square: placeholders, generation requests and the view candidate set, in ticks.</summary>
        public long SpiralTicks;

        /// <summary>Collecting the active chunks that left view, in ticks.</summary>
        public long DeactScanTicks;

        /// <summary>Tearing down the chunks that left view — borders, pool returns and mesh-queue removals, in ticks.</summary>
        public long DeactivateTicks;

        /// <summary>Bringing the chunks that entered view up: pool gets, borders and mesh requests, in ticks.</summary>
        public long ActivateTicks;

        /// <summary>Rebuilding the master active-chunk set, in ticks.</summary>
        public long ActiveSetTicks;

        /// <summary>Generation requests this crossing re-enqueued for the per-frame drain.</summary>
        public long GenEnqueued;

        /// <summary>Chunks this crossing took from the pool or reactivated.</summary>
        public long Activations;

        /// <summary>Chunks this crossing left view and tore down.</summary>
        public long Deactivations;

        /// <summary>Chunks outside the unload boundary this pass considered; the divisor of the unload figures.</summary>
        public long UnloadCandidates;

        /// <summary>The pass's candidate scan of the resident chunks, in ticks.</summary>
        public long UnloadScanTicks;

        /// <summary>Gathering the unload facts and deciding each candidate, deferrals included, in ticks.</summary>
        public long UnloadDecideTicks;

        /// <summary>A leaving chunk's telemetry stamp, pending-sky count and orphaned skylight columns, in ticks.</summary>
        public long UnloadBookkeepTicks;

        /// <summary>Saving the modified candidates; a candidate that needed no save is still timed, in ticks.</summary>
        public long UnloadSaveTicks;

        /// <summary>Recycling the leaving chunk's visuals: border, mesh-queue entry and pooled <c>Chunk</c>, in ticks.</summary>
        public long UnloadVisualTicks;

        /// <summary>Removing the chunk from the world and handing its data back to the data pool, in ticks.</summary>
        public long UnloadDataReturnTicks;

        /// <summary>Chunks this pass tore down and recycled.</summary>
        public long UnloadedChunks;

        /// <summary>Unloaded chunks whose edits were handed to a save.</summary>
        public long UnloadSaved;

        /// <summary>Voxel sections those saved chunks carried, for the save's payload size.</summary>
        public long UnloadSavedSections;

        /// <summary>Candidates deferred because a job was touching them.</summary>
        public long UnloadDeferJob;

        /// <summary>Candidates deferred because an in-range neighbor still needed their data.</summary>
        public long UnloadDeferStrand;
    }
}
