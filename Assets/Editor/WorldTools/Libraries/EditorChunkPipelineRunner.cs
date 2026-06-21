using System;
using System.Collections.Generic;
using Data;
using Data.Enums;
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
        /// When set, overrides the trunk worm enabled state from the <see cref="WorldTypeDefinition"/>.
        /// <c>true</c> = force trunk worms on, <c>false</c> = force trunk worms off, <c>null</c> = use asset value.
        /// </summary>
        public bool? TrunkWormOverride { get; set; }

        /// <summary>
        /// When true, the worm carver job emits per-worm telemetry data into
        /// <see cref="GenerationJobData.WormTelemetry"/>. Editor-only.
        /// </summary>
        public bool EnableTelemetry { get; set; }

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
            _generator.TrunkWormEnabledOverride = TrunkWormOverride;
            _generator.EnableTelemetry = EnableTelemetry;
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
            Dictionary<Vector2Int, NativeArray<ushort>> heightMaps,
            Dictionary<Vector2Int, NativeArray<ushort>> lightMaps)
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
                Neighbors = new NeighborMapSet
                {
                    NeighborN = new NativeArray<uint>(neighborN, Allocator.Persistent),
                    NeighborE = new NativeArray<uint>(neighborE, Allocator.Persistent),
                    NeighborS = new NativeArray<uint>(neighborS, Allocator.Persistent),
                    NeighborW = new NativeArray<uint>(neighborW, Allocator.Persistent),
                    NeighborNE = new NativeArray<uint>(neighborNE, Allocator.Persistent),
                    NeighborSE = new NativeArray<uint>(neighborSE, Allocator.Persistent),
                    NeighborSW = new NativeArray<uint>(neighborSW, Allocator.Persistent),
                    NeighborNW = new NativeArray<uint>(neighborNW, Allocator.Persistent),
                    LightN = GetOrCreateLightMap(lightMaps, chunkCoord.Neighbor(0, 1).ToVoxelOrigin()),
                    LightE = GetOrCreateLightMap(lightMaps, chunkCoord.Neighbor(1, 0).ToVoxelOrigin()),
                    LightS = GetOrCreateLightMap(lightMaps, chunkCoord.Neighbor(0, -1).ToVoxelOrigin()),
                    LightW = GetOrCreateLightMap(lightMaps, chunkCoord.Neighbor(-1, 0).ToVoxelOrigin()),
                    LightNE = GetOrCreateLightMap(lightMaps, chunkCoord.Neighbor(1, 1).ToVoxelOrigin()),
                    LightSE = GetOrCreateLightMap(lightMaps, chunkCoord.Neighbor(1, -1).ToVoxelOrigin()),
                    LightSW = GetOrCreateLightMap(lightMaps, chunkCoord.Neighbor(-1, -1).ToVoxelOrigin()),
                    LightNW = GetOrCreateLightMap(lightMaps, chunkCoord.Neighbor(-1, 1).ToVoxelOrigin()),
                },
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
                LightMap = GetOrCreateLightMap(lightMaps, voxelOrigin),
                Mods = new NativeList<LightModification>(Allocator.Persistent),
                IsStable = new NativeArray<bool>(1, Allocator.Persistent),
                SunLightQueue = new NativeQueue<LightQueueNode>(Allocator.Persistent),
                BlockLightQueue = new NativeQueue<LightQueueNode>(Allocator.Persistent),
                SunLightRecalcQueue = sunlightRecalcQueue,
            };

            // LI-1: gather the center + 8 neighbor maps into the halo-padded volumes the job consumes.
            // Missing neighbors are sentinel-filled inside GatherPadded* (uint/ushort MaxValue).
            NeighborMapSet nbrs = jobData.Input.Neighbors;
            jobData.PaddedVoxels = new NativeArray<uint>(ChunkMath.PADDED_LIGHTING_VOLUME, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            jobData.PaddedLight = new NativeArray<ushort>(ChunkMath.PADDED_LIGHTING_VOLUME, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            ChunkMath.GatherPaddedVoxels(jobData.PaddedVoxels, jobData.Map,
                nbrs.NeighborW, nbrs.NeighborE, nbrs.NeighborS, nbrs.NeighborN,
                nbrs.NeighborSW, nbrs.NeighborNW, nbrs.NeighborSE, nbrs.NeighborNE);
            ChunkMath.GatherPaddedLight(jobData.PaddedLight, jobData.LightMap,
                nbrs.LightW, nbrs.LightE, nbrs.LightS, nbrs.LightN,
                nbrs.LightSW, nbrs.LightNW, nbrs.LightSE, nbrs.LightNE);

            NeighborhoodLightingJob job = new NeighborhoodLightingJob
            {
                PaddedVoxels = jobData.PaddedVoxels,
                PaddedLight = jobData.PaddedLight,
                ChunkPosition = voxelOrigin,
                SunlightBfsQueue = jobData.SunLightQueue,
                BlocklightBfsQueue = jobData.BlockLightQueue,
                SunlightColumnRecalcQueue = jobData.SunLightRecalcQueue,
                Heightmap = jobData.Input.Heightmap,
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
        /// <param name="smoothLighting">Smooth lighting quality level for the mesh job.</param>
        /// <returns>A tuple of the combined job handle and mesh output, or null if neighbors are missing.</returns>
        public (JobHandle handle, MeshDataJobOutput output)? ScheduleMeshing(
            ChunkCoord chunkCoord,
            Vector2Int chunkVoxelPos,
            Dictionary<Vector2Int, NativeArray<uint>> maps,
            Dictionary<Vector2Int, NativeArray<ushort>> lightMaps,
            MeshClipBounds clipBounds,
            SmoothLightingQuality smoothLighting = SmoothLightingQuality.High)
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

            // Create light map snapshot copies
            NativeArray<ushort> lightMapCopy = GetOrCreateLightMap(lightMaps, chunkVoxelPos);
            NativeArray<ushort> lightBackCopy = GetOrCreateLightMap(lightMaps, chunkCoord.Neighbor(0, -1).ToVoxelOrigin());
            NativeArray<ushort> lightFrontCopy = GetOrCreateLightMap(lightMaps, chunkCoord.Neighbor(0, 1).ToVoxelOrigin());
            NativeArray<ushort> lightLeftCopy = GetOrCreateLightMap(lightMaps, chunkCoord.Neighbor(-1, 0).ToVoxelOrigin());
            NativeArray<ushort> lightRightCopy = GetOrCreateLightMap(lightMaps, chunkCoord.Neighbor(1, 0).ToVoxelOrigin());
            NativeArray<ushort> lightFrontRightCopy = GetOrCreateLightMap(lightMaps, chunkCoord.Neighbor(1, 1).ToVoxelOrigin());
            NativeArray<ushort> lightBackRightCopy = GetOrCreateLightMap(lightMaps, chunkCoord.Neighbor(1, -1).ToVoxelOrigin());
            NativeArray<ushort> lightBackLeftCopy = GetOrCreateLightMap(lightMaps, chunkCoord.Neighbor(-1, -1).ToVoxelOrigin());
            NativeArray<ushort> lightFrontLeftCopy = GetOrCreateLightMap(lightMaps, chunkCoord.Neighbor(-1, 1).ToVoxelOrigin());

            MeshDataJobOutput meshOutput = new MeshDataJobOutput(Allocator.Persistent);

            MeshGenerationJob job = new MeshGenerationJob
            {
                Map = mapCopy,
                LightMap = lightMapCopy,
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
                LightBack = lightBackCopy,
                LightFront = lightFrontCopy,
                LightLeft = lightLeftCopy,
                LightRight = lightRightCopy,
                LightFrontRight = lightFrontRightCopy,
                LightBackRight = lightBackRightCopy,
                LightBackLeft = lightBackLeftCopy,
                LightFrontLeft = lightFrontLeftCopy,
                CustomMeshes = _jobDataManager.CustomMeshesJobData,
                CustomFaces = _jobDataManager.CustomFacesJobData,
                CustomVerts = _jobDataManager.CustomVertsJobData,
                CustomTris = _jobDataManager.CustomTrisJobData,
                WaterVertexTemplates = _fluidTemplates.WaterVertexTemplates,
                LavaVertexTemplates = _fluidTemplates.LavaVertexTemplates,
                ClipBounds = clipBounds,
                SmoothLighting = smoothLighting,
                Output = meshOutput,
            };

            JobHandle meshJobHandle = job.Schedule();

            // Schedule disposal of snapshot copies after the job completes
            NativeArray<JobHandle> disposalHandles = new NativeArray<JobHandle>(19, Allocator.Persistent);
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
            disposalHandles[10] = lightMapCopy.Dispose(meshJobHandle);
            disposalHandles[11] = lightBackCopy.Dispose(meshJobHandle);
            disposalHandles[12] = lightFrontCopy.Dispose(meshJobHandle);
            disposalHandles[13] = lightLeftCopy.Dispose(meshJobHandle);
            disposalHandles[14] = lightRightCopy.Dispose(meshJobHandle);
            disposalHandles[15] = lightFrontRightCopy.Dispose(meshJobHandle);
            disposalHandles[16] = lightBackRightCopy.Dispose(meshJobHandle);
            disposalHandles[17] = lightBackLeftCopy.Dispose(meshJobHandle);
            disposalHandles[18] = lightFrontLeftCopy.Dispose(meshJobHandle);

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

        /// <summary>
        /// Returns a persistent copy of the light map for the given origin, or an empty (zeroed) array
        /// if none exists yet. Used during initial lighting passes when no prior light data is available.
        /// </summary>
        private static NativeArray<ushort> GetOrCreateLightMap(
            Dictionary<Vector2Int, NativeArray<ushort>> lightMaps, Vector2Int origin)
        {
            const int chunkVolume = VoxelData.ChunkWidth * VoxelData.ChunkHeight * VoxelData.ChunkWidth;
            if (lightMaps.TryGetValue(origin, out NativeArray<ushort> existing))
                return new NativeArray<ushort>(existing, Allocator.Persistent);
            return new NativeArray<ushort>(chunkVolume, Allocator.Persistent);
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
