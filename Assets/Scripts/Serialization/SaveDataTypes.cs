using System;
using System.Collections.Generic;
using Data.WorldTypes;
using UnityEngine;

namespace Serialization
{
    public enum CompressionAlgorithm : byte
    {
        None = 0,
        Deflate = 1,
        LZ4 = 2,
        // GZip = 3, // Reserved: true GZip (with header/CRC) or another codec (e.g. Zstd)
    }


    [Serializable]
    public class WorldSaveData
    {
        public int version = 1;
        public string worldName;
        public int seed;

        /// <summary>
        /// The chunk height in blocks.
        /// </summary>
        public int chunkHeight = 128;

        /// <summary>
        /// The chunk width and depth in blocks.
        /// </summary>
        public int chunkWidth = 16;

        /// <summary>
        /// The total width of the world in chunks.
        /// </summary>
        public int worldSizeInChunks = 100;

        /// <summary>
        /// The world generation type. Defaults to Legacy (0) when the field is absent in old JSON files,
        /// ensuring backwards compatibility with saves created before the World Type system was introduced.
        /// </summary>
        public WorldTypeID worldType;

        /// <summary>
        /// The world's canonical spawn point in chunk-relative coordinates.
        /// Set during initial world generation; used as the fallback player position
        /// for editor direct-scene-load and future respawn mechanics (e.g. beds).
        /// </summary>
        public ChunkRelativePosition spawnPosition;

        /// <summary>
        /// Half-extent (in voxels) of the optional per-world gameplay border, a square AABB
        /// centered on the world origin. <c>0</c> means disabled — the world stays fully
        /// unbounded (the WS-2/WS-3 default). Purely a player fence: terrain generation,
        /// lighting, meshing, and storage are border-blind. Absent in pre-v12 saves, where
        /// JSON deserialization defaults it to 0 (disabled).
        /// </summary>
        public int borderRadius;

        public long creationDate; // Ticks
        public long lastPlayed; // Ticks

        public WorldStateData worldState = new WorldStateData();

        public PlayerSaveData player = new PlayerSaveData();
    }


    /// <summary>
    /// The world's mutable global state, grouped into sub-sections by concern rather than kept as a
    /// flat bag — RF-7's weather fields join <see cref="environment"/> without swelling this type.
    /// </summary>
    [Serializable]
    public class WorldStateData
    {
        /// <summary>
        /// Ambient environment state (wind today, weather later). Lived at the document root in v14;
        /// re-parented here at v15 so every piece of world state has one home.
        /// </summary>
        public EnvironmentData environment = new EnvironmentData();

        /// <summary>The day/night clock (v15+). Absent in older saves, where the migration seeds noon.</summary>
        public WorldTimeData time = new WorldTimeData();
    }

    /// <summary>
    /// The world's persisted environment state (v14+). Stored as the raw XZ velocity the engine
    /// actually holds, not as speed/bearing — the polar form is a command-surface convenience and
    /// round-tripping it through trig on every save would drift.
    /// </summary>
    [Serializable]
    public class EnvironmentData
    {
        /// <summary>Wind velocity X component, in voxel-space blocks per second.</summary>
        public float windX;

        /// <summary>Wind velocity Z component, in voxel-space blocks per second.</summary>
        public float windZ;
    }

    /// <summary>
    /// The world's persisted day/night clock (v15+, RF-1). Replaces the v1–v14 <c>timeOfDay</c> field,
    /// which stored a light level rather than a time because no clock existed to store.
    /// </summary>
    /// <remarks>
    /// Total elapsed ticks, not a day fraction plus a day counter: one exact integer carries both, and
    /// it cannot drift the way an accumulated float would over a long-lived world.
    /// </remarks>
    [Serializable]
    public class WorldTimeData
    {
        /// <summary>Total elapsed world time in ticks (24000 per day, tick 0 = sunrise).</summary>
        public long ticks;

        /// <summary>Whether the day/night cycle is held, as set by <c>/time freeze</c>.</summary>
        public bool frozen;
    }

    [Serializable]
    public class PlayerSaveData
    {
        /// <summary>
        /// The player's position in voxel world space, stored chunk-relative so it stays exact at any distance
        /// (v13; an absolute <see cref="Vector3"/> before that, which lost sub-voxel precision past ±2²⁴).
        /// </summary>
        public ChunkRelativePosition position;

        public Vector3 rotation;
        public PlayerCapabilityData capabilities = new PlayerCapabilityData();
        public List<InventoryItemData> inventory = new List<InventoryItemData>();
        public CursorItemData cursorItem; // Can be null
    }

    [Serializable]
    public class PlayerCapabilityData
    {
        public bool isFlying;
        public bool isNoclipping;
    }

    [Serializable]
    public class InventoryItemData
    {
        public int slotIndex;
        public byte itemID;
        public int amount;

        public InventoryItemData(int slot, byte id, int amount)
        {
            slotIndex = slot;
            itemID = id;
            this.amount = amount;
        }
    }

    [Serializable]
    public class CursorItemData
    {
        public byte itemID;

        public int amount;

        // We track origin slot to try and return it there if possible,
        // though standard logic usually just adds to first available.
        public int originSlotIndex = -1;
    }
}
