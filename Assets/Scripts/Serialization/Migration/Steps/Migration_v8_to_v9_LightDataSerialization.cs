using System;
using System.IO;

namespace Serialization.Migration.Steps
{
    /// <summary>
    /// Migrates chunk data from v5 → v6 by persisting <c>ushort[] LightData</c> per section.
    /// Introduces a flag-based section format:
    /// <list type="bullet">
    ///   <item><c>0x00</c> — Voxels only (no RGB blocklight; LightData reconstructed on load)</item>
    ///   <item><c>0x01</c> — Voxels + LightData (section has RGB blocklight)</item>
    ///   <item><c>0x02</c> — Light-only (empty air section carrying propagated light)</item>
    /// </list>
    /// Old saves have no light-only sections, so only flags <c>0x00</c> and <c>0x01</c> appear
    /// in migration output. Flag <c>0x02</c> is supported by the live reader for future saves.
    /// </summary>
    public class MigrationV8ToV9LightDataSerialization : WorldMigrationStep
    {
        public override int SourceWorldVersion => 8;
        public override int TargetWorldVersion => 9;
        public override string Description => "Persisting RGB light data per section...";
        public override string ChangeSummary => "Adds persistent ushort[] LightData to sections with RGB blocklight.";

        public override byte? TargetChunkFormatVersion => 6;

        // Frozen layout constants from chunk format v5 (the source format).
        private const int SECTION_VOXEL_COUNT = 16 * 16 * 16; // 4096
        private const int SECTION_VOXEL_BYTES = SECTION_VOXEL_COUNT * sizeof(uint); // 16384
        private const int LIGHT_DATA_BYTES = SECTION_VOXEL_COUNT * sizeof(ushort); // 8192
        private const int HEIGHTMAP_ENTRIES = 16 * 16; // 256
        private const int HEIGHTMAP_BYTES = HEIGHTMAP_ENTRIES * sizeof(ushort); // 512

        // Frozen bit-packing constants from BurstVoxelDataBitMapping (source format).
        private const int SUNLIGHT_SHIFT = 16;
        private const int BLOCKLIGHT_SHIFT = 20;
        private const uint NIBBLE_MASK = 0xF;

        // Frozen bit-packing constants from LightBitMapping (target format).
        private const int SUN_SHIFT = 0;
        private const int BLOCK_R_SHIFT = 4;
        private const int BLOCK_G_SHIFT = 8;
        private const int BLOCK_B_SHIFT = 12;
        private const ushort BLOCKLIGHT_RGB_MASK = 0xFFF0;

        // Section flags (target format v6).
        private const byte FLAG_VOXELS_ONLY = 0x00;
        private const byte FLAG_VOXELS_AND_LIGHT = 0x01;

        public override byte[] MigrateChunk(byte[] uncompressedData)
        {
            using MemoryStream inStream = new MemoryStream(uncompressedData);
            using BinaryReader reader = new BinaryReader(inStream);

            // Estimate output size: original + up to 8193 bytes per section (flag + LightData)
            using MemoryStream outStream = new MemoryStream(uncompressedData.Length + 16 * (LIGHT_DATA_BYTES + 1));
            using BinaryWriter writer = new BinaryWriter(outStream);

            // --- Chunk Header ---
            byte oldVersion = reader.ReadByte(); // v5
            int chunkX = reader.ReadInt32();
            int chunkZ = reader.ReadInt32();

            writer.Write(TargetChunkFormatVersion ?? 6); // v6
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

            // --- Migrate Sections ---
            for (int i = 0; i < 16; i++)
            {
                if ((sectionBitmask & (1 << i)) != 0)
                {
                    MigrateSection(reader, writer);
                }
            }

            // --- Light Queues (pass through, already v5/v8 format with RGB) ---
            CopyLightQueue(reader, writer);
            CopyLightQueue(reader, writer);

            return outStream.ToArray();
        }

        /// <summary>
        /// Reads one v5 section (version byte + nonAirCount + voxels), synthesizes LightData
        /// from legacy light bits, and writes as v6 format with the appropriate flag.
        /// </summary>
        private static void MigrateSection(BinaryReader reader, BinaryWriter writer)
        {
            // Read old section (v1 format: version byte + nonAirCount + voxels)
            byte sectionVersion = reader.ReadByte(); // Expected: 1
            ushort nonAirCount = reader.ReadUInt16();
            byte[] voxelBytes = reader.ReadBytes(SECTION_VOXEL_BYTES);

            // Synthesize LightData from legacy light bits in voxels
            ushort[] lightData = new ushort[SECTION_VOXEL_COUNT];
            bool hasBlocklight = false;

            unsafe
            {
                fixed (byte* pVoxels = voxelBytes)
                {
                    uint* voxels = (uint*)pVoxels;
                    for (int i = 0; i < SECTION_VOXEL_COUNT; i++)
                    {
                        uint packed = voxels[i];
                        byte sun = (byte)((packed >> SUNLIGHT_SHIFT) & NIBBLE_MASK);
                        byte block = (byte)((packed >> BLOCKLIGHT_SHIFT) & NIBBLE_MASK);

                        // Map scalar blocklight to white RGB: (L, L, L)
                        lightData[i] = (ushort)(
                            ((sun & NIBBLE_MASK) << SUN_SHIFT) |
                            ((block & NIBBLE_MASK) << BLOCK_R_SHIFT) |
                            ((block & NIBBLE_MASK) << BLOCK_G_SHIFT) |
                            ((block & NIBBLE_MASK) << BLOCK_B_SHIFT));

                        if ((lightData[i] & BLOCKLIGHT_RGB_MASK) != 0)
                            hasBlocklight = true;
                    }
                }
            }

            // Write v6 section
            if (hasBlocklight)
            {
                writer.Write(FLAG_VOXELS_AND_LIGHT);
                writer.Write(nonAirCount);
                writer.Write(voxelBytes);
                WriteUshortArray(writer, lightData);
            }
            else
            {
                writer.Write(FLAG_VOXELS_ONLY);
                writer.Write(nonAirCount);
                writer.Write(voxelBytes);
            }
        }

        /// <summary>
        /// Copies a v8-format light queue (count + 16-byte entries) from reader to writer verbatim.
        /// </summary>
        private static void CopyLightQueue(BinaryReader reader, BinaryWriter writer)
        {
            int count = reader.ReadInt32();
            if (count is < 0 or > 100_000)
                throw new InvalidDataException($"Invalid LightQueue count during migration: {count}");

            writer.Write(count);

            // Each v8 entry: 3×int32 (position) + 4×byte (OldLightLevel, OldBlockR/G/B) = 16 bytes
            const int ENTRY_BYTES = 16;
            for (int i = 0; i < count; i++)
            {
                byte[] entry = reader.ReadBytes(ENTRY_BYTES);
                writer.Write(entry);
            }
        }

        /// <summary>
        /// Writes a ushort array as raw bytes to the binary writer.
        /// </summary>
        private static unsafe void WriteUshortArray(BinaryWriter writer, ushort[] data)
        {
            fixed (ushort* pData = data)
            {
                byte* pBytes = (byte*)pData;
                writer.Write(new ReadOnlySpan<byte>(pBytes, data.Length * sizeof(ushort)));
            }
        }
    }
}
