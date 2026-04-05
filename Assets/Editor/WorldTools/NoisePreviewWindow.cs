using System;
using System.Collections.Generic;
using System.IO;
using Data.WorldTypes;
using Editor.Libraries;
using Jobs.Data;
using Libraries;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Random = Unity.Mathematics.Random;

namespace Editor.WorldTools
{
    public class NoisePreviewWindow : EditorWindow
    {
        public enum NoiseTarget
        {
            Terrain,
            MajorFloraZone,
            CaveLayer,
            Lode
        }

        public enum ResolutionOptions
        {
            X128 = 128,
            X256 = 256,
            X512 = 512,
            X1024 = 1024
        }

        // Biome selection state
        private const string BIOME_SAVE_DIR = "Assets/Data/WorldGen/Biomes";
        private List<StandardBiomeAttributes> _biomeAssets;
        private StandardBiomeAttributes _biome;
        private int _selectedBiomeIndex = -1;
        private string _biomeSearchText = "";
        private Vector2 _biomeListScrollPos;

        // Noise config state
        private NoiseTarget _target;
        private int _targetIndex;
        private ResolutionOptions _resolution = ResolutionOptions.X256;

        private int _seed = 1337;
        private int _sliceY = 30;
        private Vector2Int _offset = Vector2Int.zero;
        private float _zoom = 1f;

        private bool _showThresholdOverlay = true;
        private bool _autoGenerate = true;
        private bool _showTerrainBackdrop = true;
        private bool _showChunkBorders = true;
        private bool _showWaterLevel = false;

        private Texture2D _previewTexture;
        private DateTime _lastAssetWriteTime;

        [MenuItem("Minecraft Clone/Noise Preview")]
        public static void ShowWindow()
        {
            GetWindow<NoisePreviewWindow>("Noise Preview");
        }

        private void OnEnable()
        {
            // Auto-discover all StandardBiomeAttributes assets in the project
            RefreshBiomeList();

            // Poll for external asset changes (e.g. user modifies biome in Inspector and presses Ctrl+S)
            EditorApplication.update += PollForAssetChanges;
        }

        /// <summary>
        /// Scans the AssetDatabase for all StandardBiomeAttributes ScriptableObjects
        /// and populates the biome selection list.
        /// </summary>
        private void RefreshBiomeList()
        {
            _biomeAssets = new List<StandardBiomeAttributes>();
            string[] guids = AssetDatabase.FindAssets("t:StandardBiomeAttributes");
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var asset = AssetDatabase.LoadAssetAtPath<StandardBiomeAttributes>(path);
                if (asset != null) _biomeAssets.Add(asset);
            }
        }

        private void OnDisable()
        {
            EditorApplication.update -= PollForAssetChanges;
        }

        /// <summary>
        /// Polls the biome asset's file modification timestamp each editor frame.
        /// If the file was re-saved externally, regenerate the preview automatically.
        /// </summary>
        private void PollForAssetChanges()
        {
            if (_biome == null || !_autoGenerate) return;

            string assetPath = AssetDatabase.GetAssetPath(_biome);
            if (string.IsNullOrEmpty(assetPath)) return;

            string fullPath = Path.GetFullPath(assetPath);
            var writeTime = File.GetLastWriteTimeUtc(fullPath);

            if (writeTime != _lastAssetWriteTime)
            {
                _lastAssetWriteTime = writeTime;
                GeneratePreview();
            }
        }

