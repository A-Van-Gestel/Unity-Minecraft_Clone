using System.IO;
using Data;
using UnityEngine;

namespace Serialization.Migration.Steps
{
    /// <summary>
    /// Migrates world version 5 → 6 by converting every voxel's metadata byte from the legacy
    /// "lower-3-bit storage index" encoding to a schema-aware encoding chosen per block ID.
    /// Per <c>PER_BLOCK_METADATA_SCHEMAS.md §9.5</c>, §9.6, and §9.7.
    /// </summary>
    /// <remarks>
    /// <para><b>First chunk-format migration in the project.</b> Bumps the chunk format version
    /// from 3 to 4. The chunk binary <i>layout</i> is unchanged — only the meaning of certain
    /// metadata bytes changes — but the version byte still flips so post-migration chunks can be
    /// distinguished from pre-migration chunks.</para>
    ///
    /// <para><b>Schema assignment per block</b> (frozen for v5):</para>
    /// <list type="table">
    ///   <listheader><term>Block</term><description>Target schema → meta-byte conversion</description></listheader>
    ///   <item><term>Air, Facade, Cactus, GrassBlades</term><description><b>None</b> — meta byte rewritten to <c>0</c>.</description></item>
    ///   <item><term>Water, Lava</term><description><b>FluidLevel4</b> — lower 4 bits kept, upper 4 cleared.</description></item>
    ///   <item><term>OakLog</term><description><b>Axis3</b> — all legacy oak-log orientations normalize to <c>Y</c> because v5 oak logs were always authored/rendered as upright cubes; the old yaw byte never encoded a real axis.</description></item>
    ///   <item><term>Stone, Grass, Dirt, Sand, Snow, GrassSnowy, StoneWalkway, Bedrock, DesertCracked, GrassRocky, Tile, Wood, OakLeaves, CoalOre</term><description><b>HorizontalOnly</b> — storage indices 0-3 kept verbatim (the 4 horizontal cases align bit-for-bit with the new layout); storage indices 4-5 (Top/Bottom) clamped to 0 (North).</description></item>
    ///   <item><term>StoneHalfSlab, DirectionalBlock</term><description><b>None</b> for now — schema choice deferred to a future v6→v7 migration when Facing6/Facing6Roll2 meshing exists. Meta byte left verbatim.</description></item>
    ///   <item><term>Unknown block IDs</term><description>Treated as deferred — meta byte left verbatim. Future-proofs against forks/mods.</description></item>
    /// </list>
    ///
    /// <para><b>Frozen identity</b> per §9.3: every block ID this migration cares about is
    /// hardcoded as a <c>const</c> rather than read from the live <c>BlockIDs</c> class. If a
    /// future <c>BlockDatabase</c> reordering shifts runtime IDs, this migration still operates
    /// on the correct historical bytes.</para>
    ///
    /// <para><b>Frozen mappings</b>: every storage-index → axis / yaw / fluid-level conversion
    /// table is duplicated inline rather than calling into the live encode/decode utilities, so
    /// future edits to those classes cannot retroactively change this migration's output.</para>
    ///
    /// <para><b>Two payloads migrated</b>:</para>
    /// <list type="bullet">
    ///   <item><description>Chunks (<see cref="MigrateChunk"/>): every voxel in every section gets routed through <see cref="ConvertLegacyMeta"/>.</description></item>
    ///   <item><description>Pending mods (<see cref="MigratePendingMods"/>): every mod's meta byte gets routed through the same converter. Required by §9.7 to keep persisted state coherent.</description></item>
    /// </list>
    ///
    /// <para><b>BlockDatabase update is the user's responsibility</b> — after this migration ships,
    /// the user must set each block's <c>metadataSchema</c> in the BlockEditor according to the
    /// table above. Until that change lands, blocks still route through the legacy meshing path
    /// (which produces correct output for HorizontalOnly bytes by design — see §5.3 frozen-layout
    /// note about the legacy-aligned bit layout).</para>
    /// </remarks>
    public class MigrationV5ToV6LegacyToSchemaBased : WorldMigrationStep
    {
        public override int SourceWorldVersion => 5;
        public override int TargetWorldVersion => 6;
        public override string Description => "Converting voxel metadata to schema-based encoding";

