using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Editor.Validation.MigrationChain
{
    /// <summary>
    /// Authors chunk payloads in the historical on-disk layouts that nothing in the engine can still write
    /// (chunk format **v1** and **v2**) — the input the five chunk-payload migration steps expect.
    /// <para>
    /// <b>Only these two eras are authored, deliberately.</b> The manager re-reads the version byte between
    /// steps and applies each step whose <c>TargetChunkFormatVersion</c> exceeds it, so a single v1/v2 payload
    /// walks the whole chain: v2→v3 writes **3**, v5→v6 writes **4**, v7→v8 writes **5**, v8→v9 writes **6**,
    /// v9→v10 writes **7** (current). Authoring one fixture per era, as roadmap NS-7b originally sketched,
    /// would be five times the work for the same coverage.
    /// </para>
    /// <para>
    /// <b>Every byte count here is traced to the step that reads it</b>, per
    /// <c>AOT_WORLD_MIGRATION_SYSTEM.md</c> §6's "always trace every magic number" rule. The layout is the
    /// "V2 READ DEFINITION" block in <c>Migration_v2_to_v3_RestoreLighting.cs</c>.
    /// </para>
    /// <para>
    /// <b>Known limit of this oracle:</b> the layout is derived from the steps' own read definitions, because
    /// that is the only surviving record of it. So these fixtures catch regressions and composition faults but
    /// <i>cannot</i> catch a step that has always misread its input — fixture and reader would share the error.
    /// The output side has no such gap: the chain's result is validated by the real
    /// <c>ChunkSerializer.Deserialize</c>, which is independent of anything authored here.
    /// </para>
    /// </summary>
    public static partial class MigrationChainValidationSuite
    {
        // --- Era layout constants (each traced to its reader) --------------------------------------

        /// <summary>v1 heightmap: one byte per column, 16×16. Widened to ushorts by the v2→v3 step.</summary>
        private const int ERA_V1_HEIGHTMAP_BYTES = 256;

        /// <summary>v2+ heightmap: one ushort per column, 16×16 = 512 bytes (v2→v3 reads exactly this).</summary>
        private const int ERA_V2_HEIGHTMAP_BYTES = 512;

        /// <summary>Voxels per section: 16³ (v2→v3's <c>V3_VOXELS_PER_SECTION</c>).</summary>
        private const int ERA_VOXELS_PER_SECTION = 4096;

        /// <summary>Section voxel block: uint32[4096] = 16384 bytes (v2→v3 reads <c>ReadBytes(16384)</c>).</summary>
        private const int ERA_SECTION_VOXEL_BYTES = ERA_VOXELS_PER_SECTION * sizeof(uint);

        /// <summary>Section version byte every era-v1/v2 section carries (v2→v3's <c>V3_SECTION_VERSION</c>).</summary>
        private const byte ERA_SECTION_VERSION = 1;

        /// <summary>Light-queue entry before v7→v8 widens it: 3×int32 position + 1 byte level = 13 bytes.</summary>
        private const int ERA_LIGHT_ENTRY_BYTES = 13;

        /// <summary>Sections the fixture populates — ChunkHeight 128 / 16 = 8, so bitmask bits 0–7.</summary>
        private const int FIXTURE_SECTION_COUNT = 8;

        /// <summary>
        /// Default chunk index the fixture payload is stamped with. The payload itself stores the
        /// <i>voxel-space origin</i> (index × <see cref="ERA_CHUNK_WIDTH"/>), which is what a real era save
        /// carries: <c>ChunkData.position</c> was the voxel origin, confirmed against on-disk v1 worlds.
        /// </summary>
        private const int FIXTURE_CHUNK_X = 3, FIXTURE_CHUNK_Z = -5;

        /// <summary>
        /// Chunk width frozen at every era this suite touches, for converting a chunk index to the voxel-space
        /// origin the region codecs and the storage manager address by. Hardcoded rather than read from
        /// <c>VoxelData</c> for the same reason the migration steps freeze their own copy: a future constant
        /// change must not silently re-point historical fixtures.
        /// </summary>
        private const int ERA_CHUNK_WIDTH = 16;

        // --- Frozen historical block ids ------------------------------------------------------------
        // NOT BlockIDs.*: these are the v5-era numeric ids the migration steps themselves embed as frozen
        // constants (see Migration_v5_to_v6's V5_* block and Migration_v4_to_v5's s_v4FluidBlockIds). Today's
        // BlockIDs values may differ, and using them here would test the wrong mapping.

        /// <summary>v5-era Facade — maps to SCHEMA_NONE, so its meta byte must be forced to 0.</summary>
        private const ushort V5_ID_FACADE = 13;

        /// <summary>v5-era StoneHalfSlab — maps to SCHEMA_KEEP_LEGACY, so its meta must pass through verbatim.</summary>
        private const ushort V5_ID_STONE_HALF_SLAB = 17;

        /// <summary>v5-era OakLog — routed to Axis3, but via its own converter that normalizes every legacy value to Y.</summary>
        private const ushort V5_ID_OAK_LOG = 14;

        /// <summary>v5-era Water — maps to FluidLevel4, and is one of the two frozen v4 fluid ids.</summary>
        private const ushort V5_ID_WATER = 19;

        /// <summary>v5-era Stone — maps to HorizontalOnly.</summary>
        private const ushort V5_ID_STONE = 1;

        // --- Seeded voxel slots (all inside section 0) ---------------------------------------------

        /// <summary>Section-0 voxel index of the SCHEMA_NONE probe (meta must become 0).</summary>
        private const int SLOT_FACADE = 0;

        /// <summary>Section-0 voxel index of the KEEP_LEGACY probe (meta must survive verbatim).</summary>
        private const int SLOT_HALF_SLAB = 1;

        /// <summary>Section-0 voxel index of the OakLog probe (every legacy value must normalize to Y).</summary>
        private const int SLOT_OAK_LOG = 2;

        /// <summary>Section-0 voxel index of the fluid probe.</summary>
        private const int SLOT_WATER = 3;

        /// <summary>Section-0 voxel index of the HorizontalOnly probe.</summary>
        private const int SLOT_STONE = 4;

        /// <summary>Section-0 voxel index carrying legacy light bits, for the v8→v9 LightData synthesis.</summary>
        private const int SLOT_LIGHT_CARRIER = 5;

        /// <summary>Non-air voxels the fixture seeds in section 0 (the six slots above).</summary>
        private const ushort FIXTURE_NON_AIR_COUNT = 6;

        /// <summary>Legacy meta on the KEEP_LEGACY probe — an arbitrary value that must survive unchanged.</summary>
        private const byte LEGACY_META_VERBATIM = 6;

        /// <summary>Legacy meta on the SCHEMA_NONE probe — non-zero, so "forced to 0" is observable.</summary>
        private const byte LEGACY_META_FORCED_TO_ZERO = 5;

        /// <summary>Legacy orientation storage index 0 (North) on the OakLog probe.</summary>
        private const byte LEGACY_META_AXIS3_NORTH = 0;

        /// <summary>
        /// Axis every v5 OakLog must normalize to. NOT the generic Axis3 mapping (which sends North→Z): OakLog
        /// has its own frozen converter that returns Y (0) for <i>every</i> legacy value, because historical
        /// oak logs never stored a meaningful axis and all rendered upright — see
        /// <c>ConvertLegacyOakLogMetaToAxis3</c>'s remarks.
        /// </summary>
        private const byte EXPECTED_OAK_LOG_AXIS = 0;

        /// <summary>
        /// Legacy meta on the HorizontalOnly probe: storage index 2 (West) with the high nibble set, so a
        /// correct conversion both keeps the yaw and masks the stray bits — 0xF2 → 2 is observably a
        /// conversion rather than a passthrough.
        /// </summary>
        private const byte LEGACY_META_HORIZONTAL_INPUT = 0xF2;

        /// <summary>Yaw the HorizontalOnly converter must produce for storage index 2 (West, identity).</summary>
        private const byte EXPECTED_HORIZONTAL_YAW = 2;

        /// <summary>Legacy skylight nibble on the light carrier (bits 16–19 pre-v9→v10).</summary>
        private const byte LEGACY_SUN_LEVEL = 12;

        /// <summary>Legacy blocklight nibble on the light carrier (bits 20–23), synthesized to gray RGB.</summary>
        private const byte LEGACY_BLOCK_LEVEL = 7;

        /// <summary>Legacy skylight bit position in the era voxel word (v8→v9's <c>SUNLIGHT_SHIFT</c>).</summary>
        private const int ERA_SUNLIGHT_SHIFT = 16;

        /// <summary>Legacy blocklight bit position in the era voxel word (v8→v9's <c>BLOCKLIGHT_SHIFT</c>).</summary>
        private const int ERA_BLOCKLIGHT_SHIFT = 20;

        /// <summary>Meta bit position, unchanged from the era to today (<c>BurstVoxelDataBitMapping.META_SHIFT</c>).</summary>
        private const int ERA_META_SHIFT = 24;

        /// <summary>Heightmap value seeded at column 0, to prove the v1 byte→ushort widening preserves values.</summary>
        private const ushort FIXTURE_HEIGHT_COLUMN_0 = 100;

        /// <summary>Heightmap value seeded at column 1.</summary>
        private const ushort FIXTURE_HEIGHT_COLUMN_1 = 77;

        /// <summary>Skylight queue entries the fixture seeds, to drive v7→v8's 13→16 byte widening.</summary>
        private const int FIXTURE_SUN_QUEUE_ENTRIES = 2;

        /// <summary>Blocklight queue entries the fixture seeds.</summary>
        private const int FIXTURE_BLOCK_QUEUE_ENTRIES = 1;

        // --- Builder -------------------------------------------------------------------------------

        /// <summary>
        /// Builds a complete chunk payload in the requested historical format.
        /// </summary>
        /// <param name="chunkFormatVersion">1 or 2 — the only eras this builder authors. v1 differs from v2
        /// only in its 256-byte (byte-per-column) heightmap; both carry the needs-lighting flag.</param>
        /// <param name="chunkIndexX">Chunk index X the payload is stamped with, written as a voxel-space origin.</param>
        /// <param name="chunkIndexZ">Chunk index Z the payload is stamped with, written as a voxel-space origin.</param>
        /// <returns>The uncompressed payload, ready to hand to a migration step's <c>MigrateChunk</c>.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown for any era this builder does not author.</exception>
        private static byte[] BuildHistoricalChunkPayload(
            byte chunkFormatVersion,
            int chunkIndexX = FIXTURE_CHUNK_X,
            int chunkIndexZ = FIXTURE_CHUNK_Z)
        {
            if (chunkFormatVersion is not (1 or 2))
                throw new ArgumentOutOfRangeException(nameof(chunkFormatVersion),
                    "Only chunk formats 1 and 2 are authored — later eras are produced by the chain itself.");

            using MemoryStream ms = new MemoryStream();
            using BinaryWriter w = new BinaryWriter(ms);

            // --- Header (v2→v3's READ DEFINITION, in order) ---
            w.Write(chunkFormatVersion);
            w.Write(chunkIndexX * ERA_CHUNK_WIDTH);
            w.Write(chunkIndexZ * ERA_CHUNK_WIDTH);
            w.Write(false); // needsLight — the v2→v3 step forces this true, which B15 does not assert on.

            // --- Heightmap ---
            if (chunkFormatVersion == 1)
            {
                byte[] hm = new byte[ERA_V1_HEIGHTMAP_BYTES];
                hm[0] = (byte)FIXTURE_HEIGHT_COLUMN_0;
                hm[1] = (byte)FIXTURE_HEIGHT_COLUMN_1;
                w.Write(hm);
            }
            else
            {
                byte[] hm = new byte[ERA_V2_HEIGHTMAP_BYTES];
                BitConverter.GetBytes(FIXTURE_HEIGHT_COLUMN_0).CopyTo(hm, 0);
                BitConverter.GetBytes(FIXTURE_HEIGHT_COLUMN_1).CopyTo(hm, 2);
                w.Write(hm);
            }

            // --- Section bitmask: the low FIXTURE_SECTION_COUNT bits ---
            const int bitmask = (1 << FIXTURE_SECTION_COUNT) - 1;
            w.Write(bitmask);

            // --- Sections ---
            for (int s = 0; s < FIXTURE_SECTION_COUNT; s++)
            {
                w.Write(ERA_SECTION_VERSION);
                w.Write(s == 0 ? FIXTURE_NON_AIR_COUNT : (ushort)0);
                w.Write(BuildEraSectionVoxels(s));
            }

            // --- Light queues (13-byte entries; v7→v8 widens them to 16) ---
            WriteEraLightQueue(w, FIXTURE_SUN_QUEUE_ENTRIES, baseLevel: 15);
            WriteEraLightQueue(w, FIXTURE_BLOCK_QUEUE_ENTRIES, baseLevel: 9);

            return ms.ToArray();
        }

        /// <summary>
        /// Builds one section's raw voxel block. Section 0 carries the seeded probes; the rest are air.
        /// </summary>
        /// <param name="sectionIndex">Index of the section being built.</param>
        /// <returns>Exactly <see cref="ERA_SECTION_VOXEL_BYTES"/> bytes.</returns>
        private static byte[] BuildEraSectionVoxels(int sectionIndex)
        {
            uint[] voxels = new uint[ERA_VOXELS_PER_SECTION];

            if (sectionIndex == 0)
            {
                voxels[SLOT_FACADE] = EraVoxel(V5_ID_FACADE, LEGACY_META_FORCED_TO_ZERO);
                voxels[SLOT_HALF_SLAB] = EraVoxel(V5_ID_STONE_HALF_SLAB, LEGACY_META_VERBATIM);
                voxels[SLOT_OAK_LOG] = EraVoxel(V5_ID_OAK_LOG, LEGACY_META_AXIS3_NORTH);
                voxels[SLOT_WATER] = EraVoxel(V5_ID_WATER, 3);
                voxels[SLOT_STONE] = EraVoxel(V5_ID_STONE, LEGACY_META_HORIZONTAL_INPUT);
                voxels[SLOT_LIGHT_CARRIER] =
                    EraVoxel(V5_ID_STONE, 0, LEGACY_SUN_LEVEL, LEGACY_BLOCK_LEVEL);
            }

            byte[] bytes = new byte[ERA_SECTION_VOXEL_BYTES];
            Buffer.BlockCopy(voxels, 0, bytes, 0, ERA_SECTION_VOXEL_BYTES);
            return bytes;
        }

        /// <summary>
        /// Packs one era voxel word: id in bits 0–15, legacy sun in 16–19, legacy block in 20–23, meta in 24–31.
        /// The light nibbles are what v8→v9 lifts into <c>LightData</c> and v9→v10 then strips.
        /// </summary>
        /// <param name="id">Frozen v5-era block id.</param>
        /// <param name="legacyMeta">The pre-schema meta byte.</param>
        /// <param name="sun">Legacy skylight nibble (0–15).</param>
        /// <param name="block">Legacy blocklight nibble (0–15).</param>
        /// <returns>The packed era voxel word.</returns>
        private static uint EraVoxel(ushort id, byte legacyMeta, byte sun = 0, byte block = 0) =>
            id
            | ((uint)(sun & 0xF) << ERA_SUNLIGHT_SHIFT)
            | ((uint)(block & 0xF) << ERA_BLOCKLIGHT_SHIFT)
            | ((uint)legacyMeta << ERA_META_SHIFT);

        /// <summary>Writes a pre-v8 light queue: an int32 count followed by 13-byte entries.</summary>
        /// <param name="w">Writer positioned at the queue.</param>
        /// <param name="count">Entries to write.</param>
        /// <param name="baseLevel">Level written on the first entry; subsequent entries step down.</param>
        private static void WriteEraLightQueue(BinaryWriter w, int count, byte baseLevel)
        {
            w.Write(count);
            for (int i = 0; i < count; i++)
            {
                w.Write(i); // x
                w.Write(i + 1); // y
                w.Write(i + 2); // z
                w.Write((byte)(baseLevel - i));
            }
        }

        /// <summary>
        /// SHA-256 of a payload, hex-encoded — the golden pin that makes an accidental edit to this builder
        /// fail loudly instead of silently substituting a different fixture for every scenario.
        /// </summary>
        /// <param name="payload">Bytes to hash.</param>
        /// <returns>Lower-case hex digest.</returns>
        private static string HashPayload(byte[] payload)
        {
            using SHA256 sha = SHA256.Create();
            byte[] digest = sha.ComputeHash(payload);
            StringBuilder sb = new StringBuilder(digest.Length * 2);
            foreach (byte b in digest) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}