        private void OnGUI()
        {
            EditorGUILayout.BeginHorizontal();

            // --- Left Pane: Biome Selection List ---
            DrawBiomeList();

            // --- Right Pane: Configuration & Preview ---
            EditorGUILayout.BeginVertical();
            GUILayout.Label("Noise Preview Configuration", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();

            _resolution = (ResolutionOptions)EditorGUILayout.EnumPopup("Preview Resolution", _resolution);

            var prevTarget = _target;
            _target = (NoiseTarget)EditorGUILayout.EnumPopup("Noise Target", _target);
            // Reset index when switching targets to prevent stale index references
            if (_target != prevTarget) _targetIndex = -1;

            if (_target == NoiseTarget.CaveLayer && _biome != null)
            {
                if (_biome.caveLayers != null && _biome.caveLayers.Length > 0)
                {
                    string[] options = new string[_biome.caveLayers.Length + 1];
                    options[0] = "All (Composite)";
                    for (int i = 0; i < _biome.caveLayers.Length; i++)
                    {
                        options[i + 1] = string.IsNullOrEmpty(_biome.caveLayers[i].layerName) ? $"Layer {i}" : _biome.caveLayers[i].layerName;
                    }

                    int selected = _targetIndex + 1;
                    selected = EditorGUILayout.Popup("Cave Layer", selected, options);
                    _targetIndex = selected - 1;
                }
                else
                {
                    EditorGUILayout.HelpBox("No Cave Layers defined in Biome.", MessageType.Warning);
                }
            }
            else if (_target == NoiseTarget.Lode && _biome != null)
            {
                if (_biome.lodes != null && _biome.lodes.Length > 0)
                {
                    string[] options = new string[_biome.lodes.Length + 1];
                    options[0] = "All (Composite)";
                    for (int i = 0; i < _biome.lodes.Length; i++)
                    {
                        options[i + 1] = string.IsNullOrEmpty(_biome.lodes[i].nodeName) ? $"Lode {i}" : _biome.lodes[i].nodeName;
                    }

                    int selected = _targetIndex + 1;
                    selected = EditorGUILayout.Popup("Lode", selected, options);
                    _targetIndex = selected - 1;
                }
                else
                {
                    EditorGUILayout.HelpBox("No Lodes defined in Biome.", MessageType.Warning);
                }
            }

            EditorGUILayout.Space();
            _seed = EditorGUILayout.IntField("World Seed", _seed);
            _zoom = EditorGUILayout.Slider("Zoom Scale", _zoom, 0.1f, 10f);
            _offset = EditorGUILayout.Vector2IntField("XZ Offset", _offset);

            // Only show Y Slice for 3D noises
            if (_target == NoiseTarget.CaveLayer || _target == NoiseTarget.Lode)
            {
                _sliceY = EditorGUILayout.IntSlider("World Y (Slice Height)", _sliceY, 0, 256);
            }

            if (_target != NoiseTarget.Terrain)
            {
                _showThresholdOverlay = EditorGUILayout.Toggle("Show Threshold Overlay", _showThresholdOverlay);
                _showTerrainBackdrop = EditorGUILayout.Toggle("Grayscale Terrain Backdrop", _showTerrainBackdrop);
            }
            else
            {
                _showWaterLevel = EditorGUILayout.Toggle("Show Water Level (Sea Level)", _showWaterLevel);
            }

            _showChunkBorders = EditorGUILayout.Toggle("Show Chunk Borders", _showChunkBorders);

            _autoGenerate = EditorGUILayout.Toggle("Auto Generate On Change", _autoGenerate);

            bool changed = EditorGUI.EndChangeCheck();

            if (GUILayout.Button("Generate Preview") || (changed && _autoGenerate))
            {
                GeneratePreview();
            }

            EditorGUILayout.Space();

            if (_previewTexture != null && _biome != null)
            {
                // Draw Texture centered
                Rect rect = GUILayoutUtility.GetAspectRect(1f);
                if (rect.width > 512)
                {
                    rect.width = 512;
                    rect.height = 512;
                    rect.x = (position.width - 512) / 2;
                }

                GUI.DrawTexture(rect, _previewTexture, ScaleMode.ScaleToFit);
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>
        /// Draws the left-pane biome selection list using the shared searchable list widget.
        /// </summary>
        private void DrawBiomeList()
        {
            EditorGUILayout.BeginVertical(GUILayout.Width(180));
            EditorGUILayout.LabelField("Biomes", EditorStyles.boldLabel);

            EditorGUIHelper.DrawSearchableSelectionList(
                _biomeAssets,
                ref _biomeSearchText,
                ref _biomeListScrollPos,
                ref _selectedBiomeIndex,
                (biome, search) => string.IsNullOrEmpty(search) || biome.name.ToLower().Contains(search.ToLower()),
                (rect, biome, _) =>
                {
                    GUI.Label(rect, $" {biome.name}", EditorStyles.toolbarButton);
                },
                index =>
                {
                    _biome = _biomeAssets[index];
                    _targetIndex = -1; // Reset to composite when switching biomes
                    _lastAssetWriteTime = default; // Force a timestamp refresh
                    if (_autoGenerate) GeneratePreview();
                }
            );

            // --- List management buttons ---
            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("Add New"))
            {
                // Ensure directory exists
                if (!AssetDatabase.IsValidFolder(BIOME_SAVE_DIR))
                {
                    Directory.CreateDirectory(BIOME_SAVE_DIR);
                    AssetDatabase.Refresh();
                }

                string path = AssetDatabase.GenerateUniqueAssetPath($"{BIOME_SAVE_DIR}/New Biome.asset");
                var newBiome = CreateInstance<StandardBiomeAttributes>();
                AssetDatabase.CreateAsset(newBiome, path);
                AssetDatabase.SaveAssets();

                RefreshBiomeList();
                _selectedBiomeIndex = _biomeAssets.IndexOf(newBiome);
                _biome = newBiome;
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
                var duplicated = AssetDatabase.LoadAssetAtPath<StandardBiomeAttributes>(newPath);
                _selectedBiomeIndex = _biomeAssets.IndexOf(duplicated);
                _biome = duplicated;
                _lastAssetWriteTime = default;
                if (_autoGenerate) GeneratePreview();
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
                    _previewTexture = null;
                    RefreshBiomeList();
                }
            }

            GUI.backgroundColor = Color.white;
            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();

            // Refresh button in case new biome assets were added externally
            if (GUILayout.Button("↻ Refresh List"))
            {
                RefreshBiomeList();
            }

            EditorGUILayout.EndVertical();
        }

        private void GeneratePreview()
        {
            if (_biome == null) return;

            // Editor Hotfix: Because this runs outside of Play Mode, the FastNoiseLite unmanaged
            // pointer arrays (Gradients/RandVecs) might not be allocated. We must initialize them safely.
            FastNoiseLite.InitializeLookupTables();

            int texSize = (int)_resolution;
            if (_previewTexture == null || _previewTexture.width != texSize)
            {
                _previewTexture = new Texture2D(texSize, texSize, TextureFormat.RGBA32, false);
                _previewTexture.filterMode = FilterMode.Point;
            }

            Color[] pixels = new Color[texSize * texSize];

            FastNoiseLite terrainNoise = CreateNoiseFromConfig(_biome.terrainNoiseConfig);
            bool terrainNormalized = _biome.terrainNoiseConfig.NormalizeToZeroOne;

            // Setup composite arrays
            FastNoiseLite[] layerNoises = null;
            float[] layerThresholds = null;
            Color[] layerColors = null;
            CaveMode[] layerModes = null;

            bool isComposite = _targetIndex == -1 && (_target == NoiseTarget.CaveLayer || _target == NoiseTarget.Lode);

            if (_target == NoiseTarget.CaveLayer)
            {
                int len = isComposite ? _biome.caveLayers.Length : 1;
                layerNoises = new FastNoiseLite[len];
                layerThresholds = new float[len];
                layerColors = new Color[len];
                layerModes = new CaveMode[len];

                for (int i = 0; i < len; i++)
                {
                    int queryIndex = isComposite ? i : _targetIndex;
                    if (queryIndex >= _biome.caveLayers.Length) continue;

                    var layer = _biome.caveLayers[queryIndex];
                    layerNoises[i] = CreateNoiseFromConfig(layer.noiseConfig);
                    layerThresholds[i] = layer.threshold;
                    layerColors[i] = layer.previewColor;
                    layerModes[i] = layer.mode;
                }
            }
            else if (_target == NoiseTarget.Lode)
            {
                int len = isComposite ? _biome.lodes.Length : 1;
                layerNoises = new FastNoiseLite[len];
                layerThresholds = new float[len];
                layerColors = new Color[len];
                layerModes = new CaveMode[len];

                for (int i = 0; i < len; i++)
                {
                    int queryIndex = isComposite ? i : _targetIndex;
                    if (queryIndex >= _biome.lodes.Length) continue;

                    var layer = _biome.lodes[queryIndex];
                    layerNoises[i] = CreateNoiseFromConfig(layer.noiseConfig);
                    layerThresholds[i] = 0.5f; // Lode logic hardcoded internally
                    layerColors[i] = layer.previewColor;
                    layerModes[i] = CaveMode.Blob; // Lodes evaluate as 3D blobs
                }
            }
            else if (_target == NoiseTarget.MajorFloraZone)
            {
                layerNoises = new[] { CreateNoiseFromConfig(_biome.majorFloraZoneNoiseConfig) };
                layerThresholds = new[] { 1f - _biome.majorFloraZoneCoverage };
                layerColors = new[] { Color.red };
                layerModes = new[] { CaveMode.Blob };
            }

            // Loop Pixels
            for (int z = 0; z < texSize; z++)
            {
                for (int x = 0; x < texSize; x++)
                {
                    float worldX = (x * _zoom) + _offset.x;
                    float worldZ = (z * _zoom) + _offset.y;

                    Color pixelColor = Color.black;

                    // Backdrop calculation
                    if (_target == NoiseTarget.Terrain || _showTerrainBackdrop)
                    {
                        float tNoise = terrainNoise.GetNoise(worldX, worldZ);

                        if (_target == NoiseTarget.Terrain && _showWaterLevel)
                        {
                            int terrainHeight = (int)math.floor(_biome.baseTerrainHeight + tNoise * _biome.terrainAmplitude);
                            if (terrainHeight < VoxelData.SeaLevel)
                            {
                                // Show water mask
                                pixelColor = new Color(0.15f, 0.45f, 0.85f, 1f); // Nice deep ocean blue
                            }
                            else
                            {
                                float tDisplay = terrainNormalized ? tNoise : (tNoise + 1f) / 2f;
                                tDisplay = math.clamp(tDisplay, 0.1f, 1f);
                                pixelColor = new Color(tDisplay, tDisplay, tDisplay, 1f);
                            }
                        }
                        else
                        {
                            float tDisplay = terrainNormalized ? tNoise : (tNoise + 1f) / 2f;
                            tDisplay = math.clamp(tDisplay, 0.1f, 1f); // keep it slightly above black for contrast
                            pixelColor = new Color(tDisplay, tDisplay, tDisplay, 1f);
                        }
                    }

                    // Foreground Evaluation (Non-Terrain)
                    if (_target != NoiseTarget.Terrain && layerNoises != null)
                    {
                        bool isFirstHighlight = true;
                        for (int i = 0; i < layerNoises.Length; i++)
                        {
                            float noiseVal = EvaluateNoiseVal(layerNoises[i], worldX, worldZ, _target, layerModes[i]);

                            // Visual normalized rendering if not in composite AND not showing threshold
                            if (!isComposite && !_showThresholdOverlay && _target != NoiseTarget.MajorFloraZone)
                            {
                                // Show raw grayscale of the noise (simulate Normalize behavior)
                                bool isNorm = false;
                                if (_target == NoiseTarget.CaveLayer) isNorm = _biome.caveLayers[_targetIndex].noiseConfig.NormalizeToZeroOne;
                                else if (_target == NoiseTarget.Lode) isNorm = _biome.lodes[_targetIndex].noiseConfig.NormalizeToZeroOne;

                                float rawDisplay = isNorm ? noiseVal : (noiseVal + 1f) / 2f;
                                rawDisplay = math.clamp(rawDisplay, 0f, 1f);
                                pixelColor = new Color(rawDisplay, rawDisplay, rawDisplay, 1f);
                            }

                            // Layer Threshold highlight Logic (Red/Custom Colors)
                            if (_showThresholdOverlay && noiseVal > layerThresholds[i])
                            {
                                if (isFirstHighlight)
                                {
                                    // Lerp boldly on top of the terrain backdrop
                                    pixelColor = Color.Lerp(pixelColor, layerColors[i], 0.8f);
                                    isFirstHighlight = false;
                                }
                                else
                                {
                                    // Screen Blend (Max) so overlapping tunnel colors combine into high-brightness visibility
                                    pixelColor = new Color(
                                        math.max(pixelColor.r, layerColors[i].r),
                                        math.max(pixelColor.g, layerColors[i].g),
                                        math.max(pixelColor.b, layerColors[i].b),
                                        1f
                                    );
                                }

                                // Overlay Flora Placement spots as yellow dots
                                if (_target == NoiseTarget.MajorFloraZone)
                                {
                                    int spacing = math.max(1, _biome.majorFloraPlacementSpacing);
                                    int cellX = (int)math.floor(worldX / spacing);
                                    int cellZ = (int)math.floor(worldZ / spacing);

                                    uint cellHash = math.hash(new int3(cellX, cellZ, _seed));
                                    var cellRandom = new Random(math.max(1u, cellHash));

                                    int edgePadding;
                                    if (_biome.majorFloraPlacementPadding < 0)
                                    {
                                        edgePadding = spacing >= 5 ? 1 : 0;
                                    }
                                    else
                                    {
                                        int maxPossiblePadding = (spacing - 1) / 2;
                                        edgePadding = math.clamp(_biome.majorFloraPlacementPadding, 0, maxPossiblePadding);
                                    }

                                    int innerMinX = cellX * spacing + edgePadding;
                                    int innerMaxX = cellX * spacing + spacing - edgePadding;

                                    int innerMinZ = cellZ * spacing + edgePadding;
                                    int innerMaxZ = cellZ * spacing + spacing - edgePadding;

                                    int targetX = cellRandom.NextInt(innerMinX, innerMaxX);
                                    int targetZ = cellRandom.NextInt(innerMinZ, innerMaxZ);

                                    if (cellRandom.NextFloat() <= _biome.majorFloraPlacementChance)
                                    {
                                        float dist = math.distance(new float2(worldX, worldZ), new float2(targetX + 0.5f, targetZ + 0.5f));

                                        // Scale the visual dot diameter based on zoom level so it remains visible at all scales
                                        float radius = math.max(0.5f, _zoom * 0.8f);

                                        if (dist <= radius)
                                        {
                                            pixelColor = Color.yellow;
                                        }
                                    }
                                }
                            }
                        }
                    }

                    // Chunk Borders Overlay (Cyan)
                    if (_showChunkBorders)
                    {
                        // A chunk is `ChunkWidth` (16) blocks wide. This checks if the
                        // current pixel covers the exactly discrete boundary between two chunks.
                        bool isBorderX = !Mathf.Approximately(math.floor(worldX / VoxelData.ChunkWidth), math.floor((worldX + _zoom) / VoxelData.ChunkWidth));
                        bool isBorderZ = !Mathf.Approximately(math.floor(worldZ / VoxelData.ChunkWidth), math.floor((worldZ + _zoom) / VoxelData.ChunkWidth));

                        if (isBorderX || isBorderZ)
                        {
                            pixelColor = Color.cyan;
                        }
                    }

                    pixels[z * texSize + x] = pixelColor;
                }
            }

            _previewTexture.SetPixels(pixels);
            _previewTexture.Apply();
            Repaint();
        }

        private float EvaluateNoiseVal(FastNoiseLite noise, float worldX, float worldZ, NoiseTarget target, CaveMode caveMode)
        {
            if (target == NoiseTarget.MajorFloraZone)
            {
                return noise.GetNoise(worldX, worldZ);
            }
            else if (target == NoiseTarget.Lode || (target == NoiseTarget.CaveLayer && caveMode == CaveMode.Blob))
            {
                return noise.GetNoise(worldX, _sliceY, worldZ);
            }
            else if (target == NoiseTarget.CaveLayer && caveMode == CaveMode.Spaghetti)
            {
                float ab = noise.GetNoise(worldX, _sliceY);
                float bc = noise.GetNoise(_sliceY, worldZ);
                float ac = noise.GetNoise(worldX, worldZ);
                float ba = noise.GetNoise(_sliceY, worldX);
                float cb = noise.GetNoise(worldZ, _sliceY);
                float ca = noise.GetNoise(worldZ, worldX);
                return (ab + bc + ac + ba + cb + ca) / 6f;
            }
            return 0f;
        }

        private FastNoiseLite CreateNoiseFromConfig(FastNoiseConfig config)
        {
            FastNoiseLite noise = FastNoiseLite.Create(_seed + config.SeedOffset);
            noise.SetFrequency(config.Frequency);
            noise.SetNoiseType(config.NoiseType);
            noise.SetRotationType3D(config.RotationType3D);
            noise.SetFractalType(config.FractalType);
            noise.SetFractalOctaves(config.Octaves);
            noise.SetFractalGain(config.Gain);
            noise.SetFractalLacunarity(config.Lacunarity);
            noise.SetFractalWeightedStrength(config.WeightedStrength);
            noise.SetFractalPingPongStrength(config.PingPongStrength);
            noise.SetCellularDistanceFunction(config.CellularDistanceFunction);
            noise.SetCellularReturnType(config.CellularReturnType);
            noise.SetCellularJitter(config.CellularJitter);
            noise.SetNormalizeToZeroOne(config.NormalizeToZeroOne);
            return noise;
        }
    }
}
