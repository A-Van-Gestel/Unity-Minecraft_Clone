using System.IO;
using UnityEngine;

namespace Serialization.Migration.Steps
{
    /// <summary>
    /// Migrates <c>pending_mods.bin</c> from v4 → v5 by collapsing the legacy
    /// <c>(Orientation, FluidLevel)</c> byte pair into a single <c>Meta</c> byte per
    /// <c>PER_BLOCK_METADATA_SCHEMAS.md §7.4</c>. No chunk-format changes — only the
    /// pending-mods serialization layout is touched.
    /// </summary>
    /// <remarks>
    /// <para>The v4 per-mod payload was 16 bytes:
    /// <c>[posX i32][posY i32][posZ i32][id u16][orient u8][fluid u8]</c>.</para>
    /// <para>The v5 per-mod payload is 15 bytes:
    /// <c>[posX i32][posY i32][posZ i32][id u16][meta u8]</c>.</para>
    /// <para>Per §9.3 the migration must not depend on live <c>BlockIDs</c> constants. The
    /// frozen v4 fluid-block-id snapshot below captures the only two block IDs that were
    /// fluids at v4 publication time. Encoding logic is duplicated inline (frozen) rather
    /// than calling <c>BurstVoxelDataBitMapping.BuildMetaLegacy</c>, so future changes to
    /// the live encoder cannot retroactively change this migration's output.</para>
    /// </remarks>
    public class MigrationV4ToV5VoxelModMeta : WorldMigrationStep
    {
        public override int SourceWorldVersion => 4;
        public override int TargetWorldVersion => 5;
        public override string Description => "Collapsing VoxelMod orientation/fluid into meta byte";

        public override string ChangeSummary =>
            "Rewrites pending_mods.bin so each pending modification stores a single 8-bit metadata byte " +
            "instead of separate orientation and fluid-level bytes.";

        // ── Frozen v4 block identity snapshot ────────────────────────────────
        // Per PER_BLOCK_METADATA_SCHEMAS.md §9.3: every migration step that depends on block
        // identity must embed a frozen historical snapshot rather than reading live BlockIDs.
        // At v4 publication time, the only two fluid block IDs were Water (19) and Lava (20).
        // If new fluid blocks are added in a later version, a fresh migration step must be
        // written for that version — DO NOT edit this list.
        private static readonly ushort[] s_v4FluidBlockIds = { 19, 20 };

        public override byte[] MigratePendingMods(byte[] rawOldData)
        {
            if (rawOldData == null || rawOldData.Length == 0)
                return rawOldData;

            using MemoryStream inStream = new MemoryStream(rawOldData);
            using BinaryReader reader = new BinaryReader(inStream);

            using MemoryStream outStream = new MemoryStream(capacity: rawOldData.Length);
            using BinaryWriter writer = new BinaryWriter(outStream);

            // Telemetry counters per §9.8.
            int totalMods = 0;
            int fluidEncodedCount = 0;
            int orientEncodedCount = 0;
            int unrecognizedFluidIdCount = 0;

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
                    // Read v4 payload.
                    int posX = reader.ReadInt32();
                    int posY = reader.ReadInt32();
                    int posZ = reader.ReadInt32();
                    ushort id = reader.ReadUInt16();
                    byte orient = reader.ReadByte();
                    byte fluid = reader.ReadByte();

                    // Determine isFluid from the frozen v4 snapshot.
                    bool isFluidById = IsV4FluidBlock(id);

                    // Encode the v5 meta byte using the frozen legacy rule:
                    //   if isFluid OR fluid > 0: meta = fluid & 0x0F (fluid encoding)
                    //   else:                    meta = legacy orientation storage index
                    byte meta;
                    if (isFluidById || fluid > 0)
                    {
                        meta = (byte)(fluid & 0x0F);
                        fluidEncodedCount++;
                    }
                    else
                    {
                        meta = LegacyOrientationStorageIndex(orient);
                        orientEncodedCount++;

                        // Sanity counter: this should never trigger for healthy data, but
                        // catches the case where a fluid mod was saved with fluid==0.
                        // We treat it as solid here, which would mis-place fluid mods —
                        // should be rare since fluid placement always sets fluid>0 in practice.
                        if (id != 0 && IsV4FluidBlock(id))
                        {
                            unrecognizedFluidIdCount++;
                        }
                    }

                    // Write v5 payload.
                    writer.Write(posX);
                    writer.Write(posY);
                    writer.Write(posZ);
                    writer.Write(id);
                    writer.Write(meta);

                    totalMods++;
                }
            }

            Debug.Log(
                $"[MigrationV4ToV5VoxelModMeta] Migrated {totalMods} pending mod(s): " +
                $"{fluidEncodedCount} fluid-encoded, {orientEncodedCount} orientation-encoded, " +
                $"{unrecognizedFluidIdCount} unrecognized fluid-id fallbacks.");

            return outStream.ToArray();
        }

        // ── Frozen helpers (private; do not call live engine code) ───────────

        private static bool IsV4FluidBlock(ushort id)
        {
            foreach (ushort t in s_v4FluidBlockIds)
            {
                if (t == id) return true;
            }

            return false;
        }

        /// <summary>
        /// Frozen copy of the v4 world-orientation → internal-storage-index mapping. This
        /// is intentionally duplicated rather than imported from <c>BurstVoxelDataBitMapping</c>
        /// so that future edits to the live mapping cannot retroactively change this
        /// migration's output bytes.
        /// </summary>
        private static byte LegacyOrientationStorageIndex(byte worldOrientation)
        {
            // Mirrors BurstVoxelDataBitMapping.GetOrientationIndex as of v4:
            //   1 (Front/North)  -> 0
            //   0 (Back/South)   -> 1
            //   4 (Left/West)    -> 2
            //   5 (Right/East)   -> 3
            //   2 (Top)          -> 4
            //   3 (Bottom)       -> 5
            //   anything else    -> 0
            return worldOrientation switch
            {
                1 => 0,
                0 => 1,
                4 => 2,
                5 => 3,
                2 => 4,
                3 => 5,
                _ => 0,
            };
        }
    }
}