        public override string ChangeSummary =>
            "Rewrites every voxel's metadata byte to the schema-aware encoding chosen for that " +
            "block ID. OakLog → Axis3, Water/Lava → FluidLevel4, ordinary cubes → HorizontalOnly, " +
            "Air/Facade/Cactus/GrassBlades → None. StoneHalfSlab and DirectionalBlock kept on " +
            "legacy semantics; their schema migration is deferred to a future version.";

        // ── Frozen v5 block-ID snapshot (per §9.3) ───────────────────────────
        // Sourced from BlockIDs.cs as of save-version 5 / branch feat/per-block-metadata-schemas.

        private const ushort V5_AIR = 0;
        private const ushort V5_STONE = 1;
        private const ushort V5_GRASS = 2;
        private const ushort V5_DIRT = 3;
        private const ushort V5_SAND = 4;
        private const ushort V5_SNOW = 5;
        private const ushort V5_GRASS_SNOWY = 6;
        private const ushort V5_STONE_WALKWAY = 7;
        private const ushort V5_BEDROCK = 8;
        private const ushort V5_DESERT_CRACKED = 9;
        private const ushort V5_GRASS_ROCKY = 10;
        private const ushort V5_TILE = 11;
        private const ushort V5_WOOD = 12;
        private const ushort V5_FACADE = 13;
        private const ushort V5_OAK_LOG = 14;
        private const ushort V5_OAK_LEAVES = 15;
        private const ushort V5_CACTUS = 16;
        private const ushort V5_STONE_HALF_SLAB = 17;
        private const ushort V5_DIRECTIONAL = 18;
        private const ushort V5_WATER = 19;
        private const ushort V5_LAVA = 20;
        private const ushort V5_COAL_ORE = 21;
        private const ushort V5_GRASS_BLADES = 22;

        // ── Frozen target-schema markers (private to this migration) ─────────
        // Use byte literals rather than the live `MetadataSchema` enum so future enum renames /
        // additions cannot retroactively change this migration's behavior. Values match the v5
        // enum byte values; if the enum is ever reassigned, that's a separate migration concern.

        private const byte SCHEMA_NONE = 0;
        private const byte SCHEMA_FLUID_LEVEL_4 = 1;

        private const byte SCHEMA_AXIS3 = 2;

        // SCHEMA_FACING6        = 3 (deferred)
        // SCHEMA_FACING6_ROLL2  = 4 (deferred)
        private const byte SCHEMA_HORIZONTAL_ONLY = 5;
        private const byte SCHEMA_KEEP_LEGACY = 0xFF; // sentinel — leave meta byte unchanged

        // ── Frozen v3 chunk format constants ─────────────────────────────────

        private const byte SOURCE_CHUNK_VERSION = 3;
        private const byte TARGET_CHUNK_VERSION_VAL = 4;

        private const int V3_VOXELS_PER_SECTION = 4096;
        private const int V3_SECTIONS_PER_CHUNK = 8;
        private const int V3_HEIGHTMAP_BYTES = 16 * 16 * sizeof(ushort);
        private const byte V3_SECTION_VERSION = 1;

        /// <inheritdoc />
        public override byte? TargetChunkFormatVersion => TARGET_CHUNK_VERSION_VAL;

        // ── Migration entry points ───────────────────────────────────────────

