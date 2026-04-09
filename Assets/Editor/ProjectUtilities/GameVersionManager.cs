using System;
using Data.Enums;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Editor.ProjectUtilities
{
    /// <summary>
    /// Manages the automatic generation of the game's version string.
    /// Provides a Project Settings UI and hooks into the build pipeline.
    /// </summary>
    public class GameVersionManager : IPreprocessBuildWithReport
    {
        private const string PREF_KEY_STAGE = "MC_DevStage";

        // Implements IPreprocessBuildWithReport
        public int callbackOrder => 0;

        /// <summary>
        /// Retrieves the currently selected development stage from EditorPrefs.
        /// </summary>
#pragma warning disable UDR0001 // False positive: No backing field to reset, strictly accesses EditorPrefs
        public static DevelopmentStage CurrentStage
        {
            get => (DevelopmentStage)EditorPrefs.GetInt(PREF_KEY_STAGE, (int)DevelopmentStage.Alpha);
            set => EditorPrefs.SetInt(PREF_KEY_STAGE, (int)value);
        }
#pragma warning restore UDR0001

        /// <summary>
        /// Generates the formatted version string based on the current date and stage.
        /// </summary>
        public static string GenerateVersionString()
        {
            string date = DateTime.Now.ToString("yyyy-MM-dd");
            DevelopmentStage stage = CurrentStage;

            // If it's a full release, you might just want the date (or a semantic version, but we stick to date here)
            string stageSuffix = stage == DevelopmentStage.Release ? "" : $" - {stage}";

            return $"{date}{stageSuffix}";
        }

        /// <summary>
        /// Called automatically by Unity right before a build starts.
        /// Bakes the generated string into the built executable.
        /// </summary>
        public void OnPreprocessBuild(BuildReport report)
        {
            PlayerSettings.bundleVersion = GenerateVersionString();
            Debug.Log($"[GameVersionManager] Baked Game Version for Build: {PlayerSettings.bundleVersion}");
        }

        /// <summary>
        /// Creates a custom settings panel in Edit > Project Settings > Game Versioning.
        /// </summary>
        [SettingsProvider]
        public static SettingsProvider CreateVersionSettingsProvider()
        {
            return new SettingsProvider("Project/Game Versioning", SettingsScope.Project)
            {
                guiHandler = _ =>
                {
                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField("Version Configuration", EditorStyles.boldLabel);

                    EditorGUI.BeginChangeCheck();
                    DevelopmentStage newStage = (DevelopmentStage)EditorGUILayout.EnumPopup("Development Stage", CurrentStage);

                    if (EditorGUI.EndChangeCheck())
                    {
                        CurrentStage = newStage;

                        // Instantly update the PlayerSettings so the Unity UI reflects it
                        PlayerSettings.bundleVersion = GenerateVersionString();
                    }

                    EditorGUILayout.Space();
                    EditorGUILayout.HelpBox($"Preview:\nv{GenerateVersionString()}", MessageType.Info);

                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField("Note: The date will automatically update to the current day when you press Play or create a Build.", EditorStyles.wordWrappedLabel);
                },
            };
        }
    }
}
