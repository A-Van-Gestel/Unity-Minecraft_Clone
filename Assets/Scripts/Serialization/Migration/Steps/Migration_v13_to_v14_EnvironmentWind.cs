using System;
using System.Collections.Generic;
using UnityEngine;

namespace Serialization.Migration.Steps
{
    /// <summary>
    /// Migrates level.dat from v13 → v14 by injecting the <c>worldState.environment</c> section holding
    /// the world's wind vector. Existing worlds take the wind value the engine hard-coded before it was
    /// persisted, so a migrated sky looks exactly as it did. No chunk format changes — only global
    /// metadata.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Purely additive, but it still writes its own frozen DTO rather than reusing
    /// <c>LegacyLevelDat</c>: that type describes the v1–v12 era, where <c>player.position</c> is a
    /// <see cref="Vector3"/>. Reading a v13 document through it would silently blank the position —
    /// the exact failure the v12→v13 header documents.
    /// </para>
    /// <para>
    /// <b>This step's output was revised in place (2026-08-10, RF-1)</b>, moving <c>environment</c> from
    /// the document root to <c>worldState</c> — normally forbidden, since a shipped step's byte transform
    /// must stay stable. Authorized explicitly by the project owner: the v14 format had reached exactly
    /// one local test world, it is level.dat-only, and every affected save was theirs to re-fix. A v14
    /// document written before the revision keeps its root-level <c>environment</c> and will read as
    /// calm wind; it is not migrated again, because it is already stamped v14.
    /// </para>
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
            data.worldState ??= new V14WorldState();
            data.worldState.environment = new V14Environment
            {
                windX = DEFAULT_WIND_X,
                windZ = DEFAULT_WIND_Z,
            };

            data.version = TargetWorldVersion;

            return JsonUtility.ToJson(data, true);
        }

        // ========================================================================================
        // FROZEN DTO — the level.dat shape as of v14. DO NOT MODIFY.
        // The v13 shape plus the environment section this step nests under worldState. Because the
        // change is purely additive, parsing a v13 document with it leaves every other field intact.
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
            public V14WorldState worldState;
            public V14PlayerSaveData player;
        }

        /// <summary>
        /// Frozen mirror of <c>WorldStateData</c> as of world version 14 — the v1–v12 <c>timeOfDay</c>
        /// field plus the environment section this step nests under it.
        /// </summary>
        /// <remarks>
        /// Declared locally rather than reusing <c>LegacyWorldState</c>: that type is frozen at the
        /// v1–v12 shape and <c>Migration_v12_to_v13</c> still reads it, so growing it would silently
        /// change what an older step produces.
        /// </remarks>
        [Serializable]
        private class V14WorldState
        {
            public float timeOfDay;
            public V14Environment environment;
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
