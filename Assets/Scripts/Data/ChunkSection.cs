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

        /// <summary>
        /// Parallel light array storing per-voxel RGB light data (Phase 2).
        /// Layout: <c>[Sky:4][BlockR:4][BlockG:4][BlockB:4]</c> = 16 bits.
        /// Persisted to disk via flag-based section format (v9+). Bulk-read on load
        /// when present; reconstructed from legacy <c>uint</c> light bits for older saves.
        /// </summary>
        public ushort[] LightData;

        // Optimization: Track non-air blocks.
        public int nonAirCount;

        // Optimization: Track fully light-blocking blocks.
        public int opaqueCount;

        /// <summary>
        /// Number of light-emitting voxels in this section (per <see cref="Helpers.EmissiveBlockLookup"/>).
        /// Backs the LI-2 bottom-band inert-dark derivation (<c>ChunkData.GetLightingBandBottom</c>):
        /// a dark section containing an unstamped emitter must end the skippable region, because the
        /// lighting job's emission-sync scan would stamp and propagate it. Runtime-only — never
        /// serialized; recomputed by <see cref="RecalculateCounts"/>/<see cref="RecalculateNonAirCount"/>
        /// on load and maintained incrementally by <c>ChunkData.SetVoxel</c>.
        /// </summary>
        public int emissiveCount;

        /// <summary>
        /// Number of <i>flowing</i> fluid voxels in this section (per <see cref="Helpers.FluidBlockLookup"/>):
        /// fluid blocks whose level nibble is non-zero. Backs the S3 fluid-emitter scan, which copies a
        /// section into native memory only when this is positive — so a still ocean, whose voxels are all
        /// sources, costs the scan nothing. Runtime-only — never serialized; recomputed by
        /// <see cref="RecalculateCounts"/>/<see cref="RecalculateNonAirCount"/> on load and maintained
        /// incrementally by <c>ChunkData.SetVoxel</c>.
        /// </summary>
        public int emitterFluidCount;

        /// <summary>
        /// The <see cref="Helpers.FluidBlockLookup.Generation"/> <see cref="emitterFluidCount"/> was computed
        /// under. 0 means never computed. A count from an older palette describes a different world, so the
        /// emitter scan recomputes rather than trusting it. Runtime-only — never serialized.
        /// </summary>
        public int emitterFluidGeneration;

        /// <summary>
        /// Initializes a new, empty <see cref="ChunkSection"/> with arrays allocated.
        /// </summary>
        public ChunkSection()
        {
            // 4096 * 4 bytes = 16KB allocation
            voxels = new uint[ChunkMath.SECTION_VOLUME];
            // 4096 * 2 bytes = 8KB allocation
            LightData = new ushort[ChunkMath.SECTION_VOLUME];
            nonAirCount = 0;
            opaqueCount = 0;
            emissiveCount = 0;
            emitterFluidCount = 0;
            emitterFluidGeneration = 0;
        }

        /// <summary>
        /// Resets the section for reuse in the pool.
        /// Zeros out the voxel array and resets counts.
        /// </summary>
        public void Reset()
        {
            nonAirCount = 0;
            opaqueCount = 0;
            emissiveCount = 0;
            emitterFluidCount = 0;
            emitterFluidGeneration = 0;
            Array.Clear(voxels, 0, voxels.Length);
            Array.Clear(LightData, 0, LightData.Length);
        }

        /// <summary>
        /// Returns true if the section contains no blocks other than air.
        /// </summary>
        public bool IsEmpty => nonAirCount == 0;

        /// <summary>
        /// Returns true if the section is completely filled with light-obstructing blocks,
        /// allowing meshing to optimize away internal faces.
        /// </summary>
        public bool IsFullySolid => opaqueCount >= ChunkMath.SECTION_VOLUME;

        /// <summary>
        /// Recalculates the NonAirCount using optimized pointer arithmetic and loop unrolling.
        /// Uses a mask-based check (<c>data &amp; ID_MASK</c>) to correctly ignore air voxels
        /// that only carry light data (skylight/blocklight bits set, block ID = 0).
        /// Also recalculates <see cref="emissiveCount"/> and <see cref="emitterFluidCount"/> — both tests go
        /// through palette-independent lookups (<see cref="EmissiveBlockLookup"/>,
        /// <see cref="FluidBlockLookup"/>), so this path (the <c>RecalculateCounts(null)</c> fallback) keeps
        /// them correct where <see cref="opaqueCount"/> cannot be.
        /// </summary>
        public unsafe void RecalculateNonAirCount()
        {
            nonAirCount = 0;
            emissiveCount = 0;
            emitterFluidCount = 0;

            // Use fixed pointer to avoid array bounds checks
            fixed (uint* pVoxels = voxels)
            {
                uint* ptr = pVoxels;
                uint* end = pVoxels + ChunkMath.SECTION_VOLUME;
                int count = 0;
                int emissive = 0;
                int flowing = 0;

                while (ptr < end)
                {
                    uint data = *ptr++;
                    if ((data & BurstVoxelDataBitMapping.ID_MASK) == 0) continue;

                    count++;
                    if (EmissiveBlockLookup.IsEmissive(BurstVoxelDataBitMapping.GetId(data))) emissive++;
                    if (FluidBlockLookup.IsEmitterFluid(data)) flowing++;
                }

                nonAirCount = count;
                emissiveCount = emissive;
                emitterFluidCount = flowing;
                emitterFluidGeneration = FluidBlockLookup.Generation;
            }
        }

        /// <summary>
        /// Recomputes <see cref="emitterFluidCount"/> alone and stamps it with the current palette
        /// generation. The emitter scan's lazy repair path, for a section whose count was computed under a
        /// palette that has since been rebound.
        /// </summary>
        /// <remarks>
        /// Narrower than <see cref="RecalculateCounts"/> on purpose: the other counts answer to different
        /// lookups with their own lifetimes, and a palette rebind is no reason to touch them.
        /// </remarks>
        public unsafe void RecalculateEmitterFluidCount()
        {
            int flowing = 0;

            fixed (uint* pVoxels = voxels)
            {
                uint* ptr = pVoxels;
                uint* end = pVoxels + ChunkMath.SECTION_VOLUME;

                while (ptr < end)
                {
                    uint data = *ptr++;
                    if ((data & BurstVoxelDataBitMapping.ID_MASK) == 0) continue;
                    if (FluidBlockLookup.IsEmitterFluid(data)) flowing++;
                }
            }

            emitterFluidCount = flowing;
            emitterFluidGeneration = FluidBlockLookup.Generation;
        }

        /// <summary>
        /// Recalculates NonAir, Opaque, Emissive and flowing-fluid counts.
        /// Uses a mask-based check (<c>data &amp; ID_MASK</c>) to correctly ignore air voxels
        /// that only carry light data (skylight/blocklight bits set, block ID = 0).
        /// The emissive and flowing-fluid tests go through their static lookups (not
        /// <paramref name="blockTypes"/>) so they agree with the incremental
        /// <c>ChunkData.SetVoxel</c> maintenance on every path.
        /// </summary>
        /// <param name="blockTypes">The blockTypes array to look up opacity.</param>
        public unsafe void RecalculateCounts([CanBeNull] BlockType[] blockTypes)
        {
            // Reset counts
            nonAirCount = 0;
            opaqueCount = 0;
            emissiveCount = 0;
            emitterFluidCount = 0;

            // Fallback: If no blockTypes proved, we can only calculate NonAir (and Emissive,
            // whose lookup is palette-instance-independent).
            if (blockTypes == null)
            {
                RecalculateNonAirCount();
                return;
            }

            int localNonAir = 0;
            int localOpaque = 0;
            int localEmissive = 0;
            int localFlowing = 0;

            fixed (uint* pVoxels = voxels)
            {
                uint* ptr = pVoxels;
                uint* end = ptr + ChunkMath.SECTION_VOLUME;

                while (ptr < end)
                {
                    uint data = *ptr++;

                    // OPTIMIZATION: Mask-based check to correctly skip air voxels.
                    // Light-only air voxels (data != 0 but ID == 0) are correctly skipped.
                    if ((data & BurstVoxelDataBitMapping.ID_MASK) == 0) continue;

                    localNonAir++;

                    ushort id = BurstVoxelDataBitMapping.GetId(data);

                    if (EmissiveBlockLookup.IsEmissive(id)) localEmissive++;
                    if (FluidBlockLookup.IsEmitterFluid(data)) localFlowing++;

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
            emissiveCount = localEmissive;
            emitterFluidCount = localFlowing;
            emitterFluidGeneration = FluidBlockLookup.Generation;
        }
    }
}
