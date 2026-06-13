using System;
using System.Collections.Generic;
using Data;
using Data.JobData;
using Data.WorldTypes;
using Helpers;
using Jobs;
using Jobs.Data;
using Jobs.Generators;
using Legacy;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
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
    public Dictionary<ChunkCoord, MeshingJobData> MeshJobs { get; } = new Dictionary<ChunkCoord, MeshingJobData>();
    public Dictionary<ChunkCoord, LightingJobData> LightingJobs { get; } = new Dictionary<ChunkCoord, LightingJobData>();

    /// <summary>
    /// True if any generation, meshing, or lighting jobs are currently active.
    /// </summary>
    public bool HasActiveJobs => GenerationJobs.Count > 0 || MeshJobs.Count > 0 || LightingJobs.Count > 0;

    #endregion

    // --- Native Buffer Pooling ---
    // Pools the fixed-size full-chunk job input buffers (voxel + light maps) shared by lighting
    // and meshing jobs, avoiding ~1.7 MB of Persistent alloc/dispose churn per job.
    private readonly ChunkJobArrayPool _jobArrayPool = new ChunkJobArrayPool();

    // --- Cached Collections for GC Optimization ---
    private readonly List<ChunkCoord> _completedGenJobs = new List<ChunkCoord>();
    private readonly List<ChunkCoord> _completedMeshJobs = new List<ChunkCoord>();
    private readonly List<ChunkCoord> _completedLightJobs = new List<ChunkCoord>();
    private readonly HashSet<ChunkCoord> _chunksToRebuildMesh = new HashSet<ChunkCoord>();
    private readonly Dictionary<ChunkCoord, HashSet<Vector2Int>> _droppedLightUpdates = new Dictionary<ChunkCoord, HashSet<Vector2Int>>();

    // Cross-chunk light mods whose target chunk had its own lighting job in flight at apply time.
    // Applying them immediately would be overwritten by that job's full-LightMap merge and the
    // surviving wake-up node would become a no-op — losing the mod permanently (Bug 08, path 2).
    // They are drained right after the target's merge instead. Persists across frames: the
    // target's job may complete on a later frame than the emitter's.
    private readonly Dictionary<ChunkCoord, List<LightModification>> _deferredCrossChunkMods = new Dictionary<ChunkCoord, List<LightModification>>();

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

        Settings settings = SettingsManager.LoadSettings();
        GenerationFeatureFlags flags = GenerationFeatureFlags.Default;
        flags.EnableCaves = settings.enableCaves;
        flags.EnableLodes = settings.enableLodes;
        flags.EnableWater = settings.enableWater;
        flags.EnableMajorFlora = settings.enableMajorFloraPass;
        flags.EnableMinorFlora = settings.enableMinorFloraPass;
        _chunkGenerator.FeatureFlags = flags;
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
    /// Returns terrain generation diagnostic data at the given column.
    /// Delegated to the active generator strategy.
    /// </summary>
    public TerrainDebugInfo GetTerrainDebugInfo(int globalX, int globalZ) => _chunkGenerator.GetTerrainDebugInfo(globalX, globalZ);

    /// <summary>
    /// Evaluates a batch of pixels for the terrain debug minimap.
    /// Delegated to the active generator strategy.
    /// </summary>
    public void EvaluateTerrainDebugPixels(int startIndex, int count, int textureSize,
        int originX, int originZ, int scale, TerrainDebugRenderMode mode,
        int biomeCount, int sliceY, byte[] outputPixels) =>
        _chunkGenerator.EvaluateTerrainDebugPixels(startIndex, count, textureSize,
            originX, originZ, scale, mode, biomeCount, sliceY, outputPixels);

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
        // When lighting is disabled, skip this gate entirely — no lighting job will ever run
        // to clear these flags, and the sunlight fill ensures all voxels are at max brightness.
        if (_world.settings.enableLighting &&
            (chunk.ChunkData.HasLightChangesToProcess ||
             chunk.ChunkData.NeedsInitialLighting))
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

        // 2. Rent all input maps from the pool and fill them (every element is written, so stale
        // pooled buffers are safe). If anything below throws, the catch releases every buffer
        // acquired so far (Return/Dispose skip uncreated entries), so the pool never leaks.
        MeshingJobData jobData = new MeshingJobData { SectionData = sectionData };
        try
        {
            jobData.Map = RentAndFillVoxelMap(chunkCoord.ToVoxelOrigin());
            jobData.LightMap = RentAndFillLightMap(chunkCoord.ToVoxelOrigin());
            jobData.Neighbors = AcquireNeighborMaps(chunkCoord, pooled: true, Allocator.Persistent);
            jobData.Output = new MeshDataJobOutput(Allocator.Persistent);

            MeshGenerationJob job = new MeshGenerationJob
            {
                Map = jobData.Map,
                SectionData = sectionData,
                BlockTypes = _world.JobDataManager.BlockTypesJobData,
                ChunkPosition = chunk.ChunkPosition,
                NeighborBack = jobData.Neighbors.NeighborS,
                NeighborFront = jobData.Neighbors.NeighborN,
                NeighborLeft = jobData.Neighbors.NeighborW,
                NeighborRight = jobData.Neighbors.NeighborE,
                NeighborFrontRight = jobData.Neighbors.NeighborNE,
                NeighborBackRight = jobData.Neighbors.NeighborSE,
                NeighborBackLeft = jobData.Neighbors.NeighborSW,
                NeighborFrontLeft = jobData.Neighbors.NeighborNW,
                LightMap = jobData.LightMap,
                LightBack = jobData.Neighbors.LightS,
                LightFront = jobData.Neighbors.LightN,
                LightLeft = jobData.Neighbors.LightW,
                LightRight = jobData.Neighbors.LightE,
                LightFrontRight = jobData.Neighbors.LightNE,
                LightBackRight = jobData.Neighbors.LightSE,
                LightBackLeft = jobData.Neighbors.LightSW,
                LightFrontLeft = jobData.Neighbors.LightNW,
                CustomMeshes = _world.JobDataManager.CustomMeshesJobData,
                CustomFaces = _world.JobDataManager.CustomFacesJobData,
                CustomVerts = _world.JobDataManager.CustomVertsJobData,
                CustomTris = _world.JobDataManager.CustomTrisJobData,
                WaterVertexTemplates = _world.FluidVertexTemplates.WaterVertexTemplates,
                LavaVertexTemplates = _world.FluidVertexTemplates.LavaVertexTemplates,
                SmoothLighting = _world.settings.smoothLighting,
                ClipBounds = MeshClipBounds.Disabled,
                Output = jobData.Output,
            };

            // POOLING: Input buffers are returned to _jobArrayPool in ProcessMeshJobs after
            // Handle.Complete() — never dispose or return them while the job may be running.
            jobData.Handle = job.Schedule();
            MeshJobs.Add(chunkCoord, jobData);
        }
        catch
        {
            // A scheduled-but-untracked job must finish before its buffers can be released
            // (Complete() on a default handle is a no-op).
            jobData.Handle.Complete();
            if (jobData.Output.Vertices.IsCreated) jobData.Output.Dispose();
            ReleaseMeshingJobInputs(jobData);
            throw;
        }

        return true;
    }

    /// <summary>
    /// Rents a full-chunk voxel map buffer from the pool and fills it with the given chunk's data.
    /// </summary>
    /// <param name="chunkVoxelPos">The voxel-space world origin of the chunk to snapshot.</param>
    /// <returns>A pooled buffer with every element written.</returns>
    private NativeArray<uint> RentAndFillVoxelMap(Vector2Int chunkVoxelPos)
    {
        NativeArray<uint> buffer = _jobArrayPool.RentVoxelMap();
        _world.worldData.FillChunkMapForJob(chunkVoxelPos, buffer);
        return buffer;
    }

    /// <summary>
    /// Rents a full-chunk light map buffer from the pool and fills it with the given chunk's data.
    /// </summary>
    /// <param name="chunkVoxelPos">The voxel-space world origin of the chunk to snapshot.</param>
    /// <returns>A pooled buffer with every element written.</returns>
    private NativeArray<ushort> RentAndFillLightMap(Vector2Int chunkVoxelPos)
    {
        NativeArray<ushort> buffer = _jobArrayPool.RentLightMap();
        _world.worldData.FillChunkLightMapForJob(chunkVoxelPos, buffer);
        return buffer;
    }

    /// <summary>
    /// Acquires a filled full-chunk voxel map: pooled when <paramref name="pooled"/> is true,
    /// otherwise a fresh allocation with the given allocator (startup/TempJob path).
    /// </summary>
    /// <param name="chunkVoxelPos">The voxel-space world origin of the chunk to snapshot.</param>
    /// <param name="pooled">Whether to rent from the pool instead of allocating.</param>
    /// <param name="allocator">The allocator for the non-pooled path.</param>
    /// <returns>A buffer with every element written.</returns>
    private NativeArray<uint> AcquireVoxelMap(Vector2Int chunkVoxelPos, bool pooled, Allocator allocator)
    {
        return pooled
            ? RentAndFillVoxelMap(chunkVoxelPos)
            : _world.worldData.GetChunkMapForJob(chunkVoxelPos, allocator);
    }

    /// <summary>
    /// Acquires a filled full-chunk light map: pooled when <paramref name="pooled"/> is true,
    /// otherwise a fresh allocation with the given allocator (startup/TempJob path).
    /// </summary>
    /// <param name="chunkVoxelPos">The voxel-space world origin of the chunk to snapshot.</param>
    /// <param name="pooled">Whether to rent from the pool instead of allocating.</param>
    /// <param name="allocator">The allocator for the non-pooled path.</param>
    /// <returns>A buffer with every element written.</returns>
    private NativeArray<ushort> AcquireLightMap(Vector2Int chunkVoxelPos, bool pooled, Allocator allocator)
    {
        return pooled
            ? RentAndFillLightMap(chunkVoxelPos)
            : _world.worldData.GetChunkLightMapForJob(chunkVoxelPos, allocator);
    }

    /// <summary>
    /// Acquires the filled neighbor map set (8 voxel + 8 light maps) for the given center chunk:
    /// pooled when <paramref name="pooled"/> is true, otherwise fresh allocations with the given
    /// allocator (startup/TempJob path). This is the single authoritative fill site for
    /// <see cref="NeighborMapSet"/> — its compass directions must match the offsets used here.
    /// </summary>
    /// <param name="center">The chunk whose neighborhood is snapshotted.</param>
    /// <param name="pooled">Whether to rent from the pool instead of allocating.</param>
    /// <param name="allocator">The allocator for the non-pooled path.</param>
    /// <returns>A neighbor map set with every buffer filled.</returns>
    private NeighborMapSet AcquireNeighborMaps(ChunkCoord center, bool pooled, Allocator allocator)
    {
        return new NeighborMapSet
        {
            NeighborN = AcquireVoxelMap(center.Neighbor(0, 1).ToVoxelOrigin(), pooled, allocator),
            NeighborE = AcquireVoxelMap(center.Neighbor(1, 0).ToVoxelOrigin(), pooled, allocator),
            NeighborS = AcquireVoxelMap(center.Neighbor(0, -1).ToVoxelOrigin(), pooled, allocator),
            NeighborW = AcquireVoxelMap(center.Neighbor(-1, 0).ToVoxelOrigin(), pooled, allocator),
            NeighborNE = AcquireVoxelMap(center.Neighbor(1, 1).ToVoxelOrigin(), pooled, allocator),
            NeighborSE = AcquireVoxelMap(center.Neighbor(1, -1).ToVoxelOrigin(), pooled, allocator),
            NeighborSW = AcquireVoxelMap(center.Neighbor(-1, -1).ToVoxelOrigin(), pooled, allocator),
            NeighborNW = AcquireVoxelMap(center.Neighbor(-1, 1).ToVoxelOrigin(), pooled, allocator),
            LightN = AcquireLightMap(center.Neighbor(0, 1).ToVoxelOrigin(), pooled, allocator),
            LightE = AcquireLightMap(center.Neighbor(1, 0).ToVoxelOrigin(), pooled, allocator),
            LightS = AcquireLightMap(center.Neighbor(0, -1).ToVoxelOrigin(), pooled, allocator),
            LightW = AcquireLightMap(center.Neighbor(-1, 0).ToVoxelOrigin(), pooled, allocator),
            LightNE = AcquireLightMap(center.Neighbor(1, 1).ToVoxelOrigin(), pooled, allocator),
            LightSE = AcquireLightMap(center.Neighbor(1, -1).ToVoxelOrigin(), pooled, allocator),
            LightSW = AcquireLightMap(center.Neighbor(-1, -1).ToVoxelOrigin(), pooled, allocator),
            LightNW = AcquireLightMap(center.Neighbor(-1, 1).ToVoxelOrigin(), pooled, allocator),
        };
    }

    /// <summary>
    /// Schedules a neighborhood lighting job to propagate sunlight and blocklight changes.
    /// </summary>
    /// <param name="chunkData">The central chunk data object.</param>
    /// <param name="allocator">The allocator for the small per-job containers (heightmap, queues,
    /// mods, stability flag). The full-volume voxel/light maps are always pooled Persistent buffers.</param>
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

        // POOLING: Only Persistent callers (steady-state gameplay) use pooled buffers. The startup
        // coroutine passes TempJob and schedules lighting for the whole load area in one sweep —
        // far past the pool's retention cap — so it keeps allocate-per-job, which TempJob's
        // linear allocator handles better.
        bool usePooledBuffers = allocator == Allocator.Persistent;

        // If anything below throws, the catch releases every buffer acquired so far
        // (Return/Dispose skip uncreated entries), so the pool never leaks.
        LightingJobData jobData = new LightingJobData { UsesPooledBuffers = usePooledBuffers };
        try
        {
            jobData.Input = new LightingJobInputData
            {
                Heightmap = new NativeArray<ushort>(chunkData.heightMap, allocator),
                Neighbors = AcquireNeighborMaps(chunkCoord, usePooledBuffers, allocator),
            };
            jobData.Map = AcquireVoxelMap(chunkData.Position, usePooledBuffers, allocator);
            jobData.LightMap = AcquireLightMap(chunkData.Position, usePooledBuffers, allocator);
            jobData.Mods = new NativeList<LightModification>(allocator);
            jobData.IsStable = new NativeArray<bool>(1, allocator);
            jobData.SunLightQueue = chunkData.GetSunlightQueueForJob(allocator);
            jobData.BlockLightQueue = chunkData.GetBlocklightQueueForJob(allocator);
            jobData.SunLightRecalcQueue = new NativeQueue<Vector2Int>(allocator);

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
                LightMap = jobData.LightMap,
                ChunkPosition = chunkData.Position,
                SunlightBfsQueue = jobData.SunLightQueue,
                BlocklightBfsQueue = jobData.BlockLightQueue,
                SunlightColumnRecalcQueue = jobData.SunLightRecalcQueue,
                Heightmap = jobData.Input.Heightmap,
                NeighborN = jobData.Input.Neighbors.NeighborN, NeighborE = jobData.Input.Neighbors.NeighborE,
                NeighborS = jobData.Input.Neighbors.NeighborS, NeighborW = jobData.Input.Neighbors.NeighborW,
                NeighborNE = jobData.Input.Neighbors.NeighborNE, NeighborSE = jobData.Input.Neighbors.NeighborSE,
                NeighborSW = jobData.Input.Neighbors.NeighborSW, NeighborNW = jobData.Input.Neighbors.NeighborNW,
                LightN = jobData.Input.Neighbors.LightN, LightE = jobData.Input.Neighbors.LightE,
                LightS = jobData.Input.Neighbors.LightS, LightW = jobData.Input.Neighbors.LightW,
                LightNE = jobData.Input.Neighbors.LightNE, LightSE = jobData.Input.Neighbors.LightSE,
                LightSW = jobData.Input.Neighbors.LightSW, LightNW = jobData.Input.Neighbors.LightNW,
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
        catch
        {
            // A scheduled-but-untracked job must finish before its buffers can be released
            // (Complete() on a default handle is a no-op).
            jobData.Handle.Complete();
            ReleaseLightingJobData(jobData);
            throw;
        }
    }

    /// <summary>
    /// Helper overload for Chunk objects.
    /// </summary>
    /// <param name="chunk">The chunk to schedule lighting for.</param>
    /// <param name="allocator">The allocator for the small per-job containers (the full-volume
    /// maps are always pooled Persistent buffers).</param>
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
        int modsBudget = _world.settings.maxStructureModsPerFrame;

        foreach (KeyValuePair<ChunkCoord, GenerationJobData> jobEntry in GenerationJobs)
        {
            if (jobEntry.Value.Handle.IsCompleted)
            {
                jobEntry.Value.Handle.Complete();

                ChunkData chunkData = _world.worldData.RequestChunk(jobEntry.Key.ToVoxelOrigin(), true);

                // --- STAGE 1: Populate with base terrain (Once per chunk) ---
                if (!chunkData.IsPopulated)
                {
                    chunkData.Populate(jobEntry.Value.Map, jobEntry.Value.HeightMap);
                    chunkData.Chunk?.OnDataPopulated();
                }

                bool jobFullyProcessed = true;

                // --- STAGE 2: Apply generated modifications (trees, etc.) ---

                // 2A: Process old Legacy Mods queue (legacy generators still emit VoxelMod for roots)
                while (jobEntry.Value.Mods.TryDequeue(out VoxelMod mod))
                {
                    StructureSpawnMarker marker = new StructureSpawnMarker
                    {
                        Position = new int3(mod.GlobalPosition.x, mod.GlobalPosition.y, mod.GlobalPosition.z),
                        PoolEntryIndex = mod.ID,
                    };
                    IEnumerable<VoxelMod> floraMods = _chunkGenerator.ExpandStructure(marker);

                    foreach (VoxelMod fm in floraMods)
                    {
                        _world.EnqueueVoxelModification(fm);
                        modsBudget--;
                    }

                    if (modsBudget <= 0)
                    {
                        jobFullyProcessed = false;
                        break;
                    }
                }

                if (!jobFullyProcessed) continue;

                // 2B: Process Data-Driven StructureSpawns queue
                if (jobEntry.Value.StructureSpawns.IsCreated)
                {
                    while (jobEntry.Value.StructureSpawns.TryDequeue(out StructureSpawnMarker marker))
                    {
                        IEnumerable<VoxelMod> structureMods = _chunkGenerator.ExpandStructure(marker);

                        foreach (VoxelMod sm in structureMods)
                        {
                            _world.EnqueueVoxelModification(sm);
                            modsBudget--;
                        }

                        if (modsBudget <= 0)
                        {
                            jobFullyProcessed = false;
                            break;
                        }
                    }
                }

                if (!jobFullyProcessed) continue;

                // Check if any neighbors left pending mods for THIS chunk while it was unloaded.
                if (_world.ModManager.TryGetModsForChunk(jobEntry.Key, out List<VoxelMod> pendingMods))
                {
                    foreach (VoxelMod mod in pendingMods)
                    {
                        Vector3Int localVoxelPos = _world.worldData.GetLocalVoxelPositionInChunk(mod.GlobalPosition);
                        chunkData.ModifyVoxel(localVoxelPos, mod);
                    }
                }

                // Check for pending lighting updates — only relevant when the lighting engine is active.
                // When lighting is disabled, these entries would be orphaned (no BFS job to consume them)
                // and HasLightChangesToProcess would be set without any job to clear it.
                if (_world.settings.enableLighting &&
                    _world.LightingStateManager.TryGetAndRemove(jobEntry.Key, out HashSet<Vector2Int> localLightCols))
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

                // Freshly generated chunks recompute all light from current neighbor data during
                // initial lighting and the edge-check rounds, so pending blocklight mods recorded
                // while this chunk was absent are obsolete — discard them so a later save/load
                // cycle cannot replay them on top of unrelated light data.
                _world.LightingStateManager.DiscardPendingBlocklight(jobEntry.Key);

                // --- STAGE 3: Lighting ---
                if (_world.settings.enableLighting)
                {
                    chunkData.NeedsInitialLighting = true;
                }
                else
                {
                    // Lighting disabled: stamp sky=15 on every section's LightData
                    foreach (ChunkSection section in chunkData.sections)
                    {
                        if (section == null) continue;
                        LightingHelper.FillUniformSkyLight(section.LightData, 15);
                    }
                }

                jobEntry.Value.Dispose();
                _completedGenJobs.Add(jobEntry.Key);

                Chunk chunk = _world.GetChunkFromChunkCoord(jobEntry.Key);
                if (chunk != null && chunk.IsActive)
                {
                    _world.RequestChunkMeshRebuild(chunk);
                }

                // If we ran out of budget during processing the rest of this chunk's fast STAGE 3 steps,
                // break to respect frame time, letting the remaining completely finished jobs process next frame.
                if (modsBudget <= 0) break;
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
        foreach (KeyValuePair<ChunkCoord, MeshingJobData> jobEntry in MeshJobs)
        {
            if (jobEntry.Value.Handle.IsCompleted)
            {
                jobEntry.Value.Handle.Complete();

                Chunk chunk = _world.GetChunkFromChunkCoord(jobEntry.Key);
                if (chunk != null)
                {
                    chunk.ApplyMeshData(jobEntry.Value.Output);
                }
                else
                {
                    jobEntry.Value.Output.Dispose();
                }

                // POOLING: Return the input buffers for reuse.
                ReleaseMeshingJobInputs(jobEntry.Value);

                _completedMeshJobs.Add(jobEntry.Key);
            }
        }

        foreach (ChunkCoord chunkCoord in _completedMeshJobs)
        {
            MeshJobs.Remove(chunkCoord);
        }
    }

    /// <summary>
    /// Returns a completed meshing job's pooled input buffers to <see cref="_jobArrayPool"/> and
    /// disposes its per-job section data. Must only be called after <c>Handle.Complete()</c>.
    /// Does NOT touch <see cref="MeshingJobData.Output"/> — ownership of the output transfers to
    /// <c>Chunk.ApplyMeshData</c> (or is disposed by the caller when no chunk exists).
    /// </summary>
    /// <param name="jobData">The completed meshing job data.</param>
    private void ReleaseMeshingJobInputs(in MeshingJobData jobData)
    {
        _jobArrayPool.Return(jobData.Map);
        _jobArrayPool.Return(jobData.LightMap);
        _jobArrayPool.Return(in jobData.Neighbors);

        if (jobData.SectionData.IsCreated) jobData.SectionData.Dispose();
    }

    /// <summary>
    /// Returns a completed lighting job's pooled full-volume buffers to <see cref="_jobArrayPool"/>
    /// and disposes its per-job containers (heightmap, queues, mods, stability flag).
    /// Non-pooled jobs are fully disposed instead. Must only be called after <c>Handle.Complete()</c>.
    /// </summary>
    /// <param name="jobData">The completed lighting job data.</param>
    private void ReleaseLightingJobData(in LightingJobData jobData)
    {
        // Non-pooled jobs (startup coroutine's TempJob path) own all their buffers — dispose them.
        if (!jobData.UsesPooledBuffers)
        {
            jobData.Dispose();
            return;
        }

        // Pooled full-volume buffers
        _jobArrayPool.Return(jobData.Map);
        _jobArrayPool.Return(jobData.LightMap);
        _jobArrayPool.Return(in jobData.Input.Neighbors);

        // Per-job containers
        if (jobData.Input.Heightmap.IsCreated) jobData.Input.Heightmap.Dispose();
        if (jobData.SunLightQueue.IsCreated) jobData.SunLightQueue.Dispose();
        if (jobData.BlockLightQueue.IsCreated) jobData.BlockLightQueue.Dispose();
        if (jobData.SunLightRecalcQueue.IsCreated) jobData.SunLightRecalcQueue.Dispose();
        if (jobData.Mods.IsCreated) jobData.Mods.Dispose();
        if (jobData.IsStable.IsCreated) jobData.IsStable.Dispose();
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
                    ApplyLightingJobResult(chunkData, jobData);

                    // Apply mods other chunks' jobs deferred for THIS chunk while its job was in
                    // flight — now that the merge is done they can no longer be overwritten
                    // (Bug 08, path 2). Their wake-up nodes flag the chunk for another lighting pass.
                    DrainDeferredCrossChunkMods(jobEntry.Key, chunkData);

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
                            PersistUndeliverableLightMod(neighborChunkCoord, in mod);
                            continue;
                        }

                        hasRealCrossChunkMods = true;

                        // The target chunk has its own lighting job in flight, snapshotted before
                        // this mod existed. Applying now would be overwritten by that job's
                        // full-LightMap merge (Bug 08, path 2) — defer; drained right after the
                        // target's own merge. A target already processed this pass
                        // (_completedLightJobs) has merged and is safe to apply to directly.
                        if (LightingJobs.ContainsKey(neighborChunkCoord) && !_completedLightJobs.Contains(neighborChunkCoord))
                        {
                            if (!_deferredCrossChunkMods.TryGetValue(neighborChunkCoord, out List<LightModification> deferredList))
                            {
                                deferredList = ListPool<LightModification>.Get();
                                _deferredCrossChunkMods[neighborChunkCoord] = deferredList;
                            }

                            deferredList.Add(mod);
                            continue;
                        }

                        ApplyCrossChunkLightMod(neighborChunk, in mod);
                    }
                }
                else
                {
                    // The job result is discarded (the chunk vanished or lost its data mid-flight),
                    // so mods other chunks deferred for it can never be drained — degrade them to
                    // the persisted pending stores instead.
                    DegradeDeferredCrossChunkMods(jobEntry.Key);
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

                // POOLING: Return the full-volume buffers for reuse; dispose per-job containers.
                ReleaseLightingJobData(jobData);
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
    /// Applies one cross-chunk light modification to a live, populated chunk: evaluates the shared
    /// decision logic, writes the new packed light value, and enqueues the BFS wake-up node (which
    /// also flags the chunk for its next lighting pass via <c>HasLightChangesToProcess</c>).
    /// The decision rules (stale-snapshot guards, wake-up node old values) live in
    /// <see cref="CrossChunkLightModApplier"/> so the editor lighting validation suite exercises
    /// the exact same rules as this production path.
    /// </summary>
    /// <param name="targetChunk">The populated chunk the modification targets.</param>
    /// <param name="mod">The cross-chunk modification emitted by a neighbor's lighting job.</param>
    private void ApplyCrossChunkLightMod(ChunkData targetChunk, in LightModification mod)
    {
        Vector3Int localVoxelPos = _world.worldData.GetLocalVoxelPositionInChunk(mod.GlobalPosition);

        ushort currentLight = targetChunk.GetLightData(localVoxelPos.x, localVoxelPos.y, localVoxelPos.z);
        CrossChunkLightModApplier.ApplyDecision decision = CrossChunkLightModApplier.Compute(currentLight, in mod);

        if (!decision.ShouldApply)
        {
            return;
        }

        targetChunk.SetLightData(localVoxelPos.x, localVoxelPos.y, localVoxelPos.z, decision.NewLight);

        if (mod.Channel == LightChannel.Sun)
        {
            targetChunk.AddToSunLightQueue(localVoxelPos, decision.OldLevel);
        }
        else
        {
            targetChunk.AddToBlockLightQueue(localVoxelPos, decision.OldLevel, decision.OldR, decision.OldG, decision.OldB);
        }
    }

    /// <summary>
    /// Applies the cross-chunk mods that were deferred for a chunk while its lighting job was in
    /// flight. Called immediately after the chunk's job result merge, so the applied values can no
    /// longer be overwritten by a stale LightMap (the Bug 08 path-2 fix).
    /// </summary>
    /// <param name="chunkCoord">The chunk whose job result was just merged.</param>
    /// <param name="chunkData">The chunk's live data.</param>
    private void DrainDeferredCrossChunkMods(ChunkCoord chunkCoord, ChunkData chunkData)
    {
        if (!_deferredCrossChunkMods.Remove(chunkCoord, out List<LightModification> deferred)) return;

        foreach (LightModification mod in deferred)
        {
            ApplyCrossChunkLightMod(chunkData, in mod);
        }

        ListPool<LightModification>.Release(deferred);
    }

    /// <summary>
    /// Degrades deferred cross-chunk mods whose target chunk vanished (unloaded or lost its data)
    /// before its in-flight job result could be merged: sunlight mods fall back to persisted column
    /// recalculations, blocklight mods to the persisted pending-blocklight store — the same
    /// degradation paths used for mods that target unloaded chunks directly.
    /// </summary>
    /// <param name="chunkCoord">The chunk whose deferred mods can no longer be drained.</param>
    private void DegradeDeferredCrossChunkMods(ChunkCoord chunkCoord)
    {
        if (!_deferredCrossChunkMods.Remove(chunkCoord, out List<LightModification> deferred)) return;

        foreach (LightModification mod in deferred)
        {
            PersistUndeliverableLightMod(chunkCoord, in mod);
        }

        ListPool<LightModification>.Release(deferred);
    }

    /// <summary>
    /// Persists a single cross-chunk light modification that cannot be applied to a live chunk
    /// (target unloaded or vanished mid-flight). Sun mods are saved as column recalculation
    /// entries; blocklight mods are saved as pending RGB modifications for replay on load.
    /// </summary>
    private void PersistUndeliverableLightMod(ChunkCoord targetChunkCoord, in LightModification mod)
    {
        Vector2Int chunkVoxelPos = targetChunkCoord.ToVoxelOrigin();
        int localX = mod.GlobalPosition.x - chunkVoxelPos.x;
        int localZ = mod.GlobalPosition.z - chunkVoxelPos.y;

        if (localX < 0 || localX >= VoxelData.ChunkWidth ||
            localZ < 0 || localZ >= VoxelData.ChunkWidth)
        {
            Debug.LogError($"[PersistUndeliverableLightMod] Invalid local column calculation: ({localX.ToString()}, {localZ.ToString()}) for global pos {mod.GlobalPosition.ToString()}");
            return;
        }

        if (mod.Channel == LightChannel.Sun)
        {
            if (!_droppedLightUpdates.TryGetValue(targetChunkCoord, out HashSet<Vector2Int> cols))
            {
                cols = HashSetPool<Vector2Int>.Get();
                _droppedLightUpdates[targetChunkCoord] = cols;
            }

            cols.Add(new Vector2Int(localX, localZ));
        }
        else
        {
            // A sunlight column recalc cannot restore RGB data — persist the actual blocklight
            // modification for replay when the chunk is loaded from disk (Bug 08, path 1).
            _world.LightingStateManager.AddPendingBlocklight(targetChunkCoord,
                new Vector3Int(localX, mod.GlobalPosition.y, localZ),
                mod.BlockR, mod.BlockG, mod.BlockB, mod.IsRemoval);
        }
    }

    /// <summary>
    /// Merges the lighting results from a background job into the live ChunkData.
    /// CRITICAL: This performs a bit-mask merge (only light bits) to avoid overwriting
    /// block changes (TOCTOU) made by the player while the job was running.
    /// </summary>
    private void ApplyLightingJobResult(ChunkData chunkData, LightingJobData jobData)
    {
        NativeArray<uint> jobMap = jobData.Map;
        NativeArray<ushort> jobLightMap = jobData.LightMap;
        int indexOffset = 0;
        const int sectionVolume = ChunkMath.SECTION_VOLUME;

        for (int s = 0; s < chunkData.sections.Length; s++)
        {
            ChunkSection section = chunkData.sections[s];
            bool sectionHasData = false;
            bool isNewSection = false;

            // Clear any stale compact flag — the lighting job will provide fresh data.
            chunkData.SectionUniformSkyLevel[s] = ChunkData.UNIFORM_SKY_NONE;

            if (section == null)
            {
                bool needsSection = false;
                for (int i = 0; i < sectionVolume; i++)
                {
                    if (jobMap[indexOffset + i] != 0 || jobLightMap[indexOffset + i] != 0)
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
                bool sectionHasLight = false;

                for (int i = 0; i < sectionVolume; i++)
                {
                    // Overwrite ushort light array with the job's computed values.
                    // This overwrite is safe against cross-chunk mods: mods targeting a chunk
                    // with an in-flight job are deferred and drained right after this merge
                    // (see DrainDeferredCrossChunkMods — the Bug 08 path-2 fix), so live data
                    // written during the flight is never silently reverted.
                    ushort lightVal = jobLightMap[indexOffset + i];
                    section.LightData[i] = lightVal;

                    if (section.voxels[i] != 0) sectionHasData = true;
                    if (lightVal != 0) sectionHasLight = true;
                }

                if (!sectionHasData && !sectionHasLight)
                {
                    // No blocks, no light — discard entirely.
                    _world.ChunkPool.ReturnChunkSection(section);
                    chunkData.sections[s] = null;
                }
                else
                {
                    // Try to compact the light data into a uniform sky byte.
                    TryCompactSectionLight(chunkData, s, section, sectionHasData, sectionHasLight);

                    if (!isNewSection && chunkData.sections[s] != null)
                        section.RecalculateCounts(_world.BlockTypes);
                }
            }

            indexOffset += sectionVolume;
        }
    }

    /// <summary>
    /// Attempts to compact a section's light data into a single uniform sky level byte.
    /// If successful, stores the byte in <see cref="ChunkData.SectionUniformSkyLevel"/> and,
    /// for light-only sections (no blocks), returns the section to the pool.
    /// </summary>
    private void TryCompactSectionLight(ChunkData chunkData, int sectionIndex,
        ChunkSection section, bool hasBlocks, bool hasLight)
    {
        if (!hasLight)
        {
            // Pitch black — uniform sky level 0.
            chunkData.SectionUniformSkyLevel[sectionIndex] = 0;
            if (!hasBlocks)
            {
                _world.ChunkPool.ReturnChunkSection(section);
                chunkData.sections[sectionIndex] = null;
            }

            return;
        }

        LightingHelper.ClassifyLightData(section.LightData,
            out bool hasBlocklight, out _, out bool isUniformSky, out byte uniformSkyLevel);

        if (hasBlocklight || !isUniformSky) return;

        // Uniform sky, no blocklight — compact it.
        chunkData.SectionUniformSkyLevel[sectionIndex] = uniformSkyLevel;

        if (!hasBlocks)
        {
            _world.ChunkPool.ReturnChunkSection(section);
            chunkData.sections[sectionIndex] = null;
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

        foreach (MeshingJobData job in MeshJobs.Values)
        {
            job.Handle.Complete();
            job.Output.Dispose();
            ReleaseMeshingJobInputs(job);
        }

        foreach (LightingJobData job in LightingJobs.Values)
        {
            job.Handle.Complete();
            ReleaseLightingJobData(job);
        }

        GenerationJobs.Clear();
        MeshJobs.Clear();
        LightingJobs.Clear();

        // In-flight lighting results are discarded wholesale at shutdown, so mods deferred for
        // those jobs are dropped with them — release the pooled lists.
        foreach (List<LightModification> deferred in _deferredCrossChunkMods.Values)
        {
            ListPool<LightModification>.Release(deferred);
        }

        _deferredCrossChunkMods.Clear();

        // POOLING: Dispose retained buffers last — all jobs above have returned theirs by now.
        _jobArrayPool.Dispose();

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
