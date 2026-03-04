using System.Collections.Generic;
using System.Linq;
using Data;
using Helpers;
using Jobs;
using Jobs.BurstData;
using Jobs.Data;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Pool;

public class WorldJobManager
{
    private readonly World _world;

    // --- Job Tracking Dictionaries ---
    public Dictionary<ChunkCoord, GenerationJobData> generationJobs { get; } = new Dictionary<ChunkCoord, GenerationJobData>();
    public Dictionary<ChunkCoord, (JobHandle handle, MeshDataJobOutput meshData)> meshJobs { get; } = new Dictionary<ChunkCoord, (JobHandle, MeshDataJobOutput)>();
    public Dictionary<ChunkCoord, LightingJobData> lightingJobs { get; } = new Dictionary<ChunkCoord, LightingJobData>();

    // --- Cached Collections for GC Optimization ---
    private readonly List<ChunkCoord> _completedGenJobs = new List<ChunkCoord>();
    private readonly List<ChunkCoord> _completedMeshJobs = new List<ChunkCoord>();
    private readonly List<ChunkCoord> _completedLightJobs = new List<ChunkCoord>();
    private readonly HashSet<ChunkCoord> _chunksToRebuildMesh = new HashSet<ChunkCoord>();
    private readonly Dictionary<ChunkCoord, HashSet<Vector2Int>> _droppedLightUpdates = new Dictionary<ChunkCoord, HashSet<Vector2Int>>();

    // --- Constructor ---
    /// <summary>
    /// Initializes a new instance of the <see cref="WorldJobManager"/> class.
    /// </summary>
    /// <param name="world">The main World instance that owns this manager.</param>
    public WorldJobManager(World world)
    {
        _world = world;
    }


    /// <summary>
    /// Schedules a background job to generate the voxel data (terrain and biome) for a specific chunk coordinate.
    /// If a job is already running or the data already exists, this method returns without scheduling.
    /// </summary>
    /// <param name="chunkCoord">The coordinate of the chunk to generate.</param>
    public void ScheduleGeneration(ChunkCoord chunkCoord)
    {
        // Don't schedule if a job is already running for it.
        if (generationJobs.ContainsKey(chunkCoord))
            return;

        Vector2Int chunkVoxelPos = chunkCoord.ToVoxelOrigin();

        // Don't schedule if chunk data already exists AND is already populated, in our main thread dictionary.
        if (_world.worldData.Chunks.TryGetValue(chunkVoxelPos, out ChunkData data) && data.IsPopulated)
        {
            return; // Data is already generated, no need to schedule.
        }

        // Allocate and track all data together
        var modificationsQueue = new NativeQueue<VoxelMod>(Allocator.Persistent);
        var outputMap = new NativeArray<uint>(VoxelData.ChunkWidth * VoxelData.ChunkHeight * VoxelData.ChunkWidth, Allocator.Persistent);
        var outputHeightMap = new NativeArray<byte>(VoxelData.ChunkWidth * VoxelData.ChunkWidth, Allocator.Persistent);

        ChunkGenerationJob job = new ChunkGenerationJob
        {
            Seed = VoxelData.Seed,
            ChunkPosition = chunkVoxelPos,
            BlockTypes = _world.JobDataManager.BlockTypesJobData,
            Biomes = _world.JobDataManager.BiomesJobData,
            AllLodes = _world.JobDataManager.AllLodesJobData,
            OutputMap = outputMap,
            OutputHeightMap = outputHeightMap,
            Modifications = modificationsQueue.AsParallelWriter(),
        };

        // Schedule the IJobFor using the new parallel scheduling method.
        // We run it for all 16x16 columns, with a batch size of 8 because each column is a heavy operation.
        JobHandle handle = job.ScheduleParallelByRef(VoxelData.ChunkWidth * VoxelData.ChunkWidth, 8, default);

        // Store the handle and ALL associated native containers together.
        // --- Prepare Data for the Job ---
        // Persistent data that the main thread needs to access after the job is done.
        GenerationJobData jobData = new GenerationJobData
        {
            Handle = handle,
            Map = outputMap,
            HeightMap = outputHeightMap,
            Mods = modificationsQueue,
        };

        generationJobs.Add(chunkCoord, jobData);
    }

