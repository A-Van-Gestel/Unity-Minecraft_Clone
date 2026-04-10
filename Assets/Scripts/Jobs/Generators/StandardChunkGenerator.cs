using System.Collections.Generic;
using System.Linq;
using Data;
using Data.JobData;
using Data.WorldTypes;
using Jobs.Data;
using Jobs.Helpers;
using Libraries;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Random = Unity.Mathematics.Random;

namespace Jobs.Generators
{
    /// <summary>
    /// <see cref="IChunkGenerator"/> implementation for Standard (FastNoiseLite / Burst-compiled) worlds.
    /// Owns all biome and lode NativeArrays, pre-constructed noise instances, and flora expansion logic.
    /// </summary>
    public class StandardChunkGenerator : IChunkGenerator
    {
        private int _seed;
        private int _seaLevel;
        private float _biomeBlendRadius;
        private NativeArray<StandardBiomeAttributesJobData> _biomesJobData;
        private NativeArray<StandardTerrainLayerJobData> _allTerrainLayersJobData;
        private NativeArray<StandardLodeJobData> _allLodesJobData;
        private NativeArray<StandardCaveLayerJobData> _allCaveLayersJobData;
        private NativeArray<BlockTypeJobData> _blockTypesJobData;
        private NativeArray<FastNoiseLite> _biomeTerrainNoises;
        private NativeArray<FastNoiseLite> _lodeNoises;
        private NativeArray<FastNoiseLite> _caveNoises;
        private NativeArray<FastNoiseLite> _floraZoneNoises;
        private FastNoiseLite _biomeSelectionNoise;
        private StandardBiomeAttributes[] _standardBiomes;

        #region IChunkGenerator

