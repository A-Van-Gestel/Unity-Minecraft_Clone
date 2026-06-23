using System;
using System.Collections.Generic;
using Data;
using Helpers;
using Jobs;
using Jobs.BurstData;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Pool;
using Object = UnityEngine.Object;

public class Chunk
{
    public ChunkCoord Coord;
    public Vector3 ChunkPosition;
    public ChunkData ChunkData;
    private readonly SectionRenderer[] _sectionRenderers;

    // Expose for pool management validation
    public readonly GameObject ChunkGameObject;

    private bool _isActive;

    // TG-4 Phase 1: the active-voxel behavior buckets now live on ChunkData (the data they describe), which owns
    // their allocation, per-family routing, and disposal. Chunk keeps only the tick orchestration below.

    // Profiler markers for the behavior-tick path (TG-4 profile gate: measure managed/main-thread tick cost and the
    // per-family split before deciding Phase 2 jobification vs the TG-5 finisher). Near-zero cost when not recording;
    // these are the named samples the Unity Profiler window and the profiler MCP tools key off, and the substrate the
    // full-world fluid stress pass reads. Declared once (static) — the marker name, not the instance, is what's sampled.
    private static readonly ProfilerMarker s_tickUpdateMarker = new ProfilerMarker("Chunk.TickUpdate");
    private static readonly ProfilerMarker s_tickGrassMarker = new ProfilerMarker("Chunk.TickUpdate.Grass");
    private static readonly ProfilerMarker s_tickFluidMarker = new ProfilerMarker("Chunk.TickUpdate.Fluid");

    // Cached reference to avoid a GetComponent call on every pool activation, while remaining Unity-lifetime safe
    private ChunkLoadAnimation _loadAnimation;
    private bool _hasPlayedLoadAnimation;

    #region Constructor

    /// <summary>
    /// Creates a new Chunk Visual.
    /// NOTE: Should only be called by the ChunkPool. Use World.GetChunkFromPool() instead.
    /// </summary>
    public Chunk(ChunkCoord chunkCoord)
    {
        Coord = chunkCoord;

        // Create GameObject hierarchy
#if UNITY_EDITOR
        ChunkGameObject = new GameObject($"Chunk {Coord.X.ToString()}, {Coord.Z.ToString()}");
#else
        ChunkGameObject = new GameObject();
#endif
        ChunkGameObject.transform.SetParent(World.Instance.transform);

        // Pre-add the load animation component to avoid runtime AddComponent (which causes boxing/GC overhead)
        if (World.Instance.settings.enableChunkLoadAnimations)
        {
            _loadAnimation = ChunkGameObject.AddComponent<ChunkLoadAnimation>();
            _loadAnimation.enabled = false;
        }

        // Initialize Section Renderers
        const int sectionCount = VoxelData.ChunkHeight / ChunkMath.SECTION_SIZE;
        _sectionRenderers = new SectionRenderer[sectionCount];
        for (int i = 0; i < sectionCount; i++)
        {
            _sectionRenderers[i] = new SectionRenderer(ChunkGameObject.transform, i);
        }

        // Ensure object is inactive until properly Reset/Activated
        ChunkGameObject.SetActive(false);
    }

    #endregion

    #region Lifecycle