        /// <inheritdoc />
        public override byte[] MigrateChunk(byte[] uncompressedChunkData)
        {
            using MemoryStream inMs = new MemoryStream(uncompressedChunkData);
            using BinaryReader reader = new BinaryReader(inMs);
            using MemoryStream outMs = new MemoryStream(capacity: uncompressedChunkData.Length);
            using BinaryWriter writer = new BinaryWriter(outMs);

            // --- Header ---
            byte oldVersion = reader.ReadByte();
            int chunkX = reader.ReadInt32();
            int chunkZ = reader.ReadInt32();
            bool needsInitialLighting = reader.ReadBoolean();
            byte[] heightMap = reader.ReadBytes(V3_HEIGHTMAP_BYTES);
            int sectionBitmask = reader.ReadInt32();

            // Write new version byte first (required by AOT migration protocol).
            writer.Write(TARGET_CHUNK_VERSION_VAL);
            writer.Write(chunkX);
            writer.Write(chunkZ);
            writer.Write(needsInitialLighting);
            writer.Write(heightMap);
            writer.Write(sectionBitmask);

            // --- Sections ---
            int totalRewrites = 0;
            // Iterate up to 32 times because the sectionBitmask is a 32-bit integer.
            // This properly handles chunks that were saved with a different ChunkHeight
            // (e.g., height 256 = 16 sections), preventing stream misalignment.
            for (int s = 0; s < 32; s++)
            {
                if ((sectionBitmask & (1 << s)) == 0) continue;

                byte sectionVersion = reader.ReadByte();
                ushort nonAirCount = reader.ReadUInt16();
                writer.Write(sectionVersion);
                writer.Write(nonAirCount);

                if (sectionVersion != V3_SECTION_VERSION)
                {
                    Debug.LogWarning(
                        $"[MigrationV5ToV6LegacyToSchemaBased] Unexpected section version {sectionVersion} " +
                        $"in chunk ({chunkX},{chunkZ}) section {s}. Voxel rewrites still attempted.");
                }

                for (int i = 0; i < V3_VOXELS_PER_SECTION; i++)
                {
                    uint voxel = reader.ReadUInt32();
                    ushort id = (ushort)(voxel & 0xFFFF);
                    byte oldMeta = (byte)((voxel >> 24) & 0xFF);
                    byte newMeta = ConvertLegacyMeta(id, oldMeta);

                    if (newMeta != oldMeta)
                    {
                        voxel = (voxel & 0x00FFFFFFu) | ((uint)newMeta << 24);
                        totalRewrites++;
                    }

                    writer.Write(voxel);
                }
            }

            // --- Light queues ---
            CopyLightQueueVerbatim(reader, writer);
            CopyLightQueueVerbatim(reader, writer);

            if (totalRewrites > 0)
            {
                Debug.Log(
                    $"[MigrationV5ToV6LegacyToSchemaBased] Rewrote {totalRewrites} voxel meta byte(s) " +
                    $"in chunk ({chunkX},{chunkZ})");
            }

            return outMs.ToArray();
        }

        /// <inheritdoc />
        public override byte[] MigratePendingMods(byte[] rawOldData)
        {
            if (rawOldData == null || rawOldData.Length == 0)
                return rawOldData;

            using MemoryStream inMs = new MemoryStream(rawOldData);
            using BinaryReader reader = new BinaryReader(inMs);
            using MemoryStream outMs = new MemoryStream(capacity: rawOldData.Length);
            using BinaryWriter writer = new BinaryWriter(outMs);

            int totalMods = 0;
            int rewrittenMods = 0;

            int chunkCount = reader.ReadInt32();
            writer.Write(chunkCount);

            for (int c = 0; c < chunkCount; c++)
            {
                int chunkX = reader.ReadInt32();
                int chunkZ = reader.ReadInt32();
                int modCount = reader.ReadInt32();
                writer.Write(chunkX);
                writer.Write(chunkZ);
                writer.Write(modCount);

                for (int m = 0; m < modCount; m++)
                {
                    // v5 mod payload: [posX i32][posY i32][posZ i32][id u16][meta u8]
                    int posX = reader.ReadInt32();
                    int posY = reader.ReadInt32();
                    int posZ = reader.ReadInt32();
                    ushort id = reader.ReadUInt16();
                    byte oldMeta = reader.ReadByte();
                    byte newMeta = ConvertLegacyMeta(id, oldMeta);

                    if (newMeta != oldMeta) rewrittenMods++;

                    writer.Write(posX);
                    writer.Write(posY);
                    writer.Write(posZ);
                    writer.Write(id);
                    writer.Write(newMeta);

                    totalMods++;
                }
            }

            Debug.Log(
                $"[MigrationV5ToV6LegacyToSchemaBased] Rewrote {rewrittenMods}/{totalMods} pending mod meta byte(s).");

            return outMs.ToArray();
        }

        // ── Public per-voxel converter (testability) ─────────────────────────

