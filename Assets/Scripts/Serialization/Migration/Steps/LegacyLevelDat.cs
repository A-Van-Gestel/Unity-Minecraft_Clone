using System;
using System.Collections.Generic;
using UnityEngine;

namespace Serialization.Migration.Steps
{
    // ============================================================================================
    // FROZEN DTO — the level.dat shape for world versions 1 through 12. DO NOT MODIFY.
    // ============================================================================================
    //
    // A complete, self-contained record of what level.dat looked like on disk up to and including
    // v12, so the migration steps that rewrite it never touch the live `WorldSaveData` again.
    //
    // ── WHY THIS EXISTS ─────────────────────────────────────────────────────────────────────────
    //   Four shipped steps (v3→v4, v6→v7, v10→v11, v11→v12) originally round-tripped the whole
    //   document through the LIVE `WorldSaveData`: FromJson → mutate one field → ToJson. That was
    //   only ever ACCIDENTALLY safe, because every level.dat change through v12 was purely
    //   ADDITIVE — JsonUtility fills an absent field with a default, so a v3 document parsed by a
    //   v12-shaped class survived intact.
    //
    //   WS-4c broke that: it RE-TYPES `player.position` from Vector3 to ChunkRelativePosition. A
    //   v1–v12 document holds `"position":{"x":..,"y":..,"z":..}`; the live field now expects
    //   `_chunkX`/`_chunkZ`/`localPosition`, finds none of them, and SILENTLY defaults — writing
    //   the player's position away to (0,0,0) before the v12→v13 step ever sees it. Roughly 200
    //   saves on this machine span v1–v12, so this was not hypothetical.
    //
    //   This is exactly the coupling AOT_WORLD_MIGRATION_SYSTEM.md §1.2 forbids ("a complete
    //   rewrite of those classes in the future cannot break old migrations"). The four steps now
    //   parse THIS instead, which pins their behavior to the era they were written for.
    //
    // ── WHY ONE DTO COVERS ALL FOUR STEPS ───────────────────────────────────────────────────────
    //   A step migrating vN→vN+1 only ever sees vN-shaped JSON, and every change from v1 to v12
    //   was additive — so this single v12-era shape is a superset of every document those steps
    //   can receive, and it can never DROP a field (a v3 document has no v11 fields to lose).
    //   Fields the source document lacks are written out at their defaults and then set correctly
    //   by the later step that owns them (v6→v7 the dimensions, v10→v11 the spawn point, v11→v12
    //   the border), so the end state is identical.
    //
    // ── THE RULE FOR THE NEXT PERSON ────────────────────────────────────────────────────────────
    //   Never add a field here, and never change one. If a FUTURE version re-types or removes a
    //   level.dat field, that migration writes its own frozen DTO for its own era — it does not
    //   extend this one. This type describes the past, and the past does not change.
    // ============================================================================================

    /// <summary>
    /// Frozen mirror of <c>WorldSaveData</c> as it existed for world versions 1–12, kept so the level.dat
    /// migration steps are immune to changes in the live save types. See the header comment before editing.
    /// </summary>
    [Serializable]
    internal class LegacyLevelDat
    {
        public int version = 1;
        public string worldName;
        public int seed;

        // Added in v6→v7 (SaveFormatExtensibility); absent and defaulted in v1–v6 documents.
        public int chunkHeight = 128;
        public int chunkWidth = 16;
        public int worldSizeInChunks = 100;

        // Added in v3→v4 (WorldTypes). Frozen as int, not the live WorldTypeID enum: JsonUtility writes an
        // enum as its integer value, so this is byte-identical while staying decoupled from the live enum.
        public int worldType;

        // Added in v10→v11 (SpawnPosition).
        public LegacyChunkRelativePosition spawnPosition = new LegacyChunkRelativePosition();

        // Added in v11→v12 (WorldBorder).
        public int borderRadius;

        public long creationDate;
        public long lastPlayed;

        public LegacyWorldState worldState = new LegacyWorldState();
        public LegacyPlayerSaveData player = new LegacyPlayerSaveData();
    }

    /// <summary>Frozen mirror of <c>WorldStateData</c> for world versions 1–12.</summary>
    [Serializable]
    internal class LegacyWorldState
    {
        public float timeOfDay;
    }

    /// <summary>
    /// Frozen mirror of <c>PlayerSaveData</c> for world versions 1–12 — the era in which
    /// <see cref="position"/> was an absolute <see cref="Vector3"/>. WS-4c (v13) re-types it to a
    /// chunk-relative position; this is the shape every step below v13 must keep reading.
    /// </summary>
    [Serializable]
    internal class LegacyPlayerSaveData
    {
        /// <summary>The absolute voxel-space player position. Re-typed to ChunkRelativePosition in v13.</summary>
        public Vector3 position;

        public Vector3 rotation;
        public LegacyPlayerCapabilities capabilities = new LegacyPlayerCapabilities();
        public List<LegacyInventoryItem> inventory = new List<LegacyInventoryItem>();
        public LegacyCursorItem cursorItem;
    }

    /// <summary>Frozen mirror of <c>PlayerCapabilityData</c> for world versions 1–12.</summary>
    [Serializable]
    internal class LegacyPlayerCapabilities
    {
        public bool isFlying;
        public bool isNoclipping;
    }

    /// <summary>Frozen mirror of <c>InventoryItemData</c> for world versions 1–12.</summary>
    [Serializable]
    internal class LegacyInventoryItem
    {
        public int slotIndex;
        public byte itemID;
        public int amount;
    }

    /// <summary>Frozen mirror of <c>CursorItemData</c> for world versions 1–12.</summary>
    [Serializable]
    internal class LegacyCursorItem
    {
        public byte itemID;
        public int amount;
        public int originSlotIndex = -1;
    }

    /// <summary>
    /// Frozen mirror of <c>ChunkRelativePosition</c>'s serialized form for world versions 11–12.
    /// <para>
    /// Field names match the live struct's serialized members exactly (<c>_chunkX</c>/<c>_chunkZ</c> are its
    /// private [SerializeField] ints; <c>Chunk</c> is [NonSerialized] and reconstructed from them by its
    /// deserialization callback), so this produces byte-identical JSON without depending on the live type.
    /// </para>
    /// </summary>
    [Serializable]
    internal class LegacyChunkRelativePosition
    {
        // ReSharper disable InconsistentNaming — these names ARE the on-disk contract.
        public int _chunkX;
        public int _chunkZ;

        // ReSharper restore InconsistentNaming
        public Vector3 localPosition;
    }
}
