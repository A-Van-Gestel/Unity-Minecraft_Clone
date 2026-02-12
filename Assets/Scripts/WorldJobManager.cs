using System.Collections.Generic;
using Data;
using Jobs;
using Jobs.BurstData;
using Jobs.Data;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

public class WorldJobManager
{
    private readonly World _world;

    // --- Job Tracking Dictionaries ---
    public Dictionary<ChunkCoord, GenerationJobData> generationJobs { get; } = new Dictionary<ChunkCoord, GenerationJobData>();
    public Dictionary<ChunkCoord, (JobHandle handle, MeshDataJobOutput meshData)> meshJobs { get; } = new Dictionary<ChunkCoord, (JobHandle, MeshDataJobOutput)>();
    public Dictionary<ChunkCoord, LightingJobData> lightingJobs { get; } = new Dictionary<ChunkCoord, LightingJobData>();

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
    /// <param name="coord">The coordinate of the chunk to generate.</param>
    public void ScheduleGeneration(ChunkCoord coord)
    {
        // Don't schedule if a job is already running for it.
        if (generationJobs.ContainsKey(coord))
            return;

        Vector2Int chunkPos = new Vector2Int(coord.X * VoxelData.ChunkWidth, coord.Z * VoxelData.ChunkWidth);

        // Don't schedule if chunk data already exists AND is already populated, in our main thread dictionary.
        if (_world.worldData.Chunks.TryGetValue(chunkPos, out ChunkData data) && data.IsPopulated)
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
            ChunkPosition = chunkPos,
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

        generationJobs.Add(coord, jobData);
    }

    /// <summary>
    /// Attempts to schedule a mesh generation job for the specified chunk.
    /// Checks dependencies (neighbor data existence and lighting stability) before scheduling.
    /// </summary>
    /// <param name="chunk">The chunk to generate a mesh for.</param>
    /// <returns>True if the job was scheduled or was already running; false if dependencies were not met (e.g., waiting for lighting).</returns>
    public bool ScheduleMeshing(Chunk chunk)
    {
        ChunkCoord coord = chunk.Coord;

        if (meshJobs.ContainsKey(coord))
            return true; // Job is already scheduled, we can remove it from the build list.

        // Chunk's own lighting must be stable.
        if (chunk.ChunkData.HasLightChangesToProcess || lightingJobs.ContainsKey(coord))
        {
            return false; // This chunk is still processing light, wait.
        }

        // All neighbors' data and LIGHTING must be stable.
        if (!_world.AreNeighborsReadyAndLit(coord))
        {
            return false; // A neighbor is generating or lighting, wait.
        }

        // 1. Prepare Section Data for CENTER chunk
        int sectionCount = chunk.ChunkData.sections.Length;
        var sectionData = new NativeArray<SectionJobData>(sectionCount, Allocator.TempJob);

        for (int i = 0; i < sectionCount; i++)
        {
            var s = chunk.ChunkData.sections[i];
            sectionData[i] = new SectionJobData
            {
                IsEmpty = s == null || s.IsEmpty,
                IsFullySolid = s != null && s.IsFullySolid,
            };
        }

        // 2. Allocate all input maps with TempJob. They will be used and then disposed.
        var map = _world.worldData.GetChunkMapForJob(new Vector2Int(coord.X * VoxelData.ChunkWidth, coord.Z * VoxelData.ChunkWidth), Allocator.TempJob);

        // Fetch all 8 neighbors for robust corner meshing
        Vector2Int p = new Vector2Int(coord.X * VoxelData.ChunkWidth, coord.Z * VoxelData.ChunkWidth);
        const int w = VoxelData.ChunkWidth;

        // Cardinal Neighbors
        var back = _world.worldData.GetChunkMapForJob(p + new Vector2Int(0, -w), Allocator.TempJob);
        var front = _world.worldData.GetChunkMapForJob(p + new Vector2Int(0, w), Allocator.TempJob);
        var left = _world.worldData.GetChunkMapForJob(p + new Vector2Int(-w, 0), Allocator.TempJob);
        var right = _world.worldData.GetChunkMapForJob(p + new Vector2Int(w, 0), Allocator.TempJob);
        // Diagonal Neighbors
        var frontRight = _world.worldData.GetChunkMapForJob(p + new Vector2Int(w, w), Allocator.TempJob);
        var backRight = _world.worldData.GetChunkMapForJob(p + new Vector2Int(w, -w), Allocator.TempJob);
        var backLeft = _world.worldData.GetChunkMapForJob(p + new Vector2Int(-w, -w), Allocator.TempJob);
        var frontLeft = _world.worldData.GetChunkMapForJob(p + new Vector2Int(-w, w), Allocator.TempJob);

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
        var disposalHandles = new NativeArray<JobHandle>(10, Allocator.TempJob);
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
        meshJobs.Add(coord, (finalHandle, meshOutput));

        return true;
    }

