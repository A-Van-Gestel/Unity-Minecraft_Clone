using System;
using System.Collections.Generic;
using UnityEngine;

namespace Serialization.Migration.Steps
{
    /// <summary>
    /// Migrates level.dat from v12 → v13 by re-typing <c>player.position</c> from an absolute
    /// <see cref="Vector3"/> to a chunk-relative position (WS-4c), lifting the ±2²⁴ precision cap on the saved
    /// player location. No chunk format changes — only global metadata.
    /// </summary>
    /// <remarks>
    /// The first level.dat change that is <b>not</b> additive: every earlier one only added fields, which
    /// JsonUtility tolerates by defaulting them. A re-type is different — a v12 document's
    /// <c>"position":{"x":..,"y":..,"z":..}</c> has none of the members the new type looks for, so anything reading
    /// it through the LIVE <c>WorldSaveData</c> silently blanks the field. That is why every pre-v13 step reads the
    /// frozen <see cref="LegacyLevelDat"/> instead, and why this step reads it too rather than the live type.
    /// <para>
    /// This recovers no precision that was already lost: a v12 position beyond ±2²⁴ was rounded when it was
    /// written, and the migration is faithful to whatever the file actually holds. It stops the loss from here on.
    /// </para>
    /// </remarks>
    public class MigrationV12ToV13PlayerChunkRelativePosition : WorldMigrationStep
    {
        public override int SourceWorldVersion => 12;
        public override int TargetWorldVersion => 13;
        public override string Description => "Upgrading player position to high-precision coordinates...";

        public override string ChangeSummary =>
            "Stores the player's position chunk-relative so it stays exact at extreme distances from the origin.";

        // Frozen: ChunkMath.CHUNK_WIDTH as of v13. Not referencing the live constant — this step must keep
        // producing the layout v13 meant even if the engine's chunk size ever changes.
        private const int CHUNK_WIDTH = 16;

        // Frozen: log2(CHUNK_WIDTH) = 4. Used for the floor-divide below (16 = 1 << 4).
        private const int CHUNK_WIDTH_SHIFT = 4;

        public override string MigrateLevelDat(string oldJson)
        {
            // Read through the frozen v1-v12 shape, where `position` is still a Vector3 (the whole point).
            LegacyLevelDat old = JsonUtility.FromJson<LegacyLevelDat>(oldJson);

            V13LevelDat migrated = new V13LevelDat
            {
                version = old.version,
                worldName = old.worldName,
                seed = old.seed,
                chunkHeight = old.chunkHeight,
                chunkWidth = old.chunkWidth,
                worldSizeInChunks = old.worldSizeInChunks,
                worldType = old.worldType,
                spawnPosition = old.spawnPosition,
                borderRadius = old.borderRadius,
                creationDate = old.creationDate,
                lastPlayed = old.lastPlayed,
                worldState = old.worldState,
                player = new V13PlayerSaveData
                {
                    position = ToChunkRelative(old.player.position),
                    rotation = old.player.rotation,
                    capabilities = old.player.capabilities,
                    inventory = old.player.inventory,
                    cursorItem = old.player.cursorItem,
                },
            };

            migrated.version = TargetWorldVersion;

            return JsonUtility.ToJson(migrated, true);
        }

        /// <summary>
        /// Splits an absolute voxel-space position into the chunk + local offset the v13 format stores.
        /// </summary>
        /// <remarks>
        /// Replicates <c>ChunkRelativePosition</c>'s absolute constructor as it stood at v13, frozen:
        /// <c>Chunk = ChunkCoord.FromWorldPosition(pos)</c> (renamed <c>FromVoxelPosition</c> after v13) → <c>ChunkMath.WorldToChunk(f)</c> →
        /// <c>(int)floor(f) >> 4</c>, then <c>local = pos − chunk * 16</c>. The floor-then-shift order matters and is
        /// not the same as <c>(int)(f / 16)</c>: C# integer division truncates toward zero, which lands negative
        /// coordinates in the wrong chunk (WS-1's shift/mask rule). The resulting local offset is always in
        /// <c>[0, 16)</c>, so no separate normalization pass is needed.
        /// </remarks>
        /// <param name="absolute">The absolute voxel-space position from the v12 document.</param>
        /// <returns>The equivalent chunk-relative position in the v13 on-disk layout.</returns>
        private static LegacyChunkRelativePosition ToChunkRelative(Vector3 absolute)
        {
            int chunkX = (int)Math.Floor(absolute.x) >> CHUNK_WIDTH_SHIFT;
            int chunkZ = (int)Math.Floor(absolute.z) >> CHUNK_WIDTH_SHIFT;

            return new LegacyChunkRelativePosition
            {
                _chunkX = chunkX,
                _chunkZ = chunkZ,
                localPosition = new Vector3(
                    absolute.x - chunkX * CHUNK_WIDTH,
                    absolute.y, // Y is absolute in this format — the origin is XZ-only and never shifts vertically.
                    absolute.z - chunkZ * CHUNK_WIDTH),
            };
        }

        // ========================================================================================
        // FROZEN DTO — the level.dat shape as of v13. DO NOT MODIFY.
        // Identical to LegacyLevelDat except player.position, which is the point of this migration.
        // A future format change writes its own frozen DTO; it does not extend this one.
        // ========================================================================================

        /// <summary>Frozen mirror of <c>WorldSaveData</c> as of world version 13.</summary>
        [Serializable]
        private class V13LevelDat
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
            public V13PlayerSaveData player;
        }

        /// <summary>
        /// Frozen mirror of <c>PlayerSaveData</c> as of world version 13 — the era in which
        /// <see cref="position"/> became chunk-relative.
        /// </summary>
        [Serializable]
        private class V13PlayerSaveData
        {
            public LegacyChunkRelativePosition position;
            public Vector3 rotation;
            public LegacyPlayerCapabilities capabilities;
            public List<LegacyInventoryItem> inventory;
            public LegacyCursorItem cursorItem;
        }
    }
}
