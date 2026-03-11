using System;
using Helpers;
using JetBrains.Annotations;
using Jobs.BurstData;

namespace Data
{
    [Serializable]
    public class ChunkSection
    {
        /// <summary>ID bit mask — bits 0-15 (must match <see cref="BurstVoxelDataBitMapping"/>).</summary>
        private const uint ID_MASK = 0x0000FFFF;

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
        /// Uses a mask-based check (<c>data &amp; ID_MASK</c>) to correctly ignore air voxels
        /// that only carry light data (sunlight/blocklight bits set, block ID = 0).
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
                    if ((*ptr & ID_MASK) != 0) count++;
                    if ((*(ptr + 1) & ID_MASK) != 0) count++;
                    if ((*(ptr + 2) & ID_MASK) != 0) count++;
                    if ((*(ptr + 3) & ID_MASK) != 0) count++;
                    ptr += 4;
                }

                // Handle remaining items
                while (ptr < end)
                {
                    if ((*ptr & ID_MASK) != 0) count++;
                    ptr++;
                }

                nonAirCount = count;
            }
        }

        /// <summary>
        /// Recalculates NonAir and Opaque counts.
        /// Uses a mask-based check (<c>data &amp; ID_MASK</c>) to correctly ignore air voxels
        /// that only carry light data (sunlight/blocklight bits set, block ID = 0).
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

                    // OPTIMIZATION: Mask-based check to correctly skip air voxels.
                    // Light-only air voxels (data != 0 but ID == 0) are correctly skipped.
                    if ((data & ID_MASK) == 0) continue;

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
