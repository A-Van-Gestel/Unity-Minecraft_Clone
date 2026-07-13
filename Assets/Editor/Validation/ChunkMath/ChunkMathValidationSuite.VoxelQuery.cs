using System.Collections.Generic;
using Data;
using Editor.Validation.Framework;
using Helpers;
using UnityEngine;

namespace Editor.Validation.ChunkMath
{
    /// <summary>
    /// <see cref="ChunkMathValidationSuite"/> — VQ-1 integer voxel-query decomposition parity, the WS-2
    /// unbounded-+XZ bounds baseline, and the V2 region-codec identity pin.
    /// </summary>
    public static partial class ChunkMathValidationSuite
    {
        static partial void AddVoxelQueryScenarios(List<Scenario> scenarios)
        {
            // --- VQ-1 GetVoxelState float→int decomposition parity ---
            // The new integer TryGetVoxel fast path (and its GetVoxelState(Vector3) wrapper) must resolve the SAME
            // chunk, local voxel, and in-world verdict the old float path did — the floor semantics at fractional
            // and world-bound edges preserved exactly.
            scenarios.Add(new Scenario("VQ-1 Float↔Int Decomposition Parity (sweep)", RunVoxelQueryDecompositionParity));

            // --- WS-2 unbounded positive XZ ---
            // Bounds baseline: coordinates at/past the legacy 0–1600 border must read in-world (west/south floor
            // and the Y bound stay closed). Red on tip — the parity sweep above only covers −32…+512 and never
            // reaches the bound. Codec pin: the V2 round-trip stays byte-identical for chunk origins past the old
            // 0–99 border (WS-2 makes no format change), holding the "no V3 bump" claim honest — green before and after.
            scenarios.Add(new Scenario("WS-2 Unbounded +XZ Bounds (past-border in-world)", RunUnboundedBoundsBaseline));
            scenarios.Add(new Scenario("V2 Region Codec Identity Past Legacy Border (pin)", RunRegionCodecIdentityPin));
        }

        /// <summary>
        /// Proves the VQ-1 integer decomposition (<c>Mathf.FloorToInt</c> + <see cref="ChunkMath.VoxelToChunk"/>/
        /// <see cref="ChunkMath.VoxelToLocal"/>, as <c>WorldData.TryGetVoxel</c> and its <c>GetVoxelState(Vector3)</c>
        /// wrapper use it) yields the SAME in-world verdict, chunk voxel-origin, and local voxel position as the old
        /// float path (<see cref="WorldData.IsVoxelInWorld"/> + <see cref="WorldData.GetChunkCoordFor"/> +
        /// <see cref="WorldData.GetLocalVoxelPositionInChunk"/>). Sweeps quarter-voxel steps straddling the origin
        /// (negative fractions that must resolve out-of-world) and across several chunk boundaries, plus explicit
        /// world/height-bound edge tuples where a float compare and an int compare on the floored value could disagree.
        /// The WorldData math methods are pure (no <c>World.Instance</c>), so a bare instance drives them.
        /// </summary>
        private static bool RunVoxelQueryDecompositionParity()
        {
            WorldData wd = new WorldData("parity-test", 0);

            // Diagonal sweep: X and Z share identical logic, so one coordinate applied to both axes covers both.
            // -32 → +512 spans negative fractions (out-of-world), the origin, and many chunk boundaries; Y held at a
            // valid interior value so an in-world verdict actually exercises the chunk/local comparison.
            for (int q = -32 * 4; q <= 512 * 4; q++)
            {
                float w = q * 0.25f;
                if (!CheckDecompositionParity(wd, w, 64.5f, w))
                    return false;
            }

            // Edge tuples: the legacy XZ border (now past-border but in-world under WS-2), chunk height bound, and
            // negative fractions on each axis — the cases where `IsVoxelInWorld`'s float floor test and the int
            // floor test must still agree (XZ `>= 0`, Y still bounded above).
            const float worldSize = VoxelData.WorldSizeInVoxels;
            (float x, float y, float z)[] edges =
            {
                (0f, 0f, 0f), (-0.5f, 64f, 0f), (0f, 64f, -0.5f), (0f, -0.5f, 0f),
                (worldSize - 0.5f, 64f, 0f), (worldSize, 64f, 0f), (worldSize + 0.5f, 64f, 0f),
                (0f, VoxelData.ChunkHeight - 0.5f, 0f), (0f, VoxelData.ChunkHeight, 0f),
                (15.75f, 127.5f, 15.75f), (16f, 0f, 16f),
            };
            foreach ((float x, float y, float z) in edges)
            {
                if (!CheckDecompositionParity(wd, x, y, z))
                    return false;
            }

            Debug.Log("[PASS] VQ-1 Float↔Int Decomposition Parity (sweep)");
            return true;
        }

