using System;
using Helpers;
using JetBrains.Annotations;
using Jobs.BurstData;

namespace Data
{
    [Serializable]
    public class ChunkSection
    {
        public uint[] voxels;

        // Optimization: Track non-air blocks.
        public int nonAirCount;

        // Optimization: Track fully light-blocking blocks.
        public int opaqueCount;

        public ChunkSection()
        {
            // 4096 * 4 bytes = 16KB allocation
            voxels = new uint[ChunkMath.SECTION_VOLUME];
            nonAirCount = 0;
            opaqueCount = 0;
        }

        /// <summary>
        /// Resets the section for reuse in the pool.
        /// Zeros out the voxel array and resets counts.
        /// </summary>
        public void Reset()
        {
            nonAirCount = 0;
            opaqueCount = 0;
            Array.Clear(voxels, 0, voxels.Length);
        }

        public bool IsEmpty => nonAirCount == 0;
        public bool IsFullySolid => opaqueCount >= ChunkMath.SECTION_VOLUME;

        /// <summary>
        /// Recalculates the NonAirCount using optimized pointer arithmetic and loop unrolling.
        /// </summary>
        public unsafe void RecalculateNonAirCount()
        {
            nonAirCount = 0;

            // Use fixed pointer to avoid array bounds checks
            fixed (uint* pVoxels = voxels)
            {
                uint* ptr = pVoxels;
                uint* end = pVoxels + ChunkMath.SECTION_VOLUME;
                int count = 0;

                // Unroll loop 4x for instruction pipelining efficiency
                // This reduces the overhead of the loop comparison/increment logic
                while (ptr <= end - 4)
                {
                    if (*ptr != 0) count++;
                    if (*(ptr + 1) != 0) count++;
                    if (*(ptr + 2) != 0) count++;
                    if (*(ptr + 3) != 0) count++;
                    ptr += 4;
                }

                // Handle remaining items
                while (ptr < end)
                {
                    if (*ptr != 0) count++;
                    ptr++;
                }

                nonAirCount = count;
            }
        }

        /// <summary>
        /// Recalculates NonAir and Opaque counts.
        /// </summary>
        /// <param name="blockTypes">The blockTypes array to look up opacity.</param>
        public unsafe void RecalculateCounts([CanBeNull] BlockType[] blockTypes)
        {
            // Reset counts
            nonAirCount = 0;
            opaqueCount = 0;

            // Fallback: If no blockTypes proved, we can only calculate NonAir.
            if (blockTypes == null)
            {
                RecalculateNonAirCount();
                return;
            }

            int localNonAir = 0;
            int localOpaque = 0;

            fixed (uint* pVoxels = voxels)
            {
                uint* ptr = pVoxels;
                uint* end = ptr + ChunkMath.SECTION_VOLUME;

                while (ptr < end)
                {
                    uint data = *ptr++;

                    // OPTIMIZATION: Early exit for Air.
                    // This avoids the bitwise extraction and the array lookup entirely.
                    if (data == 0) continue;

                    localNonAir++;

                    ushort id = BurstVoxelDataBitMapping.GetId(data);

                    // Safety check for ID bounds to prevent crashes in unsafe context
                    if (id < blockTypes.Length)
                    {
                        // Note: blockTypes[id] is a reference type lookup (pointer chase).
                        // This is the most expensive part, but cannot be easily avoided 
                        // without changing the data architecture to use flat structs/arrays.
                        if (blockTypes[id].IsOpaque)
                        {
                            localOpaque++;
                        }
                    }
                }
            }

            nonAirCount = localNonAir;
            opaqueCount = localOpaque;
        }
    }
}
