using System.Collections.Generic;
using Editor.Validation.Framework;
using Helpers;
using UnityEngine;

namespace Editor.Validation
{
    /// <summary>
    /// <see cref="ChunkMathValidationSuite"/> — the NS-5 G2 flattened-index pins. Guards the
    /// <see cref="ChunkMath.GetFlattenedIndexInChunk"/> ↔ <see cref="ChunkMath.GetLocalPositionFromFlattenedIndex"/>
    /// pair as a true inverse, plus the <see cref="ChunkMath.GetFlattenedIndexInSection"/> stride layout and the
    /// two defensive clamps. Both directions are pinned because a matched packer/unpacker bug pair keeps a
    /// one-way round-trip green while corrupting every decoded position — the same blindness the
    /// <c>.RegionCodec.cs</c> pins were written to defeat, applied to the in-chunk layout.
    /// Read-only: no production behavior is asserted beyond what the helpers already document.
    /// </summary>
    public static partial class ChunkMathValidationSuite
    {
        // Section-local strides, derived from the documented layout (X fastest, then Y, then Z) rather than
        // from the implementation — a transposed stride in production must not be able to satisfy these.
        private const int SECTION_STRIDE_X = 1;
        private const int SECTION_STRIDE_Y = ChunkMath.CHUNK_WIDTH;                          // 16
        private const int SECTION_STRIDE_Z = ChunkMath.CHUNK_WIDTH * ChunkMath.SECTION_SIZE; // 256

        /// <summary>Maximum per-scenario mismatch lines logged before the diff is truncated.</summary>
        private const int MAX_INDEX_DIFF_LINES = 8;

