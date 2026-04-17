using System;
using System.Collections.Generic;
using Data;
using Data.JobData;
using Data.WorldTypes;
using Helpers;
using Jobs;
using Jobs.BurstData;
using Jobs.Data;
using Jobs.Generators;
using Legacy;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Pool;

/// <summary>
/// Manages the lifecycle of all background jobs (generation, meshing, lighting).
/// Owns the active <see cref="IChunkGenerator"/> strategy and delegates scheduling to it.
/// </summary>
public class WorldJobManager : IDisposable
{
    private readonly World _world;
    private readonly IChunkGenerator _chunkGenerator;

    #region Job Tracking Dictionaries

    public Dictionary<ChunkCoord, GenerationJobData> GenerationJobs { get; } = new Dictionary<ChunkCoord, GenerationJobData>();
    public Dictionary<ChunkCoord, (JobHandle handle, MeshDataJobOutput meshData)> MeshJobs { get; } = new Dictionary<ChunkCoord, (JobHandle, MeshDataJobOutput)>();
    public Dictionary<ChunkCoord, LightingJobData> LightingJobs { get; } = new Dictionary<ChunkCoord, LightingJobData>();

    #endregion

    // --- Cached Collections for GC Optimization ---
    private readonly List<ChunkCoord> _completedGenJobs = new List<ChunkCoord>();
    private readonly List<ChunkCoord> _completedMeshJobs = new List<ChunkCoord>();
    private readonly List<ChunkCoord> _completedLightJobs = new List<ChunkCoord>();
    private readonly HashSet<ChunkCoord> _chunksToRebuildMesh = new HashSet<ChunkCoord>();
    private readonly Dictionary<ChunkCoord, HashSet<Vector2Int>> _droppedLightUpdates = new Dictionary<ChunkCoord, HashSet<Vector2Int>>();

    #region Constructor

    /// <summary>
    /// Initializes the WorldJobManager and resolves the correct IChunkGenerator strategy.
    /// </summary>
    /// <param name="world">The main World instance that owns this manager.</param>
    /// <param name="activeWorldType">The resolved WorldTypeDefinition for the current session.
    /// All unsupported type IDs (e.g. Amplified) must be remapped to a supported type
    /// in World.StartWorld() before this constructor is called.</param>
    /// <param name="globalJobData">World-type-agnostic NativeArrays (BlockTypes, CustomMeshes, etc.).</param>
    public WorldJobManager(World world, WorldTypeDefinition activeWorldType, JobDataManager globalJobData)
    {
        _world = world;

        // Strategy Factory.
        // NOTE: This is the SINGLE intentional exception to the "zero legacy references"
        // rule from the design document. The factory must create concrete generator instances,
        // which requires referencing the Legacy namespace. If the Assembly Definition
        // boundary is adopted later, this switch is replaced by a registration pattern
        // (GeneratorRegistry) that eliminates the direct reference.
        _chunkGenerator = activeWorldType.typeID switch
        {
            WorldTypeID.Legacy => new LegacyChunkGenerator(),
            WorldTypeID.Standard => new StandardChunkGenerator(),
            _ => throw new ArgumentException(
                $"[WorldJobManager] Unsupported WorldTypeID: {activeWorldType.typeID}. " +
                "Ensure all unimplemented types are remapped to a supported type before constructing WorldJobManager."),
        };

        _chunkGenerator.Initialize(VoxelData.Seed, activeWorldType, globalJobData);
    }

    #endregion

    #region Public API

    /// <summary>
    /// Synchronous main-thread voxel query, delegated to the active generator strategy.
    /// Replaces the former direct call to WorldGen.GetVoxel() in World.GetHighestVoxel().
    /// </summary>
    /// <param name="globalPos">The global voxel position to query.</param>
    /// <returns>The block ID at the given position.</returns>
    public byte GetVoxel(Vector3Int globalPos) => _chunkGenerator.GetVoxel(globalPos);

