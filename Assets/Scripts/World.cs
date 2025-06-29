using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Data;
using Helpers;
using Jobs;
using Jobs.BurstData;
using MyBox;
using Unity.Collections;
using Unity.Jobs;
using UnityEditor;
using UnityEngine;
using Random = UnityEngine.Random;

public class World : MonoBehaviour
{
    public Settings settings;
    private string settingFilePath = Application.dataPath + "/settings.json";

    [Header("World Generation Values")]
    public BiomeAttributes[] biomes;

    [Header("Lighting")]
    [Range(0f, 1f)]
    [Tooltip("Lower value equals darker light level.")]
    public float globalLightLevel;

    public Color day;
    public Color night;

    [Header("Player")]
    public Player player;

    private Transform playerTransform;
    private Camera playerCamera;

    [InitializationField]
    public Vector3 spawnPosition;

    [InitializationField]
    public Vector3 spawnPositionOffset = new Vector3(0.5f, 1.1f, 0.5f);

    [Header("Blocks & Materials")]
    public Material material;

    public Material transparentMaterial;
    public Material waterMaterial;
    public BlockType[] blockTypes;

    private Chunk[,] chunks = new Chunk[VoxelData.WorldSizeInChunks, VoxelData.WorldSizeInChunks];

    private HashSet<ChunkCoord> activeChunks = new HashSet<ChunkCoord>();
    private List<ChunkCoord> _tempActiveChunkList = new List<ChunkCoord>(); // Used to avoid modifying activeChunks while iterating, and to avoid GC allocations.
    public ChunkCoord playerChunkCoord;
    private ChunkCoord playerLastChunkCoord;

    private List<Chunk> chunksToBuildMesh = new List<Chunk>();
    public Queue<Chunk> chunksToDraw = new Queue<Chunk>();

    private bool applyingModifications = false;
    private Queue<Queue<VoxelMod>> modifications = new Queue<Queue<VoxelMod>>();

    // UI
    [Header("UI")]
    public GameObject debugScreen;

    public GameObject creativeInventoryWindow;
    public GameObject cursorSlot;
    private bool _inUI = false;

    // Clouds
    [Header("Clouds")]
    public Clouds clouds;

    [Header("World Data")]
    public WorldData worldData;

    [Header("Paths")]
    [MyBox.ReadOnly]
    public string appSaveDataPath;

    // Shader Properties
    private static readonly int ShaderGlobalLightLevel = Shader.PropertyToID("GlobalLightLevel");
    private static readonly int ShaderMinGlobalLightLevel = Shader.PropertyToID("minGlobalLightLevel");
    private static readonly int ShaderMaxGlobalLightLevel = Shader.PropertyToID("maxGlobalLightLevel");

    // --- Job Management Data ---
    private NativeArray<BiomeAttributesJobData> biomesJobData;
    private NativeArray<LodeJobData> allLodesJobData;
    private NativeArray<BlockTypeJobData> blockTypesJobData;

    // track the JobHandles and the allocated data together
    private Dictionary<ChunkCoord, (JobHandle handle, NativeArray<uint> map, NativeQueue<VoxelMod> mods)> generationJobs = new Dictionary<ChunkCoord, (JobHandle, NativeArray<uint>, NativeQueue<VoxelMod>)>();
    private Dictionary<ChunkCoord, (JobHandle handle, MeshDataJobOutput meshData)> meshJobs = new Dictionary<ChunkCoord, (JobHandle, MeshDataJobOutput)>();

    private Dictionary<ChunkCoord, (JobHandle handle, NativeArray<uint> map, NativeQueue<LightQueueNode> sunLightQueue, NativeQueue<LightQueueNode> blockLightQueue, NativeQueue<Vector2Int> sunLightRecalcQueue, NativeList<LightModification> mods)> lightingJobs =
        new Dictionary<ChunkCoord, (JobHandle, NativeArray<uint>, NativeQueue<LightQueueNode>, NativeQueue<LightQueueNode>, NativeQueue<Vector2Int>, NativeList<LightModification>)>();

    #region Singleton pattern

    public static World Instance { get; private set; }

    private void Awake()
    {
        // If the instance value is not null and not *this*, we've somehow ended up with more than one World component.
        // Since another one has already been assigned, delete this one.
        if (Instance is not null && Instance != this)
        {
            Destroy(this.gameObject);
        }
        else
        {
            Instance = this;
            appSaveDataPath = Application.persistentDataPath;
        }

        // --- Prepare Job-Safe Data ---
        PrepareJobData();
    }

    #endregion

    // Cleanup for NativeArrays
    private void OnDestroy()
    {
        // 1. Complete any running jobs immediately. This is crucial.
        //    Failing to do this before disposing their data will cause errors.
        // -- World generation jobs --
        foreach (var job in generationJobs.Values)
        {
            job.handle.Complete();
            job.map.Dispose();
            job.mods.Dispose();
        }

        generationJobs.Clear();

        // -- Mesh generation jobs --
        foreach (var job in meshJobs.Values)
        {
            job.handle.Complete();
            job.meshData.Dispose();
        }

        meshJobs.Clear();

        // -- Lighting jobs --
        foreach (var job in lightingJobs.Values)
        {
            job.handle.Complete();
            job.map.Dispose();
            job.sunLightQueue.Dispose();
            job.blockLightQueue.Dispose();
            job.sunLightRecalcQueue.Dispose();
            job.mods.Dispose();
        }

        lightingJobs.Clear();

        // 2. Dispose of the persistent global data.
        if (biomesJobData.IsCreated) biomesJobData.Dispose();
        if (allLodesJobData.IsCreated) allLodesJobData.Dispose();
        if (blockTypesJobData.IsCreated) blockTypesJobData.Dispose();
    }

    private void Start()
    {
        StartCoroutine(StartWorld());
    }

    private IEnumerator StartWorld()
    {
        Debug.Log("--- Initializing World ---");
        Debug.Log($"Generating new world using seed: {VoxelData.Seed}");

        // --- Fetch needed components ---
        // Get player transform component
        playerTransform = player.GetComponent<Transform>();

        // Get main camera.
        playerCamera = Camera.main!;

        // --- Load / Create Settings ---
        // Create settings file if it doesn't yet exist, after that, load it.
        if (!File.Exists(settingFilePath) || Application.isEditor)
        {
            string jsonExport = JsonUtility.ToJson(settings, true);
            File.WriteAllText(settingFilePath, jsonExport);
#if UNITY_EDITOR
            AssetDatabase.Refresh(); // Refresh Unity's asset database.
# endif
        }

#if !UNITY_EDITOR
        string jsonImport = File.ReadAllText(settingFilePath);
        settings = JsonUtility.FromJson<Settings>(jsonImport);
# endif

        // --- Initialize World settings (from save data / create new world) ---
        // TODO: Set worldName using UI
        if (settings.loadSaveDataOnStartup)
            worldData = SaveSystem.LoadWorld("Prototype", VoxelData.Seed);
        else
            worldData = new WorldData("Prototype", VoxelData.Seed);

        // Initialize world seed
        Random.InitState(VoxelData.Seed);

        // Initialize global shader properties
        Shader.SetGlobalFloat(ShaderMinGlobalLightLevel, VoxelData.MinLightLevel);
        Shader.SetGlobalFloat(ShaderMaxGlobalLightLevel, VoxelData.MaxLightLevel);
        SetGlobalLightValue();

        // --- STEP 1: DETERMINE INITIAL PLAYER POSITION ---
        // Set initial spawnPosition to the center of the world for X & Z, and top of the world for Y.
        spawnPosition = new Vector3Int(VoxelData.WorldCentre, VoxelData.ChunkHeight - 1, VoxelData.WorldCentre);
        playerTransform.position = spawnPosition;
        playerChunkCoord = GetChunkCoordFromVector3(playerTransform.position);

        // --- STEP 2: SYNCHRONOUSLY GENERATE ALL DATA ---
        Debug.Log("--- Generating all data within load distance ---");
        // 1. First, just schedule generation for everything in the load radius.
        LoadChunksInDataPass();

        // 2. Force complete ONLY the data-related jobs (generation and lighting).
        //    Now, instead of a blocking call, we yield to (wait for) another coroutine.
        //    The code will PAUSE here and will not continue until ForceCompleteDataJobsCoroutine is finished.
        yield return StartCoroutine(ForceCompleteDataJobsCoroutine());

        // --- STEP 3: ASYNCHRONOUSLY ACTIVATE AND MESH ---
        // We are now DONE with the synchronous part of Start().
        // The Update() loop will take over from here.
        // The very first call to Update() will see that playerLastChunkCoord is different
        // and will call CheckViewDistance, which will create the GameObjects and
        // begin the normal, asynchronous meshing process.

        // 4. NOW it's safe to get the spawn height, as the data AND mesh colliders exist.
        Debug.Log("--- Finalizing startup ---");
        Debug.Log("Getting spawn position...");
        spawnPosition = GetHighestVoxel(spawnPosition.ToVector3Int()) + spawnPositionOffset;
        playerTransform.position = spawnPosition;

        Debug.Log("Initializing clouds...");
        clouds?.Initialize();

        Debug.Log("Staring world tick...");
        StartCoroutine(Tick());

        Debug.Log("World initialization complete.");
        Debug.Log("--- Startup complete ---");
    }

