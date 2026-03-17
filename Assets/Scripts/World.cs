using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
using Serialization;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Pool;
using Debug = UnityEngine.Debug;
using Random = UnityEngine.Random;

public class World : MonoBehaviour
{
    public Settings settings;

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

    private readonly Dictionary<ChunkCoord, Chunk> _chunkMap = new Dictionary<ChunkCoord, Chunk>();

    private readonly HashSet<ChunkCoord> _activeChunks = new HashSet<ChunkCoord>();
    public ChunkCoord PlayerChunkCoord;
    private ChunkCoord _playerLastChunkCoord = new ChunkCoord(int.MinValue, int.MinValue);

    private readonly List<Chunk> _chunksToBuildMesh = new List<Chunk>();
    private readonly HashSet<ChunkCoord> _chunksToBuildMeshSet = new HashSet<ChunkCoord>();

    public readonly Queue<Chunk> ChunksToDraw = new Queue<Chunk>();

    private bool _applyingModifications;
    private readonly Queue<VoxelMod> _modifications = new Queue<VoxelMod>();

    // UI
    [Header("UI")]
    public GameObject debugScreen;

    public GameObject creativeInventoryWindow;
    public GameObject cursorSlot;
    private bool _inUI;

    // Clouds
    [Header("Clouds")]
    public Clouds clouds;

    [Header("World Data")]
    public WorldData worldData;

    public WorldJobManager JobManager;

    [Header("Paths")]
    [MyBox.ReadOnly]
    public string appSaveDataPath; // TODO: Should used by the "Save system classes"

    [Header("Debug")]
    [Tooltip("The prefab to use for chunk borders.")]
    public GameObject chunkBorderPrefab;

    [Tooltip("The VoxelVisualizer object in the scene.")]
    public VoxelVisualizer voxelVisualizer;

    [Tooltip("Selects which voxel state to visualize in the world.")]
    public DebugVisualizationMode visualizationMode;

    private DebugVisualizationMode _lastVisualizationMode;
    private readonly HashSet<ChunkCoord> _chunksToUpdateVisualization = new HashSet<ChunkCoord>();

    // --- Storage & Serialization ---
    public ChunkStorageManager StorageManager;
    public ModificationManager ModManager;
    public LightingStateManager LightingStateManager;
    public bool IsVolatileMode { get; private set; }

    // --- Chunk Pooling ---
    public ChunkPoolManager ChunkPool { get; private set; }

    // --- Shader Properties ---
    private static readonly int s_shaderGlobalLightLevel = Shader.PropertyToID("GlobalLightLevel");
    private static readonly int s_shaderMinGlobalLightLevel = Shader.PropertyToID("minGlobalLightLevel");
    private static readonly int s_shaderMaxGlobalLightLevel = Shader.PropertyToID("maxGlobalLightLevel");

    // --- Fluid Vertex Data ---
    public FluidVertexTemplatesNativeData FluidVertexTemplates;

    // --- Job Management Data ---
    public JobDataManager JobDataManager;

    // --- Chunk Border Visualization ---
    private readonly Dictionary<ChunkCoord, GameObject> _chunkBorders = new Dictionary<ChunkCoord, GameObject>();
    private Transform _chunkBorderParent;
    private bool _lastChunkBordersState;

    // --- Cached Collections for GC Optimization ---
    private readonly HashSet<ChunkCoord> _currentViewChunks = new HashSet<ChunkCoord>();
    private readonly List<ChunkCoord> _chunksToRemove = new List<ChunkCoord>();

    // --- Transient flags ---
    /// <summary>
    /// Indicates whether the world startup process has completed and the world is ready to be used.
    /// </summary>
    private bool _isWorldLoaded;

    /// <summary>
    /// Public accessor for world load state. True once <see cref="StartWorld"/> has fully completed.
    /// </summary>
    public bool IsWorldLoaded => _isWorldLoaded;

    // --- Cancellation Token for Async Saves ---
    private CancellationTokenSource _shutdownTokenSource;

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

            // Initialize World Job Manager
            JobManager = new WorldJobManager(this);

            // Initialize Pool Manager
            ChunkPool = new ChunkPoolManager(transform);

