using System.Runtime.CompilerServices;
using Unity.Mathematics;

namespace Helpers
{
    public static class ChunkMath
    {
        // Constants from VoxelData, duplicated here to be self-contained for Burst
        public const int CHUNK_WIDTH = 16;
        public const int CHUNK_HEIGHT = 128;
        public const int SECTION_SIZE = 16;
        public const int SECTION_VOLUME = SECTION_SIZE * SECTION_SIZE * SECTION_SIZE; // 4096 for 16 section width & 16 section size
        public const int CHUNK_VOLUME = SECTION_VOLUME * (CHUNK_HEIGHT / SECTION_SIZE); // 32768 (8 sections × 4096)

        /// <summary>
        /// Calculates the flat array index within a single section based on local section coordinates.
        /// </summary>
        /// <param name="x">Local X (0-15)</param>
        /// <param name="localY">Local Y within the section (0-15)</param>
        /// <param name="z">Local Z (0-15)</param>
        /// <returns>The flattened index relative to the start of the section array.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetFlattenedIndexInSection(int x, int localY, int z)
        {
            // Layout: X increases fastest (1), then Y (16), then Z (256).
            // Formula: x + (y * width) + (z * width * height)
            return x + localY * CHUNK_WIDTH + z * CHUNK_WIDTH * SECTION_SIZE;
        }

        /// <summary>
        /// Converts a 3D local chunk coordinate (x, y, z) into a flat index 
        /// compatible with the Section-based storage format.
        /// </summary>
        /// <param name="x">Local X (0-15)</param>
        /// <param name="y">Local Y (0-ChunkHeight)</param>
        /// <param name="z">Local Z (0-15)</param>
        /// <returns>The flattened index for the NativeArray.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetFlattenedIndexInChunk(int x, int y, int z)
        {
            // Defensive clamp: prevents IndexOutOfRangeException if y reaches 128 (ChunkHeight).
            y = math.clamp(y, 0, CHUNK_HEIGHT - 1);

            // 1. Determine which vertical section we are in.
            int sectionIdx = y / SECTION_SIZE;

            // 2. Determine the Y coordinate relative to that section (0-15).
            int localY = y % SECTION_SIZE;

            // 3. Calculate the start index of this section in the massive array.
            int sectionOffset = sectionIdx * SECTION_VOLUME;

            // 4. Calculate the index within the section and add the offset.
            return sectionOffset + GetFlattenedIndexInSection(x, localY, z);
        }

        /// <summary>
        /// Inverse of <see cref="GetFlattenedIndexInChunk"/>: decodes a flat chunk index back into
        /// its local 3D coordinate. Used to unpack job-emitted active-voxel indices on the main thread.
        /// </summary>
        /// <param name="index">The flattened index (0..ChunkVolume-1) produced by <see cref="GetFlattenedIndexInChunk"/>.</param>
        /// <param name="x">Decoded local X (0-15).</param>
        /// <param name="y">Decoded local Y (0-ChunkHeight).</param>
        /// <param name="z">Decoded local Z (0-15).</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void GetLocalPositionFromFlattenedIndex(int index, out int x, out int y, out int z)
        {
            // Defensive clamp mirroring GetFlattenedIndexInChunk's Y-clamp: a malformed (out-of-range) index
            // decodes to an in-chunk coordinate rather than an out-of-bounds local position that
            // Chunk.AddActiveVoxel would register and TickUpdate later evaluate against a non-existent voxel.
            index = math.clamp(index, 0, CHUNK_VOLUME - 1);

            // Mirror of the section-aware packing in GetFlattenedIndexInChunk / GetFlattenedIndexInSection.
            int sectionIdx = index / SECTION_VOLUME;
            int withinSection = index % SECTION_VOLUME;

            x = withinSection % CHUNK_WIDTH;
            int localY = withinSection / CHUNK_WIDTH % SECTION_SIZE;
            z = withinSection / (CHUNK_WIDTH * SECTION_SIZE);
            y = sectionIdx * SECTION_SIZE + localY;
        }
    }
}
