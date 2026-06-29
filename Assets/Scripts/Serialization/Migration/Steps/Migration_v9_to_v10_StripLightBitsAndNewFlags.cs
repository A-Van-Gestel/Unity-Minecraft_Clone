using System.IO;

namespace Serialization.Migration.Steps
{
    /// <summary>
    /// Migrates chunk data from v6 → v7: strips legacy light bits from the <c>uint</c> voxel
    /// (bits 16-23 zeroed) and introduces uniform-sky-level section flags.
    /// <list type="bullet">
    ///   <item><c>0x00</c> — Voxels + uniform sky level (1B sky + 2B nonAirCount + 16384B voxels)</item>
    ///   <item><c>0x01</c> — Voxels + full LightData (2B nonAirCount + 16384B voxels + 8192B LightData)</item>
    ///   <item><c>0x02</c> — Light-only + uniform sky level (1B sky — 2 bytes total)</item>
    ///   <item><c>0x03</c> — Light-only + full LightData (8192B LightData)</item>
    /// </list>
    /// The <c>uint</c> voxel layout after this step: <c>[ID:16][Reserved:8][Meta:8]</c>.
    /// The <c>ushort LightData</c> array is the sole authority for light values.
    /// </summary>
    public class MigrationV9ToV10StripLightBitsAndNewFlags : WorldMigrationStep
    {
        public override int SourceWorldVersion => 9;
        public override int TargetWorldVersion => 10;
        public override string Description => "Stripping legacy light bits and optimizing section format...";
        public override string ChangeSummary => "Removes redundant light data from voxels and compresses uniform sky-light sections.";

        public override byte? TargetChunkFormatVersion => 7;

        // ── Frozen layout constants from chunk format v6 (source format) ─────

        private const int SECTION_VOXEL_COUNT = 16 * 16 * 16; // 4096
        private const int SECTION_VOXEL_BYTES = SECTION_VOXEL_COUNT * sizeof(uint); // 16384
        private const int LIGHT_DATA_BYTES = SECTION_VOXEL_COUNT * sizeof(ushort); // 8192
        private const int HEIGHTMAP_ENTRIES = 16 * 16; // 256
        private const int HEIGHTMAP_BYTES = HEIGHTMAP_ENTRIES * sizeof(ushort); // 512

        // Frozen bit-packing constants from BurstVoxelDataBitMapping (source format uint).
        // Layout: [ID:16][Sun:4][Block:4][Meta:8]
        private const int SUNLIGHT_SHIFT = 16;
        private const uint NIBBLE_MASK = 0xF;
        private const uint LEGACY_LIGHT_MASK = 0x00FF0000; // bits 16-23 (sun + block)

        // Frozen bit-packing constants from LightBitMapping (ushort format).
        // Layout: [Sky:4][BlockR:4][BlockG:4][BlockB:4]
        private const int SKY_SHIFT = 0;
        private const int BLOCK_R_SHIFT = 4;
        private const int BLOCK_G_SHIFT = 8;
        private const int BLOCK_B_SHIFT = 12;
        private const ushort BLOCKLIGHT_RGB_MASK = 0xFFF0; // bits 4-15

        // v6 section flags (source format).
        private const byte V6_FLAG_VOXELS_ONLY = 0x00;
        private const byte V6_FLAG_VOXELS_AND_LIGHT = 0x01;
        private const byte V6_FLAG_LIGHT_ONLY = 0x02;

        // v7 section flags (target format).
        private const byte V7_FLAG_VOXELS_UNIFORM_SKY = 0x00;
        private const byte V7_FLAG_VOXELS_AND_LIGHT = 0x01;
        private const byte V7_FLAG_LIGHT_ONLY_UNIFORM_SKY = 0x02;
        private const byte V7_FLAG_LIGHT_ONLY_FULL = 0x03;