        /// <summary>
        /// Frozen converter from a legacy v3-chunk meta byte to the schema-aware meta byte chosen
        /// for the given v5 block ID. Routes to one of the per-schema conversion helpers below.
        /// </summary>
        /// <remarks>
        /// Public so the editor-time validator can verify the per-block mapping. Once shipped,
        /// this method's behavior must never change — see §9.6 schema-to-schema migration rule.
        /// </remarks>
        public static byte ConvertLegacyMeta(ushort blockId, byte legacyMeta)
        {
            if (blockId == V5_OAK_LOG)
            {
                return ConvertLegacyOakLogMetaToAxis3(legacyMeta);
            }

            byte targetSchema = GetTargetSchema(blockId);
            return targetSchema switch
            {
                SCHEMA_NONE => 0, // No metadata; meta byte forced to 0.
                SCHEMA_FLUID_LEVEL_4 => ConvertLegacyToFluidLevel4(legacyMeta),
                SCHEMA_AXIS3 => ConvertLegacyMetaToAxis3(legacyMeta),
                SCHEMA_HORIZONTAL_ONLY => ConvertLegacyToHorizontalOnly(legacyMeta),
                SCHEMA_KEEP_LEGACY => legacyMeta, // Deferred — leave verbatim for a future migration.
                _ => legacyMeta, // Unknown schema sentinel — defensive passthrough.
            };
        }

        /// <summary>
        /// Frozen v5 block-ID → target-schema lookup. Block IDs not listed map to
        /// <see cref="SCHEMA_KEEP_LEGACY"/> (verbatim passthrough) so unknown blocks from forks /
        /// future versions don't get their meta bytes silently zeroed.
        /// </summary>
        private static byte GetTargetSchema(ushort blockId)
        {
            return blockId switch
            {
                // None — no metadata
                V5_AIR => SCHEMA_NONE,
                V5_FACADE => SCHEMA_NONE,
                V5_CACTUS => SCHEMA_NONE,
                V5_GRASS_BLADES => SCHEMA_NONE,

                // FluidLevel4 — fluid blocks
                V5_WATER => SCHEMA_FLUID_LEVEL_4,
                V5_LAVA => SCHEMA_FLUID_LEVEL_4,

                // Axis3 — pillar / log
                V5_OAK_LOG => SCHEMA_AXIS3,

                // HorizontalOnly — ordinary solid cubes that benefit from yaw variety
                V5_STONE => SCHEMA_HORIZONTAL_ONLY,
                V5_GRASS => SCHEMA_HORIZONTAL_ONLY,
                V5_DIRT => SCHEMA_HORIZONTAL_ONLY,
                V5_SAND => SCHEMA_HORIZONTAL_ONLY,
                V5_SNOW => SCHEMA_HORIZONTAL_ONLY,
                V5_GRASS_SNOWY => SCHEMA_HORIZONTAL_ONLY,
                V5_STONE_WALKWAY => SCHEMA_HORIZONTAL_ONLY,
                V5_BEDROCK => SCHEMA_HORIZONTAL_ONLY,
                V5_DESERT_CRACKED => SCHEMA_HORIZONTAL_ONLY,
                V5_GRASS_ROCKY => SCHEMA_HORIZONTAL_ONLY,
                V5_TILE => SCHEMA_HORIZONTAL_ONLY,
                V5_WOOD => SCHEMA_HORIZONTAL_ONLY,
                V5_OAK_LEAVES => SCHEMA_HORIZONTAL_ONLY,
                V5_COAL_ORE => SCHEMA_HORIZONTAL_ONLY,

                // Deferred — Facing6 / Facing6Roll2 candidates whose meshing is not yet implemented.
                V5_STONE_HALF_SLAB => SCHEMA_KEEP_LEGACY,
                V5_DIRECTIONAL => SCHEMA_KEEP_LEGACY,

                // Anything else (fork/mod additions, future blocks not yet on disk) → leave alone.
                _ => SCHEMA_KEEP_LEGACY,
            };
        }

        // ── Frozen per-schema converters ─────────────────────────────────────

