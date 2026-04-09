using System.Collections.Generic;
using Data;
using Data.JobData;
using Data.WorldTypes;
using Jobs.Data;
using Jobs.Generators;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace Legacy
{
    /// <summary>
    /// IChunkGenerator implementation for legacy worlds.
    /// Uses <c>Mathf.PerlinNoise</c> via <see cref="LegacyWorldGen"/> and <see cref="LegacyChunkGenerationJob"/>.
    /// Owns all biome and lode NativeArrays for the legacy path.
    /// </summary>
    public class LegacyChunkGenerator : IChunkGenerator
    {
        private int _seed;
        private int _seaLevel;
        private int _solidGroundHeight;
        private NativeArray<LegacyBiomeAttributesJobData> _biomesJobData;
        private NativeArray<LegacyLodeJobData> _allLodesJobData;
        private NativeArray<BlockTypeJobData> _blockTypesJobData;
        private LegacyBiomeAttributes[] _legacyBiomes;

        #region IChunkGenerator

        /// <inheritdoc />
        public void Initialize(int seed, WorldTypeDefinition worldType, JobDataManager globalJobData)
        {
            _seed = seed;
            _seaLevel = worldType.SeaLevel;
            _solidGroundHeight = worldType.SolidGroundHeight;
            _blockTypesJobData = globalJobData.BlockTypesJobData;

            // Cast BiomeBase[] → LegacyBiomeAttributes[]
            _legacyBiomes = new LegacyBiomeAttributes[worldType.Biomes.Length];
            for (int i = 0; i < worldType.Biomes.Length; i++)
            {
                _legacyBiomes[i] = (LegacyBiomeAttributes)worldType.Biomes[i];
            }

            // Flatten biomes + lodes into NativeArrays (same pattern as the old PrepareJobData)
            int totalLodeCount = 0;
            foreach (LegacyBiomeAttributes biome in _legacyBiomes)
            {
                totalLodeCount += biome.lodes.Length;
            }

            _biomesJobData = new NativeArray<LegacyBiomeAttributesJobData>(_legacyBiomes.Length, Allocator.Persistent);
            _allLodesJobData = new NativeArray<LegacyLodeJobData>(totalLodeCount, Allocator.Persistent);

            int currentLodeIndex = 0;
            for (int i = 0; i < _legacyBiomes.Length; i++)
            {
                for (int j = 0; j < _legacyBiomes[i].lodes.Length; j++)
                {
                    _allLodesJobData[currentLodeIndex + j] = new LegacyLodeJobData(_legacyBiomes[i].lodes[j]);
                }

                _biomesJobData[i] = new LegacyBiomeAttributesJobData(_legacyBiomes[i], currentLodeIndex);
                currentLodeIndex += _legacyBiomes[i].lodes.Length;
            }
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

            LegacyChunkGenerationJob job = new LegacyChunkGenerationJob
            {
                Seed = _seed,
                SeaLevel = _seaLevel,
                SolidGroundHeight = _solidGroundHeight,
                ChunkPosition = new Vector2Int(chunkVoxelPos.x, chunkVoxelPos.y),
                BlockTypes = _blockTypesJobData,
                Biomes = _biomesJobData,
                AllLodes = _allLodesJobData,
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
            return LegacyWorldGen.GetVoxel(globalPos, _seed, _biomesJobData, _allLodesJobData, _solidGroundHeight, _seaLevel);
        }

        /// <inheritdoc />
        public IEnumerable<VoxelMod> ExpandFlora(VoxelMod rootMod)
        {
            // Resolve the correct biome at the flora position using the legacy biome selection logic.
            float strongestWeight = 0f;
            int strongestBiomeIndex = 0;

            for (int i = 0; i < _biomesJobData.Length; i++)
            {
                float weight = LegacyNoise.Get2DPerlin(
                    new Vector2(rootMod.GlobalPosition.x, rootMod.GlobalPosition.z),
                    _biomesJobData[i].Offset,
                    _biomesJobData[i].Scale);

                if (weight > strongestWeight)
                {
                    strongestWeight = weight;
                    strongestBiomeIndex = i;
                }
            }

            // Use the per-biome min/max height — fixes the original _world.biomes[0] hardcoded bug.
            int minHeight = _legacyBiomes[strongestBiomeIndex].minHeight;
            int maxHeight = _legacyBiomes[strongestBiomeIndex].maxHeight;

            return LegacyStructure.GenerateMajorFlora(rootMod.ID, rootMod.GlobalPosition, minHeight, maxHeight);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_biomesJobData.IsCreated) _biomesJobData.Dispose();
            if (_allLodesJobData.IsCreated) _allLodesJobData.Dispose();
        }

        #endregion
    }
}
