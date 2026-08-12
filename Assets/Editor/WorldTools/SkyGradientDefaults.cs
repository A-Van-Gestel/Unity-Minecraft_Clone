using System.Collections.Generic;
using System.Text;
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
    /// is the "revert my authoring" affordance behind the Sky Editor's reset button.
    /// </remarks>
    public static class SkyGradientDefaults
    {
        /// <summary>
        /// Serialized field name → factory for its code default. Covers exactly the four gradients the
        /// Sky Editor authors, so "reset" undoes everything that tool can change and nothing else — the
        /// light curve is deliberately absent, being RF-1 state guarded by the World Clock suite.
        /// </summary>
        private static readonly (string Field, System.Func<Gradient> Default)[] s_gradients =
        {
            ("_zenithOverDay", TimeOfDaySettings.CreateDefaultZenithGradient),
            ("_horizonOverDay", TimeOfDaySettings.CreateDefaultHorizonGradient),
            ("_skyLightOverDay", TimeOfDaySettings.CreateDefaultSkyLightGradient),
            ("_backgroundOverDay", TimeOfDaySettings.CreateDefaultBackgroundGradient),
        };

        /// <summary>
        /// Overwrites the sky gradients on every settings asset with the code defaults, after confirming.
        /// </summary>
        [MenuItem("Minecraft Clone/Dev/Reset Sky Gradients To Code Defaults")]
        public static void ResetSkyGradients()
        {
            List<TimeOfDaySettings> assets = FindAssets(out List<string> paths);
            if (assets.Count == 0)
            {
                Debug.LogWarning("[SkyGradientDefaults] No TimeOfDaySettings assets found — nothing to reset.");
                return;
            }

            if (!Confirm(paths)) return;

            for (int i = 0; i < assets.Count; i++)
            {
                Reset(assets[i]);
                Debug.Log($"[SkyGradientDefaults] Reset sky gradients on {paths[i]}.");
            }

            AssetDatabase.SaveAssets();
            Debug.Log($"[SkyGradientDefaults] Updated {assets.Count} settings asset(s).");
        }

        /// <summary>
        /// Restores one asset's sky gradients to the code defaults, without prompting.
        /// </summary>
        /// <param name="asset">The asset to reset. Ignored when null.</param>
        /// <remarks>
        /// Written through <see cref="SerializedObject"/> rather than by assigning fields, so the change
        /// is registered with Undo and the Inspector redraws — and because the fields are private.
        /// </remarks>
        public static void Reset(TimeOfDaySettings asset)
        {
            if (asset == null) return;

            SerializedObject serialized = new SerializedObject(asset);
            foreach ((string field, System.Func<Gradient> factory) in s_gradients)
            {
                SerializedProperty property = serialized.FindProperty(field);
                if (property == null)
                {
                    Debug.LogWarning($"[SkyGradientDefaults] '{field}' not found on {asset.name} — renamed? Skipped.");
                    continue;
                }

                property.gradientValue = factory();
            }

            serialized.ApplyModifiedProperties();
            EditorUtility.SetDirty(asset);
        }

        /// <summary>Collects every settings asset in the project.</summary>
        /// <param name="paths">Receives the asset paths, index-aligned with the result.</param>
        /// <returns>The loaded assets.</returns>
        private static List<TimeOfDaySettings> FindAssets(out List<string> paths)
        {
            List<TimeOfDaySettings> assets = new List<TimeOfDaySettings>();
            paths = new List<string>();

            foreach (string guid in AssetDatabase.FindAssets($"t:{nameof(TimeOfDaySettings)}"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                TimeOfDaySettings asset = AssetDatabase.LoadAssetAtPath<TimeOfDaySettings>(path);
                if (asset == null) continue;

                assets.Add(asset);
                paths.Add(path);
            }

            return assets;
        }

        /// <summary>
        /// Asks before overwriting, naming every asset that would change.
        /// </summary>
        /// <param name="paths">Asset paths about to be overwritten.</param>
        /// <returns>True when the caller should proceed.</returns>
        /// <remarks>
        /// This command discards authored art with no undo across assets, and it now covers four
        /// gradients rather than two — the sky-light tint and camera background it gained are ones a
        /// user could have spent real time on. Auto-confirmed in batch mode, where a modal dialog would
        /// hang a headless run forever.
        /// </remarks>
        private static bool Confirm(List<string> paths)
        {
            if (Application.isBatchMode) return true;

            StringBuilder message = new StringBuilder();
            message.AppendLine("Overwrite the zenith, horizon, sky-light tint and background gradients " +
                               "with the engine defaults on:");
            message.AppendLine();
            foreach (string path in paths) message.AppendLine($"  • {path}");
            message.AppendLine();
            message.Append("Authored colors on these assets are lost.");

            return EditorUtility.DisplayDialog("Reset Sky Gradients", message.ToString(), "Reset", "Cancel");
        }
    }
}