        static partial void AddFlattenedIndexScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario("Flattened Index Inverse (exhaustive, both directions)", RunFlattenedIndexInverse));
            scenarios.Add(new Scenario("Flattened Index Asymmetric Pin (axis independence)", RunFlattenedIndexAsymmetricPin));
            scenarios.Add(new Scenario("Section Index Stride Pin (hand-derived)", RunSectionIndexStridePin));
            scenarios.Add(new Scenario("Flattened Index Clamp Contract (Y and index)", RunFlattenedIndexClampContract));
        }

        /// <summary>
        /// Independent oracle for the in-chunk packing, written from the documented layout (section-major,
        /// then Z, then Y, then X within the section) without calling the helpers under test.
        /// </summary>
        private static int RefFlattenedIndexInChunk(int x, int y, int z)
        {
            int sectionIdx = y / ChunkMath.SECTION_SIZE;
            int localY = y % ChunkMath.SECTION_SIZE;
            return sectionIdx * ChunkMath.SECTION_VOLUME
                   + x * SECTION_STRIDE_X
                   + localY * SECTION_STRIDE_Y
                   + z * SECTION_STRIDE_Z;
        }

        /// <summary>
        /// Exhaustive two-way inverse: every <c>(x, y, z)</c> in the chunk packs to an index that decodes back
        /// to the same triple, AND every index in <c>[0, CHUNK_VOLUME)</c> decodes to a triple that re-packs to
        /// the same index. The forward pass additionally cross-checks against
        /// <see cref="RefFlattenedIndexInChunk"/> and asserts the packing is a bijection onto the whole volume —
        /// so a packer that silently collides two cells (leaving part of the array unreachable) fails here
        /// rather than in a job that reads a stale voxel.
        /// </summary>
        private static bool RunFlattenedIndexInverse()
        {
            bool[] reached = new bool[ChunkMath.CHUNK_VOLUME];
            int diffs = 0;

            // Direction 1: (x, y, z) → index → (x, y, z).
            for (int y = 0; y < ChunkMath.CHUNK_HEIGHT; y++)
            {
                for (int z = 0; z < ChunkMath.CHUNK_WIDTH; z++)
                {
                    for (int x = 0; x < ChunkMath.CHUNK_WIDTH; x++)
                    {
                        int index = ChunkMath.GetFlattenedIndexInChunk(x, y, z);
                        int oracle = RefFlattenedIndexInChunk(x, y, z);

                        if (index != oracle)
                        {
                            if (++diffs <= MAX_INDEX_DIFF_LINES)
                                Debug.LogError($"[FAIL] Flattened Index Inverse — ({x},{y},{z}) packed to {index.ToString()}, " +
                                               $"layout oracle says {oracle.ToString()}.");
                            continue;
                        }

                        if ((uint)index >= (uint)ChunkMath.CHUNK_VOLUME)
                        {
                            if (++diffs <= MAX_INDEX_DIFF_LINES)
                                Debug.LogError($"[FAIL] Flattened Index Inverse — ({x},{y},{z}) packed to {index.ToString()}, " +
                                               $"outside [0,{ChunkMath.CHUNK_VOLUME.ToString()}).");
                            continue;
                        }

                        if (reached[index])
                        {
                            if (++diffs <= MAX_INDEX_DIFF_LINES)
                                Debug.LogError($"[FAIL] Flattened Index Inverse — index {index.ToString()} produced twice; " +
                                               $"({x},{y},{z}) collides with an earlier cell. The packing is not a bijection.");
                            continue;
                        }

                        reached[index] = true;

                        ChunkMath.GetLocalPositionFromFlattenedIndex(index, out int dx, out int dy, out int dz);
                        if (dx != x || dy != y || dz != z)
                        {
                            if (++diffs <= MAX_INDEX_DIFF_LINES)
                                Debug.LogError($"[FAIL] Flattened Index Inverse — ({x},{y},{z}) packed to {index.ToString()} " +
                                               $"but decoded to ({dx.ToString()},{dy.ToString()},{dz.ToString()}).");
                        }
                    }
                }
            }

            // Direction 2: index → (x, y, z) → index, over the whole volume.
            for (int index = 0; index < ChunkMath.CHUNK_VOLUME; index++)
            {
                ChunkMath.GetLocalPositionFromFlattenedIndex(index, out int x, out int y, out int z);

                if ((uint)x >= (uint)ChunkMath.CHUNK_WIDTH ||
                    (uint)y >= (uint)ChunkMath.CHUNK_HEIGHT ||
                    (uint)z >= (uint)ChunkMath.CHUNK_WIDTH)
                {
                    if (++diffs <= MAX_INDEX_DIFF_LINES)
                        Debug.LogError($"[FAIL] Flattened Index Inverse — index {index.ToString()} decoded to the " +
                                       $"out-of-chunk position ({x.ToString()},{y.ToString()},{z.ToString()}).");
                    continue;
                }

                int repacked = ChunkMath.GetFlattenedIndexInChunk(x, y, z);
                if (repacked != index)
                {
                    if (++diffs <= MAX_INDEX_DIFF_LINES)
                        Debug.LogError($"[FAIL] Flattened Index Inverse — index {index.ToString()} decoded to " +
                                       $"({x.ToString()},{y.ToString()},{z.ToString()}) but re-packed to {repacked.ToString()}.");
                }
            }

            // Coverage: every slot in the array must be addressable by some (x, y, z).
            for (int index = 0; index < ChunkMath.CHUNK_VOLUME; index++)
            {
                if (reached[index])
                    continue;

                if (++diffs <= MAX_INDEX_DIFF_LINES)
                    Debug.LogError($"[FAIL] Flattened Index Inverse — index {index.ToString()} is never produced by " +
                                   "any in-chunk position; part of the chunk array is unreachable.");
            }

            if (diffs > 0)
            {
                Debug.LogError($"[FAIL] Flattened Index Inverse (exhaustive, both directions) — {diffs.ToString()} mismatch(es)" +
                               (diffs > MAX_INDEX_DIFF_LINES ? $" ({MAX_INDEX_DIFF_LINES.ToString()} shown)." : "."));
                return false;
            }

            Debug.Log("[PASS] Flattened Index Inverse (exhaustive, both directions)");
            return true;
        }

        /// <summary>
        /// Hand-derived expected values with all three axes distinct and spanning several sections. The
        /// exhaustive sweep above is symmetric under an X↔Z transposition of BOTH the packer and the unpacker
        /// (they would still invert each other), so only fixed asymmetric values prove the axes carry the
        /// strides the layout documents.
        /// </summary>
        private static bool RunFlattenedIndexAsymmetricPin()
        {
            // (x, y, z, expected index) — index = (y/16)*4096 + x + (y%16)*16 + z*256.
            (int x, int y, int z, int index)[] cases =
            {
                (0, 0, 0, 0),
                (15, 0, 0, 15),      // X is the fastest axis
                (0, 1, 0, 16),       // Y stride inside a section
                (0, 0, 1, 256),      // Z stride inside a section
                (0, 16, 0, 4096),    // first cell of section 1
                (3, 37, 11, 11091),  // asymmetric: section 2, localY 5
                (11, 37, 3, 9051),   // X/Z swapped — must NOT equal the case above
                (0, 127, 0, 28912),  // last section, last row
                (15, 127, 15, ChunkMath.CHUNK_VOLUME - 1),
            };

            foreach ((int x, int y, int z, int index) in cases)
            {
                int oracle = RefFlattenedIndexInChunk(x, y, z);
                if (oracle != index)
                {
                    Debug.LogError($"[FAIL] Flattened Index Asymmetric Pin — hand-derived table wrong at ({x},{y},{z}): " +
                                   $"table {index.ToString()} vs layout oracle {oracle.ToString()}. Fix the table.");
                    return false;
                }

                int actual = ChunkMath.GetFlattenedIndexInChunk(x, y, z);
                if (actual != index)
                {
                    Debug.LogError($"[FAIL] Flattened Index Asymmetric Pin — ({x},{y},{z}) expected index " +
                                   $"{index.ToString()}, got {actual.ToString()}.");
                    return false;
                }

                ChunkMath.GetLocalPositionFromFlattenedIndex(index, out int dx, out int dy, out int dz);
                if (dx != x || dy != y || dz != z)
                {
                    Debug.LogError($"[FAIL] Flattened Index Asymmetric Pin — index {index.ToString()} expected to decode " +
                                   $"to ({x},{y},{z}), got ({dx.ToString()},{dy.ToString()},{dz.ToString()}).");
                    return false;
                }
            }

            Debug.Log("[PASS] Flattened Index Asymmetric Pin (axis independence)");
            return true;
        }

        /// <summary>
        /// Pins <see cref="ChunkMath.GetFlattenedIndexInSection"/> to the documented section-local strides
        /// (X = 1, Y = 16, Z = 256) and to the section volume at its far corner, and asserts the whole-chunk
        /// packer is exactly the section packer plus a section offset — the coupling that lets a gather treat
        /// a fixed-(y, z) span of X as one contiguous run.
        /// </summary>
        private static bool RunSectionIndexStridePin()
        {
            (int x, int localY, int z, int index)[] cases =
            {
                (0, 0, 0, 0),
                (1, 0, 0, SECTION_STRIDE_X),
                (0, 1, 0, SECTION_STRIDE_Y),
                (0, 0, 1, SECTION_STRIDE_Z),
                (15, 15, 15, ChunkMath.SECTION_VOLUME - 1),
            };

            foreach ((int x, int localY, int z, int index) in cases)
            {
                int actual = ChunkMath.GetFlattenedIndexInSection(x, localY, z);
                if (actual != index)
                {
                    Debug.LogError($"[FAIL] Section Index Stride Pin — ({x},{localY},{z}) expected {index.ToString()}, " +
                                   $"got {actual.ToString()}.");
                    return false;
                }
            }

            // Whole-chunk packer == section offset + section-local packer, for every section.
            for (int section = 0; section < ChunkMath.SECTIONS_PER_CHUNK; section++)
            {
                for (int localY = 0; localY < ChunkMath.SECTION_SIZE; localY++)
                {
                    int y = section * ChunkMath.SECTION_SIZE + localY;
                    int expected = section * ChunkMath.SECTION_VOLUME + ChunkMath.GetFlattenedIndexInSection(7, localY, 5);
                    int actual = ChunkMath.GetFlattenedIndexInChunk(7, y, 5);
                    if (actual != expected)
                    {
                        Debug.LogError($"[FAIL] Section Index Stride Pin — chunk index at y={y.ToString()} is " +
                                       $"{actual.ToString()}, but section {section.ToString()} offset + section index is " +
                                       $"{expected.ToString()}.");
                        return false;
                    }
                }
            }

            Debug.Log("[PASS] Section Index Stride Pin (hand-derived)");
            return true;
        }

        /// <summary>
        /// Pins both defensive clamps as the deliberate contract they are documented to be: an out-of-range Y
        /// packs to the nearest valid row rather than throwing, and an out-of-range index decodes to an
        /// in-chunk position rather than one <c>Chunk.AddActiveVoxel</c> would register and later evaluate
        /// against a non-existent voxel. Pinned as-is — this scenario records the behavior, it does not
        /// endorse relying on it.
        /// </summary>
        private static bool RunFlattenedIndexClampContract()
        {
            // Y clamp on the packer: above the top and below the floor collapse onto the edge rows.
            int top = ChunkMath.GetFlattenedIndexInChunk(4, ChunkMath.CHUNK_HEIGHT - 1, 9);
            int aboveTop = ChunkMath.GetFlattenedIndexInChunk(4, ChunkMath.CHUNK_HEIGHT, 9);
            int bottom = ChunkMath.GetFlattenedIndexInChunk(4, 0, 9);
            int belowFloor = ChunkMath.GetFlattenedIndexInChunk(4, -1, 9);

            if (aboveTop != top)
            {
                Debug.LogError($"[FAIL] Flattened Index Clamp Contract — y={ChunkMath.CHUNK_HEIGHT.ToString()} packed to " +
                               $"{aboveTop.ToString()}, expected the y={(ChunkMath.CHUNK_HEIGHT - 1).ToString()} index " +
                               $"{top.ToString()}.");
                return false;
            }

            if (belowFloor != bottom)
            {
                Debug.LogError($"[FAIL] Flattened Index Clamp Contract — y=-1 packed to {belowFloor.ToString()}, " +
                               $"expected the y=0 index {bottom.ToString()}.");
                return false;
            }

            // Index clamp on the unpacker: out-of-range indices decode to the clamped edge cells.
            ChunkMath.GetLocalPositionFromFlattenedIndex(ChunkMath.CHUNK_VOLUME, out int hx, out int hy, out int hz);
            if (hx != ChunkMath.CHUNK_WIDTH - 1 || hy != ChunkMath.CHUNK_HEIGHT - 1 || hz != ChunkMath.CHUNK_WIDTH - 1)
            {
                Debug.LogError($"[FAIL] Flattened Index Clamp Contract — index {ChunkMath.CHUNK_VOLUME.ToString()} decoded to " +
                               $"({hx.ToString()},{hy.ToString()},{hz.ToString()}), expected the last in-chunk cell.");
                return false;
            }

            ChunkMath.GetLocalPositionFromFlattenedIndex(-1, out int lx, out int ly, out int lz);
            if (lx != 0 || ly != 0 || lz != 0)
            {
                Debug.LogError($"[FAIL] Flattened Index Clamp Contract — index -1 decoded to " +
                               $"({lx.ToString()},{ly.ToString()},{lz.ToString()}), expected (0,0,0).");
                return false;
            }

            Debug.Log("[PASS] Flattened Index Clamp Contract (Y and index)");
            return true;
        }
    }
}
