/// <summary>
/// Pure decision function for the per-chunk arm of the lighting scheduler's ready-set scan.
/// Both <c>World.Update</c>'s scheduling phase and the editor validation <c>LightingFrameSimulator</c>
/// (AS-2 scheduler mode) call this, so the two can never silently disagree on which arm a flagged,
/// ready chunk takes — the completion of the shared-guard pattern started by
/// <see cref="LightingScheduleDecision"/> (which covers only the in-flight / neighbors-data-ready gate).
/// The caller performs the side effects (schedule / remove / park) and the per-frame budget throttle;
/// this is a pure map from a chunk's current flag + gate state to the intended arm.
/// See Documentation/Design/LIGHTING_ASYNC_BUG_VALIDATION_ROADMAP.md §4 (AS-2) / §10 (HF-4).
/// </summary>
public static class LightingScanDecision
{
    /// <summary>The action the scan should take for one ready chunk, given its flags and neighbor gates.</summary>
    public enum ScanAction : byte
    {
        /// <summary>Schedule a first full lighting pass (chunk needs initial lighting, neighbor terrain ready).</summary>
        ScheduleInitial,

        /// <summary>Schedule a lighting job with the border edge-check enabled (edge check pending, neighbors lit).</summary>
        ScheduleEdge,

        /// <summary>Schedule a regular lighting update (pending light changes, neighbor terrain ready).</summary>
        ScheduleRegular,

        /// <summary>No lighting flags remain — forget the chunk (remove from both scheduler sets).</summary>
        Remove,

        /// <summary>Flags remain but a readiness gate failed (or a job is in flight) — park until a promotion event.</summary>
        Park,
    }

    /// <summary>
    /// Decides the scan action for a chunk that is currently in the ready set, mirroring the per-chunk
    /// arm logic of the production scheduling scan (<c>World.Update</c>, the ready-set loop): initial
    /// lighting takes priority and gates on terrain-data readiness; the edge-consistency check gates on
    /// fully-lit neighbors; a regular update is the fall-through (including when an edge check is pending
    /// but neighbors are not lit yet). A chunk whose lighting flags are all clear is forgotten; one whose
    /// flags remain but whose gate failed (or whose job is still in flight) is parked for a promotion event.
    /// </summary>
    /// <param name="jobInFlight">A lighting job is already running for this chunk (production: <c>LightingJobs.ContainsKey</c>).</param>
    /// <param name="needsInitialLighting"><c>ChunkData.NeedsInitialLighting</c>.</param>
    /// <param name="needsEdgeCheck"><c>ChunkData.NeedsEdgeCheck</c>.</param>
    /// <param name="hasLightChanges"><c>ChunkData.HasLightChangesToProcess</c>.</param>
    /// <param name="neighborsDataReady">All neighbors have populated terrain data (<c>AreNeighborsDataReady</c>) — gates the initial and regular arms.</param>
    /// <param name="neighborsReadyAndLit">All neighbors are fully lit and stable (<c>AreNeighborsReadyAndLit</c>) — gates the edge arm.</param>
    /// <returns>The scan action the caller should perform.</returns>
    public static ScanAction EvaluateReadyChunk(
        bool jobInFlight,
        bool needsInitialLighting,
        bool needsEdgeCheck,
        bool hasLightChanges,
        bool neighborsDataReady,
        bool neighborsReadyAndLit)
    {
        // A job is already running — its completion promotes the chunk (production parks it: MarkWaiting).
        if (jobInFlight) return ScanAction.Park;

        // Initial lighting takes priority; it gates on terrain-data readiness only.
        if (needsInitialLighting)
            return neighborsDataReady ? ScanAction.ScheduleInitial : ScanAction.Park;

        // Edge consistency check: needs fully-lit neighbors so the border comparison reads settled data.
        if (needsEdgeCheck && neighborsReadyAndLit)
            return ScanAction.ScheduleEdge;

        // Regular lighting update — also the fall-through when an edge check is pending but neighbors are
        // not lit yet (production's `!scheduled && HasLightChangesToProcess && AreNeighborsDataReady`).
        if (hasLightChanges && neighborsDataReady)
            return ScanAction.ScheduleRegular;

        // Nothing schedulable. No flags at all → forget the chunk; otherwise a gate failed → park.
        if (!needsInitialLighting && !needsEdgeCheck && !hasLightChanges)
            return ScanAction.Remove;

        return ScanAction.Park;
    }
}
