using System;
using UnityEngine;

namespace Helpers
{
    /// <summary>
    /// Bidirectional codec for converting between a chunk's identity and its
    /// physical address (region file + local slot) on disk.
    ///
    /// <para>
    /// The canonical intermediate representation is the <b>chunk index</b>
    /// (e.g. <c>ChunkCoord.X / ChunkCoord.Z</c>). The encoder accepts the
    /// chunk's voxel-space world origin (<c>ChunkData.position</c>) and
    /// converts it to a region address; the decoder does the reverse.
    /// </para>
    /// </summary>
    public interface IRegionAddressCodec
    {
        // ── Encoder ──────────────────────────────────────────────────────────

        /// <summary>
        /// Converts a chunk's voxel-space world origin into the region file
        /// coordinate and local slot where it should be stored on disk.
        /// </summary>
        /// <param name="chunkVoxelPos">
        /// The voxel-space world origin of the chunk — the value stored in
        /// <c>ChunkData.position</c> (e.g. chunk index 10 → voxelPos 160).
        /// </param>
        /// <returns>
        /// A tuple of:
        /// <list type="bullet">
        ///   <item><c>regionCoord</c> — the X/Z of the region file (e.g. <c>r.0.3.bin</c>)</item>
        ///   <item><c>localX</c> / <c>localZ</c> — the slot within that file (0–31)</item>
        /// </list>
        /// </returns>
        (Vector2Int regionCoord, int localX, int localZ) ChunkVoxelPosToRegionAddress(Vector2Int chunkVoxelPos);

        // ── Decoder ──────────────────────────────────────────────────────────

        /// <summary>
        /// Converts a region file address back into a chunk index.
        /// </summary>
        /// <param name="regionX">Region X parsed from the filename (e.g. 5 from r.5.3.bin).</param>
        /// <param name="regionZ">Region Z parsed from the filename (e.g. 3 from r.5.3.bin).</param>
        /// <param name="localX">Local slot X within the region file (0–31).</param>
        /// <param name="localZ">Local slot Z within the region file (0–31).</param>
        /// <returns>
        /// A <see cref="Vector2Int"/> where X and Y are chunk indices on the X and Z
        /// axes respectively — the values used as dictionary keys in
        /// <c>WorldData.Chunks</c> and as <c>ChunkCoord.X / ChunkCoord.Z</c>.
        /// </returns>
        Vector2Int RegionSlotToChunkIndex(int regionX, int regionZ, int localX, int localZ);
    }

    /// <summary>
    /// Factory that returns the correct <see cref="IRegionAddressCodec"/> for a
    /// given save version. All implementations are stateless and thread-safe.
    /// </summary>
    public static class RegionAddressCodec
    {
        private const int CHUNKS_PER_REGION_SIDE = 32;

        /// <summary>
        /// Returns the codec for the given save version.
        /// </summary>
        /// <param name="saveVersion">The version field read from <c>level.dat</c>.</param>
        /// <exception cref="NotSupportedException">
        /// Thrown for unrecognised versions (below 1).
        /// </exception>
        /// <param name="allowLegacyEncoder">
        /// When <c>false</c> (default), calling <c>ChunkVoxelPosToRegionAddress</c> on a
        /// legacy codec (currently V1) throws <see cref="InvalidOperationException"/>.
        /// Pass <c>true</c> only in migration steps, editor tooling, or unit tests that
        /// explicitly need to reproduce legacy on-disk layout. Even when <c>true</c>,
        /// every encoding call emits a <c>Debug.LogError</c> so it is always visible.
        /// <para><b>Normal game code must never pass <c>true</c> here.</b></para>
        /// </param>
        public static IRegionAddressCodec ForVersion(int saveVersion, bool allowLegacyEncoder = false)
        {
            return saveVersion switch
            {
                1 => new V1Codec(allowLegacyEncoder),
                // V2 introduced correct chunk-index addressing. All subsequent versions
                // use the same scheme unless a future migration step changes region layout,
                // at which point a new case (or range) should be added above this default.
                >= 2 => new V2Codec(),
                _ => throw new NotSupportedException(
                    $"[RegionAddressCodec] No codec registered for save version {saveVersion}. " +
                    $"Add a new IRegionAddressCodec implementation and register it in ForVersion().")
            };
        }

        // ── V1: Broken voxel-space addressing ────────────────────────────────
        //
        // V1 ENCODER (preserved — the original bug, documented exactly as it ran):
        //   regionX = floor(voxelX / 32)    ← wrong: voxelX is a world pos, not a chunk index
        //   localX  = voxelX % 32           ← only ever 0 or 16 (multiples of ChunkWidth)
        //
        // Gated behind _allowLegacyEncoder (set by ForVersion's allowLegacyEncoder flag):
        //   false (default) → throws InvalidOperationException (safe for production code)
        //   true            → logs Debug.LogError and proceeds (for tooling / migration tests)
        //
        // V1 DECODER:
        //   To recover the chunk index from a V1 address:
        //     voxelX  = regionX * 32 + localX   (undo the broken encoder)
        //     chunkX  = voxelX / V1_CHUNK_WIDTH  (convert to chunk index)
        //
        // V1_CHUNK_WIDTH is hardcoded — never reference VoxelData from a historical
        // codec so that a future change to VoxelData.ChunkWidth cannot corrupt
        // the reading of old saves.
        private sealed class V1Codec : IRegionAddressCodec
        {
            private const int V1_CHUNK_WIDTH = 16;
            private readonly bool _allowLegacyEncoder;

