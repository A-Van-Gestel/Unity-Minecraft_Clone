using System.IO;
using System.Threading.Tasks;
using Data;
using Helpers;
using Serialization;
using Serialization.Migration;
using Serialization.Migration.Steps;
using UnityEngine;

namespace Editor.Validation.MigrationChain
{
    /// <summary>
    /// The v1→v2 region-layout half of roadmap NS-7b: the only migration step that sets
    /// <c>RequiresRegionLayoutMigration</c>, and therefore the only one that moves chunks between region files
    /// instead of rewriting their bytes.
    /// <para>
    /// The fixture writes region files at the historically broken V1 addresses using its <b>own</b> copy of the
    /// V1 formula rather than borrowing <c>RegionAddressCodec</c>'s V1 encoder. Two reasons: that encoder emits
    /// a <c>Debug.LogError</c> on every call even when explicitly permitted (it is meant to be impossible to
    /// use by accident), which would spray errors through <c>Validate All</c>; and a fixture that shares the
    /// production codec could not detect the codec being wrong. The formula is cross-checked against the
    /// production V1 <i>decoder</i> instead, which is silent and is what the repack step actually uses.
    /// </para>
    /// </summary>
    public static partial class MigrationChainValidationSuite
    {
        /// <summary>Region side in chunk slots — the divisor both codecs use.</summary>
        private const int REGION_SIDE = 32;

        /// <summary>First fixture chunk index. 20 × 16 = voxel 320 → broken region 10, slot 0.</summary>
        private const int REPACK_CHUNK_A = 20;

        /// <summary>Second fixture chunk index. 21 × 16 = voxel 336 → broken region 10, slot 16.</summary>
        private const int REPACK_CHUNK_B = 21;

        /// <summary>Chunk Z for both fixture chunks. Kept non-negative: V1 addressing predates negative worlds.</summary>
        private const int REPACK_CHUNK_Z = 0;

        // --- The frozen V1 (broken) address formula --------------------------------------------------

        /// <summary>
        /// The V1 encoder's arithmetic, reproduced from the step's own header ("regionX = floor(voxelX / 32),
        /// localX = voxelX % 32") so the fixture does not depend on the production codec it is testing against.
        /// </summary>
        /// <param name="chunkIndexX">Chunk index X.</param>
        /// <param name="chunkIndexZ">Chunk index Z.</param>
        /// <returns>The broken region coordinate and in-file slot a v1 world would have used.</returns>
        private static (Vector2Int region, int localX, int localZ) BrokenV1Address(int chunkIndexX, int chunkIndexZ)
        {
            int voxelX = chunkIndexX * ERA_CHUNK_WIDTH;
            int voxelZ = chunkIndexZ * ERA_CHUNK_WIDTH;
            return (new Vector2Int(voxelX / REGION_SIDE, voxelZ / REGION_SIDE),
                voxelX % REGION_SIDE, voxelZ % REGION_SIDE);
        }

        /// <summary>
        /// Writes the two fixture chunks into a v1-addressed region folder, each carrying an authored
        /// chunk-format payload.
        /// </summary>
        /// <param name="regionPath">Folder to create the region files in.</param>
        /// <param name="chunkFormatVersion">Chunk format the payloads are authored in.</param>
        /// <returns>The payload bytes written for <see cref="REPACK_CHUNK_A"/>.</returns>
        private static byte[] SeedV1AddressedRegion(string regionPath, byte chunkFormatVersion)
        {
            Directory.CreateDirectory(regionPath);
            byte[] firstPayload = null;

            foreach (int chunkX in new[] { REPACK_CHUNK_A, REPACK_CHUNK_B })
            {
                // Each chunk is stamped with its OWN coordinate, as a real world is. Sharing one payload
                // would have both chunks claim a single position, so a chunk landing at the wrong address
                // would still read back as "a chunk" and the fault would surface only as a coord warning.
                byte[] payload = BuildHistoricalChunkPayload(chunkFormatVersion, chunkX, REPACK_CHUNK_Z);
                firstPayload ??= payload;

                (Vector2Int region, int localX, int localZ) = BrokenV1Address(chunkX, REPACK_CHUNK_Z);
                using RegionFile file = new RegionFile(
                    Path.Combine(regionPath, $"r.{region.x.ToString()}.{region.y.ToString()}.bin"));
                file.SaveChunkData(localX, localZ, payload, payload.Length, CompressionAlgorithm.None);
            }

            return firstPayload;
        }

        // --- Scenarios -----------------------------------------------------------------------------

