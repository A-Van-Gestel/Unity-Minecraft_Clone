using System;
using System.Collections.Generic;
using UnityEngine;

namespace Serialization.Migration.Steps
{
    /// <summary>
    /// Migrates level.dat from v13 → v14 by injecting the <c>environment</c> section holding the
    /// world's wind vector. Existing worlds take the wind value the engine hard-coded before it was
    /// persisted, so a migrated sky looks exactly as it did. No chunk format changes — only global
    /// metadata.
    /// </summary>
    /// <remarks>
    /// Purely additive, but it still writes its own frozen DTO rather than reusing
    /// <c>LegacyLevelDat</c>: that type describes the v1–v12 era, where <c>player.position</c> is a
    /// <see cref="Vector3"/>. Reading a v13 document through it would silently blank the position —
    /// the exact failure the v12→v13 header documents.
    /// </remarks>
    public class MigrationV13ToV14EnvironmentWind : WorldMigrationStep
    {
        public override int SourceWorldVersion => 13;
        public override int TargetWorldVersion => 14;
        public override string Description => "Adding environment (wind) metadata...";

        public override string ChangeSummary =>
            "Stores the world's wind direction and speed, so it survives a reload and can be set with /wind.";

        // Frozen: World's inspector default wind for every version up to v13, the value all existing
        // worlds were rendered with. Not referencing the live field — a later art change to the
        // default must not retroactively alter what these worlds migrate to.
        private const float DEFAULT_WIND_X = -0.6f;
        private const float DEFAULT_WIND_Z = 0f;

        public override string MigrateLevelDat(string oldJson)
        {
            // Frozen DTO, not the live WorldSaveData — see the remarks above.
            V14LevelDat data = JsonUtility.FromJson<V14LevelDat>(oldJson);

            // A v13 document has no environment section; give it the wind its world always had.
            data.environment = new V14Environment
            {
                windX = DEFAULT_WIND_X,
                windZ = DEFAULT_WIND_Z,
            };

            data.version = TargetWorldVersion;

            return JsonUtility.ToJson(data, true);
        }

        // ========================================================================================
        // FROZEN DTO — the level.dat shape as of v14. DO NOT MODIFY.
        // The v13 shape plus the environment section this step adds. Because the change is purely
        // additive, parsing a v13 document with it leaves every other field intact.
        // A future format change writes its own frozen DTO; it does not extend this one.
        // ========================================================================================

        /// <summary>Frozen mirror of <c>WorldSaveData</c> as of world version 14.</summary>
        [Serializable]
        private class V14LevelDat
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
            public LegacyWorldState worldState;
            public V14Environment environment;
            public V14PlayerSaveData player;
        }

        /// <summary>Frozen mirror of <c>EnvironmentData</c> as of world version 14 — the section this step introduces.</summary>
        [Serializable]
        private class V14Environment
        {
            public float windX;
            public float windZ;
        }

        /// <summary>
        /// Frozen mirror of <c>PlayerSaveData</c> as of world version 14 — unchanged from v13, where
        /// <see cref="position"/> became chunk-relative.
        /// </summary>
        [Serializable]
        private class V14PlayerSaveData
        {
            public LegacyChunkRelativePosition position;
            public Vector3 rotation;
            public LegacyPlayerCapabilities capabilities;
            public List<LegacyInventoryItem> inventory;
            public LegacyCursorItem cursorItem;
        }
    }
}
