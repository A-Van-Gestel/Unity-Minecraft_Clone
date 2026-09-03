using System;
using System.Collections.Generic;
using UnityEngine;

namespace Serialization.Migration.Steps
{
    /// <summary>
    /// Migrates level.dat from v14 → v15 by replacing <c>worldState.timeOfDay</c> with the RF-1 day/night
    /// clock (<c>worldState.time</c>). No chunk format changes — only global metadata.
    /// </summary>
    /// <remarks>
    /// The retired field stored a <i>light level</i>, not a time: before RF-1 there was no clock, so the
    /// save recorded how bright the world was and nothing advanced it. There is no meaningful mapping
    /// from a brightness back to a time — a level of 1.0 could be any moment across the whole midday
    /// plateau — so every migrated world starts at noon rather than inventing precision the old value
    /// never had.
    /// </remarks>
    public class MigrationV14ToV15TimeOfDay : WorldMigrationStep
    {
        public override int SourceWorldVersion => 14;
        public override int TargetWorldVersion => 15;
        public override string Description => "Converting time of day to a world clock...";

        public override string ChangeSummary =>
            "Adds a real day/night cycle. Existing worlds resume at noon, and time now advances as you play.";

        /// <summary>Day tick the migrated world starts at (noon, on Minecraft's sunrise-anchored scale).</summary>
        private const long NOON_TICKS = 6000L;

        public override string MigrateLevelDat(string oldJson)
        {
            // Frozen DTO, not the live WorldSaveData — see the v13→v14 step's remarks.
            V15LevelDat data = JsonUtility.FromJson<V15LevelDat>(oldJson);

            data.worldState ??= new V15WorldState();
            data.worldState.time = new V15WorldTime
            {
                ticks = NOON_TICKS,
                frozen = false,
            };

            // The retired field is simply not present on the v15 DTO, so serializing drops it.
            data.version = TargetWorldVersion;

            return JsonUtility.ToJson(data, true);
        }

        // ========================================================================================
        // FROZEN DTO — the level.dat shape as of v15. DO NOT MODIFY.
        // The v14 shape with worldState.timeOfDay replaced by worldState.time. This is the first
        // level.dat step that REMOVES a field, so the DTO must mirror the v15 shape exactly: any
        // field left off here is dropped from every migrated document.
        // A future format change writes its own frozen DTO; it does not extend this one.
        // ========================================================================================

        /// <summary>Frozen mirror of <c>WorldSaveData</c> as of world version 15.</summary>
        [Serializable]
        private class V15LevelDat
        {
            public int version;
            public string worldName;
            public int seed;
            public int chunkHeight;
            public int chunkWidth;
            public int worldSizeInChunks;
            public int worldType;
            public LegacyChunkRelativePosition spawnPosition;
            public int borderRadius;
            public long creationDate;
            public long lastPlayed;
            public V15WorldState worldState;
            public V15PlayerSaveData player;
        }

        /// <summary>Frozen mirror of <c>WorldStateData</c> as of world version 15 — <c>timeOfDay</c> retired.</summary>
        [Serializable]
        private class V15WorldState
        {
            public V15Environment environment;
            public V15WorldTime time;
        }

        /// <summary>Frozen mirror of <c>EnvironmentData</c> as of world version 15 — unchanged from v14.</summary>
        [Serializable]
        private class V15Environment
        {
            public float windX;
            public float windZ;
        }

        /// <summary>Frozen mirror of <c>WorldTimeData</c> as of world version 15 — the section this step introduces.</summary>
        [Serializable]
        private class V15WorldTime
        {
            public long ticks;
            public bool frozen;
        }

        /// <summary>Frozen mirror of <c>PlayerSaveData</c> as of world version 15 — unchanged from v13.</summary>
        [Serializable]
        private class V15PlayerSaveData
        {
            public LegacyChunkRelativePosition position;
            public Vector3 rotation;
            public LegacyPlayerCapabilities capabilities;
            public List<LegacyInventoryItem> inventory;
            public LegacyCursorItem cursorItem;
        }
    }
}
