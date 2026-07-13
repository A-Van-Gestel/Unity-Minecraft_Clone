using System;
using System.Collections.Generic;
using Editor.Validation.Framework;
using Helpers;
using UnityEngine;

namespace Editor.Validation
{
    /// <summary>
    /// <see cref="ChunkMathValidationSuite"/> — WS-1 <see cref="ChunkMath"/> shift/mask equivalence sweep.
    /// Guards the centralized voxel↔chunk↔region helpers against the old float-roundtrip / truncating idioms:
    /// byte-identical for the reachable all-positive range, mathematically-correct floor into the negative
    /// quadrants (where truncating <c>/</c> was silently wrong — the Tier B prerequisite).
    /// </summary>
    public static partial class ChunkMathValidationSuite
    {
        // Sweep bounds: a representative range straddling the origin, well past a couple of region files in
        // both directions, and past chunk/region boundaries. Voxel coords are the widest scale.
        private const int VOXEL_SWEEP_MIN = -2048;
        private const int VOXEL_SWEEP_MAX = 2048;
        private const int CHUNK_SWEEP_MIN = -128;
        private const int CHUNK_SWEEP_MAX = 128;

        static partial void AddShiftMaskScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario("ChunkMath Power-Of-Two Guard", RunChunkMathPow2Guard));
            scenarios.Add(new Scenario("VoxelToChunk == Floor Div (sweep)", RunVoxelToChunkSweep));
            scenarios.Add(new Scenario("VoxelToChunk == Legacy FloorToInt (positives)", RunVoxelToChunkLegacyParity));
            scenarios.Add(new Scenario("VoxelToLocal Reconstructs Voxel (sweep)", RunVoxelToLocalSweep));
            scenarios.Add(new Scenario("ChunkToRegion / RegionLocal Reconstructs Chunk (sweep)", RunRegionSweep));
            scenarios.Add(new Scenario("WorldToChunk == Floor Div (float sweep)", RunWorldToChunkSweep));
            scenarios.Add(new Scenario("Negative-Coordinate Teeth (truncation would fail)", RunNegativeTeeth));
        }

        /// <summary>Reference floor-division in double precision, independent of the shift/mask implementation.</summary>
        private static int RefFloorDiv(int value, int divisor) => (int)Math.Floor(value / (double)divisor);

        /// <summary>
        /// The shift/mask amounts are only correct because <see cref="ChunkMath.CHUNK_WIDTH"/> and the region
        /// side are powers of two. This guard fails loudly if either constant is ever changed to a non-pow2
        /// value, instead of letting <c>&gt;&gt;</c>/<c>&amp;</c> silently corrupt addressing.
        /// </summary>
        private static bool RunChunkMathPow2Guard()
        {
            bool widthOk = (ChunkMath.CHUNK_WIDTH & (ChunkMath.CHUNK_WIDTH - 1)) == 0 &&
                           ChunkMath.VoxelToChunk(ChunkMath.CHUNK_WIDTH) == 1 &&
                           ChunkMath.VoxelToLocal(ChunkMath.CHUNK_WIDTH - 1) == ChunkMath.CHUNK_WIDTH - 1;
            bool regionOk = (ChunkMath.CHUNKS_PER_REGION_SIDE & (ChunkMath.CHUNKS_PER_REGION_SIDE - 1)) == 0 &&
                            ChunkMath.ChunkToRegion(ChunkMath.CHUNKS_PER_REGION_SIDE) == 1 &&
                            ChunkMath.ChunkToRegionLocal(ChunkMath.CHUNKS_PER_REGION_SIDE - 1) == ChunkMath.CHUNKS_PER_REGION_SIDE - 1;
            if (widthOk && regionOk)
            {
                Debug.Log("[PASS] ChunkMath Power-Of-Two Guard");
                return true;
            }

            Debug.LogError($"[FAIL] ChunkMath Power-Of-Two Guard — CHUNK_WIDTH={ChunkMath.CHUNK_WIDTH} " +
                           $"CHUNKS_PER_REGION_SIDE={ChunkMath.CHUNKS_PER_REGION_SIDE} (both must be powers of two).");
            return false;
        }

        /// <summary><see cref="ChunkMath.VoxelToChunk"/> must equal the reference floor-division across the sweep, both signs.</summary>
        private static bool RunVoxelToChunkSweep()
        {
            for (int v = VOXEL_SWEEP_MIN; v <= VOXEL_SWEEP_MAX; v++)
            {
                int expected = RefFloorDiv(v, ChunkMath.CHUNK_WIDTH);
                int actual = ChunkMath.VoxelToChunk(v);
                if (actual != expected)
                {
                    Debug.LogError($"[FAIL] VoxelToChunk == Floor Div (sweep) — v={v} expected {expected}, got {actual}.");
                    return false;
                }
            }

            Debug.Log("[PASS] VoxelToChunk == Floor Div (sweep)");
            return true;
        }

        /// <summary>
        /// Non-regression: for the reachable all-positive range, the new helper must be byte-identical to the
        /// old <c>Mathf.FloorToInt((float)v / ChunkWidth)</c> idiom it replaces at the migrated call sites.
        /// </summary>
        private static bool RunVoxelToChunkLegacyParity()
        {
            for (int v = 0; v <= VOXEL_SWEEP_MAX; v++)
            {
                int legacy = Mathf.FloorToInt((float)v / VoxelData.ChunkWidth);
                int actual = ChunkMath.VoxelToChunk(v);
                if (actual != legacy)
                {
                    Debug.LogError($"[FAIL] VoxelToChunk == Legacy FloorToInt (positives) — v={v} legacy {legacy}, got {actual}.");
                    return false;
                }
            }

            Debug.Log("[PASS] VoxelToChunk == Legacy FloorToInt (positives)");
            return true;
        }

        /// <summary><see cref="ChunkMath.VoxelToLocal"/> must be in <c>[0, CHUNK_WIDTH)</c> and reconstruct the voxel with its chunk index.</summary>
        private static bool RunVoxelToLocalSweep()
        {
            for (int v = VOXEL_SWEEP_MIN; v <= VOXEL_SWEEP_MAX; v++)
            {
                int local = ChunkMath.VoxelToLocal(v);
                if (local < 0 || local >= ChunkMath.CHUNK_WIDTH)
                {
                    Debug.LogError($"[FAIL] VoxelToLocal Reconstructs Voxel (sweep) — v={v} local {local} out of [0,{ChunkMath.CHUNK_WIDTH}).");
                    return false;
                }

                int reconstructed = ChunkMath.VoxelToChunk(v) * ChunkMath.CHUNK_WIDTH + local;
                if (reconstructed != v)
                {
                    Debug.LogError($"[FAIL] VoxelToLocal Reconstructs Voxel (sweep) — v={v} reconstructed {reconstructed}.");
                    return false;
                }
            }

            Debug.Log("[PASS] VoxelToLocal Reconstructs Voxel (sweep)");
            return true;
        }

        /// <summary>
        /// <see cref="ChunkMath.ChunkToRegion"/> / <see cref="ChunkMath.ChunkToRegionLocal"/> must match the
        /// reference floor-division, keep the slot in <c>[0, CHUNKS_PER_REGION_SIDE)</c>, and reconstruct the chunk.
        /// </summary>
        private static bool RunRegionSweep()
        {
            for (int c = CHUNK_SWEEP_MIN; c <= CHUNK_SWEEP_MAX; c++)
            {
                int region = ChunkMath.ChunkToRegion(c);
                int slot = ChunkMath.ChunkToRegionLocal(c);
                int expectedRegion = RefFloorDiv(c, ChunkMath.CHUNKS_PER_REGION_SIDE);
                if (region != expectedRegion || slot < 0 || slot >= ChunkMath.CHUNKS_PER_REGION_SIDE ||
                    region * ChunkMath.CHUNKS_PER_REGION_SIDE + slot != c)
                {
                    Debug.LogError($"[FAIL] ChunkToRegion / RegionLocal Reconstructs Chunk (sweep) — c={c} " +
                                   $"region {region} (expected {expectedRegion}) slot {slot}.");
                    return false;
                }
            }

            Debug.Log("[PASS] ChunkToRegion / RegionLocal Reconstructs Chunk (sweep)");
            return true;
        }

        /// <summary>
        /// <see cref="ChunkMath.WorldToChunk"/> must equal the reference floor-division over fractional world
        /// coordinates (both signs), and match the legacy <c>Mathf.FloorToInt(world / ChunkWidth)</c> for positives.
        /// </summary>
        private static bool RunWorldToChunkSweep()
        {
            // Quarter-voxel steps exercise the fractional-boundary behavior the float floor must preserve.
            for (int q = VOXEL_SWEEP_MIN * 4; q <= VOXEL_SWEEP_MAX * 4; q++)
            {
                float world = q * 0.25f;
                int expected = (int)Math.Floor(world / ChunkMath.CHUNK_WIDTH);
                int actual = ChunkMath.WorldToChunk(world);
                if (actual != expected)
                {
                    Debug.LogError($"[FAIL] WorldToChunk == Floor Div (float sweep) — world={world} expected {expected}, got {actual}.");
                    return false;
                }

                if (world >= 0f)
                {
                    int legacy = Mathf.FloorToInt(world / VoxelData.ChunkWidth);
                    if (actual != legacy)
                    {
                        Debug.LogError($"[FAIL] WorldToChunk == Floor Div (float sweep) — world={world} legacy {legacy}, got {actual}.");
                        return false;
                    }
                }
            }

            Debug.Log("[PASS] WorldToChunk == Floor Div (float sweep)");
            return true;
        }

        /// <summary>
        /// Teeth: fixed negative cases where the old truncating <c>/</c>/<c>%</c> gave the WRONG answer. These
        /// assert the helpers changed something — the region-codec fix (commit 3) is only meaningful because
        /// <c>VoxelToChunk(-8) == -1</c> (truncation gave 0, silently overwriting the mirror chunk's slot).
        /// </summary>
        private static bool RunNegativeTeeth()
        {
            (int input, Func<int, int> fn, int expected, int truncated, string name)[] cases =
            {
                (-8, ChunkMath.VoxelToChunk, -1, -8 / VoxelData.ChunkWidth, "VoxelToChunk(-8)"),
                (-8, ChunkMath.VoxelToLocal, 8, -8 % VoxelData.ChunkWidth, "VoxelToLocal(-8)"),
                (-1, ChunkMath.ChunkToRegion, -1, -1 / ChunkMath.CHUNKS_PER_REGION_SIDE, "ChunkToRegion(-1)"),
                (-1, ChunkMath.ChunkToRegionLocal, 31, -1 % ChunkMath.CHUNKS_PER_REGION_SIDE, "ChunkToRegionLocal(-1)"),
            };

            foreach ((int input, Func<int, int> fn, int expected, int truncated, string name) in cases)
            {
                int actual = fn(input);
                if (actual != expected)
                {
                    Debug.LogError($"[FAIL] Negative-Coordinate Teeth — {name} expected {expected}, got {actual}.");
                    return false;
                }

                if (actual == truncated)
                {
                    Debug.LogError($"[FAIL] Negative-Coordinate Teeth — {name} matched the truncating result {truncated}; " +
                                   "the fix has no teeth (this case must differ from truncating `/`/`%`).");
                    return false;
                }
            }

            Debug.Log("[PASS] Negative-Coordinate Teeth (truncation would fail)");
            return true;
        }
    }
}
