using System;

namespace Data
{
    [Serializable]
    public class ChunkSection
    {
        public const int SIZE = 16;

        // 16 * 16 * 16 = 4096 voxels
        public uint[] voxels;

        // Optimization: Track non-air blocks. If 0, we can skip logic/saving.
        public int nonAirCount;

        public ChunkSection()
        {
            voxels = new uint[SIZE * SIZE * SIZE];
            nonAirCount = 0;
        }

        public bool IsEmpty => nonAirCount == 0;

        public void RecalculateNonAirCount()
        {
            nonAirCount = 0;
            foreach (uint voxelBlockId in voxels)
            {
                if (voxelBlockId != 0) nonAirCount++;
            }
        }
    }
}
