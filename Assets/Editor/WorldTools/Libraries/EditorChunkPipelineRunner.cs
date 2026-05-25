using System;
using System.Collections.Generic;
using Data;
using Data.JobData;
using Data.NativeData;
using Data.WorldTypes;
using Helpers;
using Jobs;
using Jobs.Data;
using Jobs.Generators;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace Editor.WorldTools.Libraries
{
    /// <summary>
    /// Wraps <see cref="StandardChunkGenerator"/> and job scheduling for editor-time chunk
    /// generation, lighting, and meshing without requiring a <c>World</c> instance or any
    /// MonoBehaviour lifecycle. All Burst jobs are reused directly from the runtime pipeline.
    /// </summary>
    public class EditorChunkPipelineRunner : IDisposable
    {
        private StandardChunkGenerator _generator;
        private JobDataManager _jobDataManager;
        private FluidVertexTemplatesNativeData _fluidTemplates;
        private bool _isInitialized;

        /// <summary>
        /// Whether the runner has been initialized and is ready to schedule jobs.
        /// </summary>
        public bool IsInitialized => _isInitialized;

        /// <summary>Controls which optional generation passes (caves, lodes, water) are executed.</summary>
        public GenerationFeatureFlags FeatureFlags { get; set; } = GenerationFeatureFlags.Default;

        /// <summary>
        /// When set, overrides the sea level from the <see cref="WorldTypeDefinition"/> asset.
        /// Leave <c>null</c> to use the world type's default sea level.
        /// </summary>
        public int? SeaLevelOverride { get; set; }

        /// <summary>
        /// The job data manager containing block types and custom mesh data.
        /// </summary>
        public JobDataManager JobDataManager => _jobDataManager;

        /// <summary>
        /// The fluid vertex template data for water and lava meshing.
        /// </summary>
        public FluidVertexTemplatesNativeData FluidTemplates => _fluidTemplates;

        /// <summary>
        /// Initializes the pipeline runner with the given configuration.
        /// Must be called before scheduling any jobs.
        /// </summary>
        /// <param name="seed">The world seed for terrain generation.</param>
        /// <param name="worldType">The world type definition containing biome configurations.</param>
        /// <param name="blockDatabase">The block database asset.</param>
        public void Initialize(int seed, WorldTypeDefinition worldType, BlockDatabase blockDatabase, bool isSingleBiomeMode = false, StandardBiomeAttributes selectedBiome = null)
        {
            Dispose();

            (_jobDataManager, _fluidTemplates) = EditorJobDataManagerFactory.Create(blockDatabase);

            _generator = new StandardChunkGenerator();
            _generator.Initialize(seed, worldType, _jobDataManager, isSingleBiomeMode, selectedBiome);

            _isInitialized = true;
        }

        /// <summary>
        /// Schedules a chunk generation job for the given coordinate.
        /// </summary>
        /// <param name="coord">The chunk coordinate to generate.</param>
        /// <returns>The generation job data containing the handle and output containers.</returns>
        public GenerationJobData ScheduleGeneration(ChunkCoord coord)
        {
            _generator.FeatureFlags = FeatureFlags;
            if (SeaLevelOverride.HasValue)
                _generator.SeaLevel = SeaLevelOverride.Value;
            return _generator.ScheduleGeneration(coord);
        }

        /// <summary>
        /// Expands a structure spawn marker into its constituent voxel modifications.
        /// Must be called on the main thread after generation completes.
        /// </summary>
        /// <param name="marker">The spawn marker emitted during generation.</param>
        /// <returns>An enumerable of voxel modifications representing the full structure.</returns>
        public IEnumerable<VoxelMod> ExpandStructure(StructureSpawnMarker marker)
        {
            return _generator.ExpandStructure(marker);
        }

        /// <summary>
        /// Schedules a lighting job for the given chunk using stored map data.
        /// Builds the initial sunlight column recalculation queue for all 256 columns.
        /// </summary>
        /// <param name="chunkCoord">The chunk coordinate to light.</param>
        /// <param name="maps">Dictionary of all generated chunk maps keyed by voxel origin.</param>
        /// <param name="heightMaps">Dictionary of all generated heightmaps keyed by voxel origin.</param>
        /// <returns>The lighting job data, or null if neighbors are missing.</returns>
        public LightingJobData? ScheduleLighting(
            ChunkCoord chunkCoord,
            Dictionary<Vector2Int, NativeArray<uint>> maps,
            Dictionary<Vector2Int, NativeArray<ushort>> heightMaps)
        {
            Vector2Int voxelOrigin = chunkCoord.ToVoxelOrigin();

            if (!TryGetNeighborMap(maps, chunkCoord, 0, 1, out NativeArray<uint> neighborN) ||
                !TryGetNeighborMap(maps, chunkCoord, 1, 0, out NativeArray<uint> neighborE) ||
                !TryGetNeighborMap(maps, chunkCoord, 0, -1, out NativeArray<uint> neighborS) ||
                !TryGetNeighborMap(maps, chunkCoord, -1, 0, out NativeArray<uint> neighborW) ||
                !TryGetNeighborMap(maps, chunkCoord, 1, 1, out NativeArray<uint> neighborNE) ||
                !TryGetNeighborMap(maps, chunkCoord, 1, -1, out NativeArray<uint> neighborSE) ||
                !TryGetNeighborMap(maps, chunkCoord, -1, -1, out NativeArray<uint> neighborSW) ||
                !TryGetNeighborMap(maps, chunkCoord, -1, 1, out NativeArray<uint> neighborNW))
            {
                return null;
            }

            if (!heightMaps.TryGetValue(voxelOrigin, out NativeArray<ushort> heightMap))
                return null;

            LightingJobInputData inputData = new LightingJobInputData
            {
                Heightmap = new NativeArray<ushort>(heightMap, Allocator.Persistent),
                NeighborN = new NativeArray<uint>(neighborN, Allocator.Persistent),
                NeighborE = new NativeArray<uint>(neighborE, Allocator.Persistent),
                NeighborS = new NativeArray<uint>(neighborS, Allocator.Persistent),
                NeighborW = new NativeArray<uint>(neighborW, Allocator.Persistent),
                NeighborNE = new NativeArray<uint>(neighborNE, Allocator.Persistent),
                NeighborSE = new NativeArray<uint>(neighborSE, Allocator.Persistent),
                NeighborSW = new NativeArray<uint>(neighborSW, Allocator.Persistent),
                NeighborNW = new NativeArray<uint>(neighborNW, Allocator.Persistent),
            };

            // Build initial sunlight column recalculation queue: all 256 columns
            NativeQueue<Vector2Int> sunlightRecalcQueue = new NativeQueue<Vector2Int>(Allocator.Persistent);
            for (int x = 0; x < VoxelData.ChunkWidth; x++)
            {
                for (int z = 0; z < VoxelData.ChunkWidth; z++)
                {
                    sunlightRecalcQueue.Enqueue(new Vector2Int(x, z));
                }
            }

            LightingJobData jobData = new LightingJobData
            {
                Input = inputData,
                Map = new NativeArray<uint>(maps[voxelOrigin], Allocator.Persistent),
                Mods = new NativeList<LightModification>(Allocator.Persistent),
                IsStable = new NativeArray<bool>(1, Allocator.Persistent),
                SunLightQueue = new NativeQueue<LightQueueNode>(Allocator.Persistent),
                BlockLightQueue = new NativeQueue<LightQueueNode>(Allocator.Persistent),
                SunLightRecalcQueue = sunlightRecalcQueue,
            };

            NeighborhoodLightingJob job = new NeighborhoodLightingJob
            {
                Map = jobData.Map,
                ChunkPosition = voxelOrigin,
                SunlightBfsQueue = jobData.SunLightQueue,
                BlocklightBfsQueue = jobData.BlockLightQueue,
                SunlightColumnRecalcQueue = jobData.SunLightRecalcQueue,
                Heightmap = jobData.Input.Heightmap,
                NeighborN = jobData.Input.NeighborN,
                NeighborE = jobData.Input.NeighborE,
                NeighborS = jobData.Input.NeighborS,
                NeighborW = jobData.Input.NeighborW,
                NeighborNE = jobData.Input.NeighborNE,
                NeighborSE = jobData.Input.NeighborSE,
                NeighborSW = jobData.Input.NeighborSW,
                NeighborNW = jobData.Input.NeighborNW,
                BlockTypes = _jobDataManager.BlockTypesJobData,
                CrossChunkLightMods = jobData.Mods,
                IsStable = jobData.IsStable,
                PerformEdgeCheck = false,
            };

            jobData.Handle = job.Schedule();
            return jobData;
        }

        /// <summary>
        /// Schedules a mesh generation job for the given chunk using stored (lit) map data.
        /// </summary>
        /// <param name="chunkCoord">The chunk coordinate to mesh.</param>
        /// <param name="chunkVoxelPos">The voxel-space origin of the chunk.</param>
        /// <param name="maps">Dictionary of all chunk maps keyed by voxel origin.</param>
        /// <param name="clipBounds">Axis-aligned clip bounds. Use <see cref="MeshClipBounds.Disabled"/> for no clipping.</param>
        /// <returns>A tuple of the combined job handle and mesh output, or null if neighbors are missing.</returns>
        public (JobHandle handle, MeshDataJobOutput output)? ScheduleMeshing(
            ChunkCoord chunkCoord,
            Vector2Int chunkVoxelPos,
            Dictionary<Vector2Int, NativeArray<uint>> maps,
            MeshClipBounds clipBounds)
        {
            if (!maps.TryGetValue(chunkVoxelPos, out NativeArray<uint> centerMap))
                return null;

            if (!TryGetNeighborMap(maps, chunkCoord, 0, -1, out NativeArray<uint> back) ||
                !TryGetNeighborMap(maps, chunkCoord, 0, 1, out NativeArray<uint> front) ||
                !TryGetNeighborMap(maps, chunkCoord, -1, 0, out NativeArray<uint> left) ||
                !TryGetNeighborMap(maps, chunkCoord, 1, 0, out NativeArray<uint> right) ||
                !TryGetNeighborMap(maps, chunkCoord, 1, 1, out NativeArray<uint> frontRight) ||
                !TryGetNeighborMap(maps, chunkCoord, 1, -1, out NativeArray<uint> backRight) ||
                !TryGetNeighborMap(maps, chunkCoord, -1, -1, out NativeArray<uint> backLeft) ||
                !TryGetNeighborMap(maps, chunkCoord, -1, 1, out NativeArray<uint> frontLeft))
            {
                return null;
            }

            // Compute SectionJobData from the raw map
            const int sectionCount = VoxelData.ChunkHeight / ChunkMath.SECTION_SIZE;
            NativeArray<SectionJobData> sectionData = new NativeArray<SectionJobData>(sectionCount, Allocator.Persistent);
            ComputeSectionData(centerMap, _jobDataManager.BlockTypesJobData, sectionData, sectionCount);

            // Create snapshot copies for the job
            NativeArray<uint> mapCopy = new NativeArray<uint>(centerMap, Allocator.Persistent);
            NativeArray<uint> backCopy = new NativeArray<uint>(back, Allocator.Persistent);
            NativeArray<uint> frontCopy = new NativeArray<uint>(front, Allocator.Persistent);
            NativeArray<uint> leftCopy = new NativeArray<uint>(left, Allocator.Persistent);
            NativeArray<uint> rightCopy = new NativeArray<uint>(right, Allocator.Persistent);
            NativeArray<uint> frontRightCopy = new NativeArray<uint>(frontRight, Allocator.Persistent);
            NativeArray<uint> backRightCopy = new NativeArray<uint>(backRight, Allocator.Persistent);
            NativeArray<uint> backLeftCopy = new NativeArray<uint>(backLeft, Allocator.Persistent);
            NativeArray<uint> frontLeftCopy = new NativeArray<uint>(frontLeft, Allocator.Persistent);

            MeshDataJobOutput meshOutput = new MeshDataJobOutput(Allocator.Persistent);

            MeshGenerationJob job = new MeshGenerationJob
            {
                Map = mapCopy,
                SectionData = sectionData,
                BlockTypes = _jobDataManager.BlockTypesJobData,
                ChunkPosition = new Vector3(chunkVoxelPos.x, 0, chunkVoxelPos.y),
                NeighborBack = backCopy,
                NeighborFront = frontCopy,
                NeighborLeft = leftCopy,
                NeighborRight = rightCopy,
                NeighborFrontRight = frontRightCopy,
                NeighborBackRight = backRightCopy,
                NeighborBackLeft = backLeftCopy,
                NeighborFrontLeft = frontLeftCopy,
                CustomMeshes = _jobDataManager.CustomMeshesJobData,
                CustomFaces = _jobDataManager.CustomFacesJobData,
                CustomVerts = _jobDataManager.CustomVertsJobData,
                CustomTris = _jobDataManager.CustomTrisJobData,
                WaterVertexTemplates = _fluidTemplates.WaterVertexTemplates,
                LavaVertexTemplates = _fluidTemplates.LavaVertexTemplates,
                ClipBounds = clipBounds,
                Output = meshOutput,
            };

            JobHandle meshJobHandle = job.Schedule();

            // Schedule disposal of snapshot copies after the job completes
            NativeArray<JobHandle> disposalHandles = new NativeArray<JobHandle>(10, Allocator.Persistent);
            disposalHandles[0] = mapCopy.Dispose(meshJobHandle);
            disposalHandles[1] = sectionData.Dispose(meshJobHandle);
            disposalHandles[2] = backCopy.Dispose(meshJobHandle);
            disposalHandles[3] = frontCopy.Dispose(meshJobHandle);
            disposalHandles[4] = leftCopy.Dispose(meshJobHandle);
            disposalHandles[5] = rightCopy.Dispose(meshJobHandle);
            disposalHandles[6] = frontRightCopy.Dispose(meshJobHandle);
            disposalHandles[7] = backRightCopy.Dispose(meshJobHandle);
            disposalHandles[8] = backLeftCopy.Dispose(meshJobHandle);
            disposalHandles[9] = frontLeftCopy.Dispose(meshJobHandle);

            JobHandle combinedDisposalHandle = JobHandle.CombineDependencies(disposalHandles);
            JobHandle finalHandle = disposalHandles.Dispose(combinedDisposalHandle);

            return (finalHandle, meshOutput);
        }

        /// <summary>
        /// Releases all native resources owned by the runner.
        /// </summary>
        public void Dispose()
        {
            _generator?.Dispose();
            _generator = null;

            _jobDataManager?.Dispose();
            _jobDataManager = null;

            _fluidTemplates?.Dispose();
            _fluidTemplates = null;

            _isInitialized = false;
        }

        private static bool TryGetNeighborMap(
            Dictionary<Vector2Int, NativeArray<uint>> maps,
            ChunkCoord center,
            int dx, int dz,
            out NativeArray<uint> neighborMap)
        {
            Vector2Int neighborOrigin = center.Neighbor(dx, dz).ToVoxelOrigin();
            return maps.TryGetValue(neighborOrigin, out neighborMap);
        }

        private static void ComputeSectionData(
            NativeArray<uint> map,
            NativeArray<BlockTypeJobData> blockTypes,
            NativeArray<SectionJobData> sectionData,
            int sectionCount)
        {
            for (int s = 0; s < sectionCount; s++)
            {
                int yStart = s * ChunkMath.SECTION_SIZE;
                int nonAirCount = 0;
                int opaqueCount = 0;
                const int totalVoxels = VoxelData.ChunkWidth * ChunkMath.SECTION_SIZE * VoxelData.ChunkWidth;

                for (int x = 0; x < VoxelData.ChunkWidth; x++)
                {
                    for (int y = yStart; y < yStart + ChunkMath.SECTION_SIZE; y++)
                    {
                        for (int z = 0; z < VoxelData.ChunkWidth; z++)
                        {
                            int index = ChunkMath.GetFlattenedIndexInChunk(x, y, z);
                            uint packed = map[index];
                            ushort blockId = (ushort)(packed & 0xFFFF);
                            if (blockId == 0) continue;
                            nonAirCount++;
                            if (blockId < blockTypes.Length && blockTypes[blockId].IsOpaque) opaqueCount++;
                        }
                    }
                }

                sectionData[s] = new SectionJobData
                {
                    IsEmpty = nonAirCount == 0,
                    IsFullySolid = opaqueCount == totalVoxels,
                };
            }
        }
    }
}
