using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Data;
using Data.JobData;
using Data.NativeData;
using DebugVisualizations;
using Helpers;
using JetBrains.Annotations;
using Jobs;
using Jobs.BurstData;
using Jobs.Data;
using MyBox;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Random = UnityEngine.Random;

public class World : MonoBehaviour
{
    public Settings settings;
    private readonly string _settingFilePath = Application.dataPath + "/settings.json";

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

    private Transform _playerTransform;
    private Camera _playerCamera;

    [InitializationField]
    public Vector3 spawnPosition;

    [InitializationField]
    public Vector3 spawnPositionOffset = new Vector3(0.5f, 1.1f, 0.5f);

    [Header("Blocks & Materials")]
    [SerializeField]
    [Tooltip("The BlockDatabase asset that contains all the block data & materials.")]
    public BlockDatabase blockDatabase;

    public BlockType[] blockTypes => blockDatabase.blockTypes;
    public Material opaqueMaterial => blockDatabase.opaqueMaterial;
    public Material transparentMaterial => blockDatabase.transparentMaterial;
    public Material liquidMaterial => blockDatabase.liquidMaterial;

    public Chunk[,] chunks { get; } = new Chunk[VoxelData.WorldSizeInChunks, VoxelData.WorldSizeInChunks];

    private HashSet<ChunkCoord> _activeChunks = new HashSet<ChunkCoord>();
    private readonly List<ChunkCoord> _tempActiveChunkList = new List<ChunkCoord>(); // Used to avoid modifying activeChunks while iterating, and to avoid GC allocations.
    public ChunkCoord PlayerChunkCoord;
    private ChunkCoord _playerLastChunkCoord;

    private readonly List<Chunk> _chunksToBuildMesh = new List<Chunk>();
    public readonly Queue<Chunk> ChunksToDraw = new Queue<Chunk>();

    private bool _applyingModifications = false;
    private readonly Queue<Queue<VoxelMod>> _modifications = new Queue<Queue<VoxelMod>>();

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

    public WorldJobManager JobManager;

    [Header("Paths")]
    [MyBox.ReadOnly]
    public string appSaveDataPath;

    [Header("Debug")]
    [Tooltip("The prefab to use for chunk borders.")]
    public GameObject chunkBorderPrefab;

    [Tooltip("The VoxelVisualizer object in the scene.")]
    public VoxelVisualizer voxelVisualizer;

    [Tooltip("Selects which voxel state to visualize in the world.")]
    public DebugVisualizationMode visualizationMode;

    private DebugVisualizationMode _lastVisualizationMode;
    private readonly HashSet<ChunkCoord> _chunksToUpdateVisualization = new HashSet<ChunkCoord>();

    // --- Shader Properties ---
    private static readonly int ShaderGlobalLightLevel = Shader.PropertyToID("GlobalLightLevel");
    private static readonly int ShaderMinGlobalLightLevel = Shader.PropertyToID("minGlobalLightLevel");
    private static readonly int ShaderMaxGlobalLightLevel = Shader.PropertyToID("maxGlobalLightLevel");

    // --- Fluid Vertex Data ---
    public FluidVertexTemplatesNativeData FluidVertexTemplates;

    // --- Job Management Data ---
    public JobDataManager JobDataManager;

    // --- Chunk Border Visualization ---
    private readonly Dictionary<ChunkCoord, GameObject> _chunkBorders = new Dictionary<ChunkCoord, GameObject>();
    private Transform _chunkBorderParent;
    private bool _lastChunkBordersState;

    #region Singleton pattern

    public static World Instance { get; private set; }

