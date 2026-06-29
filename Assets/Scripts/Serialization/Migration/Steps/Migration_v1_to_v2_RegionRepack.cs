using System;
using System.Collections.Generic;
using System.IO;
using Helpers;
using UnityEngine;

// ReSharper disable InconsistentNaming

namespace Serialization.Migration.Steps
{
    /// <summary>
    /// Migrates V1 worlds to V2 by repacking all region files from the broken
    /// voxel-space addressing scheme to the correct chunk-index-space scheme.
    ///
    /// <para><b>Root Cause (V1 bug):</b></para>
    /// <para>
    /// <c>ChunkData.position</c> stores the chunk's voxel-space world origin
    /// (e.g. chunk index 10 → voxelPos 160 with ChunkWidth=16).
    /// The V1 <c>GetRegionCoord</c> divided this voxel position by 32 instead of
    /// first dividing by <c>ChunkWidth</c>, producing a region index 16× too large.
    /// Because <c>voxelPos % 32</c> can only ever be 0 or 16, each region file
    /// used only 4 of its 1,024 available chunk slots.
    /// </para>
    ///
    /// <para><b>V1 (broken) formula:</b></para>
    /// <code>
    ///   regionX   = floor(voxelX / 32)          ← wrong: treats voxel pos as chunk index
    ///   localX    = voxelX % 32                  ← only ever 0 or 16
    /// </code>
    ///
    /// <para><b>V2 (correct) formula:</b></para>
    /// <code>
    ///   chunkX    = voxelX / ChunkWidth          ← convert to chunk index first
    ///   regionX   = floor(chunkX / 32)
    ///   localX    = chunkX % 32                  ← full 0–31 range
    /// </code>
    ///
    /// <para><b>Effect of this migration:</b></para>
    /// <para>
    /// A 100×100 chunk world that had ~1,371 region files (averaging 4 chunks each)
    /// will be compacted to ~4–16 region files (up to 1,024 chunks each), reducing
    /// the Region folder from ~54 MB down to ~15–25 MB and eliminating ~10 MB of
    /// wasted sector headers.
    /// </para>
    ///
    /// <para>
    /// The chunk binary payload is unchanged — only the addressing is corrected.
    /// All payloads are recompressed to the player's current <c>targetCompression</c>
    /// format as a free normalization pass.
    /// </para>
    /// </summary>
    public class MigrationV1ToV2RegionRepack : WorldMigrationStep
    {
        public override int SourceWorldVersion => 1;
        public override int TargetWorldVersion => 2;
        public override string Description => "Repacking region files (fixing coordinate scale)...";
        public override string ChangeSummary => "Fixes region file coordinates resulting in significantly smaller save sizes.";

        // Chunk binary payload is unchanged in V2 — only the region layout changes.
        public override byte? TargetChunkFormatVersion => null;

        // Signal to MigrationManager that the full region folder must be restructured.
        public override bool RequiresRegionLayoutMigration => true;

        // ── Constants baked to the V1 world format ────────────────────────────
        // Hardcoded rather than referencing VoxelData to guard against future
        // constant changes accidentally breaking the migration arithmetic.
        private const int V1_CHUNK_WIDTH = 16; // VoxelData.ChunkWidth at V1

        // ─────────────────────────────────────────────────────────────────────

        /// <inheritdoc/>
        public override int PerformRegionLayoutMigration(
            string oldRegionPath,
            string newRegionPath,
            CompressionAlgorithm targetCompression)
        {
            string[] oldFiles = Directory.GetFiles(oldRegionPath, "r.*.*.bin");

            if (oldFiles.Length == 0)
            {
                Debug.LogWarning("[Migration v1→v2] No region files found. Nothing to repack.");
                return 0;
            }

            Debug.Log($"[Migration v1→v2] Repacking {oldFiles.Length} V1 region files...");

            // V1 codec: decodes broken addresses back into chunk indices.
            // V2 codec: encodes chunk indices into correct new addresses.
            // Both are obtained from the same factory — no address math lives here.
            IRegionAddressCodec v1Codec = RegionAddressCodec.ForVersion(1);
            IRegionAddressCodec v2Codec = RegionAddressCodec.ForVersion(2);

            // Keep new region files open across the outer loop so that multiple
            // old region files that map to the same new region are written in a
            // single open/close cycle (e.g. r.10.0 and r.11.0 both → r.0.0.bin).
            Dictionary<(int rx, int rz), RegionFile> newRegions = new Dictionary<(int rx, int rz), RegionFile>();
            int totalChunksProcessed = 0;

            try
            {
                foreach (string oldFile in oldFiles)
                {
                    totalChunksProcessed += ProcessOldRegionFile(
                        oldFile, newRegionPath, targetCompression,
                        v1Codec, v2Codec, newRegions);
                }
            }
            finally
            {
                // Always flush and close every new region file, even if an exception occurred.
                foreach (RegionFile r in newRegions.Values)
                {
                    try
                    {
                        r.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[Migration v1→v2] Error closing a new region file: {ex.Message}");
                    }
                }
            }

            Debug.Log($"[Migration v1→v2] Done. {totalChunksProcessed} chunks repacked " +
                      $"into {newRegions.Count} correct region files " +
                      $"(was {oldFiles.Length} broken files).");

            return totalChunksProcessed;
        }

