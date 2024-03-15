using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using MyBox;
using UnityEngine;
using UnityEngine.Serialization;
using Random = UnityEngine.Random;
using System.Threading;
using System.IO;
using Data;
using Helpers;
using UnityEditor;

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
    public ChunkCoord playerChunkCoord;
    private ChunkCoord playerLastChunkCoord;

    private List<Chunk> chunksToUpdate = new List<Chunk>();
    public ConcurrentQueue<Chunk> chunksToDraw = new ConcurrentQueue<Chunk>();

    private bool applyingModifications = false;

    private ConcurrentQueue<ConcurrentQueue<VoxelMod>> modifications = new ConcurrentQueue<ConcurrentQueue<VoxelMod>>();

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
    [ReadOnly] public string appSaveDataPath;

    // Shader Properties
    private static readonly int ShaderGlobalLightLevel = Shader.PropertyToID("GlobalLightLevel");
    private static readonly int ShaderMinGlobalLightLevel = Shader.PropertyToID("minGlobalLightLevel");
    private static readonly int ShaderMaxGlobalLightLevel = Shader.PropertyToID("maxGlobalLightLevel");

    // Threading
    private Thread ChunkUpdateThread;
    public object ChunkUpdateThreadLock = new object();
    public object ChunkListThreadLock = new object();

#region Singleton pattern
    public static World Instance { get; private set; }

    private void Awake()
    {
        // If the instance value is not null and not *this*, we've somehow ended up with more than one World component.
        // Since another one has already been assigned, delete this one.
        if (Instance != null && Instance != this)
        {
            Destroy(this.gameObject);
        }
        else
        {
            Instance = this;
            appSaveDataPath = Application.persistentDataPath;
        }
    }