    /// <summary>
    /// Schedules a background job to generate voxel data for the given chunk coordinate.
    /// </summary>
    /// <param name="chunkCoord">The coordinate of the chunk to generate.</param>
    public void ScheduleGeneration(ChunkCoord chunkCoord)
    {
        if (GenerationJobs.ContainsKey(chunkCoord))
            return;

        Vector2Int chunkVoxelPos = chunkCoord.ToVoxelOrigin();

        if (_world.worldData.Chunks.TryGetValue(chunkVoxelPos, out ChunkData data) && data.IsPopulated)
            return;

        GenerationJobData jobData = _chunkGenerator.ScheduleGeneration(chunkCoord);
        GenerationJobs.Add(chunkCoord, jobData);
    }

    /// <summary>
    /// Schedules a generation job specifically for benchmarking purposes.
    /// Unlike <see cref="ScheduleGeneration"/>, this method does not check for existing jobs
    /// or populated chunk data, and does not add the result to the tracking dictionary.
    /// The caller is responsible for completing and disposing the returned data.
    /// </summary>
    /// <param name="chunkCoord">The coordinate of the chunk to generate.</param>
    /// <returns>The generation job data (handle + output containers).</returns>
    public GenerationJobData ScheduleBenchmarkGeneration(ChunkCoord chunkCoord)
    {
        return _chunkGenerator.ScheduleGeneration(chunkCoord);
    }

    #endregion

    /// <summary>
    /// Attempts to schedule a mesh generation job for the specified chunk.
    /// Checks dependencies (neighbor data existence and lighting stability) before scheduling.
    /// </summary>
    /// <param name="chunk">The chunk to generate a mesh for.</param>
    /// <returns>True if the job was scheduled or was already running; false if dependencies were not met (e.g., waiting for lighting).</returns>
    public bool ScheduleMeshing(Chunk chunk)
    {
        ChunkCoord chunkCoord = chunk.Coord;

        if (MeshJobs.ContainsKey(chunkCoord))
            return true;

        // Gate 1: Center chunk must have completed at least one lighting pass and have
        // no unscheduled light changes. We intentionally do NOT block on a running lighting
        // job (lightingJobs.ContainsKey) — the meshing job reads an independent snapshot of
        // the voxel data. If lighting is in-flight, the mesh uses valid data from the previous
        // pass and gets rebuilt when the lighting job completes and triggers RequestChunkMeshRebuild.
        // This prevents perpetual deadlocks from cross-chunk BFS ping-pong.
        if (chunk.ChunkData.HasLightChangesToProcess ||
            chunk.ChunkData.NeedsInitialLighting)
        {
            return false;
        }

        if (!_world.AreNeighborsMeshReady(chunkCoord))
        {
            return false;
        }

        // 1. Prepare Section Data for CENTER chunk
        int sectionCount = chunk.ChunkData.sections.Length;
        NativeArray<SectionJobData> sectionData = new NativeArray<SectionJobData>(sectionCount, Allocator.Persistent);

        for (int i = 0; i < sectionCount; i++)
        {
            ChunkSection s = chunk.ChunkData.sections[i];
            sectionData[i] = new SectionJobData
            {
                IsEmpty = s == null || s.IsEmpty,
                IsFullySolid = s != null && s.IsFullySolid,
            };
        }

        // 2. Allocate all input maps with Persistent
        NativeArray<uint> map = _world.worldData.GetChunkMapForJob(chunkCoord.ToVoxelOrigin(), Allocator.Persistent);

        NativeArray<uint> back = _world.worldData.GetChunkMapForJob(chunkCoord.Neighbor(0, -1).ToVoxelOrigin(), Allocator.Persistent);
        NativeArray<uint> front = _world.worldData.GetChunkMapForJob(chunkCoord.Neighbor(0, 1).ToVoxelOrigin(), Allocator.Persistent);
        NativeArray<uint> left = _world.worldData.GetChunkMapForJob(chunkCoord.Neighbor(-1, 0).ToVoxelOrigin(), Allocator.Persistent);
        NativeArray<uint> right = _world.worldData.GetChunkMapForJob(chunkCoord.Neighbor(1, 0).ToVoxelOrigin(), Allocator.Persistent);
        NativeArray<uint> frontRight = _world.worldData.GetChunkMapForJob(chunkCoord.Neighbor(1, 1).ToVoxelOrigin(), Allocator.Persistent);
        NativeArray<uint> backRight = _world.worldData.GetChunkMapForJob(chunkCoord.Neighbor(1, -1).ToVoxelOrigin(), Allocator.Persistent);
        NativeArray<uint> backLeft = _world.worldData.GetChunkMapForJob(chunkCoord.Neighbor(-1, -1).ToVoxelOrigin(), Allocator.Persistent);
        NativeArray<uint> frontLeft = _world.worldData.GetChunkMapForJob(chunkCoord.Neighbor(-1, 1).ToVoxelOrigin(), Allocator.Persistent);

        MeshDataJobOutput meshOutput = new MeshDataJobOutput(Allocator.Persistent);

        MeshGenerationJob job = new MeshGenerationJob
        {
            Map = map,
            SectionData = sectionData,
            BlockTypes = _world.JobDataManager.BlockTypesJobData,
            ChunkPosition = chunk.ChunkPosition,
            NeighborBack = back,
            NeighborFront = front,
            NeighborLeft = left,
            NeighborRight = right,
            NeighborFrontRight = frontRight,
            NeighborBackRight = backRight,
            NeighborBackLeft = backLeft,
            NeighborFrontLeft = frontLeft,
            CustomMeshes = _world.JobDataManager.CustomMeshesJobData,
            CustomFaces = _world.JobDataManager.CustomFacesJobData,
            CustomVerts = _world.JobDataManager.CustomVertsJobData,
            CustomTris = _world.JobDataManager.CustomTrisJobData,
            WaterVertexTemplates = _world.FluidVertexTemplates.WaterVertexTemplates,
            LavaVertexTemplates = _world.FluidVertexTemplates.LavaVertexTemplates,
            Output = meshOutput,
        };

        JobHandle meshJobHandle = job.Schedule();

        NativeArray<JobHandle> disposalHandles = new NativeArray<JobHandle>(10, Allocator.Persistent);
        disposalHandles[0] = map.Dispose(meshJobHandle);
        disposalHandles[1] = sectionData.Dispose(meshJobHandle);
        disposalHandles[2] = back.Dispose(meshJobHandle);
        disposalHandles[3] = front.Dispose(meshJobHandle);
        disposalHandles[4] = left.Dispose(meshJobHandle);
        disposalHandles[5] = right.Dispose(meshJobHandle);
        disposalHandles[6] = frontRight.Dispose(meshJobHandle);
        disposalHandles[7] = backRight.Dispose(meshJobHandle);
        disposalHandles[8] = backLeft.Dispose(meshJobHandle);
        disposalHandles[9] = frontLeft.Dispose(meshJobHandle);

        JobHandle combinedDisposalHandle = JobHandle.CombineDependencies(disposalHandles);
        JobHandle finalHandle = disposalHandles.Dispose(combinedDisposalHandle);

        MeshJobs.Add(chunkCoord, (finalHandle, meshOutput));

        return true;
    }

