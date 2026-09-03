using System.IO;
using UnityEditor;
using UnityEngine;

namespace Editor.WorldTools
{
    /// <summary>
    /// Authors the RF-2 sky material from <c>Minecraft/SkyboxShader</c>. A menu command rather than a
    /// hand-wired asset so a fresh clone reproduces it identically, and so neither the scene nor the
    /// World prefab has to be edited as text to get a sky.
    /// </summary>
    public static class SkyMaterialCreator
    {
        /// <summary>Shader the sky material instantiates.</summary>
        private const string SKY_SHADER_NAME = "Minecraft/SkyboxShader";

        /// <summary>Where the material is written; <see cref="World"/> loads it from here at startup.</summary>
        /// <remarks>
        /// Public so editor tooling that renders the sky loads the same material the game does, rather
        /// than repeating the path and drifting from it.
        /// </remarks>
        public const string SKY_MATERIAL_PATH = "Assets/Materials/Sky.mat";

        /// <summary>
        /// Creates (or refreshes) the sky material asset and selects it.
        /// </summary>
        [MenuItem("Minecraft Clone/Create Sky Material")]
        public static void CreateSkyMaterial()
        {
            Shader shader = Shader.Find(SKY_SHADER_NAME);
            if (shader == null)
            {
                Debug.LogError($"[SkyMaterialCreator] Shader '{SKY_SHADER_NAME}' not found — cannot create the sky material.");
                return;
            }

            string directory = Path.GetDirectoryName(SKY_MATERIAL_PATH);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            Material existing = AssetDatabase.LoadAssetAtPath<Material>(SKY_MATERIAL_PATH);
            if (existing != null)
            {
                // Re-point rather than recreate: the GUID is referenced by RenderSettings, so replacing
                // the asset would silently unbind every scene already using it.
                existing.shader = shader;
                EditorUtility.SetDirty(existing);
                AssetDatabase.SaveAssets();
                Debug.Log($"[SkyMaterialCreator] Refreshed the existing sky material at {SKY_MATERIAL_PATH}.");
                Selection.activeObject = existing;
                return;
            }

            Material material = new Material(shader) { name = "Sky" };
            AssetDatabase.CreateAsset(material, SKY_MATERIAL_PATH);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[SkyMaterialCreator] Created the sky material at {SKY_MATERIAL_PATH}. " +
                      "Assign it to the World prefab's Sky Material field, or let World load it by path at startup.");
            Selection.activeObject = material;
        }
    }
}