#endregion


    private void Start()
    {
        Debug.Log($"Generating new world using seed: {VoxelData.Seed}");

        // Get player transform component
        playerTransform = player.GetComponent<Transform>();
        
        // Get main camera.
        playerCamera = Camera.main!;

        // Create settings file if it doesn't yet exist, after that, load it.
        if (!File.Exists(settingFilePath) || Application.isEditor)
        {
            string jsonExport = JsonUtility.ToJson(settings, true);
            File.WriteAllText(settingFilePath, jsonExport);
            AssetDatabase.Refresh(); // Refresh Unity's asset database.
        }

#if !UNITY_EDITOR
        string jsonImport = File.ReadAllText(settingFilePath);
        settings = JsonUtility.FromJson<Settings>(jsonImport);
# endif

        // TODO: Set worldName using UI
        if (settings.loadSaveDataOnStartup)
            worldData = SaveSystem.LoadWorld("Prototype", VoxelData.Seed);
        else
            worldData = new WorldData("Prototype", VoxelData.Seed);


        Random.InitState(VoxelData.Seed);

        Shader.SetGlobalFloat(ShaderMinGlobalLightLevel, VoxelData.MinLightLevel);
        Shader.SetGlobalFloat(ShaderMaxGlobalLightLevel, VoxelData.MaxLightLevel);
        SetGlobalLightValue();

        // Set initial spawnPosition to the center of the world for X & Z, and top of the world for Y.
        spawnPosition = new Vector3(VoxelData.WorldCentre, VoxelData.ChunkHeight - 1f, VoxelData.WorldCentre);
        LoadWorld();

        // Now set the the Y position on top of the highest voxel at the initial location.
        spawnPosition = GetHighestVoxel(spawnPosition) + spawnPositionOffset;
        playerTransform.position = spawnPosition;
        CheckViewDistance();


        playerLastChunkCoord = GetChunkCoordFromVector3(playerTransform.position);

        if (settings.enableThreading)
        {
            ChunkUpdateThread = new Thread(new ThreadStart(ThreadedUpdate));
            ChunkUpdateThread.Start();
        }

        StartCoroutine(Tick());
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
                chunks[coord.x, coord.z].TickUpdate();
            }
            yield return new WaitForSeconds(VoxelData.TickLength);
        }
    }

    private void Update()
    {
        playerChunkCoord = GetChunkCoordFromVector3(playerTransform.position);

        // Only update the chunks if the player has moved from the chunk they where previously on.
        if (!playerChunkCoord.Equals(playerLastChunkCoord))
        {
            CheckLoadDistance();
            CheckViewDistance();
        }

        if (!settings.enableThreading)
        {
            // ReSharper disable InconsistentlySynchronizedField
            if (!applyingModifications)
                ApplyModifications();

            if (chunksToUpdate.Count > 0)
                UpdateChunks();
            // ReSharper restore InconsistentlySynchronizedField
        }

        if (chunksToDraw.Count > 0)
        {
            if (chunksToDraw.TryDequeue(out Chunk chunk))
            {
                try
                {
                    chunk.CreateMesh();
                }
                catch (Exception e)
                {
                    Debug.Log("Chunk MeshCreation Exception: " + e);
                }
            }
        }


        // UI - DEBUG SCREEN
        if (Input.GetKeyDown(KeyCode.F3))
            debugScreen.SetActive(!debugScreen.activeSelf);

        if (Input.GetKeyDown(KeyCode.F4))
            SaveSystem.SaveWorld(worldData);
    }

    private void LoadWorld()
    {
        ChunkCoord centerChunkCoord = GetChunkCoordFromVector3(spawnPosition);
        SpiralLoop spiralLoop = new SpiralLoop();

        int x = centerChunkCoord.x;
        int z = centerChunkCoord.z;

        int loadChunkDistance = VoxelData.WorldSizeInChunks / 2 + settings.loadDistance;

        // Don't try to load chunks outside of the world
        if (loadChunkDistance > VoxelData.WorldSizeInChunks)
            loadChunkDistance = VoxelData.WorldSizeInChunks;

        while (x < loadChunkDistance && z < loadChunkDistance)
        {
            // Debug.Log($"World.LoadWorld | Loading chunk X / Z: {x} / {z}");
            worldData.LoadChunk(new Vector2Int(x, z));

            // Next spiral coord
            spiralLoop.Next();
            x = centerChunkCoord.x + spiralLoop.X;
            z = centerChunkCoord.z + spiralLoop.Z;
        }
    }

    public void AddChunkToUpdate(Chunk chunk, bool immediateUpdate = false)
    {
        // Lock list to ensure only one thing is using the list at a time.
        lock (ChunkUpdateThreadLock)
        {
            // Make sure update list doesn't already contain the chunk.
            if (!chunksToUpdate.Contains(chunk))
            {
                if (immediateUpdate)
                    chunksToUpdate.Insert(0, chunk);
                else
                    chunksToUpdate.Add(chunk);
            }
        }
    }

    private void UpdateChunks()
    {
        lock (ChunkUpdateThreadLock)
        {
            Chunk chunkToUpdate = chunksToUpdate[0];
            chunkToUpdate.UpdateChunk();
            activeChunks.Add(chunkToUpdate.coord);

            chunksToUpdate.RemoveAt(0);
        }
    }

    /// <summary>
    /// Update loop running on a second thread.
    /// </summary>
    private void ThreadedUpdate()
    {
        while (true)
        {
            if (!applyingModifications)
                ApplyModifications();

            lock (ChunkUpdateThreadLock)
            {
                if (chunksToUpdate.Count > 0)
                    UpdateChunks();
            }
        }
        // ReSharper disable once FunctionNeverReturns
    }

    /// <summary>
    /// Disable second update thread when world gameObject is disabled.
    /// </summary>
    private void OnDisable()
    {
        if (settings.enableThreading)
            ChunkUpdateThread.Abort();
    }

    private void ApplyModifications()
    {
        applyingModifications = true;

        while (modifications.Count > 0)
        {
            // Try getting the queue, if not successful retry later
            if (!modifications.TryDequeue(out ConcurrentQueue<VoxelMod> queue)) continue;

            while (queue.Count > 0)
            {
                // Try getting the voxelMod, if not successful retry later
                if (!queue.TryDequeue(out VoxelMod v)) continue;
                ChunkCoord c = GetChunkCoordFromVector3(v.position);

                worldData.SetVoxel(v.position, v.id, 1);
            }
        }

        applyingModifications = false;
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

        return chunks[x, z];
    }
    
    private void CheckLoadDistance()
    {
        ChunkCoord coord = GetChunkCoordFromVector3(playerTransform.position);
        playerLastChunkCoord = playerChunkCoord;

        // Loop trough all chunks currently within view distance of the player.
        SpiralLoop spiralLoop = new SpiralLoop();
        ChunkCoord thisChunkCoord = coord;
        
        while (thisChunkCoord.x < coord.x + settings.loadDistance && thisChunkCoord.z < coord.z + settings.loadDistance)
        {
            thisChunkCoord = new ChunkCoord(coord.x + spiralLoop.X, coord.z + spiralLoop.Z);
            int x = thisChunkCoord.x;
            int z = thisChunkCoord.z;

            // If the current chunk is in the world...
            if (IsChunkInWorld(thisChunkCoord))
            {
                worldData.LoadChunk(new Vector2Int(x, z));
            }

            // Next spiral coord
            spiralLoop.Next();
        }
    }
    
    private void CheckViewDistance()
    {
        clouds.UpdateClouds();

        ChunkCoord coord = GetChunkCoordFromVector3(playerTransform.position);
        playerLastChunkCoord = playerChunkCoord;

        // Copy currently active chunks.
        HashSet<ChunkCoord> previouslyActiveChunks = new HashSet<ChunkCoord>(activeChunks);

        // Clear active chunks.
        activeChunks.Clear();

        // Loop trough all chunks currently within view distance of the player.
        SpiralLoop spiralLoop = new SpiralLoop();
        ChunkCoord thisChunkCoord = coord;

        while (thisChunkCoord.x < coord.x + settings.viewDistance && thisChunkCoord.z < coord.z + settings.viewDistance)
        {
            thisChunkCoord = new ChunkCoord(coord.x + spiralLoop.X, coord.z + spiralLoop.Z);
            int x = thisChunkCoord.x;
            int z = thisChunkCoord.z;

            // If the current chunk is in the world...
            if (IsChunkInWorld(thisChunkCoord))
            {
                // Check if it is active, if not, activate it.
                chunks[x, z] ??= new Chunk(thisChunkCoord);

                chunks[x, z].isActive = true;
                activeChunks.Add(thisChunkCoord);
            }

            // Check trough previously active chunks to see if this chunks is there. If it is, remove it from the list.
            previouslyActiveChunks.Remove(thisChunkCoord);

            // Next spiral coord
            spiralLoop.Next();
        }

        // Any chunks left in the previouslyActiveChunks list are no longer in the player's view distance, so loop trough and disable them.
        foreach (ChunkCoord c in previouslyActiveChunks)
        {
            chunks[c.x, c.z].isActive = false;
        }
    }

    public Vector3 GetHighestVoxel(Vector3 pos)
    {
        ChunkCoord thisChunk = new ChunkCoord(pos);
        int yMax = VoxelData.ChunkHeight - 1;

        // Voxel outside the world, highest voxel is world height.
        if (!worldData.IsVoxelInWorld(pos))
        {
            Debug.Log($"Voxel not in world for X / Y/ Z = {(int)pos.x} / {(int)pos.y} / {(int)pos.z}");
            return new Vector3(pos.x, yMax, pos.z);
        }

        // Chunk is created and editable, calculate highest voxel using chunk function.
        // TODO: Doesn't work anymore, rewrite using worldData / chunkData instead
        if (chunks[thisChunk.x, thisChunk.z] != null)
        {
            Debug.Log($"Finding highest voxel for chunk {thisChunk.x} / {thisChunk.z} in wold for X / Z = {(int)pos.x} / {(int)pos.z} using chunk function.");
            Chunk currentChunk = chunks[thisChunk.x, thisChunk.z];
            Vector3 voxelPositionInChunk = currentChunk.GetVoxelPositionInChunkFromGlobalVector3(pos);
            Vector3 highestVoxelPositionInChunk = currentChunk.GetHighestVoxel(voxelPositionInChunk);
            return new Vector3(pos.x, highestVoxelPositionInChunk.y, pos.z);
        }

        // Chunk is not created, calculate highest voxel using expensive world generation code.
        for (int i = yMax; i > 0; i--)
        {
            Vector3 currentVoxel = new Vector3(pos.x, i, pos.z);
            if (!blockTypes[GetVoxel(currentVoxel)].isSolid) continue;
            Debug.Log($"Finding highest voxel in wold for X / Z = {(int)pos.x} / {(int)pos.z} using expensive world generation code.");
            return currentVoxel;
        }

        // Fallback, highest voxel is world height
        Debug.Log($"No solid voxels found for X / Z = {(int)pos.x} / {(int)pos.z}");
        return new Vector3(pos.x, yMax, pos.z);
    }

    public bool CheckForVoxel(Vector3 pos)
    {
        VoxelState voxel = worldData.GetVoxel(pos);
        return voxel != null && blockTypes[voxel.id].isSolid;
    }

    public VoxelState GetVoxelState(Vector3 pos)
    {
        return worldData.GetVoxel(pos);
    }

    public bool inUI
    {
        get { return _inUI; }
        set
        {
            _inUI = value;
            if (_inUI)
            {
                Cursor.lockState = CursorLockMode.None; // Makes cursor visible
            }
            else
            {
                Cursor.lockState = CursorLockMode.Locked; // Makes cursor invisible and not able to go of screen
            }

            // Toggle UI based on inUI state
            Cursor.visible = _inUI;
            creativeInventoryWindow.SetActive(_inUI);
            cursorSlot.SetActive(_inUI);
        }
    }

    public byte GetVoxel(Vector3 pos)
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
            foreach (Lode lode in biome.Lodes)
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
            if (Noise.Get2DPerlin(new Vector2(pos.x, pos.z), 0, biome.majorFloraZoneScale) > biome.majorFloraZoneThreshold)
            {
                if (Noise.Get2DPerlin(new Vector2(pos.x, pos.z), 2500, biome.majorFloraPlacementScale) > biome.majorFloraPlacementThreshold)
                {
                    ConcurrentQueue<VoxelMod> structureQueue = Structure.GenerateMajorFlora(biome.majorFloraIndex, pos, biome.minHeight, biome.maxHeight);
                    modifications.Enqueue(structureQueue);
                }
            }
        }


        return voxelValue;
    }

    private bool IsChunkInWorld(ChunkCoord coord)
    {
        return coord.x is >= 0 and < VoxelData.WorldSizeInChunks &&
               coord.z is >= 0 and < VoxelData.WorldSizeInChunks;
    }
}