    /// <summary>
    /// Resets the Chunk instance for use at a new coordinate.
    /// Used by the ChunkPool.
    /// </summary>
    public void Reset(ChunkCoord chunkCoord)
    {
        Coord = chunkCoord;
        ChunkPosition = Coord.ToWorldPosition();

        // Update GameObject identity
#if UNITY_EDITOR
        ChunkGameObject.name = $"Chunk {Coord.X.ToString()}, {Coord.Z.ToString()}";
#endif

        if (World.Instance.settings.enableChunkLoadAnimations && _loadAnimation != null)
        {
            _loadAnimation.enabled = true;
            _loadAnimation.ResetToUnderground(ChunkPosition);
        }
        else
        {
            if (_loadAnimation != null) _loadAnimation.enabled = false;
            ChunkGameObject.transform.position = ChunkPosition;
        }

        // Reset State
        _isActive = true;
        _hasPlayedLoadAnimation = false;
        // NOTE: the active-voxel buckets live on ChunkData and are cleared in ChunkData.Reset (data lifecycle),
        // not here — a recycled visual re-linking to a still-valid cached ChunkData keeps its correct active set.

        Vector2Int worldPosKey = Coord.ToVoxelOrigin();

        // Link Data
        // NOTE: We retrieve the existing data (loaded or generated) from WorldData.
        ChunkData = World.Instance.worldData.RequestChunk(worldPosKey, true);

        // CRITICAL: Link the Data to this Visual Instance
        if (ChunkData != null)
        {
            ChunkData.Chunk = this;
            if (ChunkData.IsPopulated)
            {
                OnDataPopulated();
            }
        }

        // Reset Visuals (clears mesh but keeps memory allocated)
        foreach (SectionRenderer sectionRenderer in _sectionRenderers)
        {
            sectionRenderer.Clear();
        }

        // Ensure object is active
        ChunkGameObject.SetActive(true);
    }

    /// <summary>
    /// Prepares the chunk to be returned to the pool.
    /// Unlinks data references to prevent ghost updates.
    /// </summary>
    public void Release()
    {
        // CRITICAL: Unlink the Data.
        // If this ChunkData is modified while the Visual is in the pool,
        // it shouldn't try to update a disabled GameObject.
        if (ChunkData != null)
        {
            ChunkData.Chunk = null;
            ChunkData = null;
        }

        if (ChunkGameObject != null)
        {
            ChunkGameObject.SetActive(false);
        }

        if (_loadAnimation != null)
        {
            _loadAnimation.enabled = false;
        }

        _isActive = false;
    }

    /// <summary>
    /// Permanently destroys the GameObject. Used when shutting down the pool.
    /// </summary>
    public void Destroy()
    {
        if (ChunkGameObject != null)
        {
            Object.Destroy(ChunkGameObject);
        }

        // Clean up renderers (Meshes)
        if (_sectionRenderers != null)
        {
            foreach (SectionRenderer sr in _sectionRenderers) sr.Destroy();
        }
    }

    #endregion

    /// <summary>
    /// Scans the newly populated chunk data for voxels that possess active behaviors (e.g., grass spreading)
    /// and registers them to the active voxel list for continuous tick processing.
    /// </summary>
    /// <remarks>
    /// Fallback scan used by the load-from-save (<see cref="World"/>) and pool-recycle replay
    /// (<see cref="Reset"/>) paths, where no generation job runs and active voxels are not persisted.
    /// The freshly-generated path instead consumes <see cref="RegisterActiveVoxelsFromJob"/>, which is
    /// emitted by <see cref="Jobs.ActiveVoxelScanJob"/>. This scan reads the precomputed flat
    /// <see cref="World.IsActiveById"/> table instead of dereferencing managed <c>BlockType</c> objects.
    /// <para><b>Parity invariant:</b> this managed scan and the Burst <see cref="Jobs.ActiveVoxelScanJob"/>
    /// must register the same active set — they MUST agree on both the active criterion
    /// (<see cref="World.IsActiveById"/> here vs <c>BlockTypeJobData.IsActive</c> there, co-built in one loop
    /// in <c>World</c> init, so drift-proof) and the section/index convention. Change one path's criterion or
    /// convention and you must change the other.</para>
    /// </remarks>
    public void OnDataPopulated()
    {
        bool[] isActiveById = World.Instance.IsActiveById;

        // Now that the data is here, we can scan for active voxels.
        // Optimization: Iterate through sections first to skip empty ones.
        for (int s = 0; s < ChunkData.sections.Length; s++)
        {
            ChunkSection section = ChunkData.sections[s];
            if (section == null || section.IsEmpty) continue;

            int startY = s * ChunkMath.SECTION_SIZE;

            // Iterate only within this non-empty section
            for (int i = 0; i < section.voxels.Length; i++)
            {
                uint packedData = section.voxels[i];
                ushort id = BurstVoxelDataBitMapping.GetId(packedData);

                if (isActiveById[id])
                {
                    // Convert section index back to 3D position
                    int x = i % ChunkMath.SECTION_SIZE;
                    int yOffset = i / ChunkMath.SECTION_SIZE % ChunkMath.SECTION_SIZE;
                    int z = i / (ChunkMath.SECTION_SIZE * ChunkMath.SECTION_SIZE);

                    ChunkData.AddActiveVoxel(new Vector3Int(x, startY + yOffset, z), id);
                }
            }
        }
    }

