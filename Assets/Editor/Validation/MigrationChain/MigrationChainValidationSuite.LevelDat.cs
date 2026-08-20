using Data.WorldTypes;
using Serialization;
using UnityEngine;

namespace Editor.Validation.MigrationChain
{
    /// <summary>
    /// The <c>level.dat</c> chain scenarios: a document of a historical version folded through every
    /// registered step's <c>MigrateLevelDat</c> by <see cref="LevelDatCodec"/>, asserting both what each
    /// step injects and what it must carry through untouched.
    /// <para>
    /// These run against no disk at all. The on-disk half of the chain — the version stamp, the global-file
    /// rename, backup/rollback and the chunk loop — is the manager partial's job.
    /// </para>
    /// </summary>
    public static partial class MigrationChainValidationSuite
    {
        /// <summary>B2. Red when: the v12→v13 step drops a field its frozen DTO should carry, or its
        /// position re-type truncates toward zero instead of flooring-then-shifting (which lands every
        /// negative coordinate one chunk off — invisible in an all-positive world, permanent once Tier B
        /// worlds exist). This is the only coverage <c>ToChunkRelative</c> has: the shipped B8/B9 level.dat
        /// scenarios both start at v13, after the re-type has already happened.</summary>
        private static bool V12PlayerPositionRetype()
        {
            WorldSaveData upgraded = LevelDatCodec.ReadNormalized(V12_LEVEL_DAT);

            bool ok = Check($"the negative X re-types to chunk {V12_EXPECTED_CHUNK_X.ToString()}, got {upgraded.player.position.Chunk.X.ToString()}",
                upgraded.player.position.Chunk.X == V12_EXPECTED_CHUNK_X);
            ok &= Check($"the negative Z re-types to chunk {V12_EXPECTED_CHUNK_Z.ToString()}, got {upgraded.player.position.Chunk.Z.ToString()}",
                upgraded.player.position.Chunk.Z == V12_EXPECTED_CHUNK_Z);
            ok &= Check($"the local X offset is in [0,16) at {V12_EXPECTED_LOCAL_X}, got {upgraded.player.position.localPosition.x}",
                Mathf.Approximately(upgraded.player.position.localPosition.x, V12_EXPECTED_LOCAL_X));
            ok &= Check($"the local Z offset is in [0,16) at {V12_EXPECTED_LOCAL_Z}, got {upgraded.player.position.localPosition.z}",
                Mathf.Approximately(upgraded.player.position.localPosition.z, V12_EXPECTED_LOCAL_Z));
            ok &= Check("Y stays absolute through the re-type (the origin is XZ-only)",
                Mathf.Approximately(upgraded.player.position.localPosition.y, 70f));

            // Fields owned by steps that do NOT run for a v12 source must survive as the document's own.
            ok &= Check($"the document's own border radius survives, got {upgraded.borderRadius.ToString()}",
                upgraded.borderRadius == 768);
            ok &= Check("the document's own spawn point survives (the v10→v11 injector does not run at v12)",
                upgraded.spawnPosition.Chunk.X == 4 && upgraded.spawnPosition.Chunk.Z == -6);
            ok &= Check($"the document's own world type survives, got {((int)upgraded.worldType).ToString()}",
                (int)upgraded.worldType == 1);

            // Everything the later re-serializing DTOs could silently drop.
            ok &= Check("worldName survives", upgraded.worldName == "V12Probe");
            ok &= Check("seed survives", upgraded.seed == 246);
            ok &= Check("creation/lastPlayed survive", upgraded.creationDate == 5555 && upgraded.lastPlayed == 6666);
            ok &= Check("dimensions survive",
                upgraded.chunkHeight == LEGACY_CHUNK_HEIGHT && upgraded.chunkWidth == LEGACY_CHUNK_WIDTH &&
                upgraded.worldSizeInChunks == LEGACY_WORLD_SIZE);
            ok &= Check("player rotation survives",
                Mathf.Approximately(upgraded.player.rotation.y, 20f) && Mathf.Approximately(upgraded.player.rotation.z, 30f));
            ok &= Check("player capabilities survive",
                upgraded.player.capabilities.isFlying && !upgraded.player.capabilities.isNoclipping);
            ok &= Check("inventory survives",
                upgraded.player.inventory.Count == 1 && upgraded.player.inventory[0].slotIndex == 1 &&
                upgraded.player.inventory[0].itemID == 6 && upgraded.player.inventory[0].amount == 17);

            // The codec deliberately keeps the ON-DISK version so the menu still offers a real migration.
            ok &= Check("the reported version stays the disk's v12", upgraded.version == 12);
            return ok;
        }