        /// <inheritdoc />
        public void Initialize(int seed, WorldTypeDefinition worldType, JobDataManager globalJobData)
        {
            _seed = seed;
            _seaLevel = worldType.seaLevel;
            _biomeBlendRadius = worldType.biomeBlendRadius;
            _blockTypesJobData = globalJobData.BlockTypesJobData;

            // --- Lookup Table Warmup (CRITICAL) ---
            // Forces the FastNoiseLite SharedStatic lookup tables to allocate unmanaged memory
            // via UnsafeUtility on the main thread. Without this, the SharedStatic pointers are null when
            // the first Burst worker thread executes GradCoord, resulting in a native crash.
            FastNoiseLite.InitializeLookupTables();

            // Cast BiomeBase[] → StandardBiomeAttributes[]
            _standardBiomes = new StandardBiomeAttributes[worldType.biomes.Length];
            for (int i = 0; i < worldType.biomes.Length; i++)
            {
                _standardBiomes[i] = (StandardBiomeAttributes)worldType.biomes[i];
            }

            // --- Flatten biomes + lodes + caves into NativeArrays ---
            int totalLodeCount = 0;
            int totalTerrainLayerCount = 0;
            int totalCaveLayerCount = 0;
            foreach (StandardBiomeAttributes biome in _standardBiomes)
            {
                totalLodeCount += biome.lodes?.Length ?? 0;
                totalCaveLayerCount += biome.caveLayers?.Length ?? 0;
                totalTerrainLayerCount += biome.terrainLayers?.Length ?? 0;
            }

            _biomesJobData = new NativeArray<StandardBiomeAttributesJobData>(_standardBiomes.Length, Allocator.Persistent);
            _allTerrainLayersJobData = new NativeArray<StandardTerrainLayerJobData>(totalTerrainLayerCount, Allocator.Persistent);
            _allLodesJobData = new NativeArray<StandardLodeJobData>(totalLodeCount, Allocator.Persistent);
            _biomeTerrainNoises = new NativeArray<FastNoiseLite>(_standardBiomes.Length, Allocator.Persistent);
            _lodeNoises = new NativeArray<FastNoiseLite>(totalLodeCount, Allocator.Persistent);

            _allCaveLayersJobData = new NativeArray<StandardCaveLayerJobData>(totalCaveLayerCount, Allocator.Persistent);
            _caveNoises = new NativeArray<FastNoiseLite>(totalCaveLayerCount, Allocator.Persistent);
            _floraZoneNoises = new NativeArray<FastNoiseLite>(_standardBiomes.Length, Allocator.Persistent);

            int currentLodeIndex = 0;
            int currentCaveLayerIndex = 0;
            int currentTerrainLayerIndex = 0;
            for (int i = 0; i < _standardBiomes.Length; i++)
            {
                StandardBiomeAttributes biome = _standardBiomes[i];
                int lodeCount = biome.lodes?.Length ?? 0;
                int caveLayerCount = biome.caveLayers?.Length ?? 0;
                int terrainLayerCount = biome.terrainLayers?.Length ?? 0;

                // Build terrain layer data
                for (int j = 0; j < terrainLayerCount; j++)
                {
                    _allTerrainLayersJobData[currentTerrainLayerIndex + j] = new StandardTerrainLayerJobData(biome.terrainLayers[j]);
                }

                // Build lode data + noise
                for (int j = 0; j < lodeCount; j++)
                {
                    _allLodesJobData[currentLodeIndex + j] = new StandardLodeJobData(biome.lodes[j]);
                    _lodeNoises[currentLodeIndex + j] = CreateNoiseFromConfig(biome.lodes[j].noiseConfig);
                }

                // Build cave layer data + noise
                for (int j = 0; j < caveLayerCount; j++)
                {
                    _allCaveLayersJobData[currentCaveLayerIndex + j] = new StandardCaveLayerJobData(biome.caveLayers[j]);
                    _caveNoises[currentCaveLayerIndex + j] = CreateNoiseFromConfig(biome.caveLayers[j].noiseConfig);
                }

                // Build biome job data
                _biomesJobData[i] = new StandardBiomeAttributesJobData
                {
                    TerrainNoiseConfig = biome.terrainNoiseConfig,
                    BiomeWeightNoiseConfig = biome.biomeWeightNoiseConfig,
                    BaseTerrainHeight = biome.baseTerrainHeight,
                    TerrainAmplitude = biome.terrainAmplitude,
                    SurfaceBlockID = (byte)biome.surfaceBlockID,
                    UnderwaterSurfaceBlockID = (byte)biome.underwaterSurfaceBlockID,
                    EnableMajorFlora = biome.enableMajorFlora,
                    MajorFloraZoneCoverage = biome.majorFloraZoneCoverage,
                    MajorFloraPlacementSpacing = biome.majorFloraPlacementSpacing,
                    MajorFloraPlacementPadding = biome.majorFloraPlacementPadding,
                    MajorFloraPlacementChance = biome.majorFloraPlacementChance,
                    MajorFloraPlacementMinHeight = biome.majorFloraPlacementMinHeight,
                    MajorFloraPlacementMaxHeight = biome.majorFloraPlacementMaxHeight,
                    MajorFloraMinPhysicalHeight = biome.majorFloraMinPhysicalHeight,
                    MajorFloraMaxPhysicalHeight = biome.majorFloraMaxPhysicalHeight,
                    MajorFloraIndex = biome.majorFloraIndex,
                    TerrainLayerStartIndex = currentTerrainLayerIndex,
                    TerrainLayerCount = terrainLayerCount,
                    LodeStartIndex = currentLodeIndex,
                    LodeCount = lodeCount,
                    CaveLayerStartIndex = currentCaveLayerIndex,
                    CaveLayerCount = caveLayerCount,
                };

                // Build per-biome terrain noise
                _biomeTerrainNoises[i] = CreateNoiseFromConfig(biome.terrainNoiseConfig);

                // Build per-biome flora zone noise
                _floraZoneNoises[i] = CreateNoiseFromConfig(biome.majorFloraZoneNoiseConfig);

                currentLodeIndex += lodeCount;
                currentCaveLayerIndex += caveLayerCount;
                currentTerrainLayerIndex += terrainLayerCount;
            }

            // --- Biome Selection Noise (Cellular / Voronoi) ---
            // If the first biome has a BiomeWeightNoiseConfig, use it as the global biome selection noise.
            // Otherwise, use sensible defaults for Cellular noise.
            FastNoiseConfig selectionConfig = _standardBiomes.Length > 0
                ? _standardBiomes[0].biomeWeightNoiseConfig
                : new FastNoiseConfig
                {
                    noiseType = FastNoiseLite.NoiseType.Cellular,
                    frequency = 0.005f,
                    cellularDistanceFunction = FastNoiseLite.CellularDistanceFunction.EuclideanSq,
                    cellularReturnType = FastNoiseLite.CellularReturnType.CellValue,
                    cellularJitter = 1.0f,
                };

            selectionConfig.normalizeToZeroOne = true; // Enforce [0,1] normalization internally
            _biomeSelectionNoise = CreateNoiseFromConfig(selectionConfig);
        }

