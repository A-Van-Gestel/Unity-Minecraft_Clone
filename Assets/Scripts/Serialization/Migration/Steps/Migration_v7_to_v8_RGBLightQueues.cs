using System.IO;

namespace Serialization.Migration.Steps
{
    /// <summary>
    /// Migrates chunk data from v4 → v5 by expanding light queue entries from 13 bytes
    /// (Vector3Int + OldLightLevel) to 16 bytes (+ OldBlockR, OldBlockG, OldBlockB).
    /// Old scalar blocklight values are mapped to white RGB (L, L, L) for backwards compatibility.
    /// </summary>
    public class MigrationV7ToV8RGBLightQueues : WorldMigrationStep
    {
        public override int SourceWorldVersion => 7;
        public override int TargetWorldVersion => 8;
        public override string Description => "Expanding light queues for RGB blocklight...";
        public override string ChangeSummary => "Adds per-channel RGB fields to serialized light queue entries.";

        public override byte? TargetChunkFormatVersion => 5;

        /// <summary>
        /// Frozen section constants from chunk format v4 (the source format).
        /// Dense format: version byte + ushort nonAirCount + raw voxel bytes (4096 × uint = 16384 bytes).
        /// </summary>
        private const int SECTION_VOXEL_COUNT = 16 * 16 * 16; // 4096

        private const int SECTION_VOXEL_BYTES = SECTION_VOXEL_COUNT * sizeof(uint); // 16384
        private const int HEIGHTMAP_ENTRIES = 16 * 16; // 256
        private const int HEIGHTMAP_BYTES = HEIGHTMAP_ENTRIES * sizeof(ushort); // 512

        public override byte[] MigrateChunk(byte[] uncompressedData)
        {
            using MemoryStream inStream = new MemoryStream(uncompressedData);
            using BinaryReader reader = new BinaryReader(inStream);

            using MemoryStream outStream = new MemoryStream(uncompressedData.Length + 256);
            using BinaryWriter writer = new BinaryWriter(outStream);

            // --- Chunk Header ---
            byte oldVersion = reader.ReadByte();
            int chunkX = reader.ReadInt32();
            int chunkZ = reader.ReadInt32();

            writer.Write(TargetChunkFormatVersion.Value);
            writer.Write(chunkX);
            writer.Write(chunkZ);

            // --- State Flags (pass through) ---
            bool needsInitialLighting = reader.ReadBoolean();
            writer.Write(needsInitialLighting);

            // --- Height Map (pass through) ---
            byte[] heightMapBytes = reader.ReadBytes(HEIGHTMAP_BYTES);
            writer.Write(heightMapBytes);

            // --- Section Bitmask (pass through) ---
            int sectionBitmask = reader.ReadInt32();
            writer.Write(sectionBitmask);

            // --- Sections (pass through, only those flagged in bitmask) ---
            for (int i = 0; i < 16; i++)
            {
                if ((sectionBitmask & (1 << i)) != 0)
                {
                    CopySectionVerbatim(reader, writer);
                }
            }

            // --- Migrate light queues ---
            MigrateLightQueue(reader, writer, isSunlight: true); // Sunlight queue
            MigrateLightQueue(reader, writer, isSunlight: false); // Blocklight queue

            return outStream.ToArray();
        }

        /// <summary>
        /// Copies one section from reader to writer without interpreting voxel contents.
        /// Format v1: version byte + ushort nonAirCount + raw voxel data (16384 bytes).
        /// </summary>
        private static void CopySectionVerbatim(BinaryReader reader, BinaryWriter writer)
        {
            byte sectionVersion = reader.ReadByte();
            writer.Write(sectionVersion);

            if (sectionVersion == 1)
            {
                ushort nonAirCount = reader.ReadUInt16();
                writer.Write(nonAirCount);

                byte[] voxelBytes = reader.ReadBytes(SECTION_VOXEL_BYTES);
                writer.Write(voxelBytes);
            }
        }

        /// <summary>
        /// Reads old-format queue entries (13 bytes each) and writes new-format entries (16 bytes each).
        /// For blocklight entries, maps old scalar OldLightLevel to white RGB: (L, L, L).
        /// For sunlight entries, OldBlockR/G/B are set to (0, 0, 0) since sunlight has no blocklight color.
        /// </summary>
        private static void MigrateLightQueue(BinaryReader reader, BinaryWriter writer, bool isSunlight)
        {
            int count = reader.ReadInt32();
            writer.Write(count);

            for (int i = 0; i < count; i++)
            {
                int x = reader.ReadInt32();
                int y = reader.ReadInt32();
                int z = reader.ReadInt32();
                byte level = reader.ReadByte();

                writer.Write(x);
                writer.Write(y);
                writer.Write(z);
                writer.Write(level); // OldLightLevel

                byte rgbValue = isSunlight ? (byte)0 : level;
                writer.Write(rgbValue); // OldBlockR
                writer.Write(rgbValue); // OldBlockG
                writer.Write(rgbValue); // OldBlockB
            }
        }
    }
}
