using System.Collections.Generic;
using Editor.Validation.Framework;
using Helpers;
using Unity.Collections;
using UnityEngine;

namespace Editor.Validation
{
    /// <summary>
    /// <see cref="ChunkMathValidationSuite"/> — the NS-5 G1 padded-volume pins. The halo-padded index helpers
    /// and the three neighborhood gathers feed <c>NeighborhoodLightingJob</c> and <c>FluidTickJob</c> but had
    /// no direct assertion of their own: their only coverage was the Lighting/Behavior suites' end-to-end
    /// oracle compares, which see an addressing defect only where the field they compare actually varies
    /// across the affected cells.
    /// <para>
    /// The gather oracle here is an independent per-cell scatter derived from the documented layout (padded
    /// coordinate <c>p</c> maps to grid-local <c>g = p − halo</c>; <c>g &lt; 0</c> reads the negative-side
    /// neighbor at <c>g + 16</c>, <c>g &gt;= 16</c> the positive-side neighbor at <c>g − 16</c>), not from
    /// <c>GatherPaddedRange</c>'s three-bulk-runs-per-row structure — so a defect in the run decomposition
    /// cannot be reproduced by the oracle. Every source cell carries a value encoding its own
    /// <c>(chunk, x, y, z)</c>: a uniform fill would let a wrong source chunk, an X/Z transposition, or an
    /// off-by-one halo match by coincidence.
    /// </para>
    /// </summary>
    public static partial class ChunkMathValidationSuite
    {
        /// <summary>Neighbor slots in the order <c>GatherPaddedRange</c> takes them.</summary>
        private const int NB_CENTER = 0;
        private const int NB_W = 1;
        private const int NB_E = 2;
        private const int NB_S = 3;
        private const int NB_N = 4;
        private const int NB_SW = 5;
        private const int NB_NW = 6;
        private const int NB_SE = 7;
        private const int NB_NE = 8;
        private const int NB_COUNT = 9;

        /// <summary>Maximum per-scenario mismatch lines logged before the diff is truncated.</summary>
        private const int MAX_PADDED_DIFF_LINES = 6;

        /// <summary>Rows in the partial-band scenarios — small enough that the ushort fill encoding stays exact.</summary>
        private const int TEST_BAND_HEIGHT = 8;

