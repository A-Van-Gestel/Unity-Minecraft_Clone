using Data.WorldTypes;
using UnityEngine;

namespace Serialization.Migration.Steps
{
    /// <summary>
    /// Migrates level.dat from v3 → v4 by explicitly injecting the <c>worldType</c> field.
    /// Existing worlds are assigned <see cref="WorldTypeID.Legacy"/> (0) to preserve
    /// deterministic terrain output using <c>Mathf.PerlinNoise</c>.
    /// No chunk format changes — only global metadata.
    /// </summary>
    public class MigrationV3ToV4WorldTypes : WorldMigrationStep
    {
        public override int SourceWorldVersion => 3;
        public override int TargetWorldVersion => 4;
        public override string Description => "Adding World Type metadata";
        public override string ChangeSummary => "Assigns the Legacy world type to existing worlds.";

        public override string MigrateLevelDat(string oldJson)
        {
            // Parse the existing JSON, inject worldType: Legacy (0), bump version to 4.
            var data = JsonUtility.FromJson<WorldSaveData>(oldJson);
            data.worldType = WorldTypeID.Legacy;
            data.version = TargetWorldVersion;
            return JsonUtility.ToJson(data, true);
        }
    }
}
