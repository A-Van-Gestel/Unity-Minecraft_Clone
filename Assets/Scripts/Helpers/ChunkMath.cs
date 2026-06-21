using System.Runtime.CompilerServices;
using Unity.Collections;
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

        // --- Halo-padded lighting volume (LI-1) ---------------------------------------------------
        // The NeighborhoodLightingJob reads/writes a single padded volume instead of 9 separate
        // neighbor maps. The widest read the job performs reaches 2 voxels into a neighbor on every
        // horizontal axis (the sunlight column-recalc darkness path enqueues neighbor nodes at the
        // ±1 rim, and PropagateDarkness then reads THEIR face neighbors at ±2 — symmetric on all four
        // sides, plus the four diagonal corners). So the volume needs a 2-voxel halo on X and Z; Y is
        // full height with no padding (out-of-range Y reads are sentinel-guarded in the job, not
        // padded). Unlike the section-aware chunk layout above, the padded volume uses a plain linear
        // layout (X fastest, then Z, then Y) — it is a transient per-job scratch buffer, so fill and
        // read only have to agree with each other, and a contiguous horizontal slab favors the BFS's
        // horizontal spread.
        public const int LIGHTING_HALO = 2;
        public const int PADDED_CHUNK_WIDTH = CHUNK_WIDTH + 2 * LIGHTING_HALO; // 20
        public const int PADDED_HORIZONTAL_AREA = PADDED_CHUNK_WIDTH * PADDED_CHUNK_WIDTH; // 400
        public const int PADDED_LIGHTING_VOLUME = PADDED_HORIZONTAL_AREA * CHUNK_HEIGHT; // 51,200

        /// <summary>
        /// Flat index into the halo-padded lighting volume (see <see cref="PADDED_LIGHTING_VOLUME"/>).
        /// Padded coordinates are grid-local coordinates shifted by <see cref="LIGHTING_HALO"/>: a
        /// grid-local position <c>(gx, gy, gz)</c> with <c>gx, gz ∈ [-2, 17]</c> maps to
        /// <c>(gx + 2, gy, gz + 2)</c> with <c>px, pz ∈ [0, 20)</c>. The center chunk's [0,16) range
        /// therefore lives at padded [2,18). Layout: X fastest (stride 1), then Z (stride 20), then Y
        /// (stride 400). Y is defensively clamped to a valid row exactly as
        /// <see cref="GetFlattenedIndexInChunk"/> does; callers that must distinguish out-of-range Y
        /// (a sentinel read) check the Y bound BEFORE indexing.
        /// </summary>
        /// <param name="px">Padded X (0-19).</param>
        /// <param name="py">Padded/global Y (0-ChunkHeight).</param>
        /// <param name="pz">Padded Z (0-19).</param>
        /// <returns>The flattened index into the padded lighting volume.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetPaddedLightingIndex(int px, int py, int pz)
        {
            py = math.clamp(py, 0, CHUNK_HEIGHT - 1);
            return px + pz * PADDED_CHUNK_WIDTH + py * PADDED_HORIZONTAL_AREA;
        }

        /// <summary>
        /// Copies a contiguous run of voxels from a section-contiguous full-chunk source into the padded
        /// volume, or fills the destination run with <c>uint.MaxValue</c> when the source is missing (an
        /// uncreated/empty array — the LI-1 sentinel that reproduces the old per-cell
        /// <c>!IsCreated || Length == 0</c> missing-neighbor guard). The run is contiguous in BOTH layouts
        /// because X is the fastest axis (stride 1) in each, so a fixed-(y,z) span of consecutive X is a
        /// flat block in the section-aware source AND in the linear padded volume — letting the gather move
        /// a whole row segment with one bulk
        /// <see cref="NativeArray{T}.Copy(NativeArray{T},int,NativeArray{T},int,int)"/> instead of a branchy
        /// per-cell scatter (LI-1 follow-up #3).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void CopyVoxelRun(NativeArray<uint> src, int srcStart, NativeArray<uint> padded, int dstStart, int length)
        {
            if (src.IsCreated && src.Length > 0)
            {
                NativeArray<uint>.Copy(src, srcStart, padded, dstStart, length);
                return;
            }

            for (int k = 0; k < length; k++) padded[dstStart + k] = uint.MaxValue;
        }

        /// <summary>
        /// Light counterpart of <see cref="CopyVoxelRun"/>: a missing source fills the run with
        /// <c>ushort.MaxValue</c>. Voxel and light runs for the same region are copied in lock-step, so the
        /// two sentinels co-occur and the consuming job's twin <c>uint.MaxValue</c>/<c>ushort.MaxValue</c>
        /// bounds checks stay in agreement.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void CopyLightRun(NativeArray<ushort> src, int srcStart, NativeArray<ushort> padded, int dstStart, int length)
        {
            if (src.IsCreated && src.Length > 0)
            {
                NativeArray<ushort>.Copy(src, srcStart, padded, dstStart, length);
                return;
            }

            for (int k = 0; k < length; k++) padded[dstStart + k] = ushort.MaxValue;
        }

        /// <summary>
        /// Scatters the center chunk + its 8 horizontal neighbors (each a section-contiguous full-chunk
        /// voxel buffer) into the halo-padded linear volume consumed by the <c>NeighborhoodLightingJob</c>.
        /// A missing neighbor (uncreated/empty array) fills its region with <c>uint.MaxValue</c>,
        /// reproducing the job's old per-neighbor missing-source sentinel. Writes EVERY padded cell.
        /// <para>
        /// Each padded horizontal row (fixed py, pz — 20 cells of X) is built as three contiguous runs: the
        /// 2-wide West halo, the 16-wide center span, and the 2-wide East halo, each copied in bulk from one
        /// source chunk via <see cref="CopyVoxelRun"/>. The pz band picks the source row (<c>lz</c>) and the
        /// West/center/East source chunks once per row — pz∈[0,2)→south side (SW/S/SE), pz∈[18,20)→north
        /// side (NW/N/NE), else the center row (W/Center/E) — the same 3×3 dispatch the old per-cell
        /// <c>PaddedSourceIndex</c> performed, hoisted out of the inner loop. Bit-identical to the per-cell
        /// scatter it replaces (X is the fastest axis in both layouts, so the run order is preserved).
        /// </para>
        /// </summary>
        public static void GatherPaddedVoxels(NativeArray<uint> padded,
            NativeArray<uint> center, NativeArray<uint> w, NativeArray<uint> e, NativeArray<uint> s, NativeArray<uint> n,
            NativeArray<uint> sw, NativeArray<uint> nw, NativeArray<uint> se, NativeArray<uint> ne)
        {
            for (int py = 0; py < CHUNK_HEIGHT; py++)
            {
                for (int pz = 0; pz < PADDED_CHUNK_WIDTH; pz++)
                {
                    // Resolve the pz band once per row: source-local Z + the West/center/East source chunks.
                    int lz;
                    NativeArray<uint> west, mid, east;
                    if (pz < LIGHTING_HALO) // South side
                    {
                        lz = pz + CHUNK_WIDTH - LIGHTING_HALO; west = sw; mid = s; east = se;
                    }
                    else if (pz >= CHUNK_WIDTH + LIGHTING_HALO) // North side
                    {
                        lz = pz - CHUNK_WIDTH - LIGHTING_HALO; west = nw; mid = n; east = ne;
                    }
                    else // Center row
                    {
                        lz = pz - LIGHTING_HALO; west = w; mid = center; east = e;
                    }

                    int rowBase = GetPaddedLightingIndex(0, py, pz);

                    // West halo: padded px[0,2) <- west-side chunk local x[14,16).
                    CopyVoxelRun(west, GetFlattenedIndexInChunk(CHUNK_WIDTH - LIGHTING_HALO, py, lz),
                        padded, rowBase, LIGHTING_HALO);
                    // Center span: padded px[2,18) <- center-side chunk local x[0,16).
                    CopyVoxelRun(mid, GetFlattenedIndexInChunk(0, py, lz),
                        padded, rowBase + LIGHTING_HALO, CHUNK_WIDTH);
                    // East halo: padded px[18,20) <- east-side chunk local x[0,2).
                    CopyVoxelRun(east, GetFlattenedIndexInChunk(0, py, lz),
                        padded, rowBase + CHUNK_WIDTH + LIGHTING_HALO, LIGHTING_HALO);
                }
            }
        }

        /// <summary>
        /// Light counterpart of <see cref="GatherPaddedVoxels"/>: scatters center + 8 neighbor light
        /// buffers into the padded volume as three bulk row runs (see <see cref="CopyLightRun"/>), missing
        /// sources filled with <c>ushort.MaxValue</c>. The pz-band → source-chunk dispatch and run layout
        /// mirror <see cref="GatherPaddedVoxels"/> exactly so a voxel/light pair always agrees.
        /// </summary>
        public static void GatherPaddedLight(NativeArray<ushort> padded,
            NativeArray<ushort> center, NativeArray<ushort> w, NativeArray<ushort> e, NativeArray<ushort> s, NativeArray<ushort> n,
            NativeArray<ushort> sw, NativeArray<ushort> nw, NativeArray<ushort> se, NativeArray<ushort> ne)
        {
            for (int py = 0; py < CHUNK_HEIGHT; py++)
            {
                for (int pz = 0; pz < PADDED_CHUNK_WIDTH; pz++)
                {
                    // Resolve the pz band once per row: source-local Z + the West/center/East source chunks.
                    int lz;
                    NativeArray<ushort> west, mid, east;
                    if (pz < LIGHTING_HALO) // South side
                    {
                        lz = pz + CHUNK_WIDTH - LIGHTING_HALO; west = sw; mid = s; east = se;
                    }
                    else if (pz >= CHUNK_WIDTH + LIGHTING_HALO) // North side
                    {
                        lz = pz - CHUNK_WIDTH - LIGHTING_HALO; west = nw; mid = n; east = ne;
                    }
                    else // Center row
                    {
                        lz = pz - LIGHTING_HALO; west = w; mid = center; east = e;
                    }

                    int rowBase = GetPaddedLightingIndex(0, py, pz);

                    // West halo: padded px[0,2) <- west-side chunk local x[14,16).
                    CopyLightRun(west, GetFlattenedIndexInChunk(CHUNK_WIDTH - LIGHTING_HALO, py, lz),
                        padded, rowBase, LIGHTING_HALO);
                    // Center span: padded px[2,18) <- center-side chunk local x[0,16).
                    CopyLightRun(mid, GetFlattenedIndexInChunk(0, py, lz),
                        padded, rowBase + LIGHTING_HALO, CHUNK_WIDTH);
                    // East halo: padded px[18,20) <- east-side chunk local x[0,2).
                    CopyLightRun(east, GetFlattenedIndexInChunk(0, py, lz),
                        padded, rowBase + CHUNK_WIDTH + LIGHTING_HALO, LIGHTING_HALO);
                }
            }
        }

        /// <summary>
        /// Copies the center chunk's region [2,18)×[0,128)×[2,18) out of the halo-padded light volume into
        /// a section-contiguous full-chunk light buffer (the <see cref="GetFlattenedIndexInChunk"/> layout
        /// that <see cref="ChunkData.ApplyJobLightMap"/> reads back). The job only writes light into the
        /// center region; voxels are never modified, so only light is extracted.
        /// </summary>
        public static void ExtractCenterLight(NativeArray<ushort> padded, NativeArray<ushort> centerOut)
        {
            for (int cy = 0; cy < CHUNK_HEIGHT; cy++)
            {
                for (int cz = 0; cz < CHUNK_WIDTH; cz++)
                {
                    // Each center X-row is contiguous in both layouts (X is the fastest axis), so copy the
                    // 16-wide span in bulk: source = padded center span px[2,18) at (cy, cz+2); dest = the
                    // section-contiguous center row at (cx=0, cy, cz). Bit-identical to the per-cell loop.
                    NativeArray<ushort>.Copy(
                        padded, GetPaddedLightingIndex(LIGHTING_HALO, cy, cz + LIGHTING_HALO),
                        centerOut, GetFlattenedIndexInChunk(0, cy, cz),
                        CHUNK_WIDTH);
                }
            }
        }

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
