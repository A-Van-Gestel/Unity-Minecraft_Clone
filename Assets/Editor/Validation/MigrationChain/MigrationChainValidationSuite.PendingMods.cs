using System.IO;
using Serialization.Migration.Steps;

namespace Editor.Validation.MigrationChain
{
    /// <summary>
    /// The <c>pending_mods.bin</c> half of roadmap NS-7b: the two steps that rewrite queued voxel
    /// modifications — v4→v5 (collapse <c>orientation</c> + <c>fluidLevel</c> into one meta byte) and v5→v6
    /// (re-encode that meta byte per the block's metadata schema).
    /// <para>
    /// Both are driven directly through <c>MigratePendingMods</c> with authored blobs. No disk is involved:
    /// the manager's global-file loop is already covered by <c>B6</c>, and what has never been exercised is
    /// the byte transform itself.
    /// </para>
    /// </summary>
    public static partial class MigrationChainValidationSuite
    {
        /// <summary>v4 mod record: 3×int32 position + ushort id + orientation byte + fluid byte.</summary>
        private const int V4_MOD_RECORD_BYTES = 4 + 4 + 4 + 2 + 1 + 1;

        /// <summary>v5 mod record: the same, with orientation+fluid collapsed into one meta byte.</summary>
        private const int V5_MOD_RECORD_BYTES = 4 + 4 + 4 + 2 + 1;

        /// <summary>Blob header: an int32 chunk count, then per chunk an int32 X, Z and mod count.</summary>
        private const int MOD_BLOB_HEADER_BYTES = 4 + 4 + 4 + 4;

        /// <summary>v4-era Lava — the second of the two frozen fluid ids in <c>s_v4FluidBlockIds</c>.</summary>
        private const ushort V5_ID_LAVA = 20;

        /// <summary>World orientation 4 (Left/West), which the frozen v4 table maps to storage index 2.</summary>
        private const byte LEGACY_ORIENTATION_WEST = 4;

        /// <summary>Storage index the frozen v4 table produces for orientation 4 (West).</summary>
        private const byte EXPECTED_WEST_STORAGE_INDEX = 2;

        /// <summary>Chunk coordinates the mod fixture's single chunk entry carries.</summary>
        private const int MOD_CHUNK_X = 7, MOD_CHUNK_Z = -9;

        // --- Fixture builders ----------------------------------------------------------------------

        /// <summary>
        /// Builds a v4 <c>pending_mods.bin</c> blob whose four records cover every arm of the v4→v5 rule:
        /// a fluid id with a level, a fluid id at level 0 (still fluid-encoded, by id), a non-fluid taking the
        /// orientation path, and a non-fluid with a stray non-zero fluid level (which the rule lets win).
        /// </summary>
        /// <returns>The authored blob.</returns>
        private static byte[] BuildV4PendingModsBlob()
        {
            using MemoryStream ms = new MemoryStream();
            using BinaryWriter w = new BinaryWriter(ms);

            w.Write(1); // chunkCount
            w.Write(MOD_CHUNK_X);
            w.Write(MOD_CHUNK_Z);
            w.Write(4); // modCount

            WriteV4Mod(w, 10, 20, 30, V5_ID_WATER, LEGACY_ORIENTATION_WEST, 5);
            WriteV4Mod(w, 11, 21, 31, V5_ID_LAVA, LEGACY_ORIENTATION_WEST, 0);
            WriteV4Mod(w, 12, 22, 32, V5_ID_STONE, LEGACY_ORIENTATION_WEST, 0);
            WriteV4Mod(w, 13, 23, 33, V5_ID_STONE, 1, 7);

            return ms.ToArray();
        }

