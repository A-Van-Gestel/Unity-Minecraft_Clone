using System;
using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
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
        // The NeighborhoodLightingJob reads/writes a single padded volume instead of 9 separate neighbor
        // maps. Unlike the section-aware chunk layout above, the padded volume uses a plain linear layout
        // (X fastest, then Z, then Y) — it is a transient per-job scratch buffer, so fill and read only
        // have to agree with each other, and a contiguous horizontal slab favors the BFS's horizontal
        // spread.
        //
        // MAX_LIGHTING_BFS_REACH is the widest distance (in voxels) any lighting BFS read reaches PAST a
        // chunk seam, and is the load-bearing invariant of the whole optimization: LIGHTING_HALO MUST be
        // >= this reach, or a seam read silently falls off the padded volume, gets the MaxValue sentinel
        // (treated as out-of-world), and drops that voxel's light contribution -> non-bit-identical seams
        // with NO error raised. The reach is bounded at 2 by the BFS itself: the sunlight column-recalc
        // darkness path enqueues neighbor nodes only at the ±1 rim, PropagateDarkness then reads THEIR ±1
        // face neighbors (= ±2), and the IsInCenterChunk re-enqueue gate in NeighborhoodLightingJob blocks
        // anything deeper (symmetric on all four sides, plus the four diagonal corners). If a future change
        // ever lets the BFS read at ±3, bump THIS constant (LIGHTING_HALO and the volume size follow) and
        // re-verify the cross-seam baselines (Validate Lighting Engine: B5/B10/B40-B44/B48/B50-B55).
        public const int MAX_LIGHTING_BFS_REACH = 2;

        // Halo width on X/Z = the max BFS reach (single source of truth). Y is full height with no padding
        // (out-of-range Y reads are sentinel-guarded in the job, not padded).
        public const int LIGHTING_HALO = MAX_LIGHTING_BFS_REACH;
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
        /// Copies a contiguous run from a section-contiguous full-chunk source into the padded volume, or
        /// fills the destination run with <paramref name="sentinel"/> when the source is missing (an
        /// uncreated/empty array — the LI-1 sentinel that reproduces the old per-cell
        /// <c>!IsCreated || Length == 0</c> missing-neighbor guard). The run is contiguous in BOTH layouts
        /// because X is the fastest axis (stride 1) in each, so a fixed-(y,z) span of consecutive X is a
        /// flat block in the section-aware source AND in the linear padded volume — letting the gather move
        /// a whole row segment with one bulk <see cref="UnsafeUtility.MemCpy"/> instead of a branchy
        /// per-cell scatter (LI-1 follow-up #3). Generic over the element type so the voxel (uint) and light
        /// (ushort) gathers share one implementation; <c>MemCpy</c> over <c>GetUnsafeReadOnlyPtr</c>/
        /// <c>GetUnsafePtr</c> keeps the body Burst-compatible (P-2 Layer 1 moved the gather into
        /// <c>NeighborhoodLightingJob.Execute()</c>, so this now runs on a worker thread under Burst). Burst
        /// monomorphizes the generic per concrete <c>T</c>, so there is no genericity cost at runtime.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void CopyRun<T>(NativeArray<T> src, int srcStart, NativeArray<T> padded, int dstStart, int length, T sentinel)
            where T : unmanaged
        {
            if (src.IsCreated && src.Length > 0)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                // Restore the bounds validation NativeArray<T>.Copy performed (the raw MemCpy below
                // bypasses it). Gated on the SAME symbol Copy's own guards use, so it is compiled out in
                // optimized/player/Burst-no-checks builds — the hot-path MemCpy pays nothing — while the
                // editor and safety builds still trap an out-of-range run instead of corrupting memory.
                if (srcStart < 0 || dstStart < 0 || length < 0 ||
                    srcStart + length > src.Length || dstStart + length > padded.Length)
                    throw new ArgumentException("CopyRun: source or destination run is out of bounds.");
#endif
                long stride = UnsafeUtility.SizeOf<T>();
                byte* srcPtr = (byte*)src.GetUnsafeReadOnlyPtr() + srcStart * stride;
                byte* dstPtr = (byte*)padded.GetUnsafePtr() + dstStart * stride;
                UnsafeUtility.MemCpy(dstPtr, srcPtr, length * stride);
                return;
            }

            for (int k = 0; k < length; k++) padded[dstStart + k] = sentinel;
        }

        /// <summary>
        /// Scatters the center chunk + its 8 horizontal neighbors (each a section-contiguous full-chunk
        /// buffer) into the halo-padded linear volume consumed by the <c>NeighborhoodLightingJob</c>. A
        /// missing neighbor (uncreated/empty array) fills its region with <paramref name="sentinel"/>,
        /// reproducing the job's old per-neighbor missing-source sentinel. Writes EVERY padded cell.
        /// <para>
        /// Each padded horizontal row (fixed py, pz — 20 cells of X) is built as three contiguous runs: the
        /// 2-wide West halo, the 16-wide center span, and the 2-wide East halo, each copied in bulk from one
        /// source chunk via <see cref="CopyRun{T}"/>. The pz band picks the source row (<c>lz</c>) and the
        /// West/center/East source chunks once per row — pz∈[0,2)→south side (SW/S/SE), pz∈[18,20)→north
        /// side (NW/N/NE), else the center row (W/Center/E) — the same 3×3 dispatch the old per-cell
        /// <c>PaddedSourceIndex</c> performed, hoisted out of the inner loop. Bit-identical to the per-cell
        /// scatter it replaces (X is the fastest axis in both layouts, so the run order is preserved).
        /// Generic over the element type so the voxel and light gathers share one body and cannot silently
        /// diverge; the public <see cref="GatherPaddedVoxels"/>/<see cref="GatherPaddedLight"/> wrappers
        /// bind <c>T</c> and the matching sentinel.
        /// </para>
        /// </summary>
        private static void GatherPadded<T>(NativeArray<T> padded,
            NativeArray<T> center, NativeArray<T> w, NativeArray<T> e, NativeArray<T> s, NativeArray<T> n,
            NativeArray<T> sw, NativeArray<T> nw, NativeArray<T> se, NativeArray<T> ne, T sentinel)
            where T : unmanaged
        {
            for (int py = 0; py < CHUNK_HEIGHT; py++)
            {
                for (int pz = 0; pz < PADDED_CHUNK_WIDTH; pz++)
                {
                    // Resolve the pz band once per row: source-local Z + the West/center/East source chunks.
                    int lz;
                    NativeArray<T> west, mid, east;
                    if (pz < LIGHTING_HALO) // South side
                    {
                        lz = pz + CHUNK_WIDTH - LIGHTING_HALO;
                        west = sw;
                        mid = s;
                        east = se;
                    }
                    else if (pz >= CHUNK_WIDTH + LIGHTING_HALO) // North side
                    {
                        lz = pz - CHUNK_WIDTH - LIGHTING_HALO;
                        west = nw;
                        mid = n;
                        east = ne;
                    }
                    else // Center row
                    {
                        lz = pz - LIGHTING_HALO;
                        west = w;
                        mid = center;
                        east = e;
                    }

                    int rowBase = GetPaddedLightingIndex(0, py, pz);

                    // West halo: padded px[0,2) <- west-side chunk local x[14,16).
                    CopyRun(west, GetFlattenedIndexInChunk(CHUNK_WIDTH - LIGHTING_HALO, py, lz),
                        padded, rowBase, LIGHTING_HALO, sentinel);
                    // Center span: padded px[2,18) <- center-side chunk local x[0,16).
                    CopyRun(mid, GetFlattenedIndexInChunk(0, py, lz),
                        padded, rowBase + LIGHTING_HALO, CHUNK_WIDTH, sentinel);
                    // East halo: padded px[18,20) <- east-side chunk local x[0,2).
                    CopyRun(east, GetFlattenedIndexInChunk(0, py, lz),
                        padded, rowBase + CHUNK_WIDTH + LIGHTING_HALO, LIGHTING_HALO, sentinel);
                }
            }
        }

        /// <summary>
        /// Voxel gather: fills the padded voxel volume from the center + 8 neighbor voxel buffers, missing
        /// sources stamped <c>uint.MaxValue</c>. Thin typed wrapper over <see cref="GatherPadded{T}"/>.
        /// </summary>
        public static void GatherPaddedVoxels(NativeArray<uint> padded,
            NativeArray<uint> center, NativeArray<uint> w, NativeArray<uint> e, NativeArray<uint> s, NativeArray<uint> n,
            NativeArray<uint> sw, NativeArray<uint> nw, NativeArray<uint> se, NativeArray<uint> ne)
        {
            GatherPadded(padded, center, w, e, s, n, sw, nw, se, ne, uint.MaxValue);
        }

        /// <summary>
        /// Light gather: fills the padded light volume from the center + 8 neighbor light buffers, missing
        /// sources stamped <c>ushort.MaxValue</c>. Thin typed wrapper over <see cref="GatherPadded{T}"/>; the
        /// voxel/light pair always agrees because both route through the same generic body.
        /// </summary>
        public static void GatherPaddedLight(NativeArray<ushort> padded,
            NativeArray<ushort> center, NativeArray<ushort> w, NativeArray<ushort> e, NativeArray<ushort> s, NativeArray<ushort> n,
            NativeArray<ushort> sw, NativeArray<ushort> nw, NativeArray<ushort> se, NativeArray<ushort> ne)
        {
            GatherPadded(padded, center, w, e, s, n, sw, nw, se, ne, ushort.MaxValue);
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
