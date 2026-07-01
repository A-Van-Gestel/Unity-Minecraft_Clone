using Data;
using UnityEditor;
using UnityEngine;

namespace Editor.DataGeneration
{
    /// <summary>
    /// One-time migration for the <c>canReplaceTags</c> → <c>worldGenCanReplaceTags</c> + <c>placementCanReplaceTags</c>
    /// split (PLAYER_BUGS §03). The old single field is carried into <see cref="BlockType.worldGenCanReplaceTags"/> by
    /// <c>[FormerlySerializedAs]</c> on import; this utility back-fills <see cref="BlockType.placementCanReplaceTags"/>
    /// (and the matching <see cref="BlockTagPreset"/> fields) with the same value, so the split is initially
    /// <b>behavior-preserving</b>. The three offending blocks' placement masks are retuned afterwards by hand.
    /// <para>
    /// Idempotent and safe to re-run, but note it <i>overwrites</i> placement masks with the world-gen masks — do not
    /// run it after the offenders have been retuned, or it will revert them.
    /// </para>
    /// </summary>
    public static class PlacementTagMigration
    {
        [MenuItem("Minecraft Clone/Migrate canReplace Tags (worldGen -> placement)")]
        public static void Migrate()
        {
            int blockCount = 0;
            int presetCount = 0;

            BlockDatabase database = EditorBlockDatabaseCache.Database;
            if (database != null && database.blockTypes != null)
            {
                foreach (BlockType block in database.blockTypes)
                {
                    if (block == null) continue;
                    block.placementCanReplaceTags = block.worldGenCanReplaceTags;
                    blockCount++;
                }

                EditorUtility.SetDirty(database);
            }
            else
            {
                Debug.LogError("[PlacementTagMigration] Could not load the BlockDatabase — blocks not migrated.");
            }

            // Presets are standalone assets referenced by blocks; back-fill them too.
            string[] presetGuids = AssetDatabase.FindAssets("t:BlockTagPreset");
            foreach (string guid in presetGuids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                BlockTagPreset preset = AssetDatabase.LoadAssetAtPath<BlockTagPreset>(path);
                if (preset == null) continue;
                preset.placementCanReplaceTags = preset.worldGenCanReplaceTags;
                EditorUtility.SetDirty(preset);
                presetCount++;
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[PlacementTagMigration] Back-filled placementCanReplaceTags = worldGenCanReplaceTags for " +
                      $"{blockCount} block(s) and {presetCount} preset(s). Behavior-preserving until the offenders are retuned.");
        }
    }
}
