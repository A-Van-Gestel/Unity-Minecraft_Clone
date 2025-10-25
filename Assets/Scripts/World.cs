using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Data;
using Helpers;
using JetBrains.Annotations;
using Jobs;
using Jobs.BurstData;
using Jobs.Data;
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
    public Material liquidMaterial;
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

    [Header("Debug")]
    [Tooltip("The prefab to use for chunk borders.")]
    public GameObject chunkBorderPrefab;

    // --- Shader Properties ---
    private static readonly int ShaderGlobalLightLevel = Shader.PropertyToID("GlobalLightLevel");
    private static readonly int ShaderMinGlobalLightLevel = Shader.PropertyToID("minGlobalLightLevel");
    private static readonly int ShaderMaxGlobalLightLevel = Shader.PropertyToID("maxGlobalLightLevel");

    // --- Fluid Vertex Data ---
    private NativeArray<float> waterVertexTemplates;
    private NativeArray<float> lavaVertexTemplates;

    // --- Job Management Data ---
    private NativeArray<BiomeAttributesJobData> biomesJobData;
    private NativeArray<LodeJobData> allLodesJobData;
    private NativeArray<BlockTypeJobData> blockTypesJobData;
    private NativeArray<CustomMeshData> customMeshesJobData;
    private NativeArray<CustomFaceData> customFacesJobData;
    private NativeArray<CustomVertData> customVertsJobData;
    private NativeArray<int> customTrisJobData;

    // track the JobHandles and the allocated data together
    private Dictionary<ChunkCoord, (JobHandle handle, NativeArray<uint> map, NativeArray<byte> heightMap, NativeQueue<VoxelMod> mods)> generationJobs = new Dictionary<ChunkCoord, (JobHandle, NativeArray<uint>, NativeArray<byte>, NativeQueue<VoxelMod>)>();
    private Dictionary<ChunkCoord, (JobHandle handle, MeshDataJobOutput meshData)> meshJobs = new Dictionary<ChunkCoord, (JobHandle, MeshDataJobOutput)>();

    private Dictionary<ChunkCoord, LightingJobData> lightingJobs = new Dictionary<ChunkCoord, LightingJobData>();

    // --- Chunk Border Visualization ---
    private Dictionary<ChunkCoord, GameObject> chunkBorders = new Dictionary<ChunkCoord, GameObject>();
    private Transform chunkBorderParent;
    private bool lastChunkBordersState;

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
        foreach (LightingJobData jobData in lightingJobs.Values)
        {
            jobData.handle.Complete();
            jobData.Dispose();
        }

        lightingJobs.Clear();

        // 2. Dispose of the persistent global data.
        if (biomesJobData.IsCreated) biomesJobData.Dispose();
        if (allLodesJobData.IsCreated) allLodesJobData.Dispose();
        if (blockTypesJobData.IsCreated) blockTypesJobData.Dispose();
        if (customMeshesJobData.IsCreated) customMeshesJobData.Dispose();
        if (customFacesJobData.IsCreated) customFacesJobData.Dispose();
        if (customVertsJobData.IsCreated) customVertsJobData.Dispose();
        if (customTrisJobData.IsCreated) customTrisJobData.Dispose();

        // 3. Dispose of fluid vertex templates.
        if (waterVertexTemplates.IsCreated) waterVertexTemplates.Dispose();
        if (lavaVertexTemplates.IsCreated) lavaVertexTemplates.Dispose();
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

        // --- Initialize Chunk Border Visualization ---
        GameObject borderParentGO = new GameObject("Chunk Borders");
        borderParentGO.transform.SetParent(transform);
        chunkBorderParent = borderParentGO.transform;
        lastChunkBordersState = settings.showChunkBorders;

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

        // Toggle chunk border visibility if the setting has changed.
        if (lastChunkBordersState != settings.showChunkBorders)
        {
            foreach (var borderObject in chunkBorders.Values)
            {
                if (borderObject != null)
                {
                    borderObject.SetActive(settings.showChunkBorders);
                }
            }

            lastChunkBordersState = settings.showChunkBorders;
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

    // --- JOB-RELATED METHODS ---
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
                allLodesJobData[currentLodeIndex + j] = new LodeJobData(lode);
            }

            // Populate the biome data, including the start index and count for its lodes.
            biomesJobData[i] = new BiomeAttributesJobData(biomes[i], currentLodeIndex);

            // Advance the master lode index for the next biome.
            currentLodeIndex += biomes[i].lodes.Length;
        }

         // --- Block Types & Custom Meshes ---
        // --- Step 1: Collect all unique custom mesh assets
        List<VoxelMeshData> uniqueCustomMeshes = new List<VoxelMeshData>();
        foreach (var blockType in blockTypes)
        {
            if (blockType.meshData != null && !uniqueCustomMeshes.Contains(blockType.meshData))
            {
                uniqueCustomMeshes.Add(blockType.meshData);
            }
        }
        
        // --- Step 2: Flatten custom mesh data into temporary lists
        List<CustomMeshData> customMeshesList = new List<CustomMeshData>();
        List<CustomFaceData> customFacesList = new List<CustomFaceData>();
        List<CustomVertData> customVertsList = new List<CustomVertData>();
        List<int> customTrisList = new List<int>();

        foreach (var meshAsset in uniqueCustomMeshes)
        {
            customMeshesList.Add(new CustomMeshData 
            { 
                faceStartIndex = customFacesList.Count,
                faceCount = meshAsset.faces.Length
            });
            
            if (meshAsset.faces.Length > 6)
                Debug.LogWarning($"VoxelMeshData asset '{meshAsset.name}' has more than 6 faces. Only the first 6 will be used.");
            
            foreach (var faceAsset in meshAsset.faces)
            {
                customFacesList.Add(new CustomFaceData
                {
                    vertStartIndex = customVertsList.Count,
                    vertCount = faceAsset.vertData.Length,
                    triStartIndex = customTrisList.Count,
                    triCount = faceAsset.triangles.Length
                });
                
                foreach (var vertAsset in faceAsset.vertData)
                {
                    customVertsList.Add(new CustomVertData { position = vertAsset.position, uv = vertAsset.uv });
                }
                customTrisList.AddRange(faceAsset.triangles);
            }
        }
        
        // --- Step 3: Convert lists to persistent NativeArrays
        customMeshesJobData = new NativeArray<CustomMeshData>(customMeshesList.ToArray(), Allocator.Persistent);
        customFacesJobData = new NativeArray<CustomFaceData>(customFacesList.ToArray(), Allocator.Persistent);
        customVertsJobData = new NativeArray<CustomVertData>(customVertsList.ToArray(), Allocator.Persistent);
        customTrisJobData = new NativeArray<int>(customTrisList.ToArray(), Allocator.Persistent);

        // --- Step 4: Populate blockTypesJobData, including the custom mesh index
        blockTypesJobData = new NativeArray<BlockTypeJobData>(blockTypes.Length, Allocator.Persistent);
        for (int i = 0; i < blockTypes.Length; i++)
        {
            int customMeshIndex = -1;
            if (blockTypes[i].meshData != null)
            {
                customMeshIndex = uniqueCustomMeshes.IndexOf(blockTypes[i].meshData);
            }
            blockTypesJobData[i] = new BlockTypeJobData(blockTypes[i], customMeshIndex);
        }

        // --- Prepare Fluid Vertex Templates ---
        // ... (rest of the method is unchanged)
        const string fluidDataPath = "FluidData";
        var waterAsset = Resources.Load<FluidMeshData>($"{fluidDataPath}/FluidData_Water");
        if (waterAsset)
        {
            waterVertexTemplates = new NativeArray<float>(waterAsset.vertexYPositions, Allocator.Persistent);
        }

        var lavaAsset = Resources.Load<FluidMeshData>($"{fluidDataPath}/FluidData_Lava");
        if (lavaAsset)
        {
            lavaVertexTemplates = new NativeArray<float>(lavaAsset.vertexYPositions, Allocator.Persistent);
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
                chunkData.Populate(jobEntry.Value.map, jobEntry.Value.heightMap);
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
                jobEntry.Value.heightMap.Dispose();
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
        var outputHeightMap = new NativeArray<byte>(VoxelData.ChunkWidth * VoxelData.ChunkWidth, Allocator.Persistent);

        ChunkGenerationJob job = new ChunkGenerationJob
        {
            seed = VoxelData.Seed,
            chunkPosition = chunkPos,
            blockTypes = blockTypesJobData,
            biomes = biomesJobData,
            allLodes = allLodesJobData,
            outputMap = outputMap,
            outputHeightMap = outputHeightMap,
            modifications = modificationsQueue.AsParallelWriter()
        };

        JobHandle handle = job.Schedule();

        // Store the handle and ALL associated native containers together.
        generationJobs.Add(coord, (handle, outputMap, outputHeightMap, modificationsQueue));
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
            customMeshes = customMeshesJobData,
            customFaces = customFacesJobData,
            customVerts = customVertsJobData,
            customTris = customTrisJobData,
            waterVertexTemplates = waterVertexTemplates,
            lavaVertexTemplates = lavaVertexTemplates,
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

        // --- Prepare Data for the Job ---
        // Persistent data that the main thread needs to access after the job is done.
        var jobData = new LightingJobData
        {
            map = new NativeArray<uint>(chunk.chunkData.map, Allocator.Persistent),
            mods = new NativeList<LightModification>(Allocator.Persistent),
            isStable = new NativeArray<bool>(1, Allocator.Persistent),
            sunLightQueue = chunk.chunkData.GetSunlightQueueForJob(Allocator.Persistent),
            blockLightQueue = chunk.chunkData.GetBlocklightQueueForJob(Allocator.Persistent),
            sunLightRecalcQueue = new NativeQueue<Vector2Int>(Allocator.Persistent)
        };

        // Consume sunlight recalculation requests from the global queue for this chunk
        // Use a temporary list to store columns that we are consuming for this job.
        List<Vector2Int> consumedColumns = new List<Vector2Int>();
        foreach (Vector2Int col in worldData.sunlightRecalculationQueue)
        {
            // Check if the global column position belongs to the current chunk.
            if (worldData.GetChunkCoordFor(new Vector3(col.x, 0, col.y)) == chunk.chunkData.position)
            {
                // Convert global column position to local and add to the job's queue.
                jobData.sunLightRecalcQueue.Enqueue(new Vector2Int(col.x - chunk.chunkData.position.x, col.y - chunk.chunkData.position.y));
                consumedColumns.Add(col);
            }
        }

        // After iterating, remove ONLY the consumed items from the global HashSet.
        // This prevents us from deleting requests meant for other chunks.
        foreach (var col in consumedColumns)
        {
            worldData.sunlightRecalculationQueue.Remove(col);
        }

        // Get all 8 Neighbor Maps and the heightmap (Read-Only, disposed by job dependency)
        var heightmap = new NativeArray<byte>(chunk.chunkData.heightMap, Allocator.TempJob);
        Vector2Int p = chunk.chunkData.position;
        int w = VoxelData.ChunkWidth;
        var neighborN = worldData.GetChunkMapForJob(p + new Vector2Int(0, w), Allocator.TempJob);
        var neighborE = worldData.GetChunkMapForJob(p + new Vector2Int(w, 0), Allocator.TempJob);
        var neighborS = worldData.GetChunkMapForJob(p + new Vector2Int(0, -w), Allocator.TempJob);
        var neighborW = worldData.GetChunkMapForJob(p + new Vector2Int(-w, 0), Allocator.TempJob);
        var neighborNE = worldData.GetChunkMapForJob(p + new Vector2Int(w, w), Allocator.TempJob);
        var neighborSE = worldData.GetChunkMapForJob(p + new Vector2Int(w, -w), Allocator.TempJob);
        var neighborSW = worldData.GetChunkMapForJob(p + new Vector2Int(-w, -w), Allocator.TempJob);
        var neighborNW = worldData.GetChunkMapForJob(p + new Vector2Int(-w, w), Allocator.TempJob);

        var job = new NeighborhoodLightingJob
        {
            // Writable data for the central chunk
            map = jobData.map,
            chunkPosition = chunk.chunkData.position,
            sunlightBfsQueue = jobData.sunLightQueue,
            blocklightBfsQueue = jobData.blockLightQueue,
            sunlightColumnRecalcQueue = jobData.sunLightRecalcQueue,

            // Read-only heightmap & neighbor data
            heightmap = heightmap,
            neighborN = neighborN, neighborE = neighborE, neighborS = neighborS, neighborW = neighborW,
            neighborNE = neighborNE, neighborSE = neighborSE, neighborSW = neighborSW, neighborNW = neighborNW,

            blockTypes = blockTypesJobData,

            // Output lists
            crossChunkLightMods = jobData.mods,
            isStable = jobData.isStable
        };

        // Schedule the main job
        JobHandle jobHandle = job.Schedule();

        // Create a dependency chain to dispose all the TempJob neighbor arrays after the main job is complete.
        var disposalHandles = new NativeArray<JobHandle>(9, Allocator.TempJob);
        disposalHandles[0] = heightmap.Dispose(jobHandle);
        disposalHandles[0] = neighborN.Dispose(jobHandle);
        disposalHandles[1] = neighborE.Dispose(jobHandle);
        disposalHandles[2] = neighborS.Dispose(jobHandle);
        disposalHandles[3] = neighborW.Dispose(jobHandle);
        disposalHandles[4] = neighborNE.Dispose(jobHandle);
        disposalHandles[5] = neighborSE.Dispose(jobHandle);
        disposalHandles[6] = neighborSW.Dispose(jobHandle);
        disposalHandles[7] = neighborNW.Dispose(jobHandle);

        // Combine all disposal jobs into one handle.
        JobHandle combinedDisposalHandle = JobHandle.CombineDependencies(disposalHandles);
        jobData.handle = disposalHandles.Dispose(combinedDisposalHandle);

        // Reset the flag, because we are now scheduling a job to process these changes.
        chunk.chunkData.hasLightChangesToProcess = false;

        // Store the handle and all persistent data that needs to be processed and disposed of later.
        lightingJobs.Add(coord, jobData);
    }

    public void AddModification(VoxelMod mod)
    {
        // Create a new queue containing just this single modification
        Queue<VoxelMod> singleModQueue = new Queue<VoxelMod>();
        singleModQueue.Enqueue(mod);

        // Add this single-item queue to the main modifications queue
        modifications.Enqueue(singleModQueue);
    }

    public void NotifyChunkModified(Vector2Int chunkPos, Vector3Int localVoxelPos, bool immediate)
    {
        // 1. The chunk that was modified always needs a rebuild.
        ChunkCoord coord = new ChunkCoord(chunkPos);
        if (chunks[coord.X, coord.Z] != null)
        {
            RequestChunkMeshRebuild(chunks[coord.X, coord.Z], immediate);
        }

        // 2. If the modification happened on a border, queue the neighbor with the same priority.
        // Check X-axis borders
        if (localVoxelPos.x == 0)
            QueueNeighborRebuild(chunkPos + new Vector2Int(-VoxelData.ChunkWidth, 0), immediate);
        else if (localVoxelPos.x == VoxelData.ChunkWidth - 1)
            QueueNeighborRebuild(chunkPos + new Vector2Int(VoxelData.ChunkWidth, 0), immediate);

        // Check Z-axis borders
        if (localVoxelPos.z == 0)
            QueueNeighborRebuild(chunkPos + new Vector2Int(0, -VoxelData.ChunkWidth), immediate);
        else if (localVoxelPos.z == VoxelData.ChunkWidth - 1)
            QueueNeighborRebuild(chunkPos + new Vector2Int(0, VoxelData.ChunkWidth), immediate);
    }

    private void QueueNeighborRebuild(Vector2Int neighborV2Coord, bool immediate = false)
    {
        if (worldData.chunks.TryGetValue(neighborV2Coord, out ChunkData neighborData) && neighborData.chunk != null)
        {
            RequestChunkMeshRebuild(neighborData.chunk, immediate);
        }
    }

    private void RequestNeighborMeshRebuilds(ChunkCoord coord)
    {
        // Define coordinates for all 4 direct neighbors
        Vector2Int north = new Vector2Int(coord.X, coord.Z + 1);
        Vector2Int south = new Vector2Int(coord.X, coord.Z - 1);
        Vector2Int east = new Vector2Int(coord.X + 1, coord.Z);
        Vector2Int west = new Vector2Int(coord.X - 1, coord.Z);

        // Queue rebuilds for all valid neighbors
        QueueNeighborRebuild(north);
        QueueNeighborRebuild(south);
        QueueNeighborRebuild(east);
        QueueNeighborRebuild(west);
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
                LightingJobData jobData = jobEntry.Value;
                bool isChunkStable = jobData.isStable[0];

                ChunkData chunkData = worldData.RequestChunk(new Vector2Int(jobEntry.Key.X * VoxelData.ChunkWidth, jobEntry.Key.Z * VoxelData.ChunkWidth), false);
                if (chunkData != null && chunkData.isPopulated)
                {
                    // 1. Copy the modified map back to the central chunk.
                    jobData.map.CopyTo(chunkData.map);

                    // 2. Process cross-chunk modifications calculated by the job.
                    foreach (LightModification mod in jobData.mods)
                    {
                        // Find the chunk that this modification affects.
                        Vector2Int neighborChunkV2Coord = worldData.GetChunkCoordFor(mod.GlobalPosition);
                        ChunkData neighborChunk = worldData.RequestChunk(neighborChunkV2Coord, false);

                        // If the neighbor doesn't exist or isn't generated, we can't do anything.
                        if (neighborChunk == null || !neighborChunk.isPopulated) continue;

                        // Get the local position and flat array index for the voxel in the neighbor chunk.
                        Vector3Int localPos = worldData.GetLocalVoxelPositionInChunk(mod.GlobalPosition);
                        int index = localPos.x + VoxelData.ChunkWidth * (localPos.y + VoxelData.ChunkHeight * localPos.z);

                        // It's crucial to check if the index is valid for the neighbor's map.
                        if (index < 0 || index >= neighborChunk.map.Length) continue;

                        uint oldPackedData = neighborChunk.map[index];
                        byte oldLightLevel;
                        uint newPackedData;

                        // Determine which light channel to modify based on the modification request.
                        if (mod.Channel == LightChannel.Sun)
                        {
                            byte currentSunlight = BurstVoxelDataBitMapping.GetSunlight(oldPackedData);

                            // If the block already has full sunlight (15), and this modification would decrease it, ignore the modification.
                            // This prevents a job using stale data from overwriting a correct value set by another job.
                            if (currentSunlight == 15 && mod.LightLevel < 15)
                            {
                                continue; // Skip this modification
                            }

                            oldLightLevel = BurstVoxelDataBitMapping.GetSunlight(oldPackedData);
                            newPackedData = BurstVoxelDataBitMapping.SetSunLight(oldPackedData, mod.LightLevel);
                        }
                        else // Blocklight
                        {
                            oldLightLevel = BurstVoxelDataBitMapping.GetBlocklight(oldPackedData);
                            newPackedData = BurstVoxelDataBitMapping.SetBlockLight(oldPackedData, mod.LightLevel);
                        }

                        // Only proceed if the light level is actually changing to avoid redundant work.
                        if (oldLightLevel != mod.LightLevel)
                        {
                            // 1. Apply the new light value directly to the neighbor's map data.
                            neighborChunk.map[index] = newPackedData;

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
                    RequestNeighborMeshRebuilds(jobEntry.Key);
                }
                else
                {
                    // The chunk is NOT stable. It needs another lighting pass.
                    // We re-assert the flag to ensure the scheduler picks it up next frame.
                    if (chunkData != null) chunkData.hasLightChangesToProcess = true;
                }

                // 4. Dispose of all the job's persistent data.
                jobData.Dispose();

                completedCoords.Add(jobEntry.Key);
            }
        }

        // 5. After processing all completed jobs, request mesh rebuilds for all affected chunks.
        foreach (ChunkCoord coord in chunksToRebuildMesh)
        {
            if (chunks[coord.X, coord.Z] != null)
            {
                RequestChunkMeshRebuild(chunks[coord.X, coord.Z], immediate: true);
            }
        }

        // 6. Remove the completed jobs from our tracking dictionary
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

    /// <summary>
    /// Enqueues a batch of voxel modifications to be processed.
    /// </summary>
    /// <param name="voxelMods">The queue of voxel modifications to process.</param>
    public void EnqueueVoxelModifications(Queue<VoxelMod> voxelMods)
    {
        modifications.Enqueue(voxelMods);
    }

    /// <summary>
    /// Applies all queued voxel modifications.
    /// </summary>
    private void ApplyModifications()
    {
        applyingModifications = true;

        // A list for modifications that target ungenerated chunks, to be re-queued for the next frame.
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

                // --- 2. Check Placement Rules ---
                // Special Case: If the mod is to place Air (ID 0), it's a "break" action.
                // We should always allow this, unless the target is unbreakable.
                if (v.id == 0)
                {
                    VoxelState? stateToBreak = worldData.GetVoxelState(v.globalPosition);
                    if (stateToBreak.HasValue && (blockTypes[stateToBreak.Value.id].tags & BlockTags.UNBREAKABLE) != 0)
                    {
                        continue; // Cannot break an unbreakable block.
                    }
                }
                else // This is a "place" action, so run the full rule check.
                {
                    bool canPlace = true;
                    VoxelState? existingState = worldData.GetVoxelState(v.globalPosition);

                    if (existingState.HasValue)
                    {
                        switch (v.rule)
                        {
                            case ReplacementRule.ForcePlace:
                                // Force placement, but still respect Unbreakable blocks.
                                if ((blockTypes[existingState.Value.id].tags & BlockTags.UNBREAKABLE) != 0)
                                    canPlace = false;
                                break;

                            case ReplacementRule.OnlyReplaceAir:
                                // Only allow placement if the existing block is Air (ID 0).
                                if (existingState.Value.id != 0)
                                    canPlace = false;
                                break;

                            case ReplacementRule.Default:
                            default:
                                // --- Use the default Block Tag system ---
                                BlockType incomingProps = blockTypes[v.id];
                                BlockType existingProps = blockTypes[existingState.Value.id];

                                // Rule A: Nothing can replace an Unbreakable block.
                                if ((existingProps.tags & BlockTags.UNBREAKABLE) != 0)
                                {
                                    canPlace = false;
                                }
                                // Rule B: If the incoming block has specific replacement rules...
                                else if (incomingProps.canReplaceTags != BlockTags.NONE)
                                {
                                    // ...and the existing block has NO tags that match, it can't be placed.
                                    // The bitwise AND (&) will be 0 if there are no common flags.
                                    if ((existingProps.tags & incomingProps.canReplaceTags) == 0)
                                    {
                                        // We make one exception: anything can replace "Air", which we define as a block with NONE tags.
                                        if (existingProps.tags != BlockTags.NONE)
                                        {
                                            canPlace = false;
                                        }
                                    }
                                }
                                // Rule C: If the incoming block is set to NONE, it means it can only replace Air.
                                else if (existingProps.tags != BlockTags.NONE)
                                {
                                    canPlace = false;
                                }

                                break;
                        }
                    }

                    if (!canPlace)
                    {
                        continue; // Skip this VoxelMod, move to the next in the queue.
                    }
                }

                // --- 3. If chunk is ready, Apply Modification ---
                // Get the local position within the chunk.
                Vector3Int localPos = worldData.GetLocalVoxelPositionInChunk(v.globalPosition);

                // Call the authoritative ModifyVoxel method in ChunkData, passing the entire mod struct.
                chunkData.ModifyVoxel(localPos, v);
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
    public void RequestChunkMeshRebuild([CanBeNull] Chunk chunk, bool immediate = false)
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

    [CanBeNull]
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
            if (chunkBorders.TryGetValue(c, out GameObject borderObject))
            {
                Destroy(borderObject);
                chunkBorders.Remove(c);
            }

            chunks[c.X, c.Z].isActive = false;
        }

        // Activate chunks that have entered view.
        foreach (ChunkCoord c in currentViewChunks)
        {
            if (chunks[c.X, c.Z] == null)
            {
                chunks[c.X, c.Z] = new Chunk(c, createGameObject: true);
                CreateChunkBorder(c);
                RequestChunkMeshRebuild(chunks[c.X, c.Z]);
            }
            else if (!chunks[c.X, c.Z].isActive)
            {
                if (!chunkBorders.ContainsKey(c))
                {
                    CreateChunkBorder(c);
                }

                chunks[c.X, c.Z].isActive = true;
                RequestChunkMeshRebuild(chunks[c.X, c.Z]);
            }
        }

        // Update the master activeChunks set.
        activeChunks = currentViewChunks;
    }

    #region Debug Methods

    /// <summary>
    /// Creates a visualisation of the chunk border.
    /// </summary>
    /// <param name="coord">The chunk coordinate.</param>
    private void CreateChunkBorder(ChunkCoord coord)
    {
        if (chunkBorderPrefab == null)
        {
            Debug.LogError("ChunkBorderPrefab must be assigned in the World inspector.", this);
            return;
        }

        if (chunkBorders.ContainsKey(coord)) return;

        GameObject borderObject = Instantiate(chunkBorderPrefab, chunkBorderParent);
        borderObject.name = $"Border {coord.X}, {coord.Z}";
        borderObject.transform.position = new Vector3(coord.X * VoxelData.ChunkWidth, 0, coord.Z * VoxelData.ChunkWidth);

        borderObject.SetActive(settings.showChunkBorders);
        chunkBorders.Add(coord, borderObject);
    }

    #endregion

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
        return voxel.HasValue && voxel.Value.Properties.isSolid && voxel.Value.Properties.fluidType == FluidType.None;
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

    [Header("Debug")]
    [Tooltip("Visualize chunk borders in the scene view.")]
    public bool showChunkBorders = false;
}