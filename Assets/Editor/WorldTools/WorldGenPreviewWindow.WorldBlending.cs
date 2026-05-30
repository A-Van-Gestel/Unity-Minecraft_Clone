using System;
using Data.WorldTypes;
using Editor.Jobs;
using Editor.Libraries;
using Editor.WorldTools.Libraries;
using JetBrains.Annotations;
using Jobs.Data;
using Jobs.Generators;
using Libraries;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace Editor.WorldTools
{
    /// <summary>
    /// Partial class containing the World Blending tab for the World Gen Preview window.
    /// Renders a multi-biome blended terrain preview using the same BiomeBlender logic
    /// as the runtime StandardChunkGenerationJob, allowing designers to visualize
    /// biome transitions without entering Play Mode.
    /// </summary>
    public partial class WorldGenPreviewWindow
    {
        #region Tab 3: World Blending

        public enum BlendingRenderMode
        {
            /// <summary>Grayscale heightmap showing blended terrain elevation.</summary>
            Heightmap,

            /// <summary>Each biome colored uniquely; blended at boundaries.</summary>
            BiomeVoronoi,

            /// <summary>Select a biome and see its influence weight as a heatmap.</summary>
            BlendWeightHeatmap,

            /// <summary>Visualizes the borderFade value at biome boundaries (density attenuation zones).</summary>
            [UsedImplicitly]
            BiomeBorderFade,
        }

        // --- World Blending State ---
        private BlendingRenderMode _blendRenderMode = BlendingRenderMode.Heightmap;
        private ResolutionOptions _blendResolution = ResolutionOptions.X256;
        private bool _blendShowWaterLevel = true;
        private int _heatmapBiomeIndex;
        private Texture2D _blendPreviewTexture;

        private void OnEnableBlendingTab()
        {
        }

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
            EditorUILayoutHelper.SectionNote("Visualize multi-biome terrain blending using the same BiomeBlender logic as runtime generation.");

            // World Type selector (synced with shared state)
            EditorGUI.BeginChangeCheck();
            _worldType = (WorldTypeDefinition)EditorGUILayout.ObjectField(
                "World Type", _worldType, typeof(WorldTypeDefinition), false);
            if (EditorGUI.EndChangeCheck() && _worldType != null)
                _seaLevel = _worldType.seaLevel;

            if (_worldType == null)
            {
                EditorGUILayout.HelpBox("Drag a WorldTypeDefinition asset to begin.", MessageType.Warning);
                EditorGUILayout.EndVertical();
                return;
            }

            int standardBiomeCount = CountStandardBiomes(_worldType);
            if (standardBiomeCount == 0)
            {
                EditorGUILayout.HelpBox("This WorldType has no StandardBiomeAttributes biomes.", MessageType.Warning);
                EditorGUILayout.EndVertical();
                return;
            }

            EditorGUI.BeginChangeCheck();

            _blendRenderMode = (BlendingRenderMode)EditorGUILayout.EnumPopup("Render Mode", _blendRenderMode);

            if (_blendRenderMode == BlendingRenderMode.BlendWeightHeatmap)
            {
                string[] biomeNames = GetStandardBiomeNames(_worldType);
                _heatmapBiomeIndex = EditorGUILayout.Popup("Target Biome", _heatmapBiomeIndex, biomeNames);
            }

            _blendResolution = (ResolutionOptions)EditorGUILayout.EnumPopup("Resolution", _blendResolution);
            _seed = EditorGUILayout.IntField("World Seed", _seed);
            _zoom = EditorGUILayout.Slider("Zoom Scale", _zoom, 0.1f, 10f);
            _offset = EditorGUILayout.Vector2IntField("XZ Offset", _offset);
            _blendShowWaterLevel = EditorGUILayout.Toggle("Show Water Level", _blendShowWaterLevel);
            _showChunkBorders = EditorGUILayout.Toggle("Show Chunk Borders", _showChunkBorders);
            _autoGenerate = EditorGUILayout.Toggle("Auto Generate", _autoGenerate);

            bool changed = EditorGUI.EndChangeCheck();

            if (changed)
            {
                WorldGenPreviewSettings.Publish(_seed, _worldType, _crosshairPos, _csMode == CrossSectionMode.SingleBiome, _biome, _seaLevel);
            }

            if (GUILayout.Button("Generate Preview"))
            {
                _debounceTimer.Cancel();
                GenerateBlendingPreview();
            }
            else if (changed && _autoGenerate)
            {
                _debounceTimer.Request(GenerateBlendingPreview);
            }

            EditorGUILayout.Space();

            // --- Responsive texture display ---
            if (_blendPreviewTexture != null)
            {
                Rect rect = GUILayoutUtility.GetRect(0, 0, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
                if (rect.width > 10 && rect.height > 10)
                {
                    float texAspect = (float)_blendPreviewTexture.width / _blendPreviewTexture.height;
                    float rectAspect = rect.width / rect.height;

                    Rect drawRect;
                    if (texAspect > rectAspect)
                    {
                        float h = rect.width / texAspect;
                        drawRect = new Rect(rect.x, rect.y, rect.width, h);
                    }
                    else
                    {
                        float w = rect.height * texAspect;
                        drawRect = new Rect(rect.x, rect.y, w, rect.height);
                    }

                    GUI.DrawTexture(drawRect, _blendPreviewTexture, ScaleMode.StretchToFill);

                    // Screen-space chunk border overlays
                    if (_showChunkBorders)
                    {
                        int texSize = _blendPreviewTexture.width;
                        float worldMinX = _offset.x;
                        float worldMaxX = worldMinX + texSize * _zoom;
                        float worldMinZ = _offset.y;
                        float worldMaxZ = worldMinZ + texSize * _zoom;
                        float pxPerWorldX = drawRect.width / (worldMaxX - worldMinX);
                        float pxPerWorldZ = drawRect.height / (worldMaxZ - worldMinZ);

                        const int chunkW = VoxelData.ChunkWidth;
                        int firstChunkX = Mathf.CeilToInt(worldMinX / chunkW) * chunkW;
                        for (int wx = firstChunkX; wx <= (int)worldMaxX; wx += chunkW)
                        {
                            float lineX = drawRect.x + (wx - worldMinX) * pxPerWorldX;
                            EditorGUI.DrawRect(new Rect(lineX, drawRect.y, 1, drawRect.height), Color.cyan);
                        }

                        int firstChunkZ = Mathf.CeilToInt(worldMinZ / chunkW) * chunkW;
                        for (int wz = firstChunkZ; wz <= (int)worldMaxZ; wz += chunkW)
                        {
                            float lineY = drawRect.y + (wz - worldMinZ) * pxPerWorldZ;
                            EditorGUI.DrawRect(new Rect(drawRect.x, lineY, drawRect.width, 1), Color.cyan);
                        }
                    }
                }
            }

            EditorGUILayout.EndVertical();
        }

        private void GenerateBlendingPreview()
        {
            if (_worldType == null) return;

            FastNoiseLite.InitializeLookupTables();

            StandardBiomeAttributes[] standardBiomes = GetStandardBiomes(_worldType);
            int biomeCount = standardBiomes.Length;
            if (biomeCount == 0) return;

            int texSize = (int)_blendResolution;
            if (_blendPreviewTexture == null || _blendPreviewTexture.width != texSize)
            {
                if (_blendPreviewTexture != null) DestroyImmediate(_blendPreviewTexture);
                _blendPreviewTexture = new Texture2D(texSize, texSize, TextureFormat.RGBA32, false)
                {
                    filterMode = FilterMode.Point,
                };
            }

            // --- Build NativeArrays ---
            NativeArray<StandardBiomeAttributesJobData> biomesJobData =
                new NativeArray<StandardBiomeAttributesJobData>(biomeCount, Allocator.TempJob);
            NativeArray<FastNoiseLite> contNoises = new NativeArray<FastNoiseLite>(biomeCount, Allocator.TempJob);
            NativeArray<FastNoiseLite> erosionNoises = new NativeArray<FastNoiseLite>(biomeCount, Allocator.TempJob);
            NativeArray<FastNoiseLite> pvNoises = new NativeArray<FastNoiseLite>(biomeCount, Allocator.TempJob);
            NativeArray<BurstSpline> contSplines = new NativeArray<BurstSpline>(biomeCount, Allocator.TempJob);
            NativeArray<BurstSpline> erosionSplines = new NativeArray<BurstSpline>(biomeCount, Allocator.TempJob);
            NativeArray<BurstSpline> pvSplines = new NativeArray<BurstSpline>(biomeCount, Allocator.TempJob);

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
                    SurfaceBlockID = (byte)biome.surfaceBlockID,
                    UnderwaterSurfaceBlockID = (byte)biome.underwaterSurfaceBlockID,
                    FloraZoneCoverage = biome.floraZoneCoverage,
                    Enable3DDensity = biome.enable3DDensity,
                    DensityAmplitude = biome.densityAmplitude,
                    EnableDensityWarp = biome.enableDensityWarp,
                    TrunkSpawnSuppression = biome.trunkWormModifiers.spawnSuppression,
                    TrunkVerticalBiasOverride = biome.trunkWormModifiers.verticalBiasOverride,
                    TrunkYAttractionCenterOverride = biome.trunkWormModifiers.yAttractionCenterOverride,
                    TrunkTraversalAllowed = biome.trunkWormModifiers.traversalAllowed,
                    TrunkTraversalFadeSteps = biome.trunkWormModifiers.traversalFadeSteps,
                    DebugPreviewColor = new float3(biome.debugPreviewColor.r, biome.debugPreviewColor.g, biome.debugPreviewColor.b),
                };

                // Build Multi-Noise arrays
                FastNoiseConfig contCfg = biome.continentalnessNoiseConfig;
                FastNoiseConfig erosionCfg = biome.erosionNoiseConfig;
                FastNoiseConfig pvCfg = biome.peaksAndValleysNoiseConfig;
                contCfg.normalizeToZeroOne = false;
                erosionCfg.normalizeToZeroOne = false;
                pvCfg.normalizeToZeroOne = false;

                contNoises[i] = FastNoiseFactory.CreateNoiseFromConfig(contCfg, _seed);
                erosionNoises[i] = FastNoiseFactory.CreateNoiseFromConfig(erosionCfg, _seed);
                pvNoises[i] = FastNoiseFactory.CreateNoiseFromConfig(pvCfg, _seed);
                contSplines[i] = BurstSpline.FromAnimationCurve(biome.continentalnessCurve);
                erosionSplines[i] = BurstSpline.FromAnimationCurve(biome.erosionCurve);
                pvSplines[i] = BurstSpline.FromAnimationCurve(biome.peaksAndValleysCurve);
            }

            MultiNoiseData multiNoise = new MultiNoiseData
            {
                ContinentalnessNoises = contNoises,
                ErosionNoises = erosionNoises,
                PeaksValleysNoises = pvNoises,
                ContinentalnessSplines = contSplines,
                ErosionSplines = erosionSplines,
                PeaksValleysSplines = pvSplines,
            };

            FastNoiseConfig selectionConfig = standardBiomes[0].biomeWeightNoiseConfig;
            selectionConfig.normalizeToZeroOne = true;
            FastNoiseLite selectionNoise = FastNoiseFactory.CreateNoiseFromConfig(selectionConfig, _seed);

            int seaLevel = _seaLevel;

            // --- Map editor enum to job mode ---
            WorldBlendingMode jobMode;
            switch (_blendRenderMode)
            {
                case BlendingRenderMode.Heightmap: jobMode = WorldBlendingMode.Heightmap; break;
                case BlendingRenderMode.BiomeVoronoi: jobMode = WorldBlendingMode.BiomeVoronoi; break;
                case BlendingRenderMode.BlendWeightHeatmap: jobMode = WorldBlendingMode.BlendWeightHeatmap; break;
                case BlendingRenderMode.BiomeBorderFade: jobMode = WorldBlendingMode.BiomeBorderFade; break;
                default: jobMode = WorldBlendingMode.Heightmap; break;
            }

            // --- Schedule Burst parallel job ---
            // Use Persistent allocator since the job reads NativeArrays that must outlive scheduling
            int pixelCount = texSize * texSize;
            NativeArray<byte> outputPixels = new NativeArray<byte>(pixelCount * 4, Allocator.TempJob);

            WorldBlendingPreviewJob job = new WorldBlendingPreviewJob
            {
                TextureSize = texSize,
                Zoom = _zoom,
                OffsetX = _offset.x,
                OffsetZ = _offset.y,
                Mode = jobMode,
                SeaLevel = seaLevel,
                BiomeCount = biomeCount,
                TargetBiomeIndex = _heatmapBiomeIndex,
                ShowWaterLevel = _blendShowWaterLevel,
                SelectionNoise = selectionNoise,
                Biomes = biomesJobData,
                MultiNoise = multiNoise,
                OutputPixels = outputPixels,
            };

            job.Schedule(pixelCount, 64).Complete();

            _blendPreviewTexture.LoadRawTextureData(outputPixels);
            _blendPreviewTexture.Apply();
            outputPixels.Dispose();
            Repaint();

            // Cleanup
            biomesJobData.Dispose();
            contNoises.Dispose();
            erosionNoises.Dispose();
            pvNoises.Dispose();
            contSplines.Dispose();
            erosionSplines.Dispose();
            pvSplines.Dispose();
        }

        #region Helper Methods

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
                    result[idx++] = sba;
            }

            return result;
        }

        private static string[] GetStandardBiomeNames(WorldTypeDefinition worldType)
        {
            StandardBiomeAttributes[] biomes = GetStandardBiomes(worldType);
            string[] names = new string[biomes.Length];
            for (int i = 0; i < biomes.Length; i++)
                names[i] = string.IsNullOrEmpty(biomes[i].biomeName) ? $"Biome {i}" : biomes[i].biomeName;
            return names;
        }

        #endregion

        #endregion
    }
}
