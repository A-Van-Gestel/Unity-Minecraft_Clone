using Data.WorldTypes;
using UnityEditor;
using UnityEngine;

namespace Editor.WorldTools
{
    /// <summary>
    /// Pushes the engine's code-authored sky gradients into existing <see cref="TimeOfDaySettings"/>
    /// assets.
    /// </summary>
    /// <remarks>
    /// A <see cref="ScriptableObject"/> field initializer runs only when an instance is created, so an
    /// asset that already exists keeps whatever was serialized into it the first time. Editing the
    /// defaults in code therefore has no effect in game — this command is what closes that gap, and it
    /// doubles as the "revert my authoring" affordance while the sky colors have no dedicated tool.
    /// </remarks>
    public static class SkyGradientDefaults
    {
        /// <summary>
        /// Overwrites the zenith and horizon gradients on every settings asset with the code defaults.
        /// </summary>
        [MenuItem("Minecraft Clone/Dev/Reset Sky Gradients To Code Defaults")]
        public static void ResetSkyGradients()
        {
            string[] guids = AssetDatabase.FindAssets($"t:{nameof(TimeOfDaySettings)}");
            if (guids.Length == 0)
            {
                Debug.LogWarning("[SkyGradientDefaults] No TimeOfDaySettings assets found — nothing to reset.");
                return;
            }

            int updated = 0;
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                TimeOfDaySettings asset = AssetDatabase.LoadAssetAtPath<TimeOfDaySettings>(path);
                if (asset == null) continue;

                SerializedObject so = new SerializedObject(asset);
                so.FindProperty("_zenithOverDay").gradientValue = TimeOfDaySettings.CreateDefaultZenithGradient();
                so.FindProperty("_horizonOverDay").gradientValue = TimeOfDaySettings.CreateDefaultHorizonGradient();
                so.ApplyModifiedProperties();
                EditorUtility.SetDirty(asset);
                updated++;

                Debug.Log($"[SkyGradientDefaults] Reset sky gradients on {path}.");
            }

            AssetDatabase.SaveAssets();
            Debug.Log($"[SkyGradientDefaults] Updated {updated} settings asset(s).");
        }
    }
}
