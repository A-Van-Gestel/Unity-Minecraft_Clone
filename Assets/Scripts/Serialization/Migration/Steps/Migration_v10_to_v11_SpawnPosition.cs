using UnityEngine;

namespace Serialization.Migration.Steps
{
    /// <summary>
    /// Migrates level.dat from v10 → v11 by injecting the <c>spawnPosition</c> field
    /// with default coordinates matching the legacy world center.
    /// No chunk format changes — only global metadata.
    /// </summary>
    public class MigrationV10ToV11SpawnPosition : WorldMigrationStep
    {
        public override int SourceWorldVersion => 10;
        public override int TargetWorldVersion => 11;
        public override string Description => "Adding spawn position metadata...";
        public override string ChangeSummary => "Adds a persistent spawn point to the save file for respawn and editor-play support.";

        // Frozen legacy constants — NOT referencing live engine types.
        // VoxelData.WorldSizeInChunks = 100, ChunkWidth = 16 → WorldCentre = 800
        private const int LEGACY_WORLD_CENTRE = 800;

        // Frozen: ChunkRelativePosition.UNRESOLVED_HEIGHT as of v11. Not referencing the live constant — this
        // step must keep writing the value v11 meant, even if the sentinel is ever re-chosen.
        private const float UNRESOLVED_HEIGHT = -1_000_000f;

        // Frozen: ChunkMath.CHUNK_WIDTH as of v11. LEGACY_WORLD_CENTRE / 16 = 800 / 16 = 50, so the spawn's
        // chunk is (50, 50) with a zero local offset — the normalization the live ctor would have performed.
        private const int CHUNK_WIDTH = 16;

        public override string MigrateLevelDat(string oldJson)
        {
            // Frozen DTO, not the live WorldSaveData — see LegacyLevelDat's header.
            LegacyLevelDat data = JsonUtility.FromJson<LegacyLevelDat>(oldJson);

            // Inject default spawn at legacy world center.
            // The Y coordinate is set to unresolved; at load time,
            // GetHighestVoxel will resolve the actual surface height and update the save.
            data.spawnPosition = new LegacyChunkRelativePosition
            {
                _chunkX = LEGACY_WORLD_CENTRE / CHUNK_WIDTH,
                _chunkZ = LEGACY_WORLD_CENTRE / CHUNK_WIDTH,
                localPosition = new Vector3(
                    LEGACY_WORLD_CENTRE % CHUNK_WIDTH, UNRESOLVED_HEIGHT, LEGACY_WORLD_CENTRE % CHUNK_WIDTH),
            };

            data.version = TargetWorldVersion;

            return JsonUtility.ToJson(data, true);
        }
    }
}