    /// <summary>
    /// Registers the active voxels emitted by the generation job's <see cref="Jobs.ActiveVoxelScanJob"/>,
    /// unpacking each flat chunk index back into a local position. Used on the freshly-generated path
    /// in place of <see cref="OnDataPopulated"/> — the job has already done the per-voxel scan, so the
    /// main thread only copies a short list.
    /// </summary>
    /// <param name="packedIndices">Flat chunk indices (<see cref="ChunkMath.GetFlattenedIndexInChunk"/> convention) of active voxels.</param>
    public void RegisterActiveVoxelsFromJob(NativeList<int> packedIndices)
    {
        foreach (int i in packedIndices)
        {
            ChunkMath.GetLocalPositionFromFlattenedIndex(i, out int x, out int y, out int z);
            ChunkData.AddActiveVoxel(new Vector3Int(x, y, z));
        }
    }

    #region Block Behavior Methods

    /// <summary>
    /// Processes the block behavior for all active voxels currently registered in this chunk's <see cref="ChunkData"/>.
    /// Removes voxels from the active buckets if they no longer meet their activation conditions. The active-voxel
    /// storage lives on <see cref="ChunkData"/> (TG-4 Phase 1); this method is the tick orchestration that drives it.
    /// </summary>
    public void TickUpdate()
    {
        if (ChunkData == null) return;

        NativeHashSet<int> grass = ChunkData.ActiveGrassBucket;
        NativeHashSet<int> fluids = ChunkData.ActiveFluidsBucket;

        int grassCount = grass.IsCreated ? grass.Count : 0;
        int fluidCount = fluids.IsCreated ? fluids.Count : 0;
        if (grassCount == 0 && fluidCount == 0) return;

        // Marker opened AFTER the no-op early-outs so a chunk with nothing to tick pays nothing.
        using (s_tickUpdateMarker.Auto())
        {
            // Iterate each behavior family in a fixed order (grass, then fluids) — the TG-4 Phase 1 split. This changes
            // the order mods are emitted vs the old single set; BH-D1 proves the change is §4.3-equivalent (independent
            // mods canonicalize, same-voxel writes stay ordered). The apply path stays serial/unchanged in
            // World.ApplyModifications. A single pooled scratch list is reused across both families.
            List<int> toRemove = ListPool<int>.Get();
            if (grassCount > 0)
            {
                using (s_tickGrassMarker.Auto())
                    TickFamily(grass, toRemove);
            }

            if (fluidCount > 0)
            {
                using (s_tickFluidMarker.Auto())
                {
                    // TG-4 Phase 3: tick Tier-1 interior fluids via the Burst job (border fluids stay managed),
                    // feature-flagged so the fully-managed legacy path is a one-toggle revert.
                    if (World.Instance.EnableFluidBurstTick)
                        TickFluidsHybrid(fluids, toRemove);
                    else
                        TickFamily(fluids, toRemove);
                }
            }

            ListPool<int>.Release(toRemove);
        }
    }