    private void LoadChunksInDataPass()
    {
        int loadDist = settings.loadDistance;
        // We don't need the spiral loop here, a simple square loop is fine for startup.
        for (int x = -loadDist; x <= loadDist; x++)
        {
            for (int z = -loadDist; z <= loadDist; z++)
            {
                ChunkCoord coord = new ChunkCoord(playerChunkCoord.X + x, playerChunkCoord.Z + z);
                if (IsChunkInWorld(coord))
                {
                    // This just schedules the generation job.
                    ScheduleGeneration(coord);
                }
            }
        }
    }

    private IEnumerator ForceCompleteDataJobsCoroutine()
    {
        int safetyBreak = 0;
        const int maxIterations = 5_000; // Should be more than enough for startup, this should be higher for (mush) larger view distances (eg: 20+).

        while (generationJobs.Count > 0 || lightingJobs.Count > 0 || HasPendingLightChangesOnMainThread())
        {
            // Complete any finished generation jobs
            ProcessGenerationJobs();
            ApplyModifications();

            // Try to schedule lighting for chunks that are ready
            foreach (ChunkData chunkData in worldData.chunks.Values)
            {
                if (chunkData.isPopulated && chunkData.hasLightChangesToProcess)
                {
                    ChunkCoord coord = new ChunkCoord(chunkData.position);
                    if (!lightingJobs.ContainsKey(coord) && AreNeighborsDataReady(coord))
                    {
                        Chunk tempChunk = new Chunk(coord, false);
                        ScheduleLightingUpdate(tempChunk);
                    }
                }
            }

            // Complete all scheduled lighting jobs
            CompleteAndProcessLightingJobs();

            // --- Safety Break ---
            safetyBreak++;
            if (safetyBreak > maxIterations)
            {
                Debug.LogError("ForceCompleteDataJobsCoroutine exceeded max iterations. Forcing exit.");
                Debug.LogError($"Remaining chunks in generation job: {generationJobs.Count} | lighting job: {lightingJobs.Count}");
                yield break; // Exit the coroutine
            }

            // Wait a frame
            yield return null;
        }

        Debug.Log("All generation and lighting jobs are complete!");
    }

    // Renamed from HasPendingLightChanges to be more specific.
    // It now iterates over the master data source, not the GameObjects.
    private bool HasPendingLightChangesOnMainThread()
    {
        foreach (var chunkData in worldData.chunks.Values)
        {
            if (chunkData != null && chunkData.hasLightChangesToProcess)
            {
                return true;
            }
        }

        return false;
    }

// Helper methods to reduce code duplication
    private void CompleteAndProcessLightingJobs()
    {
        // Force complete all scheduled lighting jobs immediately.
        foreach (var job in lightingJobs.Values)
        {
            job.handle.Complete();
        }

        // Process their results.
        ProcessLightingJobs();
    }

    private void CompleteAndProcessMeshJobs()
    {
        // Force complete all scheduled mesh jobs immediately.
        foreach (var job in meshJobs.Values)
        {
            job.handle.Complete();
        }

        // Process their results.
        ProcessMeshJobs();
    }

    public void SetGlobalLightValue()
    {
        Shader.SetGlobalFloat(ShaderGlobalLightLevel, globalLightLevel);
        playerCamera.backgroundColor = Color.Lerp(night, day, globalLightLevel);
    }

    IEnumerator Tick()
    {
        while (true)
        {
            foreach (ChunkCoord coord in activeChunks)
            {
                chunks[coord.X, coord.Z].TickUpdate();
            }

            yield return new WaitForSeconds(VoxelData.TickLength);
        }
    }

    private void Update()
    {
        playerChunkCoord = GetChunkCoordFromVector3(playerTransform.position);

        // Only update the chunks if the player has moved from the chunk they were previously on.
        if (!playerChunkCoord.Equals(playerLastChunkCoord))
        {
            // Queue up chunks for generation
            CheckViewDistance();
        }

        playerLastChunkCoord = playerChunkCoord;

        // --- Process Job System ---
        // 1. Process any jobs that have finished generating chunk data.
        //    This might add new chunks to the chunksToBuildMesh list.
        ProcessGenerationJobs();

        // 2. Apply all queued voxel modifications (from player and world gen).
        if (!applyingModifications)
            ApplyModifications();

        // 3. Process completed lighting jobs from the PREVIOUS frame.
        ProcessLightingJobs();

        // 4. Scan active chunks for lighting work and schedule jobs (New "Pull" system)
        if (settings.enableLighting)
        {
            int lightJobsScheduled = 0;

            // 1. Clear the persistent list from the previous frame's use.
            _tempActiveChunkList.Clear();

            // 2. Populate the persistent list with the current active chunks.
            //    This does NOT create a new list, it just adds to the existing one.
            foreach (ChunkCoord coord in activeChunks)
            {
                _tempActiveChunkList.Add(coord);
            }

            // 3. Iterate over the temporary, safe-to-modify list.
            foreach (ChunkCoord chunkCoord in _tempActiveChunkList)
            {
                if (lightJobsScheduled >= settings.maxLightJobsPerFrame) break; // Respect the throttle

                Chunk chunkToUpdate = chunks[chunkCoord.X, chunkCoord.Z];

                // If chunk is valid, has pending changes, and no job is currently running for it...
                if (chunkToUpdate != null && chunkToUpdate.chunkData.hasLightChangesToProcess && !lightingJobs.ContainsKey(chunkToUpdate.coord))
                {
                    // NOTE: ScheduleLightingUpdate might indirectly modify the 'activeChunks' set
                    // by affecting neighbor states. This pattern protects against that.
                    ScheduleLightingUpdate(chunkToUpdate);
                    lightJobsScheduled++;
                }
            }
        }

        // 5. Process completed mesh jobs from the PREVIOUS frame.
        ProcessMeshJobs();

        // 6. Schedule NEW mesh jobs for chunks that now need them.
        if (chunksToBuildMesh.Count > 0)
        {
            for (int i = chunksToBuildMesh.Count - 1; i >= 0; i--)
            {
                Chunk chunk = chunksToBuildMesh[i];
                if (chunk != null)
                {
                    // ScheduleMeshing will any lighting changes jobs are running and if neighbors are ready.
                    // If any lighting jobs are running, it will return false.
                    // If neighbors are ready, it will schedule the job, and we can remove this from the list.
                    if (ScheduleMeshing(chunk))
                    {
                        chunksToBuildMesh.RemoveAt(i);
                    }
                }
                else
                {
                    // Remove null chunks from the list.
                    chunksToBuildMesh.RemoveAt(i);
                }
            }
        }

        // The chunksToDraw queue is populated by ApplyMeshData in Chunk.cs
        if (chunksToDraw.Count > 0)
        {
            chunksToDraw.Dequeue().CreateMesh();
        }

        // UI - DEBUG SCREEN
        if (Input.GetKeyDown(KeyCode.F3))
            debugScreen.SetActive(!debugScreen.activeSelf);

        if (Input.GetKeyDown(KeyCode.F4))
            SaveSystem.SaveWorld(worldData);
    }