    /// <summary>
    /// Schedules a neighborhood lighting job to propagate sunlight and blocklight changes.
    /// </summary>
    /// <param name="chunkData">The central chunk data object.</param>
    /// <param name="allocator">The allocator to use for job data.</param>
    /// <returns>True if the job was successfully scheduled, false if it exited early or was already scheduled.</returns>
    public bool ScheduleLightingUpdate(ChunkData chunkData, Allocator allocator = Allocator.Persistent)
    {
        ChunkCoord chunkCoord = ChunkCoord.FromVoxelOrigin(chunkData.Position);
        if (LightingJobs.ContainsKey(chunkCoord)) return false;

        if (!_world.AreNeighborsDataReady(chunkCoord))
        {
            chunkData.HasLightChangesToProcess = true;
            return false;
        }

        LightingJobInputData inputData = new LightingJobInputData();

        inputData.Heightmap = new NativeArray<ushort>(chunkData.heightMap, allocator);
        inputData.NeighborN = _world.worldData.GetChunkMapForJob(chunkCoord.Neighbor(0, 1).ToVoxelOrigin(), allocator);
        inputData.NeighborE = _world.worldData.GetChunkMapForJob(chunkCoord.Neighbor(1, 0).ToVoxelOrigin(), allocator);
        inputData.NeighborS = _world.worldData.GetChunkMapForJob(chunkCoord.Neighbor(0, -1).ToVoxelOrigin(), allocator);
        inputData.NeighborW = _world.worldData.GetChunkMapForJob(chunkCoord.Neighbor(-1, 0).ToVoxelOrigin(), allocator);
        inputData.NeighborNE = _world.worldData.GetChunkMapForJob(chunkCoord.Neighbor(1, 1).ToVoxelOrigin(), allocator);
        inputData.NeighborSE = _world.worldData.GetChunkMapForJob(chunkCoord.Neighbor(1, -1).ToVoxelOrigin(), allocator);
        inputData.NeighborSW = _world.worldData.GetChunkMapForJob(chunkCoord.Neighbor(-1, -1).ToVoxelOrigin(), allocator);
        inputData.NeighborNW = _world.worldData.GetChunkMapForJob(chunkCoord.Neighbor(-1, 1).ToVoxelOrigin(), allocator);

        LightingJobData jobData = new LightingJobData
        {
            Input = inputData,
            Map = _world.worldData.GetChunkMapForJob(chunkData.Position, allocator),
            Mods = new NativeList<LightModification>(allocator),
            IsStable = new NativeArray<bool>(1, allocator),
            SunLightQueue = chunkData.GetSunlightQueueForJob(allocator),
            BlockLightQueue = chunkData.GetBlocklightQueueForJob(allocator),
            SunLightRecalcQueue = new NativeQueue<Vector2Int>(allocator),
        };

        if (_world.worldData.SunlightRecalculationQueue.TryGetValue(chunkData.Position, out HashSet<Vector2Int> columns))
        {
            foreach (Vector2Int col in columns)
            {
                jobData.SunLightRecalcQueue.Enqueue(new Vector2Int(col.x - chunkData.Position.x, col.y - chunkData.Position.y));
            }

            _world.worldData.SunlightRecalculationQueue.Remove(chunkData.Position);
            HashSetPool<Vector2Int>.Release(columns);
        }

        NeighborhoodLightingJob job = new NeighborhoodLightingJob
        {
            Map = jobData.Map,
            ChunkPosition = chunkData.Position,
            SunlightBfsQueue = jobData.SunLightQueue,
            BlocklightBfsQueue = jobData.BlockLightQueue,
            SunlightColumnRecalcQueue = jobData.SunLightRecalcQueue,
            Heightmap = jobData.Input.Heightmap,
            NeighborN = jobData.Input.NeighborN, NeighborE = jobData.Input.NeighborE,
            NeighborS = jobData.Input.NeighborS, NeighborW = jobData.Input.NeighborW,
            NeighborNE = jobData.Input.NeighborNE, NeighborSE = jobData.Input.NeighborSE,
            NeighborSW = jobData.Input.NeighborSW, NeighborNW = jobData.Input.NeighborNW,
            BlockTypes = _world.JobDataManager.BlockTypesJobData,
            CrossChunkLightMods = jobData.Mods,
            IsStable = jobData.IsStable,
            PerformEdgeCheck = chunkData.NeedsEdgeCheck,
        };

        jobData.Handle = job.Schedule();
        chunkData.HasLightChangesToProcess = false;
        if (chunkData.NeedsEdgeCheck) chunkData.NeedsEdgeCheck = false;
        LightingJobs.Add(chunkCoord, jobData);
        return true;
    }

