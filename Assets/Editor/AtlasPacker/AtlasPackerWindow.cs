using System;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace Editor.AtlasPacker
{
    /// <summary>
    /// Editor window for generating a packed texture atlas from individual block textures.
    /// Textures are mapped to a grid by their array index in the AtlasConfiguration asset.
    /// </summary>
    public class AtlasPackerWindow : EditorWindow
    {
        private const int THUMBNAIL_SIZE = 48;

        private static readonly Regex s_prefixRegex = new Regex(@"^(\d+)-");

        [SerializeField]
        private AtlasConfiguration _config;

        [SerializeField]
        private string _saveLocation = "Assets/Textures/packed_texture_atlas.png";

        [SerializeField]
        private string _resourceLoadPath = "AtlasPacker";

        private Vector2 _scrollPos;
        private ReorderableList _reorderableList;
        private SerializedObject _serializedConfig;
        private Texture2D _atlasPreview;
        private bool _showPreview;

        /// <summary>
        /// Opens the Atlas Packer editor window from the menu bar.
        /// </summary>
        [MenuItem("Minecraft Clone/Atlas Packer")]
        public static void ShowWindow()
        {
            GetWindow<AtlasPackerWindow>("Atlas Packer");
        }

        private void OnGUI()
        {
            GUILayout.Label("Voxel Atlas Generator", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            EditorGUI.BeginChangeCheck();
            _config = (AtlasConfiguration)EditorGUILayout.ObjectField("Configuration Asset", _config, typeof(AtlasConfiguration), false);
            if (EditorGUI.EndChangeCheck())
            {
                // Config changed — rebuild the reorderable list
                _reorderableList = null;
                _serializedConfig = null;
                _atlasPreview = null;
            }

            if (_config == null)
            {
                EditorGUILayout.HelpBox("Please assign an AtlasConfiguration asset to use the tool. You can create one via Right-Click -> Create -> Minecraft Clone -> Atlas Configuration.", MessageType.Warning);
                return;
            }

            // Ensure serialized objects are initialized
            if (_serializedConfig == null || _serializedConfig.targetObject != _config)
            {
                _serializedConfig = new SerializedObject(_config);
                _reorderableList = null;
            }

            if (_reorderableList == null)
            {
                BuildReorderableList();
            }

            _serializedConfig.Update();

            EditorGUI.BeginChangeCheck();

            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);

            // --- Settings Section ---
            EditorGUILayout.LabelField("Settings", EditorStyles.boldLabel);
            _config.blockSize = EditorGUILayout.IntField("Block Size (Pixels)", _config.blockSize);
            EditorGUILayout.LabelField("Save Path");
            _saveLocation = EditorGUILayout.TextField(_saveLocation);
            EditorGUILayout.LabelField("Auto-Populate Folder (Resources/)");
            _resourceLoadPath = EditorGUILayout.TextField(_resourceLoadPath);

            EditorGUILayout.Space();

            // --- Action Buttons ---
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Auto-Populate Array", GUILayout.Height(30)))
            {
                AutoPopulateFromResources();
                _reorderableList = null; // Force rebuild after populate
                GUIUtility.ExitGUI(); // Safely exit the current GUI pass to avoid layout errors
                return;
            }

            if (GUILayout.Button("Preview Atlas", GUILayout.Height(30)))
            {
                _atlasPreview = BuildAtlasTexture();
                _showPreview = _atlasPreview != null;
            }

            if (GUILayout.Button("Generate Atlas", GUILayout.Height(30)))
            {
                GenerateAtlas();
            }

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space();

            // --- Atlas Preview Section ---
            if (_showPreview && _atlasPreview != null)
            {
                EditorGUILayout.LabelField("Atlas Preview", EditorStyles.boldLabel);

                int atlasBlocks = VoxelData.TextureAtlasSizeInBlocks;
                string resLabel = $"{_atlasPreview.width}x{_atlasPreview.height} ({atlasBlocks}x{atlasBlocks} grid)";
                EditorGUILayout.LabelField(resLabel, EditorStyles.miniLabel);

                // Draw the atlas preview scaled to fit the available window width
                float availableWidth = EditorGUIUtility.currentViewWidth - 30f;
                float previewSize = Mathf.Min(availableWidth, 512f);

                Rect previewRect = GUILayoutUtility.GetRect(previewSize, previewSize, GUILayout.ExpandWidth(false));
                EditorGUI.DrawPreviewTexture(previewRect, _atlasPreview, null, ScaleMode.ScaleToFit);

                EditorGUILayout.Space();
            }

            // --- Texture Mapping Section ---
            EditorGUILayout.LabelField("Texture Mapping", EditorStyles.boldLabel);
            EditorGUILayout.LabelField($"{_config.textures.Length} slots", EditorStyles.miniLabel);

            _reorderableList.DoLayoutList();

            EditorGUILayout.EndScrollView();

            _serializedConfig.ApplyModifiedProperties();

            if (EditorGUI.EndChangeCheck())
            {
                EditorUtility.SetDirty(_config);
            }
        }

        #region Reorderable List

        /// <summary>
        /// Builds the ReorderableList for the texture array with thumbnail previews.
        /// </summary>
        private void BuildReorderableList()
        {
            SerializedProperty texturesProp = _serializedConfig.FindProperty("textures");

            _reorderableList = new ReorderableList(_serializedConfig, texturesProp, true, true, true, true)
            {
                elementHeight = THUMBNAIL_SIZE + 8,

                drawHeaderCallback = rect => { EditorGUI.LabelField(rect, "ID    Texture"); },

                drawElementCallback = (rect, index, _, _) =>
                {
                    SerializedProperty element = texturesProp.GetArrayElementAtIndex(index);
                    rect.y += 4;
                    float lineHeight = THUMBNAIL_SIZE;

                    // Index label
                    Rect indexRect = new Rect(rect.x, rect.y, 30, lineHeight);
                    EditorGUI.LabelField(indexRect, index.ToString(), EditorStyles.boldLabel);

                    // Thumbnail preview
                    Rect thumbRect = new Rect(rect.x + 34, rect.y, THUMBNAIL_SIZE, THUMBNAIL_SIZE);
                    Texture2D tex = element.objectReferenceValue as Texture2D;
                    if (tex != null)
                    {
                        EditorGUI.DrawPreviewTexture(thumbRect, tex, null, ScaleMode.ScaleToFit);
                    }
                    else
                    {
                        EditorGUI.DrawRect(thumbRect, new Color(0.15f, 0.15f, 0.15f));
                        GUI.Label(thumbRect, "—", new GUIStyle(EditorStyles.centeredGreyMiniLabel)
                        {
                            fontSize = 16,
                            alignment = TextAnchor.MiddleCenter
                        });
                    }

                    // Object field for assigning texture
                    float fieldX = rect.x + 34 + THUMBNAIL_SIZE + 8;
                    float fieldWidth = rect.width - (34 + THUMBNAIL_SIZE + 8);
                    Rect fieldRect = new Rect(fieldX, rect.y + (lineHeight - EditorGUIUtility.singleLineHeight) * 0.5f, fieldWidth, EditorGUIUtility.singleLineHeight);
                    EditorGUI.PropertyField(fieldRect, element, GUIContent.none);
                }
            };
        }

        #endregion

        #region Atlas Building

        /// <summary>
        /// Builds the packed atlas texture in memory without writing to disk.
        /// Used for both the preview and the export path.
        /// </summary>
        /// <returns>The packed atlas texture, or null if the config has no textures.</returns>
        private Texture2D BuildAtlasTexture()
        {
            if (_config.textures == null || _config.textures.Length == 0)
            {
                EditorUtility.DisplayDialog("Error", "No textures assigned in configuration.", "OK");
                return null;
            }

            int blockSize = _config.blockSize;
            int atlasBlocks = VoxelData.TextureAtlasSizeInBlocks;
            int atlasPixels = blockSize * atlasBlocks;

            Texture2D atlas = new Texture2D(atlasPixels, atlasPixels, TextureFormat.RGBA32, false);
            atlas.filterMode = FilterMode.Point;

            // Clear atlas to transparent
            Color[] clearColors = new Color[atlasPixels * atlasPixels];
            for (int i = 0; i < clearColors.Length; i++) clearColors[i] = Color.clear;
            atlas.SetPixels(clearColors);

            for (int i = 0; i < _config.textures.Length; i++)
            {
                Texture2D tex = _config.textures[i];
                if (tex == null) continue;

                if (tex.width != blockSize || tex.height != blockSize)
                {
                    Debug.LogWarning($"AtlasPacker: Texture '{tex.name}' at index {i} does not match block size {blockSize}. Skipping.");
                    continue;
                }

                int blockX = i % atlasBlocks;
                int logicalY = i / atlasBlocks;
                int blockY = (atlasBlocks - 1) - logicalY;

                if (logicalY >= atlasBlocks)
                {
                    Debug.LogError($"AtlasPacker: Index {i} exceeds the maximum atlas capacity ({atlasBlocks * atlasBlocks} blocks). Texture '{tex.name}' was not packed.");
                    continue;
                }

                Color[] pixels = tex.GetPixels();
                atlas.SetPixels(blockX * blockSize, blockY * blockSize, blockSize, blockSize, pixels);
            }

            atlas.Apply();
            return atlas;
        }

        #endregion

        #region Auto-Populate

        /// <summary>
        /// Scans the Resources folder for textures with a numeric prefix (e.g., "000-stone")
        /// and assigns them to the configuration array at their corresponding index.
        /// </summary>
        private void AutoPopulateFromResources()
        {
            Texture2D[] loadedTextures = Resources.LoadAll<Texture2D>(_resourceLoadPath);
            if (loadedTextures.Length == 0)
            {
                EditorUtility.DisplayDialog("Warning", $"No textures found in Resources/{_resourceLoadPath}", "OK");
                return;
            }

            int maxIndex = -1;

            foreach (var tex in loadedTextures)
            {
                Match match = s_prefixRegex.Match(tex.name);
                if (match.Success)
                {
                    int index = int.Parse(match.Groups[1].Value);
                    if (index > maxIndex) maxIndex = index;
                }
            }

            if (maxIndex == -1)
            {
                EditorUtility.DisplayDialog("Notice", "Could not find any textures with the prefix format '000-name'. Ensure your PNGs are fully prefixed before auto-populating.", "OK");
                return;
            }

            // Expand the array if needed, but don't shrink and lose assignments.
            int newSize = Mathf.Max(_config.textures.Length, maxIndex + 1);

            Undo.RecordObject(_config, "Auto-Populate Atlas Config");

            Texture2D[] newArray = new Texture2D[newSize];

            // Retain old assignments
            for (int i = 0; i < _config.textures.Length; i++)
            {
                newArray[i] = _config.textures[i];
            }

            int populatedCount = 0;
            foreach (var tex in loadedTextures)
            {
                Match match = s_prefixRegex.Match(tex.name);
                if (match.Success)
                {
                    int index = int.Parse(match.Groups[1].Value);
                    if (tex.width != _config.blockSize || tex.height != _config.blockSize)
                    {
                        Debug.LogWarning($"AtlasPacker: Skipping {tex.name} because its size ({tex.width}x{tex.height}) does not match configured block size ({_config.blockSize}).");
                        continue;
                    }

                    newArray[index] = tex;
                    populatedCount++;
                }
            }

            _config.textures = newArray;
            EditorUtility.SetDirty(_config);
            AssetDatabase.SaveAssets();

            EditorUtility.DisplayDialog("Success", $"Auto-populated {populatedCount} textures successfully.", "OK");
        }

        #endregion

        #region Generate Atlas (Export)

        /// <summary>
        /// Generates the packed texture atlas and saves it to disk as a PNG file.
        /// </summary>
        private void GenerateAtlas()
        {
            Texture2D atlas = BuildAtlasTexture();
            if (atlas == null) return;

            int packedCount = 0;
            foreach (Texture2D t in _config.textures)
            {
                if (t != null) packedCount++;
            }

            byte[] bytes = atlas.EncodeToPNG();
            try
            {
                string path = _saveLocation;
                if (!path.StartsWith("Assets/"))
                {
                    EditorUtility.DisplayDialog("Error", "Save Path must start with 'Assets/'", "OK");
                    return;
                }

                string fullPath = Path.Combine(Application.dataPath, path.Substring(7));
                string dir = Path.GetDirectoryName(fullPath);
                if (!Directory.Exists(dir) && dir != null)
                {
                    Directory.CreateDirectory(dir);
                }

                File.WriteAllBytes(fullPath, bytes);
                AssetDatabase.Refresh();

                int atlasPixels = _config.blockSize * VoxelData.TextureAtlasSizeInBlocks;
                EditorUtility.DisplayDialog("Success", $"Successfully packed {packedCount} textures into {atlasPixels}x{atlasPixels} atlas.\nSaved to: {path}", "OK");
            }
            catch (Exception e)
            {
                Debug.LogError($"Atlas Packer: Couldn't save atlas to file. Exception: {e}");
                EditorUtility.DisplayDialog("Error", $"Failed to save atlas: {e.Message}", "OK");
            }
        }

        #endregion
    }
}
