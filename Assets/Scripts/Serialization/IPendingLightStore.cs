using System.Collections.Generic;
using Data;
using UnityEngine;

namespace Serialization
{
    /// <summary>
    /// The in-memory surface of the pending-lighting store: cross-chunk light work that targets an
    /// unloaded/unpopulated chunk and must be held until that chunk loads (sunlight column recalcs and
    /// cross-chunk blocklight modifications). Implemented by <see cref="LightingStateManager"/>.
    /// <para>
    /// Binary persistence (<c>Save</c>/<c>Load</c>) is intentionally NOT part of this contract: it is a
    /// serialization concern, and keeping it off the interface lets disk-free consumers (notably the
    /// lighting validation harness) exercise the real persist/replay logic with a compile-time guarantee
    /// that no disk I/O can be triggered through this surface.
    /// </para>
    /// </summary>
    public interface IPendingLightStore
    {
        /// <summary>
        /// Adds a set of local column coordinates that need sunlight recalculation to the pending store.
        /// </summary>
        /// <param name="chunkCoord">The chunk coordinate.</param>
        /// <param name="localColumns">A set of Vector2Ints where x/y are local 0-15 coordinates.</param>
        void AddPending(ChunkCoord chunkCoord, HashSet<Vector2Int> localColumns);

        /// <summary>
        /// Attempts to retrieve and remove the pending local columns for sunlight recalculation in a specific chunk.
        /// </summary>
        /// <param name="chunkCoord">The chunk coordinate to query.</param>
        /// <param name="localColumns">A set of local 0-15 coordinates needing recalculation, if any.</param>
        /// <returns>True if pending columns were found; otherwise, false.</returns>
        bool TryGetAndRemove(ChunkCoord chunkCoord, out HashSet<Vector2Int> localColumns);

        /// <summary>
        /// Records a cross-chunk blocklight modification targeting an unloaded/unpopulated chunk so it can
        /// be replayed when the chunk is loaded.
        /// </summary>
        /// <param name="chunkCoord">The target chunk coordinate.</param>
        /// <param name="localPos">The LOCAL voxel position inside the target chunk.</param>
        /// <param name="r">The red blocklight channel the modification wants to set (0-15).</param>
        /// <param name="g">The green blocklight channel the modification wants to set (0-15).</param>
        /// <param name="b">The blue blocklight channel the modification wants to set (0-15).</param>
        /// <param name="isRemoval">True when emitted by a darkness/removal pass (zero channels mean "remove").</param>
        void AddPendingBlocklight(ChunkCoord chunkCoord, Vector3Int localPos, byte r, byte g, byte b, bool isRemoval);

        /// <summary>
        /// Attempts to retrieve and remove the pending cross-chunk blocklight modifications for a chunk.
        /// The caller takes ownership of the returned dictionary and must release it via
        /// <c>DictionaryPool&lt;Vector3Int, LightingStateManager.PendingBlocklightMod&gt;</c> after replaying.
        /// </summary>
        /// <param name="chunkCoord">The chunk coordinate to query.</param>
        /// <param name="mods">The pending modifications keyed by LOCAL voxel position, if any.</param>
        /// <returns>True if pending modifications were found; otherwise, false.</returns>
        bool TryGetAndRemovePendingBlocklight(ChunkCoord chunkCoord, out Dictionary<Vector3Int, LightingStateManager.PendingBlocklightMod> mods);

        /// <summary>
        /// Discards any pending cross-chunk blocklight modifications for a chunk. Used when the chunk is
        /// freshly GENERATED (not loaded from disk): initial lighting recomputes all light from current
        /// neighbor data, so mods recorded while the chunk was absent are obsolete.
        /// </summary>
        /// <param name="chunkCoord">The chunk coordinate whose pending modifications are discarded.</param>
        void DiscardPendingBlocklight(ChunkCoord chunkCoord);

        /// <summary>
        /// Safely releases all remaining pooled HashSets and Dictionaries. Call this when destroying the
        /// world (or tearing down a harness) to prevent pool leaks.
        /// </summary>
        void Clear();
    }
}