        /// <summary>B3. Red when: any of the fourteen steps drops a field on the way from v1 to current, or
        /// injects a historical default at the wrong value. This is the composition gate — per-step coverage
        /// cannot catch a step that is individually correct but composes wrongly with a successor, and the
        /// two steps that re-serialize from a frozen DTO (v12→v13, v14→v15) drop whatever their DTO omits
        /// from EVERY migrated document.</summary>
        private static bool V1ChainedToCurrent()
        {
            WorldSaveData upgraded = LevelDatCodec.ReadNormalized(V1_LEVEL_DAT);

            // --- Injected historical defaults, step by step ---
            bool ok = Check($"v3→v4 stamps the Legacy world type, got {((int)upgraded.worldType).ToString()}",
                (int)upgraded.worldType == (int)WorldTypeID.Legacy);
            ok &= Check("v6→v7 pins the legacy dimensions 128/16/100",
                upgraded.chunkHeight == LEGACY_CHUNK_HEIGHT && upgraded.chunkWidth == LEGACY_CHUNK_WIDTH &&
                upgraded.worldSizeInChunks == LEGACY_WORLD_SIZE);
            ok &= Check($"v10→v11 injects the legacy-centre spawn chunk ({LEGACY_SPAWN_CHUNK.ToString()},{LEGACY_SPAWN_CHUNK.ToString()}), got ({upgraded.spawnPosition.Chunk.X.ToString()},{upgraded.spawnPosition.Chunk.Z.ToString()})",
                upgraded.spawnPosition.Chunk.X == LEGACY_SPAWN_CHUNK && upgraded.spawnPosition.Chunk.Z == LEGACY_SPAWN_CHUNK);
            ok &= Check($"the injected spawn height is the unresolved sentinel, got {upgraded.spawnPosition.localPosition.y}",
                Mathf.Approximately(upgraded.spawnPosition.localPosition.y, UNRESOLVED_SPAWN_HEIGHT));
            ok &= Check($"v11→v12 leaves the border disabled, got {upgraded.borderRadius.ToString()}",
                upgraded.borderRadius == 0);
            ok &= Check($"v13→v14 injects the historical wind, got ({upgraded.worldState?.environment?.windX}, {upgraded.worldState?.environment?.windZ})",
                upgraded.worldState?.environment != null &&
                Mathf.Approximately(upgraded.worldState.environment.windX, HISTORICAL_WIND_X) &&
                Mathf.Approximately(upgraded.worldState.environment.windZ, 0f));
            ok &= Check($"v14→v15 seeds the clock at noon and unfrozen, got tick {upgraded.worldState?.time?.ticks} frozen={upgraded.worldState?.time?.frozen}",
                upgraded.worldState?.time != null && upgraded.worldState.time.ticks == MIGRATED_NOON_TICKS &&
                !upgraded.worldState.time.frozen);

            // --- Every v1 field must survive all fourteen steps ---
            ok &= Check("worldName survives fourteen steps", upgraded.worldName == "V1Probe");
            ok &= Check("seed survives fourteen steps", upgraded.seed == 987);
            ok &= Check("creation/lastPlayed survive fourteen steps",
                upgraded.creationDate == 1111 && upgraded.lastPlayed == 2222);

            // The v1 absolute position 33.5 / 8.25 re-types to chunk (2, 0), local (1.5, 71, 8.25).
            ok &= Check($"the v1 player position survives the re-type, got chunk ({upgraded.player.position.Chunk.X.ToString()},{upgraded.player.position.Chunk.Z.ToString()}) local ({upgraded.player.position.localPosition.x},{upgraded.player.position.localPosition.z})",
                upgraded.player.position.Chunk.X == 2 && upgraded.player.position.Chunk.Z == 0 &&
                Mathf.Approximately(upgraded.player.position.localPosition.x, 1.5f) &&
                Mathf.Approximately(upgraded.player.position.localPosition.z, 8.25f));
            ok &= Check("player height survives", Mathf.Approximately(upgraded.player.position.localPosition.y, 71f));
            ok &= Check("player rotation survives", Mathf.Approximately(upgraded.player.rotation.y, 45f));
            ok &= Check("both player capabilities survive",
                upgraded.player.capabilities.isFlying && upgraded.player.capabilities.isNoclipping);
            ok &= Check("inventory survives with its slot, id and amount",
                upgraded.player.inventory.Count == 1 && upgraded.player.inventory[0].slotIndex == 2 &&
                upgraded.player.inventory[0].itemID == 9 && upgraded.player.inventory[0].amount == 13);
            ok &= Check("the cursor item survives (the field most easily dropped — it is nullable)",
                upgraded.player.cursorItem != null && upgraded.player.cursorItem.itemID == 4 &&
                upgraded.player.cursorItem.amount == 2 && upgraded.player.cursorItem.originSlotIndex == 5);

            ok &= Check("the reported version stays the disk's v1", upgraded.version == 1);
            return ok;
        }

