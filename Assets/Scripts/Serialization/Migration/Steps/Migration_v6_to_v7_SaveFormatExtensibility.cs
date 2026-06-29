using System.IO;
using UnityEngine;

namespace Serialization.Migration.Steps
{
    /// <summary>
    /// Migrates level.dat from v6 → v7 by injecting the structural world dimensions
    /// (chunkHeight, chunkWidth, worldSizeInChunks) into the JSON.
    /// This decouples the save file from the engine's hardcoded compile-time constraints,
    /// allowing future updates to dynamically support worlds with varying dimensions.
    /// No chunk format changes — only global metadata.
    /// </summary>
    public class MigrationV6ToV7SaveFormatExtensibility : WorldMigrationStep
    {
        public override int SourceWorldVersion => 6;
        public override int TargetWorldVersion => 7;
        public override string Description => "Upgrading world schema extensibility...";
        public override string ChangeSummary => "Adds structural world dimensions to the save file for future format flexibility.";

        public override void PerformGlobalFileRename(string savePath)
        {
            // Rename legacy pending lighting file to match pending_mods.bin convention
            string oldPath = Path.Combine(savePath, "lighting_pending.bin");
            string newPath = Path.Combine(savePath, "pending_lighting.bin");

            if (File.Exists(oldPath))
            {
                File.Move(oldPath, newPath);
            }
        }

        public override string MigrateLevelDat(string oldJson)
        {
            // Parse the existing JSON, inject the legacy hardcoded values, and bump version to 7.
            // By injecting 128/16/100, we guarantee that old worlds will remain physically
            // identical to when they were first generated, even if the engine's constants change.
            WorldSaveData data = JsonUtility.FromJson<WorldSaveData>(oldJson);

            data.chunkHeight = 128;
            data.chunkWidth = 16;
            data.worldSizeInChunks = 100;

            data.version = TargetWorldVersion;

            return JsonUtility.ToJson(data, true);
        }
    }
}