        /// <summary>
        /// Single-position parity check for <see cref="RunVoxelQueryDecompositionParity"/>: compares the old float
        /// decomposition against the integer one for one world position. Returns false (and logs) on any mismatch.
        /// </summary>
        private static bool CheckDecompositionParity(WorldData wd, float wx, float wy, float wz)
        {
            Vector3 wp = new Vector3(wx, wy, wz);

            bool oldInWorld = wd.IsVoxelInWorld(wp);
            int fx = Mathf.FloorToInt(wx), fy = Mathf.FloorToInt(wy), fz = Mathf.FloorToInt(wz);
            // Mirrors the relaxed production bounds (WS-2): XZ unbounded on the positive side (`>= 0` floor only),
            // Y still folded/bounded above by ChunkHeight.
            bool newInWorld = fx >= 0 &&
                              (uint)fy < VoxelData.ChunkHeight &&
                              fz >= 0;

            if (oldInWorld != newInWorld)
            {
                Debug.LogError($"[FAIL] VQ-1 Float↔Int Decomposition Parity — in-world verdict differs at {wp}: " +
                               $"float={oldInWorld}, int={newInWorld}.");
                return false;
            }

            // Chunk/local only defined when in-world (both agree it is, per the check above).
            if (!oldInWorld) return true;

            Vector2Int oldChunk = wd.GetChunkCoordFor(wp);
            Vector3Int oldLocal = wd.GetLocalVoxelPositionInChunk(wp);
            Vector2Int newChunk = new Vector2Int(
                Helpers.ChunkMath.VoxelToChunk(fx) * VoxelData.ChunkWidth,
                Helpers.ChunkMath.VoxelToChunk(fz) * VoxelData.ChunkWidth);
            Vector3Int newLocal = new Vector3Int(Helpers.ChunkMath.VoxelToLocal(fx), fy, Helpers.ChunkMath.VoxelToLocal(fz));

            if (oldChunk != newChunk || oldLocal != newLocal)
            {
                Debug.LogError($"[FAIL] VQ-1 Float↔Int Decomposition Parity — decomposition differs at {wp}: " +
                               $"chunk float={oldChunk}/int={newChunk}, local float={oldLocal}/int={newLocal}.");
                return false;
            }

            return true;
        }