        /// <inheritdoc />
        public GenerationJobData ScheduleGeneration(ChunkCoord coord)
        {
            Vector2Int chunkVoxelPos = coord.ToVoxelOrigin();

            NativeQueue<VoxelMod> modificationsQueue = new NativeQueue<VoxelMod>(Allocator.Persistent);
            NativeArray<uint> outputMap = new NativeArray<uint>(
                VoxelData.ChunkWidth * VoxelData.ChunkHeight * VoxelData.ChunkWidth, Allocator.Persistent);
            NativeArray<ushort> outputHeightMap = new NativeArray<ushort>(
                VoxelData.ChunkWidth * VoxelData.ChunkWidth, Allocator.Persistent);

            StandardChunkGenerationJob job = new StandardChunkGenerationJob
            {
                SeaLevel = _seaLevel,
                BiomeBlendRadius = _biomeBlendRadius,
                BaseSeed = _seed,
                ChunkPosition = new int2(chunkVoxelPos.x, chunkVoxelPos.y),
                BlockTypes = _blockTypesJobData,
                Biomes = _biomesJobData,
                AllTerrainLayers = _allTerrainLayersJobData,
                AllLodes = _allLodesJobData,
                AllCaveLayers = _allCaveLayersJobData,
                BiomeTerrainNoises = _biomeTerrainNoises,
                LodeNoises = _lodeNoises,
                CaveNoises = _caveNoises,
                FloraZoneNoises = _floraZoneNoises,
                BiomeSelectionNoise = _biomeSelectionNoise,
                OutputMap = outputMap,
                OutputHeightMap = outputHeightMap,
                Modifications = modificationsQueue.AsParallelWriter(),
            };

            JobHandle handle = job.ScheduleParallelByRef(VoxelData.ChunkWidth * VoxelData.ChunkWidth, 8, default);

            return new GenerationJobData
            {
                Handle = handle,
                Map = outputMap,
                HeightMap = outputHeightMap,
                Mods = modificationsQueue,
            };
        }