    /// <summary>
    /// Helper overload for Chunk objects.
    /// </summary>
    /// <param name="chunk">The chunk to schedule lighting for.</param>
    /// <param name="allocator">The allocator to use for job data.</param>
    /// <returns>True if the job was successfully scheduled, false if it exited early or was already scheduled.</returns>
    public bool ScheduleLightingUpdate(Chunk chunk, Allocator allocator = Allocator.Persistent)
    {
        return ScheduleLightingUpdate(chunk.ChunkData, allocator);
    }

    /// <summary>
    /// Checks for completed generation jobs, populates chunk data, generates structures,
    /// and flags chunks for their initial lighting pass.
    /// </summary>
    public void ProcessGenerationJobs()
    {
        _completedGenJobs.Clear();
        foreach (KeyValuePair<ChunkCoord, GenerationJobData> jobEntry in GenerationJobs)
        {
            if (jobEntry.Value.Handle.IsCompleted)
            {
                jobEntry.Value.Handle.Complete();

                // --- STAGE 1: Populate with base terrain ---
                ChunkData chunkData = _world.worldData.RequestChunk(jobEntry.Key.ToVoxelOrigin(), true);
                chunkData.Populate(jobEntry.Value.Map, jobEntry.Value.HeightMap);
                chunkData.Chunk?.OnDataPopulated();

                // --- STAGE 2: Apply generated modifications (trees, etc.) ---
                while (jobEntry.Value.Mods.TryDequeue(out VoxelMod mod))
                {
                    // Delegate flora expansion to the active generator strategy.
                    // Each generator resolves the correct biome at the mod's position and uses
                    // its own noise/random strategy for trunk height determination.
                    IEnumerable<VoxelMod> floraMods = _chunkGenerator.ExpandFlora(mod);
                    _world.EnqueueVoxelModifications(floraMods);
                }

                // Check if any neighbors left pending mods for THIS chunk while it was unloaded.
                if (_world.ModManager.TryGetModsForChunk(jobEntry.Key, out List<VoxelMod> pendingMods))
                {
                    foreach (VoxelMod mod in pendingMods)
                    {
                        Vector3Int localVoxelPos = _world.worldData.GetLocalVoxelPositionInChunk(mod.GlobalPosition);
                        chunkData.ModifyVoxel(localVoxelPos, mod);
                    }
                }

                // Check for pending lighting updates
                if (_world.LightingStateManager.TryGetAndRemove(jobEntry.Key, out HashSet<Vector2Int> localLightCols))
                {
                    HashSet<Vector2Int> globalLightCols = HashSetPool<Vector2Int>.Get();
                    foreach (Vector2Int lCol in localLightCols)
                    {
                        globalLightCols.Add(new Vector2Int(lCol.x + chunkData.Position.x, lCol.y + chunkData.Position.y));
                    }

                    HashSetPool<Vector2Int>.Release(localLightCols);

                    if (_world.worldData.SunlightRecalculationQueue.TryGetValue(chunkData.Position, out HashSet<Vector2Int> existingQueue))
                    {
                        existingQueue.UnionWith(globalLightCols);
                        HashSetPool<Vector2Int>.Release(globalLightCols);
                    }
                    else
                    {
                        _world.worldData.SunlightRecalculationQueue[chunkData.Position] = globalLightCols;
                    }

                    chunkData.HasLightChangesToProcess = true;
                }

                // --- STAGE 3: Lighting ---
                if (_world.settings.enableLighting)
                {
                    chunkData.NeedsInitialLighting = true;
                }
                else
                {
                    for (int y = 0; y < VoxelData.ChunkHeight; y++)
                    {
                        for (int x = 0; x < VoxelData.ChunkWidth; x++)
                        {
                            for (int z = 0; z < VoxelData.ChunkWidth; z++)
                            {
                                uint packed = chunkData.GetVoxel(x, y, z);
                                packed = BurstVoxelDataBitMapping.SetSunLight(packed, 15);
                                chunkData.SetVoxel(x, y, z, packed);
                            }
                        }
                    }
                }

                jobEntry.Value.Dispose();
                _completedGenJobs.Add(jobEntry.Key);

                Chunk chunk = _world.GetChunkFromChunkCoord(jobEntry.Key);
                if (chunk != null && chunk.IsActive)
                {
                    _world.RequestChunkMeshRebuild(chunk);
                }
            }
        }

        foreach (ChunkCoord chunkCoord in _completedGenJobs)
        {
            GenerationJobs.Remove(chunkCoord);
        }
    }