        /// <summary>
        /// WS-2 prove-red: the XZ world border is unbounded on the positive side. Coordinates at and past the
        /// legacy 0–<see cref="VoxelData.WorldSizeInVoxels"/> edge must read in-world through
        /// <see cref="WorldData.IsVoxelInWorld"/>, while the west/south floor (negatives) and the Y bound stay
        /// closed. Also gives the VQ-1 <see cref="WorldData.TryGetVoxel"/> fold-split teeth: a loaded chunk past
        /// the old border must resolve (the pre-relaxation folded unsigned compare rejects it at the gate).
        /// </summary>
        private static bool RunUnboundedBoundsBaseline()
        {
            WorldData wd = new WorldData("ws2-bounds", 0);
            const float y = 64f; // valid interior height

            (float x, float y, float z, bool expected, string label)[] voxelCases =
            {
                (1599.75f, y, 0f, true, "just inside old east border"),
                (1600f, y, 0f, true, "on old east border (was out)"),
                (1600.25f, y, 0f, true, "just past old east border (was out)"),
                (0f, y, 1600f, true, "on old north border (was out)"),
                (1_000_000f, y, 0f, true, "far past old border X (was out)"),
                (0f, y, 1_000_000f, true, "far past old border Z (was out)"),
                (-0.5f, y, 0f, false, "west of origin stays out"),
                (0f, y, -0.5f, false, "south of origin stays out"),
                (1600f, -1f, 0f, false, "past border but below world stays out"),
                (1600f, VoxelData.ChunkHeight, 0f, false, "past border but above world stays out"),
            };

            foreach ((float x, float yy, float z, bool expected, string label) in voxelCases)
            {
                bool actual = wd.IsVoxelInWorld(new Vector3(x, yy, z));
                if (actual != expected)
                {
                    Debug.LogError($"[FAIL] WS-2 Unbounded +XZ Bounds — IsVoxelInWorld({x},{yy},{z}) [{label}] " +
                                   $"expected {expected}, got {actual}.");
                    return false;
                }
            }

            // Fold-split teeth: seed a chunk at the past-border origin (chunk index (100,0)); TryGetVoxel must pass
            // the bounds gate and hit the dictionary, while the west floor stays closed for negatives.
            Vector2Int borderChunkKey = new Vector2Int(1600, 0);
            wd.Chunks[borderChunkKey] = new ChunkData(borderChunkKey);

            if (!wd.TryGetVoxel(1600, 64, 0, out _))
            {
                Debug.LogError("[FAIL] WS-2 Unbounded +XZ Bounds — TryGetVoxel(1600,64,0) on a loaded past-border " +
                               "chunk returned false; the bounds gate rejected an in-world coordinate.");
                return false;
            }

            if (wd.TryGetVoxel(-1, 64, 0, out _))
            {
                Debug.LogError("[FAIL] WS-2 Unbounded +XZ Bounds — TryGetVoxel(-1,64,0) returned true; the west " +
                               "floor must stay closed.");
                return false;
            }

            Debug.Log("[PASS] WS-2 Unbounded +XZ Bounds (past-border in-world)");
            return true;
        }

        /// <summary>
        /// Pin (not a prove-red): the V2 region codec round-trips chunk origins past the legacy 0–99 chunk border
        /// byte-identically. WS-2 makes no on-disk format change, so this must be green before AND after the bounds
        /// relaxation — it holds the "no V3 codec bump / old saves keep working" claim honest. Coordinates stay
        /// ≤ 100_000 chunks so <c>×16</c> voxel-origin conversion never wraps <c>int</c>.
        /// </summary>
        private static bool RunRegionCodecIdentityPin()
        {
            IRegionAddressCodec codec = RegionAddressCodec.ForVersion(2);

            int[] chunkIndices = { 100, 1000, 100_000 };
            foreach (int cx in chunkIndices)
            {
                foreach (int cz in new[] { 0, cx })
                {
                    Vector2Int chunkVoxelPos = new Vector2Int(cx * VoxelData.ChunkWidth, cz * VoxelData.ChunkWidth);
                    (Vector2Int region, int lx, int lz) = codec.ChunkVoxelPosToRegionAddress(chunkVoxelPos);
                    Vector2Int chunkIndex = codec.RegionSlotToChunkIndex(region.x, region.y, lx, lz);
                    Vector2Int roundTrip = new Vector2Int(
                        chunkIndex.x * VoxelData.ChunkWidth, chunkIndex.y * VoxelData.ChunkWidth);
                    if (roundTrip != chunkVoxelPos)
                    {
                        Debug.LogError($"[FAIL] V2 Region Codec Identity — chunkVoxelPos {chunkVoxelPos} round-tripped " +
                                       $"to {roundTrip} (region {region}, slot {lx},{lz}).");
                        return false;
                    }
                }
            }

            Debug.Log("[PASS] V2 Region Codec Identity Past Legacy Border (pin)");
            return true;
        }
    }
}
