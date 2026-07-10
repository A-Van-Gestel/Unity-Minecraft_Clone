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
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Pool;

/// <summary>
/// Manages the lifecycle of all background jobs (generation, meshing, lighting).
/// Owns the active <see cref="IChunkGenerator"/> strategy and delegates scheduling to it.
/// </summary>
public class WorldJobManager : IDisposable, ILightingCompletionDriver<ChunkCoord>
{
    private readonly World _world;
    private readonly IChunkGenerator _chunkGenerator;

    // Cached predicate for the cross-chunk sunlight veto: is a block id fully opaque (cannot propagate
    // sunlight)? Allocated once so ApplyCrossChunkLightMod doesn't churn a closure per cross-chunk mod.
    private readonly Func<ushort, bool> _isBlockFullyOpaque;

    // Cached lookup for the veto's live third-party support scan (CrossChunkLightModApplier.
    // CrossChunkSunlightSupport, the Bug 13 fix): chunk voxel origin -> live populated ChunkData, or
    // null when absent. Allocated once so ApplyCrossChunkLightMod doesn't churn a closure per mod.
    private readonly Func<Vector2Int, ChunkData> _getLoadedChunkByOrigin;

    #region Job Tracking Dictionaries

    public Dictionary<ChunkCoord, GenerationJobData> GenerationJobs { get; } = new Dictionary<ChunkCoord, GenerationJobData>();
    public Dictionary<ChunkCoord, MeshingJobData> MeshJobs { get; } = new Dictionary<ChunkCoord, MeshingJobData>();
    public Dictionary<ChunkCoord, LightingJobData> LightingJobs { get; } = new Dictionary<ChunkCoord, LightingJobData>();

    /// <summary>
    /// True if any generation, meshing, or lighting jobs are currently active.
    /// </summary>
    public bool HasActiveJobs => GenerationJobs.Count > 0 || MeshJobs.Count > 0 || LightingJobs.Count > 0;

    #endregion

    #region Lighting Diagnostics

    // These counters reflect ONLY the most recent ProcessLightingJobs() call and are intended to be
    // read immediately afterwards by the startup convergence diagnostics in
    // World.ForceCompleteDataJobsCoroutine. They separate the two known non-convergence modes:
    // edge-check cascade (stable but re-armed) vs. a persistent IsStable=false stability bug.
    // See Documentation/Design/CHUNK_PIPELINE_PERFORMANCE_ANALYSIS.md §4.

    /// <summary>Number of lighting job results processed in the most recent <see cref="ProcessLightingJobs"/> call.</summary>
    public int LastProcessedJobCount { get; private set; }

    /// <summary>Of the most recently processed jobs, how many reported not-stable and re-flagged
    /// <c>HasLightChangesToProcess</c> for another pass (the §4.2 stability-bug signature).</summary>
    public int LastUnstableJobCount { get; private set; }

    /// <summary>Of the most recently processed jobs, how many were stable but consumed an edge-check
    /// round and re-armed themselves plus neighbors (the §4.1 edge-check-cascade signature).</summary>
    public int LastEdgeRecycleJobCount { get; private set; }

    /// <summary>Cross-chunk light mods emitted by the most recently processed jobs and routed to a
    /// loaded neighbor (<c>ApplyDirect</c>) — i.e. mods that count toward stability.</summary>
    public int LastCrossChunkModsApplyRouted { get; private set; }

    /// <summary>How many cross-chunk applies in the most recent call actually changed the neighbor's
    /// light (<c>decision.ShouldApply</c>). Counts <b>every</b> effective apply — both the
    /// <c>ApplyDirect</c> path and the deferred-drain path (<c>DrainDeferredCrossChunkMods</c>) — so this
    /// can exceed <see cref="LastCrossChunkModsApplyRouted"/> (which tallies ApplyDirect only) when
    /// previously-deferred mods land this call. When this stays ≈0 while
    /// <see cref="LastCrossChunkModsApplyRouted"/> is high, the chunk is held unstable by perpetual no-op
    /// emissions against stale snapshots.</summary>
    public int LastCrossChunkModsEffective { get; private set; }

    /// <summary>Stale pull-back claims cleared by <see cref="VerifyPullBackClaims"/> in the most recent
    /// call — snapshot-trusting cross-seam re-lights whose live source no longer supported them (the
    /// Bug 14 ghost-light guard). Occasional non-zero values under concurrent multi-chunk darkening are
    /// expected; sustained high counts indicate heavy snapshot churn worth investigating.</summary>
    public int LastStalePullBacksCleared { get; private set; }

    /// <summary>Lighting jobs whose per-job processing threw in the most recent
    /// <see cref="ProcessLightingJobs"/> call (HF-2 fault isolation). Each fault logs one error and the
    /// pass continues — the faulted job is still released and removed, and its chunk stays
    /// re-schedulable. Any sustained non-zero value is a bug: investigate the logged exception.</summary>
    public int LastFaultedLightJobs { get; private set; }

    /// <summary>Effective cross-chunk applies in the most recent call, broken down by channel and
    /// operation. A steady non-zero in a removal bucket alongside its matching placement bucket is the
    /// signature of a stale-snapshot darkness/re-placement oscillation across a chunk seam.</summary>
    public int LastEffSunPlacement { get; private set; }

    public int LastEffSunRemoval { get; private set; }
    public int LastEffBlockPlacement { get; private set; }
    public int LastEffBlockRemoval { get; private set; }

    // First effective cross-chunk apply captured in the most recent call — a concrete sample of the
    // oscillating voxel (global position + old→new packed light). LastEffSampleValid gates the rest.
    public bool LastEffSampleValid { get; private set; }
    public bool LastEffSampleIsSun { get; private set; }
    public bool LastEffSampleIsRemoval { get; private set; }
    public Vector3Int LastEffSampleGlobalPos { get; private set; }
    public ushort LastEffSampleOldLight { get; private set; }
    public ushort LastEffSampleNewLight { get; private set; }

    #endregion

    // --- Native Buffer Pooling ---
    // Pools the fixed-size full-chunk job input buffers (voxel + light maps) shared by lighting
    // and meshing jobs, avoiding ~1.7 MB of Persistent alloc/dispose churn per job.
    // Retention cap is device-calibrated (OM-1); constructed in the constructor from settings.
    private readonly ChunkJobArrayPool _jobArrayPool;