        // ─────────────────────────────────────────────────────────────────────

        private static int ProcessOldRegionFile(
            string oldFilePath,
            string newRegionPath,
            CompressionAlgorithm targetCompression,
            IRegionAddressCodec v1Codec,
            IRegionAddressCodec v2Codec,
            Dictionary<(int, int), RegionFile> newRegions)
        {
            // ── Parse the broken region coords from the filename ───────────────
            // Filename format: "r.{brokenRX}.{brokenRZ}.bin"
            string stem = Path.GetFileNameWithoutExtension(oldFilePath); // "r.10.28"
            string[] parts = stem.Split('.');

            if (parts.Length != 3
                || !int.TryParse(parts[1], out int brokenRX)
                || !int.TryParse(parts[2], out int brokenRZ))
            {
                Debug.LogWarning($"[Migration v1→v2] Skipping unrecognized filename: {Path.GetFileName(oldFilePath)}");
                return 0;
            }

            int chunksProcessed = 0;

            using RegionFile oldRegion = new RegionFile(oldFilePath);

            foreach (Vector2Int localCoord in oldRegion.GetAllChunkCoords())
            {
                (byte[] compressedData, CompressionAlgorithm oldAlgorithm) = oldRegion.LoadChunkData(localCoord.x, localCoord.y);
                if (compressedData == null) continue;

                // ── Step 1: decode V1 address → chunk index ───────────────────
                // The V1 decoder reverses the broken encoder to recover the true
                // chunk index (e.g. slot (0, localCoord.x=0) in broken region r.10.0
                // → voxelX 320 → chunkIndex 20).
                Vector2Int chunkIndex = v1Codec.RegionSlotToChunkIndex(
                    brokenRX, brokenRZ,
                    localCoord.x, localCoord.y);

                // ── Step 2: re-encode chunk index → correct V2 address ────────
                // ChunkVoxelPosToRegionAddress expects the voxel-space world origin
                // (ChunkData.position = chunkIndex * ChunkWidth). We reconstruct it
                // here with V1's hardcoded ChunkWidth of 16 to avoid a live
                // dependency on VoxelData.ChunkWidth. The V2 encoder divides it
                // back to a chunk index internally, so the round-trip is exact.
                Vector2Int chunkVoxelPos = new Vector2Int(
                    chunkIndex.x * V1_CHUNK_WIDTH,
                    chunkIndex.y * V1_CHUNK_WIDTH);

                (Vector2Int correctRegionCoord, int correctLocalX, int correctLocalZ) =
                    v2Codec.ChunkVoxelPosToRegionAddress(chunkVoxelPos);

                // ── Step 3: get or create the target new region file ──────────
                (int x, int y) regionKey = (correctRegionCoord.x, correctRegionCoord.y);
                if (!newRegions.TryGetValue(regionKey, out RegionFile newRegion))
                {
                    string newFilePath = Path.Combine(
                        newRegionPath,
                        $"r.{correctRegionCoord.x}.{correctRegionCoord.y}.bin");
                    newRegion = new RegionFile(newFilePath);
                    newRegions[regionKey] = newRegion;
                }

                // ── Step 4: recompress and write at the correct address ────────
                byte[] rawData = Decompress(compressedData, oldAlgorithm);
                byte[] recompressed = Compress(rawData, targetCompression);

                newRegion.SaveChunkData(correctLocalX, correctLocalZ, recompressed, recompressed.Length, targetCompression);
                chunksProcessed++;
            }

            return chunksProcessed;
        }

        // ── Compression helpers ───────────────────────────────────────────────

        private static byte[] Decompress(byte[] data, CompressionAlgorithm algo)
        {
            using MemoryStream inMs = new MemoryStream(data);
            using Stream decompressor = CompressionFactory.CreateInputStream(inMs, algo);
            using MemoryStream outMs = new MemoryStream();
            decompressor.CopyTo(outMs);
            return outMs.ToArray();
        }

        private static byte[] Compress(byte[] data, CompressionAlgorithm algo)
        {
            using MemoryStream outMs = new MemoryStream();
            using (Stream compressor = CompressionFactory.CreateOutputStream(outMs, algo, leaveOpen: true))
            {
                compressor.Write(data, 0, data.Length);
            }

            return outMs.ToArray();
        }
    }
}