[System.Serializable]
public class BlockType
{
    public string blockName;
    public VoxelMeshData meshData;
    public bool isSolid;
    public bool renderNeighborFaces;
    public bool isWater;
    
    [Tooltip("How many light will be stopped by this block.")]
    [Range(0, 15)]
    public byte opacity = 15;

    public Sprite icon;
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
}

public class VoxelMod
{
    public Vector3 position;
    public byte id;

    public VoxelMod()
    {
        position = new Vector3();
        id = 0;
    }

    public VoxelMod(Vector3 _position, byte _id)
    {
        position = _position;
        id = _id;
    }
}


[System.Serializable]
public class Settings
{
    [Header("Game Data")]
    public string version = "0.0.01";


    [Header("Save System")]
    public bool loadSaveDataOnStartup = true;


    [Header("Performance")]
    public int loadDistance = 10;
    public int viewDistance = 5;

    [Tooltip("PERFORMANCE INTENSIVE - Prevent invisible blocks in case of cross chunk structures by re-rendering the modified chunks.")]
    // TODO: Needs to be re-implemented: https://github.com/A-Van-Gestel/Unity-Minecraft_Clone/commit/320d9710f620db537acb3ed8f94e5d98ec567f59#diff-47f56e730b0aac4f6699ec185244b6f897aacac5d53cc53bab0c19f20dda1c08L295-L307
    public bool rerenderChunksOnModification = true;
    
    [InitializationField]
    [Tooltip("PERFORMANCE INTENSIVE - Enable the lighting system, on large caves this can cause the game to hang for a couple of seconds.")]
    public bool enableLighting = true;

    [InitializationField]
    [Tooltip("Updates chunks on a separate thread. This however might negatively impact performance due to the extra overhead.")]
    public bool enableThreading = false;
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