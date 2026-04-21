using System;
using Data.WorldTypes;
using Jobs.Data;
using Jobs.Generators;
using Jobs.Helpers;
using Libraries;
using Unity.Collections;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace Editor.WorldTools
{
    /// <summary>
    /// Partial class containing the World Blending tab for the Noise Preview window.
    /// Renders a multi-biome blended terrain preview using the same BiomeBlender logic
    /// as the runtime StandardChunkGenerationJob, allowing designers to visualize
    /// biome transitions without entering Play Mode.
    /// </summary>
    public partial class NoisePreviewWindow
    {
        #region Tab 1: World Blending

        public enum BlendingRenderMode
        {
            /// <summary>Grayscale heightmap showing blended terrain elevation.</summary>
            Heightmap,

            /// <summary>Each biome colored uniquely; blended at boundaries.</summary>
            BiomeVoronoi,

            /// <summary>Select a biome and see its influence weight as a heatmap.</summary>
            BlendWeightHeatmap,
        }

        // --- World Blending State ---
        private WorldTypeDefinition _worldType;
        private BlendingRenderMode _blendRenderMode = BlendingRenderMode.Heightmap;
        private int _blendSeed = 1337;
        private ResolutionOptions _blendResolution = ResolutionOptions.X256;
        private Vector2Int _blendOffset = Vector2Int.zero;
        private float _blendZoom = 1f;
        private bool _blendShowChunkBorders = true;
        private bool _blendAutoGenerate = true;
        private bool _blendShowWaterLevel = true;
        private int _heatmapBiomeIndex;

        private Texture2D _blendPreviewTexture;

        // Distinct colors for biome Voronoi visualization
        private static readonly Color[] s_biomeColors =
        {
            new Color(0.30f, 0.70f, 0.30f), // Green
            new Color(0.85f, 0.80f, 0.50f), // Sandy
            new Color(0.40f, 0.55f, 0.80f), // Blue
            new Color(0.70f, 0.35f, 0.35f), // Red
            new Color(0.90f, 0.90f, 0.95f), // Snowy White
            new Color(0.55f, 0.40f, 0.25f), // Brown
            new Color(0.20f, 0.50f, 0.45f), // Teal
            new Color(0.75f, 0.55f, 0.75f), // Lavender
        };

        /// <summary>
        /// Called from OnEnable. Initialize blending tab state.
        /// </summary>
        private void OnEnableBlendingTab()
        {
            // No persistent state to initialize beyond defaults
        }

        /// <summary>
        /// Called from OnDisable. Cleanup blending tab state.
        /// </summary>
        private void OnDisableBlendingTab()
        {
            if (_blendPreviewTexture != null)
            {
                DestroyImmediate(_blendPreviewTexture);
                _blendPreviewTexture = null;
            }
        }

        private void DrawWorldBlendingTab()
        {
            EditorGUILayout.BeginVertical();

            GUILayout.Label("World Blending Preview", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Visualize multi-biome terrain blending using the same BiomeBlender logic as runtime generation.",
                MessageType.Info);

            EditorGUI.BeginChangeCheck();

            _worldType = (WorldTypeDefinition)EditorGUILayout.ObjectField(
                "World Type", _worldType, typeof(WorldTypeDefinition), false);

            if (_worldType == null)
            {
                EditorGUILayout.HelpBox("Drag a WorldTypeDefinition asset to begin.", MessageType.Warning);
                EditorGUILayout.EndVertical();
                return;
            }

            // Filter to only Standard biomes
            int standardBiomeCount = CountStandardBiomes(_worldType);
            if (standardBiomeCount == 0)
            {
                EditorGUILayout.HelpBox("This WorldType has no StandardBiomeAttributes biomes.", MessageType.Warning);
                EditorGUILayout.EndVertical();
                return;
            }

            _blendRenderMode = (BlendingRenderMode)EditorGUILayout.EnumPopup("Render Mode", _blendRenderMode);

            if (_blendRenderMode == BlendingRenderMode.BlendWeightHeatmap)
            {
                string[] biomeNames = GetStandardBiomeNames(_worldType);
                _heatmapBiomeIndex = EditorGUILayout.Popup("Target Biome", _heatmapBiomeIndex, biomeNames);
            }

            _blendResolution = (ResolutionOptions)EditorGUILayout.EnumPopup("Resolution", _blendResolution);
            _blendSeed = EditorGUILayout.IntField("World Seed", _blendSeed);
            _blendZoom = EditorGUILayout.Slider("Zoom Scale", _blendZoom, 0.1f, 10f);
            _blendOffset = EditorGUILayout.Vector2IntField("XZ Offset", _blendOffset);
            _blendShowWaterLevel = EditorGUILayout.Toggle("Show Water Level", _blendShowWaterLevel);
            _blendShowChunkBorders = EditorGUILayout.Toggle("Show Chunk Borders", _blendShowChunkBorders);
            _blendAutoGenerate = EditorGUILayout.Toggle("Auto Generate On Change", _blendAutoGenerate);

            bool changed = EditorGUI.EndChangeCheck();

            if (GUILayout.Button("Generate Preview") || (changed && _blendAutoGenerate))
            {
                GenerateBlendingPreview();
            }

            EditorGUILayout.Space();

            if (_blendPreviewTexture != null)
            {
                Rect rect = GUILayoutUtility.GetAspectRect(1f);
                if (rect.width > 512)
                {
                    rect.width = 512;
                    rect.height = 512;
                    rect.x = (position.width - 512) / 2;
                }

                GUI.DrawTexture(rect, _blendPreviewTexture, ScaleMode.ScaleToFit);
            }

            EditorGUILayout.EndVertical();
        }

        /// <summary>
        /// Generates the blended terrain preview texture using BiomeBlender.
        /// Replicates the runtime biome selection and blending pipeline in an editor-safe manner.
        /// </summary>
        private void GenerateBlendingPreview()
        {
            if (_worldType == null) return;

            FastNoiseLite.InitializeLookupTables();

            // --- Collect Standard Biomes ---
            StandardBiomeAttributes[] standardBiomes = GetStandardBiomes(_worldType);
            int biomeCount = standardBiomes.Length;
            if (biomeCount == 0) return;

            int texSize = (int)_blendResolution;
            if (_blendPreviewTexture == null || _blendPreviewTexture.width != texSize)
            {
                if (_blendPreviewTexture != null) DestroyImmediate(_blendPreviewTexture);
                _blendPreviewTexture = new Texture2D(texSize, texSize, TextureFormat.RGBA32, false);
                _blendPreviewTexture.filterMode = FilterMode.Point;
            }

            // --- Build NativeArrays mirroring StandardChunkGenerator.Initialize() ---
            NativeArray<StandardBiomeAttributesJobData> biomesJobData =
                new NativeArray<StandardBiomeAttributesJobData>(biomeCount, Allocator.Temp);
            NativeArray<FastNoiseLite> terrainNoises =
                new NativeArray<FastNoiseLite>(biomeCount, Allocator.Temp);

            for (int i = 0; i < biomeCount; i++)
            {
                StandardBiomeAttributes biome = standardBiomes[i];
                biomesJobData[i] = new StandardBiomeAttributesJobData
                {
                    BlendRadius = biome.blendRadius,
                    BlendWeight = biome.blendWeight,
                    BlendCurve = biome.blendCurve,
                    SurfaceBlockDitheringWidth = biome.surfaceBlockDitheringWidth,
                    BaseTerrainHeight = biome.baseTerrainHeight,
                    TerrainAmplitude = biome.terrainAmplitude,
                    SurfaceBlockID = (byte)biome.surfaceBlockID,
                    UnderwaterSurfaceBlockID = (byte)biome.underwaterSurfaceBlockID,
                    FloraZoneCoverage = biome.floraZoneCoverage,
                };

                terrainNoises[i] = FastNoiseFactory.CreateNoiseFromConfig(biome.terrainNoiseConfig, _blendSeed);
            }

            // --- Build Biome Selection Noise (Cellular / Voronoi) ---
            FastNoiseConfig selectionConfig = standardBiomes[0].biomeWeightNoiseConfig;
            selectionConfig.normalizeToZeroOne = true;
            FastNoiseLite selectionNoise = FastNoiseFactory.CreateNoiseFromConfig(selectionConfig, _blendSeed);

            int seaLevel = _worldType.seaLevel;

            Color[] pixels = new Color[texSize * texSize];

            // --- Pixel Loop ---
            for (int z = 0; z < texSize; z++)
            {
                for (int x = 0; x < texSize; x++)
                {
                    float worldX = x * _blendZoom + _blendOffset.x;
                    float worldZ = z * _blendZoom + _blendOffset.y;

                    int gx = (int)math.floor(worldX);
                    int gz = (int)math.floor(worldZ);

                    Color pixelColor;

                    switch (_blendRenderMode)
                    {
                        case BlendingRenderMode.Heightmap:
                            pixelColor = EvaluateHeightmapPixel(gx, gz, seaLevel,
                                ref selectionNoise, ref biomesJobData, ref terrainNoises);
                            break;

                        case BlendingRenderMode.BiomeVoronoi:
                            pixelColor = EvaluateVoronoiPixel(gx, gz,
                                ref selectionNoise, ref biomesJobData, ref terrainNoises, biomeCount);
                            break;

                        case BlendingRenderMode.BlendWeightHeatmap:
                            pixelColor = EvaluateHeatmapPixel(gx, gz, _heatmapBiomeIndex,
                                ref selectionNoise, ref biomesJobData, ref terrainNoises, biomeCount);
                            break;

                        default:
                            pixelColor = Color.black;
                            break;
                    }

                    // Chunk border overlay
                    if (_blendShowChunkBorders)
                    {
                        bool isBorderX = !Mathf.Approximately(
                            math.floor(worldX / VoxelData.ChunkWidth),
                            math.floor((worldX + _blendZoom) / VoxelData.ChunkWidth));
                        bool isBorderZ = !Mathf.Approximately(
                            math.floor(worldZ / VoxelData.ChunkWidth),
                            math.floor((worldZ + _blendZoom) / VoxelData.ChunkWidth));

                        if (isBorderX || isBorderZ)
                        {
                            pixelColor = Color.cyan;
                        }
                    }

                    pixels[z * texSize + x] = pixelColor;
                }
            }

            _blendPreviewTexture.SetPixels(pixels);
            _blendPreviewTexture.Apply();
            Repaint();

            // Cleanup
            biomesJobData.Dispose();
            terrainNoises.Dispose();
        }

        #region Pixel Evaluators

        /// <summary>
        /// Evaluates a single pixel in Heightmap mode — grayscale blended terrain height.
        /// </summary>
        private Color EvaluateHeightmapPixel(
            int gx, int gz, int seaLevel,
            ref FastNoiseLite selectionNoise,
            ref NativeArray<StandardBiomeAttributesJobData> biomes,
            ref NativeArray<FastNoiseLite> terrainNoises)
        {
            int height = BiomeBlender.CalculateBlendedTerrainHeight(
                gx, gz, ref selectionNoise, ref biomes, ref terrainNoises);

            if (_blendShowWaterLevel && height < seaLevel)
            {
                // Ocean blue tint, darker for deeper water
                float depth = math.saturate((seaLevel - height) / 30f);
                return Color.Lerp(new Color(0.25f, 0.55f, 0.85f), new Color(0.08f, 0.20f, 0.55f), depth);
            }

            // Normalize height to ~[0,1] for visualization (assume range 0–128)
            float normalized = math.clamp(height / 128f, 0.05f, 1f);
            return new Color(normalized, normalized, normalized, 1f);
        }

        /// <summary>
        /// Evaluates a single pixel in Voronoi mode — biome-colored cells.
        /// </summary>
        private Color EvaluateVoronoiPixel(
            int gx, int gz,
            ref FastNoiseLite selectionNoise,
            ref NativeArray<StandardBiomeAttributesJobData> biomes,
            ref NativeArray<FastNoiseLite> terrainNoises,
            int biomeCount)
        {
            // Get the raw cellular value to determine biome index
            float cellValue = selectionNoise.GetNoise(gx, gz);
            int biomeIndex = (int)math.floor(cellValue * biomeCount);
            biomeIndex = math.clamp(biomeIndex, 0, biomeCount - 1);

            Color biomeColor = s_biomeColors[biomeIndex % s_biomeColors.Length];

            // Modulate brightness by terrain height for depth
            int height = BiomeBlender.CalculateBlendedTerrainHeight(
                gx, gz, ref selectionNoise, ref biomes, ref terrainNoises);
            float brightness = math.clamp(height / 100f, 0.3f, 1.0f);

            return biomeColor * brightness;
        }

        /// <summary>
        /// Evaluates a single pixel in Heatmap mode — single biome's blend weight.
        /// </summary>
        private unsafe Color EvaluateHeatmapPixel(
            int gx, int gz, int targetBiomeIndex,
            ref FastNoiseLite selectionNoise,
            ref NativeArray<StandardBiomeAttributesJobData> biomes,
            ref NativeArray<FastNoiseLite> terrainNoises,
            int biomeCount)
        {
            // Replicate the BiomeBlender weight calculation for a specific biome
            selectionNoise.GetCellularEdgeData(gx, gz, out FastNoiseLite.CellularEdgeData edgeData);

            int* b = stackalloc int[9];
            float* rad = stackalloc float[9];
            float* bw = stackalloc float[9];
            BlendCurve* curves = stackalloc BlendCurve[9];
            for (int i = 0; i < 9; i++)
            {
                b[i] = GetBiomeIndexFromHash(edgeData.Hashes[i], biomeCount);
                rad[i] = biomes[b[i]].BlendRadius;
                bw[i] = biomes[b[i]].BlendWeight;
                curves[i] = biomes[b[i]].BlendCurve;
            }

            float trSum = 0f;
            float localBlendRadiusSum = 0f;
            float dist0 = edgeData.Distances[0];

            for (int i = 0; i < 9; i++)
            {
                float tr = math.max(0f, 1f - (edgeData.Distances[i] - dist0));
                trSum += tr;
                localBlendRadiusSum += tr * rad[i];
            }

            float localBlendRadius = localBlendRadiusSum / trSum;
            float wiggle = selectionNoise.GetNoise(gx * 0.25f, gz * 0.25f) * 0.5f * localBlendRadius;
            float activeRadius = math.max(0.001f, localBlendRadius + wiggle);

            float* raw = stackalloc float[9];
            float totalRaw = 0f;
            for (int i = 0; i < 9; i++)
            {
                raw[i] = math.max(0f, 1f - (edgeData.Distances[i] - dist0) / activeRadius) * bw[i];
                totalRaw += raw[i];
            }

            // Calculate normalized + curved weight for the target biome
            float targetWeight = 0f;
            float totalSmooth = 0f;
            for (int i = 0; i < 9; i++)
            {
                float norm = raw[i] / totalRaw;
                float curved = ApplyBlendCurve(norm, curves[i]);
                totalSmooth += curved;
                if (b[i] == targetBiomeIndex)
                {
                    targetWeight += curved;
                }
            }

            float finalWeight = totalSmooth > 0f ? targetWeight / totalSmooth : 0f;

            // Render as heatmap: black (0) → blue → green → yellow → red (1)
            return HeatmapColor(finalWeight);
        }

        #endregion

        #region Helper Methods

        private static int GetBiomeIndexFromHash(int hash, int biomeCount)
        {
            float noiseValue = hash * (1.0f / 2147483648.0f);
            noiseValue = (noiseValue + 1.0f) * 0.5f;
            int idx = (int)math.floor(noiseValue * biomeCount);
            return math.clamp(idx, 0, biomeCount - 1);
        }

        private static float ApplyBlendCurve(float t, BlendCurve curve)
        {
            switch (curve)
            {
                case BlendCurve.Linear:
                    return t;
                case BlendCurve.SmootherStep:
                    return t * t * t * (t * (t * 6f - 15f) + 10f);
                case BlendCurve.SmoothStep:
                default:
                    return t * t * (3f - 2f * t);
            }
        }

        /// <summary>
        /// Maps a [0,1] weight value to a scientific heatmap color ramp.
        /// </summary>
        private static Color HeatmapColor(float t)
        {
            t = math.saturate(t);

            if (t < 0.25f)
            {
                float s = t / 0.25f;
                return Color.Lerp(Color.black, Color.blue, s);
            }

            if (t < 0.5f)
            {
                float s = (t - 0.25f) / 0.25f;
                return Color.Lerp(Color.blue, Color.green, s);
            }

            if (t < 0.75f)
            {
                float s = (t - 0.5f) / 0.25f;
                return Color.Lerp(Color.green, Color.yellow, s);
            }

            {
                float s = (t - 0.75f) / 0.25f;
                return Color.Lerp(Color.yellow, Color.red, s);
            }
        }

        private static int CountStandardBiomes(WorldTypeDefinition worldType)
        {
            if (worldType.biomes == null) return 0;
            int count = 0;
            foreach (BiomeBase b in worldType.biomes)
            {
                if (b is StandardBiomeAttributes) count++;
            }

            return count;
        }

        private static StandardBiomeAttributes[] GetStandardBiomes(WorldTypeDefinition worldType)
        {
            if (worldType.biomes == null) return Array.Empty<StandardBiomeAttributes>();

            int count = CountStandardBiomes(worldType);
            StandardBiomeAttributes[] result = new StandardBiomeAttributes[count];
            int idx = 0;
            foreach (BiomeBase b in worldType.biomes)
            {
                if (b is StandardBiomeAttributes sba)
                {
                    result[idx++] = sba;
                }
            }

            return result;
        }

        private static string[] GetStandardBiomeNames(WorldTypeDefinition worldType)
        {
            StandardBiomeAttributes[] biomes = GetStandardBiomes(worldType);
            string[] names = new string[biomes.Length];
            for (int i = 0; i < biomes.Length; i++)
            {
                names[i] = string.IsNullOrEmpty(biomes[i].biomeName) ? $"Biome {i}" : biomes[i].biomeName;
            }

            return names;
        }

        #endregion

        #endregion
    }
}
