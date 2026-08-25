using Data;

namespace Helpers
{
    /// <summary>
    /// Pure decision function for the per-chunk arm of the lighting scheduler's ready-set scan: a map from a
    /// chunk's current flag + gate state to the arm it should take. Callers own the side effects (schedule /
    /// remove / park) and any per-frame budget throttle.
    /// <para>
    /// This is the sole arm-selection rule for the engine — it exists so that no scheduling path can hold a
    /// private opinion of which arm a flagged, ready chunk takes. Adding a second implementation defeats its
    /// purpose. Completes the shared-guard pattern started by <see cref="LightingScheduleDecision"/>, which
    /// covers only the in-flight / neighbors-data-ready gate.
    /// </para>
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
        /// <remarks>
        /// <b>Caller contract.</b> A caller that keeps ready/waiting sets must implement all three bullets
        /// identically — divergence here is the drift this type exists to prevent:
        /// <list type="bullet">
        /// <item><c>Park</c> → <c>MarkWaiting(pos)</c>.</item>
        /// <item><c>Remove</c> → <c>Remove(pos)</c>.</item>
        /// <item><c>ScheduleInitial</c> / <c>ScheduleEdge</c> / <c>ScheduleRegular</c> → perform the arm's side
        /// effects (initial: full skylight recalc; edge: set <c>HasLightChangesToProcess</c> so a chunk with only
        /// an edge check can schedule), then attempt the schedule. On <b>success</b> the schedule clears every
        /// lighting flag, so the caller <c>Remove(pos)</c>s the chunk — it re-enters the ready set only via its
        /// completion's flag callback (if it re-flags unstable) or a <c>PromoteNeighborhood</c> event. A declined
        /// schedule (unreachable from a scan — the gates are pre-checked here) parks the chunk instead.</item>
        /// </list>
        /// The load-bearing half is that a successfully-scheduled chunk has no flags left and is <b>removed</b>,
        /// never left in the ready set.
        /// <para>
        /// A caller that keeps <i>no</i> sets — a sweep that re-visits every chunk until nothing is actionable —
        /// treats <c>Park</c> and <c>Remove</c> as no-ops. Such a caller may also split <c>ScheduleInitial</c>,
        /// running the recalc for its whole set before scheduling any of it; that ordering is required, not
        /// optional, because the recalc sets <c>HasLightChangesToProcess</c>, which is a term in the edge arm's
        /// neighbor gate — interleaving the two lets iteration order decide which chunks reach the edge arm.
        /// </para>
        /// </remarks>
        /// <param name="jobInFlight">A lighting job is already running for this chunk (production: <c>LightingJobs.ContainsKey</c>).</param>
        /// <param name="needsInitialLighting"><c>ChunkData.NeedsInitialLighting</c>.</param>
        /// <param name="needsEdgeCheck"><c>ChunkData.NeedsEdgeCheck</c>.</param>
        /// <param name="hasLightChanges"><c>ChunkData.HasLightChangesToProcess</c>.</param>
        /// <param name="neighborsDataReady">All neighbors have populated terrain data (<c>AreNeighborsDataReady</c>) — gates the initial and regular arms.</param>
        /// <param name="neighborsReadyAndLit">All neighbors are fully lit and stable (<c>AreNeighborsReadyAndLit</c>) — gates the edge arm.</param>
        /// <returns>The scan action the caller should perform.</returns>
        /// <remarks>
        /// The pre-evaluated form, for callers that need the gate values themselves (a baseline asserting on
        /// them) or that hold no world to gate against. A caller with live gates should prefer the
        /// <see cref="EvaluateReadyChunk(bool, bool, bool, bool, INeighborGates, ChunkCoord)"/> overload,
        /// which skips the gate this chunk's arm cannot read. Allocation-free: the constant provider is a
        /// struct reaching <see cref="Evaluate{TGates}"/> through its generic constraint, so it is never boxed.
        /// </remarks>
        public static ScanAction EvaluateReadyChunk(
            bool jobInFlight,
            bool needsInitialLighting,
            bool needsEdgeCheck,
            bool hasLightChanges,
            bool neighborsDataReady,
            bool neighborsReadyAndLit) =>
            Evaluate(jobInFlight, needsInitialLighting, needsEdgeCheck, hasLightChanges,
                new ConstantGates(neighborsDataReady, neighborsReadyAndLit), default);

