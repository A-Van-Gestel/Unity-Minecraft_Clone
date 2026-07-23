using System;
using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Helpers
{
    public static class ChunkMath
    {
        // Aliases of the authoritative VoxelData world-dimension constants (CP-7/F8 unification —
        // one declaration site). A const-to-const reference compiles to an IL literal, so these
        // remain Burst-safe compile-time values; change the value in VoxelData only and every
        // derived constant below (shifts, masks, volumes, padded sizes) follows.
        public const int CHUNK_WIDTH = VoxelData.ChunkWidth;
        public const int CHUNK_HEIGHT = VoxelData.ChunkHeight;
        public const int SECTION_SIZE = 16;
        public const int SECTION_VOLUME = SECTION_SIZE * SECTION_SIZE * SECTION_SIZE; // 4096 for 16 section width & 16 section size

        /// <summary>Vertical sections per chunk column — the single derivation site (CP-7); alias this instead of re-deriving <c>ChunkHeight / 16</c>.</summary>
        public const int SECTIONS_PER_CHUNK = CHUNK_HEIGHT / SECTION_SIZE; // 8

        public const int CHUNK_VOLUME = SECTION_VOLUME * SECTIONS_PER_CHUNK; // 32768 (8 sections × 4096)

        // --- Chunk / region coordinate conversion (WS-1) ------------------------------------------
        // Voxel↔chunk↔region math via power-of-two shift/mask instead of float-roundtrip floors or
        // truncating integer `/`/`%`. Shift/mask is simultaneously the fastest AND the only always-correct
        // option: `>>` is floor division and `&` is positive modulo for BOTH signs, whereas C# integer `/`
        // truncates toward zero (wrong for negative coordinates) and `Mathf.FloorToInt((float)v / 16)` also
        // loses integer precision past ±2^24. All-positive coordinates hide these differences today; every
        // helper below is byte-identical to the old idioms for v >= 0 and stays correct into the negative
        // quadrants (the Tier B prerequisite — WORLD_SCALING_ANALYSIS.md §3.2). The shift/mask amounts are
        // derived from CHUNK_WIDTH (16 = 1<<4) and the 32-chunk region side (1<<5); both power-of-two
        // couplings are asserted by the "Chunk Math" validation suite, so a future non-pow2 CHUNK_WIDTH
        // fails loudly instead of silently corrupting addressing.
        private const int CHUNK_WIDTH_SHIFT = 4; // log2(CHUNK_WIDTH) — floor-divide a voxel coord by 16
        private const int CHUNK_WIDTH_MASK = CHUNK_WIDTH - 1; // 15 — positive modulo 16 (local voxel coord)
        public const int CHUNKS_PER_REGION_SIDE = 32;
        private const int REGION_SHIFT = 5; // log2(CHUNKS_PER_REGION_SIDE) — floor-divide a chunk index by 32
        private const int REGION_MASK = CHUNKS_PER_REGION_SIDE - 1; // 31 — positive modulo 32 (local region slot)

        /// <summary>
        /// Floor-divides an integer voxel coordinate by <see cref="CHUNK_WIDTH"/> to get its chunk index,
        /// correct for negative coordinates (unlike truncating <c>/</c>). Burst-safe.
        /// </summary>
        /// <param name="voxel">A voxel coordinate on one axis (X or Z).</param>
        /// <returns>The chunk index containing that voxel.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int VoxelToChunk(int voxel) => voxel >> CHUNK_WIDTH_SHIFT;

        /// <summary>
        /// Returns the voxel's position local to its chunk (positive modulo <see cref="CHUNK_WIDTH"/>),
        /// correct for negative coordinates (unlike truncating <c>%</c>). Burst-safe.
        /// </summary>
        /// <param name="voxel">A voxel coordinate on one axis (X or Z).</param>
        /// <returns>The chunk-local coordinate in <c>[0, CHUNK_WIDTH)</c>.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int VoxelToLocal(int voxel) => voxel & CHUNK_WIDTH_MASK;

        /// <summary>
        /// Floor-divides a chunk index by <see cref="CHUNKS_PER_REGION_SIDE"/> to get its region coordinate,
        /// correct for negative indices. Burst-safe.
        /// </summary>
        /// <param name="chunk">A chunk index on one axis (X or Z).</param>
        /// <returns>The region coordinate containing that chunk.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ChunkToRegion(int chunk) => chunk >> REGION_SHIFT;

        /// <summary>
        /// Returns the chunk's local slot within its region file (positive modulo
        /// <see cref="CHUNKS_PER_REGION_SIDE"/>), correct for negative indices. Burst-safe.
        /// </summary>
        /// <param name="chunk">A chunk index on one axis (X or Z).</param>
        /// <returns>The region-local slot in <c>[0, CHUNKS_PER_REGION_SIDE)</c>.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ChunkToRegionLocal(int chunk) => chunk & REGION_MASK;

        /// <summary>
        /// True when the voxel coordinate is an exact chunk origin (a multiple of <see cref="CHUNK_WIDTH"/>),
        /// correct for negative coordinates. The sanctioned alignment test — use this instead of inline
        /// <c>% CHUNK_WIDTH == 0</c> (equivalent for the ==0 case, but keeps chunk math on the helpers). Burst-safe.
        /// </summary>
        /// <param name="voxel">A voxel coordinate on one axis (X or Z).</param>
        /// <returns>True when the coordinate lies on a chunk origin.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsChunkAligned(int voxel) => (voxel & CHUNK_WIDTH_MASK) == 0;

        /// <summary>
        /// Floor-divides a floating-point world coordinate to its chunk index: floors to the integer voxel
        /// coordinate first (full float precision), then shifts. Equivalent to
        /// <c>Mathf.FloorToInt(world / CHUNK_WIDTH)</c> for the reachable range but Burst-safe
        /// (<see cref="math.floor"/>, not <c>Mathf</c>) and negative-correct.
        /// </summary>
        /// <param name="world">A world-space coordinate on one axis (X or Z).</param>
        /// <returns>The chunk index containing that world position.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int WorldToChunk(float world) => (int)math.floor(world) >> CHUNK_WIDTH_SHIFT;

        /// <summary>
        /// General integer floor division for divisors that are not powers of two (those use the
        /// shift helpers above). Exact for any <paramref name="value"/> to the ±2³¹ edge, correct for
        /// negative values (unlike truncating <c>/</c>) and free of the ±2²⁴ precision cap of the
        /// <c>(int)math.floor((float)v / d)</c> idiom. Burst-safe.
        /// </summary>
        /// <param name="value">The dividend (any sign).</param>
        /// <param name="divisor">The divisor; must be ≥ 1 (callers clamp — not asserted, Burst code).</param>
        /// <returns>The floor of <c>value / divisor</c>.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int FloorDiv(int value, int divisor)
        {
            int quotient = value / divisor;
            return (value % divisor != 0 && (value ^ divisor) < 0) ? quotient - 1 : quotient;
        }

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

        // --- Halo-padded fluid volume (TG-4 Phase 4b) ---------------------------------------------
        // The FluidTickJob reads a halo-padded neighborhood so Tier-2 BORDER fluid voxels can resolve cross-chunk
        // neighbor reads (the same gather mechanic LI-1 proved for lighting, but a WIDER halo). Two reach invariants,
        // each a single source of truth — bump them (and re-verify BH-4) only if the fluid logic's reach changes:
        //   • Horizontal: FLUID_HALO MUST be >= the widest distance any fluid read reaches PAST a seam. The
        //     pathfinder (CalculateFlowCost BFS) reaches Manhattan distance 4 (= FluidTierClassifier.MaxFlowSearchDepth),
        //     so FLUID_HALO = 4. A square halo of 4 covers the 4-cardinal diamond incl. its (±2,±2) diagonal corners.
        //   • Vertical: FLUID_VERTICAL_REACH = 1 — every read is at the source's level, one below, or one above,
        //     regardless of horizontal distance (the BFS only moves horizontally). Used by the Y-band gather variant
        //     (a later optimization); the full-height variant pads no Y (out-of-range Y is sentinel-guarded).
        public const int FLUID_HALO = 4;
        public const int FLUID_VERTICAL_REACH = 1;
        public const int PADDED_FLUID_WIDTH = CHUNK_WIDTH + 2 * FLUID_HALO; // 24
        public const int PADDED_FLUID_HORIZONTAL_AREA = PADDED_FLUID_WIDTH * PADDED_FLUID_WIDTH; // 576
        public const int PADDED_FLUID_VOLUME = PADDED_FLUID_HORIZONTAL_AREA * CHUNK_HEIGHT; // 73,728

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
        /// Flat index into the full-height halo-padded fluid volume (see <see cref="PADDED_FLUID_VOLUME"/>). Same
        /// linear layout as <see cref="GetPaddedLightingIndex"/> but with the wider <see cref="FLUID_HALO"/>: a
        /// chunk-local position <c>(lx, ly, lz)</c> with <c>lx, lz ∈ [-4, 19]</c> maps to <c>(lx + 4, ly, lz + 4)</c>
        /// with <c>px, pz ∈ [0, 24)</c>, so the center chunk's [0,16) range lives at padded [4,20). Layout: X fastest
        /// (stride 1), then Z (stride 24), then Y (stride 576). Y is defensively clamped exactly as the lighting
        /// index does; callers that must distinguish an out-of-range Y check the Y bound BEFORE indexing.
        /// </summary>
        /// <param name="px">Padded X (0-23).</param>
        /// <param name="py">Padded/global Y (0-ChunkHeight).</param>
        /// <param name="pz">Padded Z (0-23).</param>
        /// <returns>The flattened index into the padded fluid volume.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetPaddedFluidIndex(int px, int py, int pz)
        {
            py = math.clamp(py, 0, CHUNK_HEIGHT - 1);
            return px + pz * PADDED_FLUID_WIDTH + py * PADDED_FLUID_HORIZONTAL_AREA;
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
        /// Scatters the center chunk + its 8 horizontal neighbors (each a section-contiguous full-chunk buffer) into
        /// the halo-padded linear volume, restricted to the Y-band <c>[<paramref name="bandMinY"/>,
        /// <paramref name="bandMinY"/> + <paramref name="bandCount"/>)</c>. Source rows are read at their <b>global</b>
        /// Y (<c>gy = bandMinY + by</c>); destination rows are written at the <b>band-local</b> Y (<c>by</c>), so the
        /// padded buffer holds only <paramref name="bandCount"/> rows (a band-sized prefix of a full-height
        /// allocation). The full-height gather is just the special case <c>bandMinY = 0, bandCount = CHUNK_HEIGHT</c>
        /// (the lighting gathers pass their LI-2 band); the TG-4 Y-band fluid gather passes the tight active-fluid band
        /// (see <see cref="GatherPaddedFluidVoxelsBand"/>). A missing neighbor (uncreated/empty array) fills its
        /// region with <paramref name="sentinel"/>, reproducing the per-neighbor missing-source sentinel. Writes
        /// EVERY padded cell <b>in the band</b>; rows outside the band are never touched (callers reading band-local
        /// Y must guard their own bounds). Parameterized by <paramref name="halo"/> + <paramref name="paddedWidth"/>
        /// so the same body serves the lighting halo (2, width 20) and the wider fluid halo (4, width 24) — see the
        /// <see cref="GatherPaddedVoxels"/>/<see cref="GatherPaddedLight"/>/<see cref="GatherPaddedFluidVoxelsBand"/> wrappers.
        /// <para>
        /// Each padded horizontal row (fixed <c>by</c>, pz — <paramref name="paddedWidth"/> cells of X) is built as
        /// three contiguous runs: the <paramref name="halo"/>-wide West halo, the 16-wide center span, and the
        /// <paramref name="halo"/>-wide East halo, each copied in bulk from one source chunk via <see cref="CopyRun{T}"/>.
        /// The pz band picks the source row (<c>lz</c>) and the West/center/East source chunks once per row —
        /// pz∈[0,halo)→south side (SW/S/SE), pz∈[width−halo,width)→north side (NW/N/NE), else the center row
        /// (W/Center/E) — hoisted out of the inner loop. Bit-identical to a per-cell scatter (X is the fastest axis in
        /// both layouts, so the run order is preserved). Generic over the element type so the voxel and light gathers
        /// share one body and cannot silently diverge; the wrappers bind <c>T</c>, the matching sentinel, and the geometry.
        /// </para>
        /// </summary>
        private static void GatherPaddedRange<T>(NativeArray<T> padded,
            NativeArray<T> center, NativeArray<T> w, NativeArray<T> e, NativeArray<T> s, NativeArray<T> n,
            NativeArray<T> sw, NativeArray<T> nw, NativeArray<T> se, NativeArray<T> ne,
            int bandMinY, int bandCount, int halo, int paddedWidth, T sentinel)
            where T : unmanaged
        {
            int paddedArea = paddedWidth * paddedWidth;
            for (int by = 0; by < bandCount; by++)
            {
                int gy = bandMinY + by; // global source Y (band-local destination Y is `by`)
                for (int pz = 0; pz < paddedWidth; pz++)
                {
                    // Resolve the pz band once per row: source-local Z + the West/center/East source chunks.
                    int lz;
                    NativeArray<T> west, mid, east;
                    if (pz < halo) // South side
                    {
                        lz = pz + CHUNK_WIDTH - halo;
                        west = sw;
                        mid = s;
                        east = se;
                    }
                    else if (pz >= CHUNK_WIDTH + halo) // North side
                    {
                        lz = pz - CHUNK_WIDTH - halo;
                        west = nw;
                        mid = n;
                        east = ne;
                    }
                    else // Center row
                    {
                        lz = pz - halo;
                        west = w;
                        mid = center;
                        east = e;
                    }

                    int rowBase = by * paddedArea + pz * paddedWidth; // band-local padded index of px=0 in this row

                    // West halo: padded px[0,halo) <- west-side chunk local x[16−halo,16).
                    CopyRun(west, GetFlattenedIndexInChunk(CHUNK_WIDTH - halo, gy, lz),
                        padded, rowBase, halo, sentinel);
                    // Center span: padded px[halo,halo+16) <- center-side chunk local x[0,16).
                    CopyRun(mid, GetFlattenedIndexInChunk(0, gy, lz),
                        padded, rowBase + halo, CHUNK_WIDTH, sentinel);
                    // East halo: padded px[halo+16,width) <- east-side chunk local x[0,halo).
                    CopyRun(east, GetFlattenedIndexInChunk(0, gy, lz),
                        padded, rowBase + CHUNK_WIDTH + halo, halo, sentinel);
                }
            }
        }

        /// <summary>
        /// Lighting voxel gather: fills the Y-band <c>[<paramref name="bandMinY"/>, <paramref name="bandHeight"/>)</c>
        /// of the padded voxel volume from the center + 8 neighbor voxel buffers, missing sources stamped
        /// <c>uint.MaxValue</c>. The band is a prefix of a full-height allocation (LI-2): destination rows are
        /// band-local (<c>by = gy − bandMinY</c>), rows outside the band are never written, and
        /// <c>NeighborhoodLightingJob</c> answers reads there virtually from the band's region summaries.
        /// Pass <c>0, CHUNK_HEIGHT</c> for the full-height gather. Thin typed wrapper over
        /// <see cref="GatherPaddedRange{T}"/> bound to the <see cref="LIGHTING_HALO"/>/<see cref="PADDED_CHUNK_WIDTH"/>
        /// geometry.
        /// </summary>
        /// <param name="bandMinY">Global Y of the band's first gathered row (band-local row 0).</param>
        /// <param name="bandHeight">Exclusive top of the band in global Y, in <c>(bandMinY, CHUNK_HEIGHT]</c>.</param>
        public static void GatherPaddedVoxels(NativeArray<uint> padded,
            NativeArray<uint> center, NativeArray<uint> w, NativeArray<uint> e, NativeArray<uint> s, NativeArray<uint> n,
            NativeArray<uint> sw, NativeArray<uint> nw, NativeArray<uint> se, NativeArray<uint> ne, int bandMinY, int bandHeight)
        {
            GatherPaddedRange(padded, center, w, e, s, n, sw, nw, se, ne, bandMinY, bandHeight - bandMinY, LIGHTING_HALO, PADDED_CHUNK_WIDTH, uint.MaxValue);
        }

        /// <summary>
        /// Fluid voxel gather (TG-4 Y-band): fills only the Y-band <c>[<paramref name="bandMinY"/>,
        /// <paramref name="bandMinY"/> + <paramref name="bandCount"/>)</c> of the <see cref="PADDED_FLUID_WIDTH"/>-wide
        /// padded volume — a band-sized prefix of a full-height <see cref="PADDED_FLUID_VOLUME"/> allocation. Since
        /// every fluid read is within <see cref="FLUID_VERTICAL_REACH"/> of an active source in Y, sizing the gather
        /// to <c>[minActiveY − reach, maxActiveY + reach]</c> drops no read the full-height gather would have served,
        /// while making the per-tick copy independent of world height. Destination rows are band-local
        /// (<c>py = y − bandMinY</c>), so <c>FluidTickJob.GetStateLocal</c> must offset its Y read by the same
        /// <paramref name="bandMinY"/> and treat out-of-band Y as void. Thin typed wrapper over
        /// <see cref="GatherPaddedRange{T}"/> bound to the <see cref="FLUID_HALO"/> geometry.
        /// </summary>
        /// <param name="bandMinY">The global Y of the band's first row (band-local Y 0). Maps to source <c>gy = bandMinY + py</c>.</param>
        /// <param name="bandCount">The number of Y rows in the band; the padded prefix written is <c>bandCount × PADDED_FLUID_HORIZONTAL_AREA</c>.</param>
        public static void GatherPaddedFluidVoxelsBand(NativeArray<uint> padded,
            NativeArray<uint> center, NativeArray<uint> w, NativeArray<uint> e, NativeArray<uint> s, NativeArray<uint> n,
            NativeArray<uint> sw, NativeArray<uint> nw, NativeArray<uint> se, NativeArray<uint> ne, int bandMinY, int bandCount)
        {
            GatherPaddedRange(padded, center, w, e, s, n, sw, nw, se, ne, bandMinY, bandCount, FLUID_HALO, PADDED_FLUID_WIDTH, uint.MaxValue);
        }

        /// <summary>
        /// Light gather: fills the Y-band <c>[<paramref name="bandMinY"/>, <paramref name="bandHeight"/>)</c> of the
        /// padded light volume from the center + 8 neighbor light buffers, missing sources stamped
        /// <c>ushort.MaxValue</c> (LI-2 — see <see cref="GatherPaddedVoxels"/> for the band semantics; pass
        /// <c>0, CHUNK_HEIGHT</c> for full height). Thin typed wrapper over <see cref="GatherPaddedRange{T}"/> bound
        /// to the lighting geometry; the voxel/light pair always agrees because both route through the same generic body.
        /// </summary>
        /// <param name="bandMinY">Global Y of the band's first gathered row (band-local row 0).</param>
        /// <param name="bandHeight">Exclusive top of the band in global Y, in <c>(bandMinY, CHUNK_HEIGHT]</c>.</param>
        public static void GatherPaddedLight(NativeArray<ushort> padded,
            NativeArray<ushort> center, NativeArray<ushort> w, NativeArray<ushort> e, NativeArray<ushort> s, NativeArray<ushort> n,
            NativeArray<ushort> sw, NativeArray<ushort> nw, NativeArray<ushort> se, NativeArray<ushort> ne, int bandMinY, int bandHeight)
        {
            GatherPaddedRange(padded, center, w, e, s, n, sw, nw, se, ne, bandMinY, bandHeight - bandMinY, LIGHTING_HALO, PADDED_CHUNK_WIDTH, ushort.MaxValue);
        }

        /// <summary>
        /// Copies the center chunk's band region [2,18)×[<paramref name="bandMinY"/>,<paramref name="bandHeight"/>)×[2,18)
        /// out of the halo-padded light volume into a section-contiguous full-chunk light buffer (the
        /// <see cref="GetFlattenedIndexInChunk"/> layout that <see cref="ChunkData.ApplyJobLightMap"/> reads
        /// back). Source rows are band-local (<c>cy − bandMinY</c>); destination rows are absolute. The job
        /// only writes light into the center region; voxels are never modified, so only light is extracted.
        /// LI-2: both bounds MUST equal the values the job's volumes were gathered with — rows outside the
        /// band are un-gathered scratch in the padded volume and are left untouched in
        /// <paramref name="centerOut"/>, which therefore keeps its schedule-time snapshot values there (the
        /// job cannot have changed them, so the subsequent full merge is unchanged-identity for those rows).
        /// Pass <c>0, CHUNK_HEIGHT</c> for a full-height extract.
        /// </summary>
        /// <param name="bandMinY">Global Y of the band's first gathered row (band-local row 0).</param>
        /// <param name="bandHeight">Exclusive top of the band in global Y, in <c>(bandMinY, CHUNK_HEIGHT]</c>.</param>
        public static void ExtractCenterLight(NativeArray<ushort> padded, NativeArray<ushort> centerOut, int bandMinY, int bandHeight)
        {
            for (int cy = bandMinY; cy < bandHeight; cy++)
            {
                for (int cz = 0; cz < CHUNK_WIDTH; cz++)
                {
                    // Each center X-row is contiguous in both layouts (X is the fastest axis), so copy the
                    // 16-wide span in bulk: source = padded center span px[2,18) at band-local (cy−bandMinY,
                    // cz+2); dest = the section-contiguous center row at (cx=0, cy, cz). Bit-identical to the
                    // per-cell loop.
                    NativeArray<ushort>.Copy(
                        padded, GetPaddedLightingIndex(LIGHTING_HALO, cy - bandMinY, cz + LIGHTING_HALO),
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