        public override byte[] MigrateChunk(byte[] uncompressedData)
        {
            using MemoryStream inStream = new MemoryStream(uncompressedData);
            using BinaryReader reader = new BinaryReader(inStream);

            using MemoryStream outStream = new MemoryStream(uncompressedData.Length);
            using BinaryWriter writer = new BinaryWriter(outStream);

            // --- Chunk Header ---
            byte oldVersion = reader.ReadByte(); // 1 byte — v6
            int chunkX = reader.ReadInt32(); // 4 bytes
            int chunkZ = reader.ReadInt32(); // 4 bytes

            writer.Write((byte)7); // v7 — must match TargetChunkFormatVersion
            writer.Write(chunkX);
            writer.Write(chunkZ);

            // --- State Flags (pass through) ---
            bool needsInitialLighting = reader.ReadBoolean(); // 1 byte
            writer.Write(needsInitialLighting);

            // --- Height Map (pass through) ---
            // ushort[256] = 512 bytes, see ChunkSerializer.WriteChunkInternal()
            byte[] heightMapBytes = reader.ReadBytes(HEIGHTMAP_BYTES);
            writer.Write(heightMapBytes);

            // --- Section Bitmask (pass through) ---
            int sectionBitmask = reader.ReadInt32(); // 4 bytes
            writer.Write(sectionBitmask);

            // --- Migrate Sections ---
            for (int i = 0; i < 16; i++)
            {
                if ((sectionBitmask & (1 << i)) != 0)
                {
                    MigrateSection(reader, writer);
                }
            }

            // --- Light Queues (pass through, v9 format with RGB) ---
            // Each entry: 3×int32 (position XYZ) + 4×byte (OldLightLevel, OldBlockR/G/B) = 16 bytes
            CopyLightQueue(reader, writer);
            CopyLightQueue(reader, writer);

            return outStream.ToArray();
        }

        /// <summary>
        /// Reads one v6 section, strips legacy light bits from voxels, classifies the light data,
        /// and writes as v7 format with the most compact applicable flag.
        /// </summary>
        private static void MigrateSection(BinaryReader reader, BinaryWriter writer)
        {
            byte v6Flag = reader.ReadByte();

            switch (v6Flag)
            {
                case V6_FLAG_VOXELS_ONLY:
                    MigrateSectionVoxelsOnly(reader, writer);
                    break;
                case V6_FLAG_VOXELS_AND_LIGHT:
                    MigrateSectionVoxelsAndLight(reader, writer);
                    break;
                case V6_FLAG_LIGHT_ONLY:
                    MigrateSectionLightOnly(reader, writer);
                    break;
                default:
                    throw new InvalidDataException($"Unknown v6 section flag: 0x{v6Flag:X2}");
            }
        }