            // --- Prepare Job-Safe Data ---
            PrepareJobData();
        }
    }

    private void OnEnable()
    {
        _shutdownTokenSource = new CancellationTokenSource();
    }


    /// Ensures global state (Inventory, Pending Mods) is saved when closing.
    private void OnApplicationQuit()
    {
        // Only save if the world successfully loaded AND persistence is allowed.
        if (_isWorldLoaded && settings.EnablePersistence)
        {
            Debug.Log("[OnApplicationQuit] Game Quitting... Saving World.");

            // 1. Cancel any pending async saves to prevent thread conflicts
            _shutdownTokenSource?.Cancel();

            // 2. Brief delay to let cancellation propagate (Fixes Race Condition)
            //    This allows background threads to hit the "if (cancelled) return" check
            //    before we start locking files on the main thread.
            Thread.Sleep(100);

            Debug.Log($"[OnApplicationQuit] Total chunks in world: {worldData.Chunks.Count}");
            Debug.Log($"[OnApplicationQuit] Chunks marked as modified: {worldData.ModifiedChunks.Count}");

            // 3. Save all active/modified chunks SYNCHRONOUSLY.
            SaveAllModifiedChunks(true);

            // 4. Save Metadata and Pending Queues
            SaveSystem.SaveWorld(this);

            // 5. Flush and Close Storage
            // This ensures all FileStreams write their buffers to the physical disk.
            if (StorageManager != null)
            {
                StorageManager.Dispose();
                Debug.Log("[OnApplicationQuit] Storage Manager disposed and flushed.");
            }

            Debug.Log("[OnApplicationQuit] World saved successfully.");
        }

        _shutdownTokenSource?.Dispose();
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
            job.Handle.Complete(); // TODO: Possibly impure struct method called on readonly variable: struct value always copied before invocation
            job.Dispose();
        }

        JobManager.generationJobs.Clear();

        // -- Mesh generation jobs --
        foreach ((JobHandle handle, MeshDataJobOutput meshData) job in JobManager.meshJobs.Values) // TODO: Possibly impure struct method called on readonly variable: struct value always copied before invocation
        {
            job.handle.Complete(); // TODO: Possibly impure struct method called on readonly variable: struct value always copied before invocation
            job.meshData.Dispose(); // TODO: Possibly impure struct method called on readonly variable: struct value always copied before invocation
        }

        JobManager.meshJobs.Clear();

        // -- Lighting jobs --
        foreach (LightingJobData jobData in JobManager.lightingJobs.Values)
        {
            jobData.Handle.Complete(); // TODO: Possibly impure struct method called on readonly variable: struct value always copied before invocation
            jobData.Dispose();
        }

        JobManager.lightingJobs.Clear();

        // 2. Dispose of the persistent global data.
        JobDataManager.Dispose();

        // 3. Dispose of fluid vertex templates.
        FluidVertexTemplates.Dispose();

        // --- Save system ---
        // Ensure storage is flushed even if OnApplicationQuit didn't run
        if (StorageManager != null)
        {
            StorageManager.Dispose();
            Debug.Log("[World] Storage Manager disposed in OnDestroy.");
        }

        // Ensure orphaned lighting sets are returned to the pool
        LightingStateManager?.Clear();

        // Cleanup mesh generation lists
        _chunksToBuildMesh.Clear();
        _chunksToBuildMeshSet.Clear();

        // Cleanup world data
        if (worldData != null)
        {
            foreach (ChunkData data in worldData.Chunks.Values)
            {
                // POOLING: Return data to pool
                ChunkPool.ReturnChunkData(data);
            }

            worldData.Chunks.Clear();
            worldData.ModifiedChunks.Clear();
        }

        // Cleanup chunk pool
        ChunkPool?.Clear();

        // Clean active map
        foreach (Chunk chunk in _chunkMap.Values) chunk.Destroy();
        _chunkMap.Clear();

        // Clean active borders map
        foreach (GameObject border in _chunkBorders.Values)
        {
            if (border != null) Destroy(border);
        }

        _chunkBorders.Clear();
    }

    private void Start()
    {
        // Initialize Pool Settings
        ChunkPool.SetTargetViewDistance(settings.viewDistance);

        // Initialize World
        StartCoroutine(StartWorld());
    }

    private IEnumerator StartWorld()
    {
        Debug.Log("--- Initializing World ---");

        // --- Fetch needed components ---
        _playerTransform = player.GetComponent<Transform>();
        _playerCamera = Camera.main!;

        // --- Load Settings via Manager ---
        settings = SettingsManager.LoadSettings();

        // --- Initialize World settings (from save data / create new world) ---
        // 1. Determine Mode
        IsVolatileMode = Application.isEditor && settings.enableVolatileSaveData;
        // Debug Log for Mode
        if (!settings.EnablePersistence)
        {
            Debug.LogWarning("<b>[Memory Only Mode]</b> Persistence is DISABLED. Chunks will NOT unload or save.");
        }
        else if (IsVolatileMode)
        {
            Debug.LogWarning("<b>[Volatile Mode]</b> Saves are temporary!");
        }

        // 2. Initialize Managers
        string worldName = WorldLaunchState.WorldName;
        int seed = WorldLaunchState.Seed;
        bool isNewGame = WorldLaunchState.IsNewGame;

        // Set static seed IMMEDIATELY
        // This MUST happen before we create WorldData or schedule any jobs.
        VoxelData.Seed = seed;

        Debug.Log($"Launching World: {worldName} (Seed: {seed}, New: {isNewGame})");

        worldData = new WorldData(worldName, seed);

        StorageManager = new ChunkStorageManager(worldName, IsVolatileMode, SaveSystem.CURRENT_VERSION);
        ModManager = new ModificationManager(worldName, IsVolatileMode);
        LightingStateManager = new LightingStateManager(worldName, IsVolatileMode);

        // 3. Load Global Metadata (Level.dat & Pending Mods)
        // Only load if it's NOT a new game AND Persistence is actually enabled.
        if (!isNewGame && settings.EnablePersistence)
        {
            // Load Pending Mods
            ModManager.Load();
            LightingStateManager.Load();

            // Load Level.dat (Player pos, Inventory, Time)
            WorldSaveData metadata = SaveSystem.LoadWorldMetadata(worldName, IsVolatileMode);

            if (metadata != null)
            {
                SaveSystem.LoadWorldGameState(this, metadata);
                VoxelData.Seed = metadata.seed; // Re-affirm seed from save just in case
                worldData.seed = metadata.seed;
            }
        }

        // Initialize world seed
        Random.InitState(VoxelData.Seed);

        // Initialize global shader properties
        Shader.SetGlobalFloat(s_shaderMinGlobalLightLevel, VoxelData.MinLightLevel);
        Shader.SetGlobalFloat(s_shaderMaxGlobalLightLevel, VoxelData.MaxLightLevel);
        SetGlobalLightValue();

        // --- Initialize Chunk Border Visualization ---
        GameObject borderParentGo = new GameObject("Chunk Borders");
        borderParentGo.transform.SetParent(transform);
        _chunkBorderParent = borderParentGo.transform;
        _lastChunkBordersState = settings.showChunkBorders;

        // --- STEP 1: DETERMINE INITIAL PLAYER POSITION ---
        // If we loaded a save, the player position is already set by LoadWorldGameState.
        // If not, we use the default spawn logic.
        bool wasSaveLoaded = !isNewGame && settings.EnablePersistence;
        Vector3 savedPlayerPosition = new Vector3();
        if (!wasSaveLoaded)
        {
            // Set initial spawnPosition to the center of the world for X & Z, and top of the world for Y.
            spawnPosition = new Vector3Int(VoxelData.WorldCentre, VoxelData.ChunkHeight - 1, VoxelData.WorldCentre);
            _playerTransform.position = spawnPosition;
        }
        else
        {
            // If we loaded a save, update our local 'spawnPosition' to match where the player actually is.
            spawnPosition = _playerTransform.position;
            savedPlayerPosition = _playerTransform.position;
        }

        PlayerChunkCoord = GetChunkCoordFromVector3(_playerTransform.position);

        // --- STEP 2: LOAD INITIAL CHUNKS (Async -> Sync Wait) ---
        Debug.Log("--- Loading/Generating initial chunks ---");

        // Create stopwatch to measure time taken for initial data generation.
        Stopwatch stopwatch = Stopwatch.StartNew();

        // 1. First, just schedule generation for everything in the load radius.
        //    This ensures the initial "blocking" load is fast, even with high view distance settings.
        int initialLoadRadius = Mathf.Min(settings.loadDistance, settings.maxInitialLoadRadius);

        // Generate an extra ring of chunks (+1 radius).
        // This ensures that the chunks inside 'initialLoadRadius' have valid neighbors to calculate lighting against.
        int generationRadius = initialLoadRadius + 1;

        // Capture the specific list of chunks we are loading.
        List<ChunkCoord> allChunksToGenerate = LoadChunksInDataPass(generationRadius);
        int loadedChunks = allChunksToGenerate.Count;

        // We only wait for the completion of the requested radius, not the buffer ring.
        List<ChunkCoord> chunksToWaitFor = new List<ChunkCoord>();
        foreach (ChunkCoord chunkCoord in allChunksToGenerate)
        {
            if (Mathf.Max(Mathf.Abs(chunkCoord.X - PlayerChunkCoord.X), Mathf.Abs(chunkCoord.Z - PlayerChunkCoord.Z)) <=
                initialLoadRadius)
            {
                chunksToWaitFor.Add(chunkCoord);
            }
        }

        // Trigger loading for all chunks (including buffer)
        List<Awaitable> loadTasks = new List<Awaitable>();
        foreach (ChunkCoord chunkCoord in allChunksToGenerate)
        {
            // Create placeholder if missing
            worldData.EnsureChunkExists(chunkCoord.ToWorldPosition());

            // Start the Load/Gen process
            loadTasks.Add(LoadOrGenerateChunk(chunkCoord));
        }

        // Wait for all to finish (Data Ready)
        foreach (Awaitable task in loadTasks) yield return task;

        // 2. Force complete ONLY the data-related jobs (generation and lighting).
        //    Now, instead of a blocking call, we yield to (wait for) another coroutine.
        //    The code will PAUSE here and will not continue until ForceCompleteDataJobsCoroutine is finished.
        yield return StartCoroutine(ForceCompleteDataJobsCoroutine(chunksToWaitFor));

        stopwatch.Stop();
        long totalMilliseconds = stopwatch.ElapsedMilliseconds;
        float avgTime = (float)totalMilliseconds / Mathf.Max(1, loadedChunks);
        Debug.Log($"Initial data load / generation took {totalMilliseconds} ms for {loadedChunks} chunks (Initial Load Radius: {initialLoadRadius})");
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
        if (!wasSaveLoaded)
        {
            spawnPosition = GetHighestVoxel(spawnPosition.ToVector3Int()) + spawnPositionOffset;
            _playerTransform.position = spawnPosition;
        }
        else
        {
            Debug.Log($"Re-using last player location from loaded save. {savedPlayerPosition}");
            _playerTransform.position = savedPlayerPosition;
        }

        Debug.Log("Initializing clouds...");
        clouds?.Initialize();

        Debug.Log("Staring world tick...");
        StartCoroutine(Tick());

        Debug.Log("World initialization complete.");
        Debug.Log("--- Startup complete ---");

        // Enable world loading logic
        _isWorldLoaded = true;
    }

    /// <summary>
    /// Schedules generation jobs for all chunks within a given radius around the player's starting position.
    /// </summary>
    /// <param name="loadRadius">The radius of chunks to load.</param>
    /// <returns>The list of chunk coordinates for which generation was scheduled.</returns>
    private List<ChunkCoord> LoadChunksInDataPass(int loadRadius)
    {
        List<ChunkCoord> loadedChunks = new List<ChunkCoord>();

        // We don't need the spiral loop here, a simple square loop is fine for startup.
        for (int x = -loadRadius; x <= loadRadius; x++)
        {
            for (int z = -loadRadius; z <= loadRadius; z++)
            {
                ChunkCoord coord = new ChunkCoord(PlayerChunkCoord.X + x, PlayerChunkCoord.Z + z);
                // NOTE: We do NOT schedule generation here.
                // We just collect the list. LoadOrGenerateChunk (called later) decides whether to Load or Schedule.
                if (IsChunkInWorld(coord))
                {
                    loadedChunks.Add(coord);
                }
            }
        }

        return loadedChunks;
    }

    /// <summary>
    /// The core async pipeline:
    /// 1. Check Memory (Done by caller usually)
    /// 2. Check Disk (Async)
    /// 3. If missing, Schedule Gen (Job)
    /// </summary>
    private async Awaitable LoadOrGenerateChunk(ChunkCoord chunkCoord)
    {
        Vector2Int chunkVoxelPos = chunkCoord.ToVoxelOrigin();

        // We assume placeholder exists in worldData.Chunks[chunkVoxelPos]
        ChunkData data = worldData.Chunks[chunkVoxelPos];

        if (data.IsPopulated) return; // Already done

        // 1. Try Load from Disk if allowed
        if (settings.EnablePersistence)
        {
            ChunkData loaded = await StorageManager.LoadChunkAsync(chunkVoxelPos);

            // Ensure the chunk wasn't unloaded or recycled during the "await" above.
            if (!worldData.Chunks.TryGetValue(chunkVoxelPos, out ChunkData currentData) || currentData != data)
            {
                // The chunk was unloaded. Recycle the loaded data to prevent a memory leak.
                if (loaded != null)
                {
                    ChunkPool.ReturnChunkData(loaded);
                }

                return;
            }


            if (loaded != null)
            {
                Debug.Log($"[LoadOrGenerateChunk] Chunk {chunkCoord} loaded successfully, calling PopulateFromSave");

                // Hydrate the placeholder
                data.PopulateFromSave(loaded);
                ChunkPool.ReturnChunkData(
                    loaded); // Recycle the outer shell of the loaded data now that we've extracted its contents.
                data.Chunk?.OnDataPopulated();

                // Apply Pending Mods (Trees, etc that spilled over)
                if (ModManager.TryGetModsForChunk(chunkCoord, out List<VoxelMod> pendingMods))
                {
                    Debug.Log($"[LoadOrGenerateChunk] Applying {pendingMods.Count} pending mods to chunk {chunkCoord}");
                    foreach (VoxelMod mod in pendingMods)
                    {
                        // Apply directly to data (fast)
                        Vector3Int localVoxelPos = worldData.GetLocalVoxelPositionInChunk(mod.GlobalPosition);
                        // We use a simplified set here or the standard ModifyVoxel
                        // ModifyVoxel handles lighting queues automatically
                        data.ModifyVoxel(localVoxelPos, mod);
                    }
                }

                // Restore lighting queues
                if (LightingStateManager.TryGetAndRemove(chunkCoord, out HashSet<Vector2Int> localCols))
                {
                    Debug.Log($"[LoadOrGenerateChunk] Restoring {localCols.Count} lighting columns for chunk {chunkCoord}");

                    HashSet<Vector2Int> globalCols = new HashSet<Vector2Int>();
                    foreach (Vector2Int lCol in localCols)
                    {
                        globalCols.Add(new Vector2Int(lCol.x + chunkVoxelPos.x, lCol.y + chunkVoxelPos.y));
                    }

                    if (worldData.SunlightRecalculationQueue.ContainsKey(chunkVoxelPos))
                        worldData.SunlightRecalculationQueue[chunkVoxelPos].UnionWith(globalCols);
                    else
                        worldData.SunlightRecalculationQueue[chunkVoxelPos] = globalCols;

                    data.HasLightChangesToProcess = true;
                }

                // Check for initial lighting needs
                if (data.NeedsInitialLighting)
                {
                    Debug.Log($"[LoadOrGenerateChunk] Chunk {chunkCoord} needs initial lighting. Checking neighbors...");

                    if (AreNeighborsDataReady(chunkCoord))
                    {
                        Debug.Log($"[LoadOrGenerateChunk] Neighbors ready - triggering lighting for {chunkCoord}");

                        // 1. Fill the queue (RecalculateSunLightLight populates the queues in data)
                        data.RecalculateSunLightLight();

                        // 2. Schedule the job immediately using Data overload
                        JobManager.ScheduleLightingUpdate(data);

                        // 3. Clear flag so we don't do this again
                        data.NeedsInitialLighting = false;
                    }
                    else
                    {
                        Debug.Log($"[LoadOrGenerateChunk] Neighbors not ready - deferring lighting for {chunkCoord}");
                    }
                }
                else
                {
                    // If the chunk is loaded and doesn't need lighting updates (it's stable),
                    // we must explicitly request the mesh rebuild here.
                    // CheckViewDistance skipped it because IsPopulated was false at that time.
                    if (data.Chunk != null && data.Chunk.isActive)
                    {
                        RequestChunkMeshRebuild(data.Chunk);
                    }
                }

                return;
            }

            Debug.Log($"[LoadOrGenerateChunk] Chunk {chunkCoord} not on disk, scheduling generation");
        }

        // 2. Not on disk (or Persistence disabled) -> Generate
        JobManager.ScheduleGeneration(chunkCoord);
    }

    /// <summary>
    /// A startup coroutine that forces the completion of all initial generation and lighting jobs.
    /// It runs in a tight loop, processing job results and scheduling dependent jobs until the entire
    /// initial world area is fully generated and lit. It includes a dynamic safety break to prevent infinite loops.
    /// </summary>
    /// <param name="initialChunks">The list of chunks being loaded to monitor.</param>
    private IEnumerator ForceCompleteDataJobsCoroutine(List<ChunkCoord> initialChunks)
    {
        // --- Profiling Setup ---
        Stopwatch totalStopwatch = Stopwatch.StartNew();
        Stopwatch generationProcessingWatch = new Stopwatch();
        Stopwatch lightingSchedulingWatch = new Stopwatch();
        Stopwatch lightingCompletionWatch = new Stopwatch();

        int safetyBreak = 0;

        // --- Dynamically calculate maxIterations ---
        // The maximum number of iterations should be proportional to the number of chunks being processed and account
        // for the multiple states each chunk passes through (generation, lighting passes, neighbor interactions).
        // - Base iterations: 15x chunk count
        // - Additional iterations for large loads: +5 per chunk over 100
        int totalChunksToProcess = initialChunks.Count;
        int baseIterations = totalChunksToProcess * 15;
        int additionalIterations = Mathf.Max(0, (totalChunksToProcess - 100) * 5);
        int maxIterations = baseIterations + additionalIterations;

        // SAFETY: Minimum 500 iterations even for tiny loads
        maxIterations = Mathf.Max(maxIterations, 500);

        // --- PHASE 1: Complete all terrain generation first ---
        // This is fast and linear, but we profile it for completeness.
        generationProcessingWatch.Start();
        while (JobManager.generationJobs.Count > 0)
        {
            // Complete any finished generation jobs. This may set `NeedsInitialLighting = true` on chunks.
            JobManager.ProcessGenerationJobs();
            ApplyModifications();

            safetyBreak++;
            if (safetyBreak > maxIterations)
            {
                Debug.LogError("ForceCompleteDataJobsCoroutine timed out during Generation Phase. Forcing exit.");
                yield break; // Exit the coroutine
            }

            yield return null; // Wait a frame
        }

        generationProcessingWatch.Stop();

        // --- PHASE 2: Complete all lighting calculations ---
        // Optimization: Convert Coord list to Data list once
        List<ChunkData> chunksInLoadArea = new List<ChunkData>();
        foreach (ChunkCoord chunkCoord in initialChunks)
        {
            // We can use the dictionary directly as we know they were requested
            Vector2Int chunkVoxelPos = chunkCoord.ToVoxelOrigin();
            if (worldData.Chunks.TryGetValue(chunkVoxelPos, out ChunkData cd)) chunksInLoadArea.Add(cd);
        }

        int lightingLoopIterations = 0;

        // This logic is a synchronous version of what the Update() loop does asynchronously.
        while (HasPendingInitialLighting(chunksInLoadArea) || HasPendingLightChangesOnMainThread(chunksInLoadArea) ||
               JobManager.lightingJobs.Count > 0)
        {
            lightingLoopIterations++;

            // --- Step 2a: Trigger Initial Lighting Requests ---
            lightingSchedulingWatch.Start();
            // Iterate through a copy to prevent modification-during-iteration issues.
            foreach (ChunkData chunkData in chunksInLoadArea)
            {
                if (chunkData.IsPopulated && chunkData.NeedsInitialLighting)
                {
                    // We must still ensure neighbors have their terrain data ready before lighting.
                    if (AreNeighborsDataReady(ChunkCoord.FromVoxelOrigin(chunkData.position)))
                    {
                        // This chunk is ready. Trigger its full sunlight recalculation, which sets `HasLightChangesToProcess = true` and populates the light queues.
                        chunkData.RecalculateSunLightLight();

                        // The request for an *initial* light pass has now been fulfilled.
                        chunkData.NeedsInitialLighting = false;
                    }
                }
            }

            // --- Step 2b: Schedule Lighting Jobs ---
            // Now that the initial light requests have been processed, the regular lighting scheduler can pick them up in the same coroutine iteration.
            foreach (ChunkData chunkData in chunksInLoadArea)
            {
                if (chunkData.IsPopulated && chunkData.HasLightChangesToProcess)
                {
                    ChunkCoord chunkCoord = ChunkCoord.FromVoxelOrigin(chunkData.position);
                    if (!JobManager.lightingJobs.ContainsKey(chunkCoord) && AreNeighborsDataReady(chunkCoord))
                    {
                        // OPTIMIZATION: Use TempJob allocator.
                        // This is safe because we call CompleteAndProcessLightingJobs() immediately below, ensuring these allocations live for less than 1 frame.
                        JobManager.ScheduleLightingUpdate(chunkData, Allocator.TempJob);
                    }
                }
            }

            lightingSchedulingWatch.Stop();

            // --- Step 2c: Force-complete and process all scheduled jobs ---
            lightingCompletionWatch.Start();
            CompleteAndProcessLightingJobs();
            lightingCompletionWatch.Stop();

            // --- Safety Break ---
            safetyBreak++;
            if (safetyBreak > maxIterations)
            {
                Debug.LogError($"ForceCompleteDataJobsCoroutine exceeded max iterations ({maxIterations}) during Lighting Phase. Forcing exit.");
                Debug.LogError($"Remaining jobs: Lighting({JobManager.lightingJobs.Count}). Pending chunks: InitialLight({chunksInLoadArea.Count(c => c.NeedsInitialLighting)}), LightChanges({chunksInLoadArea.Count(c => c.HasLightChangesToProcess)})");
                yield break; // Exit the coroutine
            }

            yield return null; // Wait a frame
        }

        totalStopwatch.Stop();
        Debug.Log("All generation and lighting jobs are complete!");

        // --- Generate and Print Profiling Report ---
        StringBuilder report = new StringBuilder();
        report.AppendLine(
            $"<color=yellow><b>--- Startup Coroutine Profile Report (Load Radius: {initialChunks.Count}) ---</b></color>");
        report.AppendLine($"<b>Total Time: {totalStopwatch.ElapsedMilliseconds} ms</b>");
        report.AppendLine(
            $"Total Main Loop Iterations: {safetyBreak} (Lighting Phase took {lightingLoopIterations} iterations)");
        report.AppendLine();
        report.AppendLine("<b>--- Phase Timings ---</b>");

        long genTime = generationProcessingWatch.ElapsedMilliseconds;
        long lightScheduleTime = lightingSchedulingWatch.ElapsedMilliseconds;
        long lightCompleteTime = lightingCompletionWatch.ElapsedMilliseconds;
        long totalPhaseTime = genTime + lightScheduleTime + lightCompleteTime;

        report.AppendLine($"  - Generation Processing: {genTime,5} ms ({genTime * 100f / totalPhaseTime:F1}%)");
        report.AppendLine(
            $"  - Lighting Scheduling:   {lightScheduleTime,5} ms ({lightScheduleTime * 100f / totalPhaseTime:F1}%)");
        report.AppendLine(
            $"  - Lighting Completion:   {lightCompleteTime,5} ms ({lightCompleteTime * 100f / totalPhaseTime:F1}%)");
        report.AppendLine();
        report.AppendLine("<b>--- Averages Per Lighting Iteration ---</b>");
        if (lightingLoopIterations > 0)
        {
            report.AppendLine(
                $"  - Avg Scheduling Time / Iteration: {lightScheduleTime / (float)lightingLoopIterations:F2} ms");
            report.AppendLine(
                $"  - Avg Completion Time / Iteration: {lightCompleteTime / (float)lightingLoopIterations:F2} ms");
        }

        Debug.Log(report.ToString());
    }

    /// <summary>
    /// Checks a specific list of chunks for any that are waiting for their initial lighting pass.
    /// </summary>
    /// <param name="chunkList">The list of chunks to check.</param>
    /// <returns>True if any chunk in the list has the `NeedsInitialLighting` flag set; otherwise, false.</returns>
    private static bool HasPendingInitialLighting(List<ChunkData> chunkList)
    {
        return chunkList.Any(chunkData => chunkData != null && chunkData.NeedsInitialLighting);
    }

    /// <summary>
    /// Checks a specific list of chunks for any that have pending lighting updates.
    /// </summary>
    /// <param name="chunkList">The list of chunks to check.</param>
    /// <returns>True if any chunk in the list has the `HasLightChangesToProcess` flag set; otherwise, false.</returns>
    private static bool HasPendingLightChangesOnMainThread(List<ChunkData> chunkList)
    {
        return chunkList.Any(chunkData => chunkData != null && chunkData.HasLightChangesToProcess);
    }

    private void CompleteAndProcessLightingJobs()
    {
        // Force complete all scheduled lighting jobs immediately.
        foreach (LightingJobData job in JobManager.lightingJobs.Values)
        {
            job.Handle.Complete(); // TODO: Possibly impure struct method called on readonly variable: struct value always copied before invocation
        }

        // Process their results.
        JobManager.ProcessLightingJobs();
    }

    private void CompleteAndProcessMeshJobs()
    {
        // Force complete all scheduled mesh jobs immediately.
        foreach ((JobHandle handle, MeshDataJobOutput meshData) job in JobManager.meshJobs.Values)
        {
            job.handle.Complete(); // TODO: Possibly impure struct method called on readonly variable: struct value always copied before invocation
        }

        // Process their results.
        JobManager.ProcessMeshJobs();
    }

    public void SetGlobalLightValue()
    {
        Shader.SetGlobalFloat(s_shaderGlobalLightLevel, globalLightLevel);
        _playerCamera.backgroundColor = Color.Lerp(night, day, globalLightLevel);
    }

    private IEnumerator Tick()
    {
        while (true)
        {
            // FIX: Snapshot _activeChunks to prevent InvalidOperationException if
            // CheckViewDistance modifies the set between coroutine yields.
            using HashSet<ChunkCoord>.Enumerator enumerator = _activeChunks.GetEnumerator();
            List<ChunkCoord> snapshot = ListPool<ChunkCoord>.Get();
            try
            {
                while (enumerator.MoveNext())
                    snapshot.Add(enumerator.Current);

                foreach (ChunkCoord chunkCoord in snapshot)
                {
                    if (_chunkMap.TryGetValue(chunkCoord, out Chunk chunk))
                    {
                        chunk.TickUpdate();
                    }
                }
            }
            finally
            {
                ListPool<ChunkCoord>.Release(snapshot);
            }

            yield return new WaitForSeconds(VoxelData.TickLength);
        }
        // ReSharper disable once IteratorNeverReturns
    }

    private void Update()
    {
        // Prevent normal generation logic from interfering with the startup coroutine
        if (!_isWorldLoaded) return;

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
            foreach (GameObject borderObject in _chunkBorders.Values)
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

        // 4. Scan all loaded chunks for lighting work and schedule jobs (New "Pull" system)
        if (settings.enableLighting)
        {
            int lightJobsScheduled = 0;

            foreach (ChunkData chunkData in worldData.Chunks.Values)
            {
                if (lightJobsScheduled >= settings.maxLightJobsPerFrame) break; // Respect the throttle

                // Skip placeholder data that hasn't generated terrain yet
                if (!chunkData.IsPopulated) continue;

                // Create coord from position
                ChunkCoord chunkCoord = ChunkCoord.FromVoxelOrigin(chunkData.position);

                // If no job is currently running...
                if (!JobManager.lightingJobs.ContainsKey(chunkCoord))
                {
                    // --- Prioritize initial lighting ---
                    if (chunkData.NeedsInitialLighting)
                    {
                        // Before scheduling, we must still ensure neighbors have their data ready.
                        if (AreNeighborsDataReady(chunkCoord))
                        {
                            // This is the first lighting pass, so we trigger the full recalculation.
                            chunkData.RecalculateSunLightLight();
                            JobManager.ScheduleLightingUpdate(chunkData);

                            // The request has been fulfilled, so clear the flag.
                            chunkData.NeedsInitialLighting = false;
                            lightJobsScheduled++;
                        }
                    }
                    // --- Regular lighting updates ---
                    // If no initial lighting is needed, check for regular updates.
                    else if (chunkData.HasLightChangesToProcess)
                    {
                        JobManager.ScheduleLightingUpdate(chunkData);
                        lightJobsScheduled++;
                    }
                }
            }
        }

        // 5. Process completed mesh jobs from the PREVIOUS frame.
        JobManager.ProcessMeshJobs();

        // 6. Schedule NEW mesh jobs for chunks that now need them.
        //    NOTE: If we have too many jobs already running (e.g. > 20),
        //          pause scheduling new ones to let the Job System catch up.
        if (_chunksToBuildMesh.Count > 0 && JobManager.meshJobs.Count < 20)
        {
            int meshJobsScheduled = 0;
            // Iterate forwards to respect priority (Index 0 is highest priority).
            // We manipulate 'i' when removing items.
            for (int i = 0; i < _chunksToBuildMesh.Count; i++)
            {
                if (meshJobsScheduled >= settings.maxMeshRebuildsPerFrame) break;

                Chunk chunk = _chunksToBuildMesh[i];

                // Validate chunk state before attempting to mesh.
                if (chunk == null || !chunk.isActive)
                {
                    // Remove from both list and HashSet to keep them in sync
                    if (chunk != null)
                        _chunksToBuildMeshSet.Remove(chunk.Coord);

                    _chunksToBuildMesh.RemoveAt(i);
                    i--; // Adjust index since we removed an element
                    continue;
                }

                // JobManager.ScheduleMeshing will return false if deps (neighbors/lighting) aren't ready.
                // In that case, we leave the chunk in both the list and HashSet to try again next frame.
                if (JobManager.ScheduleMeshing(chunk))
                {
                    // Successfully scheduled - remove from both tracking structures
                    _chunksToBuildMeshSet.Remove(chunk.Coord);
                    _chunksToBuildMesh.RemoveAt(i);
                    i--; // Decrement index
                    meshJobsScheduled++;
                }
            }
        }

        // The chunksToDraw queue is populated by ApplyMeshData in Chunk.cs
        if (ChunksToDraw.Count > 0)
        {
            ChunksToDraw.Dequeue().CreateMesh();
        }

        // Run Pool Cleanup
        ChunkPool.Update();

        // Check if settings changed to update pool target
        // (Optional: You can move this to a dedicated ApplySettings method)
        if (settings.viewDistance != ChunkPool.targetViewDistance)
        {
            ChunkPool.SetTargetViewDistance(settings.viewDistance);
        }
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
        NativeArray<BiomeAttributesJobData> biomesJobData = new NativeArray<BiomeAttributesJobData>(biomes.Length, Allocator.Persistent);
        NativeArray<LodeJobData> allLodesJobData = new NativeArray<LodeJobData>(totalLodeCount, Allocator.Persistent);

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
        foreach (BlockType blockType in blockDatabase.blockTypes)
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
        NativeArray<CustomMeshData> customMeshesJobData = new NativeArray<CustomMeshData>(customMeshesList.ToArray(), Allocator.Persistent);
        NativeArray<CustomFaceData> customFacesJobData = new NativeArray<CustomFaceData>(customFacesList.ToArray(), Allocator.Persistent);
        NativeArray<CustomVertData> customVertsJobData = new NativeArray<CustomVertData>(customVertsList.ToArray(), Allocator.Persistent);
        NativeArray<int> customTrisJobData = new NativeArray<int>(customTrisList.ToArray(), Allocator.Persistent);

        // --- Step 4: Populate blockTypesJobData, including the custom mesh index
        NativeArray<BlockTypeJobData> blockTypesJobData =
            new NativeArray<BlockTypeJobData>(blockDatabase.blockTypes.Length, Allocator.Persistent);
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

    /// <summary>
    /// Enqueues a voxel modification to be processed.
    /// </summary>
    /// <param name="mod">The voxel modification to process.</param>
    public void AddModification(VoxelMod mod)
    {
        _modifications.Enqueue(mod);
    }

    /// <summary>
    /// The central handler for voxel modifications. This method queues mesh rebuilds and debug visualization updates
    /// for the source chunk and all affected cardinal and diagonal neighbors.
    /// </summary>
    /// <param name="chunkVoxelPos">The world-space coordinate of the chunk where the modification occurred.</param>
    /// <param name="localVoxelPos">The position of the modified voxel within its own chunk, used for border detection.</param>
    /// <param name="immediate">If true, the mesh rebuild requests are prioritized to be processed as soon as possible.</param>
    public void NotifyChunkModified(Vector2Int chunkVoxelPos, Vector3Int localVoxelPos, bool immediate)
    {
        // --- 1. Queue Mesh Rebuilds ---

        // The chunk that was directly modified always needs a rebuild.
        ChunkCoord coord = ChunkCoord.FromVoxelOrigin(chunkVoxelPos);
        Chunk chunk = _chunkMap.GetValueOrDefault(coord);
        if (chunk != null)
        {
            RequestChunkMeshRebuild(chunk, immediate);
        }

        // Determine which borders the modification is on for efficient neighbor updates.
        bool onWestBorder = localVoxelPos.x == 0;
        bool onEastBorder = localVoxelPos.x == VoxelData.ChunkWidth - 1;
        bool onSouthBorder = localVoxelPos.z == 0;
        bool onNorthBorder = localVoxelPos.z == VoxelData.ChunkWidth - 1;

        // Queue rebuilds for cardinal neighbors.
        if (onWestBorder) QueueNeighborRebuild(coord.Neighbor(-1, 0), immediate);
        if (onEastBorder) QueueNeighborRebuild(coord.Neighbor(1, 0), immediate);
        if (onSouthBorder) QueueNeighborRebuild(coord.Neighbor(0, -1), immediate);
        if (onNorthBorder) QueueNeighborRebuild(coord.Neighbor(0, 1), immediate);

        // Queue rebuilds for diagonal neighbors if the modification was on a corner.
        // This is critical for seamless fluid mesh smoothing.
        if (onWestBorder && onSouthBorder) QueueNeighborRebuild(coord.Neighbor(-1, -1), immediate);
        if (onEastBorder && onSouthBorder) QueueNeighborRebuild(coord.Neighbor(1, -1), immediate);
        if (onWestBorder && onNorthBorder) QueueNeighborRebuild(coord.Neighbor(-1, 1), immediate);
        if (onEastBorder && onNorthBorder) QueueNeighborRebuild(coord.Neighbor(1, 1), immediate);

        // --- 2. Queue Debug Visualization Updates ---

        // Always update the visualization for the directly modified chunk.
        AddChunksToUpdateVisualization(coord);

        // Queue updates for cardinal neighbors. As analyzed, diagonal updates are not needed for visualization.
        if (onWestBorder) AddChunksToUpdateVisualization(coord.Neighbor(-1, 0));
        if (onEastBorder) AddChunksToUpdateVisualization(coord.Neighbor(1, 0));
        if (onSouthBorder) AddChunksToUpdateVisualization(coord.Neighbor(0, -1));
        if (onNorthBorder) AddChunksToUpdateVisualization(coord.Neighbor(0, 1));
    }

    private void QueueNeighborRebuild(ChunkCoord neighborCoord, bool immediate = false)
    {
        Vector2Int neighborVoxelPos = neighborCoord.ToVoxelOrigin();
        if (worldData.Chunks.TryGetValue(neighborVoxelPos, out ChunkData neighborData) && neighborData.Chunk != null)
        {
            RequestChunkMeshRebuild(neighborData.Chunk, immediate);
        }
    }

    /// <summary>
    /// Explicitly queues mesh rebuild jobs for the four cardinal neighbors of a specified chunk.
    /// Typically called when a central chunk stabilizes its lighting, changing the boundary appearance of its neighbors.
    /// </summary>
    /// <param name="chunkCoord">The chunk coordinate of the central chunk.</param>
    public void RequestNeighborMeshRebuilds(ChunkCoord chunkCoord)
    {
        // Queue rebuilds for all 4 direct neighbors
        QueueNeighborRebuild(chunkCoord.Neighbor(0, 1)); // North
        QueueNeighborRebuild(chunkCoord.Neighbor(0, -1)); // South
        QueueNeighborRebuild(chunkCoord.Neighbor(1, 0)); // East
        QueueNeighborRebuild(chunkCoord.Neighbor(-1, 0)); // West
    }

    private bool AreNeighborsReady(ChunkCoord chunkCoord)
    {
        // Check all 4 horizontal neighbors
        foreach (int faceIndex in VoxelData.HorizontalFaceChecksIndices)
        {
            Vector3Int offset = VoxelData.FaceChecks[faceIndex];
            ChunkCoord neighborCoord = chunkCoord.Neighbor(offset.x, offset.z);

            if (IsChunkInWorld(neighborCoord))
            {
                // Is a generation job for this neighbor still running?
                if (JobManager.generationJobs.ContainsKey(neighborCoord))
                {
                    return false; // Neighbor data is not ready yet.
                }

                // Does the placeholder ChunkData exist in the world dictionary?
                Vector2Int chunkVoxelPos = neighborCoord.ToVoxelOrigin();
                if (!worldData.Chunks.ContainsKey(chunkVoxelPos))
                {
                    // This case is unlikely with the new CheckViewDistance, but is a good safeguard.
                    return false;
                }
            }
        }

        // If we get here, all neighbors are ready.
        return true;
    }

    /// <summary>
    /// Checks if all of a chunk's cardinal neighbors have finished generating their data and have a stable lighting state.
    /// A neighbor is considered "ready" if no generation or lighting job is running for it, and it has no pending
    /// lighting updates on the main thread. This is a prerequisite for scheduling a mesh generation job.
    /// </summary>
    /// <param name="chunkCoord">The coordinate of the central chunk whose neighbors are to be checked.</param>
    /// <returns>True if all neighbors are fully generated and lit; otherwise, false.</returns>
    public bool AreNeighborsReadyAndLit(ChunkCoord chunkCoord)
    {
        // Check all 4 horizontal neighbors
        foreach (int faceIndex in VoxelData.HorizontalFaceChecksIndices)
        {
            Vector3Int offset = VoxelData.FaceChecks[faceIndex];
            ChunkCoord neighborCoord = chunkCoord.Neighbor(offset.x, offset.z);

            if (!IsChunkInWorld(neighborCoord)) continue;

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

            Vector2Int neighborV2Pos = neighborCoord.ToVoxelOrigin();

            // Only enforce lighting stability checks if the chunk is actually populated with data.
            // If it's an empty placeholder, it has no light to process anyway.
            if (worldData.Chunks.TryGetValue(neighborV2Pos, out ChunkData neighborData) && neighborData.IsPopulated)
            {
                // Does the neighbor have pending light changes that haven't even been scheduled yet,
                // OR is waiting for first light is NOT ready to provide lighting data for meshing.
                if (neighborData.HasLightChangesToProcess || neighborData.NeedsInitialLighting)
                {
                    return
                        false; // Neighbor has pending light changes that haven't even been scheduled yet, we must wait.
                }

                // Is the neighbor waiting for its completed lighting job to be processed on the main thread?
                if (neighborData.IsAwaitingMainThreadProcess)
                {
                    return false; // Neighbor is in a transitional state, we must wait.
                }
            }
        }

        // Also check the 4 diagonal neighbors. Both mesh and lighting jobs copy data
        // from all 8 neighbors (including diagonals), so stale diagonal lighting data
        // can cause seam artifacts at chunk corners.
        foreach (Vector3Int diagOffset in VoxelData.DiagonalNeighborOffsets)
        {
            ChunkCoord neighborCoord = chunkCoord.Neighbor(diagOffset.x, diagOffset.z);

            if (!IsChunkInWorld(neighborCoord)) continue;

            if (JobManager.generationJobs.ContainsKey(neighborCoord)) return false;
            if (JobManager.lightingJobs.ContainsKey(neighborCoord)) return false;

            Vector2Int neighborV2Pos = neighborCoord.ToVoxelOrigin();
            if (worldData.Chunks.TryGetValue(neighborV2Pos, out ChunkData diagData) && diagData.IsPopulated)
            {
                if (diagData.HasLightChangesToProcess || diagData.NeedsInitialLighting) return false;
                if (diagData.IsAwaitingMainThreadProcess) return false;
            }
        }

        // If we get here, all neighbors are stable.
        return true;
    }


    /// <summary>
    /// Verifies that all four cardinal neighbors of a chunk exist and have completely finished initial terrain generation.
    /// Out-of-bounds chunks (beyond world limits) are treated as intrinsically "ready".
    /// </summary>
    /// <param name="coord">The coordinate of the central chunk.</param>
    /// <returns>True if all valid neighbors are fully populated with voxel data.</returns>
    public bool AreNeighborsDataReady(ChunkCoord coord)
    {
        // Check all 4 horizontal neighbors
        foreach (int faceIndex in VoxelData.HorizontalFaceChecksIndices)
        {
            Vector3Int offset = VoxelData.FaceChecks[faceIndex];
            ChunkCoord neighborCoord = coord.Neighbor(offset.x, offset.z);

            // Skip neighbors outside world bounds
            if (!IsChunkInWorld(neighborCoord))
            {
                // Neighbor is outside the world - treat as "ready" (it will never exist)
                continue;
            }

            // 1. // Is a generation job for this neighbor still running?
            if (JobManager.generationJobs.ContainsKey(neighborCoord))
            {
                return false; // Neighbor data is not ready yet.
            }
        }

        // All neighbors are present and generated.
        return true;
    }

    /// <summary>
    /// Safely writes an updated light value to a specific voxel's data without triggering the cascading
    /// flood-fill updates normally caused by player modifications.
    /// This is strictly used by background jobs to apply cross-chunk propagation results.
    /// </summary>
    /// <param name="globalPos">The absolute world position of the target voxel.</param>
    /// <param name="lightValue">The newly calculated light intensity (0-15).</param>
    /// <param name="channel">Which light channel to apply this to (Sunlight or Blocklight).</param>
    public void SetLight(Vector3 globalPos, byte lightValue, LightChannel channel)
    {
        ChunkData chunkData = worldData.RequestChunk(worldData.GetChunkCoordFor(globalPos), false);
        if (chunkData != null && chunkData.IsPopulated)
        {
            // Get data from the chunk
            Vector3Int localPos = worldData.GetLocalVoxelPositionInChunk(globalPos);
            uint oldPackedData = chunkData.GetVoxel(localPos.x, localPos.y, localPos.z);
            uint newPackedData;

            // Use the channel to call the correct setter
            if (channel == LightChannel.Sun)
            {
                if (BurstVoxelDataBitMapping.GetSunLight(oldPackedData) == lightValue) return; // No change needed
                newPackedData = BurstVoxelDataBitMapping.SetSunLight(oldPackedData, lightValue);
            }
            else // Blocklight
            {
                if (BurstVoxelDataBitMapping.GetBlockLight(oldPackedData) == lightValue) return; // No change needed
                newPackedData = BurstVoxelDataBitMapping.SetBlockLight(oldPackedData, lightValue);
            }

            // Write data back
            chunkData.SetVoxel(localPos.x, localPos.y, localPos.z, newPackedData);
        }
    }


    #region Voxel Modifications

    /// <summary>
    /// Enqueues a batch of voxel modifications to be processed.
    /// </summary>
    /// <param name="voxelMods">The queue of voxel modifications to process.</param>
    public void EnqueueVoxelModifications(IEnumerable<VoxelMod> voxelMods)
    {
        foreach (VoxelMod mod in voxelMods)
        {
            _modifications.Enqueue(mod);
        }
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

        // Process directly from the single flattened queue
        while (_modifications.Count > 0)
        {
            VoxelMod v = _modifications.Dequeue();

            // Calculate which chunk this mod belongs to
            ChunkCoord chunkCoord = GetChunkCoordFromVector3(v.GlobalPosition);
            Vector2Int chunkVoxelPos = chunkCoord.ToVoxelOrigin();

            // --- 1. Get Chunk Data ---
            // We check worldData directly to see if it is loaded/generating
            bool chunkIsReady = false;
            if (worldData.Chunks.TryGetValue(chunkVoxelPos, out ChunkData chunkData))
            {
                chunkIsReady = chunkData.IsPopulated;
            }

            // If the chunk is NOT ready to receive mods (not loaded or still generating)
            if (!chunkIsReady)
            {
                // Send to Persistent Manager
                ModManager.AddPendingMod(chunkCoord, v);
                continue;
            }

            // --- 2. Check Placement Rules ---
            // Special Case: If the mod is to place Air (ID 0), it's a "break" action.
            // We should always allow this, unless the target is unbreakable.
            if (v.ID == BlockIDs.Air)
            {
                VoxelState? stateToBreak = worldData.GetVoxelState(v.GlobalPosition);
                if (stateToBreak.HasValue &&
                    (blockDatabase.blockTypes[stateToBreak.Value.id].tags & BlockTags.UNBREAKABLE) != 0)
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
                            if (existingState.Value.id != BlockIDs.Air)
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
                        Vector3Int localPosInNeighbor =
                            neighborChunk.GetVoxelPositionInChunkFromGlobalVector3(neighborPos);
                        neighborChunk.AddActiveVoxel(localPosInNeighbor);
                    }
                }
            }
        }

        _applyingModifications = false;
    }

    #endregion

    /// <summary>
    /// Adds a chunk to the queue to have its mesh rebuilt.
    /// For priority, add it to the front of the list.
    /// </summary>
    /// <param name="chunk">The chunk to rebuild</param>
    /// <param name="immediate">If true, rebuild the chunk as soon as possible</param>
    public void RequestChunkMeshRebuild([CanBeNull] Chunk chunk, bool immediate = false)
    {
        // Validate chunk state and check for duplicates using O(1) HashSet.
        // 1. Don't queue null chunks.
        // 2. Don't queue inactive chunks (they are out of view or being destroyed).
        // 3. Don't queue chunks that are already in the queue (prevents duplicates).
        if (chunk == null || !chunk.isActive || _chunksToBuildMeshSet.Contains(chunk.Coord))
            return;

        // Add to tracking set (O(1) operation)
        _chunksToBuildMeshSet.Add(chunk.Coord);

        if (immediate)
            _chunksToBuildMesh.Insert(0, chunk); // Insert at the front
        else
            _chunksToBuildMesh.Add(chunk);
    }

    /// <summary>
    /// Returns the chunk coordinates for a given world position.
    /// </summary>
    /// <param name="worldPos">The world position</param>
    /// <returns>The chunk coordinates for the given world position</returns>
    private static ChunkCoord GetChunkCoordFromVector3(Vector3 worldPos)
    {
        return ChunkCoord.FromWorldPosition(worldPos);
    }

    /// <summary>
    /// Retrieves the active chunk object at the specified coordinate.
    /// </summary>
    /// <param name="chunkCoord">The chunk coordinate to look up.</param>
    /// <returns>The Chunk object if found and in bounds; otherwise, null.</returns>
    [CanBeNull]
    public Chunk GetChunkFromChunkCoord(ChunkCoord chunkCoord)
    {
        // "Is in World" bounds check before accessing the array.
        if (!IsChunkInWorld(chunkCoord))
        {
            return null; // Return null if the coordinate is outside the world.
        }

        return _chunkMap.GetValueOrDefault(chunkCoord);
    }

    /// <summary>
    /// Retrieves the active chunk object containing the specified world position.
    /// </summary>
    /// <param name="pos">The world-space position.</param>
    /// <returns>The Chunk object if found and in bounds; otherwise, null.</returns>
    [CanBeNull]
    public Chunk GetChunkFromVector3(Vector3 pos)
    {
        int x = Mathf.FloorToInt(pos.x / VoxelData.ChunkWidth);
        int z = Mathf.FloorToInt(pos.z / VoxelData.ChunkWidth);

        // "Is in World" bounds check before accessing the array.
        if (!IsChunkInWorld(x, z))
        {
            return null; // Return null if the coordinate is outside the world.
        }

        return _chunkMap.GetValueOrDefault(ChunkCoord.FromWorldPosition(pos));
    }

    /// <summary>
    /// Unloads chunks that are outside the load distance.
    /// Saves them if modified, destroys the GameObject, and removes data from memory.
    /// </summary>
    private void UnloadChunks()
    {
        // Guard Chunk Unloading
        // If Persistence is disabled, we intentionally keep ALL chunks in memory.
        if (!settings.EnablePersistence) return;

        // OPTIMIZATION: Use ListPool to avoid allocations
        List<ChunkCoord> chunksToRemove = ListPool<ChunkCoord>.Get();
        int unloadDistance = settings.loadDistance + 2; // Buffer to prevent flickering

        // Step A: Identify candidates
        foreach (KeyValuePair<Vector2Int, ChunkData> kvp in worldData.Chunks)
        {
            ChunkCoord chunkCoord = ChunkCoord.FromVoxelOrigin(kvp.Key);

            // Calculate distance check
            if (Mathf.Abs(chunkCoord.X - PlayerChunkCoord.X) > unloadDistance ||
                Mathf.Abs(chunkCoord.Z - PlayerChunkCoord.Z) > unloadDistance)
            {
                chunksToRemove.Add(chunkCoord);
            }
        }

        // Step B: Unload
        foreach (ChunkCoord chunkCoord in chunksToRemove)
        {
            Vector2Int chunkVoxelPos = chunkCoord.ToVoxelOrigin();

            if (!worldData.Chunks.TryGetValue(chunkVoxelPos, out ChunkData data))
                continue;

            // Safety: Don't unload if a job is currently touching it
            bool isJobRunning = JobManager.generationJobs.ContainsKey(chunkCoord)
                                || JobManager.meshJobs.ContainsKey(chunkCoord)
                                || JobManager.lightingJobs.ContainsKey(chunkCoord);

            // Check data state logic to prevent unloading chunks that have lighting work in the pipeline but no active job.
            bool isProcessingLight = data.IsAwaitingMainThreadProcess ||
                                     data.HasLightChangesToProcess;

            if (isJobRunning || isProcessingLight)
            {
                // Skip unload - chunk is still being processed
                continue;
            }

            // 1. Persist Orphaned Lighting Queue
            if (worldData.SunlightRecalculationQueue.TryGetValue(chunkVoxelPos, out HashSet<Vector2Int> globalCols))
            {
                if (globalCols != null && globalCols.Count > 0)
                {
                    // Convert to Local Coordinates (0-15) for storage
                    HashSet<Vector2Int> localCols = HashSetPool<Vector2Int>.Get(); // POOLING
                    foreach (Vector2Int gCol in globalCols)
                    {
                        localCols.Add(new Vector2Int(gCol.x - chunkVoxelPos.x, gCol.y - chunkVoxelPos.y));
                    }

                    // Save to Persistence
                    LightingStateManager.AddPending(chunkCoord, localCols);

                    Debug.Log($"[LIGHTING RESCUE] Saved {localCols.Count} orphaned sunlight columns for chunk {chunkCoord}");

                    // Release temp set (AddPending makes its own copy)
                    HashSetPool<Vector2Int>.Release(localCols);
                }

                worldData.SunlightRecalculationQueue.Remove(chunkVoxelPos);

                // CRITICAL: We are removing this set from the active world entirely, so it must be returned to the pool!
                if (globalCols != null) HashSetPool<Vector2Int>.Release(globalCols);
            }

            // 2. Save if modified
            if (worldData.ModifiedChunks.Contains(data))
            {
                // Fire and forget (StorageManager handles the Snapshot lifecycle)
                Task saveTask = StorageManager.SaveChunkAsync(data, _shutdownTokenSource.Token);

                saveTask.ContinueWith(t =>
                {
                    if (t.IsFaulted) Debug.LogError($"[UnloadChunks] Save failed for {chunkCoord}: {t.Exception}");
                });

                worldData.ModifiedChunks.Remove(data);
            }

            // POOLING: Recycle Visuals
            if (_chunkMap.TryGetValue(chunkCoord, out Chunk chunkObj))
            {
                // Cleanup visualizers
                if (voxelVisualizer != null) voxelVisualizer.ClearChunkVisualization(chunkCoord);
                if (_chunkBorders.TryGetValue(chunkCoord, out GameObject b))
                {
                    // POOLING: Return to pool
                    ChunkPool.ReturnBorder(b);
                    _chunkBorders.Remove(chunkCoord);
                }

                // Remove from mesh queue before returning to pool.
                // This prevents dead chunk references from lingering in the list (Memory Leak / Logic Error).
                if (_chunksToBuildMeshSet.Remove(chunkCoord))
                {
                    _chunksToBuildMesh.Remove(chunkObj);
                }

                // Return to pool
                ChunkPool.Return(chunkObj);
                _chunkMap.Remove(chunkCoord);
            }

            // 3. Remove Data Reference from World
            worldData.Chunks.Remove(chunkVoxelPos);

            // 4. Recycle Data
            // POOLING: Return data to pool
            ChunkPool.ReturnChunkData(data);
        }

        // 5. Return temp pools back to pool list
        ListPool<ChunkCoord>.Release(chunksToRemove); // Free the ListPool
    }

    /// <summary>
    /// Checks the view distance and updates the active chunks.
    /// </summary>
    private void CheckViewDistance()
    {
        clouds.UpdateClouds();

        ChunkCoord playerCurrentChunkCoord = GetChunkCoordFromVector3(_playerTransform.position);

        // Return early if the player hasn't moved outside the last chunk.
        if (playerCurrentChunkCoord.Equals(_playerLastChunkCoord)) return;
        _playerLastChunkCoord = playerCurrentChunkCoord;

        // OPTIMIZATION: Clear cached sets instead of allocating new ones
        _currentViewChunks.Clear();

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
            ChunkCoord chunkCoord = playerCurrentChunkCoord.Neighbor(spiralLoop.X, spiralLoop.Z);

            if (IsChunkInWorld(chunkCoord))
            {
                Vector2Int chunkVoxelPos = chunkCoord.ToVoxelOrigin();

                // If chunk not in memory at all
                if (!worldData.Chunks.TryGetValue(chunkVoxelPos, out ChunkData data))
                {
                    // Create placeholder
                    data = Instance.ChunkPool.GetChunkData(chunkVoxelPos);
                    worldData.Chunks.Add(chunkVoxelPos, data);
                }

                // If it's empty, and not currently fetching from disk, and not currently generating... start the pipeline!
                if (!data.IsPopulated && !data.IsLoading && !JobManager.generationJobs.ContainsKey(chunkCoord))
                {
                    // Trigger Async Load
                    data.IsLoading = true;
                    _ = LoadOrGenerateChunk(chunkCoord);
                }

                // If within view distance, it's a candidate for being active.
                if (Mathf.Abs(chunkCoord.X - playerCurrentChunkCoord.X) <= viewDist &&
                    Mathf.Abs(chunkCoord.Z - playerCurrentChunkCoord.Z) <= viewDist)
                {
                    _currentViewChunks.Add(chunkCoord);
                }
            }

            // Next spiral coord
            spiralLoop.Next();
        }

        // Deactivate chunks that are no longer in view.
        _chunksToRemove.Clear();
        foreach (ChunkCoord chunkCoord in _activeChunks)
        {
            if (!_currentViewChunks.Contains(chunkCoord))
            {
                _chunksToRemove.Add(chunkCoord);
            }
        }

        foreach (ChunkCoord chunkCoord in _chunksToRemove)
        {
            // Deactivate chunk border visualization
            if (_chunkBorders.TryGetValue(chunkCoord, out GameObject borderObject))
            {
                // POOLING: Return to pool
                ChunkPool.ReturnBorder(borderObject);
                _chunkBorders.Remove(chunkCoord);
            }

            // Deactivate chunk itself
            if (_chunkMap.TryGetValue(chunkCoord, out Chunk chunk))
            {
                // Remove from mesh queue to prevent processing deactivated chunks
                if (_chunksToBuildMeshSet.Remove(chunkCoord))
                {
                    _chunksToBuildMesh.Remove(chunk);
                }

                // POOLING: Return chunk to pool instead of just deactivating
                ChunkPool.Return(chunk);
                _chunkMap.Remove(chunkCoord);
            }

            // Debug: Clear visualization.
            if (voxelVisualizer != null)
                voxelVisualizer.ClearChunkVisualization(chunkCoord);
        }

        // Activate chunks that have entered view.
        foreach (ChunkCoord chunkCoord in _currentViewChunks)
        {
            if (!_chunkMap.ContainsKey(chunkCoord))
            {
                // POOLING: Get from pool
                Chunk newChunk = ChunkPool.Get(chunkCoord);
                _chunkMap.Add(chunkCoord, newChunk);
                CreateChunkBorder(chunkCoord);

                // Only request a mesh if the data is actually ready.
                // If IsPopulated is false, the Load/Gen pipeline will trigger the mesh build later.
                if (newChunk.ChunkData.IsPopulated)
                {
                    RequestChunkMeshRebuild(newChunk);
                }
            }
            else
            {
                Chunk chunk = _chunkMap[chunkCoord];
                // NOTE: Should technically rarely happen if we remove from map on deactivate,
                //       but good for safety if logic drifts.
                if (!chunk.isActive)
                {
                    chunk.isActive = true;
                    if (!_chunkBorders.ContainsKey(chunkCoord))
                    {
                        CreateChunkBorder(chunkCoord);
                    }

                    // If we reactivate a chunk that lost its data (rare/impossible?), don't mesh.
                    if (chunk.ChunkData.IsPopulated)
                    {
                        RequestChunkMeshRebuild(chunk);
                    }
                }
            }

            AddChunksToUpdateVisualization(chunkCoord);
        }

        // Update the master activeChunks set.
        // OPTIMIZATION: Clear and copy to avoid replacing the reference with a new allocation.
        _activeChunks.Clear();
        _activeChunks.UnionWith(_currentViewChunks);

        // Run cleanup
        UnloadChunks();
    }

    #region Debug Methods

    /// <summary>
    /// Creates a visualization of the chunk border.
    /// </summary>
    /// <param name="chunkCoord">The chunk coordinate.</param>
    private void CreateChunkBorder(ChunkCoord chunkCoord)
    {
        if (chunkBorderPrefab == null)
        {
            Debug.LogError("ChunkBorderPrefab must be assigned in the World inspector.", this);
            return;
        }

        if (_chunkBorders.ContainsKey(chunkCoord)) return;

        // POOLING: Use ChunkPoolManager
        Vector3 pos = chunkCoord.ToWorldPosition();
        GameObject borderObject = ChunkPool.GetBorder(chunkBorderPrefab, pos, _chunkBorderParent);

        // Ensure state matches setting (Pool might return active object, but setting might be off)
        borderObject.SetActive(settings.showChunkBorders);
        _chunkBorders.Add(chunkCoord, borderObject);
    }

    /// <summary>
    /// Adds a chunk to the list of chunks to update visualization for.
    /// </summary>
    /// <param name="chunkCoord">The chunk coordinate.</param>
    public void AddChunksToUpdateVisualization(ChunkCoord chunkCoord)
    {
        // Ensure we never add an out-of-bounds coordinate.
        if (IsChunkInWorld(chunkCoord))
        {
            _chunksToUpdateVisualization.Add(chunkCoord);
        }
    }

    /// <summary>
    /// Handles the visualization of the internal voxel state.
    /// </summary>
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
            // Use Pools for tracking collections
            List<ChunkCoord> chunksReadyForVisualization = ListPool<ChunkCoord>.Get();
            Dictionary<ChunkCoord, Dictionary<Vector3Int, Color>> chunkDataCache = DictionaryPool<ChunkCoord, Dictionary<Vector3Int, Color>>.Get();

            try
            {
                // Identify which chunks are actually ready to be visualized.
                foreach (ChunkCoord coord in _chunksToUpdateVisualization)
                {
                    if (_chunkMap.TryGetValue(coord, out Chunk chunk))
                    {
                        // A chunk is ready if it exists, is not currently processing a lighting job,
                        // and has no pending lighting changes on the main thread.
                        if (!JobManager.lightingJobs.ContainsKey(coord) && !chunk.ChunkData.HasLightChangesToProcess)
                        {
                            chunksReadyForVisualization.Add(coord);
                        }
                    }
                }


                // Pre-cache all required data in one go for chunks that are ready
                foreach (ChunkCoord coord in chunksReadyForVisualization)
                {
                    if (_chunkMap.TryGetValue(coord, out Chunk chunk))
                    {
                        // Explicit Ownership. The caller requests the pooled dictionary and passes it into the helper method to be populated.
                        Dictionary<Vector3Int, Color> voxelsToDraw = DictionaryPool<Vector3Int, Color>.Get();
                        GetVoxelDataForVisualization(chunk, voxelsToDraw);
                        chunkDataCache[coord] = voxelsToDraw;
                    }
                }

                // Iterate through the cached data to draw meshes
                foreach ((ChunkCoord coord, Dictionary<Vector3Int, Color> value) in chunkDataCache)
                {
                    // Get neighbor data from the cache, or null if not available.
                    chunkDataCache.TryGetValue(coord.Neighbor(0, 1), out Dictionary<Vector3Int, Color> northData);
                    chunkDataCache.TryGetValue(coord.Neighbor(0, -1), out Dictionary<Vector3Int, Color> southData);
                    chunkDataCache.TryGetValue(coord.Neighbor(1, 0), out Dictionary<Vector3Int, Color> eastData);
                    chunkDataCache.TryGetValue(coord.Neighbor(-1, 0), out Dictionary<Vector3Int, Color> westData);

                    // Call the visualizer method with all neighbor data.
                    voxelVisualizer.UpdateChunkVisualization(coord, value, northData, southData, eastData,
                        westData);
                }

                // Remove only the processed chunks from the update set.
                // Chunks that were not ready will remain in the set to be checked next frame.
                foreach (ChunkCoord coord in chunksReadyForVisualization)
                {
                    _chunksToUpdateVisualization.Remove(coord);
                }
            }
            finally
            {
                // ALWAYS release pools to prevent memory leaks, even on errors
                foreach (Dictionary<Vector3Int, Color> dict in chunkDataCache.Values)
                {
                    if (dict != null) DictionaryPool<Vector3Int, Color>.Release(dict);
                }

                DictionaryPool<ChunkCoord, Dictionary<Vector3Int, Color>>.Release(chunkDataCache);
                ListPool<ChunkCoord>.Release(chunksReadyForVisualization);
            }
        }
    }

    /// <summary>
    /// Gathers the positions and colors of voxels to be visualized for a given chunk,
    /// based on the current visualization mode.
    /// </summary>
    /// <param name="chunk">The chunk to gather voxel data from.</param>
    /// <param name="voxelsToDraw">The dictionary to populate with the visualization data.</param>
    private void GetVoxelDataForVisualization(Chunk chunk, Dictionary<Vector3Int, Color> voxelsToDraw)
    {
        if (chunk == null || !chunk.ChunkData.IsPopulated) return;

        switch (visualizationMode)
        {
            case DebugVisualizationMode.ActiveVoxels:
                // Instead of checking every voxel, iterate only through the known active list.
                foreach (Vector3Int localPos in chunk.ActiveVoxels)
                {
                    voxelsToDraw[localPos] = new Color(1f, 0f, 0f, 0.7f); // Red for active
                }

                break;

            // For other modes, we iterate sections to efficiently skip empty space.
            case DebugVisualizationMode.Sunlight:
            case DebugVisualizationMode.Blocklight:
            case DebugVisualizationMode.FluidLevel:

                // Loop through all sections in the chunk
                for (int s = 0; s < chunk.ChunkData.sections.Length; s++)
                {
                    ChunkSection section = chunk.ChunkData.sections[s];

                    // Optimization: Skip null or empty sections entirely
                    if (section == null || section.IsEmpty) continue;

                    int startY = s * ChunkMath.SECTION_SIZE;

                    // Loop through the voxels in this specific section
                    for (int i = 0; i < section.voxels.Length; i++)
                    {
                        uint packedData = section.voxels[i];

                        // --- OPTIMIZATION: Get ID first and skip if it's air ---
                        if (BurstVoxelDataBitMapping.GetId(packedData) == BlockIDs.Air)
                        {
                            continue;
                        }

                        // Convert section index to 3D position
                        int x = i % ChunkMath.SECTION_SIZE;
                        int yOffset = i / ChunkMath.SECTION_SIZE % ChunkMath.SECTION_SIZE;
                        int z = i / (ChunkMath.SECTION_SIZE * ChunkMath.SECTION_SIZE);

                        Vector3Int localPos = new Vector3Int(x, startY + yOffset, z);

                        VoxelState state = new VoxelState(packedData);
                        Color? color = null;

                        if (visualizationMode == DebugVisualizationMode.Sunlight && state.Sunlight > 0)
                        {
                            color = new Color(1f, 1f, 0f, state.Sunlight / 15f * 0.8f); // Yellow
                        }
                        else if (visualizationMode == DebugVisualizationMode.Blocklight && state.Blocklight > 0)
                        {
                            color = new Color(1f, 0.5f, 0f, state.Blocklight / 15f * 0.8f); // Orange
                        }
                        else if (visualizationMode == DebugVisualizationMode.FluidLevel &&
                                 state.Properties.fluidType != FluidType.None)
                        {
                            byte fluidLevel = state.FluidLevel;
                            if (fluidLevel >= 8) // Falling blocks (FluidLevel 8-15)
                            {
                                byte effectiveLevel = (byte)(fluidLevel & 0x7);
                                float levelRatio = (7 - effectiveLevel) / 7f;
                                color = new Color(0f, 1f, 0.8f, 0.5f + levelRatio * 0.3f); // Cyan/teal
                            }
                            else // Horizontal flow (FluidLevel 0-7)
                            {
                                float levelRatio = (7 - fluidLevel) / 7f;
                                color = new Color(0f, levelRatio * 0.7f, 1f, 0.7f); // Blue gradient
                            }
                        }

                        if (color.HasValue)
                        {
                            voxelsToDraw[localPos] = color.Value;
                        }
                    }
                }

                break;

            case DebugVisualizationMode.None:
            default:
                break;
        }
    }

    #endregion

    /// <summary>
    /// Finds the absolute world-space Y-coordinate of the highest solid voxel at a given X/Z position.
    /// <para>Priority: Chunk Data (respects structures) -> World Generation (expensive fallback) -> World Height (safe fallback).</para>
    /// </summary>
    /// <param name="worldPos">The world-space position to check.</param>
    /// <returns>A Vector3Int containing the highest solid block's coordinates.</returns>
    public Vector3Int GetHighestVoxel(Vector3Int worldPos)
    {
        const int yMax = VoxelData.ChunkHeight - 1;
        int x = worldPos.x;
        int y = worldPos.y;
        int z = worldPos.z;


        Vector3Int worldHeight = new Vector3Int(x, yMax, z);
        ChunkCoord chunkCoord = ChunkCoord.FromWorldPosition(worldPos);

        // Voxel outside the world, highest voxel is world height.
        if (!worldData.IsVoxelInWorld(worldPos))
        {
            Debug.Log($"Voxel not in world for X / Y/ Z = {x} / {y} / {z}, returning world height.");
            return worldHeight;
        }

        // Find the chunk data for this position.
        // Requesting the chunk ensures that the data is loaded from disk or generated if it doesn't exist.
        // This is the only reliable way to get voxel data that includes modifications (like trees).
        Vector2Int chunkVoxelPos = worldData.GetChunkCoordFor(worldPos);
        ChunkData chunkData = worldData.RequestChunk(chunkVoxelPos, true);

        // Chunk is created and editable, calculate the highest voxel using chunkData function.
        if (chunkData != null)
        {
            Debug.Log($"Finding highest voxel for chunk {chunkCoord.X} / {chunkCoord.Z} in wold for X / Z = {x} / {z} using chunk function.");

            // Get the (highest) local voxel position within the chunk.
            Vector3Int localPos = worldData.GetLocalVoxelPositionInChunk(worldPos);
            Vector3Int highestVoxelLocal = chunkData.GetHighestVoxel(localPos);

            // Get the world position of the highest voxel.
            Vector3Int highestVoxel = new Vector3Int(x, highestVoxelLocal.y, z);
            Debug.Log($"Highest voxel in chunk {chunkCoord.X} / {chunkCoord.Z} is {highestVoxel}.");
            return highestVoxel;
        }

        Debug.Log($"Chunk {chunkCoord.X} / {chunkCoord.Z} is not created, accurate result is not possible.");

        // Chunk is not created, calculate the highest voxel using expensive world generation code.
        // NOTE: This will not include voxel modifications (like trees).
        Debug.Log($"Finding highest voxel in wold for X / Z = {x} / {z} using expensive world generation code. This will not include voxel modifications (eg: trees)");
        for (int i = yMax; i > 0; i--)
        {
            Vector3Int currentVoxel = new Vector3Int(x, i, z);
            byte voxelBlockId = WorldGen.GetVoxel(currentVoxel, VoxelData.Seed, JobDataManager.BiomesJobData,
                JobDataManager.AllLodesJobData);
            if (!blockDatabase.blockTypes[voxelBlockId].isSolid) continue;
            Debug.Log($"Highest voxel in chunk {chunkCoord.X} / {chunkCoord.Z} is {currentVoxel}.");
            return currentVoxel;
        }

        // Fallback, highest voxel is world height
        Debug.Log($"No solid voxels found for X / Z = {x} / {z}, returning world height.");
        return worldHeight;
    }

    /// <summary>
    /// Determines if a voxel at the given world position is solid.
    /// </summary>
    /// <param name="worldPos">The world-space position.</param>
    /// <returns>True if the voxel is solid; otherwise, false.</returns>
    public bool CheckForVoxel(Vector3 worldPos)
    {
        VoxelState? voxel = worldData.GetVoxelState(worldPos);
        return voxel.HasValue && blockDatabase.blockTypes[voxel.Value.id].isSolid;
    }

    /// <summary>
    /// Determines if a voxel at the given world position should cause physical collision (solid and not a fluid).
    /// </summary>
    /// <param name="pos">The world-space position.</param>
    /// <returns>True if the voxel is solid and not water; otherwise, false.</returns>
    public bool CheckForCollision(Vector3 pos)
    {
        VoxelState? voxel = worldData.GetVoxelState(pos);
        return voxel.HasValue && voxel.Value.Properties.isSolid && voxel.Value.Properties.fluidType == FluidType.None;
    }

    /// <summary>
    /// Retrieves the full state of a voxel at a given world position.
    /// </summary>
    /// <param name="worldPos">The world-space position.</param>
    /// <returns>The VoxelState if the position is within world bounds; otherwise, null.</returns>
    public VoxelState? GetVoxelState(Vector3 worldPos)
    {
        return worldData.GetVoxelState(worldPos);
    }

    /// <summary>
    /// Gets or sets whether the game is currently in a UI menu.
    /// Manages cursor locking and UI window visibility.
    /// </summary>
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

    /// <summary>
    /// Checks if the specified chunk coordinate is within the permitted world boundaries.
    /// </summary>
    /// <param name="chunkCoord">The chunk coordinate.</param>
    /// <returns>True if the chunk is in the world; otherwise, false.</returns>
    private static bool IsChunkInWorld(ChunkCoord chunkCoord)
    {
        return chunkCoord.X is >= 0 and < VoxelData.WorldSizeInChunks &&
               chunkCoord.Z is >= 0 and < VoxelData.WorldSizeInChunks;
    }

    /// <summary>
    /// Checks if the specified X/Z coordinates are within the permitted world boundaries.
    /// </summary>
    /// <param name="x">The X coordinate.</param>
    /// <param name="z">The Z coordinate.</param>
    /// <returns>True if in world; otherwise, false.</returns>
    private static bool IsChunkInWorld(int x, int z)
    {
        return x is >= 0 and < VoxelData.WorldSizeInChunks &&
               z is >= 0 and < VoxelData.WorldSizeInChunks;
    }

    #region Public Interface Methods

    /// <summary>
    /// Toggles the debug screen between Off, FPS Only, and Full Debug modes.
    /// </summary>
    public void ToggleDebugScreen()
    {
        if (!debugScreen.activeSelf)
        {
            // State 1: Off -> FPS Only
            debugScreen.SetActive(true);
            debugScreen.GetComponent<DebugScreen>().SetMode(DebugScreen.DebugMode.FPSOnly);
        }
        else
        {
            DebugScreen dbg = debugScreen.GetComponent<DebugScreen>();
            if (dbg.CurrentMode == DebugScreen.DebugMode.FPSOnly)
            {
                // State 2: FPS Only -> Full Debug
                dbg.SetMode(DebugScreen.DebugMode.Full);
            }
            else
            {
                // State 3: Full Debug -> Off
                debugScreen.SetActive(false);
            }
        }
    }

    /// <summary>
    /// Saves all chunks currently marked as modified.
    /// </summary>
    /// <param name="synchronous">
    /// If true, saves immediately on the main thread (CRITICAL for OnApplicationQuit).
    /// If false, schedules background tasks (Good for Auto-Save/Manual Save).
    /// </param>
    private void SaveAllModifiedChunks(bool synchronous)
    {
        if (worldData.ModifiedChunks.Count == 0) return;

        // Snapshot the list to avoid collection modification errors
        List<ChunkData> chunksToSave = new List<ChunkData>(worldData.ModifiedChunks);
        worldData.ModifiedChunks.Clear();

        Debug.Log($"Saving {chunksToSave.Count} modified chunks (Sync: {synchronous})...");

        foreach (ChunkData data in chunksToSave)
        {
            if (synchronous)
            {
                StorageManager.SaveChunk(data); // Sync method
            }
            else
            {
                // Pass the token so manual saves don't keep running if we suddenly quit
                _ = StorageManager.SaveChunkAsync(data, _shutdownTokenSource.Token);
            }
        }
    }

    /// <summary>
    /// Saves all modified chunk data and global world metadata to disk.
    /// This is an asynchronous operation.
    /// </summary>
    public void SaveWorldData()
    {
        // For manual/auto saves, use Async to prevent freezing the game
        SaveAllModifiedChunks(false);

        SaveSystem.SaveWorld(Instance);
        Debug.Log("[Manual Save] World data saved successfully.");
    }

    /// <summary>
    /// Cycles through the available voxel debug visualization modes.
    /// </summary>
    public void CycleVisualizationMode()
    {
        int currentModeIndex = (int)visualizationMode;
        currentModeIndex++;
        int modeCount = Enum.GetValues(typeof(DebugVisualizationMode)).Length;

        if (currentModeIndex >= modeCount)
        {
            currentModeIndex = 0;
        }

        visualizationMode = (DebugVisualizationMode)currentModeIndex;
        Debug.Log($"Voxel Visualization Mode set to: {visualizationMode}");
    }

    #endregion

    #region Debug Information Methods

    /// <summary>
    /// Gets the number of chunks currently waiting for a mesh rebuild.
    /// </summary>
    /// <returns>The mesh build queue count.</returns>
    public int GetChunksToBuildMeshCount()
    {
        return _chunksToBuildMesh.Count;
    }

    /// <summary>
    /// Gets the total number of individual voxel modifications currently queued.
    /// </summary>
    /// <returns>The number of individual voxel modifications waiting to be processed.</returns>
    public int GetVoxelModificationsCount()
    {
        return _modifications.Count;
    }

    /// <summary>
    /// Calculates the sum of all active voxels across all currently loaded chunks.
    /// </summary>
    /// <returns>Total active voxel count.</returns>
    public int GetTotalActiveVoxelsInWorld()
    {
        int total = 0;

        foreach (ChunkCoord coord in _activeChunks)
        {
            if (_chunkMap.TryGetValue(coord, out Chunk chunk))
            {
                total += chunk.GetActiveVoxelCount();
            }
        }

        return total;
    }

    /// <summary>
    /// Gets a detailed breakdown of the mesh queue for display in debug UI.
    /// Note: Categories evaluate strictly in order. A chunk that is both null and inactive will only increment Null.
    /// </summary>
    /// <returns>Formatted string with mesh queue statistics</returns>
    public string GetMeshQueueDebugInfo()
    {
        int active = 0;
        int inactive = 0;
        int destroyed = 0;
        int nullCount = 0;

        foreach (Chunk c in _chunksToBuildMesh)
        {
            if (c == null)
                nullCount++;
            else if (c.ChunkGameObject == null)
                destroyed++;
            else if (!c.isActive)
                inactive++;
            else
                active++;
        }

        return $"{_chunksToBuildMesh.Count} total\n" +
               $" └ Active: {active}, Inactive: {inactive}, Destroyed: {destroyed}, Null: {nullCount}";
    }

    #endregion
}