    /// <summary>
    /// Checks for completed mesh generation jobs and applies the resulting mesh data.
    /// </summary>
    public void ProcessMeshJobs()
    {
        _completedMeshJobs.Clear();
        foreach (KeyValuePair<ChunkCoord, (JobHandle handle, MeshDataJobOutput meshData)> jobEntry in MeshJobs)
        {
            if (jobEntry.Value.handle.IsCompleted)
            {
                jobEntry.Value.handle.Complete();

                Chunk chunk = _world.GetChunkFromChunkCoord(jobEntry.Key);
                if (chunk != null)
                {
                    chunk.ApplyMeshData(jobEntry.Value.meshData);
                }
                else
                {
                    jobEntry.Value.meshData.Dispose();
                }

                _completedMeshJobs.Add(jobEntry.Key);
            }
        }

        foreach (ChunkCoord chunkCoord in _completedMeshJobs)
        {
            MeshJobs.Remove(chunkCoord);
        }
    }

    /// <summary>
    /// Checks for completed lighting jobs, applies light changes, and triggers mesh rebuilds.
    /// </summary>
    public void ProcessLightingJobs()
    {
        if (LightingJobs.Count == 0) return;

        _chunksToRebuildMesh.Clear();
        _completedLightJobs.Clear();

        foreach (HashSet<Vector2Int> set in _droppedLightUpdates.Values)
        {
            HashSetPool<Vector2Int>.Release(set);
        }

        _droppedLightUpdates.Clear();

        foreach (KeyValuePair<ChunkCoord, LightingJobData> jobEntry in LightingJobs)
        {
            if (jobEntry.Value.Handle.IsCompleted)
            {
                jobEntry.Value.Handle.Complete();
                LightingJobData jobData = jobEntry.Value;

                ChunkData chunkData = _world.worldData.RequestChunk(jobEntry.Key.ToVoxelOrigin(), false);

                if (chunkData != null)
                {
                    chunkData.IsAwaitingMainThreadProcess = true;
                }

                bool isChunkStable = jobData.IsStable[0];
                bool hasRealCrossChunkMods = false;
                if (chunkData != null && chunkData.IsPopulated)
                {
                    ApplyLightingJobResult(chunkData, jobData.Map);
                    foreach (LightModification mod in jobData.Mods)
                    {
                        Vector2Int neighborChunkVoxelPos = _world.worldData.GetChunkCoordFor(mod.GlobalPosition);
                        ChunkCoord neighborChunkCoord = ChunkCoord.FromVoxelOrigin(neighborChunkVoxelPos);

                        // Skip mods targeting chunks outside world boundaries entirely.
                        // These can never be consumed (the target chunk will never exist),
                        // and would cause perpetual IsStable=false rescheduling for boundary chunks.
                        if (!World.IsChunkInWorld(neighborChunkCoord))
                        {
                            continue;
                        }

                        ChunkData neighborChunk = _world.worldData.RequestChunk(neighborChunkVoxelPos, false);

                        if (neighborChunk == null || !neighborChunk.IsPopulated)
                        {
                            hasRealCrossChunkMods = true;

                            int localX = mod.GlobalPosition.x - neighborChunkVoxelPos.x;
                            int localZ = mod.GlobalPosition.z - neighborChunkVoxelPos.y;

                            if (localX < 0 || localX >= VoxelData.ChunkWidth ||
                                localZ < 0 || localZ >= VoxelData.ChunkWidth)
                            {
                                Debug.LogError($"[ProcessLightingJobs] Invalid local column calculation: ({localX.ToString()}, {localZ.ToString()}) for global pos {mod.GlobalPosition.ToString()}");
                                continue;
                            }

                            if (!_droppedLightUpdates.TryGetValue(neighborChunkCoord, out HashSet<Vector2Int> cols))
                            {
                                cols = HashSetPool<Vector2Int>.Get();
                                _droppedLightUpdates[neighborChunkCoord] = cols;
                            }

                            cols.Add(new Vector2Int(localX, localZ));
                            continue;
                        }

                        hasRealCrossChunkMods = true;

                        Vector3Int localVoxelPos = _world.worldData.GetLocalVoxelPositionInChunk(mod.GlobalPosition);

                        uint oldPackedData = neighborChunk.GetVoxel(localVoxelPos.x, localVoxelPos.y, localVoxelPos.z);
                        byte oldLightLevel;
                        uint newPackedData;

                        if (mod.Channel == LightChannel.Sun)
                        {
                            byte currentSunlight = BurstVoxelDataBitMapping.GetSunLight(oldPackedData);

                            // Guard: Cross-chunk BFS mods are computed against a STALE snapshot of
                            // the neighbor's data (taken before the neighbor's own lighting pass).
                            // This means a mod might try to set sunlight to a value LOWER than what
                            // the neighbor's own column recalculation has already computed.
                            //
                            // Rule: Non-zero cross-chunk sunlight mods may only INCREASE light.
                            // - Uplift mods (from PropagateLight): must be >= current to apply.
                            // - Darkness removal mods (level=0, from PropagateDarkness): always apply
                            //   so that block removal/placement propagates correctly across borders.
                            if (mod.LightLevel > 0 && mod.LightLevel < currentSunlight)
                            {
                                continue;
                            }

                            oldLightLevel = currentSunlight;
                            newPackedData = BurstVoxelDataBitMapping.SetSunLight(oldPackedData, mod.LightLevel);
                        }
                        else
                        {
                            oldLightLevel = BurstVoxelDataBitMapping.GetBlockLight(oldPackedData);
                            newPackedData = BurstVoxelDataBitMapping.SetBlockLight(oldPackedData, mod.LightLevel);
                        }

                        if (oldLightLevel != mod.LightLevel)
                        {
                            neighborChunk.SetVoxel(localVoxelPos.x, localVoxelPos.y, localVoxelPos.z, newPackedData);

                            if (mod.Channel == LightChannel.Sun)
                                neighborChunk.AddToSunLightQueue(localVoxelPos, oldLightLevel);
                            else
                                neighborChunk.AddToBlockLightQueue(localVoxelPos, oldLightLevel);
                        }
                    }
                }

                // Override stability: If the Burst job reported not-stable solely because
                // of cross-chunk mods targeting out-of-world positions (which can never be
                // consumed), treat the chunk as effectively stable. Without this, world-boundary
                // chunks would reschedule lighting indefinitely.
                if (!isChunkStable && !hasRealCrossChunkMods)
                {
                    isChunkStable = true;
                }

                if (isChunkStable)
                {
                    _chunksToRebuildMesh.Add(jobEntry.Key);
                    _world.RequestNeighborMeshRebuilds(jobEntry.Key);

                    // After a chunk's initial lighting stabilizes, schedule iterative
                    // edge-check rounds (self + neighbors). During initial world generation,
                    // chunks run their lighting with stale neighbor snapshots (neighbors are
                    // populated but not yet lit). Each edge-check round reconciles border
                    // lighting against the latest neighbor data.
                    //
                    // Multiple rounds are needed because two adjacent chunks that both
                    // stabilize with stale data from each other need iterative convergence:
                    // round 1 fixes the immediate frontier, round 2 reconciles any remaining
                    // discrepancies after neighbors have run their own edge checks.
                    if (chunkData != null && chunkData.RemainingEdgeCheckRounds > 0)
                    {
                        chunkData.RemainingEdgeCheckRounds--;

                        // Self-edge-check: re-examine this chunk's own borders with the
                        // latest neighbor snapshot data.
                        chunkData.NeedsEdgeCheck = true;
                        chunkData.HasLightChangesToProcess = true;

                        TriggerNeighborEdgeChecks(jobEntry.Key);
                    }
                }
                else
                {
                    if (chunkData != null) chunkData.HasLightChangesToProcess = true;
                }

                if (chunkData != null) chunkData.IsAwaitingMainThreadProcess = false;

                jobData.Dispose();
                _completedLightJobs.Add(jobEntry.Key);
            }
        }

        // Save vanishing neighbor updates (BATCH)
        foreach (KeyValuePair<ChunkCoord, HashSet<Vector2Int>> kvp in _droppedLightUpdates)
        {
            if (kvp.Value.Count > 0)
            {
                _world.LightingStateManager.AddPending(kvp.Key, kvp.Value);
            }
        }

        if (_droppedLightUpdates.Count > 0 && _world.settings.enableDiagnosticLogs)
        {
            int totalColumns = 0;
            foreach (HashSet<Vector2Int> set in _droppedLightUpdates.Values)
            {
                totalColumns += set.Count;
            }

            Debug.Log($"[LIGHTING] Processed {_completedLightJobs.Count.ToString()} jobs. Saved updates for {_droppedLightUpdates.Count.ToString()} unloaded chunks ({totalColumns.ToString()} columns)");
        }

        foreach (ChunkCoord chunkCoord in _chunksToRebuildMesh)
        {
            Chunk chunk = _world.GetChunkFromChunkCoord(chunkCoord);
            if (chunk != null)
            {
                _world.RequestChunkMeshRebuild(chunk, immediate: true);
            }
        }

        foreach (ChunkCoord chunkCoord in _completedLightJobs)
        {
            LightingJobs.Remove(chunkCoord);
        }
    }

