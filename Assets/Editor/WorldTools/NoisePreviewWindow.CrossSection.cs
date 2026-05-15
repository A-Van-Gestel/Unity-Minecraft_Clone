using System.Collections.Generic;
using Data;
using Data.WorldTypes;
using Editor.Libraries;
using Helpers;
using JetBrains.Annotations;
using Jobs;
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
    /// Partial class containing the Cross-Section tab for the Noise Preview window.
    /// Renders a vertical X-Y terrain profile showing blocks, caves, strata, overhangs,
    /// and the density field at a configurable Z-slice.
    /// </summary>
    public partial class NoisePreviewWindow
    {
        #region Tab 0: Cross Section

        private enum CrossSectionMode
        {
            /// <summary>Shows the selected biome in isolation (no Voronoi blending).</summary>
            SingleBiome,

            /// <summary>Shows the full world with Voronoi biome selection and blending.</summary>
            [UsedImplicitly]
            WorldView,
        }

        // --- Cross Section State ---
        private CrossSectionMode _csMode = CrossSectionMode.SingleBiome;
        private int _crossSectionZ;
        private Texture2D _crossSectionTexture;

        private bool _csShowCaves = true;
        private bool _csShowLodes = true;
        private bool _csShowWater = true;
        private bool _csShowSeaLevelLine = true;
        private int _csSeaLevel = 45;

        private void OnDisableCrossSectionTab()
        {
            if (_crossSectionTexture != null)
            {
                DestroyImmediate(_crossSectionTexture);
                _crossSectionTexture = null;
            }
        }

        private void DrawCrossSectionTab()
        {
            EditorGUILayout.BeginHorizontal();
            DrawBiomeList();

            EditorGUILayout.BeginVertical();
            GUILayout.Label("Cross Section Preview", EditorStyles.boldLabel);

            if (_biome == null)
            {
                EditorGUILayout.HelpBox("Select a biome from the list to begin.", MessageType.Warning);
                EditorGUILayout.EndVertical();
                EditorGUILayout.EndHorizontal();
                return;
            }

            EditorGUI.BeginChangeCheck();

            _csMode = (CrossSectionMode)EditorGUILayout.EnumPopup("Mode", _csMode);
            _chunkRadius = EditorGUILayout.IntSlider("Chunk Count", _chunkRadius, 1, 32);
            _seed = EditorGUILayout.IntField("World Seed", _seed);
            _crossSectionZ = EditorGUILayout.IntField("Z Slice", _crossSectionZ);
            _offset.x = EditorGUILayout.IntField("X Offset", _offset.x);

            EditorGUILayout.Space();
            _csShowCaves = EditorGUILayout.Toggle("Show Caves", _csShowCaves);
            _csShowLodes = EditorGUILayout.Toggle("Show Lodes", _csShowLodes);
            _csShowWater = EditorGUILayout.Toggle("Show Water", _csShowWater);
            _csShowSeaLevelLine = EditorGUILayout.Toggle("Show Sea Level Line", _csShowSeaLevelLine);
            if (_csShowSeaLevelLine || _csShowWater)
                _csSeaLevel = EditorGUILayout.IntSlider("Sea Level", _csSeaLevel, 0, VoxelData.ChunkHeight - 1);
            _showChunkBorders = EditorGUILayout.Toggle("Show Chunk Borders", _showChunkBorders);
            _autoGenerate = EditorGUILayout.Toggle("Auto Generate", _autoGenerate);

            bool changed = EditorGUI.EndChangeCheck();

            if (GUILayout.Button("Generate Preview") || (changed && _autoGenerate))
            {
                GenerateCrossSectionPreview();
            }

            EditorGUILayout.Space();

            // --- Responsive texture display (fills remaining space) ---
            if (_crossSectionTexture != null)
            {
                Rect rect = GUILayoutUtility.GetRect(0, 0, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
                if (rect.width > 10 && rect.height > 10)
                {
                    // Fit texture into available rect preserving aspect ratio
                    float texAspect = (float)_crossSectionTexture.width / _crossSectionTexture.height;
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

                    GUI.DrawTexture(drawRect, _crossSectionTexture, ScaleMode.StretchToFill);

                    // Draw overlays directly on screen (1px screen lines, not texture pixels)
                    float blockW = drawRect.width / _crossSectionTexture.width;
                    float blockH = drawRect.height / _crossSectionTexture.height;

                    // Sea level line
                    if (_csShowSeaLevelLine && _csSeaLevel > 0 && _csSeaLevel < VoxelData.ChunkHeight)
                    {
                        float lineY = drawRect.yMax - (_csSeaLevel * blockH);
                        EditorGUI.DrawRect(new Rect(drawRect.x, lineY, drawRect.width, 1), new Color(0.2f, 0.6f, 1f, 0.8f));
                    }

                    // Chunk borders
                    if (_showChunkBorders)
                    {
                        for (int col = 0; col < _crossSectionTexture.width; col++)
                        {
                            int gx = col + _offset.x;
                            if (gx % VoxelData.ChunkWidth == 0)
                            {
                                float lineX = drawRect.x + (col * blockW);
                                EditorGUI.DrawRect(new Rect(lineX, drawRect.y, 1, drawRect.height), Color.cyan);
                            }
                        }
                    }
                }
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
        }

        private void GenerateCrossSectionPreview()
        {
            if (_biome == null) return;

            FastNoiseLite.InitializeLookupTables();

            // --- Collect all biome data (needed for world view and blending) ---
            StandardBiomeAttributes[] standardBiomes = GetAllStandardBiomes();
            int biomeCount = standardBiomes.Length;
            if (biomeCount == 0) return;

            // Find the selected biome's index in the array
            int selectedBiomeIdx = 0;
            for (int i = 0; i < biomeCount; i++)
            {
                if (standardBiomes[i] == _biome)
                {
                    selectedBiomeIdx = i;
                    break;
                }
            }

            // --- Build NativeArrays ---
            BuildCrossSectionData(standardBiomes, out CrossSectionNativeData data);

            // --- Generate worm masks per chunk ---
            int width = _chunkRadius * VoxelData.ChunkWidth;
            int height = VoxelData.ChunkHeight;
            int globalZ = _crossSectionZ;

            Dictionary<int, NativeBitArray> wormMasks = null;
            if (_csShowCaves)
            {
                wormMasks = GenerateWormMasks(width, globalZ, ref data);
            }

            // --- Generate texture (1 pixel per block) ---
            if (_crossSectionTexture == null || _crossSectionTexture.width != width || _crossSectionTexture.height != height)
            {
                if (_crossSectionTexture != null) DestroyImmediate(_crossSectionTexture);
                _crossSectionTexture = new Texture2D(width, height, TextureFormat.RGBA32, false)
                {
                    filterMode = FilterMode.Point,
                };
            }

            Color[] pixels = new Color[width * height];

            for (int col = 0; col < width; col++)
            {
                int globalX = col + _offset.x;

                // Determine which chunk this column belongs to (for worm mask lookup)
                int chunkX = (int)math.floor((float)globalX / VoxelData.ChunkWidth) * VoxelData.ChunkWidth;
                int localX = globalX - chunkX;
                int localZ = globalZ - (int)math.floor((float)globalZ / VoxelData.ChunkWidth) * VoxelData.ChunkWidth;
                NativeBitArray wormMask = default;
                if (wormMasks != null && wormMasks.TryGetValue(chunkX, out NativeBitArray mask))
                    wormMask = mask;

                ushort[] column = EvaluateColumn(
                    globalX, globalZ, _csSeaLevel,
                    _csMode == CrossSectionMode.SingleBiome ? selectedBiomeIdx : -1,
                    _csShowCaves, _csShowLodes,
                    localX, localZ, ref wormMask,
                    ref data);

                for (int y = 0; y < height; y++)
                {
                    ushort blockID = column[y];
                    Color color;

                    if (blockID == BlockIDs.Air)
                        color = CrossSectionBlockColorMap.GetSkyColor(y, height);
                    else if (blockID == BlockIDs.Water && _csShowWater)
                        color = CrossSectionBlockColorMap.GetWaterColor(y, _csSeaLevel);
                    else if (blockID == BlockIDs.Water)
                        color = CrossSectionBlockColorMap.GetSkyColor(y, height);
                    else
                        color = CrossSectionBlockColorMap.GetBlockColor(blockID);

                    pixels[y * width + col] = color;
                }
            }

            _crossSectionTexture.SetPixels(pixels);
            _crossSectionTexture.Apply();
            Repaint();

            // --- Cleanup ---
            if (wormMasks != null)
            {
                foreach (NativeBitArray m in wormMasks.Values)
                    m.Dispose();
            }

            DisposeCrossSectionData(ref data);
        }

        #region Data Building

        private struct CrossSectionNativeData
        {
            public NativeArray<StandardBiomeAttributesJobData> Biomes;
            public MultiNoiseData MultiNoise;
            public NativeArray<FastNoiseLite> DensityNoises;
            public NativeArray<FastNoiseLite> DensityWarpNoises;
            public NativeArray<FastNoiseLite> StrataDepthNoises;
            public NativeArray<StandardTerrainLayerJobData> AllTerrainLayers;
            public NativeArray<StandardCaveLayerJobData> AllCaveLayers;
            public NativeArray<FastNoiseLite> CaveNoises;
            public NativeArray<FastNoiseLite> CaveWarpNoises;
            public NativeArray<StandardLodeJobData> AllLodes;
            public NativeArray<FastNoiseLite> LodeNoises;
            public FastNoiseLite SelectionNoise;
        }

        private StandardBiomeAttributes[] GetAllStandardBiomes()
        {
            string[] guids = AssetDatabase.FindAssets("t:StandardBiomeAttributes", new[] { "Assets/Data/WorldGen/Biomes" });
            StandardBiomeAttributes[] biomes = new StandardBiomeAttributes[guids.Length];
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                biomes[i] = AssetDatabase.LoadAssetAtPath<StandardBiomeAttributes>(path);
            }

            return biomes;
        }

        private void BuildCrossSectionData(StandardBiomeAttributes[] standardBiomes, out CrossSectionNativeData data)
        {
            int biomeCount = standardBiomes.Length;
            data = new CrossSectionNativeData();

            data.Biomes = new NativeArray<StandardBiomeAttributesJobData>(biomeCount, Allocator.Persistent);

            // Multi-Noise
            NativeArray<FastNoiseLite> contNoises = new NativeArray<FastNoiseLite>(biomeCount, Allocator.Persistent);
            NativeArray<FastNoiseLite> erosionNoises = new NativeArray<FastNoiseLite>(biomeCount, Allocator.Persistent);
            NativeArray<FastNoiseLite> pvNoises = new NativeArray<FastNoiseLite>(biomeCount, Allocator.Persistent);
            NativeArray<BurstSpline> contSplines = new NativeArray<BurstSpline>(biomeCount, Allocator.Persistent);
            NativeArray<BurstSpline> erosionSplines = new NativeArray<BurstSpline>(biomeCount, Allocator.Persistent);
            NativeArray<BurstSpline> pvSplines = new NativeArray<BurstSpline>(biomeCount, Allocator.Persistent);

            data.DensityNoises = new NativeArray<FastNoiseLite>(biomeCount, Allocator.Persistent);
            data.DensityWarpNoises = new NativeArray<FastNoiseLite>(biomeCount, Allocator.Persistent);
            data.StrataDepthNoises = new NativeArray<FastNoiseLite>(biomeCount, Allocator.Persistent);

            // Count flattened arrays
            int totalCaves = 0, totalLodes = 0, totalLayers = 0;
            foreach (StandardBiomeAttributes b in standardBiomes)
            {
                totalCaves += b.caveLayers?.Length ?? 0;
                totalLodes += b.lodes?.Length ?? 0;
                totalLayers += b.terrainLayers?.Length ?? 0;
            }

            data.AllCaveLayers = new NativeArray<StandardCaveLayerJobData>(totalCaves, Allocator.Persistent);
            data.CaveNoises = new NativeArray<FastNoiseLite>(totalCaves, Allocator.Persistent);
            data.CaveWarpNoises = new NativeArray<FastNoiseLite>(totalCaves, Allocator.Persistent);
            data.AllLodes = new NativeArray<StandardLodeJobData>(totalLodes, Allocator.Persistent);
            data.LodeNoises = new NativeArray<FastNoiseLite>(totalLodes, Allocator.Persistent);
            data.AllTerrainLayers = new NativeArray<StandardTerrainLayerJobData>(totalLayers, Allocator.Persistent);

            int caveIdx = 0, lodeIdx = 0, layerIdx = 0;

            for (int i = 0; i < biomeCount; i++)
            {
                StandardBiomeAttributes biome = standardBiomes[i];
                int caveCount = biome.caveLayers?.Length ?? 0;
                int lodeCount = biome.lodes?.Length ?? 0;
                int layerCount = biome.terrainLayers?.Length ?? 0;

                data.Biomes[i] = new StandardBiomeAttributesJobData
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
                    TerrainLayerStartIndex = layerIdx,
                    TerrainLayerCount = layerCount,
                    LodeStartIndex = lodeIdx,
                    LodeCount = lodeCount,
                    CaveLayerStartIndex = caveIdx,
                    CaveLayerCount = caveCount,
                    Enable3DDensity = biome.enable3DDensity,
                    DensityAmplitude = biome.densityAmplitude,
                    EnableDensityWarp = biome.enableDensityWarp,
                };

                for (int j = 0; j < layerCount; j++)
                    data.AllTerrainLayers[layerIdx + j] = new StandardTerrainLayerJobData(biome.terrainLayers[j]);

                for (int j = 0; j < caveCount; j++)
                {
                    data.AllCaveLayers[caveIdx + j] = new StandardCaveLayerJobData(biome.caveLayers[j]);
                    data.CaveNoises[caveIdx + j] = FastNoiseFactory.CreateNoiseFromConfig(biome.caveLayers[j].noiseConfig, _seed);
                    data.CaveWarpNoises[caveIdx + j] = biome.caveLayers[j].enableWarp
                        ? FastNoiseFactory.CreateNoiseFromConfig(biome.caveLayers[j].warpConfig, _seed)
                        : FastNoiseLite.Create(0);
                }

                for (int j = 0; j < lodeCount; j++)
                {
                    data.AllLodes[lodeIdx + j] = new StandardLodeJobData(biome.lodes[j]);
                    data.LodeNoises[lodeIdx + j] = FastNoiseFactory.CreateNoiseFromConfig(biome.lodes[j].noiseConfig, _seed);
                }

                // Multi-Noise (with legacy fallback)
                FastNoiseConfig contCfg = biome.continentalnessNoiseConfig;
                FastNoiseConfig erosionCfg = biome.erosionNoiseConfig;
                FastNoiseConfig pvCfg = biome.peaksAndValleysNoiseConfig;
                bool hasMultiNoise = contCfg.frequency != 0f || erosionCfg.frequency != 0f || pvCfg.frequency != 0f;

                if (hasMultiNoise)
                {
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
                else
                {
#pragma warning disable CS0618 // Type or member is obsolete
                    FastNoiseConfig legacyCfg = biome.terrainNoiseConfig;
#pragma warning restore CS0618 // Type or member is obsolete
                    legacyCfg.normalizeToZeroOne = false;
                    contNoises[i] = FastNoiseFactory.CreateNoiseFromConfig(legacyCfg, _seed);
                    erosionNoises[i] = FastNoiseLite.Create(0);
                    pvNoises[i] = FastNoiseLite.Create(0);
                    contSplines[i] = BurstSpline.CreateLinearRamp(biome.terrainAmplitude);
                    erosionSplines[i] = BurstSpline.FromAnimationCurve(null);
                    pvSplines[i] = BurstSpline.FromAnimationCurve(null);
                }

                data.DensityNoises[i] = FastNoiseFactory.CreateNoiseFromConfig(biome.densityNoiseConfig, _seed);
                data.DensityWarpNoises[i] = biome.enableDensityWarp
                    ? FastNoiseFactory.CreateNoiseFromConfig(biome.densityWarpConfig, _seed)
                    : FastNoiseLite.Create(0);
                data.StrataDepthNoises[i] = FastNoiseFactory.CreateNoiseFromConfig(biome.strataDepthNoiseConfig, _seed);

                caveIdx += caveCount;
                lodeIdx += lodeCount;
                layerIdx += layerCount;
            }

            data.MultiNoise = new MultiNoiseData
            {
                ContinentalnessNoises = contNoises,
                ErosionNoises = erosionNoises,
                PeaksValleysNoises = pvNoises,
                ContinentalnessSplines = contSplines,
                ErosionSplines = erosionSplines,
                PeaksValleysSplines = pvSplines,
            };

            FastNoiseConfig selCfg = standardBiomes[0].biomeWeightNoiseConfig;
            selCfg.normalizeToZeroOne = true;
            data.SelectionNoise = FastNoiseFactory.CreateNoiseFromConfig(selCfg, _seed);
        }

        private static void DisposeCrossSectionData(ref CrossSectionNativeData data)
        {
            if (data.Biomes.IsCreated) data.Biomes.Dispose();
            if (data.MultiNoise.ContinentalnessNoises.IsCreated) data.MultiNoise.ContinentalnessNoises.Dispose();
            if (data.MultiNoise.ErosionNoises.IsCreated) data.MultiNoise.ErosionNoises.Dispose();
            if (data.MultiNoise.PeaksValleysNoises.IsCreated) data.MultiNoise.PeaksValleysNoises.Dispose();
            if (data.MultiNoise.ContinentalnessSplines.IsCreated) data.MultiNoise.ContinentalnessSplines.Dispose();
            if (data.MultiNoise.ErosionSplines.IsCreated) data.MultiNoise.ErosionSplines.Dispose();
            if (data.MultiNoise.PeaksValleysSplines.IsCreated) data.MultiNoise.PeaksValleysSplines.Dispose();
            if (data.DensityNoises.IsCreated) data.DensityNoises.Dispose();
            if (data.DensityWarpNoises.IsCreated) data.DensityWarpNoises.Dispose();
            if (data.StrataDepthNoises.IsCreated) data.StrataDepthNoises.Dispose();
            if (data.AllCaveLayers.IsCreated) data.AllCaveLayers.Dispose();
            if (data.CaveNoises.IsCreated) data.CaveNoises.Dispose();
            if (data.CaveWarpNoises.IsCreated) data.CaveWarpNoises.Dispose();
            if (data.AllLodes.IsCreated) data.AllLodes.Dispose();
            if (data.LodeNoises.IsCreated) data.LodeNoises.Dispose();
            if (data.AllTerrainLayers.IsCreated) data.AllTerrainLayers.Dispose();
        }

        #endregion

        #region Worm Carver Support

        /// <summary>
        /// Runs <see cref="StandardWormCarverJob"/> for each chunk the cross-section spans,
        /// producing per-chunk <see cref="NativeBitArray"/> worm masks.
        /// </summary>
        private Dictionary<int, NativeBitArray> GenerateWormMasks(int widthInBlocks, int globalZ, ref CrossSectionNativeData data)
        {
            Dictionary<int, NativeBitArray> masks = new Dictionary<int, NativeBitArray>();

            int startX = _offset.x;
            int endX = startX + widthInBlocks;

            int chunkStartX = (int)math.floor((float)startX / VoxelData.ChunkWidth) * VoxelData.ChunkWidth;
            int chunkEndX = (int)math.floor((float)(endX - 1) / VoxelData.ChunkWidth) * VoxelData.ChunkWidth;
            int chunkZ = (int)math.floor((float)globalZ / VoxelData.ChunkWidth) * VoxelData.ChunkWidth;

            for (int cx = chunkStartX; cx <= chunkEndX; cx += VoxelData.ChunkWidth)
            {
                NativeBitArray wormMask = new NativeBitArray(
                    VoxelData.ChunkWidth * VoxelData.ChunkHeight * VoxelData.ChunkWidth, Allocator.Persistent);

                StandardWormCarverJob wormJob = new StandardWormCarverJob
                {
                    BaseSeed = _seed,
                    ChunkPosition = new int2(cx, chunkZ),
                    Biomes = data.Biomes,
                    AllCaveLayers = data.AllCaveLayers,
                    BiomeSelectionNoise = data.SelectionNoise,
                    CaveNoises = data.CaveNoises,
                    OutputWormMask = wormMask,
                };

                wormJob.Execute();
                masks[cx] = wormMask;
            }

            return masks;
        }

        #endregion

        #region Column Evaluator

        /// <summary>
        /// Evaluates a single terrain column, replicating <see cref="StandardChunkGenerationJob"/> logic.
        /// </summary>
        /// <param name="forceBiomeIdx">If >= 0, forces this biome index (single-biome mode). If -1, uses Voronoi selection.</param>
        /// <param name="showCaves">Whether to evaluate cave carving.</param>
        /// <param name="showLodes">Whether to evaluate lode replacement.</param>
        /// <param name="localX">Local X within the chunk (for worm mask lookup).</param>
        /// <param name="localZ">Local Z within the chunk (for worm mask lookup).</param>
        /// <param name="wormMask">Worm carver bitmask for this chunk. Default if unavailable.</param>
        /// <param name="data">All shared native data.</param>
        private static ushort[] EvaluateColumn(
            int globalX, int globalZ, int seaLevel,
            int forceBiomeIdx, bool showCaves, bool showLodes,
            int localX, int localZ, ref NativeBitArray wormMask,
            ref CrossSectionNativeData data)
        {
            int chunkHeight = VoxelData.ChunkHeight;
            ushort[] column = new ushort[chunkHeight];

            // Biome selection
            int biomeIndex;
            if (forceBiomeIdx >= 0)
            {
                biomeIndex = forceBiomeIdx;
            }
            else
            {
                float biomeNoise = data.SelectionNoise.GetNoise(globalX, globalZ);
                biomeIndex = math.clamp((int)math.floor(biomeNoise * data.Biomes.Length), 0, data.Biomes.Length - 1);
            }

            StandardBiomeAttributesJobData biome = data.Biomes[biomeIndex];
            int surfaceBiomeIndex = biomeIndex;
            StandardBiomeAttributesJobData surfaceBiome = biome;

            // Terrain height
            float terrainHeightFloat;
            float borderFade;

            if (forceBiomeIdx >= 0)
            {
                // Single-biome: evaluate this biome's height directly (no blending)
                float cont = data.MultiNoise.ContinentalnessSplines[biomeIndex].Evaluate(
                    data.MultiNoise.ContinentalnessNoises[biomeIndex].GetNoise(globalX, globalZ));
                float erosion = data.MultiNoise.ErosionSplines[biomeIndex].Evaluate(
                    data.MultiNoise.ErosionNoises[biomeIndex].GetNoise(globalX, globalZ));
                float pv = data.MultiNoise.PeaksValleysSplines[biomeIndex].Evaluate(
                    data.MultiNoise.PeaksValleysNoises[biomeIndex].GetNoise(globalX, globalZ));
                terrainHeightFloat = biome.BaseTerrainHeight + cont + (pv * erosion);
                borderFade = 1f;
            }
            else
            {
                terrainHeightFloat = BiomeBlender.CalculateBlendedTerrainHeight(
                    globalX, globalZ, ref data.SelectionNoise, ref data.Biomes, ref data.MultiNoise, out borderFade);
            }

            int baseTerrainHeight = (int)math.floor(terrainHeightFloat);

            // Density band
            float effectiveDensityAmplitude = biome.DensityAmplitude * borderFade;
            int bandLow = baseTerrainHeight - (int)math.ceil(effectiveDensityAmplitude);
            int bandHigh = baseTerrainHeight + (int)math.ceil(effectiveDensityAmplitude);

            float previousDensity = -1f;
            int lastSurfaceY = baseTerrainHeight;

            float strataJitter = data.StrataDepthNoises[surfaceBiomeIndex].GetNoise(globalX, globalZ);
            int strataJitterBlocks = (int)math.round(strataJitter * 2.5f);

            bool hasWormMask = wormMask.IsCreated;

            for (int y = chunkHeight - 1; y >= 0; y--)
            {
                // ReSharper disable once RedundantAssignment
                ushort voxelValue = BlockIDs.Air;
                float density = baseTerrainHeight - y;

                // 3D Density
                if (biome.Enable3DDensity && y >= bandLow && y <= bandHigh)
                {
                    float dx = globalX, dy = y, dz = globalZ;
                    if (biome.EnableDensityWarp)
                        data.DensityWarpNoises[biomeIndex].DomainWarp(ref dx, ref dy, ref dz);
                    density += data.DensityNoises[biomeIndex].GetNoise(dx, dy, dz) * effectiveDensityAmplitude;
                }

                // Bedrock
                if (y == 0)
                {
                    voxelValue = BlockIDs.Bedrock;
                    density = 1f;
                }
                else if (density > 0f)
                {
                    bool isExposedSurface = (previousDensity <= 0f);

                    if (isExposedSurface)
                    {
                        lastSurfaceY = y;
                        voxelValue = y < seaLevel - 1 ? surfaceBiome.UnderwaterSurfaceBlockID : surfaceBiome.SurfaceBlockID;
                    }
                    else
                    {
                        voxelValue = BlockIDs.Stone;
                        int depthCounter = 0;
                        for (int i = 0; i < surfaceBiome.TerrainLayerCount; i++)
                        {
                            StandardTerrainLayerJobData layer = data.AllTerrainLayers[surfaceBiome.TerrainLayerStartIndex + i];
                            int effectiveDepth = math.max(1, layer.Depth + strataJitterBlocks);
                            if (y < lastSurfaceY - depthCounter && y >= lastSurfaceY - depthCounter - effectiveDepth)
                            {
                                voxelValue = layer.BlockID;
                                break;
                            }

                            depthCounter += effectiveDepth;
                        }
                    }
                }
                else
                {
                    voxelValue = y < seaLevel ? BlockIDs.Water : BlockIDs.Air;
                }

                previousDensity = density;

                // Cave carving
                if (showCaves && voxelValue != BlockIDs.Air && voxelValue != BlockIDs.Bedrock && voxelValue != BlockIDs.Water)
                {
                    for (int i = 0; i < biome.CaveLayerCount; i++)
                    {
                        int caveIdx = biome.CaveLayerStartIndex + i;
                        StandardCaveLayerJobData caveLayer = data.AllCaveLayers[caveIdx];

                        if (y < caveLayer.MinHeight || y > caveLayer.MaxHeight) continue;

                        // WormCarver — check bitmask
                        if (caveLayer.Mode == CaveMode.WormCarver)
                        {
                            if (hasWormMask)
                            {
                                int flatIdx = ChunkMath.GetFlattenedIndexInChunk(localX, y, localZ);
                                if (flatIdx >= 0 && flatIdx < wormMask.Length && wormMask.IsSet(flatIdx))
                                {
                                    voxelValue = BlockIDs.Air;
                                    break;
                                }
                            }

                            continue;
                        }

                        float depthFade = 1f;
                        if (caveLayer.DepthFadeMargin > 0)
                        {
                            int distFromEdge = math.min(y - caveLayer.MinHeight, caveLayer.MaxHeight - y);
                            depthFade = math.saturate((float)distFromEdge / caveLayer.DepthFadeMargin);
                        }

                        float effectiveThreshold = caveLayer.Threshold + (1f - depthFade) * (1f - caveLayer.Threshold);
                        FastNoiseLite caveNoise = data.CaveNoises[caveIdx];

                        if (caveLayer.Mode == CaveMode.Cheese)
                        {
                            float cx = globalX, cy = y, cz = globalZ;
                            if (caveLayer.EnableWarp) data.CaveWarpNoises[caveIdx].DomainWarp(ref cx, ref cy, ref cz);
                            if (caveNoise.GetNoise(cx, cy, cz) > effectiveThreshold)
                            {
                                voxelValue = BlockIDs.Air;
                                break;
                            }
                        }
                        else if (caveLayer.Mode == CaveMode.Spaghetti)
                        {
                            float bound = caveNoise.GetNoise(globalX * 0.25f, y * 0.25f, globalZ * 0.25f);
                            if (bound < effectiveThreshold - 0.2f) continue;
                            float noiseVal = (caveNoise.GetNoise(globalX, y) + caveNoise.GetNoise(y, globalZ) +
                                              caveNoise.GetNoise(globalX, globalZ) + caveNoise.GetNoise(y, globalX) +
                                              caveNoise.GetNoise(globalZ, y) + caveNoise.GetNoise(globalZ, globalX)) / 6f;
                            if (noiseVal > effectiveThreshold)
                            {
                                voxelValue = BlockIDs.Air;
                                break;
                            }
                        }
                        else if (caveLayer.Mode == CaveMode.Noodle)
                        {
                            float cx = globalX, cy = y, cz = globalZ;
                            if (caveLayer.EnableWarp) data.CaveWarpNoises[caveIdx].DomainWarp(ref cx, ref cy, ref cz);
                            float noiseVal = 1.0f - math.abs(caveNoise.GetNoise(cx, cy, cz));
                            if (noiseVal > effectiveThreshold)
                            {
                                voxelValue = BlockIDs.Air;
                                break;
                            }
                        }
                    }
                }

                // Lode pass
                if (showLodes && voxelValue == BlockIDs.Stone)
                {
                    for (int i = 0; i < biome.LodeCount; i++)
                    {
                        int lodeIdx = biome.LodeStartIndex + i;
                        StandardLodeJobData lode = data.AllLodes[lodeIdx];
                        if (y > lode.MinHeight && y < lode.MaxHeight)
                        {
                            if (data.LodeNoises[lodeIdx].GetNoise(globalX, y, globalZ) > lode.Threshold)
                                voxelValue = lode.BlockID;
                        }
                    }
                }

                column[y] = voxelValue;
            }

            return column;
        }

        #endregion

        #endregion
    }
}
