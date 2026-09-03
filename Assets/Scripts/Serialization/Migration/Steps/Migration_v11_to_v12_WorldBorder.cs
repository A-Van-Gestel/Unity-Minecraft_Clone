using UnityEngine;

namespace Serialization.Migration.Steps
{
    /// <summary>
    /// Migrates level.dat from v11 → v12 by injecting the <c>borderRadius</c> field,
    /// defaulted to 0 (border disabled). Existing worlds stay fully unbounded, consistent
    /// with the WS-2/WS-3 unbounded-XZ direction; the per-world border is opt-in.
    /// No chunk format changes — only global metadata.
    /// </summary>
    public class MigrationV11ToV12WorldBorder : WorldMigrationStep
    {
        public override int SourceWorldVersion => 11;
        public override int TargetWorldVersion => 12;
        public override string Description => "Adding world border metadata...";
        public override string ChangeSummary => "Adds an optional per-world gameplay border (disabled by default for existing worlds).";

        public override string MigrateLevelDat(string oldJson)
        {
            // Frozen DTO, not the live WorldSaveData — see LegacyLevelDat's header.
            LegacyLevelDat data = JsonUtility.FromJson<LegacyLevelDat>(oldJson);

            // Existing worlds were unbounded, so upgrade them with the border disabled.
            data.borderRadius = 0;

            data.version = TargetWorldVersion;

            return JsonUtility.ToJson(data, true);
        }
    }
}