    /// <summary>
    /// Attempts to schedule a mesh generation job for the specified chunk.
    /// Checks dependencies (neighbor data existence and lighting stability) before scheduling.
    /// </summary>
    /// <param name="chunk">The chunk to generate a mesh for.</param>
    /// <returns>True if the job was scheduled or was already running; false if dependencies were not met (e.g., waiting for lighting).</returns>
    public bool ScheduleMeshing(Chunk chunk)
    {
        ChunkCoord chunkCoord = chunk.Coord;

        if (meshJobs.ContainsKey(chunkCoord))
            return true; // Job is already scheduled, we can remove it from the build list.

        // Chunk's own lighting must be stable.
        if (chunk.ChunkData.HasLightChangesToProcess ||
            chunk.ChunkData.NeedsInitialLighting ||
            lightingJobs.ContainsKey(chunkCoord))
        {
            return false; // This chunk is still processing light, wait.
        }

        // All neighbors' data and LIGHTING must be stable.
        if (!_world.AreNeighborsReadyAndLit(chunkCoord))
        {
            return false; // A neighbor is generating or lighting, wait.
        }

        // 1. Prepare Section Data for CENTER chunk
        int sectionCount = chunk.ChunkData.sections.Length;
        var sectionData = new NativeArray<SectionJobData>(sectionCount, Allocator.Persistent);

        for (int i = 0; i < sectionCount; i++)
        {
            var s = chunk.ChunkData.sections[i];
            sectionData[i] = new SectionJobData
            {
                IsEmpty = s == null || s.IsEmpty,
                IsFullySolid = s != null && s.IsFullySolid,
            };
        }

        // 2. Allocate all input maps with Persistent. They will be disposed via background jobs.
        var map = _world.worldData.GetChunkMapForJob(chunkCoord.ToVoxelOrigin(), Allocator.Persistent);

        // Fetch all 8 neighbors for robust corner meshing
        // Cardinal Neighbors
        var back = _world.worldData.GetChunkMapForJob(chunkCoord.Neighbor(0, -1).ToVoxelOrigin(), Allocator.Persistent);
        var front = _world.worldData.GetChunkMapForJob(chunkCoord.Neighbor(0, 1).ToVoxelOrigin(), Allocator.Persistent);
        var left = _world.worldData.GetChunkMapForJob(chunkCoord.Neighbor(-1, 0).ToVoxelOrigin(), Allocator.Persistent);
        var right = _world.worldData.GetChunkMapForJob(chunkCoord.Neighbor(1, 0).ToVoxelOrigin(), Allocator.Persistent);
        // Diagonal Neighbors
        var frontRight = _world.worldData.GetChunkMapForJob(chunkCoord.Neighbor(1, 1).ToVoxelOrigin(), Allocator.Persistent);
        var backRight = _world.worldData.GetChunkMapForJob(chunkCoord.Neighbor(1, -1).ToVoxelOrigin(), Allocator.Persistent);
        var backLeft = _world.worldData.GetChunkMapForJob(chunkCoord.Neighbor(-1, -1).ToVoxelOrigin(), Allocator.Persistent);
        var frontLeft = _world.worldData.GetChunkMapForJob(chunkCoord.Neighbor(-1, 1).ToVoxelOrigin(), Allocator.Persistent);

        // The output data must be persistent, as it lives until processed on the main thread.
        MeshDataJobOutput meshOutput = new MeshDataJobOutput(Allocator.Persistent);

        MeshGenerationJob job = new MeshGenerationJob
        {
            // --- Input data ---
            Map = map,
            SectionData = sectionData,

            BlockTypes = _world.JobDataManager.BlockTypesJobData,
            ChunkPosition = chunk.ChunkPosition,
            // Cardinal neighbors
            NeighborBack = back,
            NeighborFront = front,
            NeighborLeft = left,
            NeighborRight = right,
            // Diagonal neighbors
            NeighborFrontRight = frontRight,
            NeighborBackRight = backRight,
            NeighborBackLeft = backLeft,
            NeighborFrontLeft = frontLeft,
            // Custom data
            CustomMeshes = _world.JobDataManager.CustomMeshesJobData,
            CustomFaces = _world.JobDataManager.CustomFacesJobData,
            CustomVerts = _world.JobDataManager.CustomVertsJobData,
            CustomTris = _world.JobDataManager.CustomTrisJobData,
            WaterVertexTemplates = _world.FluidVertexTemplates.WaterVertexTemplates,
            LavaVertexTemplates = _world.FluidVertexTemplates.LavaVertexTemplates,
            // --- Output ----
            Output = meshOutput,
        };

        // 3. Schedule the main mesh generation job. It has no dependencies yet.
        JobHandle meshJobHandle = job.Schedule();

        // 4. Create a NativeArray to hold all the disposal handles, and make them dependent on the main job's handle.
        //    This means "Don't dispose of 'map' until 'meshJobHandle' is complete".
        var disposalHandles = new NativeArray<JobHandle>(10, Allocator.Persistent);
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

        // 5. Combine all the disposal handles into a single final handle.
        //    This handle will be complete only after the mesh job AND all its input disposal jobs are done.
        JobHandle combinedDisposalHandle = JobHandle.CombineDependencies(disposalHandles);
        JobHandle finalHandle = disposalHandles.Dispose(combinedDisposalHandle);

        // 6. Store this final, all-encompassing handle in our tracking dictionary.
        meshJobs.Add(chunkCoord, (finalHandle, meshOutput));

        return true;
    }