    // MR-6: pools whole MeshDataJobOutput instances for the runtime meshing path. The output is rented
    // at ScheduleMeshing and returned in ProcessMeshJobs after the data is uploaded — never while the
    // job may still be running. NativeList retains capacity across Clear(), so after warm-up no meshing
    // job reallocates its output buffers.
    private readonly MeshOutputPool _meshOutputPool = new MeshOutputPool();

    // TG-6: pools the per-chunk active-voxel NativeList rented by the generation path. The list is rented
    // inside StandardChunkGenerator.ScheduleGeneration and returned at the single terminal release point
    // (ReleaseGenerationJobData, post-Complete) — never while the generation job may still be running.
    // NativeList retains capacity across Clear(), so after warm-up no scan job reallocates its active-voxel list.
    private readonly ActiveVoxelListPool _activeVoxelListPool = new ActiveVoxelListPool();

    // --- Cached Collections for GC Optimization ---
    private readonly List<ChunkCoord> _completedGenJobs = new List<ChunkCoord>();
    private readonly List<ChunkCoord> _completedMeshJobs = new List<ChunkCoord>();
    private readonly List<ChunkCoord> _completedLightJobs = new List<ChunkCoord>();

    // Reused snapshot of LightingJobs.Keys for one completion pass — the shared LightingCompletionPass
    // iterates a stable candidate list rather than the live dictionary (removal is after-loop anyway).
    private readonly List<ChunkCoord> _lightCompletionCandidates = new List<ChunkCoord>();

    // Per-job scratch shared across the LightingCompletionPass driver hooks (single-threaded, non-reentrant
    // pass): the job being completed/merged/released and its chunk, cached so the hooks don't re-look-them-up.
    private LightingJobData _curLightJob;
    private ChunkData _curLightChunk;

    private readonly HashSet<ChunkCoord> _chunksToRebuildMesh = new HashSet<ChunkCoord>();
    private readonly Dictionary<ChunkCoord, HashSet<Vector2Int>> _droppedLightUpdates = new Dictionary<ChunkCoord, HashSet<Vector2Int>>();

    /// <summary>
    /// A cross-chunk light modification deferred while its target's job was in flight, paired with the
    /// voxel origin of the chunk whose job emitted it — the Bug 13 live-support veto must still exclude
    /// the emitter when the mod is finally drained on a later pass.
    /// </summary>
    private readonly struct DeferredLightMod
    {
        /// <summary>The emitting chunk's voxel origin (world XZ).</summary>
        public readonly Vector2Int EmitterOriginXZ;

        /// <summary>The deferred cross-chunk modification.</summary>
        public readonly LightModification Mod;

        /// <summary>Initializes a deferred modification record.</summary>
        /// <param name="emitterOriginXZ">The emitting chunk's voxel origin (world XZ).</param>
        /// <param name="mod">The deferred cross-chunk modification.</param>
        public DeferredLightMod(Vector2Int emitterOriginXZ, in LightModification mod)
        {
            EmitterOriginXZ = emitterOriginXZ;
            Mod = mod;
        }
    }

    // Cross-chunk light mods whose target chunk had its own lighting job in flight at apply time.
    // Applying them immediately would be overwritten by that job's full-LightMap merge and the
    // surviving wake-up node would become a no-op — losing the mod permanently (Bug 08, path 2).
    // They are drained right after the target's merge instead. Persists across frames: the
    // target's job may complete on a later frame than the emitter's.
    private readonly Dictionary<ChunkCoord, List<DeferredLightMod>> _deferredCrossChunkMods = new Dictionary<ChunkCoord, List<DeferredLightMod>>();

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
        _isBlockFullyOpaque = id => _world.BlockTypes[id].IsOpaque;
        _getLoadedChunkByOrigin = originXZ =>
        {
            if (!World.IsChunkInWorld(ChunkCoord.FromVoxelOrigin(originXZ))) return null;
            ChunkData chunk = _world.worldData.RequestChunk(originXZ, false);
            return chunk != null && chunk.IsPopulated ? chunk : null;
        };

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

        // OM-1: size the native buffer pool's retention cap to the device (calibrated; default 512 on desktop).
        _jobArrayPool = new ChunkJobArrayPool(settings.chunkJobArrayPoolRetention);

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

        GenerationJobData jobData = _chunkGenerator.ScheduleGeneration(chunkCoord, _activeVoxelListPool);
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
            jobData.Output = _meshOutputPool.Rent(); // MR-6: pooled, pre-sized output (returned in ProcessMeshJobs)

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

            // MR-5: chain the chunk-space → section-space post-process onto the mesh job so the rewrite +
            // stream-3 interleave run on a worker thread instead of blocking the main thread inside
            // Chunk.ApplyMeshData. It only touches the output buffers (which live until ProcessMeshJobs),
            // so by the time the combined handle reports completed the post-process has already run.
            MeshPostProcessJob postJob = new MeshPostProcessJob
            {
                Vertices = jobData.Output.Vertices,
                OpaqueTris = jobData.Output.Triangles,
                TransparentTris = jobData.Output.TransparentTriangles,
                FluidTris = jobData.Output.FluidTriangles,
                Stats = jobData.Output.SectionStats,
                Normals = jobData.Output.Normals,
                LightData = jobData.Output.LightData,
                InterleavedStream3 = jobData.Output.InterleavedStream3,
                SectionHeight = ChunkMath.SECTION_SIZE,
            };