        /// <summary>B24. Red when: the v1→v2 repack stops recovering the true chunk index from a broken address,
        /// or stops writing it at the corrected one — either way chunks land in the wrong file and are lost.
        /// <para>
        /// Guarded against the no-op false green two ways: the fixture's broken address is asserted to
        /// <i>differ</i> from the correct one (a fixture accidentally written at V2 addresses would make the
        /// repack a no-op and pass), and the fixture's own V1 formula is cross-checked against the production
        /// V1 decoder the step relies on.
        /// </para></summary>
        private static bool RegionRepackMovesChunksToCorrectAddresses()
        {
            using MigrationFixture fx = new MigrationFixture();
            string oldPath = Path.Combine(fx.SavePath, "RepackOld");
            string newPath = Path.Combine(fx.SavePath, "RepackNew");
            byte[] payload = SeedV1AddressedRegion(oldPath, 2);
            Directory.CreateDirectory(newPath);

            // The fixture's frozen formula must agree with the decoder the step uses to undo it.
            (Vector2Int brokenRegion, int brokenLocalX, int brokenLocalZ) = BrokenV1Address(REPACK_CHUNK_A, REPACK_CHUNK_Z);
            Vector2Int decoded = RegionAddressCodec.ForVersion(1)
                .RegionSlotToChunkIndex(brokenRegion.x, brokenRegion.y, brokenLocalX, brokenLocalZ);
            bool ok = Check($"the fixture's V1 address round-trips through the production V1 decoder to chunk {REPACK_CHUNK_A.ToString()}, got {decoded.x.ToString()}",
                decoded.x == REPACK_CHUNK_A && decoded.y == REPACK_CHUNK_Z);

            // Non-no-op guard: the broken and correct addresses must actually differ.
            Vector2Int chunkVoxelPos = new Vector2Int(REPACK_CHUNK_A * ERA_CHUNK_WIDTH, REPACK_CHUNK_Z * ERA_CHUNK_WIDTH);
            (Vector2Int correctRegion, int correctLocalX, int correctLocalZ) =
                RegionAddressCodec.ForVersion(2).ChunkVoxelPosToRegionAddress(chunkVoxelPos);
            ok &= Check($"the broken address (r{brokenRegion.x.ToString()} slot {brokenLocalX.ToString()}) differs from the correct one (r{correctRegion.x.ToString()} slot {correctLocalX.ToString()}) — so the repack cannot pass as a no-op",
                brokenRegion != correctRegion || brokenLocalX != correctLocalX);

            int processed = new MigrationV1ToV2RegionRepack()
                .PerformRegionLayoutMigration(oldPath, newPath, CompressionAlgorithm.None);

            ok &= Check($"both fixture chunks were repacked, got {processed.ToString()}", processed == 2);
            ok &= Check($"the broken multi-file layout collapses to one correct region file, got {Directory.GetFiles(newPath, "r.*.*.bin").Length.ToString()}",
                Directory.GetFiles(newPath, "r.*.*.bin").Length == 1);

            string newFile = Path.Combine(newPath, $"r.{correctRegion.x.ToString()}.{correctRegion.y.ToString()}.bin");
            ok &= Check("the corrected region file exists", File.Exists(newFile));
            if (File.Exists(newFile))
            {
                using RegionFile region = new RegionFile(newFile);
                (byte[] stored, CompressionAlgorithm algorithm) = region.LoadChunkData(correctLocalX, correctLocalZ);
                ok &= Check("the chunk is readable at its corrected slot", stored != null);
                if (stored != null)
                {
                    // Hash both sides: a length + first-byte check would pass a repack that reordered or
                    // rewrote any interior byte, under a PASS line claiming the payload was byte-identical.
                    ok &= Check($"the stored algorithm is preserved, got {algorithm.ToString()}",
                        algorithm == CompressionAlgorithm.None);
                    ok &= Check($"the payload is byte-identical — the repack corrects addressing only ({stored.Length.ToString()} vs {payload.Length.ToString()} bytes)",
                        HashPayload(stored) == HashPayload(payload));
                }
            }

            return ok;
        }

        /// <summary>B25. Red when: a v1 world stops getting BOTH region passes — the layout repack and the
        /// per-chunk format chain. v1 is the only world version whose path contains a layout step, so it is the
        /// only version where running one pass instead of both is possible; the payloads would stay
        /// chunk-format v1 inside a world stamped current, fail the version check in
        /// <c>ChunkSerializer.Deserialize</c>, and regenerate from seed silently — recoverable only from the
        /// pre-migration backup, which the player has to know to reach for.
        /// <para>
        /// Authored as the <c>K10</c> repro of <c>_FIXED_BUGS.md</c> Serialization 07 and promoted after the fix was
        /// confirmed on a real v1 save: 1282 broken-addressed region files collapsed to 9 correct ones and all
        /// 4855 chunks came back at the current format.
        /// </para></summary>
        private static bool V1WorldChunksSurviveMigration()
        {
            using MigrationFixture fx = new MigrationFixture();
            SeedV1AddressedRegion(fx.RegionPath, 1);
            File.WriteAllText(fx.LevelDatPath, V1_LEVEL_DAT);

            ProgressRecorder progress = new ProgressRecorder();
            RunMigration(new MigrationManager(), fx, 1, progress);

            // The world reports itself fully migrated — which is exactly what makes the loss silent.
            bool ok = Check($"the world is stamped current after migration, got v{ReadLevelDatFromDisk(fx).version.ToString()}",
                ReadLevelDatFromDisk(fx).version == SaveSystem.CURRENT_VERSION);
            ok &= Check($"both chunks were repacked, got {progress.Last.ProcessedItems.ToString()}",
                progress.Last.ProcessedItems == 2);

            int readable = 0;
            ChunkStorageManager storage = new ChunkStorageManager(fx.WorldName, true, SaveSystem.CURRENT_VERSION);
            try
            {
                foreach (int chunkX in new[] { REPACK_CHUNK_A, REPACK_CHUNK_B })
                {
                    Vector2Int pos = new Vector2Int(chunkX * ERA_CHUNK_WIDTH, REPACK_CHUNK_Z * ERA_CHUNK_WIDTH);
                    ChunkData chunk = Task.Run(() => storage.LoadChunkAsync(pos)).GetAwaiter().GetResult();
                    if (chunk == null) continue;
                    readable++;
                    World.Instance.ChunkPool.ReturnChunkData(chunk);
                }
            }
            finally
            {
                storage.Dispose();
            }

            ok &= Check($"every migrated chunk is readable rather than regenerated from seed, got {readable.ToString()} of 2",
                readable == 2);
            return ok;
        }
    }
}
