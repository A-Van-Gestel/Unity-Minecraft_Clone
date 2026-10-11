using System.Runtime.CompilerServices;
using Unity.Collections;

namespace Helpers
{
    /// <summary>
    /// The nine chunks a behavior-tick job may read, in the order its section table lists them: the center, then its
    /// four cardinal and four diagonal horizontal neighbors (N = +Z, S = −Z, E = +X, W = −X).
    /// </summary>
    public static class GatherSlot
    {
        /// <summary>The chunk being ticked.</summary>
        public const int Center = 0;

        /// <summary>The −X neighbor.</summary>
        public const int West = 1;

        /// <summary>The +X neighbor.</summary>
        public const int East = 2;

        /// <summary>The −Z neighbor.</summary>
        public const int South = 3;

        /// <summary>The +Z neighbor.</summary>
        public const int North = 4;

        /// <summary>The −X −Z neighbor.</summary>
        public const int SouthWest = 5;

        /// <summary>The −X +Z neighbor.</summary>
        public const int NorthWest = 6;

        /// <summary>The +X −Z neighbor.</summary>
        public const int SouthEast = 7;

        /// <summary>The +X +Z neighbor.</summary>
        public const int NorthEast = 8;

        /// <summary>Number of slots.</summary>
        public const int Count = 9;
    }

    /// <summary>
    /// One behavior-tick job's view into the tick's packed section arena (<c>TickSnapshotArena</c>): a table of
    /// <see cref="GatherSlot.Count"/> × <see cref="ChunkMath.SECTIONS_PER_CHUNK"/> entries giving each (slot, section)
    /// the arena index of its first voxel, or <see cref="MissingChunk"/> when that chunk is absent or unpopulated, or
    /// <see cref="NotRequested"/> when the section lies outside the band the job asked for. Burst-safe.
    /// </summary>
    public static class SectionArenaTable
    {
        /// <summary>Entry for a chunk that is absent or unpopulated; its voxels read as void.</summary>
        public const int MissingChunk = -1;

        /// <summary>
        /// Entry for a section outside the job's band. Reading one is a band bug: the gather throws when collections
        /// checks are on and reads void otherwise.
        /// </summary>
        public const int NotRequested = -2;

        /// <summary>Entries per job: one per section of each slot.</summary>
        public const int Length = GatherSlot.Count * ChunkMath.SECTIONS_PER_CHUNK;

        /// <summary>
        /// Arena index of the voxel at a section-contiguous flat chunk index of one slot, or that section's negative
        /// table entry.
        /// </summary>
        /// <param name="table">The job's section table.</param>
        /// <param name="slot">The chunk, a <see cref="GatherSlot"/> value.</param>
        /// <param name="flatIndex">The voxel's flat chunk index (<see cref="ChunkMath.GetFlattenedIndexInChunk"/>).</param>
        /// <returns>The arena index, or <see cref="MissingChunk"/> / <see cref="NotRequested"/>.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ArenaIndex(NativeArray<int> table, int slot, int flatIndex)
        {
            int sectionStart = table[slot * ChunkMath.SECTIONS_PER_CHUNK + flatIndex / ChunkMath.SECTION_VOLUME];
            return sectionStart < 0 ? sectionStart : sectionStart + flatIndex % ChunkMath.SECTION_VOLUME;
        }
    }
}