        /// <summary>
        /// Decides the scan action for a chunk in the ready set, evaluating <b>only the neighbor gate the
        /// chunk's arm actually reads</b>. Identical in outcome to the pre-evaluated overload — the arm rule
        /// below is the single implementation both reach.
        /// </summary>
        /// <remarks>
        /// The arm precedence makes at most one gate reachable per chunk: an in-flight or flag-less chunk
        /// reads neither, an initial-lighting chunk reads only <see cref="INeighborGates.DataReady"/>, and
        /// <see cref="INeighborGates.ReadyAndLit"/> is reachable only for a chunk that is not awaiting initial
        /// lighting and has an edge check pending. Each gate is therefore invoked at most once, so an
        /// implementation needs no memoization. The caller contract is unchanged — see the overload above.
        /// </remarks>
        /// <param name="jobInFlight">A lighting job is already running for this chunk (production: <c>LightingJobs.ContainsKey</c>).</param>
        /// <param name="needsInitialLighting"><c>ChunkData.NeedsInitialLighting</c>.</param>
        /// <param name="needsEdgeCheck"><c>ChunkData.NeedsEdgeCheck</c>.</param>
        /// <param name="hasLightChanges"><c>ChunkData.HasLightChangesToProcess</c>.</param>
        /// <param name="gates">The live neighbor gates, queried on demand.</param>
        /// <param name="coord">The chunk being decided, passed through to <paramref name="gates"/>.</param>
        /// <returns>The scan action the caller should perform.</returns>
        public static ScanAction EvaluateReadyChunk(
            bool jobInFlight,
            bool needsInitialLighting,
            bool needsEdgeCheck,
            bool hasLightChanges,
            INeighborGates gates,
            ChunkCoord coord) =>
            Evaluate(jobInFlight, needsInitialLighting, needsEdgeCheck, hasLightChanges, gates, coord);

        /// <summary>
        /// The sole arm-selection implementation. Generic over the gate provider so a value-type provider is
        /// specialized rather than boxed, which is what keeps the pre-evaluated overload allocation-free.
        /// </summary>
        /// <typeparam name="TGates">The gate provider type.</typeparam>
        /// <param name="jobInFlight">A lighting job is already running for this chunk.</param>
        /// <param name="needsInitialLighting"><c>ChunkData.NeedsInitialLighting</c>.</param>
        /// <param name="needsEdgeCheck"><c>ChunkData.NeedsEdgeCheck</c>.</param>
        /// <param name="hasLightChanges"><c>ChunkData.HasLightChangesToProcess</c>.</param>
        /// <param name="gates">The gate provider.</param>
        /// <param name="coord">The chunk being decided.</param>
        /// <returns>The scan action the caller should perform.</returns>
        private static ScanAction Evaluate<TGates>(
            bool jobInFlight,
            bool needsInitialLighting,
            bool needsEdgeCheck,
            bool hasLightChanges,
            TGates gates,
            ChunkCoord coord)
            where TGates : INeighborGates
        {
            // A job is already running — its completion promotes the chunk (production parks it: MarkWaiting).
            // Neither gate is reachable from here, so neither is evaluated.
            if (jobInFlight) return ScanAction.Park;

            // Initial lighting takes priority; it gates on terrain-data readiness only.
            if (needsInitialLighting)
                return gates.DataReady(coord) ? ScanAction.ScheduleInitial : ScanAction.Park;

            // Edge consistency check: needs fully-lit neighbors so the border comparison reads settled data.
            // The strict gate is read here and nowhere else, and only once initial lighting is ruled out.
            if (needsEdgeCheck && gates.ReadyAndLit(coord))
                return ScanAction.ScheduleEdge;

            // Regular lighting update — also the fall-through when an edge check is pending but neighbors are
            // not lit yet (production's `!scheduled && HasLightChangesToProcess && AreNeighborsDataReady`).
            if (hasLightChanges && gates.DataReady(coord))
                return ScanAction.ScheduleRegular;

            // Nothing schedulable. No flags at all → forget the chunk; otherwise a gate failed → park.
            // needsInitialLighting is provably false here (its arm returned above); it is restated so this
            // reads as the documented three-flag rule rather than a condition narrowed by control flow.
            if (!needsInitialLighting && !needsEdgeCheck && !hasLightChanges)
                return ScanAction.Remove;

            return ScanAction.Park;
        }

        /// <summary>
        /// Gate provider returning values the caller already computed — the adapter behind the pre-evaluated
        /// overload. A struct, so <see cref="Evaluate{TGates}"/> specializes over it and no allocation occurs.
        /// </summary>
        private readonly struct ConstantGates : INeighborGates
        {
            private readonly bool _dataReady;
            private readonly bool _readyAndLit;

            /// <summary>Captures the pre-evaluated gate values.</summary>
            /// <param name="dataReady">The <c>AreNeighborsDataReady</c> result.</param>
            /// <param name="readyAndLit">The <c>AreNeighborsReadyAndLit</c> result.</param>
            public ConstantGates(bool dataReady, bool readyAndLit)
            {
                _dataReady = dataReady;
                _readyAndLit = readyAndLit;
            }

            /// <summary><see cref="INeighborGates.DataReady"/>: the captured value; the coordinate is unused.</summary>
            /// <param name="coord">Ignored — the value was computed by the caller.</param>
            /// <returns>The captured data-ready result.</returns>
            bool INeighborGates.DataReady(ChunkCoord coord) => _dataReady;

            /// <summary><see cref="INeighborGates.ReadyAndLit"/>: the captured value; the coordinate is unused.</summary>
            /// <param name="coord">Ignored — the value was computed by the caller.</param>
            /// <returns>The captured ready-and-lit result.</returns>
            bool INeighborGates.ReadyAndLit(ChunkCoord coord) => _readyAndLit;
        }
    }
}
