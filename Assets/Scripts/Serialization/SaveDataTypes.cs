using System;
using System.Collections.Generic;
using UnityEngine;

namespace Serialization
{
    public enum CompressionAlgorithm : byte
    {
        None = 0,
        GZip = 1,
        LZ4 = 2,
        // Zlib = 3, // Reserved for future use (e.g. Zstd)
    }

    [Serializable]
    public class WorldSaveData
    {
        public int version = 1;
        public string worldName;
        public int seed;
        public long creationDate; // Ticks
        public long lastPlayed; // Ticks

        public WorldStateData worldState = new WorldStateData();
        public PlayerSaveData player = new PlayerSaveData();
    }

    [Serializable]
    public class WorldStateData
    {
        public float timeOfDay;
    }

    [Serializable]
    public class PlayerSaveData
    {
        public Vector3 position;
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