        /// <summary>
        /// v6 flag 0x00 — Voxels only (no persisted LightData).
        /// Layout: [flag:1][nonAirCount:2][voxels:16384]
        /// Light data is embedded in the uint voxel bits (sun=bits 16-19, block=bits 20-23).
        /// v6 flag 0x00 was written when no RGB blocklight existed — blocklight bits are always 0.
        /// Extracts sun levels, zeros legacy bits, and writes as v7 flag 0x00 (uniform) or 0x01 (non-uniform).
        /// </summary>
        private static unsafe void MigrateSectionVoxelsOnly(BinaryReader reader, BinaryWriter writer)
        {
            ushort nonAirCount = reader.ReadUInt16(); // 2 bytes
            byte[] voxelBytes = ReadExact(reader, SECTION_VOXEL_BYTES); // uint[4096] × 4 = 16384 bytes

            // Single pass: extract sky levels, check uniformity, AND zero legacy bits simultaneously.
            // Blocklight is always 0 for v6 flag 0x00 sections (that's why they were voxels-only).
            // Sky values must be captured before zeroing since both operate on the same uint bits.
            byte[] skyLevels = new byte[SECTION_VOXEL_COUNT];
            bool isUniformSky = true;
            byte uniformSkyLevel;

            fixed (byte* pVoxels = voxelBytes)
            {
                uint* voxels = (uint*)pVoxels;
                uniformSkyLevel = (byte)((voxels[0] >> SUNLIGHT_SHIFT) & NIBBLE_MASK);

                for (int i = 0; i < SECTION_VOXEL_COUNT; i++)
                {
                    byte sun = (byte)((voxels[i] >> SUNLIGHT_SHIFT) & NIBBLE_MASK);
                    skyLevels[i] = sun;
                    if (sun != uniformSkyLevel) isUniformSky = false;
                    voxels[i] &= ~LEGACY_LIGHT_MASK;
                }
            }

            if (isUniformSky)
            {
                // v7 flag 0x00: Voxels + uniform sky level
                writer.Write(V7_FLAG_VOXELS_UNIFORM_SKY);
                writer.Write(uniformSkyLevel);
                writer.Write(nonAirCount);
                writer.Write(voxelBytes);
            }
            else
            {
                // Non-uniform sky — synthesize full LightData (sky only, block=0) and write as flag 0x01.
                // Rare: partially shaded sections saved as voxels-only because no RGB blocklight.
                byte[] lightDataBytes = new byte[LIGHT_DATA_BYTES];
                fixed (byte* pLight = lightDataBytes)
                {
                    ushort* lightData = (ushort*)pLight;
                    for (int i = 0; i < SECTION_VOXEL_COUNT; i++)
                    {
                        lightData[i] = (ushort)((skyLevels[i] & NIBBLE_MASK) << SKY_SHIFT);
                    }
                }

                writer.Write(V7_FLAG_VOXELS_AND_LIGHT);
                writer.Write(nonAirCount);
                writer.Write(voxelBytes);
                writer.Write(lightDataBytes);
            }
        }

        /// <summary>
        /// v6 flag 0x01 — Voxels + full LightData (section has RGB blocklight).
        /// Layout: [flag:1][nonAirCount:2][voxels:16384][lightData:8192]
        /// Zeros legacy light bits in voxels. May downgrade to v7 flag 0x00 if LightData is
        /// uniform sky with no blocklight.
        /// </summary>
        private static unsafe void MigrateSectionVoxelsAndLight(BinaryReader reader, BinaryWriter writer)
        {
            ushort nonAirCount = reader.ReadUInt16(); // 2 bytes
            byte[] voxelBytes = ReadExact(reader, SECTION_VOXEL_BYTES); // uint[4096] × 4 = 16384 bytes
            byte[] lightDataBytes = ReadExact(reader, LIGHT_DATA_BYTES); // ushort[4096] × 2 = 8192 bytes

            // Zero bits 16-23 in voxels (strip legacy light).
            fixed (byte* pVoxels = voxelBytes)
            {
                uint* voxels = (uint*)pVoxels;
                for (int i = 0; i < SECTION_VOXEL_COUNT; i++)
                {
                    voxels[i] &= ~LEGACY_LIGHT_MASK;
                }
            }

            // Classify the existing LightData.
            ClassifyLightData(lightDataBytes, out bool hasBlocklight, out bool isUniformSky, out byte uniformSkyLevel);

            if (!hasBlocklight && isUniformSky)
            {
                // Downgrade: no blocklight and uniform sky → v7 flag 0x00 (saves 8192 bytes).
                writer.Write(V7_FLAG_VOXELS_UNIFORM_SKY);
                writer.Write(uniformSkyLevel);
                writer.Write(nonAirCount);
                writer.Write(voxelBytes);
            }
            else
            {
                // Full LightData required.
                writer.Write(V7_FLAG_VOXELS_AND_LIGHT);
                writer.Write(nonAirCount);
                writer.Write(voxelBytes);
                writer.Write(lightDataBytes);
            }
        }