        /// <summary>
        /// Builds a v5 blob whose three records cover the distinct schema arms of the v5→v6 rewrite:
        /// SCHEMA_NONE (forced to 0), SCHEMA_KEEP_LEGACY (verbatim), and HorizontalOnly (masked).
        /// </summary>
        /// <returns>The authored blob.</returns>
        private static byte[] BuildV5PendingModsBlob()
        {
            using MemoryStream ms = new MemoryStream();
            using BinaryWriter w = new BinaryWriter(ms);

            w.Write(1);
            w.Write(MOD_CHUNK_X);
            w.Write(MOD_CHUNK_Z);
            w.Write(3);

            WriteV5Mod(w, 10, 20, 30, V5_ID_FACADE, LEGACY_META_FORCED_TO_ZERO);
            WriteV5Mod(w, 11, 21, 31, V5_ID_STONE_HALF_SLAB, LEGACY_META_VERBATIM);
            WriteV5Mod(w, 12, 22, 32, V5_ID_STONE, LEGACY_META_HORIZONTAL_INPUT);

            return ms.ToArray();
        }

        /// <summary>Writes one v4 mod record.</summary>
        /// <param name="w">Target writer.</param>
        /// <param name="x">Voxel X.</param>
        /// <param name="y">Voxel Y.</param>
        /// <param name="z">Voxel Z.</param>
        /// <param name="id">Frozen v4-era block id.</param>
        /// <param name="orientation">World orientation byte.</param>
        /// <param name="fluidLevel">Fluid level byte.</param>
        private static void WriteV4Mod(BinaryWriter w, int x, int y, int z, ushort id, byte orientation, byte fluidLevel)
        {
            w.Write(x);
            w.Write(y);
            w.Write(z);
            w.Write(id);
            w.Write(orientation);
            w.Write(fluidLevel);
        }

        /// <summary>Writes one v5 mod record.</summary>
        /// <param name="w">Target writer.</param>
        /// <param name="x">Voxel X.</param>
        /// <param name="y">Voxel Y.</param>
        /// <param name="z">Voxel Z.</param>
        /// <param name="id">Frozen v5-era block id.</param>
        /// <param name="meta">The collapsed meta byte.</param>
        private static void WriteV5Mod(BinaryWriter w, int x, int y, int z, ushort id, byte meta)
        {
            w.Write(x);
            w.Write(y);
            w.Write(z);
            w.Write(id);
            w.Write(meta);
        }

        /// <summary>Reads the meta byte of the record at <paramref name="index"/> from a v5-shaped blob.</summary>
        /// <param name="blob">A v5/v6-shaped pending-mods blob.</param>
        /// <param name="index">Zero-based record index within the single chunk entry.</param>
        /// <returns>The record's meta byte.</returns>
        private static byte ReadV5ModMeta(byte[] blob, int index) =>
            blob[MOD_BLOB_HEADER_BYTES + index * V5_MOD_RECORD_BYTES + 14];

        /// <summary>Reads the block id of the record at <paramref name="index"/> from a v5-shaped blob.</summary>
        /// <param name="blob">A v5/v6-shaped pending-mods blob.</param>
        /// <param name="index">Zero-based record index.</param>
        /// <returns>The record's block id.</returns>
        private static ushort ReadV5ModId(byte[] blob, int index) =>
            (ushort)(blob[MOD_BLOB_HEADER_BYTES + index * V5_MOD_RECORD_BYTES + 12]
                     | (blob[MOD_BLOB_HEADER_BYTES + index * V5_MOD_RECORD_BYTES + 13] << 8));

        // --- Scenarios -----------------------------------------------------------------------------