    /// <summary>
    /// Ticks one behavior-family bucket: evaluates <see cref="BlockBehavior.Behave"/>/<see cref="BlockBehavior.Active"/>
    /// for every registered voxel (unpacking its flat index to a local position), enqueues emitted mods to the world,
    /// and drops voxels that are no longer active. Removals are deferred until after enumeration (a
    /// <see cref="NativeHashSet{T}"/> cannot be modified mid-iteration); the apply pass runs later, so the bucket is
    /// not mutated by mod application during this loop.
    /// </summary>
    /// <param name="bucket">The per-family active-voxel set (flat chunk indices) owned by <see cref="ChunkData"/>.</param>
    /// <param name="removeScratch">A reusable scratch list for the now-inactive indices; cleared on entry.</param>
    private void TickFamily(NativeHashSet<int> bucket, List<int> removeScratch)
    {
        removeScratch.Clear();

        foreach (int idx in bucket)
        {
            ChunkMath.GetLocalPositionFromFlattenedIndex(idx, out int x, out int y, out int z);
            Vector3Int pos = new Vector3Int(x, y, z);

            // Get the list of modifications from the behavior logic.
            List<VoxelMod> mods = BlockBehavior.Behave(ChunkData, pos);

            // If the block is NO LONGER active, mark it for removal.
            // TODO: Future refactor could combine Behave and Active logic to save chunk lookups (TG-1).
            if (!BlockBehavior.Active(ChunkData, pos))
            {
                removeScratch.Add(idx);
            }

            // If the behavior produced any changes, submit them to the world's global queue.
            if (mods != null)
            {
                World.Instance.EnqueueVoxelModifications(mods);
            }
        }

        // Remove inactive voxels from the bucket in O(1) time each (deferred — see remarks).
        foreach (int idx in removeScratch)
        {
            bucket.Remove(idx);
        }
    }

    /// <summary>
    /// TG-4 Phase 3 — ticks the fluids family as a <b>hybrid</b>: Tier-1 interior voxels are evaluated by the Burst
    /// <see cref="FluidTickJob"/> (via <see cref="World.FluidBurstTicker"/>), Tier-2 border voxels stay on the
    /// managed <see cref="BlockBehavior.Behave"/>/<see cref="BlockBehavior.Active"/> path. The job runs first (over
    /// a pre-tick snapshot), then this method <b>replays the emitted mods in the original bucket-enumeration
    /// order</b>, interleaved with the managed border evaluations — so the emitted <see cref="VoxelMod"/> stream is
    /// byte-identical to the legacy single loop (zero drift; preserves same-target ordering for BH-D1). Removals
    /// (interior from the job, border from <see cref="BlockBehavior.Active"/>) are order-independent and applied
    /// after the loop, exactly as <see cref="TickFamily"/> does.
    /// </summary>
    /// <param name="fluids">The active-fluids bucket (flat chunk indices) owned by <see cref="ChunkData"/>.</param>
    /// <param name="removeScratch">A reusable scratch list for now-inactive indices; cleared on entry.</param>
    private void TickFluidsHybrid(NativeHashSet<int> fluids, List<int> removeScratch)
    {
        removeScratch.Clear();

        World world = World.Instance;
        FluidBurstTicker ticker = world.FluidBurstTicker;

        // Pass 1: snapshot + partition + run the interior fluid job (serial .Run). The border list is required by
        // the runner API but unused here — the replay below re-classifies in bucket order instead.
        List<int> borderScratch = ListPool<int>.Get();
        ticker.RunInteriorFluids(ChunkData, world.TickCounter, world.JobDataManager.BlockTypesJobData, borderScratch);
        ListPool<int>.Release(borderScratch);

        NativeList<VoxelMod> jobMods = ticker.Mods;
        NativeList<int> modsPerSource = ticker.ModsPerSource;

        // Pass 2: walk the bucket in the SAME enumeration order the runner used to build the interior set, so the
        // interior cursor stays in lockstep. Interior voxels emit their precomputed job-mod run; border voxels run
        // the managed path. This interleaves emission exactly as the legacy single loop would.
        int interiorCursor = 0;
        int modCursor = 0;
        foreach (int idx in fluids)
        {
            ChunkMath.GetLocalPositionFromFlattenedIndex(idx, out int x, out int y, out int z);

            if (FluidTierClassifier.IsTier1Interior(x, y, z))
            {
                int count = modsPerSource[interiorCursor];
                interiorCursor++;
                for (int k = 0; k < count; k++)
                    world.EnqueueVoxelModification(jobMods[modCursor + k]);
                modCursor += count;
            }
            else
            {
                Vector3Int pos = new Vector3Int(x, y, z);
                List<VoxelMod> mods = BlockBehavior.Behave(ChunkData, pos);
                if (!BlockBehavior.Active(ChunkData, pos))
                    removeScratch.Add(idx);
                if (mods != null)
                    world.EnqueueVoxelModifications(mods);
            }
        }

        // Interior now-inactive indices come from the job (order-independent set removal, like the border path).
        NativeList<int> inactive = ticker.InactiveInterior;
        foreach (int k in inactive)
            removeScratch.Add(k);

        foreach (int idx in removeScratch)
            fluids.Remove(idx);
    }

