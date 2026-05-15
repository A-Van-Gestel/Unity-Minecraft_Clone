using UnityEditor;
using UnityEngine;

namespace Editor.WorldTools
{
    /// <summary>
    /// Partial class containing the Noise Channels tab for the Noise Preview window.
    /// Visualizes individual multi-noise channels (Continentalness, Erosion, P&amp;V)
    /// and their spline-mapped outputs as 2D top-down maps.
    /// </summary>
    public partial class NoisePreviewWindow
    {
        #region Tab 1: Noise Channels

        private Texture2D _noiseChannelsTexture;

        private void OnDisableNoiseChannelsTab()
        {
            if (_noiseChannelsTexture != null)
            {
                DestroyImmediate(_noiseChannelsTexture);
                _noiseChannelsTexture = null;
            }
        }

        private void DrawNoiseChannelsTab()
        {
            EditorGUILayout.BeginHorizontal();
            DrawBiomeList();

            EditorGUILayout.BeginVertical();
            GUILayout.Label("Noise Channels Preview", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Individual multi-noise channel visualization with raw and spline-mapped modes. Implementation in progress.",
                MessageType.Info);

            if (_biome == null)
            {
                EditorGUILayout.HelpBox("Select a biome from the list to begin.", MessageType.Warning);
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
        }

        private void GenerateNoiseChannelsPreview()
        {
            // Phase 4 implementation
        }

        #endregion
    }
}
