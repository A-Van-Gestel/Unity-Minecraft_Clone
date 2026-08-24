using System;

namespace Data
{
    /// <summary>
    /// The kinds of lighting work pending for one chunk. This is a <b>set</b>, not a position in a chain —
    /// all eight combinations are reachable and meaningful, and they drain in the priority order
    /// initial → edge → changes. The only legal mutation sites are the transition methods on
    /// <see cref="ChunkData"/>; the bits are never written directly.
    /// </summary>
    [Flags]
    public enum LightingWork : byte
    {
        /// <summary>No lighting work pending.</summary>
        None = 0,

        /// <summary>
        /// The chunk has terrain data but has not yet undergone its initial, mandatory lighting pass.
        /// The only bit that crosses the save boundary (see <c>ChunkSerializer</c>).
        /// </summary>
        InitialLighting = 1 << 0,

        /// <summary>
        /// The chunk has pending light changes — queued BFS nodes, a queued column recalculation, or a
        /// re-flag asking for another pass.
        /// </summary>
        LightChanges = 1 << 1,

        /// <summary>
        /// The chunk needs an edge consistency check against its neighbors. Requires all neighbors to be
        /// lit before the strict arm will schedule it.
        /// </summary>
        EdgeCheck = 1 << 2,
    }
}