    /// <summary>
    /// Registers a voxel as active (delegates to <see cref="ChunkData"/>, which owns the per-family buckets). Used
    /// by the World's cross-chunk neighbor re-activation, which holds the neighbor <see cref="Chunk"/>.
    /// </summary>
    /// <param name="pos">The local position of the voxel within this chunk.</param>
    public void AddActiveVoxel(Vector3Int pos)
    {
        // Null-guarded like the sibling delegations (TickUpdate/GetActiveVoxelCount/IsVoxelActive/ActiveVoxels):
        // World's cross-chunk re-activation reaches here holding only the neighbor Chunk, whose ChunkData may be
        // unlinked mid-recycle. The old local-HashSet add could never NRE; preserve that.
        ChunkData?.AddActiveVoxel(pos);
    }

    /// <summary>
    /// Retrieves the total number of active voxels currently registered for ticking in this chunk (across all
    /// behavior families). Delegates to <see cref="ChunkData"/>.
    /// </summary>
    /// <returns>The count of active voxels, or 0 if no data is linked.</returns>
    public int GetActiveVoxelCount()
    {
        return ChunkData?.GetActiveVoxelCount() ?? 0;
    }

    #endregion

    public bool IsActive
    {
        get => _isActive;
        set
        {
            _isActive = value;
            if (ChunkGameObject != null)
            {
                ChunkGameObject.SetActive(value);
                if (value) PlayChunkLoadAnimation();
            }
        }
    }

    /// <summary>
    /// Converts a global world position into a local voxel position strictly within the bounds of this chunk.
    /// </summary>
    /// <param name="pos">The global world-space position.</param>
    /// <returns>The local 3D position of the voxel (0-15 on X and Z).</returns>
    /// <example><c>Global Pos (17.5f, 50f, -5f)</c> in Chunk at <c>(16, 0, -16)</c> -> <c>Local Pos (1, 50, 11)</c></example>
    public Vector3Int GetVoxelPositionInChunkFromGlobalVector3(Vector3 pos)
    {
        int xCheck = Mathf.FloorToInt(pos.x);
        int yCheck = Mathf.FloorToInt(pos.y);
        int zCheck = Mathf.FloorToInt(pos.z);

        xCheck -= Mathf.FloorToInt(ChunkPosition.x);
        zCheck -= Mathf.FloorToInt(ChunkPosition.z);

        return new Vector3Int(xCheck, yCheck, zCheck);
    }

    #region Mesh Generation

