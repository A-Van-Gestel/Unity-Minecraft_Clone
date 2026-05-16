using System;
using System.Collections.Generic;
using System.IO;
using Data.WorldTypes;
using Editor.Libraries;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace Editor.WorldTools
{
    /// <summary>
    /// Multi-tab editor window for authoring and previewing world generation (terrain, caves, biomes, blending).
    /// Split across partial class files: core (this file), CrossSection, NoiseChannels, BiomeEditor, WorldBlending.
    /// </summary>
    public partial class WorldGenPreviewWindow : EditorWindow
    {
        public enum ResolutionOptions
        {
            X128 = 128,
            X256 = 256,
            X512 = 512,
            X768 = 768,
            X1024 = 1024,
            X1536 = 1536,
            X2048 = 2048,
        }

        // --- Tab State ---
        private int _selectedTabIndex;
        private static readonly string[] s_tabLabels = { "Cross-Section", "Noise Channels", "Biome Editing", "World Blending" };

        // --- Shared World Type & Biome Selection ---
        private WorldTypeDefinition _worldType;
        private int _seaLevel = 45;
        private const string BIOME_SAVE_DIR = "Assets/Data/WorldGen/Biomes";
        private List<StandardBiomeAttributes> _biomeAssets;
        private StandardBiomeAttributes _biome;
        private int _selectedBiomeIndex = -1;
        private string _biomeSearchText = "";
        private Vector2 _biomeListScrollPos;
        private DateTime _lastAssetWriteTime;

        // --- Shared Preview State ---
        private int _seed = 1337;
        private Vector2Int _offset = Vector2Int.zero;
        private float _zoom = 1f;
        private int _chunkRadius = 2;
        private int3 _crosshairPos = new int3(0, 60, 0);
        private bool _autoGenerate = true;
        private bool _liveUpdate = true;
        private bool _showChunkBorders = true;

        // --- Inline Biome Editing ---
        private SerializedObject _biomeSerializedObject;
        private int _lastPreviewTabIndex;

        [MenuItem("Minecraft Clone/World Gen Preview")]
        public static void ShowWindow()
        {
            GetWindow<WorldGenPreviewWindow>("World Gen Preview");
        }

        private void OnEnable()
        {
            AutoDetectWorldType();
            RefreshBiomeList();
            OnEnableBlendingTab();

#pragma warning disable UDR0004
            EditorApplication.update -= PollForAssetChanges;
            EditorApplication.update += PollForAssetChanges;
#pragma warning restore UDR0004
        }

        /// <summary>
        /// Auto-detects a <see cref="WorldTypeDefinition"/> if exactly one exists with Standard biomes.
        /// </summary>
        private void AutoDetectWorldType()
        {
            if (_worldType != null) return;

            string[] guids = AssetDatabase.FindAssets("t:WorldTypeDefinition");
            WorldTypeDefinition candidate = null;
            int validCount = 0;

            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                WorldTypeDefinition wt = AssetDatabase.LoadAssetAtPath<WorldTypeDefinition>(path);
                if (wt == null || wt.biomes == null) continue;

                bool hasStandard = false;
                foreach (BiomeBase b in wt.biomes)
                {
                    if (b is StandardBiomeAttributes)
                    {
                        hasStandard = true;
                        break;
                    }
                }

                if (!hasStandard) continue;
                candidate = wt;
                validCount++;
            }

            if (validCount == 1)
            {
                _worldType = candidate;
                _seaLevel = _worldType.seaLevel;
            }
        }

        /// <summary>
        /// Scans the AssetDatabase for all <see cref="StandardBiomeAttributes"/> ScriptableObjects
        /// and populates the biome selection list.
        /// </summary>
        private void RefreshBiomeList()
        {
            _biomeAssets = new List<StandardBiomeAttributes>();
            string[] guids = AssetDatabase.FindAssets("t:StandardBiomeAttributes");
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                StandardBiomeAttributes asset = AssetDatabase.LoadAssetAtPath<StandardBiomeAttributes>(path);
                if (asset != null) _biomeAssets.Add(asset);
            }
        }

        private void OnDisable()
        {
            EditorApplication.update -= PollForAssetChanges;
            OnDisableBlendingTab();
            OnDisableCrossSectionTab();
            OnDisableNoiseChannelsTab();
        }

        /// <summary>
        /// Polls the biome asset's file modification timestamp each editor frame.
        /// If the file was re-saved externally, regenerate the active preview automatically.
        /// </summary>
        private void PollForAssetChanges()
        {
            if (_biome == null || !_autoGenerate) return;

            string assetPath = AssetDatabase.GetAssetPath(_biome);
            if (string.IsNullOrEmpty(assetPath)) return;

            string fullPath = Path.GetFullPath(assetPath);
            DateTime writeTime = File.GetLastWriteTimeUtc(fullPath);

            if (writeTime != _lastAssetWriteTime)
            {
                _lastAssetWriteTime = writeTime;
                RegenerateActivePreview();
            }
        }

        /// <summary>
        /// Triggers preview regeneration on whichever preview tab is currently active.
        /// Called by the asset change poller and by the biome editor on live-update.
        /// </summary>
        private void RegenerateActivePreview()
        {
            switch (_lastPreviewTabIndex)
            {
                case 0: GenerateCrossSectionPreview(); break;
                case 1: GenerateNoiseChannelsPreview(); break;
                case 3: GenerateBlendingPreview(); break;
            }
        }

        private void OnGUI()
        {
            _selectedTabIndex = GUILayout.Toolbar(_selectedTabIndex, s_tabLabels, GUILayout.Height(25));

            // Track which preview tab was last selected (skip tab 2 = Biome Editing, not a preview)
            if (_selectedTabIndex != 2)
                _lastPreviewTabIndex = _selectedTabIndex;

            switch (_selectedTabIndex)
            {
                case 0:
                    DrawCrossSectionTab();
                    break;
                case 1:
                    DrawNoiseChannelsTab();
                    break;
                case 2:
                    DrawBiomeEditorTab();
                    break;
                case 3:
                    DrawWorldBlendingTab();
                    break;
            }
        }

        #region Shared Biome List

        /// <summary>
        /// Draws the left-pane biome selection list used by tabs 0, 1, and 2.
        /// </summary>
        private void DrawBiomeList()
        {
            EditorGUILayout.BeginVertical(GUILayout.Width(180));

            // World Type selector
            EditorGUI.BeginChangeCheck();
            _worldType = (WorldTypeDefinition)EditorGUILayout.ObjectField(
                _worldType, typeof(WorldTypeDefinition), false, GUILayout.Height(18));
            if (EditorGUI.EndChangeCheck() && _worldType != null)
            {
                _seaLevel = _worldType.seaLevel;
            }

            if (_worldType != null)
            {
                GUI.enabled = false;
                EditorGUILayout.IntField("Sea Level", _seaLevel, GUILayout.Height(16));
                GUI.enabled = true;
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Biomes", EditorStyles.boldLabel);

            EditorGUIHelper.DrawSearchableSelectionList(
                _biomeAssets,
                ref _biomeSearchText,
                ref _biomeListScrollPos,
                ref _selectedBiomeIndex,
                (biome, search) => string.IsNullOrEmpty(search) || biome.name.ToLower().Contains(search.ToLower()),
                (rect, biome, _) => { GUI.Label(rect, $" {biome.name}", EditorStyles.toolbarButton); },
                index =>
                {
                    _biome = _biomeAssets[index];
                    _lastAssetWriteTime = default;
                    _biomeSerializedObject = new SerializedObject(_biome);
                    if (_autoGenerate) RegenerateActivePreview();
                }
            );

            // --- List management buttons ---
            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("Add New"))
            {
                if (!AssetDatabase.IsValidFolder(BIOME_SAVE_DIR))
                {
                    Directory.CreateDirectory(BIOME_SAVE_DIR);
                    AssetDatabase.Refresh();
                }

                string path = AssetDatabase.GenerateUniqueAssetPath($"{BIOME_SAVE_DIR}/New Biome.asset");
                StandardBiomeAttributes newBiome = CreateInstance<StandardBiomeAttributes>();
                AssetDatabase.CreateAsset(newBiome, path);
                AssetDatabase.SaveAssets();

                RefreshBiomeList();
                _selectedBiomeIndex = _biomeAssets.IndexOf(newBiome);
                _biome = newBiome;
                _biomeSerializedObject = new SerializedObject(_biome);
            }

            GUI.enabled = _biome != null;

            if (GUILayout.Button("Duplicate"))
            {
                string sourcePath = AssetDatabase.GetAssetPath(_biome);
                string newPath = AssetDatabase.GenerateUniqueAssetPath(
                    $"{BIOME_SAVE_DIR}/{_biome.name} (Copy).asset");
                AssetDatabase.CopyAsset(sourcePath, newPath);
                AssetDatabase.SaveAssets();

                RefreshBiomeList();
                StandardBiomeAttributes duplicated = AssetDatabase.LoadAssetAtPath<StandardBiomeAttributes>(newPath);
                _selectedBiomeIndex = _biomeAssets.IndexOf(duplicated);
                _biome = duplicated;
                _biomeSerializedObject = new SerializedObject(_biome);
                _lastAssetWriteTime = default;
                if (_autoGenerate) RegenerateActivePreview();
            }

            GUI.backgroundColor = new Color(1f, 0.6f, 0.6f);
            if (GUILayout.Button("Delete"))
            {
                if (EditorUtility.DisplayDialog(
                        "Delete Biome",
                        $"Are you sure you want to delete '{_biome.name}'?\nThis cannot be undone.",
                        "Delete",
                        "Cancel"))
                {
                    string path = AssetDatabase.GetAssetPath(_biome);
                    AssetDatabase.DeleteAsset(path);
                    AssetDatabase.SaveAssets();

                    _biome = null;
                    _selectedBiomeIndex = -1;
                    _biomeSerializedObject = null;
                    RefreshBiomeList();
                }
            }

            GUI.backgroundColor = Color.white;
            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();

            if (GUILayout.Button("↻ Refresh List"))
            {
                RefreshBiomeList();
            }

            EditorGUILayout.EndVertical();
        }

        #endregion
    }
}