        /// <inheritdoc />
        public byte GetVoxel(Vector3Int globalPos)
        {
            int y = globalPos.y;

            // Bedrock
            if (y == 0) return 8;

            // Biome selection (now enforced to normalized [0,1] domain)
            float biomeNoise = _biomeSelectionNoise.GetNoise(globalPos.x, globalPos.z);
            int biomeIndex = (int)math.floor(biomeNoise * _biomesJobData.Length);
            biomeIndex = math.clamp(biomeIndex, 0, _biomesJobData.Length - 1);

            StandardBiomeAttributesJobData biome = _biomesJobData[biomeIndex];

            // Terrain height
            int terrainHeight = BiomeBlender.CalculateBlendedTerrainHeight(
                globalPos.x, globalPos.z, _biomeBlendRadius, ref _biomeSelectionNoise, ref _biomesJobData, ref _biomeTerrainNoises);

            byte voxelValue;
            if (y == terrainHeight)
            {
                voxelValue = y < _seaLevel - 1 ? biome.UnderwaterSurfaceBlockID : biome.SurfaceBlockID;
            }
            else if (y > terrainHeight)
            {
                return y < _seaLevel ? (byte)19 : (byte)0;
            }
            else
            {
                // Assign Stone as default baseline
                voxelValue = 1;

                // Evaluate Terrain Layers (subsurface block swapping) map over stone
                int depthCounter = 0;
                for (int i = 0; i < biome.TerrainLayerCount; i++)
                {
                    StandardTerrainLayerJobData layer = _allTerrainLayersJobData[biome.TerrainLayerStartIndex + i];
                    if (y < terrainHeight - depthCounter && y >= terrainHeight - depthCounter - layer.Depth)
                    {
                        voxelValue = layer.BlockID;
                        break;
                    }

                    depthCounter += layer.Depth;
                }
            }

            // Caves
            if (voxelValue != 0 && voxelValue != 8 && voxelValue != 19 && _blockTypesJobData[voxelValue].FluidType == FluidType.None)
            {
                for (int i = 0; i < biome.CaveLayerCount; i++)
                {
                    int caveIdx = biome.CaveLayerStartIndex + i;
                    StandardCaveLayerJobData caveLayer = _allCaveLayersJobData[caveIdx];

                    if (y < caveLayer.MinHeight || y > caveLayer.MaxHeight) continue;

                    float depthFade = 1f;
                    if (caveLayer.DepthFadeMargin > 0)
                    {
                        int distFromMin = y - caveLayer.MinHeight;
                        int distFromMax = caveLayer.MaxHeight - y;
                        int distFromEdge = math.min(distFromMin, distFromMax);
                        depthFade = math.saturate((float)distFromEdge / caveLayer.DepthFadeMargin);
                    }

                    FastNoiseLite caveNoise = _caveNoises[caveIdx];
                    float noiseVal;

                    if (caveLayer.Mode == CaveMode.Spaghetti)
                    {
                        // 25% frequency bounding check
                        float bound = caveNoise.GetNoise(globalPos.x * 0.25f, y * 0.25f, globalPos.z * 0.25f);
                        float effectiveThreshold = caveLayer.Threshold + (1f - depthFade) * (1f - caveLayer.Threshold);
                        if (bound < effectiveThreshold - 0.2f) continue; // Skip to save performance

                        noiseVal = (caveNoise.GetNoise(globalPos.x, y) + caveNoise.GetNoise(y, globalPos.z) +
                                    caveNoise.GetNoise(globalPos.x, globalPos.z) + caveNoise.GetNoise(y, globalPos.x) +
                                    caveNoise.GetNoise(globalPos.z, y) + caveNoise.GetNoise(globalPos.z, globalPos.x)) / 6f;
                    }
                    else
                    {
                        noiseVal = caveNoise.GetNoise(globalPos.x, y, globalPos.z);
                    }

                    float finalThreshold = caveLayer.Threshold + (1f - depthFade) * (1f - caveLayer.Threshold);
                    if (noiseVal > finalThreshold)
                    {
                        voxelValue = 0; // Air
                        break;
                    }
                }
            }

            // Lodes
            if (voxelValue == 1)
            {
                for (int i = 0; i < biome.LodeCount; i++)
                {
                    int lodeIdx = biome.LodeStartIndex + i;
                    StandardLodeJobData lode = _allLodesJobData[lodeIdx];
                    if (y > lode.MinHeight && y < lode.MaxHeight)
                    {
                        float noiseVal = _lodeNoises[lodeIdx].GetNoise(globalPos.x, y, globalPos.z);
                        if (noiseVal > lode.Threshold)
                            voxelValue = lode.BlockID;
                    }
                }
            }

            return voxelValue;
        }