    /// <summary>
    /// Applies the completed mesh data output from the Burst Job System to the chunk's internal section renderers.
    /// Uses the advanced native mesh API to apply data seamlessly without GC allocations.
    /// </summary>
    /// <param name="meshData">The structured mesh data buffer produced by the <see cref="Jobs.MeshGenerationJob"/>.</param>
    public void ApplyMeshData(MeshDataJobOutput meshData)
    {
        // MR-5: the chunk-space → section-space rewrite + stream-3 interleave (MeshPostProcessJob) is no
        // longer run here. It is now chained onto the mesh job at schedule time in
        // WorldJobManager.ScheduleMeshing, so it has already run on a worker thread by the time
        // ProcessMeshJobs completes the handle and calls this method — ApplyMeshData only uploads buffers.

        // 1. Pass the data to the renderers using zero-allocation NativeArray views.
        NativeArray<MeshSectionStats> stats = meshData.SectionStats;

        // Obtain raw NativeArray views from the lists
        NativeArray<Vector3> allVerts = meshData.Vertices.AsArray();
        NativeArray<half4> allUvs = meshData.Uvs.AsArray(); // MR-2 half4: xy=flow/atlas, zw=shorePush
        NativeArray<Color32> allColors = meshData.Colors.AsArray();
        NativeArray<NormalLightVertex> allStream3 = meshData.InterleavedStream3.AsArray();
        NativeArray<int> allOpaqueTris = meshData.Triangles.AsArray();
        NativeArray<int> allTransTris = meshData.TransparentTriangles.AsArray();
        NativeArray<int> allFluidTris = meshData.FluidTriangles.AsArray();

        for (int i = 0; i < _sectionRenderers.Length; i++)
        {
            MeshSectionStats s = stats[i];

            if (s.VertexCount == 0)
            {
                // Pass empty data to clear mesh / disable object
                _sectionRenderers[i].UpdateMeshNative(
                    default, default, default, default, 0, 0,
                    default, 0, 0,
                    default, 0, 0,
                    default, 0, 0
                );
                continue;
            }

            _sectionRenderers[i].UpdateMeshNative(
                allVerts, allUvs, allColors, allStream3, s.VertexStartIndex, s.VertexCount,
                allOpaqueTris, s.OpaqueTriStartIndex, s.OpaqueTriCount,
                allTransTris, s.TransparentTriStartIndex, s.TransparentTriCount,
                allFluidTris, s.FluidTriStartIndex, s.FluidTriCount
            );
        }

        // MR-6: the mesh output's native memory is no longer released here. The buffers are uploaded
        // synchronously above (SetVertex/IndexBufferData copy), and WorldJobManager.ProcessMeshJobs
        // returns the output to its pool (or disposes it) immediately after this call — so the meshing
        // job's output buffers are pooled and reused instead of allocated/freed per chunk.

        // Add to the draw queue to be enabled on the main thread
        World.Instance.ChunksToDraw.Enqueue(this);
    }

    /// <summary>
    /// Finalizes the visual creation step by optionally triggering the chunk load animation.
    /// </summary>
    public void CreateMesh()
    {
        // The mesh is already assigned in ApplyMeshData.
        // This method could be used to enable the GameObject or an animation.
        PlayChunkLoadAnimation();
    }

    #endregion

    #region Public Getters

    /// <summary>
    /// Enumerates the active voxels in this chunk (local positions), across all behavior families. Delegates to
    /// <see cref="ChunkData"/>, which owns the buckets; empty when no data is linked.
    /// </summary>
    public IEnumerable<Vector3Int> ActiveVoxels =>
        ChunkData != null ? ChunkData.ActiveVoxels : Array.Empty<Vector3Int>();

    #endregion

    #region Debug Information Methods

    /// <summary>
    /// Checks if a voxel is active in this chunk. Delegates to <see cref="ChunkData"/>.
    /// </summary>
    /// <param name="localVoxelPos">The local position of the voxel in the given chunk.</param>
    /// <returns>True if the voxel is active, false otherwise.</returns>
    public bool IsVoxelActive(Vector3Int localVoxelPos)
    {
        return ChunkData != null && ChunkData.IsVoxelActive(localVoxelPos);
    }

    #endregion


    #region Bonus Stuff

    private void PlayChunkLoadAnimation()
    {
        if (_hasPlayedLoadAnimation) return;

        if (World.Instance.settings.enableChunkLoadAnimations && _loadAnimation != null)
        {
            _loadAnimation.enabled = true;
            _loadAnimation.StartAnimation();
        }
        else
        {
            // If animations are heavily disabled or toggled off mid-game, ensure chunk is snapped to correct position
            if (_loadAnimation != null) _loadAnimation.enabled = false;
            ChunkGameObject.transform.position = ChunkPosition;
        }

        _hasPlayedLoadAnimation = true;
    }

    #endregion
}