            public V1Codec(bool allowLegacyEncoder)
            {
                _allowLegacyEncoder = allowLegacyEncoder;
            }

            public (Vector2Int regionCoord, int localX, int localZ) ChunkVoxelPosToRegionAddress(Vector2Int chunkVoxelPos)
            {
                if (!_allowLegacyEncoder)
                    throw new InvalidOperationException(
                        "[RegionAddressCodec.V1] Encoding to V1 region format is not permitted in normal use. "
                        + "V1 addressing was a bug. Use a V2+ codec for writing region files. "
                        + "If you explicitly need V1 encoding (e.g. for migration tooling), "
                        + "pass allowLegacyEncoder: true to RegionAddressCodec.ForVersion().");

                // Legacy encoder enabled — proceed with the original broken formula
                // and emit a persistent error so this usage is always visible in logs.
                Debug.LogError(
                    $"[RegionAddressCodec.V1] Legacy V1 encoder used for voxelPos {chunkVoxelPos}. "
                    + "This produces the historically broken region layout and must only be used "
                    + "in migration tooling or tests. Never call this from production save code.");

                // Original V1 formula (the bug, preserved verbatim):
                //   regionX = floor(voxelX / 32)  — treated voxel pos as chunk index
                //   localX  = voxelX % 32         — only ever 0 or 16
                int regionX = Mathf.FloorToInt(chunkVoxelPos.x / (float)CHUNKS_PER_REGION_SIDE);
                int regionZ = Mathf.FloorToInt(chunkVoxelPos.y / (float)CHUNKS_PER_REGION_SIDE);
                int lx = chunkVoxelPos.x % CHUNKS_PER_REGION_SIDE;
                int lz = chunkVoxelPos.y % CHUNKS_PER_REGION_SIDE;

                return (new Vector2Int(regionX, regionZ), lx, lz);
            }

            public Vector2Int RegionSlotToChunkIndex(int regionX, int regionZ, int localX, int localZ)
            {
                // Undo V1's broken encoder to recover the original voxel position,
                // then convert to a chunk index.
                int voxelX = regionX * CHUNKS_PER_REGION_SIDE + localX;
                int voxelZ = regionZ * CHUNKS_PER_REGION_SIDE + localZ;

                return new Vector2Int(
                    Mathf.FloorToInt((float)voxelX / V1_CHUNK_WIDTH),
                    Mathf.FloorToInt((float)voxelZ / V1_CHUNK_WIDTH)
                );
            }
        }

        // ── V2+: Correct chunk-index addressing ──────────────────────────────
        //
        // V2 ENCODER:
        //   chunkX    = voxelX / ChunkWidth          (convert voxel pos to chunk index)
        //   regionX   = floor(chunkX / 32)           (which region file)
        //   localX    = chunkX % 32                  (slot within the file, full 0–31 range)
        //
        // V2 DECODER (inverse):
        //   chunkX    = regionX * 32 + localX        (direct chunk index, no voxel conversion)
        private sealed class V2Codec : IRegionAddressCodec
        {
            public (Vector2Int regionCoord, int localX, int localZ) ChunkVoxelPosToRegionAddress(Vector2Int chunkVoxelPos)
            {
                // Step 1: voxel-space world origin → chunk index
                int chunkX = chunkVoxelPos.x / VoxelData.ChunkWidth;
                int chunkZ = chunkVoxelPos.y / VoxelData.ChunkWidth;

                // Step 2: chunk index → region coord
                int regionX = Mathf.FloorToInt(chunkX / 32f);
                int regionZ = Mathf.FloorToInt(chunkZ / 32f);

                // Step 3: chunk index → local slot (negative correction for future-proofing)
                int lx = chunkX % 32;
                int lz = chunkZ % 32;
                if (lx < 0) lx += 32;
                if (lz < 0) lz += 32;

                return (new Vector2Int(regionX, regionZ), lx, lz);
            }

            public Vector2Int RegionSlotToChunkIndex(int regionX, int regionZ, int localX, int localZ)
            {
                // In V2+ the slot IS the chunk index — no voxel conversion needed.
                return new Vector2Int(
                    regionX * CHUNKS_PER_REGION_SIDE + localX,
                    regionZ * CHUNKS_PER_REGION_SIDE + localZ
                );
            }
        }
    }
}