    /// <summary>
    /// Schedules a neighborhood lighting job to propagate sunlight and blocklight changes.
    /// Manages the allocation of persistent input data required for the job.
    /// </summary>
    /// <param name="chunk">The central chunk to update lighting for.</param>
    /// <param name="allocator">The allocator to use for job data (Allocator.TempJob for startup, Allocator.Persistent for runtime).</param>
    public void ScheduleLightingUpdate(Chunk chunk, Allocator allocator = Allocator.Persistent)
    {
        ChunkCoord coord = chunk.Coord;
        if (lightingJobs.ContainsKey(coord)) return; // Job already running for this chunk

        // Do not schedule a lighting job until all neighbors have finished generating their data.
        if (!_world.AreNeighborsDataReady(coord))
        {
            // We can't schedule it now. Mark it so we can try again on the next frame.
            chunk.ChunkData.HasLightChangesToProcess = true;
            return;
        }

        // --- Prepare Data for the Job ---

        // --- 1. ALLOCATE INPUT DATA ---
        // Use the passed allocator (TempJob for startup, Persistent for runtime)
        var inputData = new LightingJobInputData();

        // Get all 8 Neighbor Maps and the heightmap (Read-Only, disposed by job dependency)
        Vector2Int p = chunk.ChunkData.position;
        const int w = VoxelData.ChunkWidth;

        inputData.Heightmap = new NativeArray<byte>(chunk.ChunkData.heightMap, allocator);
        // Cardinal Neighbors
        inputData.NeighborN = _world.worldData.GetChunkMapForJob(p + new Vector2Int(0, w), allocator);
        inputData.NeighborE = _world.worldData.GetChunkMapForJob(p + new Vector2Int(w, 0), allocator);
        inputData.NeighborS = _world.worldData.GetChunkMapForJob(p + new Vector2Int(0, -w), allocator);
        inputData.NeighborW = _world.worldData.GetChunkMapForJob(p + new Vector2Int(-w, 0), allocator);
        // Diagonal Neighbors
        inputData.NeighborNE = _world.worldData.GetChunkMapForJob(p + new Vector2Int(w, w), allocator);
        inputData.NeighborSE = _world.worldData.GetChunkMapForJob(p + new Vector2Int(w, -w), allocator);
        inputData.NeighborSW = _world.worldData.GetChunkMapForJob(p + new Vector2Int(-w, -w), allocator);
        inputData.NeighborNW = _world.worldData.GetChunkMapForJob(p + new Vector2Int(-w, w), allocator);

        // --- 2. ALLOCATE OUTPUT DATA ---
        var jobData = new LightingJobData
        {
            Input = inputData,
            // The output arrays also use the faster allocator
            Map = _world.worldData.GetChunkMapForJob(p, allocator),
            Mods = new NativeList<LightModification>(allocator),
            IsStable = new NativeArray<bool>(1, allocator),

            // Note: These queues come from ChunkData. internal copies must match the allocator life cycle
            SunLightQueue = chunk.ChunkData.GetSunlightQueueForJob(allocator),
            BlockLightQueue = chunk.ChunkData.GetBlocklightQueueForJob(allocator),
            SunLightRecalcQueue = new NativeQueue<Vector2Int>(allocator),
        };

        // Consume sunlight recalculation requests from the global queue for this chunk
        // Optimized: Direct bucket lookup instead of global iteration
        if (_world.worldData.SunlightRecalculationQueue.TryGetValue(chunk.ChunkData.position, out HashSet<Vector2Int> columns))
        {
            foreach (Vector2Int col in columns)
            {
                // Convert global column position to local and add to the job's queue.
                jobData.SunLightRecalcQueue.Enqueue(new Vector2Int(col.x - chunk.ChunkData.position.x, col.y - chunk.ChunkData.position.y));
            }

            // After iterating, remove the entire bucket for this chunk as we've consumed the requests.
            _world.worldData.SunlightRecalculationQueue.Remove(chunk.ChunkData.position);
        }

        NeighborhoodLightingJob job = new NeighborhoodLightingJob
        {
            // Writable data for the central chunk
            Map = jobData.Map,
            ChunkPosition = chunk.ChunkData.position,
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
        chunk.ChunkData.HasLightChangesToProcess = false;

        // Store the handle and all persistent data (input and output) that needs to be processed and disposed of later.
        lightingJobs.Add(coord, jobData);
    }

    /// <summary>
    /// Checks for completed generation jobs, populates chunk data, generates structures,
    /// and flags chunks for their initial lighting pass.
    /// </summary>
    public void ProcessGenerationJobs()
    {
        // Using a temporary list to avoid modifying dictionary while iterating
        List<ChunkCoord> completedCoords = new List<ChunkCoord>();
        foreach (var jobEntry in generationJobs)
        {
            // Only continue if the generation job has completed
            if (jobEntry.Value.Handle.IsCompleted)
            {
                // Complete the job to ensure it's finished and sync memory
                jobEntry.Value.Handle.Complete();

                // --- STAGE 1: Populate with base terrain ---
                ChunkData chunkData = _world.worldData.RequestChunk(new Vector2Int(jobEntry.Key.X * VoxelData.ChunkWidth, jobEntry.Key.Z * VoxelData.ChunkWidth), true);
                chunkData.Populate(jobEntry.Value.Map, jobEntry.Value.HeightMap);
                chunkData.Chunk?.OnDataPopulated();

                // --- STAGE 2: Apply generated modifications (trees, etc.) ---
                Queue<VoxelMod> structureMods = new Queue<VoxelMod>();
                while (jobEntry.Value.Mods.TryDequeue(out VoxelMod mod))
                {
                    // The VoxelMod from the job just contains the request type and position.
                    // The main thread expands this into the full structure queue.
                    Queue<VoxelMod> structureQueue = Structure.GenerateMajorFlora(mod.ID, mod.GlobalPosition, _world.biomes[0].minHeight, _world.biomes[0].maxHeight);
                    // We add these to a temporary queue.
                    while (structureQueue.Count > 0)
                    {
                        structureMods.Enqueue(structureQueue.Dequeue());
                    }
                }

                // If we have any structures, enqueue them to be applied.
                if (structureMods.Count > 0)
                {
                    _world.EnqueueVoxelModifications(structureMods);
                }

                // Check if any neighbors (or previous sessions) left pending mods for THIS chunk while it was unloaded.
                if (_world.ModManager.TryGetModsForChunk(jobEntry.Key, out List<VoxelMod> pendingMods))
                {
                    // We apply these directly to the data now, as the chunk is populated but not yet meshed.
                    foreach (VoxelMod mod in pendingMods)
                    {
                        Vector3Int localPos = _world.worldData.GetLocalVoxelPositionInChunk(mod.GlobalPosition);
                        chunkData.ModifyVoxel(localPos, mod);
                    }
                }

                // Check for pending lighting updates for this chunk
                if (_world.ModManager.TryGetLightUpdatesForChunk(jobEntry.Key, out HashSet<Vector2Int> lightCols))
                {
                    if (!_world.worldData.SunlightRecalculationQueue.TryAdd(chunkData.position, lightCols))
                        _world.worldData.SunlightRecalculationQueue[chunkData.position].UnionWith(lightCols);

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


                completedCoords.Add(jobEntry.Key);

                // Now that data is fully ready and lit, the chunk can have its mesh generated.
                Chunk chunk = _world.GetChunkFromChunkCoord(jobEntry.Key);
                if (chunk != null && chunk.isActive)
                {
                    _world.RequestChunkMeshRebuild(chunk);
                }
            }
        }

        // Remove the completed jobs from our tracking dictionary
        foreach (ChunkCoord coord in completedCoords)
        {
            generationJobs.Remove(coord);
        }
    }

    /// <summary>
    /// Checks for completed mesh generation jobs and applies the resulting mesh data to the chunk GameObjects.
    /// </summary>
    public void ProcessMeshJobs()
    {
        // Using a temporary list to avoid modifying dictionary while iterating
        List<ChunkCoord> completedCoords = new List<ChunkCoord>();
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

                completedCoords.Add(jobEntry.Key);
            }
        }

        foreach (ChunkCoord coord in completedCoords)
        {
            meshJobs.Remove(coord);
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
        HashSet<ChunkCoord> chunksToRebuildMesh = new HashSet<ChunkCoord>();

        List<ChunkCoord> completedCoords = new List<ChunkCoord>();
        foreach (var jobEntry in lightingJobs)
        {
            if (jobEntry.Value.Handle.IsCompleted)
            {
                jobEntry.Value.Handle.Complete();
                LightingJobData jobData = jobEntry.Value;

                ChunkData chunkData = _world.worldData.RequestChunk(new Vector2Int(jobEntry.Key.X * VoxelData.ChunkWidth, jobEntry.Key.Z * VoxelData.ChunkWidth), false);

                // Flag this chunk to prevent neighbors from meshing until its results are fully processed.
                if (chunkData != null)
                {
                    chunkData.IsAwaitingMainThreadProcess = true;
                }

                bool isChunkStable = jobData.IsStable[0];

                if (chunkData != null && chunkData.IsPopulated)
                {
                    // 1. Populate sections from the flat map returned by the job
                    chunkData.PopulateFromFlattened(jobData.Map);

                    // 2. Process cross-chunk modifications calculated by the job.
                    foreach (LightModification mod in jobData.Mods)
                    {
                        // Find the chunk that this modification affects.
                        Vector2Int neighborChunkV2Coord = _world.worldData.GetChunkCoordFor(mod.GlobalPosition);
                        ChunkData neighborChunk = _world.worldData.RequestChunk(neighborChunkV2Coord, false);

                        // If the neighbor doesn't exist or isn't generated, we can't do anything.
                        if (neighborChunk == null || !neighborChunk.IsPopulated) continue;

                        // Get the local position and flat array index for the voxel in the neighbor chunk.
                        Vector3Int localPos = _world.worldData.GetLocalVoxelPositionInChunk(mod.GlobalPosition);

                        uint oldPackedData = neighborChunk.GetVoxel(localPos.x, localPos.y, localPos.z);
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
                            neighborChunk.SetVoxel(localPos.x, localPos.y, localPos.z, newPackedData);

                            // 2. Queue an update for the neighbor's *next* lighting pass.
                            //    This correctly seeds the propagation algorithm for the neighbor.
                            if (mod.Channel == LightChannel.Sun)
                                neighborChunk.AddToSunLightQueue(localPos, oldLightLevel);
                            else
                                neighborChunk.AddToBlockLightQueue(localPos, oldLightLevel);

                            // 3. Mark the neighbor chunk for a mesh rebuild, as its lighting has changed.
                            chunksToRebuildMesh.Add(new ChunkCoord(neighborChunk.position));
                        }
                    }
                }

                // 3. Check if the central chunk's lighting has stabilized.
                if (isChunkStable)
                {
                    // The chunk is stable! It's now safe to request a mesh rebuild.
                    chunksToRebuildMesh.Add(jobEntry.Key);
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

                completedCoords.Add(jobEntry.Key);
            }
        }

        // 5. After processing all completed jobs, request mesh rebuilds for all affected world.chunks.
        foreach (ChunkCoord coord in chunksToRebuildMesh)
        {
            Chunk chunk = _world.GetChunkFromChunkCoord(coord);
            if (chunk != null)
            {
                _world.RequestChunkMeshRebuild(chunk, immediate: true);
            }
        }

        // 6. Remove the completed jobs from our tracking dictionary
        foreach (ChunkCoord coord in completedCoords)
        {
            lightingJobs.Remove(coord);
        }
    }
}