        /// <summary>
        /// v6 flag 0x02 — Light-only (empty air section carrying propagated light).
        /// Layout: [flag:1][lightData:8192]
        /// Classifies light data and writes as v7 flag 0x02 (uniform) or 0x03 (full).
        /// </summary>
        private static void MigrateSectionLightOnly(BinaryReader reader, BinaryWriter writer)
        {
            byte[] lightDataBytes = ReadExact(reader, LIGHT_DATA_BYTES); // ushort[4096] × 2 = 8192 bytes

            ClassifyLightData(lightDataBytes, out bool hasBlocklight, out bool isUniformSky, out byte uniformSkyLevel);

            if (!hasBlocklight && isUniformSky)
            {
                // v7 flag 0x02: 2 bytes total (down from 8193 in v6).
                writer.Write(V7_FLAG_LIGHT_ONLY_UNIFORM_SKY);
                writer.Write(uniformSkyLevel);
            }
            else
            {
                // v7 flag 0x03: full LightData.
                writer.Write(V7_FLAG_LIGHT_ONLY_FULL);
                writer.Write(lightDataBytes);
            }
        }

        // ── Classification Helpers ──────────────────────────────────────────

        /// <summary>
        /// Single-pass scan of raw <c>ushort[]</c> LightData bytes to determine blocklight presence,
        /// sky uniformity, and the uniform sky level (if applicable).
        /// </summary>
        private static unsafe void ClassifyLightData(
            byte[] lightDataBytes, out bool hasBlocklight, out bool isUniformSky, out byte uniformSkyLevel)
        {
            hasBlocklight = false;
            isUniformSky = true;

            fixed (byte* pLight = lightDataBytes)
            {
                ushort* lightData = (ushort*)pLight;

                // First entry establishes the candidate uniform sky value.
                uniformSkyLevel = (byte)((lightData[0] >> SKY_SHIFT) & NIBBLE_MASK);

                for (int i = 0; i < SECTION_VOXEL_COUNT; i++)
                {
                    ushort packed = lightData[i];

                    if ((packed & BLOCKLIGHT_RGB_MASK) != 0)
                        hasBlocklight = true;

                    byte sky = (byte)((packed >> SKY_SHIFT) & NIBBLE_MASK);
                    if (sky != uniformSkyLevel)
                        isUniformSky = false;

                    if (hasBlocklight && !isUniformSky)
                        return;
                }
            }
        }

        /// <summary>
        /// Reads exactly <paramref name="count"/> bytes from the reader, throwing if the stream is truncated.
        /// <c>BinaryReader.ReadBytes</c> silently returns a short array on EOF — this guard prevents
        /// unsafe pointer arithmetic from walking past the allocation boundary.
        /// </summary>
        private static byte[] ReadExact(BinaryReader reader, int count)
        {
            byte[] data = reader.ReadBytes(count);
            if (data.Length != count)
                throw new InvalidDataException(
                    $"Section data truncated. Read {data.Length} of {count} bytes.");
            return data;
        }

        /// <summary>
        /// Copies a v9-format light queue (count + 16-byte entries) from reader to writer verbatim.
        /// Each entry: 3×int32 (position XYZ) + 4×byte (OldLightLevel, OldBlockR, OldBlockG, OldBlockB) = 16 bytes.
        /// See ChunkSerializer.WriteLightQueue().
        /// </summary>
        private static void CopyLightQueue(BinaryReader reader, BinaryWriter writer)
        {
            int count = reader.ReadInt32();
            if (count is < 0 or > 100_000)
                throw new InvalidDataException($"Invalid LightQueue count during migration: {count}");

            writer.Write(count);

            const int ENTRY_BYTES = 16; // 3×int32 (12) + 4×byte (4) = 16
            for (int i = 0; i < count; i++)
            {
                byte[] entry = reader.ReadBytes(ENTRY_BYTES);
                writer.Write(entry);
            }
        }
    }
}