    /// <summary>
    /// Merges the lighting results from a background job into the live ChunkData.
    /// CRITICAL: This performs a bit-mask merge (only light bits) to avoid overwriting
    /// block changes (TOCTOU) made by the player while the job was running.
    /// </summary>
    private void ApplyLightingJobResult(ChunkData chunkData, NativeArray<uint> jobMap)
    {
        int indexOffset = 0;
        const int sectionVolume = ChunkMath.SECTION_VOLUME;

        for (int s = 0; s < chunkData.sections.Length; s++)
        {
            ChunkSection section = chunkData.sections[s];
            bool sectionHasData = false;
            bool isNewSection = false;

            if (section == null)
            {
                bool needsSection = false;
                for (int i = 0; i < sectionVolume; i++)
                {
                    if (jobMap[indexOffset + i] != 0)
                    {
                        needsSection = true;
                        break;
                    }
                }

                if (needsSection)
                {
                    section = _world.ChunkPool.GetChunkSection();
                    chunkData.sections[s] = section;
                    isNewSection = true;
                }
            }

            if (section != null)
            {
                for (int i = 0; i < sectionVolume; i++)
                {
                    uint liveData = section.voxels[i];
                    uint jobVoxel = jobMap[indexOffset + i];

                    byte jobSunlight = BurstVoxelDataBitMapping.GetSunLight(jobVoxel);
                    byte jobBlocklight = BurstVoxelDataBitMapping.GetBlockLight(jobVoxel);

                    liveData = BurstVoxelDataBitMapping.SetSunLight(liveData, jobSunlight);
                    liveData = BurstVoxelDataBitMapping.SetBlockLight(liveData, jobBlocklight);

                    section.voxels[i] = liveData;

                    if (liveData != 0) sectionHasData = true;
                }

                if (!sectionHasData)
                {
                    _world.ChunkPool.ReturnChunkSection(section);
                    chunkData.sections[s] = null;
                }
                else if (!isNewSection)
                {
                    section.RecalculateCounts(_world.BlockTypes);
                }
            }

            indexOffset += sectionVolume;
        }
    }