        /// <summary>
        /// Frozen converter from a legacy v3-chunk meta byte (lower 3 bits = orientation storage
        /// index) to the new <see cref="MetadataSchema.Axis3"/> meta byte (lower 2 bits = axis:
        /// 0=Y, 1=X, 2=Z).
        /// </summary>
        /// <remarks>
        /// Public so the editor-time validator can verify the mapping. Once shipped, this method's
        /// behavior must never change — see §9.6 schema-to-schema migration rule.
        /// </remarks>
        public static byte ConvertLegacyMetaToAxis3(byte legacyMeta)
        {
            byte storageIndex = (byte)(legacyMeta & 0x07);
            return storageIndex switch
            {
                // §9.5.A: N/S → Z, E/W → X, T/B/default → Y.
                0 => 2, // North → Z
                1 => 2, // South → Z
                2 => 1, // West  → X
                3 => 1, // East  → X
                4 => 0, // Top   → Y
                5 => 0, // Bottom → Y
                _ => 0, // invalid → Y (fallback per §9.5.D)
            };
        }

        /// <summary>
        /// Frozen converter for v5 OakLog specifically.
        /// </summary>
        /// <remarks>
        /// <para>Although OakLog migrates to <see cref="Data.MetadataSchema.Axis3"/>, historical v5 OakLog
        /// voxels never stored a meaningful axis. Trees and the old placement path authored oak logs as
        /// ordinary upright cubes using the legacy yaw byte, so every legacy orientation value rendered as
        /// the same upright log.</para>
        /// <para>To preserve the appearance of existing worlds, every v5 OakLog meta byte normalizes to
        /// Axis3.Y during migration.</para>
        /// </remarks>
        public static byte ConvertLegacyOakLogMetaToAxis3(byte legacyMeta)
        {
            return 0;
        }

        /// <summary>
        /// Frozen converter from a legacy v3-chunk meta byte (lower 3 bits = orientation storage
        /// index) to the new <see cref="MetadataSchema.HorizontalOnly"/> meta byte (lower 2 bits =
        /// 4-way yaw: 0=N, 1=S, 2=W, 3=E).
        /// </summary>
        /// <remarks>
        /// <para>By design, the HorizontalOnly bit layout is aligned with the legacy storage indices
        /// for the four horizontal cases (0-3), so the converter is the identity for those.
        /// Storage indices 4-5 (Top/Bottom — never sensible for an ordinary cube) and 6-7 (invalid)
        /// clamp to 0 (North).</para>
        /// </remarks>
        public static byte ConvertLegacyToHorizontalOnly(byte legacyMeta)
        {
            byte storageIndex = (byte)(legacyMeta & 0x07);
            return storageIndex switch
            {
                0 => 0, // North (identity)
                1 => 1, // South (identity)
                2 => 2, // West  (identity)
                3 => 3, // East  (identity)
                _ => 0, // Top / Bottom / invalid → North fallback
            };
        }

        /// <summary>
        /// Frozen converter from a legacy v3-chunk fluid-block meta byte to the new
        /// <see cref="MetadataSchema.FluidLevel4"/> meta byte. Lower 4 bits already hold the fluid
        /// level under both the legacy and new encodings; this just masks off any stray reserved bits.
        /// </summary>
        public static byte ConvertLegacyToFluidLevel4(byte legacyMeta) => (byte)(legacyMeta & 0x0F);

        // ── Light queue passthrough helper ───────────────────────────────────

        /// <summary>
        /// Reads one light queue from <paramref name="reader"/> and writes it verbatim to
        /// <paramref name="writer"/>. Light queues store positions and old light levels — no voxel
        /// meta — so they pass through unchanged.
        /// </summary>
        private static void CopyLightQueueVerbatim(BinaryReader reader, BinaryWriter writer)
        {
            int count = reader.ReadInt32();
            
            // Sanity check to prevent OOM or EndOfStreamException on corrupt/misaligned data
            if (count < 0 || count > 100_000)
                throw new InvalidDataException($"[MigrationV5ToV6] Invalid LightQueue count: {count}. Chunk stream is likely misaligned.");
                
            writer.Write(count);
            for (int i = 0; i < count; i++)
            {
                writer.Write(reader.ReadInt32()); // x
                writer.Write(reader.ReadInt32()); // y
                writer.Write(reader.ReadInt32()); // z
                writer.Write(reader.ReadByte()); // level
            }
        }
    }
}
