using Data.WorldTypes;
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

        public override string MigrateLevelDat(string oldJson)
        {
            WorldSaveData data = JsonUtility.FromJson<WorldSaveData>(oldJson);

            // Inject default spawn at legacy world center.
            // The Y coordinate is set to unresolved; at load time,
            // GetHighestVoxel will resolve the actual surface height and update the save.
            data.spawnPosition = new ChunkRelativePosition(
                new Vector3(LEGACY_WORLD_CENTRE, ChunkRelativePosition.UNRESOLVED_HEIGHT, LEGACY_WORLD_CENTRE));

            data.version = TargetWorldVersion;

            return JsonUtility.ToJson(data, true);
        }
    }
}