            // POOLING: Input buffers are returned to _jobArrayPool in ProcessMeshJobs after
            // Handle.Complete() — never dispose or return them while the job may be running.
            // Stage the handle: store the mesh-job handle BEFORE scheduling the chained post-process,
            // so if postJob.Schedule throws, the catch's Handle.Complete() still drains the already-live
            // mesh job before its output buffers are disposed (avoids a write-after-free race).
            jobData.Handle = job.Schedule();
            jobData.Handle = postJob.Schedule(jobData.Handle);
            MeshJobs.Add(chunkCoord, jobData);
        }
        catch
        {
            // A scheduled-but-untracked job must finish before its buffers can be released
            // (Complete() on a default handle is a no-op).
            jobData.Handle.Complete();
            _meshOutputPool.Return(jobData.Output); // MR-6: return-or-dispose (Return no-ops on uncreated)
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
    /// Acquires a halo-padded voxel volume buffer (length <see cref="ChunkJobArrayPool.PaddedBufferLength"/>):
    /// pooled when <paramref name="pooled"/> is true, otherwise a fresh allocation. Contents are undefined
    /// and left UNFILLED at schedule time — the job's worker-thread gather
    /// (<see cref="Jobs.NeighborhoodLightingJob.Execute"/>, P-2 Layer 1) fills every element from the
    /// snapshot maps wired in via <see cref="Jobs.NeighborhoodLightingJob.SetGatherSources"/>.
    /// </summary>
    /// <param name="pooled">Whether to rent from the pool instead of allocating.</param>
    /// <param name="allocator">The allocator for the non-pooled path.</param>
    /// <returns>An uninitialized padded voxel buffer.</returns>
    private NativeArray<uint> AcquirePaddedVoxels(bool pooled, Allocator allocator)
    {
        return pooled
            ? _jobArrayPool.RentPaddedVoxels()
            : new NativeArray<uint>(ChunkJobArrayPool.PaddedBufferLength, allocator, NativeArrayOptions.UninitializedMemory);
    }

    /// <summary>
    /// Acquires a halo-padded light volume buffer (length <see cref="ChunkJobArrayPool.PaddedBufferLength"/>):
    /// pooled when <paramref name="pooled"/> is true, otherwise a fresh allocation. Contents are undefined
    /// and left UNFILLED at schedule time — the job's worker-thread gather
    /// (<see cref="Jobs.NeighborhoodLightingJob.Execute"/>, P-2 Layer 1) fills every element from the
    /// snapshot maps wired in via <see cref="Jobs.NeighborhoodLightingJob.SetGatherSources"/>.
    /// </summary>
    /// <param name="pooled">Whether to rent from the pool instead of allocating.</param>
    /// <param name="allocator">The allocator for the non-pooled path.</param>
    /// <returns>An uninitialized padded light buffer.</returns>
    private NativeArray<ushort> AcquirePaddedLight(bool pooled, Allocator allocator)
    {
        return pooled
            ? _jobArrayPool.RentPaddedLight()
            : new NativeArray<ushort>(ChunkJobArrayPool.PaddedBufferLength, allocator, NativeArrayOptions.UninitializedMemory);
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

        LightingScheduleDecision.Result decision = LightingScheduleDecision.Evaluate(
            LightingJobs.ContainsKey(chunkCoord),
            _world.AreNeighborsDataReady(chunkCoord));

        if (decision == LightingScheduleDecision.Result.AlreadyInFlight)
            return false;

        if (decision == LightingScheduleDecision.Result.NeighborsNotReady)
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

            // P-2 Layer 1: rent the halo-padded volumes the job reads/writes, but leave them UNFILLED —
            // the gather now runs on the worker thread inside NeighborhoodLightingJob.Execute() (fed by the
            // 9 snapshot maps wired in below), so the main thread no longer pays the ~305 µs gather floor.
            NeighborMapSet neighbors = jobData.Input.Neighbors;
            jobData.PaddedVoxels = AcquirePaddedVoxels(usePooledBuffers, allocator);
            jobData.PaddedLight = AcquirePaddedLight(usePooledBuffers, allocator);

            jobData.Mods = new NativeList<LightModification>(allocator);
            jobData.PullBackClaims = new NativeList<PullBackClaim>(allocator);
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

            // LI-2 Step 2: banding is plumbed but not yet enabled — every job runs full height until the
            // band derivation is wired in behind the EnableLightingBandGather flag.
            jobData.BandHeight = ChunkMath.CHUNK_HEIGHT;

            NeighborhoodLightingJob job = new NeighborhoodLightingJob
            {
                PaddedVoxels = jobData.PaddedVoxels,
                PaddedLight = jobData.PaddedLight,
                BandHeight = jobData.BandHeight,
                ChunkPosition = chunkData.Position,
                SunlightBfsQueue = jobData.SunLightQueue,
                BlocklightBfsQueue = jobData.BlockLightQueue,
                SunlightColumnRecalcQueue = jobData.SunLightRecalcQueue,
                Heightmap = jobData.Input.Heightmap,
                BlockTypes = _world.JobDataManager.BlockTypesJobData,
                CrossChunkLightMods = jobData.Mods,
                PullBackClaims = jobData.PullBackClaims,
                IsStable = jobData.IsStable,
                PerformEdgeCheck = chunkData.NeedsEdgeCheck,
            };
            // P-2 Layer 1: wire the worker-thread gather's sources (center + 8 neighbors) in one place.
            job.SetGatherSources(neighbors, jobData.Map, jobData.LightMap);

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
    /// and flags chunks for their initial lighting pass. Each job is fault-isolated: an exception logs
    /// one error, the job is released and removed (unless it faulted before release), and the pass
    /// continues — budget-retry paths keep their un-released jobs for next frame as before.
    /// </summary>
    public void ProcessGenerationJobs()
    {
        _completedGenJobs.Clear();
        int modsBudget = _world.settings.maxStructureModsPerFrame;

        foreach (KeyValuePair<ChunkCoord, GenerationJobData> jobEntry in GenerationJobs)
        {
            if (!jobEntry.Value.Handle.IsCompleted) continue;

            // Fault isolation, stage 1 (HF-2): a failed Complete() means the job may still own its
            // containers — leave the entry enrolled (no release) and retry next pass.
            try
            {
                jobEntry.Value.Handle.Complete();
            }
            catch (Exception e)
            {
                Debug.LogError($"[GENERATION] Handle.Complete() for chunk {jobEntry.Key} faulted — job left enrolled for retry. {e}");
                continue;
            }

            // Fault isolation, stage 2 (HF-2): one faulted job must not abort the pass — released
            // jobs stranded in GenerationJobs get re-touched every frame (the ObjectDisposedException
            // cascade, fidelity B7). The budget-retry paths (jobFullyProcessed) intentionally keep the
            // job un-released for next frame, so the fault path releases only when the happy path
            // has not.
            bool released = false;
            try
            {
                ChunkData chunkData = _world.worldData.RequestChunk(jobEntry.Key.ToVoxelOrigin(), true);

                // --- STAGE 1: Populate with base terrain (Once per chunk) ---
                if (!chunkData.IsPopulated)
                {
                    chunkData.Populate(jobEntry.Value.Map, jobEntry.Value.HeightMap);

                    // Prefer the jobified active-voxel list (generation path). Generators that do not
                    // run the scan pass (e.g. legacy) leave it uncreated → fall back to the bitmask scan.
                    // Contract: IsCreated ⟺ the scan pass ran. An empty *created* list means genuinely zero
                    // active voxels (NOT a signal to fall back) — a generator that allocates ActiveVoxels must
                    // run ActiveVoxelScanJob to fill it. Both branches register the same active set; see the
                    // parity invariant on ActiveVoxelScanJob / Chunk.OnDataPopulated.
                    if (jobEntry.Value.ActiveVoxels.IsCreated)
                        chunkData.Chunk?.RegisterActiveVoxelsFromJob(jobEntry.Value.ActiveVoxels);
                    else
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
                        // World-generation expansion: resolve the Default replacement rule against the broad
                        // worldGenCanReplaceTags (not the player placement mask).
                        VoxelMod worldGenMod = fm;
                        worldGenMod.Source = VoxelModSource.WorldGen;
                        _world.EnqueueVoxelModification(worldGenMod);
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
                            // World-generation expansion: resolve the Default replacement rule against the broad
                            // worldGenCanReplaceTags (not the player placement mask).
                            VoxelMod worldGenMod = sm;
                            worldGenMod.Source = VoxelModSource.WorldGen;
                            _world.EnqueueVoxelModification(worldGenMod);
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

                ReleaseGenerationJobData(jobEntry.Value);
                _completedGenJobs.Add(jobEntry.Key);
                released = true;

                Chunk chunk = _world.GetChunkFromChunkCoord(jobEntry.Key);
                if (chunk != null && chunk.IsActive)
                {
                    _world.RequestChunkMeshRebuild(chunk);
                }

                // If we ran out of budget during processing the rest of this chunk's fast STAGE 3 steps,
                // break to respect frame time, letting the remaining completely finished jobs process next frame.
                if (modsBudget <= 0) break;
            }
            catch (Exception e)
            {
                Debug.LogError($"[GENERATION] Processing the completed generation job for chunk {jobEntry.Key} faulted — job released and removed; the chunk may be left partially processed. {e}");

                if (!released)
                {
                    ReleaseGenerationJobData(jobEntry.Value);
                    _completedGenJobs.Add(jobEntry.Key);
                }
            }
        }

        // Each entry here was already Complete()+ReleaseGenerationJobData()'d at its STAGE-3 completion above,
        // so removing it only drops the (now-empty) dictionary slot. Any future path that evicts a still-pending
        // job (e.g. an early removal on view-distance change before it completes) MUST Complete() then
        // ReleaseGenerationJobData() it first — see ReleaseGenerationJobData / GenerationJobData.Dispose — or its
        // per-chunk native buffers leak (and a pooled active-voxel list is lost from the pool).
        foreach (ChunkCoord chunkCoord in _completedGenJobs)
        {
            GenerationJobs.Remove(chunkCoord);

            // The job's removal is what flips AreNeighborsDataReady for the 8 neighbors — wake any
            // parked light work now instead of waiting for the fail-safe scan (MT-2).
            _world.PromoteLightWorkNeighborhood(chunkCoord.ToVoxelOrigin());
        }
    }

    /// <summary>
    /// Checks for completed mesh generation jobs and applies the resulting mesh data.
    /// Each job is fault-isolated: an exception logs one error, the buffers are still returned and the
    /// job removed (the chunk keeps its previous mesh), and the pass continues.
    /// </summary>
    public void ProcessMeshJobs()
    {
        _completedMeshJobs.Clear();
        foreach (KeyValuePair<ChunkCoord, MeshingJobData> jobEntry in MeshJobs)
        {
            if (!jobEntry.Value.Handle.IsCompleted) continue;

            // Fault isolation, stage 1 (HF-2): a failed Complete() means the job may still own its
            // buffers — leave the entry enrolled (no release) and retry next pass.
            try
            {
                jobEntry.Value.Handle.Complete();
            }
            catch (Exception e)
            {
                Debug.LogError($"[MESHING] Handle.Complete() for chunk {jobEntry.Key} faulted — job left enrolled for retry. {e}");
                continue;
            }

            // Fault isolation, stage 2 (HF-2): a faulted upload must not abort the pass — released jobs
            // stranded in MeshJobs get re-touched every frame (the ObjectDisposedException cascade,
            // fidelity B7). The chunk simply keeps its previous mesh; a later rebuild request recovers.
            try
            {
                Chunk chunk = _world.GetChunkFromChunkCoord(jobEntry.Key);
                if (chunk != null)
                {
                    // ApplyMeshData uploads the buffers synchronously (SetVertex/IndexBufferData copy);
                    // it no longer owns the output's lifecycle — the pool is returned to centrally below.
                    chunk.ApplyMeshData(jobEntry.Value.Output);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[MESHING] Applying the completed mesh for chunk {jobEntry.Key} faulted — buffers released, previous mesh kept. {e}");
            }
            finally
            {
                // MR-6: single output-release site for both branches, symmetric with the input release.
                // The upload above (or the discarded result when the chunk is gone) is done, so the
                // pooled buffers can be cleared and reused (or disposed if not pooled).
                _meshOutputPool.Return(jobEntry.Value.Output);

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
    /// Releases a completed generation job: returns its pooled active-voxel list to
    /// <see cref="_activeVoxelListPool"/> (TG-6) and disposes the rest of its per-job containers via
    /// <see cref="GenerationJobData.Dispose"/>. This is the single terminal release path for a pooled
    /// <see cref="GenerationJobData.ActiveVoxels"/> — co-located with <c>Dispose</c> so the list is
    /// returned exactly once (a job is removed from <see cref="GenerationJobs"/> the moment it reaches this
    /// point, and shutdown only releases jobs still enrolled — never both). Non-pooled lists
    /// (<see cref="GenerationJobData.ActiveVoxelsFromPool"/> == false) are freed by <c>Dispose</c> instead.
    /// Must only be called after <c>Handle.Complete()</c>.
    /// </summary>
    /// <param name="jobData">The completed generation job data.</param>
    private void ReleaseGenerationJobData(in GenerationJobData jobData)
    {
        // Return the pooled list BEFORE Dispose (which skips it via the ActiveVoxelsFromPool guard).
        if (jobData.ActiveVoxelsFromPool)
            _activeVoxelListPool.Return(jobData.ActiveVoxels);

        jobData.Dispose();
    }

    /// <summary>
    /// Returns a completed meshing job's pooled input buffers to <see cref="_jobArrayPool"/> and
    /// disposes its per-job section data. Must only be called after <c>Handle.Complete()</c>.
    /// Does NOT touch <see cref="MeshingJobData.Output"/> — the output is returned to
    /// <see cref="_meshOutputPool"/> separately (MR-6: centrally in <c>ProcessMeshJobs</c>, after
    /// <c>Chunk.ApplyMeshData</c> has uploaded it).
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

        // LI-1 padded volumes (distinct length — returned to their own retained stacks)
        _jobArrayPool.ReturnPaddedVoxels(jobData.PaddedVoxels);
        _jobArrayPool.ReturnPaddedLight(jobData.PaddedLight);

        // Per-job containers
        if (jobData.Input.Heightmap.IsCreated) jobData.Input.Heightmap.Dispose();
        if (jobData.SunLightQueue.IsCreated) jobData.SunLightQueue.Dispose();
        if (jobData.BlockLightQueue.IsCreated) jobData.BlockLightQueue.Dispose();
        if (jobData.SunLightRecalcQueue.IsCreated) jobData.SunLightRecalcQueue.Dispose();
        if (jobData.Mods.IsCreated) jobData.Mods.Dispose();
        if (jobData.PullBackClaims.IsCreated) jobData.PullBackClaims.Dispose();
        if (jobData.IsStable.IsCreated) jobData.IsStable.Dispose();
    }

    /// <summary>
    /// Checks for completed lighting jobs, applies light changes, and triggers mesh rebuilds.
    /// Each job's merge is fault-isolated: an exception logs one error, the job is still released and
    /// removed with its chunk left re-schedulable, and the pass continues
    /// (<see cref="LastFaultedLightJobs"/> counts occurrences).
    /// </summary>
    public void ProcessLightingJobs()
    {
        // Reset startup-diagnostic counters before the early-out so a no-op call reports zeros
        // rather than leaving stale values from the previous sweep.
        LastProcessedJobCount = 0;
        LastUnstableJobCount = 0;
        LastEdgeRecycleJobCount = 0;
        LastCrossChunkModsApplyRouted = 0;
        LastCrossChunkModsEffective = 0;
        LastStalePullBacksCleared = 0;
        LastFaultedLightJobs = 0;
        LastEffSunPlacement = 0;
        LastEffSunRemoval = 0;
        LastEffBlockPlacement = 0;
        LastEffBlockRemoval = 0;
        LastEffSampleValid = false;

        if (LightingJobs.Count == 0) return;

        _chunksToRebuildMesh.Clear();
        // _completedLightJobs is cleared + repopulated by LightingCompletionPass.RunMergeLoop below.

        foreach (HashSet<Vector2Int> set in _droppedLightUpdates.Values)
        {
            HashSetPool<Vector2Int>.Release(set);
        }

        _droppedLightUpdates.Clear();

        // Snapshot the in-flight keys, then run the shared completion-pass merge loop. The fault-isolation
        // (stage 1/2, HF-2) and release-inside / enroll ordering now live in LightingCompletionPass, driven
        // here via the ILightingCompletionDriver<ChunkCoord> hooks below and by the editor frame simulator,
        // so both replay the same bookkeeping (HF-4 #2). Snapshot is byte-identical to iterating the live
        // dictionary — the loop never adds to LightingJobs and removal is after-loop. Enrollment fills
        // _completedLightJobs (also read by the merge's cross-chunk _completedLightJobs.Contains check).
        _lightCompletionCandidates.Clear();
        foreach (ChunkCoord coord in LightingJobs.Keys)
            _lightCompletionCandidates.Add(coord);

        LightingCompletionPass.RunMergeLoop(_lightCompletionCandidates, this, _completedLightJobs);

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

        // Remove + promote every enrolled job, strictly after the whole merge loop (shared skeleton, so a
        // completion promoting a neighbor sees the fully-merged pass — MT-2). See the driver's
        // RemoveAndPromote hook for the per-job rationale.
        LightingCompletionPass.RunRemoveAndPromote(_completedLightJobs, this);
    }

    #region LightingCompletionPass driver (HF-4 #2)

    // Explicit ILightingCompletionDriver<ChunkCoord> implementation: the per-job side effects the shared
    // LightingCompletionPass skeleton sequences (the exact body the old inline ProcessLightingJobs loop
    // ran). Explicit so they don't widen WorldJobManager's public surface — the pass invokes them through
    // the interface (`this`). _curLightJob / _curLightChunk cache the job + chunk across a single job's
    // hooks; the pass is single-threaded and non-reentrant so plain fields are safe.

    /// <inheritdoc />
    bool ILightingCompletionDriver<ChunkCoord>.IsComplete(ChunkCoord key) => LightingJobs[key].Handle.IsCompleted;

    /// <inheritdoc />
    void ILightingCompletionDriver<ChunkCoord>.CompleteJob(ChunkCoord key)
    {
        _curLightJob = LightingJobs[key];
        _curLightChunk = null;
        _curLightJob.Handle.Complete();
    }

    /// <inheritdoc />
    void ILightingCompletionDriver<ChunkCoord>.OnCompleteFault(ChunkCoord key, Exception e)
    {
        LastFaultedLightJobs++;
        Debug.LogError($"[LIGHTING] Handle.Complete() for chunk {key} faulted — job left enrolled for retry. {e}");
    }

    /// <inheritdoc />
    void ILightingCompletionDriver<ChunkCoord>.MergeJob(ChunkCoord key)
    {
        LastProcessedJobCount++;
        _curLightChunk = _world.worldData.RequestChunk(key.ToVoxelOrigin(), false);
        MergeCompletedLightingJob(key, _curLightJob, _curLightChunk);
    }

    /// <inheritdoc />
    void ILightingCompletionDriver<ChunkCoord>.OnMergeFault(ChunkCoord key, Exception e)
    {
        LastFaultedLightJobs++;
        Debug.LogError($"[LIGHTING] Merging the completed lighting job for chunk {key} faulted — containers released, chunk re-flagged. {e}");

        // Stability is unknown after a fault: keep the chunk re-schedulable so a corrective pass runs,
        // rather than silently dropping it in a half-merged state.
        if (_curLightChunk != null) _curLightChunk.HasLightChangesToProcess = true;
    }

    /// <inheritdoc />
    void ILightingCompletionDriver<ChunkCoord>.ReleaseJob(ChunkCoord key)
    {
        // Unconditional (merge finally): the flag-pairing invariant (IsAwaitingMainThreadProcess set in
        // MergeCompletedLightingJob, cleared here even on fault) + the container release that keeps a
        // faulted job from lingering in LightingJobs with disposed containers.
        if (_curLightChunk != null) _curLightChunk.IsAwaitingMainThreadProcess = false;

        // POOLING: Return the full-volume buffers for reuse; dispose per-job containers.
        ReleaseLightingJobData(_curLightJob);
    }

    /// <inheritdoc />
    void ILightingCompletionDriver<ChunkCoord>.RemoveAndPromote(ChunkCoord key)
    {
        LightingJobs.Remove(key);

        // Completion is the last event in an AreNeighborsReadyAndLit unblock chain (the neighbor flags it
        // also reads clear at schedule time, while the job is still in-flight) and is what un-parks the
        // chunk itself if it was re-flagged mid-flight — promote the 3×3 now (MT-2).
        _world.PromoteLightWorkNeighborhood(key.ToVoxelOrigin());
    }

    #endregion

    /// <summary>
    /// Merges one completed lighting job's results into its chunk: applies the light map, drains
    /// mods other jobs deferred for it, verifies the job's pull-back claims (Bug 14), routes its
    /// outbound cross-chunk mods, and runs the stability / edge-check bookkeeping. Extracted from
    /// the <see cref="ProcessLightingJobs"/> loop so a fault in any single merge stays isolated to
    /// that job (HF-2); the caller owns <c>Handle.Complete()</c>, container release, the
    /// <c>IsAwaitingMainThreadProcess</c> clear, and removal enrollment.
    /// </summary>
    /// <param name="chunkCoord">The chunk whose lighting job completed.</param>
    /// <param name="jobData">The completed job's data (already <c>Complete()</c>d).</param>
    /// <param name="chunkData">The chunk's live data, or null when it vanished mid-flight.</param>
    private void MergeCompletedLightingJob(ChunkCoord chunkCoord, in LightingJobData jobData, ChunkData chunkData)
    {
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
            DrainDeferredCrossChunkMods(chunkCoord, chunkData);

            // The emitting chunk's voxel origin — excluded from the Bug 13 live-support veto
            // (its data is exactly the stale side the removal mods came from).
            Vector2Int emitterOriginXZ = chunkCoord.ToVoxelOrigin();

            // Re-verify the job's snapshot-trusting cross-seam re-lights against live neighbor
            // data, clearing stale ghost light through the removal veto (Bug 14). Runs after the
            // merge + deferred drain so superseded claims are recognized.
            VerifyPullBackClaims(chunkData, emitterOriginXZ, jobData.PullBackClaims);

            foreach (LightModification mod in jobData.Mods)
            {
                Vector2Int neighborChunkVoxelPos = _world.worldData.GetChunkCoordFor(mod.GlobalPosition);
                ChunkCoord neighborChunkCoord = ChunkCoord.FromVoxelOrigin(neighborChunkVoxelPos);

                // Resolve the target chunk's state, then route via the shared decision so the
                // editor validation harness exercises this exact drop/persist/defer/apply rule.
                // The terrain lookup and in-flight checks are guarded by targetInWorld so an
                // out-of-world mod short-circuits without touching the chunk store or job dict.
                bool targetInWorld = World.IsChunkInWorld(neighborChunkCoord);
                ChunkData neighborChunk = targetInWorld
                    ? _world.worldData.RequestChunk(neighborChunkVoxelPos, false)
                    : null;
                bool targetLoaded = neighborChunk != null && neighborChunk.IsPopulated;

                // A target already processed this pass (_completedLightJobs) has merged and is
                // safe to apply to directly; one still in flight must be deferred (Bug 08 path 2).
                bool targetJobInFlightThisPass = targetInWorld &&
                                                 LightingJobs.ContainsKey(neighborChunkCoord) &&
                                                 !_completedLightJobs.Contains(neighborChunkCoord);

                LightingJobProcessor.CrossChunkModRoute route = LightingJobProcessor.RouteCrossChunkMod(
                    targetInWorld, targetLoaded, targetJobInFlightThisPass);

                // Out-of-world mods can never be consumed; everything else keeps the chunk from
                // being treated as stable until delivered.
                hasRealCrossChunkMods |= LightingJobProcessor.CountsAsRealCrossChunkMod(route);

                switch (route)
                {
                    case LightingJobProcessor.CrossChunkModRoute.DropOutOfWorld:
                        // Dropped without affecting stability (boundary chunks would otherwise
                        // reschedule lighting indefinitely).
                        continue;

                    case LightingJobProcessor.CrossChunkModRoute.PersistUndeliverable:
                        PersistUndeliverableLightMod(neighborChunkCoord, in mod);
                        continue;

                    case LightingJobProcessor.CrossChunkModRoute.Defer:
                        // Applying now would be overwritten by the target's own full-LightMap
                        // merge — defer; drained right after that merge (DrainDeferredCrossChunkMods).
                        if (!_deferredCrossChunkMods.TryGetValue(neighborChunkCoord, out List<DeferredLightMod> deferredList))
                        {
                            deferredList = ListPool<DeferredLightMod>.Get();
                            _deferredCrossChunkMods[neighborChunkCoord] = deferredList;
                        }

                        deferredList.Add(new DeferredLightMod(emitterOriginXZ, in mod));
                        continue;

                    case LightingJobProcessor.CrossChunkModRoute.ApplyDirect:
                        // Diagnostics: an ApplyDirect mod counts toward stability regardless of
                        // effect; ApplyCrossChunkLightMod tallies how many actually changed the
                        // neighbor (plus channel/op breakdown + a sample) to characterize the
                        // §4.2 non-convergence mode.
                        LastCrossChunkModsApplyRouted++;
                        ApplyCrossChunkLightMod(neighborChunk, in mod, emitterOriginXZ);
                        continue;
                }
            }
        }
        else
        {
            // The job result is discarded (the chunk vanished or lost its data mid-flight),
            // so mods other chunks deferred for it can never be drained — degrade them to
            // the persisted pending stores instead.
            DegradeDeferredCrossChunkMods(chunkCoord);
        }

        // Override stability: If the Burst job reported not-stable solely because
        // of cross-chunk mods targeting out-of-world positions (which can never be
        // consumed), treat the chunk as effectively stable. Without this, world-boundary
        // chunks would reschedule lighting indefinitely.
        isChunkStable = LightingJobProcessor.IsEffectivelyStable(isChunkStable, hasRealCrossChunkMods);

        if (isChunkStable)
        {
            _chunksToRebuildMesh.Add(chunkCoord);
            _world.RequestNeighborMeshRebuilds(chunkCoord);

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
                LastEdgeRecycleJobCount++;

                // Self-edge-check: re-examine this chunk's own borders with the
                // latest neighbor snapshot data.
                chunkData.NeedsEdgeCheck = true;
                chunkData.HasLightChangesToProcess = true;

                TriggerNeighborEdgeChecks(chunkCoord);
            }
        }
        else
        {
            if (chunkData != null) chunkData.HasLightChangesToProcess = true;
            LastUnstableJobCount++;
        }
    }

    /// <summary>
    /// Re-verifies a completed lighting job's <see cref="PullBackClaim"/>s against LIVE neighbor data
    /// (the Bug 14 stale-ghost guard). The job's darkness-wave pull-back re-lit border voxels from
    /// schedule-time neighbor snapshots; a snapshot that went stale (the neighbor darkened after it was
    /// taken) plants sourceless ghost light that nothing ever revisits. For each claim: a superseded
    /// write (the voxel no longer holds the claimed value) is skipped; a claim the live neighbor still
    /// supports (<see cref="CrossChunkLightModApplier.PullBackClaimStillSupported"/>) is kept; an
    /// unverifiable claim (neighbor chunk absent/unloaded) is kept conservatively; a stale claim is
    /// routed through the standard cross-chunk sunlight-removal veto with the claimed neighbor's chunk
    /// as the excluded emitter — so a voxel with OTHER genuine support survives, and a genuinely
    /// sourceless one clears and wakes the chunk for the corrective darkness wave.
    /// </summary>
    /// <param name="chunkData">The just-merged chunk whose claims are verified.</param>
    /// <param name="ownOriginXZ">The chunk's voxel origin (world XZ).</param>
    /// <param name="claims">The claims the job recorded.</param>
    private void VerifyPullBackClaims(ChunkData chunkData, Vector2Int ownOriginXZ, NativeList<PullBackClaim> claims)
    {
        foreach (PullBackClaim claim in claims)
        {
            // Defensive: a claim must target a center voxel (the job guarantees it — see PullBackClaim).
            // An out-of-bounds position would throw below and abort the WHOLE processing pass, leaving
            // already-released jobs in LightingJobs to spam ObjectDisposedException every frame after.
            if ((uint)claim.CenterPos.x >= VoxelData.ChunkWidth ||
                (uint)claim.CenterPos.z >= VoxelData.ChunkWidth ||
                (uint)claim.CenterPos.y >= VoxelData.ChunkHeight)
                continue;

            // Superseded: a later write (same job's wave, a drained inbound mod) replaced the value —
            // the claim no longer describes live state, so there is nothing to verify.
            ushort currentLight = chunkData.GetLightData(claim.CenterPos.x, claim.CenterPos.y, claim.CenterPos.z);
            if (LightBitMapping.GetSkyLight(currentLight) != claim.WrittenSky) continue;

            // Resolve the claimed neighbor voxel in world space (NeighborPos is 3x3-local).
            Vector3Int neighborGlobal = new Vector3Int(
                ownOriginXZ.x + claim.NeighborPos.x, claim.NeighborPos.y, ownOriginXZ.y + claim.NeighborPos.z);
            Vector2Int neighborOriginXZ = _world.worldData.GetChunkCoordFor(neighborGlobal);

            // Unverifiable (neighbor absent/unloaded): keep the value — the trusted snapshot is the best
            // available data, and the neighbor's own load path re-lights the seam when it returns.
            ChunkData neighborChunk = _getLoadedChunkByOrigin(neighborOriginXZ);
            if (neighborChunk == null) continue;

            Vector3Int neighborLocal = new Vector3Int(
                neighborGlobal.x - neighborOriginXZ.x, neighborGlobal.y, neighborGlobal.z - neighborOriginXZ.y);
            byte liveNeighborSky = LightBitMapping.GetSkyLight(
                neighborChunk.GetLightData(neighborLocal.x, neighborLocal.y, neighborLocal.z));
            bool neighborFullyOpaque = _isBlockFullyOpaque(BurstVoxelDataBitMapping.GetId(
                neighborChunk.GetVoxel(neighborLocal.x, neighborLocal.y, neighborLocal.z)));
            byte centerOpacity = _world.BlockTypes[BurstVoxelDataBitMapping.GetId(
                chunkData.GetVoxel(claim.CenterPos.x, claim.CenterPos.y, claim.CenterPos.z))].opacity;

            if (CrossChunkLightModApplier.PullBackClaimStillSupported(
                    liveNeighborSky, neighborFullyOpaque, centerOpacity, claim.WrittenSky))
                continue;

            // Stale: clear through the standard removal veto (emitter = the claimed neighbor's chunk,
            // the side whose snapshot went stale). Other genuine support still vetoes the removal.
            LightModification removal = new LightModification
            {
                GlobalPosition = new Vector3Int(
                    ownOriginXZ.x + claim.CenterPos.x, claim.CenterPos.y, ownOriginXZ.y + claim.CenterPos.z),
                LightLevel = 0,
                Channel = LightChannel.Sun,
            };

            if (ApplyCrossChunkLightMod(chunkData, in removal, neighborOriginXZ))
                LastStalePullBacksCleared++;
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
    /// <param name="emitterOriginXZ">The emitting chunk's voxel origin — excluded from the live
    /// cross-chunk support scan (its data is the stale side the mod came from).</param>
    /// <returns>True if the modification changed the target's light (and enqueued a BFS wake-up node);
    /// false if it was a no-op against the target's current value.</returns>
    private bool ApplyCrossChunkLightMod(ChunkData targetChunk, in LightModification mod, Vector2Int emitterOriginXZ)
    {
        Vector3Int localVoxelPos = _world.worldData.GetLocalVoxelPositionInChunk(mod.GlobalPosition);

        ushort currentLight = targetChunk.GetLightData(localVoxelPos.x, localVoxelPos.y, localVoxelPos.z);
        // Only sunlight REMOVALs (LightLevel == 0) consult independent support — see
        // CrossChunkLightModApplier.ComputeSunlight. Skip the neighbor scans for placements/uplifts
        // (the common case during initial-load sunlight propagation), whose decision ignores it.
        byte independentSunSupport = 0;
        if (mod.Channel == LightChannel.Sun && mod.LightLevel == 0)
        {
            // Support is attenuated by the target voxel's own opacity (the light enters it), matching
            // NeighborhoodLightingJob.AttenuateLight — a flat air step would over-estimate support into
            // semi-transparent media and wrongly veto a legitimate removal. Independent support is the
            // max of in-chunk neighbors (Bug 11) and live third-party cross-chunk neighbors (Bug 13).
            ushort targetId = BurstVoxelDataBitMapping.GetId(
                targetChunk.GetVoxel(localVoxelPos.x, localVoxelPos.y, localVoxelPos.z));
            byte targetOpacity = _world.BlockTypes[targetId].opacity;
            byte inChunk = CrossChunkLightModApplier.InChunkSunlightSupport(targetChunk, localVoxelPos, targetOpacity, _isBlockFullyOpaque);
            byte crossChunk = CrossChunkLightModApplier.CrossChunkSunlightSupport(
                _world.worldData.GetChunkCoordFor(mod.GlobalPosition), localVoxelPos, targetOpacity,
                emitterOriginXZ, _getLoadedChunkByOrigin, _isBlockFullyOpaque);
            independentSunSupport = Math.Max(inChunk, crossChunk);
        }

        CrossChunkLightModApplier.ApplyDecision decision = CrossChunkLightModApplier.Compute(currentLight, in mod, independentSunSupport);

        if (!decision.ShouldApply)
        {
            return false;
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

        RecordEffectiveCrossChunkMod(in mod, currentLight, decision.NewLight);
        return true;
    }

    /// <summary>
    /// Records diagnostic accounting for one effective cross-chunk apply (one that changed the
    /// neighbor's light): increments the effective total, the per-channel/per-operation breakdown, and
    /// captures the first such apply this call as a concrete sample. Sunlight removal is identified by
    /// a zero target level; blocklight removal by the mod's <c>IsRemoval</c> flag.
    /// </summary>
    /// <param name="mod">The cross-chunk modification that was applied.</param>
    /// <param name="oldLight">The target voxel's packed light before the apply.</param>
    /// <param name="newLight">The target voxel's packed light after the apply.</param>
    private void RecordEffectiveCrossChunkMod(in LightModification mod, ushort oldLight, ushort newLight)
    {
        LastCrossChunkModsEffective++;

        bool isRemoval;
        if (mod.Channel == LightChannel.Sun)
        {
            isRemoval = mod.LightLevel == 0;
            if (isRemoval) LastEffSunRemoval++;
            else LastEffSunPlacement++;
        }
        else
        {
            isRemoval = mod.IsRemoval;
            if (isRemoval) LastEffBlockRemoval++;
            else LastEffBlockPlacement++;
        }

        if (LastEffSampleValid) return;

        LastEffSampleValid = true;
        LastEffSampleIsSun = mod.Channel == LightChannel.Sun;
        LastEffSampleIsRemoval = isRemoval;
        LastEffSampleGlobalPos = mod.GlobalPosition;
        LastEffSampleOldLight = oldLight;
        LastEffSampleNewLight = newLight;
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
        if (!_deferredCrossChunkMods.Remove(chunkCoord, out List<DeferredLightMod> deferred)) return;

        foreach (DeferredLightMod deferredMod in deferred)
        {
            ApplyCrossChunkLightMod(chunkData, in deferredMod.Mod, deferredMod.EmitterOriginXZ);
        }

        ListPool<DeferredLightMod>.Release(deferred);
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
        if (!_deferredCrossChunkMods.Remove(chunkCoord, out List<DeferredLightMod> deferred)) return;

        foreach (DeferredLightMod deferredMod in deferred)
        {
            PersistUndeliverableLightMod(chunkCoord, in deferredMod.Mod);
        }

        ListPool<DeferredLightMod>.Release(deferred);
    }

    /// <summary>
    /// Persists a single cross-chunk light modification that cannot be applied to a live chunk
    /// (target unloaded or vanished mid-flight). Sun mods are saved as column recalculation
    /// entries; blocklight mods are saved as pending RGB modifications for replay on load.
    /// </summary>
    private void PersistUndeliverableLightMod(ChunkCoord targetChunkCoord, in LightModification mod)
    {
        // Shared column math + in-footprint bounds guard (LightingModPersister), so production and the
        // lighting validation harness can never drift on how a mod's local column is resolved.
        if (!LightingModPersister.TryComputeLocalColumn(targetChunkCoord, in mod, out int localX, out int localZ))
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
    /// Merges the lighting results from a background job into the live ChunkData. Delegates to
    /// <see cref="ChunkData.ApplyJobLightMap"/> — the shared per-section merge + uniform-sky compaction
    /// that the editor lighting validation harness also runs, so the two can never silently diverge.
    /// The full-LightMap overwrite is safe against cross-chunk mods: mods targeting a chunk with an
    /// in-flight job are deferred and drained right after this merge (the Bug 08 path-2 fix).
    /// </summary>
    private void ApplyLightingJobResult(ChunkData chunkData, LightingJobData jobData)
    {
        // LI-1: the job wrote light only into the center [2,18) region of the padded volume — extract it
        // back into the section-contiguous center LightMap, then merge through the same ApplyJobLightMap.
        // Voxels are never modified in-job, so jobData.Map (the unchanged center voxel snapshot) is still
        // the correct merge reference. LI-2: only the job's gathered band rows are extracted; above them
        // LightMap keeps its schedule-time snapshot, which the job provably did not change.
        ChunkMath.ExtractCenterLight(jobData.PaddedLight, jobData.LightMap, jobData.BandHeight);
        chunkData.ApplyJobLightMap(jobData.Map, jobData.LightMap, _world.BlockTypes);
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
            // Releases an enrolled (not-yet-terminally-completed) job — returns its pooled list to the pool
            // (freed by _activeVoxelListPool.Dispose() below) and disposes the rest. These jobs never reached
            // the terminal ReleaseGenerationJobData in ProcessGenerationJobs, so this is their only release.
            ReleaseGenerationJobData(job);
        }

        foreach (MeshingJobData job in MeshJobs.Values)
        {
            job.Handle.Complete();
            _meshOutputPool.Return(job.Output); // returned then freed by _meshOutputPool.Dispose() below
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
        foreach (List<DeferredLightMod> deferred in _deferredCrossChunkMods.Values)
        {
            ListPool<DeferredLightMod>.Release(deferred);
        }

        _deferredCrossChunkMods.Clear();

        // POOLING: Dispose retained buffers last — all jobs above have returned theirs by now.
        _jobArrayPool.Dispose();
        _meshOutputPool.Dispose();
        _activeVoxelListPool.Dispose();

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
