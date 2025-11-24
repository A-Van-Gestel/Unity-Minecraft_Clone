using System.Runtime.CompilerServices;

namespace Helpers
{
    public static class ChunkMath
    {
        // Constants from VoxelData, duplicated here to be self-contained for Burst
        public const int CHUNK_WIDTH = 16;
        public const int SECTION_SIZE = 16;
        public const int SECTION_VOLUME = SECTION_SIZE * SECTION_SIZE * SECTION_SIZE; // 4096 for 16 section width & 16 section size

        /// <summary>
        /// Converts a 3D local chunk coordinate (x, y, z) into a flat index 
        /// compatible with the Section-based storage format.
        /// </summary>
        /// <param name="x">Local X (0-15)</param>
        /// <param name="y">Local Y (0-ChunkHeight)</param>
        /// <param name="z">Local Z (0-15)</param>
        /// <returns>The flattened index for the NativeArray.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetFlattenedIndex(int x, int y, int z)
        {
            // 1. Determine which vertical section we are in.
            int sectionIdx = y / SECTION_SIZE;

            // 2. Determine the Y coordinate relative to that section (0-15).
            int localY = y % SECTION_SIZE;

            // 3. Calculate the start index of this section in the massive array.
            int sectionOffset = sectionIdx * SECTION_VOLUME;

            // 4. Calculate the index within the section.
            // Layout: X increases fastest (1), then Y (16), then Z (256).
            // Formula: x + (y * 16) + (z * 16 * 16)
            int indexInSection = x + (localY * CHUNK_WIDTH) + (z * CHUNK_WIDTH * SECTION_SIZE);

            return sectionOffset + indexInSection;
        }
    }
}