        /// <summary>B22. Red when: the v4→v5 collapse stops keying the fluid arm off the frozen v4 fluid-id
        /// snapshot, mis-maps an orientation to its storage index, or shifts the record stride. A stride error
        /// here silently rewrites the wrong block at the wrong position for every queued edit in the save.</summary>
        private static bool PendingModsV4ToV5()
        {
            byte[] v4 = BuildV4PendingModsBlob();
            bool ok = Check($"the authored v4 blob is the length the layout predicts, got {v4.Length.ToString()}",
                v4.Length == MOD_BLOB_HEADER_BYTES + 4 * V4_MOD_RECORD_BYTES);

            byte[] v5 = new MigrationV4ToV5VoxelModMeta().MigratePendingMods(v4);

            ok &= Check($"each record loses one byte (16→15), got a {v5.Length.ToString()}-byte blob",
                v5.Length == MOD_BLOB_HEADER_BYTES + 4 * V5_MOD_RECORD_BYTES);
            ok &= Check("the chunk count survives", v5[0] == 1);

            // Ids must survive so the fluid/orientation decision stays attributable to the right block.
            ok &= Check($"record 0 keeps the Water id, got {ReadV5ModId(v5, 0).ToString()}", ReadV5ModId(v5, 0) == V5_ID_WATER);
            ok &= Check($"record 2 keeps the Stone id, got {ReadV5ModId(v5, 2).ToString()}", ReadV5ModId(v5, 2) == V5_ID_STONE);

            ok &= Check($"a fluid id with level 5 encodes meta 5, got {ReadV5ModMeta(v5, 0).ToString()}",
                ReadV5ModMeta(v5, 0) == 5);
            ok &= Check($"a fluid id at level 0 still takes the fluid arm (meta 0), got {ReadV5ModMeta(v5, 1).ToString()}",
                ReadV5ModMeta(v5, 1) == 0);
            ok &= Check($"a non-fluid encodes its orientation storage index {EXPECTED_WEST_STORAGE_INDEX.ToString()}, got {ReadV5ModMeta(v5, 2).ToString()}",
                ReadV5ModMeta(v5, 2) == EXPECTED_WEST_STORAGE_INDEX);
            ok &= Check($"a non-fluid carrying a stray fluid level takes the fluid arm (meta 7), got {ReadV5ModMeta(v5, 3).ToString()}",
                ReadV5ModMeta(v5, 3) == 7);

            // An empty blob must pass straight through rather than producing a bare header.
            ok &= Check("an empty blob is returned untouched",
                new MigrationV4ToV5VoxelModMeta().MigratePendingMods(System.Array.Empty<byte>()).Length == 0);
            return ok;
        }

        /// <summary>B23. Red when: the v5→v6 schema rewrite stops applying to pending mods, or applies a
        /// different mapping than the chunk-voxel path does. Both routes share <c>ConvertLegacyMeta</c>, so a
        /// divergence here means a queued edit lands with different metadata than the same edit already
        /// baked into a chunk.</summary>
        private static bool PendingModsV5ToV6()
        {
            byte[] v5 = BuildV5PendingModsBlob();
            byte[] v6 = new MigrationV5ToV6LegacyToSchemaBased().MigratePendingMods(v5);

            bool ok = Check($"the record stride is unchanged, got a {v6.Length.ToString()}-byte blob",
                v6.Length == v5.Length);
            ok &= Check($"SCHEMA_NONE forces the Facade meta to 0, got {ReadV5ModMeta(v6, 0).ToString()}",
                ReadV5ModMeta(v6, 0) == 0);
            ok &= Check($"SCHEMA_KEEP_LEGACY leaves the half-slab meta at {LEGACY_META_VERBATIM.ToString()}, got {ReadV5ModMeta(v6, 1).ToString()}",
                ReadV5ModMeta(v6, 1) == LEGACY_META_VERBATIM);
            ok &= Check($"HorizontalOnly masks 0x{LEGACY_META_HORIZONTAL_INPUT:X2} to {EXPECTED_HORIZONTAL_YAW.ToString()}, got {ReadV5ModMeta(v6, 2).ToString()}",
                ReadV5ModMeta(v6, 2) == EXPECTED_HORIZONTAL_YAW);
            ok &= Check("every id survives the rewrite",
                ReadV5ModId(v6, 0) == V5_ID_FACADE && ReadV5ModId(v6, 1) == V5_ID_STONE_HALF_SLAB &&
                ReadV5ModId(v6, 2) == V5_ID_STONE);

            // The pending-mods and chunk-voxel routes must agree, since they share one converter.
            ok &= Check("the pending-mods rewrite agrees with the per-voxel converter on every record",
                ReadV5ModMeta(v6, 0) == MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyMeta(V5_ID_FACADE, LEGACY_META_FORCED_TO_ZERO) &&
                ReadV5ModMeta(v6, 1) == MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyMeta(V5_ID_STONE_HALF_SLAB, LEGACY_META_VERBATIM) &&
                ReadV5ModMeta(v6, 2) == MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyMeta(V5_ID_STONE, LEGACY_META_HORIZONTAL_INPUT));
            return ok;
        }
    }
}
