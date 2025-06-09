using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Data;
using Helpers;
using MyBox;
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
    public ChunkCoord playerChunkCoord;
    private ChunkCoord playerLastChunkCoord;

    private List<Chunk> chunksToUpdate = new List<Chunk>();
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
    [ReadOnly]
    public string appSaveDataPath;

    // Shader Properties
    private static readonly int ShaderGlobalLightLevel = Shader.PropertyToID("GlobalLightLevel");
    private static readonly int ShaderMinGlobalLightLevel = Shader.PropertyToID("minGlobalLightLevel");
    private static readonly int ShaderMaxGlobalLightLevel = Shader.PropertyToID("maxGlobalLightLevel");

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
#if UNITY_EDITOR
            AssetDatabase.Refresh(); // Refresh Unity's asset database.
# endif
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
        spawnPosition = new Vector3Int(VoxelData.WorldCentre, VoxelData.ChunkHeight - 1, VoxelData.WorldCentre);

        // Initialize clouds
        clouds?.Initialize();

        // Initialize world
        LoadWorld();

        // Apply any modifications that were generated during the LoadWorld pass (e.g. structures like trees).
        // This is critical for getting the correct spawn height, as GetHighestVoxel needs to check the final, modified chunk data.
        ApplyModifications();

        // Now set the Y position on top of the highest voxel at the initial location.
        spawnPosition = GetHighestVoxel(spawnPosition) + spawnPositionOffset;
        playerTransform.position = spawnPosition;
        CheckViewDistance();


        playerLastChunkCoord = GetChunkCoordFromVector3(playerTransform.position);

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

        // Only update the chunks if the player has moved from the chunk they were previously on.
        if (!playerChunkCoord.Equals(playerLastChunkCoord))
        {
            // CheckLoadDistance ensures the ChunkData is created/loaded from disk.
            CheckLoadDistance();
            // CheckViewDistance creates the Chunk GameObjects and makes them active.
            CheckViewDistance();
        }

        // After CheckViewDistance has run, process any new chunks that need meshes.
        if (chunksToBuildMesh.Count > 0)
        {
            foreach (Chunk chunk in chunksToBuildMesh)
            {
                // By the time this runs, all neighboring chunks that are also in view
                // will have had their ChunkData loaded by CheckLoadDistance.
                AddChunkToUpdate(chunk);
            }

            chunksToBuildMesh.Clear();
        }

        if (!applyingModifications)
            ApplyModifications();

        if (chunksToUpdate.Count > 0)
            UpdateChunks();

        if (chunksToDraw.Count > 0)
        {
            Chunk chunk = chunksToDraw.Dequeue();
            try
            {
                chunk.CreateMesh();
            }
            catch (Exception e)
            {
                Debug.Log("Chunk MeshCreation Exception: " + e);
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

        // Don't try to load chunks outside the world
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
        if (chunk.coord.x == 0 && chunk.coord.z == 0)
            Debug.Log($"Adding chunk (0, 0) to update queue. Immediate: {immediateUpdate}");

        // Make sure update list doesn't already contain the chunk.
        if (!chunksToUpdate.Contains(chunk))
        {
            if (immediateUpdate)
                chunksToUpdate.Insert(0, chunk);
            else
                chunksToUpdate.Add(chunk);
        }
    }

    private void UpdateChunks()
    {
        Chunk chunkToUpdate = chunksToUpdate[0];
        chunkToUpdate.UpdateChunk();
        activeChunks.Add(chunkToUpdate.coord);

        chunksToUpdate.RemoveAt(0);
    }

    private void ApplyModifications()
    {
        applyingModifications = true;

        while (modifications.Count > 0)
        {
            Queue<VoxelMod> queue = modifications.Dequeue();

            while (queue.Count > 0)
            {
                VoxelMod v = queue.Dequeue();
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

        if (x == 0 && z == 0)
            Debug.Log($"Getting chunk for position: {pos}, Chunk: {x}, {z}");

        return chunks[x, z];
    }

    private void CheckLoadDistance()
    {
        ChunkCoord coord = GetChunkCoordFromVector3(playerTransform.position);
        playerLastChunkCoord = playerChunkCoord;

        // Loop through all chunks currently within view distance of the player.
        SpiralLoop spiralLoop = new SpiralLoop();
        ChunkCoord thisChunkCoord = coord;

        Debug.Log($"settings v: {settings.viewDistance} | l: {settings.loadDistance}");

        while (thisChunkCoord.x < coord.x + settings.loadDistance && thisChunkCoord.z < coord.z + settings.loadDistance)
        {
            thisChunkCoord = new ChunkCoord(coord.x + spiralLoop.X, coord.z + spiralLoop.Z);
            int x = thisChunkCoord.x;
            int z = thisChunkCoord.z;

            // If the current chunk is in the world...
            if (IsChunkInWorld(thisChunkCoord))
            {
                worldData.LoadChunk(new Vector2Int(x * VoxelData.ChunkWidth, z * VoxelData.ChunkWidth));
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

        // Loop through all chunks currently within view distance of the player.
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
                // If the chunk is being created for the first time...
                if (chunks[x, z] == null)
                {
                    chunks[x, z] = new Chunk(thisChunkCoord);
                    // Add it to our special list to build its mesh later in Update().
                    chunksToBuildMesh.Add(chunks[x, z]);
                }

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

    /// <summary>
    /// Finds the Y-coordinate of the highest solid voxel at a given X/Z position.
    /// It fist tries to get it from the actual chunk data, this respects voxel modifications (like trees),
    /// if that fails, it will use the expensive world generation code, this however doesn't respect voxel modifications.
    /// Should even that fail, it will return the world height.
    /// </summary>
    /// <param name="pos">The world position to check.</param>
    /// <returns>A Vector3 with the original X/Z and the new Y of the highest solid block.</returns>
    public Vector3Int GetHighestVoxel(Vector3 pos)
    {
        int x = Mathf.FloorToInt(pos.x);
        int y = Mathf.FloorToInt(pos.y);
        int z = Mathf.FloorToInt(pos.z);

        const int yMax = VoxelData.ChunkHeight - 1;
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
            Debug.Log($"Finding highest voxel for chunk {thisChunk.x} / {thisChunk.z} in wold for X / Z = {x} / {z} using chunk function.");

            // Get the (highest) local voxel position within the chunk.
            Vector3Int localPos = worldData.GetLocalVoxelPositionInChunk(pos);
            Vector3Int highestVoxelLocal = chunkData.GetHighestVoxel(localPos);

            // Get the world position of the highest voxel.
            Vector3Int highestVoxel = new Vector3Int(x, highestVoxelLocal.y, z);
            Debug.Log($"Highest voxel in chunk {thisChunk.x} / {thisChunk.z} is {highestVoxel}.");
            return highestVoxel;
        }

        // Chunk is not created, calculate the highest voxel using expensive world generation code.
        // NOTE: This will not include voxel modifications (like trees).
        for (int i = yMax; i > 0; i--)
        {
            Vector3Int currentVoxel = new Vector3Int(x, i, z);
            if (!blockTypes[GetVoxel(currentVoxel)].isSolid) continue;
            Debug.Log($"Finding highest voxel in wold for X / Z = {x} / {z} using expensive world generation code.");
            Debug.Log($"Highest voxel in chunk {thisChunk.x} / {thisChunk.z} is {currentVoxel}.");
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
            if (Noise.Get2DPerlin(new Vector2(pos.x, pos.z), 0, biome.majorFloraZoneScale) > biome.majorFloraZoneThreshold)
            {
                if (Noise.Get2DPerlin(new Vector2(pos.x, pos.z), 2500, biome.majorFloraPlacementScale) > biome.majorFloraPlacementThreshold)
                {
                    Queue<VoxelMod> structureQueue = Structure.GenerateMajorFlora(biome.majorFloraIndex, pos, biome.minHeight, biome.maxHeight);
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

    #region Debug Information Methods

    public int GetActiveChunksCount()
    {
        return activeChunks.Count;
    }

    public int GetChunksToBuildMeshCount()
    {
        return chunksToBuildMesh.Count;
    }

    public int GetChunksToUpdateCount()
    {
        return chunksToUpdate.Count;
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


[Serializable]
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