        static partial void AddPaddedVolumeScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario("Padded Index Stride Pin (lighting + fluid geometry)", RunPaddedIndexStridePin));
            scenarios.Add(new Scenario("Padded Voxel Gather == Per-Cell Oracle (full height)", RunPaddedVoxelGatherFullHeight));
            scenarios.Add(new Scenario("Padded Gather Missing-Neighbor Sentinel (each of 8 sides)", RunPaddedGatherMissingNeighbors));
            scenarios.Add(new Scenario("Padded Gather Y-Band (band-local destination rows)", RunPaddedGatherYBand));
            scenarios.Add(new Scenario("Padded Light Gather + ExtractCenterLight Round-Trip", RunPaddedLightGatherAndExtract));
            scenarios.Add(new Scenario("Padded Fluid Gather == Per-Cell Oracle (halo 4, bandCount)", RunPaddedFluidGather));
        }

        // ── Fixture helpers ──────────────────────────────────────────────────────────────────────────

        /// <summary>Neighbor slot for a chunk offset, or -1 for an offset that is not one of the nine.</summary>
        private static int NeighborSlot(int dx, int dz)
        {
            if (dx == 0 && dz == 0) return NB_CENTER;
            if (dx == -1 && dz == 0) return NB_W;
            if (dx == 1 && dz == 0) return NB_E;
            if (dx == 0 && dz == -1) return NB_S;
            if (dx == 0 && dz == 1) return NB_N;
            if (dx == -1 && dz == -1) return NB_SW;
            if (dx == -1 && dz == 1) return NB_NW;
            if (dx == 1 && dz == -1) return NB_SE;
            if (dx == 1 && dz == 1) return NB_NE;
            return -1;
        }

        /// <summary>
        /// Splits a grid-local coordinate into (chunk offset, chunk-local coordinate) per the documented halo
        /// mapping. Valid for <c>g ∈ [−16, 32)</c>, which covers every halo this engine uses.
        /// </summary>
        private static void SplitGrid(int g, out int delta, out int local)
        {
            if (g < 0)
            {
                delta = -1;
                local = g + ChunkMath.CHUNK_WIDTH;
            }
            else if (g >= ChunkMath.CHUNK_WIDTH)
            {
                delta = 1;
                local = g - ChunkMath.CHUNK_WIDTH;
            }
            else
            {
                delta = 0;
                local = g;
            }
        }

        /// <summary>Position-encoding cell value: distinct per (chunk slot, x, y, z), never the uint sentinel.</summary>
        private static uint EncodeCell(int slot, int x, int y, int z) =>
            1u + (uint)((((slot * ChunkMath.CHUNK_WIDTH + x) * ChunkMath.CHUNK_HEIGHT + y) * ChunkMath.CHUNK_WIDTH) + z);

        /// <summary>
        /// Band-limited position-encoding value for the ushort gathers: distinct per (chunk slot, x, band row,
        /// z) and always below <see cref="ushort.MaxValue"/>, so the sentinel stays unambiguous.
        /// </summary>
        private static ushort EncodeCellShort(int slot, int x, int bandRow, int z) =>
            (ushort)(1 + ((((slot * ChunkMath.CHUNK_WIDTH + x) * TEST_BAND_HEIGHT + bandRow) * ChunkMath.CHUNK_WIDTH) + z));

        /// <summary>Allocates and position-fills the nine source chunks; slots listed in <paramref name="absent"/> stay uncreated.</summary>
        private static NativeArray<uint>[] CreateVoxelChunks(params int[] absent)
        {
            NativeArray<uint>[] chunks = new NativeArray<uint>[NB_COUNT];

            for (int slot = 0; slot < NB_COUNT; slot++)
            {
                bool skip = false;
                foreach (int a in absent)
                {
                    if (a != slot) continue;
                    skip = true;
                    break;
                }

                if (skip)
                    continue; // left as default(NativeArray<uint>) — IsCreated == false, the missing-source case

                chunks[slot] = new NativeArray<uint>(ChunkMath.CHUNK_VOLUME, Allocator.Persistent);
                for (int y = 0; y < ChunkMath.CHUNK_HEIGHT; y++)
                {
                    for (int z = 0; z < ChunkMath.CHUNK_WIDTH; z++)
                    {
                        for (int x = 0; x < ChunkMath.CHUNK_WIDTH; x++)
                            chunks[slot][ChunkMath.GetFlattenedIndexInChunk(x, y, z)] = EncodeCell(slot, x, y, z);
                    }
                }
            }

            return chunks;
        }

        private static void DisposeChunks(NativeArray<uint>[] chunks)
        {
            if (chunks == null)
                return;

            for (int slot = 0; slot < chunks.Length; slot++)
            {
                if (chunks[slot].IsCreated)
                    chunks[slot].Dispose();
            }
        }

        /// <summary>
        /// Compares a gathered padded volume against the per-cell oracle. Returns the mismatch count and logs
        /// up to <see cref="MAX_PADDED_DIFF_LINES"/> bounded diffs naming the padded cell, the source it
        /// should have come from, and both values.
        /// </summary>
        private static int ComparePaddedAgainstOracle(string scenario, NativeArray<uint> padded,
            NativeArray<uint>[] chunks, int bandMinY, int bandCount, int halo, int paddedWidth)
        {
            int paddedArea = paddedWidth * paddedWidth;
            int diffs = 0;

            for (int by = 0; by < bandCount; by++)
            {
                int gy = bandMinY + by;
                for (int pz = 0; pz < paddedWidth; pz++)
                {
                    SplitGrid(pz - halo, out int dz, out int lz);
                    for (int px = 0; px < paddedWidth; px++)
                    {
                        SplitGrid(px - halo, out int dx, out int lx);

                        int slot = NeighborSlot(dx, dz);
                        uint expected = slot >= 0 && chunks[slot].IsCreated && chunks[slot].Length > 0
                            ? chunks[slot][ChunkMath.GetFlattenedIndexInChunk(lx, gy, lz)]
                            : uint.MaxValue;

                        uint actual = padded[by * paddedArea + pz * paddedWidth + px];
                        if (actual == expected)
                            continue;

                        if (++diffs <= MAX_PADDED_DIFF_LINES)
                            Debug.LogError($"[FAIL] {scenario} — padded ({px.ToString()},{by.ToString()},{pz.ToString()}) " +
                                           $"(global y {gy.ToString()}) should read slot {slot.ToString()} local " +
                                           $"({lx.ToString()},{gy.ToString()},{lz.ToString()}): expected " +
                                           $"{expected.ToString()}, got {actual.ToString()}.");
                    }
                }
            }

            return diffs;
        }

        // ── Scenarios ────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Pins both padded index helpers to their documented linear layout (X fastest, then Z, then Y), the
        /// halo offset that places the center chunk's <c>[0,16)</c> at <c>[halo, halo+16)</c>, the derived
        /// volume constants, and the defensive Y clamp. Note the padded volumes order Z before Y while the
        /// in-chunk layout orders Y before Z — the two live in the same file and must not be conflated.
        /// </summary>
        private static bool RunPaddedIndexStridePin()
        {
            // Lighting geometry: width 20, area 400.
            if (ChunkMath.PADDED_CHUNK_WIDTH != ChunkMath.CHUNK_WIDTH + 2 * ChunkMath.LIGHTING_HALO ||
                ChunkMath.PADDED_HORIZONTAL_AREA != ChunkMath.PADDED_CHUNK_WIDTH * ChunkMath.PADDED_CHUNK_WIDTH ||
                ChunkMath.PADDED_LIGHTING_VOLUME != ChunkMath.PADDED_HORIZONTAL_AREA * ChunkMath.CHUNK_HEIGHT)
            {
                Debug.LogError("[FAIL] Padded Index Stride Pin — lighting padded geometry constants are not " +
                               "consistent with CHUNK_WIDTH / LIGHTING_HALO / CHUNK_HEIGHT.");
                return false;
            }

            if (ChunkMath.PADDED_FLUID_WIDTH != ChunkMath.CHUNK_WIDTH + 2 * ChunkMath.FLUID_HALO ||
                ChunkMath.PADDED_FLUID_HORIZONTAL_AREA != ChunkMath.PADDED_FLUID_WIDTH * ChunkMath.PADDED_FLUID_WIDTH ||
                ChunkMath.PADDED_FLUID_VOLUME != ChunkMath.PADDED_FLUID_HORIZONTAL_AREA * ChunkMath.CHUNK_HEIGHT)
            {
                Debug.LogError("[FAIL] Padded Index Stride Pin — fluid padded geometry constants are not " +
                               "consistent with CHUNK_WIDTH / FLUID_HALO / CHUNK_HEIGHT.");
                return false;
            }

            // Strides, hand-derived from the documented layout.
            (int px, int py, int pz, int expected)[] lightCases =
            {
                (0, 0, 0, 0),
                (1, 0, 0, 1),                                       // X stride 1
                (0, 0, 1, ChunkMath.PADDED_CHUNK_WIDTH),            // Z stride 20
                (0, 1, 0, ChunkMath.PADDED_HORIZONTAL_AREA),        // Y stride 400
                (19, 0, 19, 19 + 19 * ChunkMath.PADDED_CHUNK_WIDTH),
            };

            foreach ((int px, int py, int pz, int expected) in lightCases)
            {
                int actual = ChunkMath.GetPaddedLightingIndex(px, py, pz);
                if (actual != expected)
                {
                    Debug.LogError($"[FAIL] Padded Index Stride Pin — lighting index ({px.ToString()}," +
                                   $"{py.ToString()},{pz.ToString()}) expected {expected.ToString()}, got {actual.ToString()}.");
                    return false;
                }
            }

            (int px, int py, int pz, int expected)[] fluidCases =
            {
                (0, 0, 0, 0),
                (1, 0, 0, 1),
                (0, 0, 1, ChunkMath.PADDED_FLUID_WIDTH),            // Z stride 24
                (0, 1, 0, ChunkMath.PADDED_FLUID_HORIZONTAL_AREA),  // Y stride 576
                (23, 0, 23, 23 + 23 * ChunkMath.PADDED_FLUID_WIDTH),
            };

            foreach ((int px, int py, int pz, int expected) in fluidCases)
            {
                int actual = ChunkMath.GetPaddedFluidIndex(px, py, pz);
                if (actual != expected)
                {
                    Debug.LogError($"[FAIL] Padded Index Stride Pin — fluid index ({px.ToString()}," +
                                   $"{py.ToString()},{pz.ToString()}) expected {expected.ToString()}, got {actual.ToString()}.");
                    return false;
                }
            }

            // The center chunk's [0,16) must land at [halo, halo+16) in both geometries.
            if (ChunkMath.GetPaddedLightingIndex(ChunkMath.LIGHTING_HALO, 0, ChunkMath.LIGHTING_HALO) !=
                ChunkMath.LIGHTING_HALO + ChunkMath.LIGHTING_HALO * ChunkMath.PADDED_CHUNK_WIDTH ||
                ChunkMath.GetPaddedFluidIndex(ChunkMath.FLUID_HALO, 0, ChunkMath.FLUID_HALO) !=
                ChunkMath.FLUID_HALO + ChunkMath.FLUID_HALO * ChunkMath.PADDED_FLUID_WIDTH)
            {
                Debug.LogError("[FAIL] Padded Index Stride Pin — the center chunk's origin does not land at the " +
                               "halo offset in one of the two padded geometries.");
                return false;
            }

            // Y clamp, mirroring GetFlattenedIndexInChunk's documented defensive behavior.
            if (ChunkMath.GetPaddedLightingIndex(3, ChunkMath.CHUNK_HEIGHT, 5) !=
                ChunkMath.GetPaddedLightingIndex(3, ChunkMath.CHUNK_HEIGHT - 1, 5) ||
                ChunkMath.GetPaddedLightingIndex(3, -1, 5) != ChunkMath.GetPaddedLightingIndex(3, 0, 5) ||
                ChunkMath.GetPaddedFluidIndex(3, ChunkMath.CHUNK_HEIGHT, 5) !=
                ChunkMath.GetPaddedFluidIndex(3, ChunkMath.CHUNK_HEIGHT - 1, 5) ||
                ChunkMath.GetPaddedFluidIndex(3, -1, 5) != ChunkMath.GetPaddedFluidIndex(3, 0, 5))
            {
                Debug.LogError("[FAIL] Padded Index Stride Pin — the padded Y clamp does not collapse " +
                               "out-of-range rows onto the edge rows.");
                return false;
            }

            Debug.Log("[PASS] Padded Index Stride Pin (lighting + fluid geometry)");
            return true;
        }

        /// <summary>
        /// Full-height voxel gather with all nine sources present, compared cell-for-cell against the per-cell
        /// oracle. Opens with a trivial-case check the oracle must satisfy before it is trusted: with only the
        /// center chunk present, every halo cell is the sentinel and every center cell is the center chunk's
        /// own value.
        /// </summary>
        private static bool RunPaddedVoxelGatherFullHeight()
        {
            const int halo = ChunkMath.LIGHTING_HALO;
            const int width = ChunkMath.PADDED_CHUNK_WIDTH;

            NativeArray<uint>[] chunks = null;
            NativeArray<uint> padded = default;

            try
            {
                // --- Oracle sanity: center-only, so every halo cell must be sentinel. ---
                chunks = CreateVoxelChunks(NB_W, NB_E, NB_S, NB_N, NB_SW, NB_NW, NB_SE, NB_NE);
                padded = new NativeArray<uint>(ChunkMath.PADDED_LIGHTING_VOLUME, Allocator.Persistent);

                ChunkMath.GatherPaddedVoxels(padded, chunks[NB_CENTER], chunks[NB_W], chunks[NB_E], chunks[NB_S],
                    chunks[NB_N], chunks[NB_SW], chunks[NB_NW], chunks[NB_SE], chunks[NB_NE], 0, ChunkMath.CHUNK_HEIGHT);

                int haloCells = 0;
                for (int pz = 0; pz < width; pz++)
                {
                    for (int px = 0; px < width; px++)
                    {
                        bool isHalo = px < halo || px >= halo + ChunkMath.CHUNK_WIDTH ||
                                      pz < halo || pz >= halo + ChunkMath.CHUNK_WIDTH;
                        if (!isHalo)
                            continue;

                        haloCells++;
                        uint v = padded[5 * ChunkMath.PADDED_HORIZONTAL_AREA + pz * width + px];
                        if (v == uint.MaxValue)
                            continue;

                        Debug.LogError($"[FAIL] Padded Voxel Gather (oracle sanity) — center-only gather left halo cell " +
                                       $"({px.ToString()},{pz.ToString()}) as {v.ToString()}, expected the sentinel.");
                        return false;
                    }
                }

                if (haloCells != width * width - ChunkMath.CHUNK_WIDTH * ChunkMath.CHUNK_WIDTH)
                {
                    Debug.LogError($"[FAIL] Padded Voxel Gather (oracle sanity) — counted {haloCells.ToString()} halo " +
                                   "cells; the scenario's own halo geometry is wrong.");
                    return false;
                }

                DisposeChunks(chunks);

                // --- Full gather, all nine sources present. ---
                chunks = CreateVoxelChunks();
                ChunkMath.GatherPaddedVoxels(padded, chunks[NB_CENTER], chunks[NB_W], chunks[NB_E], chunks[NB_S],
                    chunks[NB_N], chunks[NB_SW], chunks[NB_NW], chunks[NB_SE], chunks[NB_NE], 0, ChunkMath.CHUNK_HEIGHT);

                int diffs = ComparePaddedAgainstOracle("Padded Voxel Gather (full height)", padded, chunks,
                    0, ChunkMath.CHUNK_HEIGHT, halo, width);

                if (diffs > 0)
                {
                    Debug.LogError($"[FAIL] Padded Voxel Gather == Per-Cell Oracle (full height) — {diffs.ToString()} " +
                                   $"mismatch(es)" + (diffs > MAX_PADDED_DIFF_LINES ? $" ({MAX_PADDED_DIFF_LINES.ToString()} shown)." : "."));
                    return false;
                }
            }
            finally
            {
                if (padded.IsCreated) padded.Dispose();
                DisposeChunks(chunks);
            }

            Debug.Log("[PASS] Padded Voxel Gather == Per-Cell Oracle (full height)");
            return true;
        }

        /// <summary>
        /// Each of the eight neighbors absent in turn: the absent chunk's region of the padded volume must be
        /// stamped with the sentinel and every other region must still carry its own source's values. A gather
        /// that filled the wrong region on a missing neighbor — or filled nothing — fails here.
        /// </summary>
        private static bool RunPaddedGatherMissingNeighbors()
        {
            const int halo = ChunkMath.LIGHTING_HALO;
            const int width = ChunkMath.PADDED_CHUNK_WIDTH;
            int[] sides = { NB_W, NB_E, NB_S, NB_N, NB_SW, NB_NW, NB_SE, NB_NE };

            NativeArray<uint> padded = default;

            try
            {
                padded = new NativeArray<uint>(ChunkMath.PADDED_LIGHTING_VOLUME, Allocator.Persistent);

                foreach (int missing in sides)
                {
                    NativeArray<uint>[] chunks = null;
                    try
                    {
                        chunks = CreateVoxelChunks(missing);
                        ChunkMath.GatherPaddedVoxels(padded, chunks[NB_CENTER], chunks[NB_W], chunks[NB_E], chunks[NB_S],
                            chunks[NB_N], chunks[NB_SW], chunks[NB_NW], chunks[NB_SE], chunks[NB_NE],
                            0, ChunkMath.CHUNK_HEIGHT);

                        int diffs = ComparePaddedAgainstOracle($"Padded Gather (missing slot {missing.ToString()})",
                            padded, chunks, 0, ChunkMath.CHUNK_HEIGHT, halo, width);

                        if (diffs > 0)
                        {
                            Debug.LogError($"[FAIL] Padded Gather Missing-Neighbor Sentinel — slot {missing.ToString()} " +
                                           $"absent produced {diffs.ToString()} mismatch(es).");
                            return false;
                        }
                    }
                    finally
                    {
                        DisposeChunks(chunks);
                    }
                }
            }
            finally
            {
                if (padded.IsCreated) padded.Dispose();
            }

            Debug.Log("[PASS] Padded Gather Missing-Neighbor Sentinel (each of 8 sides)");
            return true;
        }

        /// <summary>
        /// Partial-band gather (LI-2): source rows are read at their global Y but written at band-local Y, so
        /// the padded buffer holds only the band as a prefix. Asserts the band contents against the oracle AND
        /// that rows past the band are left untouched — a gather that wrote at global Y would both corrupt the
        /// prefix and scribble outside it.
        /// </summary>
        private static bool RunPaddedGatherYBand()
        {
            const int halo = ChunkMath.LIGHTING_HALO;
            const int width = ChunkMath.PADDED_CHUNK_WIDTH;
            const int bandMinY = 40;
            const int bandHeight = bandMinY + TEST_BAND_HEIGHT; // exclusive global top

            NativeArray<uint>[] chunks = null;
            NativeArray<uint> padded = default;

            try
            {
                chunks = CreateVoxelChunks();
                padded = new NativeArray<uint>(ChunkMath.PADDED_LIGHTING_VOLUME, Allocator.Persistent);

                // Poison the whole buffer so "left untouched" is observable.
                const uint poison = 0xDEADBEEF;
                for (int i = 0; i < padded.Length; i++)
                    padded[i] = poison;

                ChunkMath.GatherPaddedVoxels(padded, chunks[NB_CENTER], chunks[NB_W], chunks[NB_E], chunks[NB_S],
                    chunks[NB_N], chunks[NB_SW], chunks[NB_NW], chunks[NB_SE], chunks[NB_NE], bandMinY, bandHeight);

                int diffs = ComparePaddedAgainstOracle("Padded Gather (Y-band)", padded, chunks,
                    bandMinY, TEST_BAND_HEIGHT, halo, width);

                if (diffs > 0)
                {
                    Debug.LogError($"[FAIL] Padded Gather Y-Band — {diffs.ToString()} mismatch(es) inside the band " +
                                   $"[{bandMinY.ToString()},{bandHeight.ToString()}).");
                    return false;
                }

                // Rows past the band must still hold the poison value.
                for (int by = TEST_BAND_HEIGHT; by < ChunkMath.CHUNK_HEIGHT; by++)
                {
                    int probe = by * ChunkMath.PADDED_HORIZONTAL_AREA;
                    if (padded[probe] == poison)
                        continue;

                    Debug.LogError($"[FAIL] Padded Gather Y-Band — band-local row {by.ToString()} is outside the " +
                                   $"{TEST_BAND_HEIGHT.ToString()}-row band but was written ({padded[probe].ToString()}); " +
                                   "the gather is not writing band-local rows.");
                    return false;
                }
            }
            finally
            {
                if (padded.IsCreated) padded.Dispose();
                DisposeChunks(chunks);
            }

            Debug.Log("[PASS] Padded Gather Y-Band (band-local destination rows)");
            return true;
        }

        /// <summary>
        /// The ushort light gather over a band, then <see cref="ChunkMath.ExtractCenterLight"/> back out: the
        /// extracted center buffer must reproduce the center chunk's own band rows exactly (source rows
        /// band-local, destination rows absolute), and must leave rows outside the band untouched.
        /// </summary>
        private static bool RunPaddedLightGatherAndExtract()
        {
            const int halo = ChunkMath.LIGHTING_HALO;
            const int width = ChunkMath.PADDED_CHUNK_WIDTH;
            const int bandMinY = 24;
            const int bandHeight = bandMinY + TEST_BAND_HEIGHT;

            NativeArray<ushort>[] chunks = new NativeArray<ushort>[NB_COUNT];
            NativeArray<ushort> padded = default;
            NativeArray<ushort> centerOut = default;

            try
            {
                for (int slot = 0; slot < NB_COUNT; slot++)
                {
                    chunks[slot] = new NativeArray<ushort>(ChunkMath.CHUNK_VOLUME, Allocator.Persistent);
                    for (int row = 0; row < TEST_BAND_HEIGHT; row++)
                    {
                        for (int z = 0; z < ChunkMath.CHUNK_WIDTH; z++)
                        {
                            for (int x = 0; x < ChunkMath.CHUNK_WIDTH; x++)
                            {
                                chunks[slot][ChunkMath.GetFlattenedIndexInChunk(x, bandMinY + row, z)] =
                                    EncodeCellShort(slot, x, row, z);
                            }
                        }
                    }
                }

                padded = new NativeArray<ushort>(ChunkMath.PADDED_LIGHTING_VOLUME, Allocator.Persistent);
                ChunkMath.GatherPaddedLight(padded, chunks[NB_CENTER], chunks[NB_W], chunks[NB_E], chunks[NB_S],
                    chunks[NB_N], chunks[NB_SW], chunks[NB_NW], chunks[NB_SE], chunks[NB_NE], bandMinY, bandHeight);

                // The gathered padded volume must match the per-cell oracle over the band.
                int diffs = 0;
                for (int by = 0; by < TEST_BAND_HEIGHT; by++)
                {
                    int gy = bandMinY + by;
                    for (int pz = 0; pz < width; pz++)
                    {
                        SplitGrid(pz - halo, out int dz, out int lz);
                        for (int px = 0; px < width; px++)
                        {
                            SplitGrid(px - halo, out int dx, out int lx);
                            int slot = NeighborSlot(dx, dz);
                            ushort expected = chunks[slot][ChunkMath.GetFlattenedIndexInChunk(lx, gy, lz)];
                            ushort actual = padded[by * ChunkMath.PADDED_HORIZONTAL_AREA + pz * width + px];
                            if (actual == expected)
                                continue;

                            if (++diffs <= MAX_PADDED_DIFF_LINES)
                                Debug.LogError($"[FAIL] Padded Light Gather — padded ({px.ToString()},{by.ToString()}," +
                                               $"{pz.ToString()}) expected {expected.ToString()} from slot " +
                                               $"{slot.ToString()}, got {actual.ToString()}.");
                        }
                    }
                }

                if (diffs > 0)
                {
                    Debug.LogError($"[FAIL] Padded Light Gather + ExtractCenterLight Round-Trip — {diffs.ToString()} " +
                                   "gather mismatch(es).");
                    return false;
                }

                // Extract the center back out; band rows must equal the center chunk, others stay poisoned.
                const ushort poison = 0xABCD;
                centerOut = new NativeArray<ushort>(ChunkMath.CHUNK_VOLUME, Allocator.Persistent);
                for (int i = 0; i < centerOut.Length; i++)
                    centerOut[i] = poison;

                ChunkMath.ExtractCenterLight(padded, centerOut, bandMinY, bandHeight);

                for (int y = 0; y < ChunkMath.CHUNK_HEIGHT; y++)
                {
                    bool inBand = y >= bandMinY && y < bandHeight;
                    for (int z = 0; z < ChunkMath.CHUNK_WIDTH; z++)
                    {
                        for (int x = 0; x < ChunkMath.CHUNK_WIDTH; x++)
                        {
                            int idx = ChunkMath.GetFlattenedIndexInChunk(x, y, z);
                            ushort expected = inBand ? chunks[NB_CENTER][idx] : poison;
                            if (centerOut[idx] == expected)
                                continue;

                            Debug.LogError($"[FAIL] Padded Light Gather + ExtractCenterLight Round-Trip — extracted " +
                                           $"({x.ToString()},{y.ToString()},{z.ToString()}) is " +
                                           $"{centerOut[idx].ToString()}, expected {expected.ToString()} " +
                                           $"({(inBand ? "in band" : "outside the band, must be untouched")}).");
                            return false;
                        }
                    }
                }
            }
            finally
            {
                if (centerOut.IsCreated) centerOut.Dispose();
                if (padded.IsCreated) padded.Dispose();
                for (int slot = 0; slot < chunks.Length; slot++)
                {
                    if (chunks[slot].IsCreated)
                        chunks[slot].Dispose();
                }
            }

            Debug.Log("[PASS] Padded Light Gather + ExtractCenterLight Round-Trip");
            return true;
        }

        /// <summary>
        /// The fluid gather's wider halo (4, width 24) against the same oracle, and the parameter-semantics
        /// pin: <see cref="ChunkMath.GatherPaddedFluidVoxelsBand"/> takes a band <b>count</b> where
        /// <see cref="ChunkMath.GatherPaddedVoxels"/> takes an exclusive band <b>top</b>. The two wrappers have
        /// identically shaped signatures, so passing one's convention to the other is a silent, plausible
        /// mistake; this asserts the fluid wrapper really does treat its last argument as a length.
        /// </summary>
        private static bool RunPaddedFluidGather()
        {
            const int halo = ChunkMath.FLUID_HALO;
            const int width = ChunkMath.PADDED_FLUID_WIDTH;
            const int bandMinY = 30;

            NativeArray<uint>[] chunks = null;
            NativeArray<uint> padded = default;

            try
            {
                chunks = CreateVoxelChunks();
                padded = new NativeArray<uint>(ChunkMath.PADDED_FLUID_VOLUME, Allocator.Persistent);

                ChunkMath.GatherPaddedFluidVoxelsBand(padded, chunks[NB_CENTER], chunks[NB_W], chunks[NB_E],
                    chunks[NB_S], chunks[NB_N], chunks[NB_SW], chunks[NB_NW], chunks[NB_SE], chunks[NB_NE],
                    bandMinY, TEST_BAND_HEIGHT);

                int diffs = ComparePaddedAgainstOracle("Padded Fluid Gather", padded, chunks,
                    bandMinY, TEST_BAND_HEIGHT, halo, width);

                if (diffs > 0)
                {
                    Debug.LogError($"[FAIL] Padded Fluid Gather == Per-Cell Oracle — {diffs.ToString()} mismatch(es)" +
                                   (diffs > MAX_PADDED_DIFF_LINES ? $" ({MAX_PADDED_DIFF_LINES.ToString()} shown)." : "."));
                    return false;
                }

                // The compare above IS the bandCount pin: it walks TEST_BAND_HEIGHT rows starting at
                // bandMinY = 30. Had the wrapper treated its last argument as an exclusive global top, the
                // internal count would have been 8 − 30 = −22, no row would have been written at all, and
                // row 0 would have failed. Nothing further to assert here.
            }
            finally
            {
                if (padded.IsCreated) padded.Dispose();
                DisposeChunks(chunks);
            }

            Debug.Log("[PASS] Padded Fluid Gather == Per-Cell Oracle (halo 4, bandCount)");
            return true;
        }
    }
}