    private void Awake()
    {
        // If the instance value is not null and not *this*, we've somehow ended up with more than one World component.
        // Since another one has already been assigned, delete this one.
        if (Instance is not null && Instance != this)
        {
            Destroy(gameObject);
        }
        else
        {
            Instance = this;
            appSaveDataPath = Application.persistentDataPath;
            JobManager = new WorldJobManager(this);
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
        foreach (GenerationJobData job in JobManager.generationJobs.Values)
        {
            job.Handle.Complete();
            job.Dispose();
        }

        JobManager.generationJobs.Clear();

        // -- Mesh generation jobs --
        foreach (var job in JobManager.meshJobs.Values)
        {
            job.handle.Complete();
            job.meshData.Dispose();
        }

        JobManager.meshJobs.Clear();

        // -- Lighting jobs --
        foreach (LightingJobData jobData in JobManager.lightingJobs.Values)
        {
            jobData.Handle.Complete();
            jobData.Dispose();
        }

        JobManager.lightingJobs.Clear();

        // 2. Dispose of the persistent global data.
        JobDataManager.Dispose();

        // 3. Dispose of fluid vertex templates.
        FluidVertexTemplates.Dispose();
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
        _playerTransform = player.GetComponent<Transform>();

        // Get main camera.
        _playerCamera = Camera.main!;

        // --- Load / Create Settings ---
        // Create settings file if it doesn't yet exist, after that, load it.
        if (!File.Exists(_settingFilePath) || Application.isEditor)
        {
            string jsonExport = JsonUtility.ToJson(settings, true);
            File.WriteAllText(_settingFilePath, jsonExport);
#if UNITY_EDITOR
            AssetDatabase.Refresh(); // Refresh Unity's asset database.
# endif
        }

#if !UNITY_EDITOR
        string jsonImport = File.ReadAllText(_settingFilePath);
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
        GameObject borderParentGo = new GameObject("Chunk Borders");
        borderParentGo.transform.SetParent(transform);
        _chunkBorderParent = borderParentGo.transform;
        _lastChunkBordersState = settings.showChunkBorders;

        // --- STEP 1: DETERMINE INITIAL PLAYER POSITION ---
        // Set initial spawnPosition to the center of the world for X & Z, and top of the world for Y.
        spawnPosition = new Vector3Int(VoxelData.WorldCentre, VoxelData.ChunkHeight - 1, VoxelData.WorldCentre);
        _playerTransform.position = spawnPosition;
        PlayerChunkCoord = GetChunkCoordFromVector3(_playerTransform.position);

        // --- STEP 2: SYNCHRONOUSLY GENERATE ALL DATA ---
        Debug.Log("--- Generating all data within load distance ---");

        Stopwatch stopwatch = new Stopwatch(); // Create stopwatch to measure time taken for initial data generation.
        stopwatch.Start();

        // 1. First, just schedule generation for everything in the load radius.
        int loadedChunks = LoadChunksInDataPass();

        // 2. Force complete ONLY the data-related jobs (generation and lighting).
        //    Now, instead of a blocking call, we yield to (wait for) another coroutine.
        //    The code will PAUSE here and will not continue until ForceCompleteDataJobsCoroutine is finished.
        yield return StartCoroutine(ForceCompleteDataJobsCoroutine());

        stopwatch.Stop();
        long totalMilliseconds = stopwatch.ElapsedMilliseconds;
        float avgTime = (float)totalMilliseconds / loadedChunks;
        Debug.Log($"Initial data generation took {totalMilliseconds} ms for {loadedChunks} chunks (Load distance: {Instance.settings.loadDistance})");
        Debug.Log($"Average time per chunk {avgTime} ms");

        stopwatch.Reset();

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
        _playerTransform.position = spawnPosition;

        Debug.Log("Initializing clouds...");
        clouds?.Initialize();

        Debug.Log("Staring world tick...");
        StartCoroutine(Tick());

        Debug.Log("World initialization complete.");
        Debug.Log("--- Startup complete ---");
    }

    /// <summary>
    /// Loads all chunks in the data pass.
    /// </summary>
    /// <returns>The number of chunks loaded.</returns>
    private int LoadChunksInDataPass()
    {
        int loadedChunks = 0;
        int loadDist = settings.loadDistance;
        // We don't need the spiral loop here, a simple square loop is fine for startup.
        for (int x = -loadDist; x <= loadDist; x++)
        {
            for (int z = -loadDist; z <= loadDist; z++)
            {
                ChunkCoord coord = new ChunkCoord(PlayerChunkCoord.X + x, PlayerChunkCoord.Z + z);
                if (IsChunkInWorld(coord))
                {
                    // This just schedules the generation job.
                    JobManager.ScheduleGeneration(coord);
                    loadedChunks++;
                }
            }
        }

        return loadedChunks;
    }

    private IEnumerator ForceCompleteDataJobsCoroutine()
    {
        int safetyBreak = 0;
        const int maxIterations = 5_000; // Should be more than enough for startup, this should be higher for (mush) larger view distances (eg: 20+).

        while (JobManager.generationJobs.Count > 0 || JobManager.lightingJobs.Count > 0 || HasPendingLightChangesOnMainThread())
        {
            // Complete any finished generation jobs
            JobManager.ProcessGenerationJobs();
            ApplyModifications();

            // Try to schedule lighting for chunks that are ready
            foreach (ChunkData chunkData in worldData.Chunks.Values)
            {
                if (chunkData.IsPopulated && chunkData.HasLightChangesToProcess)
                {
                    ChunkCoord coord = new ChunkCoord(chunkData.position);
                    if (!JobManager.lightingJobs.ContainsKey(coord) && AreNeighborsDataReady(coord))
                    {
                        Chunk tempChunk = new Chunk(coord, false);
                        JobManager.ScheduleLightingUpdate(tempChunk);
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
                Debug.LogError($"Remaining chunks in generation job: {JobManager.generationJobs.Count} | lighting job: {JobManager.lightingJobs.Count}");
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
        foreach (var chunkData in worldData.Chunks.Values)
        {
            if (chunkData != null && chunkData.HasLightChangesToProcess)
            {
                return true;
            }
        }

        return false;
    }

    private void CompleteAndProcessLightingJobs()
    {
        // Force complete all scheduled lighting jobs immediately.
        foreach (var job in JobManager.lightingJobs.Values)
        {
            job.Handle.Complete();
        }

        // Process their results.
        JobManager.ProcessLightingJobs();
    }

    private void CompleteAndProcessMeshJobs()
    {
        // Force complete all scheduled mesh jobs immediately.
        foreach (var job in JobManager.meshJobs.Values)
        {
            job.handle.Complete();
        }

        // Process their results.
        JobManager.ProcessMeshJobs();
    }

    public void SetGlobalLightValue()
    {
        Shader.SetGlobalFloat(ShaderGlobalLightLevel, globalLightLevel);
        _playerCamera.backgroundColor = Color.Lerp(night, day, globalLightLevel);
    }

    private IEnumerator Tick()
    {
        while (true)
        {
            foreach (ChunkCoord coord in _activeChunks)
            {
                chunks[coord.X, coord.Z].TickUpdate();
            }

            yield return new WaitForSeconds(VoxelData.TickLength);
        }
        // ReSharper disable once IteratorNeverReturns
    }

    private void Update()
    {
        PlayerChunkCoord = GetChunkCoordFromVector3(_playerTransform.position);

        // Only update the chunks if the player has moved from the chunk they were previously on.
        if (!PlayerChunkCoord.Equals(_playerLastChunkCoord))
        {
            // Queue up chunks for generation
            CheckViewDistance();
        }

        // Toggle chunk border visibility if the setting has changed.
        if (_lastChunkBordersState != settings.showChunkBorders)
        {
            foreach (var borderObject in _chunkBorders.Values)
            {
                if (borderObject != null)
                {
                    borderObject.SetActive(settings.showChunkBorders);
                }
            }

            _lastChunkBordersState = settings.showChunkBorders;
        }

        // Debug: Voxel Visualization Management
        HandleVisualization();

        _playerLastChunkCoord = PlayerChunkCoord;

        // --- Process Job System ---
        // 1. Process any jobs that have finished generating chunk data.
        //    This might add new chunks to the chunksToBuildMesh list.
        JobManager.ProcessGenerationJobs();

        // 2. Apply all queued voxel modifications (from player and world gen).
        if (!_applyingModifications)
            ApplyModifications();

        // 3. Process completed lighting jobs from the PREVIOUS frame.
        JobManager.ProcessLightingJobs();

        // 4. Scan active chunks for lighting work and schedule jobs (New "Pull" system)
        if (settings.enableLighting)
        {
            int lightJobsScheduled = 0;

            // 1. Clear the persistent list from the previous frame's use.
            _tempActiveChunkList.Clear();

            // 2. Populate the persistent list with the current active chunks.
            //    This does NOT create a new list, it just adds to the existing one.
            foreach (ChunkCoord coord in _activeChunks)
            {
                _tempActiveChunkList.Add(coord);
            }

            // 3. Iterate over the temporary, safe-to-modify list.
            foreach (ChunkCoord chunkCoord in _tempActiveChunkList)
            {
                if (lightJobsScheduled >= settings.maxLightJobsPerFrame) break; // Respect the throttle

                Chunk chunkToUpdate = chunks[chunkCoord.X, chunkCoord.Z];

                // If chunk is valid, has pending changes, and no job is currently running for it...
                if (chunkToUpdate != null && chunkToUpdate.ChunkData.HasLightChangesToProcess && !JobManager.lightingJobs.ContainsKey(chunkToUpdate.Coord))
                {
                    // NOTE: jobManager.ScheduleLightingUpdate might indirectly modify the 'activeChunks' set
                    // by affecting neighbor states. This pattern protects against that.
                    JobManager.ScheduleLightingUpdate(chunkToUpdate);
                    lightJobsScheduled++;
                }
            }
        }

        // 5. Process completed mesh jobs from the PREVIOUS frame.
        JobManager.ProcessMeshJobs();

        // 6. Schedule NEW mesh jobs for chunks that now need them.
        if (_chunksToBuildMesh.Count > 0)
        {
            for (int i = _chunksToBuildMesh.Count - 1; i >= 0; i--)
            {
                Chunk chunk = _chunksToBuildMesh[i];
                if (chunk != null)
                {
                    // jobManager.ScheduleMeshing will any lighting changes jobs are running and if neighbors are ready.
                    // If any lighting jobs are running, it will return false.
                    // If neighbors are ready, it will schedule the job, and we can remove this from the list.
                    if (JobManager.ScheduleMeshing(chunk))
                    {
                        _chunksToBuildMesh.RemoveAt(i);
                    }
                }
                else
                {
                    // Remove null chunks from the list.
                    _chunksToBuildMesh.RemoveAt(i);
                }
            }
        }

        // The chunksToDraw queue is populated by ApplyMeshData in Chunk.cs
        if (ChunksToDraw.Count > 0)
        {
            ChunksToDraw.Dequeue().CreateMesh();
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
        var biomesJobData = new NativeArray<BiomeAttributesJobData>(biomes.Length, Allocator.Persistent);
        var allLodesJobData = new NativeArray<LodeJobData>(totalLodeCount, Allocator.Persistent);

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
        foreach (var blockType in blockDatabase.blockTypes)
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

        foreach (VoxelMeshData meshAsset in uniqueCustomMeshes)
        {
            customMeshesList.Add(new CustomMeshData
            {
                FaceStartIndex = customFacesList.Count,
                FaceCount = meshAsset.faces.Length,
            });

            if (meshAsset.faces.Length > 6)
                Debug.LogWarning($"VoxelMeshData asset '{meshAsset.name}' has more than 6 faces. Only the first 6 will be used.");

            foreach (FaceMeshData faceAsset in meshAsset.faces)
            {
                customFacesList.Add(new CustomFaceData
                {
                    VertStartIndex = customVertsList.Count,
                    VertCount = faceAsset.vertData.Length,
                    TriStartIndex = customTrisList.Count,
                    TriCount = faceAsset.triangles.Length,
                });

                foreach (VertData vertAsset in faceAsset.vertData)
                {
                    customVertsList.Add(new CustomVertData { Position = vertAsset.position, UV = vertAsset.uv });
                }

                customTrisList.AddRange(faceAsset.triangles);
            }
        }

        // --- Step 3: Convert lists to persistent NativeArrays
        var customMeshesJobData = new NativeArray<CustomMeshData>(customMeshesList.ToArray(), Allocator.Persistent);
        var customFacesJobData = new NativeArray<CustomFaceData>(customFacesList.ToArray(), Allocator.Persistent);
        var customVertsJobData = new NativeArray<CustomVertData>(customVertsList.ToArray(), Allocator.Persistent);
        var customTrisJobData = new NativeArray<int>(customTrisList.ToArray(), Allocator.Persistent);

        // --- Step 4: Populate blockTypesJobData, including the custom mesh index
        var blockTypesJobData = new NativeArray<BlockTypeJobData>(blockDatabase.blockTypes.Length, Allocator.Persistent);
        for (int i = 0; i < blockDatabase.blockTypes.Length; i++)
        {
            int customMeshIndex = -1;
            if (blockDatabase.blockTypes[i].meshData != null)
            {
                customMeshIndex = uniqueCustomMeshes.IndexOf(blockDatabase.blockTypes[i].meshData);
            }

            blockTypesJobData[i] = new BlockTypeJobData(blockDatabase.blockTypes[i], customMeshIndex);
        }

        // --- Step 5: Create the final JobDataManager ---
        JobDataManager = new JobDataManager(
            biomesJobData,
            allLodesJobData,
            blockTypesJobData,
            customMeshesJobData,
            customFacesJobData,
            customVertsJobData,
            customTrisJobData
        );

        // --- Prepare Fluid Vertex Templates ---
        FluidTemplates fluidTemplates = ResourceLoader.LoadFluidTemplates();
        FluidVertexTemplates = new FluidVertexTemplatesNativeData(fluidTemplates);
    }

    public void AddModification(VoxelMod mod)
    {
        // Create a new queue containing just this single modification
        Queue<VoxelMod> singleModQueue = new Queue<VoxelMod>();
        singleModQueue.Enqueue(mod);

        // Add this single-item queue to the main modifications queue
        _modifications.Enqueue(singleModQueue);
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

        // --- Debug Visualization ---
        // Update visualization for this chunk.
        AddChunksToUpdateVisualization(new ChunkCoord(chunkPos));

        // If the modification happened on a border, queue the neighbor for visualization.
        // Check X-axis borders
        if (localVoxelPos.x == 0)
            AddChunksToUpdateVisualization(new ChunkCoord(chunkPos + new Vector2Int(-VoxelData.ChunkWidth, 0)));
        else if (localVoxelPos.x == VoxelData.ChunkWidth - 1)
            AddChunksToUpdateVisualization(new ChunkCoord(chunkPos + new Vector2Int(VoxelData.ChunkWidth, 0)));

        // Check Z-axis borders
        if (localVoxelPos.z == 0)
            AddChunksToUpdateVisualization(new ChunkCoord(chunkPos + new Vector2Int(0, -VoxelData.ChunkWidth)));
        else if (localVoxelPos.z == VoxelData.ChunkWidth - 1)
            AddChunksToUpdateVisualization(new ChunkCoord(chunkPos + new Vector2Int(0, VoxelData.ChunkWidth)));
    }

    private void QueueNeighborRebuild(Vector2Int neighborV2Coord, bool immediate = false)
    {
        if (worldData.Chunks.TryGetValue(neighborV2Coord, out ChunkData neighborData) && neighborData.Chunk != null)
        {
            RequestChunkMeshRebuild(neighborData.Chunk, immediate);
        }
    }

    public void RequestNeighborMeshRebuilds(ChunkCoord coord)
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
                if (JobManager.generationJobs.ContainsKey(neighborCoord))
                {
                    return false; // Neighbor data is not ready yet.
                }

                // Does the placeholder ChunkData exist in the world dictionary?
                Vector2Int chunkPos = new Vector2Int(neighborCoord.X * VoxelData.ChunkWidth, neighborCoord.Z * VoxelData.ChunkWidth);
                if (!worldData.Chunks.ContainsKey(chunkPos))
                {
                    // This case is unlikely with the new CheckViewDistance, but is a good safeguard.
                    return false;
                }
            }
        }

        // If we get here, all neighbors are ready.
        return true;
    }

    public bool AreNeighborsReadyAndLit(ChunkCoord coord)
    {
        // Check all 4 horizontal neighbors
        foreach (int faceIndex in VoxelData.HorizontalFaceChecksIndices)
        {
            Vector3Int offset = VoxelData.FaceChecks[faceIndex];
            ChunkCoord neighborCoord = new ChunkCoord(coord.X + offset.x, coord.Z + offset.z);

            if (IsChunkInWorld(neighborCoord))
            {
                // Is a terrain generation job for this neighbor still running?
                if (JobManager.generationJobs.ContainsKey(neighborCoord))
                {
                    return false; // Neighbor terrain data is not ready.
                }

                // Is a lighting job for this neighbor currently running?
                if (JobManager.lightingJobs.ContainsKey(neighborCoord))
                {
                    return false; // Neighbor is still calculating light, we must wait.
                }

                // Also check the main-thread flag for the neighbor.
                Vector2Int neighborV2Pos = new Vector2Int(neighborCoord.X * VoxelData.ChunkWidth, neighborCoord.Z * VoxelData.ChunkWidth);
                if (worldData.Chunks.TryGetValue(neighborV2Pos, out ChunkData neighborData) && neighborData.HasLightChangesToProcess)
                {
                    return false; // Neighbor has pending light changes that haven't even been scheduled yet.
                }
            }
        }

        // If we get here, all neighbors are stable.
        return true;
    }


    public bool AreNeighborsDataReady(ChunkCoord coord)
    {
        // Check all 4 horizontal neighbors
        foreach (int faceIndex in VoxelData.HorizontalFaceChecksIndices)
        {
            Vector3Int offset = VoxelData.FaceChecks[faceIndex];
            ChunkCoord neighborCoord = new ChunkCoord(coord.X + offset.x, coord.Z + offset.z);

            if (IsChunkInWorld(neighborCoord))
            {
                // Is a generation job for this neighbor still running?
                if (JobManager.generationJobs.ContainsKey(neighborCoord))
                {
                    return false; // Neighbor data is not ready yet.
                }
            }
        }

        // If we get here, all neighbors have finished their data generation.
        return true;
    }

    // A global SetLight method that ONLY sets the light value
    // This is used by the main thread when processing cross-chunk modifications.
    public void SetLight(Vector3 globalPos, byte lightValue, LightChannel channel)
    {
        ChunkData chunkData = worldData.RequestChunk(worldData.GetChunkCoordFor(globalPos), false);
        if (chunkData != null && chunkData.IsPopulated)
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
        _modifications.Enqueue(voxelMods);
    }

    /// <summary>
    /// Applies all queued voxel modifications.
    /// </summary>
    /// <remarks>
    /// TODO: I believe this method should be private, as forcing ALL modifications to be applied at once should be avoided
    /// </remarks>
    internal void ApplyModifications()
    {
        _applyingModifications = true;

        // A list for modifications that target ungenerated chunks to be re-queued for the next frame.
        List<Queue<VoxelMod>> deferredModifications = new List<Queue<VoxelMod>>();

        while (_modifications.Count > 0)
        {
            Queue<VoxelMod> queue = _modifications.Dequeue();
            bool batchFailed = false;

            while (queue.Count > 0)
            {
                VoxelMod v = queue.Dequeue();

                // --- 1. Get Chunk Data ---
                ChunkData chunkData = worldData.RequestChunk(worldData.GetChunkCoordFor(v.GlobalPosition), false);

                // If the chunk doesn't exist or its data hasn't been generated yet, defer this modification.
                if (chunkData == null || !chunkData.IsPopulated)
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
                if (v.ID == 0)
                {
                    VoxelState? stateToBreak = worldData.GetVoxelState(v.GlobalPosition);
                    if (stateToBreak.HasValue && (blockDatabase.blockTypes[stateToBreak.Value.id].tags & BlockTags.UNBREAKABLE) != 0)
                    {
                        continue; // Cannot break an unbreakable block.
                    }
                }
                else // This is a "place" action, so run the full rule check.
                {
                    bool canPlace = true;
                    VoxelState? existingState = worldData.GetVoxelState(v.GlobalPosition);

                    if (existingState.HasValue)
                    {
                        switch (v.Rule)
                        {
                            case ReplacementRule.ForcePlace:
                                // Force placement, but still respect Unbreakable blocks.
                                if ((blockDatabase.blockTypes[existingState.Value.id].tags & BlockTags.UNBREAKABLE) != 0)
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
                                BlockType incomingProps = blockDatabase.blockTypes[v.ID];
                                BlockType existingProps = blockDatabase.blockTypes[existingState.Value.id];

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
                Vector3Int localPos = worldData.GetLocalVoxelPositionInChunk(v.GlobalPosition);
                chunkData.ModifyVoxel(localPos, v);

                // --- 4. Neighbor Activation ---
                // After any modification, the World is now responsible for waking up all 6 neighbors.
                for (int i = 0; i < 6; i++)
                {
                    // Get the global position of the neighbor.
                    Vector3Int neighborPos = v.GlobalPosition + VoxelData.FaceChecks[i];
                    VoxelState? neighborState = worldData.GetVoxelState(neighborPos);

                    // If the neighbor exists and has behavior, ensure it's active.
                    if (neighborState.HasValue && neighborState.Value.Properties.isActive)
                    {
                        Chunk neighborChunk = GetChunkFromVector3(neighborPos);
                        if (neighborChunk != null)
                        {
                            Vector3Int localPosInNeighbor = neighborChunk.GetVoxelPositionInChunkFromGlobalVector3(neighborPos);
                            neighborChunk.AddActiveVoxel(localPosInNeighbor);
                        }
                    }
                }
            }

            // If part of the batch failed, we already re-queued it.
            if (batchFailed) continue;
        }

        // After checking all queues, add the deferred ones back for the next frame.
        foreach (var deferredQueue in deferredModifications)
        {
            _modifications.Enqueue(deferredQueue);
        }

        _applyingModifications = false;
    }

    #endregion

    /// <summary>
    /// Adds a chunk to the queue to have its mesh rebuilt.
    /// For priority, add it to the front of the list.
    /// </summary>
    public void RequestChunkMeshRebuild([CanBeNull] Chunk chunk, bool immediate = false)
    {
        // We only add it if it's not already in the list to avoid redundant processing.
        if (chunk == null || _chunksToBuildMesh.Contains(chunk)) return;

        if (immediate)
            _chunksToBuildMesh.Insert(0, chunk); // Insert at the front
        else
            _chunksToBuildMesh.Add(chunk);
    }

    private static ChunkCoord GetChunkCoordFromVector3(Vector3 pos)
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

        ChunkCoord playerCurrentChunkCoord = GetChunkCoordFromVector3(_playerTransform.position);

        // Return early if the player hasn't moved outside the last chunk.
        if (playerCurrentChunkCoord.Equals(_playerLastChunkCoord)) return;
        _playerLastChunkCoord = playerCurrentChunkCoord;

        HashSet<ChunkCoord> previouslyActiveChunks = new HashSet<ChunkCoord>(_activeChunks);
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
                JobManager.ScheduleGeneration(thisChunkCoord);

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
            if (_chunkBorders.TryGetValue(c, out GameObject borderObject))
            {
                Destroy(borderObject);
                _chunkBorders.Remove(c);
            }

            chunks[c.X, c.Z].isActive = false;

            // Debug: Clear visualization.
            if (voxelVisualizer != null)
                voxelVisualizer.ClearChunkVisualization(c);
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
                if (!_chunkBorders.ContainsKey(c))
                {
                    CreateChunkBorder(c);
                }

                chunks[c.X, c.Z].isActive = true;
                RequestChunkMeshRebuild(chunks[c.X, c.Z]);
            }

            // If the chunk has no light changes to process, update its visualization.
            if (!chunks[c.X, c.Z].ChunkData.HasLightChangesToProcess)
            {
                // Debug: Update visualization for this chunk.
                AddChunksToUpdateVisualization(c);
            }
        }

        // Update the master activeChunks set.
        _activeChunks = currentViewChunks;
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

        if (_chunkBorders.ContainsKey(coord)) return;

        GameObject borderObject = Instantiate(chunkBorderPrefab, _chunkBorderParent);
        borderObject.name = $"Border {coord.X}, {coord.Z}";
        borderObject.transform.position = new Vector3(coord.X * VoxelData.ChunkWidth, 0, coord.Z * VoxelData.ChunkWidth);

        borderObject.SetActive(settings.showChunkBorders);
        _chunkBorders.Add(coord, borderObject);
    }

    /// <summary>
    /// Adds a chunk to the list of chunks to update visualization for.
    /// </summary>
    /// <param name="chunkCoord">The chunk coordinate.</param>
    public void AddChunksToUpdateVisualization(ChunkCoord chunkCoord)
    {
        _chunksToUpdateVisualization.Add(chunkCoord);
    }

    private void HandleVisualization()
    {
        // If the visualizer isn't set, do nothing.
        if (voxelVisualizer == null) return;

        // --- 1. Check if the visualization mode has changed ---
        if (_lastVisualizationMode != visualizationMode)
        {
            voxelVisualizer.ClearAll(); // Clear any previous visualization.

            // If the new mode is not 'None', request an update for all currently active chunks.
            if (visualizationMode != DebugVisualizationMode.None)
            {
                foreach (ChunkCoord coord in _activeChunks)
                {
                    AddChunksToUpdateVisualization(coord);
                }
            }

            _lastVisualizationMode = visualizationMode;
        }

        // --- 2. Process any pending visualization updates ---
        if (visualizationMode != DebugVisualizationMode.None && _chunksToUpdateVisualization.Count > 0)
        {
            var chunksReadyForVisualization = new List<ChunkCoord>();
            // Identify which chunks are actually ready to be visualized.
            foreach (ChunkCoord coord in _chunksToUpdateVisualization)
            {
                Chunk chunk = chunks[coord.X, coord.Z];
                // A chunk is ready if it exists, is not currently processing a lighting job,
                // and has no pending lighting changes on the main thread.
                if (chunk != null && !JobManager.lightingJobs.ContainsKey(coord) && !chunk.ChunkData.HasLightChangesToProcess)
                {
                    chunksReadyForVisualization.Add(coord);
                }
            }


            // Pre-cache all required data in one go for chunks that are ready
            var chunkDataCache = new Dictionary<ChunkCoord, Dictionary<Vector3Int, Color>>();
            foreach (ChunkCoord coord in chunksReadyForVisualization)
            {
                if (chunks[coord.X, coord.Z] != null)
                {
                    chunkDataCache[coord] = GetVoxelDataForVisualization(chunks[coord.X, coord.Z]);
                }
            }

            // Iterate through the cached data to draw meshes
            foreach (var cachedChunk in chunkDataCache)
            {
                ChunkCoord coord = cachedChunk.Key;

                // Get neighbor data from the cache, or null if not available.
                chunkDataCache.TryGetValue(new ChunkCoord(coord.X, coord.Z + 1), out var northData);
                chunkDataCache.TryGetValue(new ChunkCoord(coord.X, coord.Z - 1), out var southData);
                chunkDataCache.TryGetValue(new ChunkCoord(coord.X + 1, coord.Z), out var eastData);
                chunkDataCache.TryGetValue(new ChunkCoord(coord.X - 1, coord.Z), out var westData);

                // Call the visualizer method with all neighbor data.
                voxelVisualizer.UpdateChunkVisualization(coord, cachedChunk.Value, northData, southData, eastData, westData);
            }

            // Remove only the processed chunks from the update set.
            // Chunks that were not ready will remain in the set to be checked next frame.
            foreach (var coord in chunksReadyForVisualization)
            {
                _chunksToUpdateVisualization.Remove(coord);
            }
        }
    }

    /// <summary>
    /// Gathers the positions and colors of voxels to be visualized for a given chunk,
    /// based on the current visualization mode.
    /// </summary>
    private Dictionary<Vector3Int, Color> GetVoxelDataForVisualization(Chunk chunk)
    {
        var voxelsToDraw = new Dictionary<Vector3Int, Color>();
        if (chunk == null || !chunk.ChunkData.IsPopulated) return voxelsToDraw;

        switch (visualizationMode)
        {
            case DebugVisualizationMode.ActiveVoxels:
                // Instead of checking every voxel, iterate only through the known active list.
                foreach (var localPos in chunk.ActiveVoxels)
                {
                    voxelsToDraw[localPos] = new Color(1f, 0f, 0f, 0.7f); // Red for active
                }

                break;

            // For other modes, we iterate but now skip air blocks.
            case DebugVisualizationMode.Sunlight:
            case DebugVisualizationMode.Blocklight:
            case DebugVisualizationMode.FluidLevel:
                for (int i = 0; i < chunk.ChunkData.map.Length; i++)
                {
                    // --- OPTIMIZATION: Get ID first and skip if it's air ---
                    uint packedData = chunk.ChunkData.map[i];
                    if (BurstVoxelDataBitMapping.GetId(packedData) == 0)
                    {
                        continue; // Skip air blocks entirely.
                    }

                    // Convert flat index to 3D position only when needed.
                    int x = i % VoxelData.ChunkWidth;
                    int y = i / VoxelData.ChunkWidth % VoxelData.ChunkHeight;
                    int z = i / (VoxelData.ChunkWidth * VoxelData.ChunkHeight);
                    var localPos = new Vector3Int(x, y, z);

                    var state = new VoxelState(packedData);
                    Color? color = null;

                    if (visualizationMode == DebugVisualizationMode.Sunlight && state.Sunlight > 0)
                    {
                        color = new Color(1f, 1f, 0f, state.Sunlight / 15f * 0.8f); // Yellow
                    }
                    else if (visualizationMode == DebugVisualizationMode.Blocklight && state.Blocklight > 0)
                    {
                        color = new Color(1f, 0.5f, 0f, state.Blocklight / 15f * 0.8f); // Orange
                    }
                    else if (visualizationMode == DebugVisualizationMode.FluidLevel && state.Properties.fluidType != FluidType.None)
                    {
                        // Make color fade from bright blue (level 0) to dark blue
                        float levelRatio = (15 - state.FluidLevel) / 15f;
                        color = new Color(0f, levelRatio * 0.7f, 1f, 0.7f);
                    }

                    if (color.HasValue)
                    {
                        voxelsToDraw[localPos] = color.Value;
                    }
                }

                break;
        }

        return voxelsToDraw;
    }

    #endregion

    /// <summary>
    /// Finds the Y-coordinate of the highest solid voxel at a given X/Z position.
    /// It fist tries to get it from the actual chunk data, this respects voxel modifications (like trees),
    /// if that fails, it will use the expensive world generation code, this however doesn't respect voxel modifications.
    /// Should even that fail, it will return the world height.
    /// </summary>
    /// <param name="worldPos">The world position to check.</param>
    /// <returns>A Vector3 with the original X/Z and the new Y of the highest solid block.</returns>
    public Vector3Int GetHighestVoxel(Vector3Int worldPos)
    {
        const int yMax = VoxelData.ChunkHeight - 1;
        int x = worldPos.x;
        int y = worldPos.y;
        int z = worldPos.z;


        Vector3Int worldHeight = new Vector3Int(x, yMax, z);
        ChunkCoord thisChunk = new ChunkCoord(worldPos);

        // Voxel outside the world, highest voxel is world height.
        if (!worldData.IsVoxelInWorld(worldPos))
        {
            Debug.Log($"Voxel not in world for X / Y/ Z = {x} / {y} / {z}, returning world height.");
            return worldHeight;
        }

        // Find the chunk data for this position.
        // Requesting the chunk ensures that the data is loaded from disk or generated if it doesn't exist.
        // This is the only reliable way to get voxel data that includes modifications (like trees).
        Vector2Int chunkCoord = worldData.GetChunkCoordFor(worldPos);
        ChunkData chunkData = worldData.RequestChunk(chunkCoord, true);

        // Chunk is created and editable, calculate the highest voxel using chunkData function.
        if (chunkData != null)
        {
            Debug.Log($"Finding highest voxel for chunk {thisChunk.X} / {thisChunk.Z} in wold for X / Z = {x} / {z} using chunk function.");

            // Get the (highest) local voxel position within the chunk.
            Vector3Int localPos = worldData.GetLocalVoxelPositionInChunk(worldPos);
            Vector3Int highestVoxelLocal = chunkData.GetHighestVoxel(localPos);

            // Get the world position of the highest voxel.
            Vector3Int highestVoxel = new Vector3Int(x, highestVoxelLocal.y, z);
            Debug.Log($"Highest voxel in chunk {thisChunk.X} / {thisChunk.Z} is {highestVoxel}.");
            return highestVoxel;
        }

        Debug.Log($"Chunk {thisChunk.X} / {thisChunk.Z} is not created, accurate result is not possible.");

        // Chunk is not created, calculate the highest voxel using expensive world generation code.
        // NOTE: This will not include voxel modifications (like trees).
        Debug.Log($"Finding highest voxel in wold for X / Z = {x} / {z} using expensive world generation code. This will not include voxel modifications (eg: trees)");
        for (int i = yMax; i > 0; i--)
        {
            Vector3Int currentVoxel = new Vector3Int(x, i, z);
            byte voxelBlockId = WorldGen.GetVoxel(currentVoxel, VoxelData.Seed, JobDataManager.BiomesJobData, JobDataManager.AllLodesJobData);
            if (!blockDatabase.blockTypes[voxelBlockId].isSolid) continue;
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
        return voxel.HasValue && blockDatabase.blockTypes[voxel.Value.id].isSolid;
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
        // ReSharper disable once ArrangeAccessorOwnerBody
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

    private static bool IsChunkInWorld(ChunkCoord coord)
    {
        return coord.X is >= 0 and < VoxelData.WorldSizeInChunks &&
               coord.Z is >= 0 and < VoxelData.WorldSizeInChunks;
    }

    #region Debug Information Methods

    public int GetActiveChunksCount()
    {
        return _activeChunks.Count;
    }

    public int GetChunksToBuildMeshCount()
    {
        return _chunksToBuildMesh.Count;
    }

    public int GetVoxelModificationsCount()
    {
        return _modifications.Count;
    }

    public int GetTotalActiveVoxelsInWorld()
    {
        int total = 0;

        foreach (ChunkCoord coord in _activeChunks)
        {
            if (chunks[coord.X, coord.Z] != null)
            {
                total += chunks[coord.X, coord.Z].GetActiveVoxelCount();
            }
        }

        return total;
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