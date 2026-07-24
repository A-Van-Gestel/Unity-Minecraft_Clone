using System;
using System.Collections.Generic;
using Editor.Validation.Framework;
using Helpers;
using UnityEngine;

namespace Editor.Validation
{
    /// <summary>
    /// <see cref="ChunkMathValidationSuite"/> — the NS-5 V1/V2 region-codec pins (CP-2 close-out).
    /// Pins <see cref="RegionAddressCodec"/> V2's address math to hand-derived expected values (not just
    /// round-trip identity — a matched encoder/decoder bug pair keeps round-trips green while corrupting
    /// every on-disk address), over positive, negative, and ±2³¹-adjacent domains, plus the decode-only
    /// V1 legacy contract (expected values, V1≠V2 divergence, encoder guard). Read-only: no format
    /// change is made or implied — the recorded no-V3-bump verdict stands (WORLD_SCALING_ANALYSIS §3.2).
    /// </summary>
    public static partial class ChunkMathValidationSuite
    {
        static partial void AddRegionCodecScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario("V2 Encoder Expected Address (hand-derived, both signs)", RunV2EncoderExpectedAddress));
            scenarios.Add(new Scenario("V2 Codec ±2³¹-Adjacent Domain (pin)", RunV2CodecIntEdgeDomain));
            scenarios.Add(new Scenario("V2 Decoder Inverse Property (two-way sweep)", RunV2DecoderInverseProperty));
            scenarios.Add(new Scenario("V2 Encoder Slot Range + Truncation Teeth", RunV2EncoderSlotRangeTeeth));
            scenarios.Add(new Scenario("V1 Decoder Legacy Pin + V1≠V2 Divergence", RunV1DecoderLegacyPin));
            scenarios.Add(new Scenario("V1 Encoder Guard + ForVersion Dispatch", RunV1EncoderGuardAndDispatch));
        }

        /// <summary>Independent per-axis oracle for the V2 encoder: double-precision floor, no shift/mask.</summary>
        private static (int region, int slot) RefV2EncodeAxis(int voxel)
        {
            int chunk = RefFloorDiv(voxel, ChunkMath.CHUNK_WIDTH);
            int region = RefFloorDiv(chunk, ChunkMath.CHUNKS_PER_REGION_SIDE);
            return (region, chunk - region * ChunkMath.CHUNKS_PER_REGION_SIDE);
        }

        /// <summary>
        /// Pins the V2 encoder to hand-derived <c>(voxel origin → region, slot)</c> values on both signs,
        /// each cross-checked against the independent floor oracle. Expected values are per-axis (X and Z
        /// share the formula); a mixed-sign pair proves the axes stay independent.
        /// </summary>
        private static bool RunV2EncoderExpectedAddress()
        {
            IRegionAddressCodec codec = RegionAddressCodec.ForVersion(2);

            // Per-axis: (voxel origin, expected region, expected slot) — chunk = v>>4, region = chunk>>5, slot = chunk&31.
            (int voxel, int region, int slot)[] cases =
            {
                (0, 0, 0), (16, 0, 1), (496, 0, 31), (512, 1, 0), (8192, 16, 0), (1_600_000, 3125, 0),
                (-16, -1, 31), (-512, -1, 0), (-528, -2, 31), (-8192, -16, 0), (-1_600_016, -3126, 31),
            };

            foreach ((int voxel, int region, int slot) in cases)
            {
                (int oracleRegion, int oracleSlot) = RefV2EncodeAxis(voxel);
                if (oracleRegion != region || oracleSlot != slot)
                {
                    Debug.LogError($"[FAIL] V2 Encoder Expected Address — hand-derived table wrong at v={voxel}: " +
                                   $"table ({region},{slot}) vs oracle ({oracleRegion},{oracleSlot}). Fix the table.");
                    return false;
                }

                (Vector2Int actualRegion, int lx, int lz) = codec.ChunkVoxelPosToRegionAddress(new Vector2Int(voxel, voxel));
                if (actualRegion.x != region || actualRegion.y != region || lx != slot || lz != slot)
                {
                    Debug.LogError($"[FAIL] V2 Encoder Expected Address (hand-derived, both signs) — v={voxel} " +
                                   $"expected region ({region},{region}) slot ({slot},{slot}), got {actualRegion} ({lx},{lz}).");
                    return false;
                }
            }

            // Mixed-sign pair: the axes must not leak into each other.
            (Vector2Int mixedRegion, int mlx, int mlz) = codec.ChunkVoxelPosToRegionAddress(new Vector2Int(16, -16));
            if (mixedRegion != new Vector2Int(0, -1) || mlx != 1 || mlz != 31)
            {
                Debug.LogError($"[FAIL] V2 Encoder Expected Address (hand-derived, both signs) — mixed (16,-16) " +
                               $"expected region (0,-1) slot (1,31), got {mixedRegion} ({mlx},{mlz}).");
                return false;
            }

            Debug.Log("[PASS] V2 Encoder Expected Address (hand-derived, both signs)");
            return true;
        }

        /// <summary>
        /// Pins the V2 codec at the ±2³¹ edges of the aligned-origin domain: the most-negative and
        /// most-positive chunk origins an <c>int</c> can hold (plus one chunk inward). Expected values are
        /// hand-derived; the decoder inverse and the <c>×16</c> voxel reconstruction are exact at the edge
        /// (no overflow: <c>−134217728·16 = int.MinValue</c>).
        /// </summary>
        private static bool RunV2CodecIntEdgeDomain()
        {
            IRegionAddressCodec codec = RegionAddressCodec.ForVersion(2);

            // (voxel origin, expected region, expected slot, expected chunk index) per axis.
            (int voxel, int region, int slot, int chunk)[] cases =
            {
                (int.MinValue, -4_194_304, 0, -134_217_728), // −2³¹, an exact chunk (and region) origin
                (int.MinValue + 16, -4_194_304, 1, -134_217_727), // one chunk inward from the edge
                (2_147_483_632, 4_194_303, 31, 134_217_727), // 0x7FFFFFF0 — the last aligned origin below +2³¹
                (2_147_483_616, 4_194_303, 30, 134_217_726), // one chunk inward
            };

            foreach ((int voxel, int region, int slot, int chunk) in cases)
            {
                (Vector2Int actualRegion, int lx, int lz) = codec.ChunkVoxelPosToRegionAddress(new Vector2Int(voxel, voxel));
                if (actualRegion.x != region || actualRegion.y != region || lx != slot || lz != slot)
                {
                    Debug.LogError($"[FAIL] V2 Codec ±2³¹-Adjacent Domain — v={voxel} expected region {region} " +
                                   $"slot {slot}, got {actualRegion} ({lx},{lz}).");
                    return false;
                }

                Vector2Int chunkIndex = codec.RegionSlotToChunkIndex(actualRegion.x, actualRegion.y, lx, lz);
                if (chunkIndex.x != chunk || chunkIndex.y != chunk || chunkIndex.x * ChunkMath.CHUNK_WIDTH != voxel)
                {
                    Debug.LogError($"[FAIL] V2 Codec ±2³¹-Adjacent Domain — v={voxel} decoder gave chunk {chunkIndex} " +
                                   $"(expected {chunk}); ×16 reconstruction {chunkIndex.x * ChunkMath.CHUNK_WIDTH}.");
                    return false;
                }
            }

            Debug.Log("[PASS] V2 Codec ±2³¹-Adjacent Domain (pin)");
            return true;
        }

        /// <summary>
        /// Two-way inverse sweep: encoder∘decoder is identity over a dense chunk-index sweep straddling the
        /// origin, AND decoder∘encoder is identity over every <c>(region, slot)</c> tuple of four regions —
        /// the direction the pre-existing round-trip pins never exercised (a decoder that mangles slots
        /// consistently with the encoder would round-trip green). Ends with an <b>asymmetric</b>
        /// expected-value pin so decoder axis-independence is proven directly (the sweeps above are
        /// axis-symmetric; an X/Z swap in the decoder would pass every diagonal input).
        /// </summary>
        private static bool RunV2DecoderInverseProperty()
        {
            IRegionAddressCodec codec = RegionAddressCodec.ForVersion(2);

            // Direction 1: chunk index → encode → decode → same chunk index (dense, both signs).
            for (int c = CHUNK_SWEEP_MIN; c <= CHUNK_SWEEP_MAX; c++)
            {
                Vector2Int voxelOrigin = new Vector2Int(c * ChunkMath.CHUNK_WIDTH, c * ChunkMath.CHUNK_WIDTH);
                (Vector2Int region, int lx, int lz) = codec.ChunkVoxelPosToRegionAddress(voxelOrigin);
                Vector2Int chunkIndex = codec.RegionSlotToChunkIndex(region.x, region.y, lx, lz);
                if (chunkIndex.x != c || chunkIndex.y != c)
                {
                    Debug.LogError($"[FAIL] V2 Decoder Inverse Property — chunk {c} encoded to region {region} " +
                                   $"slot ({lx},{lz}) but decoded to {chunkIndex}.");
                    return false;
                }
            }

            // Direction 2: (region, slot) → decode → encode → same (region, slot), every slot of four regions.
            foreach (int r in new[] { -2, -1, 0, 1 })
            {
                for (int s = 0; s < ChunkMath.CHUNKS_PER_REGION_SIDE; s++)
                {
                    Vector2Int chunkIndex = codec.RegionSlotToChunkIndex(r, r, s, s);
                    Vector2Int voxelOrigin = chunkIndex * ChunkMath.CHUNK_WIDTH;
                    (Vector2Int region, int lx, int lz) = codec.ChunkVoxelPosToRegionAddress(voxelOrigin);
                    if (region.x != r || region.y != r || lx != s || lz != s)
                    {
                        Debug.LogError($"[FAIL] V2 Decoder Inverse Property — region {r} slot {s} decoded to chunk " +
                                       $"{chunkIndex} but re-encoded to region {region} slot ({lx},{lz}).");
                        return false;
                    }
                }
            }

            // Asymmetric expected-value pin: region (3,−2) slot (16,5) → chunk (3·32+16, −2·32+5) = (112,−59),
            // and its voxel origin (1792,−944) must re-encode to exactly that address. All decoder inputs above
            // are diagonal, so only this case fails if the decoder ever swaps its X/Z derivations.
            Vector2Int asymChunk = codec.RegionSlotToChunkIndex(3, -2, 16, 5);
            (Vector2Int asymRegion, int asymLx, int asymLz) =
                codec.ChunkVoxelPosToRegionAddress(asymChunk * ChunkMath.CHUNK_WIDTH);
            if (asymChunk != new Vector2Int(112, -59) ||
                asymRegion != new Vector2Int(3, -2) || asymLx != 16 || asymLz != 5)
            {
                Debug.LogError($"[FAIL] V2 Decoder Inverse Property — asymmetric pin: region (3,-2) slot (16,5) " +
                               $"decoded to {asymChunk} (expected (112,-59)), re-encoded to {asymRegion} " +
                               $"({asymLx},{asymLz}) (expected (3,-2) (16,5)).");
                return false;
            }

            Debug.Log("[PASS] V2 Decoder Inverse Property (two-way sweep)");
            return true;
        }

        /// <summary>
        /// Teeth for the expected-value pins: on negative origins whose chunk index is not a region multiple,
        /// the slot must be in <c>[0, CHUNKS_PER_REGION_SIDE)</c> AND the <c>(region, slot)</c> pair must
        /// differ from the truncating <c>/</c>+<c>%</c> pair — so these pins cannot be green if the codec
        /// silently reverted to truncation (the matched-pair regression round-trip identity cannot see).
        /// </summary>
        private static bool RunV2EncoderSlotRangeTeeth()
        {
            IRegionAddressCodec codec = RegionAddressCodec.ForVersion(2);

            // Negative chunk indices that are NOT multiples of 32 — where floor and truncation diverge.
            int[] voxelOrigins = { -16, -528, -1_600_016 };

            foreach (int voxel in voxelOrigins)
            {
                (Vector2Int region, int lx, int _) = codec.ChunkVoxelPosToRegionAddress(new Vector2Int(voxel, voxel));
                if (lx < 0 || lx >= ChunkMath.CHUNKS_PER_REGION_SIDE)
                {
                    Debug.LogError($"[FAIL] V2 Encoder Slot Range + Truncation Teeth — v={voxel} slot {lx} " +
                                   $"out of [0,{ChunkMath.CHUNKS_PER_REGION_SIDE}).");
                    return false;
                }

                int chunk = RefFloorDiv(voxel, ChunkMath.CHUNK_WIDTH);
                int truncRegion = chunk / ChunkMath.CHUNKS_PER_REGION_SIDE;
                int truncSlot = chunk % ChunkMath.CHUNKS_PER_REGION_SIDE;
                if (region.x == truncRegion && lx == truncSlot)
                {
                    Debug.LogError($"[FAIL] V2 Encoder Slot Range + Truncation Teeth — v={voxel} matched the " +
                                   $"truncating pair ({truncRegion},{truncSlot}); the pin has no teeth here.");
                    return false;
                }
            }

            Debug.Log("[PASS] V2 Encoder Slot Range + Truncation Teeth");
            return true;
        }

        /// <summary>
        /// Pins the V1 decoder's historical formula (undo the broken voxel-as-chunk-index encoder, then
        /// float-floor to a chunk index) to expected values, and asserts V1 ≠ V2 on the same address tuples
        /// — the divergence that makes the v1→v2 region-repack migration's reads meaningful.
        /// Positive domain only: V1 worlds predate negative coordinates, and V1 is decode-only legacy.
        /// </summary>
        private static bool RunV1DecoderLegacyPin()
        {
            IRegionAddressCodec v1 = RegionAddressCodec.ForVersion(1);
            IRegionAddressCodec v2 = RegionAddressCodec.ForVersion(2);

            // (region, slotX, slotZ, expected V1 chunk index) — voxel = r*32 + slot, chunk = floor(voxel/16).
            (int rx, int rz, int sx, int sz, int cx, int cz, bool divergesFromV2)[] cases =
            {
                (0, 0, 0, 0, 0, 0, false), // origin: V1 and V2 agree by coincidence
                (0, 0, 16, 0, 1, 0, true), // voxel 16 → chunk 1 (V2 would say chunk 16)
                (0, 0, 0, 16, 0, 1, true),
                (1, 0, 0, 0, 2, 0, true), // voxel 32 → chunk 2 (V2: chunk 32)
                (3, 2, 16, 0, 7, 4, true), // voxel (112, 64) → chunk (7, 4) (V2: (112, 64))
            };

            foreach ((int rx, int rz, int sx, int sz, int cx, int cz, bool diverges) in cases)
            {
                Vector2Int v1Chunk = v1.RegionSlotToChunkIndex(rx, rz, sx, sz);
                if (v1Chunk.x != cx || v1Chunk.y != cz)
                {
                    Debug.LogError($"[FAIL] V1 Decoder Legacy Pin — region ({rx},{rz}) slot ({sx},{sz}) " +
                                   $"expected chunk ({cx},{cz}), got {v1Chunk}.");
                    return false;
                }

                Vector2Int v2Chunk = v2.RegionSlotToChunkIndex(rx, rz, sx, sz);
                if (diverges == (v1Chunk == v2Chunk))
                {
                    Debug.LogError($"[FAIL] V1 Decoder Legacy Pin — region ({rx},{rz}) slot ({sx},{sz}) " +
                                   $"divergence expectation {diverges} violated: V1 {v1Chunk}, V2 {v2Chunk}.");
                    return false;
                }
            }

            Debug.Log("[PASS] V1 Decoder Legacy Pin + V1≠V2 Divergence");
            return true;
        }

        /// <summary>
        /// Pins the factory contract: the V1 encoder throws without <c>allowLegacyEncoder</c> (decode-only
        /// legacy — normal code can never write V1 layout), versions below 1 are rejected, and every
        /// version ≥ 2 (including the current save version) dispatches to the same V2 addressing. The
        /// <c>allowLegacyEncoder: true</c> arm is deliberately not exercised — it emits a
        /// <c>Debug.LogError</c> by design, which would pollute a green run.
        /// </summary>
        private static bool RunV1EncoderGuardAndDispatch()
        {
            // V1 encoder guard: must throw InvalidOperationException by default — and specifically the
            // legacy-encoder guard (message-bound via its stable "not permitted" token), so an unrelated
            // InvalidOperationException on the same path cannot satisfy this pin after the guard is deleted.
            try
            {
                RegionAddressCodec.ForVersion(1).ChunkVoxelPosToRegionAddress(new Vector2Int(16, 16));
                Debug.LogError("[FAIL] V1 Encoder Guard + ForVersion Dispatch — V1 encode did not throw; " +
                               "the decode-only legacy guard is gone.");
                return false;
            }
            catch (InvalidOperationException ex)
            {
                if (!ex.Message.Contains("not permitted"))
                {
                    Debug.LogError("[FAIL] V1 Encoder Guard + ForVersion Dispatch — V1 encode threw an " +
                                   $"InvalidOperationException that is NOT the legacy-encoder guard: {ex.Message}");
                    return false;
                }
            }

            // Unknown versions (< 1) must be rejected loudly.
            try
            {
                RegionAddressCodec.ForVersion(0);
                Debug.LogError("[FAIL] V1 Encoder Guard + ForVersion Dispatch — ForVersion(0) did not throw.");
                return false;
            }
            catch (NotSupportedException)
            {
                // Expected.
            }

            // Dispatch: every version ≥ 2 must share the V2 addressing scheme (the ForVersion switch's
            // documented contract), including the build's current save version.
            Vector2Int probe = new Vector2Int(-528, 8192);
            (Vector2Int v2Region, int v2Lx, int v2Lz) = RegionAddressCodec.ForVersion(2).ChunkVoxelPosToRegionAddress(probe);
            (Vector2Int curRegion, int curLx, int curLz) =
                RegionAddressCodec.ForVersion(SaveSystem.CURRENT_VERSION).ChunkVoxelPosToRegionAddress(probe);
            if (v2Region != curRegion || v2Lx != curLx || v2Lz != curLz)
            {
                Debug.LogError($"[FAIL] V1 Encoder Guard + ForVersion Dispatch — v2 and v{SaveSystem.CURRENT_VERSION} " +
                               $"codecs disagree at {probe}: ({v2Region},{v2Lx},{v2Lz}) vs ({curRegion},{curLx},{curLz}).");
                return false;
            }

            Debug.Log("[PASS] V1 Encoder Guard + ForVersion Dispatch");
            return true;
        }
    }
}