    // --- NEW JOB-RELATED METHODS ---
    private void PrepareJobData()
    {
        // --- Biomes: Biome and Lode ---
        // Pass 1: Calculate the total number of lodes across all biomes.
        int totalLodeCount = 0;
        foreach (BiomeAttributes biome in biomes)
        {
            totalLodeCount += biome.lodes.Length;
        }

        // Pass 2: Allocate memory and populate the flattened arrays.
        biomesJobData = new NativeArray<BiomeAttributesJobData>(biomes.Length, Allocator.Persistent);
        allLodesJobData = new NativeArray<LodeJobData>(totalLodeCount, Allocator.Persistent);

        int currentLodeIndex = 0;
        for (int i = 0; i < biomes.Length; i++)
        {
            // Copy the lodes for the current biome into the single large array.
            for (int j = 0; j < biomes[i].lodes.Length; j++)
            {
                Lode lode = biomes[i].lodes[j];
                allLodesJobData[currentLodeIndex + j] = new LodeJobData
                {
                    blockID = lode.blockID,
                    minHeight = lode.minHeight,
                    maxHeight = lode.maxHeight,
                    scale = lode.scale,
                    threshold = lode.threshold,
                    noiseOffset = lode.noiseOffset
                };
            }

            // Populate the biome data, including the start index and count for its lodes.
            biomesJobData[i] = new BiomeAttributesJobData
            {
                // Copy all fields from biomes[i] to biomesJobData[i]
                offset = biomes[i].offset,
                scale = biomes[i].scale,
                terrainHeight = biomes[i].terrainHeight,
                terrainScale = biomes[i].terrainScale,
                surfaceBlock = biomes[i].surfaceBlock,
                subSurfaceBlock = biomes[i].subSurfaceBlock,
                placeMajorFlora = biomes[i].placeMajorFlora,
                majorFloraIndex = biomes[i].majorFloraIndex,
                majorFloraZoneScale = biomes[i].majorFloraZoneScale,
                majorFloraZoneThreshold = biomes[i].majorFloraZoneThreshold,
                majorFloraPlacementScale = biomes[i].majorFloraPlacementScale,
                majorFloraPlacementThreshold = biomes[i].majorFloraPlacementThreshold,
                maxHeight = biomes[i].maxHeight,
                minHeight = biomes[i].minHeight,
                lodeStartIndex = currentLodeIndex,
                lodeCount = biomes[i].lodes.Length
            };

            // Advance the master lode index for the next biome.
            currentLodeIndex += biomes[i].lodes.Length;
        }

        // --- Block Types ---
        blockTypesJobData = new NativeArray<BlockTypeJobData>(blockTypes.Length, Allocator.Persistent);
        for (int i = 0; i < blockTypes.Length; i++)
        {
            blockTypesJobData[i] = new BlockTypeJobData
            {
                isSolid = blockTypes[i].isSolid,
                isWater = blockTypes[i].isWater,
                opacity = blockTypes[i].opacity,
                renderNeighborFaces = blockTypes[i].renderNeighborFaces,
                isActive = blockTypes[i].isActive,
                backFaceTexture = blockTypes[i].backFaceTexture,
                frontFaceTexture = blockTypes[i].frontFaceTexture,
                topFaceTexture = blockTypes[i].topFaceTexture,
                bottomFaceTexture = blockTypes[i].bottomFaceTexture,
                leftFaceTexture = blockTypes[i].leftFaceTexture,
                rightFaceTexture = blockTypes[i].rightFaceTexture,
            };
        }
    }

    // Helper for other classes to get job data
    public NativeArray<BlockTypeJobData> GetBlockTypesJobData(Allocator allocator)
    {
        return new NativeArray<BlockTypeJobData>(blockTypesJobData, allocator);
    }

    private void ProcessGenerationJobs()
    {
        // Using a temporary list to avoid modifying dictionary while iterating
        List<ChunkCoord> completedCoords = new List<ChunkCoord>();
        foreach (var jobEntry in generationJobs)
        {
            if (jobEntry.Value.handle.IsCompleted)
            {
                // Complete the job to ensure it's finished and sync memory
                jobEntry.Value.handle.Complete();

                // --- STAGE 1: Populate with base terrain ---
                ChunkData chunkData = worldData.RequestChunk(new Vector2Int(jobEntry.Key.X * VoxelData.ChunkWidth, jobEntry.Key.Z * VoxelData.ChunkWidth), true);
                chunkData.Populate(jobEntry.Value.map);
                chunkData.chunk?.OnDataPopulated();

                // --- STAGE 2: Apply generated modifications (trees, etc.) ---
                Queue<VoxelMod> structureMods = new Queue<VoxelMod>();
                while (jobEntry.Value.mods.TryDequeue(out VoxelMod mod))
                {
                    // The VoxelMod from the job just contains the request type and position.
                    // The main thread expands this into the full structure queue.
                    Queue<VoxelMod> structureQueue = Structure.GenerateMajorFlora(mod.id, mod.globalPosition, biomes[0].minHeight, biomes[0].maxHeight);
                    // We add these to a temporary queue.
                    while (structureQueue.Count > 0)
                    {
                        structureMods.Enqueue(structureQueue.Dequeue());
                    }
                }

                // If we have any structures, apply them now.
                if (structureMods.Count > 0)
                {
                    modifications.Enqueue(structureMods);
                    ApplyModifications(); // Process it immediately
                }

                // Dispose of the job's data here, AFTER it has been used
                jobEntry.Value.map.Dispose();
                jobEntry.Value.mods.Dispose();

                // --- STAGE 3: Now that the chunk has its final form, calculate lighting ---
                if (Instance.settings.enableLighting)
                {
                    chunkData.RecalculateSunLightLight();
                }
                else
                {
                    // If lighting is off, set all blocks to full brightness.
                    for (int i = 0; i < chunkData.map.Length; i++)
                    {
                        chunkData.map[i] = BurstVoxelDataBitMapping.SetSunLight(chunkData.map[i], 15);
                    }
                }


                completedCoords.Add(jobEntry.Key);

                // Now that data is fully ready and lit, the chunk can have its mesh generated.
                Chunk chunk = chunks[jobEntry.Key.X, jobEntry.Key.Z];
                if (chunk != null && chunk.isActive)
                {
                    RequestChunkMeshRebuild(chunk);
                }
            }
        }

        // Remove the completed jobs from our tracking dictionary
        foreach (ChunkCoord coord in completedCoords)
        {
            generationJobs.Remove(coord);
        }
    }