    /// <summary>
    /// Schedules a neighborhood lighting job to propagate sunlight and blocklight changes.
    /// Manages the allocation of persistent input data required for the job.
    /// </summary>
    /// <param name="chunkData">The central chunk data object.</param>
    /// <param name="allocator">The allocator to use for job data (Allocator.TempJob for startup, Allocator.Persistent for runtime).</param>
    public void ScheduleLightingUpdate(ChunkData chunkData, Allocator allocator = Allocator.Persistent)
    {
        ChunkCoord chunkCoord = ChunkCoord.FromVoxelOrigin(chunkData.position);
        if (lightingJobs.ContainsKey(chunkCoord)) return; // Job already running for this chunk

        // Do not schedule a lighting job until all neighbors have finished generating their data.
        if (!_world.AreNeighborsDataReady(chunkCoord))
        {
            // We can't schedule it now. Mark it so we can try again on the next frame.
            chunkData.HasLightChangesToProcess = true;
            return;
        }

        // --- Prepare Data for the Job ---

        // --- 1. ALLOCATE INPUT DATA ---
        // Use the passed allocator (TempJob for startup, Persistent for runtime)
        var inputData = new LightingJobInputData();

        // Get all 8 Neighbor Maps and the heightmap (Read-Only, disposed by job dependency)
        inputData.Heightmap = new NativeArray<byte>(chunkData.heightMap, allocator);
        // Cardinal Neighbors
        inputData.NeighborN = _world.worldData.GetChunkMapForJob(chunkCoord.Neighbor(0, 1).ToVoxelOrigin(), allocator);
        inputData.NeighborE = _world.worldData.GetChunkMapForJob(chunkCoord.Neighbor(1, 0).ToVoxelOrigin(), allocator);
        inputData.NeighborS = _world.worldData.GetChunkMapForJob(chunkCoord.Neighbor(0, -1).ToVoxelOrigin(), allocator);
        inputData.NeighborW = _world.worldData.GetChunkMapForJob(chunkCoord.Neighbor(-1, 0).ToVoxelOrigin(), allocator);
        // Diagonal Neighbors
        inputData.NeighborNE = _world.worldData.GetChunkMapForJob(chunkCoord.Neighbor(1, 1).ToVoxelOrigin(), allocator);
        inputData.NeighborSE = _world.worldData.GetChunkMapForJob(chunkCoord.Neighbor(1, -1).ToVoxelOrigin(), allocator);
        inputData.NeighborSW = _world.worldData.GetChunkMapForJob(chunkCoord.Neighbor(-1, -1).ToVoxelOrigin(), allocator);
        inputData.NeighborNW = _world.worldData.GetChunkMapForJob(chunkCoord.Neighbor(-1, 1).ToVoxelOrigin(), allocator);

        // --- 2. ALLOCATE OUTPUT DATA ---
        var jobData = new LightingJobData
        {
            Input = inputData,
            // The output arrays also use the faster allocator
            Map = _world.worldData.GetChunkMapForJob(chunkData.position, allocator),
            Mods = new NativeList<LightModification>(allocator),
            IsStable = new NativeArray<bool>(1, allocator),

            // Note: These queues come from ChunkData. internal copies must match the allocator life cycle
            SunLightQueue = chunkData.GetSunlightQueueForJob(allocator),
            BlockLightQueue = chunkData.GetBlocklightQueueForJob(allocator),
            SunLightRecalcQueue = new NativeQueue<Vector2Int>(allocator),
        };

        // Consume sunlight recalculation requests from the global queue for this chunk
        // Optimized: Direct bucket lookup
        if (_world.worldData.SunlightRecalculationQueue.TryGetValue(chunkData.position, out HashSet<Vector2Int> columns))
        {
            foreach (Vector2Int col in columns)
            {
                // Convert global column position to local and add to the job's queue.
                jobData.SunLightRecalcQueue.Enqueue(new Vector2Int(col.x - chunkData.position.x, col.y - chunkData.position.y));
            }

            // After iterating, remove the entire bucket for this chunk as we've consumed the requests.
            _world.worldData.SunlightRecalculationQueue.Remove(chunkData.position);

            // OPTIMIZATION: Release fully consumed queue back to the pool
            HashSetPool<Vector2Int>.Release(columns);
        }

        NeighborhoodLightingJob job = new NeighborhoodLightingJob
        {
            // Writable data for the central chunk
            Map = jobData.Map,
            ChunkPosition = chunkData.position,
            SunlightBfsQueue = jobData.SunLightQueue,
            BlocklightBfsQueue = jobData.BlockLightQueue,
            SunlightColumnRecalcQueue = jobData.SunLightRecalcQueue,

            // Read-only heightmap & neighbor data
            Heightmap = jobData.Input.Heightmap,
            NeighborN = jobData.Input.NeighborN, NeighborE = jobData.Input.NeighborE,
            NeighborS = jobData.Input.NeighborS, NeighborW = jobData.Input.NeighborW,
            NeighborNE = jobData.Input.NeighborNE, NeighborSE = jobData.Input.NeighborSE,
            NeighborSW = jobData.Input.NeighborSW, NeighborNW = jobData.Input.NeighborNW,

            BlockTypes = _world.JobDataManager.BlockTypesJobData,

            // Output lists
            CrossChunkLightMods = jobData.Mods,
            IsStable = jobData.IsStable,
        };

        // Schedule the job.
        jobData.Handle = job.Schedule();

        // Reset the flag, because we are now scheduling a job to process these changes.
        chunkData.HasLightChangesToProcess = false;

        // Store the handle and all persistent data (input and output) that needs to be processed and disposed of later.
        lightingJobs.Add(chunkCoord, jobData);
    }

