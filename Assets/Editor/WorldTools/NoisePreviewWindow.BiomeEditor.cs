using UnityEditor;
using UnityEngine;

namespace Editor.WorldTools
{
    /// <summary>
    /// Partial class containing the Biome Editing tab for the Noise Preview window.
    /// Provides inline <see cref="SerializedObject"/>-based editing of all
    /// <see cref="Data.WorldTypes.StandardBiomeAttributes"/> fields with Undo support and live-update.
    /// </summary>
    public partial class NoisePreviewWindow
    {
        #region Tab 2: Biome Editing

        private Vector2 _biomeEditorScrollPos;

        private void DrawBiomeEditorTab()
        {
            EditorGUILayout.BeginHorizontal();
            DrawBiomeList();

            EditorGUILayout.BeginVertical();
            GUILayout.Label("Biome Editor", EditorStyles.boldLabel);

            if (_biome == null || _biomeSerializedObject == null)
            {
                EditorGUILayout.HelpBox("Select a biome from the list to begin editing.", MessageType.Warning);
                EditorGUILayout.EndVertical();
                EditorGUILayout.EndHorizontal();
                return;
            }

            EditorGUILayout.HelpBox(
                "Inline biome property editor with Undo support. Implementation in progress.",
                MessageType.Info);

            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
        }

        #endregion
    }
}
