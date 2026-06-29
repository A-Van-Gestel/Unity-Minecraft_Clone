#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace Legacy.Editor
{
    /// <summary>
    /// Custom Inspector for <see cref="LegacyBiomeAttributes"/> that makes all fields read-only.
    /// Legacy biome assets are frozen and must not be modified to preserve seed reproducibility.
    /// </summary>
    [CustomEditor(typeof(LegacyBiomeAttributes))]
    public class LegacyBiomeAttributesEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            EditorGUILayout.HelpBox(
                "LEGACY BIOME — READ ONLY\n\n" +
                "This biome configuration is frozen. Modifying any value would alter the " +
                "deterministic terrain output for existing Legacy worlds.\n\n" +
                "To create new biomes, use 'Minecraft > Standard Biome Attributes' instead.",
                MessageType.Warning);

            EditorGUILayout.Space();

            GUI.enabled = false;
            DrawDefaultInspector();
            GUI.enabled = true;
        }
    }
}
#endif