    /// <summary>
    /// Helper overload for Chunk objects. Forwards to the ChunkData-based implementation.
    /// </summary>
    /// <param name="chunk">The chunk object.</param>
    /// <param name="allocator">The allocator to use for job data (Allocator.TempJob for startup, Allocator.Persistent for runtime).</param>
    public void ScheduleLightingUpdate(Chunk chunk, Allocator allocator = Allocator.Persistent)
    {
        ScheduleLightingUpdate(chunk.ChunkData, allocator);
    }

    /// <summary>
    /// Checks for completed generation jobs, populates chunk data, generates structures,
    /// and flags chunks for their initial lighting pass.
    /// </summary>
    public void ProcessGenerationJobs()
    {
        // Clear the temp list, we use this list to avoid modifying dictionary while iterating
        _completedGenJobs.Clear();
        foreach (var jobEntry in generationJobs)
        {
            // Only continue if the generation job has completed
            if (jobEntry.Value.Handle.IsCompleted)
            {
                // Complete the job to ensure it's finished and sync memory
                jobEntry.Value.Handle.Complete();

                // --- STAGE 1: Populate with base terrain ---
                ChunkData chunkData = _world.worldData.RequestChunk(jobEntry.Key.ToVoxelOrigin(), true);
                chunkData.Populate(jobEntry.Value.Map, jobEntry.Value.HeightMap);
                chunkData.Chunk?.OnDataPopulated();

                // --- STAGE 2: Apply generated modifications (trees, etc.) ---
                while (jobEntry.Value.Mods.TryDequeue(out VoxelMod mod))
                {
                    // Directly pass the IEnumerable into the world queue, avoiding any intermediate array/list allocations.
                    IEnumerable<VoxelMod> floraMods = Structure.GenerateMajorFlora(mod.ID, mod.GlobalPosition, _world.biomes[0].minHeight, _world.biomes[0].maxHeight);
                    _world.EnqueueVoxelModifications(floraMods);
                }

                // Check if any neighbors (or previous sessions) left pending mods for THIS chunk while it was unloaded.
                if (_world.ModManager.TryGetModsForChunk(jobEntry.Key, out List<VoxelMod> pendingMods))
                {
                    // We apply these directly to the data now, as the chunk is populated but not yet meshed.
                    foreach (VoxelMod mod in pendingMods)
                    {
                        Vector3Int localVoxelPos = _world.worldData.GetLocalVoxelPositionInChunk(mod.GlobalPosition);
                        chunkData.ModifyVoxel(localVoxelPos, mod);
                    }
                }

                // Check for pending lighting updates for this chunk
                if (_world.LightingStateManager.TryGetAndRemove(jobEntry.Key, out HashSet<Vector2Int> localLightCols))
                {
                    // Convert Local columns to Global Columns before adding to the queue!
                    HashSet<Vector2Int> globalLightCols = HashSetPool<Vector2Int>.Get(); // POOLING
                    foreach (var lCol in localLightCols)
                    {
                        globalLightCols.Add(new Vector2Int(lCol.x + chunkData.position.x, lCol.y + chunkData.position.y));
                    }

                    // We took ownership from TryGetAndRemove, so we must release it now!
                    HashSetPool<Vector2Int>.Release(localLightCols);

                    // Fix the Memory Leak: Explicitly handle the TryGetValue scenario
                    if (_world.worldData.SunlightRecalculationQueue.TryGetValue(chunkData.position, out var existingQueue))
                    {
                        existingQueue.UnionWith(globalLightCols);
                        // The temp set is now redundant, release it!
                        HashSetPool<Vector2Int>.Release(globalLightCols);
                    }
                    else
                    {
                        // Transfer ownership to the dictionary
                        _world.worldData.SunlightRecalculationQueue[chunkData.position] = globalLightCols;
                    }

                    chunkData.HasLightChangesToProcess = true;
                }

                // Dispose of the job's data here, AFTER it has been used
                jobEntry.Value.Dispose();

                // --- STAGE 3: Lighting ---
                if (_world.settings.enableLighting)
                {
                    // Set the flag to indicate that the chunk needs initial lighting.
                    chunkData.NeedsInitialLighting = true;
                }
                else
                {
                    // If lighting is off, set all blocks to full brightness.
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

                _completedGenJobs.Add(jobEntry.Key);

                // Now that data is fully ready and lit, the chunk can have its mesh generated.
                Chunk chunk = _world.GetChunkFromChunkCoord(jobEntry.Key);
                if (chunk != null && chunk.isActive)
                {
                    _world.RequestChunkMeshRebuild(chunk);
                }
            }
        }

        // Remove the completed jobs from our tracking dictionary
        foreach (ChunkCoord chunkCoord in _completedGenJobs)
        {
            generationJobs.Remove(chunkCoord);
        }
    }

    /// <summary>
    /// Checks for completed mesh generation jobs and applies the resulting mesh data to the chunk GameObjects.
    /// </summary>
    public void ProcessMeshJobs()
    {
        // Clear the temp list, we this list to avoid modifying dictionary while iterating
        _completedMeshJobs.Clear();
        foreach (var jobEntry in meshJobs)
        {
            if (jobEntry.Value.handle.IsCompleted)
            {
                jobEntry.Value.handle.Complete();

                Chunk chunk = _world.GetChunkFromChunkCoord(jobEntry.Key);
                if (chunk != null)
                {
                    // ApplyMeshData will handle disposing of the NativeLists inside MeshDataJobOutput
                    chunk.ApplyMeshData(jobEntry.Value.meshData);
                }
                else
                {
                    // If chunk is null, we must still dispose the data
                    jobEntry.Value.meshData.Dispose();
                }

                _completedMeshJobs.Add(jobEntry.Key);
            }
        }

        foreach (ChunkCoord chunkCoord in _completedMeshJobs)
        {
            meshJobs.Remove(chunkCoord);
        }
    }

    /// <summary>
    /// Checks for completed lighting jobs, applies light changes to chunk data,
    /// processes cross-chunk light modifications, and triggers mesh rebuilds for affected chunks.
    /// </summary>
    public void ProcessLightingJobs()
    {
        if (lightingJobs.Count == 0) return;

        // Use a HashSet to track which world.chunks need a mesh rebuild this frame.
        // Clear cached collections instead of making new ones
        _chunksToRebuildMesh.Clear();
        _completedLightJobs.Clear();

        // OPTIMIZATION: Cache for dropped updates to avoid calling LightingStateManager (and allocating HashSets) per voxel.
        // OPTIMIZATION: Use Unity's Global HashSetPool
        // Key: Neighbor Chunk, Value: Set of columns
        foreach (var set in _droppedLightUpdates.Values)
        {
            // Release returns it to the global pool AND automatically calls set.Clear()
            HashSetPool<Vector2Int>.Release(set);
        }

        _droppedLightUpdates.Clear();

        foreach (var jobEntry in lightingJobs)
        {
            if (jobEntry.Value.Handle.IsCompleted)
            {
                jobEntry.Value.Handle.Complete();
                LightingJobData jobData = jobEntry.Value;

                ChunkData chunkData = _world.worldData.RequestChunk(jobEntry.Key.ToVoxelOrigin(), false);

                // Flag this chunk to prevent neighbors from meshing until its results are fully processed.
                if (chunkData != null)
                {
                    chunkData.IsAwaitingMainThreadProcess = true;
                }

                bool isChunkStable = jobData.IsStable[0];

                if (chunkData != null && chunkData.IsPopulated)
                {
                    // 1. Merge ONLY light bits to prevent overwriting player modifications (TOCTOU fix)
                    ApplyLightingJobResult(chunkData, jobData.Map);

                    // 2. Process cross-chunk modifications calculated by the job.
                    foreach (LightModification mod in jobData.Mods)
                    {
                        // Find the chunk that this modification affects.
                        Vector2Int neighborChunkVoxelPos = _world.worldData.GetChunkCoordFor(mod.GlobalPosition);
                        ChunkData neighborChunk = _world.worldData.RequestChunk(neighborChunkVoxelPos, false);

                        // If the neighbor doesn't exist or isn't generated, save any propagating lighting for that unloaded neighbor
                        if (neighborChunk == null || !neighborChunk.IsPopulated)
                        {
                            // Calculate Chunk Coord
                            ChunkCoord neighborChunkCoord = ChunkCoord.FromVoxelOrigin(neighborChunkVoxelPos);

                            // Calculate Local Column
                            int localX = mod.GlobalPosition.x - neighborChunkVoxelPos.x;
                            int localZ = mod.GlobalPosition.z - neighborChunkVoxelPos.y;

                            // Validate range
                            if (localX < 0 || localX >= VoxelData.ChunkWidth ||
                                localZ < 0 || localZ >= VoxelData.ChunkWidth)
                            {
                                Debug.LogError($"[ProcessLightingJobs] Invalid local column calculation: ({localX}, {localZ}) for global pos {mod.GlobalPosition}");
                                continue;
                            }

                            // Add to local batch dictionary instead of immediate manager call
                            if (!_droppedLightUpdates.TryGetValue(neighborChunkCoord, out HashSet<Vector2Int> cols))
                            {
                                cols = HashSetPool<Vector2Int>.Get();
                                _droppedLightUpdates[neighborChunkCoord] = cols;
                            }

                            cols.Add(new Vector2Int(localX, localZ));
                            continue;
                        }

                        // Get the local position and flat array index for the voxel in the neighbor chunk.
                        Vector3Int localVoxelPos = _world.worldData.GetLocalVoxelPositionInChunk(mod.GlobalPosition);

                        uint oldPackedData = neighborChunk.GetVoxel(localVoxelPos.x, localVoxelPos.y, localVoxelPos.z);
                        byte oldLightLevel;
                        uint newPackedData;

                        // Determine which light channel to modify based on the modification request.
                        if (mod.Channel == LightChannel.Sun)
                        {
                            byte currentSunlight = BurstVoxelDataBitMapping.GetSunLight(oldPackedData);

                            // If the block already has full sunlight (15), and this modification would decrease it, ignore the modification.
                            // This prevents a job using stale data from overwriting a correct value set by another job.
                            if (currentSunlight == 15 && mod.LightLevel < 15)
                            {
                                continue; // Skip this modification
                            }

                            oldLightLevel = BurstVoxelDataBitMapping.GetSunLight(oldPackedData);
                            newPackedData = BurstVoxelDataBitMapping.SetSunLight(oldPackedData, mod.LightLevel);
                        }
                        else // Blocklight
                        {
                            oldLightLevel = BurstVoxelDataBitMapping.GetBlockLight(oldPackedData);
                            newPackedData = BurstVoxelDataBitMapping.SetBlockLight(oldPackedData, mod.LightLevel);
                        }

                        // Only proceed if the light level is actually changing to avoid redundant work.
                        if (oldLightLevel != mod.LightLevel)
                        {
                            // 1. Apply the new light value directly to the neighbor's chunk data.
                            neighborChunk.SetVoxel(localVoxelPos.x, localVoxelPos.y, localVoxelPos.z, newPackedData);

                            // 2. Queue an update for the neighbor's *next* lighting pass.
                            //    This correctly seeds the propagation algorithm for the neighbor.
                            if (mod.Channel == LightChannel.Sun)
                                neighborChunk.AddToSunLightQueue(localVoxelPos, oldLightLevel);
                            else
                                neighborChunk.AddToBlockLightQueue(localVoxelPos, oldLightLevel);

                            // 3. Mark the neighbor chunk for a mesh rebuild, as its lighting has changed.
                            _chunksToRebuildMesh.Add(ChunkCoord.FromVoxelOrigin(neighborChunk.position));
                        }
                    }
                }

                // 3. Check if the central chunk's lighting has stabilized.
                if (isChunkStable)
                {
                    // The chunk is stable! It's now safe to request a mesh rebuild.
                    _chunksToRebuildMesh.Add(jobEntry.Key);
                    // ALSO queue neighbors for a mesh rebuild, as their appearance may have changed.
                    _world.RequestNeighborMeshRebuilds(jobEntry.Key);
                }
                else
                {
                    // The chunk is NOT stable. It needs another lighting pass.
                    // We re-assert the flag to ensure the scheduler picks it up next frame.
                    if (chunkData != null) chunkData.HasLightChangesToProcess = true;
                }

                // All results for this chunk have been processed and propagated.
                // It is now safe for neighbors to consider this chunk's data stable for meshing.
                if (chunkData != null) chunkData.IsAwaitingMainThreadProcess = false;

                // 4. Dispose of all the job's persistent data.
                jobData.Dispose();

                _completedLightJobs.Add(jobEntry.Key);
            }
        }

        // 5. Save vanishing neighbor updates (BATCH)
        foreach (var kvp in _droppedLightUpdates)
        {
            if (kvp.Value.Count > 0) // Only add if we actually put data in it this frame
            {
                _world.LightingStateManager.AddPending(kvp.Key, kvp.Value);
            }
        }

        // Log summary
        if (_droppedLightUpdates.Count > 0)
        {
            int totalColumns = _droppedLightUpdates.Values.Sum(set => set.Count);
            Debug.Log($"[LIGHTING] Processed {_completedLightJobs.Count} jobs. Saved updates for {_droppedLightUpdates.Count} unloaded chunks ({totalColumns} columns)");
        }

        // 6. After processing all completed jobs, request mesh rebuilds for all affected world.chunks.
        foreach (ChunkCoord chunkCoord in _chunksToRebuildMesh)
        {
            Chunk chunk = _world.GetChunkFromChunkCoord(chunkCoord);
            if (chunk != null)
            {
                _world.RequestChunkMeshRebuild(chunk, immediate: true);
            }
        }


        // 7. Remove the completed jobs from our tracking dictionary
        foreach (ChunkCoord chunkCoord in _completedLightJobs)
        {
            lightingJobs.Remove(chunkCoord);
        }
    }

    /// <summary>
    /// Merges the lighting results from a background job into the live ChunkData.
    /// CRITICAL: This performs a bit-mask merge (only light bits) to avoid overwriting
    /// block changes (TOCTOU) made by the player while the job was running.
    /// </summary>
    /// <param name="chunkData">The target chunk data object.</param>
    /// <param name="jobMap">The raw voxel data from the background job.</param>
    private void ApplyLightingJobResult(ChunkData chunkData, NativeArray<uint> jobMap)
    {
        int indexOffset = 0;
        int sectionVolume = ChunkMath.SECTION_VOLUME; // Cache for slight perf

        for (int s = 0; s < chunkData.sections.Length; s++)
        {
            ChunkSection section = chunkData.sections[s];
            bool sectionHasData = false;

            // 1. If the live section is null, check if the job has light data for this area.
            // If the job says there is light here, we must create a section to hold it.
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
                }
            }

            // 2. If we have a section (existing or just created), merge the light bits.
            if (section != null)
            {
                for (int i = 0; i < sectionVolume; i++)
                {
                    uint liveData = section.voxels[i];
                    uint jobVoxel = jobMap[indexOffset + i];

                    // Extract ONLY the calculated light levels from the background job result
                    byte jobSunlight = BurstVoxelDataBitMapping.GetSunLight(jobVoxel);
                    byte jobBlocklight = BurstVoxelDataBitMapping.GetBlockLight(jobVoxel);

                    // Apply them to the current LIVE terrain data (preserving Block ID)
                    liveData = BurstVoxelDataBitMapping.SetSunLight(liveData, jobSunlight);
                    liveData = BurstVoxelDataBitMapping.SetBlockLight(liveData, jobBlocklight);

                    section.voxels[i] = liveData; // Write back

                    if (liveData != 0) sectionHasData = true;
                }

                // 3. Cleanup: If the section became empty (air + dark), pool it.
                if (!sectionHasData)
                {
                    _world.ChunkPool.ReturnChunkSection(section);
                    chunkData.sections[s] = null; // Clear array slot
                }
                else
                {
                    // Ensure opaque counts are updated since we modified voxel data
                    section.RecalculateCounts(_world.blockTypes);
                }
            }

            indexOffset += sectionVolume;
        }
    }
}
