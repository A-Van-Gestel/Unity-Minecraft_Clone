using System.Collections.Generic;
using System.Linq;
using Data;
using Data.JobData;
using Data.WorldTypes;
using Jobs.Data;
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
        private NativeArray<StandardBiomeAttributesJobData> _biomesJobData;
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
            _blockTypesJobData = globalJobData.BlockTypesJobData;

            // --- Lookup Table Warmup (CRITICAL) ---
            // Forces the FastNoiseLite SharedStatic lookup tables to allocate unmanaged memory
            // via UnsafeUtility on the main thread. Without this, the SharedStatic pointers are null when
            // the first Burst worker thread executes GradCoord, resulting in a native crash.
            FastNoiseLite.InitializeLookupTables();

            // Cast BiomeBase[] → StandardBiomeAttributes[]
            _standardBiomes = new StandardBiomeAttributes[worldType.Biomes.Length];
            for (int i = 0; i < worldType.Biomes.Length; i++)
            {
                _standardBiomes[i] = (StandardBiomeAttributes)worldType.Biomes[i];
            }

            // --- Flatten biomes + lodes + caves into NativeArrays ---
            int totalLodeCount = 0;
            int totalCaveLayerCount = 0;
            foreach (StandardBiomeAttributes biome in _standardBiomes)
            {
                totalLodeCount += (biome.Lodes != null ? biome.Lodes.Length : 0);
                totalCaveLayerCount += (biome.CaveLayers != null ? biome.CaveLayers.Length : 0);
            }

            _biomesJobData = new NativeArray<StandardBiomeAttributesJobData>(_standardBiomes.Length, Allocator.Persistent);
            _allLodesJobData = new NativeArray<StandardLodeJobData>(totalLodeCount, Allocator.Persistent);
            _biomeTerrainNoises = new NativeArray<FastNoiseLite>(_standardBiomes.Length, Allocator.Persistent);
            _lodeNoises = new NativeArray<FastNoiseLite>(totalLodeCount, Allocator.Persistent);

            _allCaveLayersJobData = new NativeArray<StandardCaveLayerJobData>(totalCaveLayerCount, Allocator.Persistent);
            _caveNoises = new NativeArray<FastNoiseLite>(totalCaveLayerCount, Allocator.Persistent);
            _floraZoneNoises = new NativeArray<FastNoiseLite>(_standardBiomes.Length, Allocator.Persistent);

            int currentLodeIndex = 0;
            int currentCaveLayerIndex = 0;
            for (int i = 0; i < _standardBiomes.Length; i++)
            {
                StandardBiomeAttributes biome = _standardBiomes[i];
                int lodeCount = biome.Lodes != null ? biome.Lodes.Length : 0;
                int caveLayerCount = biome.CaveLayers != null ? biome.CaveLayers.Length : 0;

                // Build lode data + noise
                for (int j = 0; j < lodeCount; j++)
                {
                    _allLodesJobData[currentLodeIndex + j] = new StandardLodeJobData(biome.Lodes[j]);
                    _lodeNoises[currentLodeIndex + j] = CreateNoiseFromConfig(biome.Lodes[j].noiseConfig);
                }

                // Build cave layer data + noise
                for (int j = 0; j < caveLayerCount; j++)
                {
                    _allCaveLayersJobData[currentCaveLayerIndex + j] = new StandardCaveLayerJobData(biome.CaveLayers[j]);
                    _caveNoises[currentCaveLayerIndex + j] = CreateNoiseFromConfig(biome.CaveLayers[j].NoiseConfig);
                }

                // Build biome job data
                _biomesJobData[i] = new StandardBiomeAttributesJobData
                {
                    TerrainNoiseConfig = biome.TerrainNoiseConfig,
                    BiomeWeightNoiseConfig = biome.BiomeWeightNoiseConfig,
                    BaseTerrainHeight = biome.BaseTerrainHeight,
                    TerrainAmplitude = biome.TerrainAmplitude,
                    SurfaceBlockID = biome.SurfaceBlockID,
                    SubSurfaceBlockID = biome.SubSurfaceBlockID,
                    EnableMajorFlora = biome.EnableMajorFlora,
                    MajorFloraZoneCoverage = biome.MajorFloraZoneCoverage,
                    MajorFloraPlacementSpacing = biome.MajorFloraPlacementSpacing,
                    MajorFloraPlacementPadding = biome.MajorFloraPlacementPadding,
                    MajorFloraPlacementChance = biome.MajorFloraPlacementChance,
                    MajorFloraIndex = biome.MajorFloraIndex,
                    LodeStartIndex = currentLodeIndex,
                    LodeCount = lodeCount,
                    CaveLayerStartIndex = currentCaveLayerIndex,
                    CaveLayerCount = caveLayerCount,
                };

                // Build per-biome terrain noise
                _biomeTerrainNoises[i] = CreateNoiseFromConfig(biome.TerrainNoiseConfig);

                // Build per-biome flora zone noise
                _floraZoneNoises[i] = CreateNoiseFromConfig(biome.MajorFloraZoneNoiseConfig);

                currentLodeIndex += lodeCount;
                currentCaveLayerIndex += caveLayerCount;
            }

            // --- Biome Selection Noise (Cellular / Voronoi) ---
            // If the first biome has a BiomeWeightNoiseConfig, use it as the global biome selection noise.
            // Otherwise, use sensible defaults for Cellular noise.
            FastNoiseConfig selectionConfig = _standardBiomes.Length > 0
                ? _standardBiomes[0].BiomeWeightNoiseConfig
                : new FastNoiseConfig
                {
                    NoiseType = FastNoiseLite.NoiseType.Cellular,
                    Frequency = 0.005f,
                    CellularDistanceFunction = FastNoiseLite.CellularDistanceFunction.EuclideanSq,
                    CellularReturnType = FastNoiseLite.CellularReturnType.CellValue,
                    CellularJitter = 1.0f,
                };

            _biomeSelectionNoise = CreateNoiseFromConfig(selectionConfig);
        }

        /// <inheritdoc />
        public GenerationJobData ScheduleGeneration(ChunkCoord coord)
        {
            Vector2Int chunkVoxelPos = coord.ToVoxelOrigin();

            var modificationsQueue = new NativeQueue<VoxelMod>(Allocator.Persistent);
            var outputMap = new NativeArray<uint>(
                VoxelData.ChunkWidth * VoxelData.ChunkHeight * VoxelData.ChunkWidth, Allocator.Persistent);
            var outputHeightMap = new NativeArray<ushort>(
                VoxelData.ChunkWidth * VoxelData.ChunkWidth, Allocator.Persistent);

            var job = new StandardChunkGenerationJob
            {
                BaseSeed = _seed,
                ChunkPosition = new int2(chunkVoxelPos.x, chunkVoxelPos.y),
                BlockTypes = _blockTypesJobData,
                Biomes = _biomesJobData,
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

            // Biome selection
            float biomeNoise = _biomeSelectionNoise.GetNoise(globalPos.x, globalPos.z);
            int biomeIndex = (int)math.floor((biomeNoise + 1f) * 0.5f * _biomesJobData.Length);
            biomeIndex = math.clamp(biomeIndex, 0, _biomesJobData.Length - 1);

            StandardBiomeAttributesJobData biome = _biomesJobData[biomeIndex];

            // Terrain height
            float heightNoise = _biomeTerrainNoises[biomeIndex].GetNoise(globalPos.x, globalPos.z);
            int terrainHeight = (int)math.floor(biome.BaseTerrainHeight + heightNoise * biome.TerrainAmplitude);
            terrainHeight = math.clamp(terrainHeight, 1, VoxelData.ChunkHeight - 1);

            byte voxelValue;
            if (y == terrainHeight)
                voxelValue = biome.SurfaceBlockID;
            else if (y < terrainHeight && y > terrainHeight - 4)
                voxelValue = biome.SubSurfaceBlockID;
            else if (y > terrainHeight)
                return y < VoxelData.SeaLevel ? (byte)19 : (byte)0;
            else
                voxelValue = 1; // Stone

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
                        if (noiseVal > 0.5f)
                            voxelValue = lode.BlockID;
                    }
                }
            }

            return voxelValue;
        }

        /// <inheritdoc />
        public IEnumerable<VoxelMod> ExpandFlora(VoxelMod rootMod)
        {
            // Use Unity.Mathematics.Random for trunk height (deterministic per position + seed)
            uint deterministicSeed = math.max(1u, math.hash(new int3(
                rootMod.GlobalPosition.x, rootMod.GlobalPosition.z, _seed)));
            var random = new Random(deterministicSeed);

            return rootMod.ID switch
            {
                0 => MakeTree(rootMod.GlobalPosition, random),
                1 => MakeCacti(rootMod.GlobalPosition, random),
                _ => Enumerable.Empty<VoxelMod>(),
            };
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_biomesJobData.IsCreated) _biomesJobData.Dispose();
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

        #endregion

        #region Flora Generation

        /// <summary>
        /// Generates a tree structure using deterministic random for trunk height.
        /// Same visual output as the legacy tree but uses <c>Unity.Mathematics.Random</c>.
        /// </summary>
        private static IEnumerable<VoxelMod> MakeTree(Vector3Int position, Random random)
        {
            int height = random.NextInt(5, 13); // Trunk height range

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
        private static IEnumerable<VoxelMod> MakeCacti(Vector3Int position, Random random)
        {
            int height = random.NextInt(3, 7); // Cactus height range

            for (int i = 1; i <= height; i++)
                yield return new VoxelMod(new Vector3Int(position.x, position.y + i, position.z), BlockIDs.Cactus);
        }

        #endregion
    }
}