    #region IDisposable

    /// <summary>
    /// Disposes all active jobs and the generator strategy.
    /// </summary>
    public void Dispose()
    {
        foreach (GenerationJobData job in GenerationJobs.Values)
        {
            job.Handle.Complete();
            job.Dispose();
        }

        foreach ((JobHandle handle, MeshDataJobOutput meshData) in MeshJobs.Values)
        {
            handle.Complete();
            meshData.Dispose();
        }

        foreach (LightingJobData job in LightingJobs.Values)
        {
            job.Handle.Complete();
            job.Dispose();
        }

        GenerationJobs.Clear();
        MeshJobs.Clear();
        LightingJobs.Clear();

        _chunkGenerator?.Dispose();
    }

    /// <summary>
    /// Triggers edge consistency checks on the 4 cardinal neighbors of the specified chunk.
    /// Called when a chunk's initial lighting stabilizes, so that neighbors can reconcile
    /// their border lighting against the now-correct data.
    /// </summary>
    /// <param name="sourceCoord">The chunk that just stabilized.</param>
    private void TriggerNeighborEdgeChecks(ChunkCoord sourceCoord)
    {
        for (int d = 0; d < 4; d++)
        {
            ChunkCoord neighborCoord = d switch
            {
                0 => sourceCoord.Neighbor(0, 1), // North
                1 => sourceCoord.Neighbor(1, 0), // East
                2 => sourceCoord.Neighbor(0, -1), // South
                _ => sourceCoord.Neighbor(-1, 0), // West
            };

            if (!World.IsChunkInWorld(neighborCoord)) continue;

            ChunkData neighborData = _world.worldData.RequestChunk(
                neighborCoord.ToVoxelOrigin(), false);

            // Only trigger on neighbors that are populated and have already finished
            // their initial lighting. Neighbors still awaiting initial lighting will
            // get their own edge check trigger when THEY stabilize.
            if (neighborData != null && neighborData.IsPopulated
                                     && !neighborData.NeedsInitialLighting)
            {
                neighborData.NeedsEdgeCheck = true;
                neighborData.HasLightChangesToProcess = true;
            }
        }
    }

    #endregion
}