    private void ProcessMeshJobs()
    {
        // Using a temporary list to avoid modifying dictionary while iterating
        List<ChunkCoord> completedCoords = new List<ChunkCoord>();
        foreach (var jobEntry in meshJobs)
        {
            if (jobEntry.Value.handle.IsCompleted)
            {
                jobEntry.Value.handle.Complete();

                Chunk chunk = chunks[jobEntry.Key.X, jobEntry.Key.Z];
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

    // This replaces the old CheckLoadDistance and part of CheckViewDistance
    public void ScheduleGeneration(ChunkCoord coord)
    {
        // Don't schedule if a job is already running for it.
        if (generationJobs.ContainsKey(coord))
            return;

        Vector2Int chunkPos = new Vector2Int(coord.X * VoxelData.ChunkWidth, coord.Z * VoxelData.ChunkWidth);

        // Don't schedule if chunk data already exists AND is already populated, in our main thread dictionary.
        if (worldData.chunks.TryGetValue(chunkPos, out ChunkData data) && data.isPopulated)
        {
            return; // Data is already generated, no need to schedule.
        }

        // Allocate and track all data together
        var modificationsQueue = new NativeQueue<VoxelMod>(Allocator.Persistent);
        var outputMap = new NativeArray<uint>(VoxelData.ChunkWidth * VoxelData.ChunkHeight * VoxelData.ChunkWidth, Allocator.Persistent);

        ChunkGenerationJob job = new ChunkGenerationJob
        {
            seed = VoxelData.Seed,
            chunkPosition = chunkPos,
            biomes = biomesJobData,
            allLodes = allLodesJobData,
            outputMap = outputMap,
            modifications = modificationsQueue.AsParallelWriter()
        };

        JobHandle handle = job.Schedule();

        // Store the handle and ALL associated native containers together.
        generationJobs.Add(coord, (handle, outputMap, modificationsQueue));
    }

    /// Returns a bool indicating success
    public bool ScheduleMeshing(Chunk chunk)
    {
        ChunkCoord coord = chunk.coord;

        if (meshJobs.ContainsKey(coord))
            return true; // Job is already scheduled, we can remove it from the build list.

        // Chunk's own lighting must be stable.
        if (chunk.chunkData.hasLightChangesToProcess || lightingJobs.ContainsKey(coord))
        {
            return false; // This chunk is still processing light, wait.
        }

        // All neighbors' data and LIGHTING must be stable.
        if (!AreNeighborsReadyAndLit(coord))
        {
            return false; // A neighbor is generating or lighting, wait.
        }

        // 1. Allocate all input maps with TempJob. They will be used and then disposed.
        var map = worldData.GetChunkMapForJob(new Vector2Int(coord.X * VoxelData.ChunkWidth, coord.Z * VoxelData.ChunkWidth), Allocator.TempJob);
        var back = worldData.GetChunkMapForJob(new Vector2Int(coord.X * VoxelData.ChunkWidth, (coord.Z - 1) * VoxelData.ChunkWidth), Allocator.TempJob);
        var front = worldData.GetChunkMapForJob(new Vector2Int(coord.X * VoxelData.ChunkWidth, (coord.Z + 1) * VoxelData.ChunkWidth), Allocator.TempJob);
        var left = worldData.GetChunkMapForJob(new Vector2Int((coord.X - 1) * VoxelData.ChunkWidth, coord.Z * VoxelData.ChunkWidth), Allocator.TempJob);
        var right = worldData.GetChunkMapForJob(new Vector2Int((coord.X + 1) * VoxelData.ChunkWidth, coord.Z * VoxelData.ChunkWidth), Allocator.TempJob);

        // The output data must be persistent, as it lives until processed on the main thread.
        MeshDataJobOutput meshOutput = new MeshDataJobOutput(Allocator.Persistent);

        MeshGenerationJob job = new MeshGenerationJob
        {
            map = map,
            blockTypes = blockTypesJobData,
            chunkPosition = chunk.chunkPosition,
            neighborBack = back,
            neighborFront = front,
            neighborLeft = left,
            neighborRight = right,
            output = meshOutput
        };

        // 2. Schedule the main mesh generation job. It has no dependencies yet.
        JobHandle meshJobHandle = job.Schedule();

        // 3. Create a NativeArray to hold all the disposal handles, and make them dependent on the main job's handle.
        //    This means "Don't dispose of 'map' until 'meshJobHandle' is complete".
        var disposalHandles = new NativeArray<JobHandle>(5, Allocator.TempJob);
        disposalHandles[0] = map.Dispose(meshJobHandle);
        disposalHandles[1] = back.Dispose(meshJobHandle);
        disposalHandles[2] = front.Dispose(meshJobHandle);
        disposalHandles[3] = left.Dispose(meshJobHandle);
        disposalHandles[4] = right.Dispose(meshJobHandle);

        // 4. Combine all the disposal handles into a single final handle.
        //    This handle will be complete only after the mesh job AND all its input disposal jobs are done.
        JobHandle combinedDisposalHandle = JobHandle.CombineDependencies(disposalHandles);
        JobHandle finalHandle = disposalHandles.Dispose(combinedDisposalHandle);

        // 5. Store this final, all-encompassing handle in our tracking dictionary.
        meshJobs.Add(coord, (finalHandle, meshOutput));

        return true;
    }

    private void ScheduleLightingUpdate(Chunk chunk)
    {
        ChunkCoord coord = chunk.coord;
        if (lightingJobs.ContainsKey(coord)) return; // Job already running for this chunk

        // Do not schedule a lighting job until all neighbors have finished generating their data.
        if (!AreNeighborsDataReady(coord))
        {
            // We can't schedule it now. Mark it so we can try again on the next frame.
            chunk.chunkData.hasLightChangesToProcess = true;
            return;
        }

        // If there are no light changes, no need to schedule a job.
        // NOTE: Might need to be removed if this causes issues.
        bool noSunlightRecalc = !worldData.sunlightRecalculationQueue.Contains(chunk.chunkData.position); // Simple check, more robust would be to check all columns in chunk
        if (chunk.chunkData.BlockLightQueueCount == 0 && chunk.chunkData.SunLightQueueCount == 0 && noSunlightRecalc)
        {
            chunk.chunkData.hasLightChangesToProcess = false;
            return;
        }

        // --- STANDARD LIGHTING PROPAGATION PASS ---
        // These containers are created on the main thread and live until the job is processed on the main thread.
        // They MUST be persistent.
        var mapCopy = new NativeArray<uint>(chunk.chunkData.map, Allocator.Persistent);
        var crossChunkMods = new NativeList<LightModification>(Allocator.Persistent);

        // Get propagation queues from ChunkData
        var sunLightBfs = chunk.chunkData.GetSunlightQueueForJob(Allocator.Persistent);
        var blockLightBfs = chunk.chunkData.GetBlocklightQueueForJob(Allocator.Persistent);

        // Get sunlight column rescan requests from WorldData
        var sunLightRecalc = new NativeQueue<Vector2Int>(Allocator.Persistent);
        // This is inefficient. A better system would pass only relevant columns. For now, we pass all.
        foreach (Vector2Int col in worldData.sunlightRecalculationQueue)
        {
            // Check if the column belongs to this chunk
            Vector2Int chunkForCol = worldData.GetChunkCoordFor(new Vector3(col.x, 0, col.y));
            if (chunkForCol == chunk.chunkData.position)
            {
                // The job expects local coordinates for the column
                sunLightRecalc.Enqueue(new Vector2Int(col.x - chunk.chunkData.position.x, col.y - chunk.chunkData.position.y));
            }
        }

        // Clear the global queue once we've consumed the items for this chunk.
        // A better system would remove only the consumed items.
        worldData.sunlightRecalculationQueue.Clear();

        // Neighbor maps are read-only inputs, so they can be TempJob, but we MUST schedule their disposal.
        var neighborBack = worldData.GetChunkMapForJob(chunk.chunkData.position + new Vector2Int(0, -VoxelData.ChunkWidth), Allocator.TempJob);
        var neighborFront = worldData.GetChunkMapForJob(chunk.chunkData.position + new Vector2Int(0, VoxelData.ChunkWidth), Allocator.TempJob);
        var neighborLeft = worldData.GetChunkMapForJob(chunk.chunkData.position + new Vector2Int(-VoxelData.ChunkWidth, 0), Allocator.TempJob);
        var neighborRight = worldData.GetChunkMapForJob(chunk.chunkData.position + new Vector2Int(VoxelData.ChunkWidth, 0), Allocator.TempJob);

        var job = new LightingJob
        {
            // The chunk's own data is read-write
            map = mapCopy,
            chunkPosition = chunk.chunkData.position,
            sunlightBfsQueue = sunLightBfs,
            blocklightBfsQueue = blockLightBfs,
            sunlightColumnRecalcQueue = sunLightRecalc,

            // Neighbor data is read-only
            neighborBack = neighborBack,
            neighborFront = neighborFront,
            neighborLeft = neighborLeft,
            neighborRight = neighborRight,

            blockTypes = blockTypesJobData,

            // Output list for modified neighbor modifications
            crossChunkLightMods = crossChunkMods
        };

        // Schedule the main job
        JobHandle jobHandle = job.Schedule();

        // Create a dependency chain to dispose all the TempJob arrays after the main job is complete.
        var disposalHandles = new NativeArray<JobHandle>(4, Allocator.TempJob);
        disposalHandles[0] = neighborBack.Dispose(jobHandle);
        disposalHandles[1] = neighborFront.Dispose(jobHandle);
        disposalHandles[2] = neighborLeft.Dispose(jobHandle);
        disposalHandles[3] = neighborRight.Dispose(jobHandle);

        // Combine all disposal jobs into one handle.
        JobHandle combinedDisposalHandle = JobHandle.CombineDependencies(disposalHandles);

        // Finally, create a job to dispose the disposalHandles array itself. This becomes the final handle.
        JobHandle finalHandle = disposalHandles.Dispose(combinedDisposalHandle);

        // Reset the flag, because we are now scheduling a job to process these changes.
        chunk.chunkData.hasLightChangesToProcess = false;

        // Store this final, all-encompassing handle, along with the persistent data that needs to be processed later.
        lightingJobs.Add(coord, (finalHandle, mapCopy, sunLightBfs, blockLightBfs, sunLightRecalc, crossChunkMods));
    }

    public void AddModification(VoxelMod mod)
    {
        // Create a new queue containing just this single modification
        Queue<VoxelMod> singleModQueue = new Queue<VoxelMod>();
        singleModQueue.Enqueue(mod);

        // Add this single-item queue to the main modifications queue
        modifications.Enqueue(singleModQueue);
    }

    public void NotifyChunkModified(Vector2Int chunkPos, Vector3Int localVoxelPos)
    {
        // 1. The chunk that was modified always needs a rebuild.
        ChunkCoord coord = new ChunkCoord(chunkPos);
        if (chunks[coord.X, coord.Z] != null)
        {
            RequestChunkMeshRebuild(chunks[coord.X, coord.Z], true);
        }

        // 2. If the modification happened on a border, queue the neighbor.
        // Check X-axis borders
        if (localVoxelPos.x == 0)
            QueueNeighborRebuild(chunkPos + new Vector2Int(-VoxelData.ChunkWidth, 0));
        else if (localVoxelPos.x == VoxelData.ChunkWidth - 1)
            QueueNeighborRebuild(chunkPos + new Vector2Int(VoxelData.ChunkWidth, 0));

        // Check Z-axis borders
        if (localVoxelPos.z == 0)
            QueueNeighborRebuild(chunkPos + new Vector2Int(0, -VoxelData.ChunkWidth));
        else if (localVoxelPos.z == VoxelData.ChunkWidth - 1)
            QueueNeighborRebuild(chunkPos + new Vector2Int(0, VoxelData.ChunkWidth));
    }

    private void QueueNeighborRebuild(Vector2Int neighborV2Coord)
    {
        if (worldData.chunks.TryGetValue(neighborV2Coord, out ChunkData neighborData) && neighborData.chunk != null)
        {
            RequestChunkMeshRebuild(neighborData.chunk, true);
        }
    }

    private bool AreNeighborsReady(ChunkCoord coord)
    {
        // Check all 4 horizontal neighbors
        foreach (int faceIndex in VoxelData.HorizontalFaceChecksIndices)
        {
            Vector3Int offset = VoxelData.FaceChecks[faceIndex];
            ChunkCoord neighborCoord = new ChunkCoord(coord.X + offset.x, coord.Z + offset.z);

            if (IsChunkInWorld(neighborCoord))
            {
                // Is a generation job for this neighbor still running?
                if (generationJobs.ContainsKey(neighborCoord))
                {
                    return false; // Neighbor data is not ready yet.
                }

                // Does the placeholder ChunkData exist in the world dictionary?
                Vector2Int chunkPos = new Vector2Int(neighborCoord.X * VoxelData.ChunkWidth, neighborCoord.Z * VoxelData.ChunkWidth);
                if (!worldData.chunks.ContainsKey(chunkPos))
                {
                    // This case is unlikely with the new CheckViewDistance, but is a good safeguard.
                    return false;
                }
            }
        }

        // If we get here, all neighbors are ready.
        return true;
    }

    private bool AreNeighborsReadyAndLit(ChunkCoord coord)
    {
        // Check all 4 horizontal neighbors
        foreach (int faceIndex in VoxelData.HorizontalFaceChecksIndices)
        {
            Vector3Int offset = VoxelData.FaceChecks[faceIndex];
            ChunkCoord neighborCoord = new ChunkCoord(coord.X + offset.x, coord.Z + offset.z);

            if (IsChunkInWorld(neighborCoord))
            {
                // Is a terrain generation job for this neighbor still running?
                if (generationJobs.ContainsKey(neighborCoord))
                {
                    return false; // Neighbor terrain data is not ready.
                }

                // Is a lighting job for this neighbor currently running?
                if (lightingJobs.ContainsKey(neighborCoord))
                {
                    return false; // Neighbor is still calculating light, we must wait.
                }

                // Also check the main-thread flag for the neighbor.
                Vector2Int neighborV2Pos = new Vector2Int(neighborCoord.X * VoxelData.ChunkWidth, neighborCoord.Z * VoxelData.ChunkWidth);
                if (worldData.chunks.TryGetValue(neighborV2Pos, out ChunkData neighborData) && neighborData.hasLightChangesToProcess)
                {
                    return false; // Neighbor has pending light changes that haven't even been scheduled yet.
                }
            }
        }

        // If we get here, all neighbors are stable.
        return true;
    }


    private bool AreNeighborsDataReady(ChunkCoord coord)
    {
        // Check all 4 horizontal neighbors
        foreach (int faceIndex in VoxelData.HorizontalFaceChecksIndices)
        {
            Vector3Int offset = VoxelData.FaceChecks[faceIndex];
            ChunkCoord neighborCoord = new ChunkCoord(coord.X + offset.x, coord.Z + offset.z);

            if (IsChunkInWorld(neighborCoord))
            {
                // Is a generation job for this neighbor still running?
                if (generationJobs.ContainsKey(neighborCoord))
                {
                    return false; // Neighbor data is not ready yet.
                }
            }
        }

        // If we get here, all neighbors have finished their data generation.
        return true;
    }

    private void ProcessLightingJobs()
    {
        if (lightingJobs.Count == 0) return;

        // Use a HashSet to track which chunks need a mesh rebuild this frame.
        HashSet<ChunkCoord> chunksToRebuildMesh = new HashSet<ChunkCoord>();

        List<ChunkCoord> completedCoords = new List<ChunkCoord>();
        foreach (var jobEntry in lightingJobs)
        {
            if (jobEntry.Value.handle.IsCompleted)
            {
                jobEntry.Value.handle.Complete();

                ChunkData chunkData = worldData.RequestChunk(new Vector2Int(jobEntry.Key.X * VoxelData.ChunkWidth, jobEntry.Key.Z * VoxelData.ChunkWidth), false);
                if (chunkData != null && chunkData.isPopulated)
                {
                    // 1. Copy the modified map back. This chunk definitely needs a rebuild.
                    jobEntry.Value.map.CopyTo(chunkData.map);
                    chunksToRebuildMesh.Add(jobEntry.Key);

                    // 2. Process cross-chunk modifications.
                    foreach (LightModification mod in jobEntry.Value.mods)
                    {
                        // Check the state of the neighbor voxel BEFORE we do anything.
                        VoxelState? neighborState = worldData.GetVoxelState(mod.GlobalPosition);

                        // If the neighbor doesn't exist or its light level is ALREADY what the job calculated,
                        // then we have reached a stable state. DO NOTHING. This breaks the infinite loop.
                        if (!neighborState.HasValue) continue;

                        byte neighborChannelLight = (mod.Channel == LightChannel.Sun)
                            ? neighborState.Value.Sunlight
                            : neighborState.Value.Blocklight;

                        if (neighborChannelLight == mod.LightLevel)
                        {
                            continue; // Light level is already correct for this channel. Stop the loop.
                        }

                        // --- If we are here, the light level genuinely needs to be updated. ---
                        // Apply the new light level. This also marks the chunk for a mesh rebuild.
                        SetLight(mod.GlobalPosition, mod.LightLevel, mod.Channel);

                        // Now, treat this modification as a new event. We must queue the
                        // modified block to ensure the light propagates correctly *within the neighbor chunk*.
                        // Queue update for the neighbor chunk's correct light channel
                        worldData.QueueLightUpdate(mod.GlobalPosition, neighborChannelLight, mod.Channel);

                        // The neighbor's light data has changed. It MUST have its mesh rebuilt.
                        Vector2Int neighborChunkV2Coord = worldData.GetChunkCoordFor(mod.GlobalPosition);
                        chunksToRebuildMesh.Add(new ChunkCoord(neighborChunkV2Coord));
                    }
                }

                // 3. Dispose of all the temporary job data.
                jobEntry.Value.map.Dispose();
                jobEntry.Value.sunLightQueue.Dispose();
                jobEntry.Value.blockLightQueue.Dispose();
                jobEntry.Value.sunLightRecalcQueue.Dispose();
                jobEntry.Value.mods.Dispose();

                completedCoords.Add(jobEntry.Key);
            }
        }

        // 4. After processing all completed jobs, request mesh rebuilds for all affected chunks.
        foreach (ChunkCoord coord in chunksToRebuildMesh)
        {
            if (chunks[coord.X, coord.Z] != null)
            {
                RequestChunkMeshRebuild(chunks[coord.X, coord.Z], immediate: true);
            }
        }

        // 5. Remove the completed jobs from our tracking dictionary
        foreach (ChunkCoord coord in completedCoords)
        {
            lightingJobs.Remove(coord);
        }
    }

    // A global SetLight method that ONLY sets the light value
    // This is used by the main thread when processing cross-chunk modifications.
    public void SetLight(Vector3 globalPos, byte lightValue, LightChannel channel)
    {
        ChunkData chunkData = worldData.RequestChunk(worldData.GetChunkCoordFor(globalPos), false);
        if (chunkData != null && chunkData.isPopulated)
        {
            Vector3Int localPos = worldData.GetLocalVoxelPositionInChunk(globalPos);
            int index = localPos.x + VoxelData.ChunkWidth * (localPos.y + VoxelData.ChunkHeight * localPos.z);
            uint oldPackedData = chunkData.map[index];
            uint newPackedData;

            // Use the channel to call the correct setter
            if (channel == LightChannel.Sun)
            {
                if (BurstVoxelDataBitMapping.GetSunlight(oldPackedData) == lightValue) return; // No change needed
                newPackedData = BurstVoxelDataBitMapping.SetSunLight(oldPackedData, lightValue);
            }
            else // Blocklight
            {
                if (BurstVoxelDataBitMapping.GetBlocklight(oldPackedData) == lightValue) return; // No change needed
                newPackedData = BurstVoxelDataBitMapping.SetBlockLight(oldPackedData, lightValue);
            }

            chunkData.map[index] = newPackedData;
        }
    }


    #region Voxel Modifications

    private void ApplyModifications()
    {
        applyingModifications = true;

        // Use a temporary list for modifications that need to be re-queued.
        // These will most likely be cross-chunk modifications in un-loaded chunks.
        List<Queue<VoxelMod>> deferredModifications = new List<Queue<VoxelMod>>();

        while (modifications.Count > 0)
        {
            Queue<VoxelMod> queue = modifications.Dequeue();
            bool batchFailed = false;

            while (queue.Count > 0)
            {
                VoxelMod v = queue.Dequeue();

                // --- 1. Get Chunk Data ---
                ChunkData chunkData = worldData.RequestChunk(worldData.GetChunkCoordFor(v.globalPosition), false);

                // If the chunk doesn't exist or its data hasn't been generated yet, defer this modification.
                if (chunkData == null || !chunkData.isPopulated)
                {
                    var tempList = new List<VoxelMod>(queue);
                    tempList.Insert(0, v);
                    deferredModifications.Add(new Queue<VoxelMod>(tempList));
                    batchFailed = true;
                    break; // Break from processing this batch and move to the next.
                }

                // --- 2. If chunk is ready, DELEGATE the modification ---
                // Get the local position within the chunk.
                Vector3Int localPos = worldData.GetLocalVoxelPositionInChunk(v.globalPosition);

                // Call the authoritative ModifyVoxel method in ChunkData.
                chunkData.ModifyVoxel(localPos, v.id, v.orientation);
            }

            // If part of the batch failed, we already re-queued it.
            if (batchFailed) continue;
        }

        // After checking all queues, add the deferred ones back for the next frame.
        foreach (var deferredQueue in deferredModifications)
        {
            modifications.Enqueue(deferredQueue);
        }

        applyingModifications = false;
    }

    #endregion

    /// <summary>
    /// Adds a chunk to the queue to have its mesh rebuilt.
    /// For priority, add it to the front of the list.
    /// </summary>
    public void RequestChunkMeshRebuild(Chunk chunk, bool immediate = false)
    {
        // We only add it if it's not already in the list to avoid redundant processing.
        if (chunk == null || chunksToBuildMesh.Contains(chunk)) return;

        if (immediate)
            chunksToBuildMesh.Insert(0, chunk); // Insert at the front
        else
            chunksToBuildMesh.Add(chunk);
    }

    private ChunkCoord GetChunkCoordFromVector3(Vector3 pos)
    {
        int x = Mathf.FloorToInt(pos.x / VoxelData.ChunkWidth);
        int z = Mathf.FloorToInt(pos.z / VoxelData.ChunkWidth);

        return new ChunkCoord(x, z);
    }

    public Chunk GetChunkFromVector3(Vector3 pos)
    {
        int x = Mathf.FloorToInt(pos.x / VoxelData.ChunkWidth);
        int z = Mathf.FloorToInt(pos.z / VoxelData.ChunkWidth);

        if (x == 0 && z == 0)
            Debug.Log($"Getting chunk for position: {pos}, Chunk: {x}, {z}");

        return chunks[x, z];
    }

    private void CheckViewDistance()
    {
        clouds.UpdateClouds();

        ChunkCoord playerCurrentChunkCoord = GetChunkCoordFromVector3(playerTransform.position);

        // Return early if the player hasn't moved outside the last chunk.
        if (playerCurrentChunkCoord.Equals(playerLastChunkCoord)) return;
        playerLastChunkCoord = playerCurrentChunkCoord;

        HashSet<ChunkCoord> previouslyActiveChunks = new HashSet<ChunkCoord>(activeChunks);
        HashSet<ChunkCoord> currentViewChunks = new HashSet<ChunkCoord>();

        int viewDist = settings.viewDistance;
        int loadDist = settings.loadDistance;

        // Use a spiral loop to check chunks outwards from the player's current position.
        // Determine the total number of chunks to check in a square grid around the player.
        int checkWidth = loadDist * 2 + 1;
        int chunksToCheck = checkWidth * checkWidth;
        SpiralLoop spiralLoop = new SpiralLoop();

        // Iterate a specific number of times to cover the whole area
        for (int i = 0; i < chunksToCheck; i++)
        {
            ChunkCoord thisChunkCoord = new ChunkCoord(playerCurrentChunkCoord.X + spiralLoop.X, playerCurrentChunkCoord.Z + spiralLoop.Z);

            if (IsChunkInWorld(thisChunkCoord))
            {
                // Schedule data generation for all chunks within load distance.
                ScheduleGeneration(thisChunkCoord);

                // If within view distance, it's a candidate for being active.
                if (Mathf.Abs(thisChunkCoord.X - playerCurrentChunkCoord.X) <= viewDist &&
                    Mathf.Abs(thisChunkCoord.Z - playerCurrentChunkCoord.Z) <= viewDist)
                {
                    currentViewChunks.Add(thisChunkCoord);
                }
            }

            // Next spiral coord
            spiralLoop.Next();
        }

        // Deactivate chunks that are no longer in view.
        foreach (ChunkCoord c in previouslyActiveChunks.AsParallel().Where(c => !currentViewChunks.Contains(c) && chunks[c.X, c.Z] != null))
        {
            chunks[c.X, c.Z].isActive = false;
        }

        // Activate chunks that have entered view.
        foreach (ChunkCoord c in currentViewChunks)
        {
            if (chunks[c.X, c.Z] == null)
            {
                chunks[c.X, c.Z] = new Chunk(c, createGameObject: true);
                RequestChunkMeshRebuild(chunks[c.X, c.Z]);
            }
            else if (!chunks[c.X, c.Z].isActive)
            {
                chunks[c.X, c.Z].isActive = true;
                RequestChunkMeshRebuild(chunks[c.X, c.Z]);
            }
        }

        // Update the master activeChunks set.
        activeChunks = currentViewChunks;
    }

    /// <summary>
    /// Finds the Y-coordinate of the highest solid voxel at a given X/Z position.
    /// It fist tries to get it from the actual chunk data, this respects voxel modifications (like trees),
    /// if that fails, it will use the expensive world generation code, this however doesn't respect voxel modifications.
    /// Should even that fail, it will return the world height.
    /// </summary>
    /// <param name="pos">The world position to check.</param>
    /// <returns>A Vector3 with the original X/Z and the new Y of the highest solid block.</returns>
    public Vector3Int GetHighestVoxel(Vector3Int pos)
    {
        const int yMax = VoxelData.ChunkHeight - 1;
        int x = pos.x;
        int y = pos.y;
        int z = pos.z;


        Vector3Int worldHeight = new Vector3Int(x, yMax, z);
        ChunkCoord thisChunk = new ChunkCoord(pos);

        // Voxel outside the world, highest voxel is world height.
        if (!worldData.IsVoxelInWorld(pos))
        {
            Debug.Log($"Voxel not in world for X / Y/ Z = {x} / {y} / {z}, returning world height.");
            return worldHeight;
        }

        // Find the chunk data for this position.
        // Requesting the chunk ensures that the data is loaded from disk or generated if it doesn't exist.
        // This is the only reliable way to get voxel data that includes modifications (like trees).
        Vector2Int chunkCoord = worldData.GetChunkCoordFor(pos);
        ChunkData chunkData = worldData.RequestChunk(chunkCoord, true);

        // Chunk is created and editable, calculate the highest voxel using chunkData function.
        if (chunkData != null)
        {
            Debug.Log($"Finding highest voxel for chunk {thisChunk.X} / {thisChunk.Z} in wold for X / Z = {x} / {z} using chunk function.");

            // Get the (highest) local voxel position within the chunk.
            Vector3Int localPos = worldData.GetLocalVoxelPositionInChunk(pos);
            Vector3Int highestVoxelLocal = chunkData.GetHighestVoxel(localPos);

            // Get the world position of the highest voxel.
            Vector3Int highestVoxel = new Vector3Int(x, highestVoxelLocal.y, z);
            Debug.Log($"Highest voxel in chunk {thisChunk.X} / {thisChunk.Z} is {highestVoxel}.");
            return highestVoxel;
        }

        // Chunk is not created, calculate the highest voxel using expensive world generation code.
        // NOTE: This will not include voxel modifications (like trees).
        for (int i = yMax; i > 0; i--)
        {
            Vector3Int currentVoxel = new Vector3Int(x, i, z);
            if (!blockTypes[GetVoxel(currentVoxel)].isSolid) continue;
            Debug.Log($"Finding highest voxel in wold for X / Z = {x} / {z} using expensive world generation code.");
            Debug.Log($"Highest voxel in chunk {thisChunk.X} / {thisChunk.Z} is {currentVoxel}.");
            return currentVoxel;
        }

        // Fallback, highest voxel is world height
        Debug.Log($"No solid voxels found for X / Z = {x} / {z}, returning world height.");
        return worldHeight;
    }

    public bool CheckForVoxel(Vector3 pos)
    {
        VoxelState? voxel = worldData.GetVoxelState(pos);
        return voxel.HasValue && blockTypes[voxel.Value.id].isSolid;
    }

    /// Returns true when voxel is solid & not water.
    public bool CheckForCollision(Vector3 pos)
    {
        VoxelState? voxel = worldData.GetVoxelState(pos);
        return voxel.HasValue && voxel.Value.Properties.isSolid && !voxel.Value.Properties.isWater;
    }

    public VoxelState? GetVoxelState(Vector3 pos)
    {
        return worldData.GetVoxelState(pos);
    }

    public bool inUI
    {
        get { return _inUI; }
        set
        {
            _inUI = value;
            Cursor.lockState = _inUI
                ? CursorLockMode.None // Makes cursor visible
                : CursorLockMode.Locked; // Makes cursor invisible and not able to go of screen

            // Toggle UI based on inUI state
            Cursor.visible = _inUI;
            creativeInventoryWindow.SetActive(_inUI);
            cursorSlot.SetActive(_inUI);
        }
    }

    #region World Generation

    // TODO: Logic move to WorldGen.GetVoxel, this should be removed and any logic calling this updated to use WorldGen.GetVoxel
    public byte GetVoxel(Vector3Int pos)
    {
        int yPos = Mathf.FloorToInt(pos.y);

        // ----- IMMUTABLE PASS -----
        // If outside of world, return air.
        if (!worldData.IsVoxelInWorld(pos))
            return 0;

        // If bottom block of chunk, return bedrock
        if (yPos == 0)
            return 8; // Bedrock

        // ----- BIOME SELECTION PASS -----
        float sumOfHeights = 0f;
        int count = 0;
        float strongestWeight = 0f;
        int strongestBiomeIndex = 0;

        for (int i = 0; i < biomes.Length; i++)
        {
            float weight = Noise.Get2DPerlin(new Vector2(pos.x, pos.z), biomes[i].offset, biomes[i].scale);

            // Keep track of which weight is strongest.
            if (weight > strongestWeight)
            {
                strongestWeight = weight;
                strongestBiomeIndex = i;
            }

            // Get the height of the terrain (for the current biome) and multiply it by its weight.
            float height = biomes[i].terrainHeight * Noise.Get2DPerlin(new Vector2(pos.x, pos.z), 0, biomes[i].terrainScale) * weight;

            // If the height value is greater than 0, add it to the sum of heights.
            if (height > 0)
            {
                sumOfHeights += height;
                count++;
            }
        }

        // Set biome to the one with the strongest weight.
        BiomeAttributes biome = biomes[strongestBiomeIndex];

        // Get the average of the heights.
        sumOfHeights /= count;
        int terrainHeight = Mathf.FloorToInt(sumOfHeights + VoxelData.SolidGroundHeight);


        // ----- BASIC TERRAIN PASS -----
        byte voxelValue = 0;

        if (yPos == terrainHeight)
        {
            voxelValue = biome.surfaceBlock; // Grass
        }
        else if (yPos < terrainHeight && yPos > terrainHeight - 4)
        {
            voxelValue = biome.subSurfaceBlock; // Dirt
        }
        else if (yPos > terrainHeight)
        {
            if (yPos < VoxelData.SeaLevel)
                return 19; // Water

            return 0; // Air
        }
        else
        {
            voxelValue = 1; // Stone
        }

        // ----- SECOND PASS -----
        if (settings.enableSecondPass && voxelValue == 1)
        {
            // Stone
            foreach (Lode lode in biome.lodes)
            {
                if (yPos > lode.minHeight && yPos < lode.maxHeight)
                {
                    if (Noise.Get3DPerlin(pos, lode.noiseOffset, lode.scale, lode.threshold))
                    {
                        voxelValue = lode.blockID;
                    }
                }
            }
        }

        // ----- MAJOR FLORA PASS -----
        if (settings.enableMajorFloraPass && yPos == terrainHeight && biome.placeMajorFlora)
        {
            if (Noise.Get2DPerlin(new Vector2Int(pos.x, pos.z), 0, biome.majorFloraZoneScale) > biome.majorFloraZoneThreshold)
            {
                if (Noise.Get2DPerlin(new Vector2Int(pos.x, pos.z), 2500, biome.majorFloraPlacementScale) > biome.majorFloraPlacementThreshold)
                {
                    Queue<VoxelMod> structureQueue = Structure.GenerateMajorFlora(biome.majorFloraIndex, pos, biome.minHeight, biome.maxHeight);
                    modifications.Enqueue(structureQueue);
                }
            }
        }


        return voxelValue;
    }

    #endregion

    private bool IsChunkInWorld(ChunkCoord coord)
    {
        return coord.X is >= 0 and < VoxelData.WorldSizeInChunks &&
               coord.Z is >= 0 and < VoxelData.WorldSizeInChunks;
    }

    #region Debug Information Methods

    public int GetActiveChunksCount()
    {
        return activeChunks.Count;
    }

    public int GetChunksToBuildMeshCount()
    {
        return chunksToBuildMesh.Count;
    }

    public int GetVoxelModificationsCount()
    {
        return modifications.Count;
    }

    #endregion
}

[Serializable]
public class BlockType
{
    public string blockName;
    public VoxelMeshData meshData;
    public bool isSolid;
    public bool renderNeighborFaces;
    public bool isWater;

    [Tooltip("How many light levels will be blocked by this block.")]
    [Range(0, 15)]
    public byte opacity = 15;

    [Tooltip("How many light levels will be emitted by this block.")]
    [Range(0, 15)]
    public byte lightEmission = 0;

    public Sprite icon;

    [Range(0, 64)]
    public int stackSize = 64;

    [Header("Block Behavior")]
    [Tooltip("Whether the block has any block behavior.")]
    public bool isActive;

    [Header("Texture Values")]
    public int backFaceTexture;

    public int frontFaceTexture;
    public int topFaceTexture;
    public int bottomFaceTexture;
    public int leftFaceTexture;
    public int rightFaceTexture;

    // Back, Front, Top, Bottom, Left, Right
    public int GetTextureID(int faceIndex)
    {
        switch (faceIndex)
        {
            case 0:
                return backFaceTexture;
            case 1:
                return frontFaceTexture;
            case 2:
                return topFaceTexture;
            case 3:
                return bottomFaceTexture;
            case 4:
                return leftFaceTexture;
            case 5:
                return rightFaceTexture;
            default:
                Debug.LogError("Error in GetTextureID; invalid face index");
                return 0;
        }
    }

    public override string ToString()
    {
        return $"BlockType: {{ Name = {blockName}, IsSolid = {isSolid}, IsWater = {isWater}, Opacity = {opacity}, RenderNeighborFaces = {renderNeighborFaces}, Icon = {icon}, StackSize = {stackSize} }}";
    }
}

public struct VoxelMod
{
    public Vector3Int globalPosition;
    public byte id;
    public byte orientation;

    public VoxelMod(Vector3Int _globalPosition, byte _id, byte _orientation = 1)
    {
        globalPosition = _globalPosition;
        id = _id;
        orientation = _orientation; // Default to Front / North (1)
    }
}


[Serializable]
public class Settings
{
    [Header("Game Data")]
    public string version = "0.0.01";


    [Header("Save System")]
    public bool loadSaveDataOnStartup = false;


    [Header("Performance")]
    // TODO: Meke loadDistance dynamic based on considered viewDistance (eg: viewDistance + 2)
    public int loadDistance = 7;

    public int viewDistance = 5;

    [Tooltip("The maximum number of lighting jobs that can be scheduled in a single frame. Prevents performance drops from lighting cascades.")]
    public int maxLightJobsPerFrame = 8;

    [InitializationField]
    [Tooltip("PERFORMANCE INTENSIVE - Enable the lighting system, on large caves this can cause the game to hang for a couple of seconds.")]
    public bool enableLighting = true;

    public CloudStyle clouds = CloudStyle.Fancy;

    [Header("Controls")]
    [Range(0.1f, 10f)]
    public float mouseSensitivityX = 1.2f;

    [Range(0.1f, 10f)]
    public float mouseSensitivityY = 1.2f;


    [Header("World Generation")]
    [InitializationField]
    [Tooltip("Second Pass: Lode generation")]
    public bool enableSecondPass = true;

    [InitializationField]
    [Tooltip("Structure Pass: Tree generation")]
    public bool enableMajorFloraPass = true;


    [Header("Bonus Stuff")]
    public bool enableChunkLoadAnimations = false;
}