        /// <summary>B4. Red when: the v3→v4 or v6→v7 step preserves the document's existing value instead of
        /// overwriting it. Both steps deliberately CLOBBER — a pre-v4 world is Legacy by definition, and
        /// pinning 128/16/100 is what keeps an old world physically identical if the engine's constants
        /// change. A fixture whose fields already held the expected values could not tell the two apart, so
        /// this one arrives with a non-Legacy type and nonsense dimensions.</summary>
        private static bool V3OverwritesTypeAndDimensions()
        {
            WorldSaveData upgraded = LevelDatCodec.ReadNormalized(V3_LEVEL_DAT);

            bool ok = Check($"the non-Legacy world type is overwritten with Legacy, got {((int)upgraded.worldType).ToString()}",
                (int)upgraded.worldType == (int)WorldTypeID.Legacy);
            ok &= Check($"the bogus chunk height is overwritten with 128, got {upgraded.chunkHeight.ToString()}",
                upgraded.chunkHeight == LEGACY_CHUNK_HEIGHT);
            ok &= Check($"the bogus chunk width is overwritten with 16, got {upgraded.chunkWidth.ToString()}",
                upgraded.chunkWidth == LEGACY_CHUNK_WIDTH);
            ok &= Check($"the bogus world size is overwritten with 100, got {upgraded.worldSizeInChunks.ToString()}",
                upgraded.worldSizeInChunks == LEGACY_WORLD_SIZE);

            // The overwrite must stay surgical — everything else still carries through.
            ok &= Check("worldName survives the overwriting steps", upgraded.worldName == "V3Probe");
            ok &= Check("seed survives the overwriting steps", upgraded.seed == 555);
            ok &= Check("creation/lastPlayed survive", upgraded.creationDate == 3333 && upgraded.lastPlayed == 4444);
            ok &= Check("an empty inventory stays empty rather than becoming null",
                upgraded.player.inventory != null && upgraded.player.inventory.Count == 0);
            // A null cursor item does NOT stay null: JsonUtility cannot write null for a serializable class
            // field, so every re-serializing step resurrects it as a default-valued object. Harmless only
            // because DragAndDropHandler.LoadCursorData treats itemID 0 as empty — that guard is load-bearing
            // for every migrated save, so this pins the pair rather than the null.
            ok &= Check("a null cursor item resurrects as the empty default, which the load path reads as empty",
                upgraded.player.cursorItem != null && upgraded.player.cursorItem.itemID == 0 &&
                upgraded.player.cursorItem.amount == 0);
            ok &= Check("the reported version stays the disk's v3", upgraded.version == 3);
            return ok;
        }
    }
}
