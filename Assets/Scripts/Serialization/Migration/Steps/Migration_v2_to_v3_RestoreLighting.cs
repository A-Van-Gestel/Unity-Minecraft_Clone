using System;
using System.IO;

namespace Serialization.Migration.Steps
{
    /// <summary>
    /// Upgrades chunk format from v2 to v3 to trigger a full initial lighting recalculation via the background job system.
    /// This fixes legacy worlds that suffered from the "empty section light discard" bug, where sky light and large cavern
    /// blocklights were erased if the 16x16x16 section contained no solid blocks.
    /// </summary>
    public class MigrationV2ToV3RestoreLighting : WorldMigrationStep
    {
        public override int SourceWorldVersion => 2;
        public override int TargetWorldVersion => 3;
        public override string Description => "Upgrading chunk format to v3: scheduling deferred lighting fixes...";
        public override string ChangeSummary => "Restores sunlight and blocklight into un-solid sections that were discarded in V2 saves.";

        // Declare that this step writes chunk format version 3.
        public override byte? TargetChunkFormatVersion => 3;

        public override byte[] MigrateChunk(byte[] uncompressedData)
        {
            using MemoryStream inStream = new MemoryStream(uncompressedData);
            using BinaryReader reader = new BinaryReader(inStream);

            // =================================================================
            // V2 READ DEFINITION
            // Historical Reference: ChunkSerializer.cs, WriteChunkInternal()
            // =================================================================

            byte oldVersion = reader.ReadByte(); // 1 byte  | oldVersion (version 1 or 2)
            int x = reader.ReadInt32(); // 4 bytes | chunk X coordinate
            int z = reader.ReadInt32(); // 4 bytes | chunk Z coordinate
            _ = reader.ReadBoolean(); // 1 byte  | needsLight (THE FIELD WE ARE CHANGING TO TRUE)

            byte[] heightMap;
            if (oldVersion == 1)
            {
                // V1 heightmaps were 256 bytes (1 byte per block). Target is 512 (ushort per block).
                byte[] oldHm = reader.ReadBytes(256);
                heightMap = new byte[512];
                for (int i = 0; i < 256; i++)
                {
                    ushort val = oldHm[i];
                    heightMap[i * 2] = (byte)(val & 0xFF);
                    heightMap[i * 2 + 1] = (byte)(val >> 8);
                }
            }
            else
            {
                // V2 heightmaps are already 512 bytes
                heightMap = reader.ReadBytes(512);
            }

            int sectionBitmask = reader.ReadInt32(); // 4 bytes | bitmask of non-empty sections

            // --- Sections ---
            // Historical Reference: ChunkSerializer.cs, WriteSection()
            // Each section contains:
            //   byte (1)      : CURRENT_SECTION_VERSION
            //   ushort (2)    : section.nonAirCount
            //   byte[] (16384): voxel data (16*16*16 * sizeof(uint))
            // Total per section: 16387 bytes
            const int maxSections = 8;
            byte[][] v2Sections = new byte[maxSections][];

            for (int i = 0; i < maxSections; i++)
            {
                if ((sectionBitmask & (1 << i)) == 0) continue;

                byte secVersion = reader.ReadByte();
                ushort nonAirCount = reader.ReadUInt16();
                byte[] voxelData = reader.ReadBytes(16384);

                using MemoryStream secMs = new MemoryStream();
                using BinaryWriter secWriter = new BinaryWriter(secMs);
                secWriter.Write(secVersion);
                secWriter.Write(nonAirCount);
                secWriter.Write(voxelData);
                v2Sections[i] = secMs.ToArray();
            }

            // --- Lighting Queues ---
            // Historical Reference: ChunkSerializer.cs, WriteLightQueue()
            // Each queue item: Vector3Int (x, y, z -> 12 bytes) + byte (level -> 1 byte) = 13 bytes
            int sunCount = reader.ReadInt32();
            byte[] sunQueueData = reader.ReadBytes(sunCount * 13);
            int blockCount = reader.ReadInt32();
            byte[] blockQueueData = reader.ReadBytes(blockCount * 13);

            // =================================================================
            // V3 WRITE DEFINITION
            // Key change: 'needsLight' is written as true regardless of its previous value.
            // =================================================================

            using MemoryStream outStream = new MemoryStream();
            using BinaryWriter writer = new BinaryWriter(outStream);

            if (!TargetChunkFormatVersion.HasValue)
                throw new InvalidOperationException("TargetChunkFormatVersion must be defined for this step.");

            writer.Write(TargetChunkFormatVersion.Value); // NEW CHUNK VERSION
            writer.Write(x);
            writer.Write(z);
            writer.Write(true); // FORCE NeedsInitialLighting = true
            writer.Write(heightMap);
            writer.Write(sectionBitmask);

            for (int i = 0; i < maxSections; i++)
            {
                if ((sectionBitmask & (1 << i)) != 0)
                    writer.Write(v2Sections[i]);
            }

            writer.Write(sunCount);
            writer.Write(sunQueueData);
            writer.Write(blockCount);
            writer.Write(blockQueueData);

            return outStream.ToArray();
        }
    }
}
