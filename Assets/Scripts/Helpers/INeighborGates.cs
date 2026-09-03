using Data;

namespace Helpers
{
    /// <summary>
    /// The two neighbor-readiness gates the lighting ready-set scan decides against, supplied on demand so
    /// <see cref="LightingScanDecision.EvaluateReadyChunk(bool, bool, bool, bool, INeighborGates, ChunkCoord)"/>
    /// evaluates only the gate the chunk's arm actually reads.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each gate is an 8-neighbor walk over the chunk map and the job dictionaries, so evaluating one the arm
    /// precedence cannot read is pure waste — measured at ~36 % of the scan's gate calls (LP-6).
    /// </para>
    /// <para>
    /// <b>Why the coordinate is a parameter.</b> An implementation must stay stateless with respect to which
    /// chunk is being gated. The alternative — a cached adapter carrying a mutable coordinate set immediately
    /// before each call — makes "forgot to update the coordinate" a silent fail-<i>open</i> bug: the gates
    /// answer truthfully about the previous chunk, and a stale <c>true</c> from <see cref="ReadyAndLit"/>
    /// schedules a border edge check against light that is still moving. Passing the coordinate removes that
    /// failure mode structurally.
    /// </para>
    /// <para>
    /// Implementations are expected to be reference types passed as <c>this</c>, so the conversion allocates
    /// nothing; a value-type implementation must reach the decision through its generic core to avoid boxing.
    /// </para>
    /// </remarks>
    public interface INeighborGates
    {
        /// <summary>
        /// Whether every neighbor has populated terrain data (production: <c>World.AreNeighborsDataReady</c>).
        /// Gates the initial and regular arms.
        /// </summary>
        /// <param name="coord">The chunk whose neighbors are gated.</param>
        /// <returns>True when no neighbor blocks the data-ready gate.</returns>
        bool DataReady(ChunkCoord coord);

        /// <summary>
        /// Whether every neighbor is fully lit and settled (production: <c>World.AreNeighborsReadyAndLit</c>).
        /// Gates the border edge-check arm only.
        /// </summary>
        /// <param name="coord">The chunk whose neighbors are gated.</param>
        /// <returns>True when no neighbor blocks the strict gate.</returns>
        bool ReadyAndLit(ChunkCoord coord);
    }
}