        /// <inheritdoc />
        public IEnumerable<VoxelMod> ExpandFlora(VoxelMod rootMod)
        {
            // Evaluate biome at placement position to lookup exact Flora Size Traits
            float biomeNoise = _biomeSelectionNoise.GetNoise(rootMod.GlobalPosition.x, rootMod.GlobalPosition.z);
            int biomeIndex = (int)math.floor(biomeNoise * _biomesJobData.Length);
            biomeIndex = math.clamp(biomeIndex, 0, _biomesJobData.Length - 1);
            StandardBiomeAttributesJobData biome = _biomesJobData[biomeIndex];

            // Use Unity.Mathematics.Random for trunk height (deterministic per position + seed)
            uint deterministicSeed = math.max(1u, math.hash(new int3(
                rootMod.GlobalPosition.x, rootMod.GlobalPosition.z, _seed)));
            Random random = new Random(deterministicSeed);

            return rootMod.ID switch
            {
                0 => MakeTree(rootMod.GlobalPosition, random, biome.MajorFloraMinPhysicalHeight, biome.MajorFloraMaxPhysicalHeight),
                1 => MakeCacti(rootMod.GlobalPosition, random, biome.MajorFloraMinPhysicalHeight, biome.MajorFloraMaxPhysicalHeight),
                _ => Enumerable.Empty<VoxelMod>(),
            };
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_biomesJobData.IsCreated) _biomesJobData.Dispose();
            if (_allTerrainLayersJobData.IsCreated) _allTerrainLayersJobData.Dispose();
            if (_allLodesJobData.IsCreated) _allLodesJobData.Dispose();
            if (_biomeTerrainNoises.IsCreated) _biomeTerrainNoises.Dispose();
            if (_lodeNoises.IsCreated) _lodeNoises.Dispose();
            if (_allCaveLayersJobData.IsCreated) _allCaveLayersJobData.Dispose();
            if (_caveNoises.IsCreated) _caveNoises.Dispose();
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Constructs a <see cref="FastNoiseLite"/> instance from a <see cref="FastNoiseConfig"/> struct.
        /// Must be called on the main thread.
        /// </summary>
        private FastNoiseLite CreateNoiseFromConfig(FastNoiseConfig config)
        {
            FastNoiseLite noise = FastNoiseLite.Create(_seed + config.seedOffset);
            noise.SetFrequency(config.frequency);
            noise.SetNoiseType(config.noiseType);
            noise.SetRotationType3D(config.rotationType3D);
            noise.SetFractalType(config.fractalType);
            noise.SetFractalOctaves(config.octaves);
            noise.SetFractalGain(config.gain);
            noise.SetFractalLacunarity(config.lacunarity);
            noise.SetFractalWeightedStrength(config.weightedStrength);
            noise.SetFractalPingPongStrength(config.pingPongStrength);
            noise.SetCellularDistanceFunction(config.cellularDistanceFunction);
            noise.SetCellularReturnType(config.cellularReturnType);
            noise.SetCellularJitter(config.cellularJitter);
            noise.SetNormalizeToZeroOne(config.normalizeToZeroOne);
            return noise;
        }

        #endregion

        #region Flora Generation

        /// <summary>
        /// Generates a tree structure using deterministic random for trunk height.
        /// Same visual output as the legacy tree but uses <c>Unity.Mathematics.Random</c>.
        /// </summary>
        private static IEnumerable<VoxelMod> MakeTree(Vector3Int position, Random random, int minHeight, int maxHeight)
        {
            int height = random.NextInt(minHeight, maxHeight + 1); // Trunk height range

            // LEAVES
            VoxelMod leafMod = new VoxelMod { ID = BlockIDs.OakLeaves };

            for (int x = -2; x < 3; x++)
            {
                for (int z = -2; z < 3; z++)
                {
                    leafMod.GlobalPosition = new Vector3Int(position.x + x, position.y + height - 2, position.z + z);
                    yield return leafMod;
                    leafMod.GlobalPosition = new Vector3Int(position.x + x, position.y + height - 3, position.z + z);
                    yield return leafMod;
                }
            }

            for (int x = -1; x < 2; x++)
            {
                for (int z = -1; z < 2; z++)
                {
                    leafMod.GlobalPosition = new Vector3Int(position.x + x, position.y + height - 1, position.z + z);
                    yield return leafMod;
                }
            }

            for (int x = -1; x < 2; x++)
            {
                if (x == 0)
                    for (int z = -1; z < 2; z++)
                    {
                        leafMod.GlobalPosition = new Vector3Int(position.x + x, position.y + height, position.z + z);
                        yield return leafMod;
                    }
                else
                {
                    leafMod.GlobalPosition = new Vector3Int(position.x + x, position.y + height, position.z);
                    yield return leafMod;
                }
            }

            // TRUNK
            for (int i = 1; i <= height; i++)
                yield return new VoxelMod(new Vector3Int(position.x, position.y + i, position.z), BlockIDs.OakLog);
        }

        /// <summary>
        /// Generates a cactus structure using deterministic random for trunk height.
        /// </summary>
        private static IEnumerable<VoxelMod> MakeCacti(Vector3Int position, Random random, int minHeight, int maxHeight)
        {
            int height = random.NextInt(minHeight, maxHeight + 1); // Cactus height range

            for (int i = 1; i <= height; i++)
                yield return new VoxelMod(new Vector3Int(position.x, position.y + i, position.z), BlockIDs.Cactus);
        }

        #endregion
    }
}
