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
using Helpers;

public class World : MonoBehaviour
{
    public Settings settings;
    private string settingFilePath = Application.dataPath + "/settings.json";

    [Header("World Generation Values")]
    public BiomeAttributes biome;

    [Header("Lighting")]
    [Range(0f, 1f)]
    [Tooltip("Lower value equals darker light level.")]
    public float globalLightLevel;

    public Color day;
    public Color night;

    [Header("Player")]
    public Transform player;
    private Camera playerCamera;

    public Vector3 spawnPosition;

    [Header("Blocks & Materials")]
    public Material material;

    public Material transparentMaterial;
    public BlockType[] blockTypes;

    private Chunk[,] chunks = new Chunk[VoxelData.WorldSizeInChunks, VoxelData.WorldSizeInChunks];

    private List<ChunkCoord> activeChunks = new List<ChunkCoord>();
    public ChunkCoord playerChunkCoord;
    private ChunkCoord playerLastChunkCoord;

    private List<ChunkCoord> chunksToCreate = new List<ChunkCoord>();
    public List<Chunk> chunksToUpdate = new List<Chunk>();
    public ConcurrentQueue<Chunk> chunksToDraw = new ConcurrentQueue<Chunk>();

    private bool applyingModifications = false;

    private ConcurrentQueue<ConcurrentQueue<VoxelMod>> modifications = new ConcurrentQueue<ConcurrentQueue<VoxelMod>>();

    // UI
    [Header("UI")]
    public GameObject debugScreen;

    public GameObject creativeInventoryWindow;
    public GameObject cursorSlot;
    private bool _inUI = false;

    // Shader Properties
    private static readonly int ShaderGlobalLightLevel = Shader.PropertyToID("GlobalLightLevel");
    private static readonly int ShaderMinGlobalLightLevel = Shader.PropertyToID("minGlobalLightLevel");
    private static readonly int ShaderMaxGlobalLightLevel = Shader.PropertyToID("maxGlobalLightLevel");

    // Threading
    private Thread ChunkUpdateThread;
    public object ChunkUpdateThreadLock = new object();

    private void Start()
    {
        // Get main camera.
        playerCamera = Camera.main!;

        // Create settings file if it doesn't yet exist, after that, load it.
        if (!File.Exists(settingFilePath))
        {
            string jsonExport = JsonUtility.ToJson(settings, true);
            File.WriteAllText(settingFilePath, jsonExport);
        }

        string jsonImport = File.ReadAllText(settingFilePath);
        settings = JsonUtility.FromJson<Settings>(jsonImport);

        Random.InitState(settings.seed);

        Shader.SetGlobalFloat(ShaderMinGlobalLightLevel, VoxelData.minLightLevel);
        Shader.SetGlobalFloat(ShaderMaxGlobalLightLevel, VoxelData.maxLightLevel);
        SetGlobalLightValue();

        if (settings.enableThreading)
        {
            ChunkUpdateThread = new Thread(new ThreadStart(ThreadedUpdate));
            ChunkUpdateThread.Start();
        }

        spawnPosition = new Vector3(VoxelData.WorldSizeInVoxels / 2f, VoxelData.ChunkHeight - 1f, VoxelData.WorldSizeInVoxels / 2f);
        GenerateWorld();
        playerLastChunkCoord = GetChunkCoordFromVector3(player.position);
    }

    public void SetGlobalLightValue()
    {
        Shader.SetGlobalFloat(ShaderGlobalLightLevel, globalLightLevel);
        playerCamera.backgroundColor = Color.Lerp(night, day, globalLightLevel);
    }

    private void Update()
    {
        playerChunkCoord = GetChunkCoordFromVector3(player.position);

        // Only update the chunks if the player has moved from the chunk they where previously on.
        if (!playerChunkCoord.Equals(playerLastChunkCoord))
            CheckViewDistance();

        if (!applyingModifications)
            ApplyModifications();

        if (chunksToCreate.Count > 0)
            CreateChunk();

        if (chunksToUpdate.Count > 0)
            UpdateChunks();

        if (chunksToDraw.Count > 0)
        {
            if (chunksToDraw.TryPeek(out Chunk chunk) && chunk.IsEditable && chunksToDraw.TryDequeue(out chunk))
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

        if (!settings.enableThreading)
        {
            if (!applyingModifications)
                ApplyModifications();

            if (chunksToUpdate.Count > 0)
                UpdateChunks();
        }


        if (Input.GetKeyDown(KeyCode.F3))
            debugScreen.SetActive(!debugScreen.activeSelf);
    }

    private void GenerateWorld()
    {
        ChunkCoord centerChunkCoord = GetChunkCoordFromVector3(spawnPosition);
        SpiralLoop spiralLoop = new SpiralLoop();

        ChunkCoord newChunk = centerChunkCoord;
        while (newChunk.x < VoxelData.WorldSizeInChunks / 2 + settings.viewDistance && newChunk.z < VoxelData.WorldSizeInChunks / 2 + settings.viewDistance)
        {
            newChunk = new ChunkCoord(centerChunkCoord.x + spiralLoop.X, centerChunkCoord.z + spiralLoop.Z);

            chunks[newChunk.x, newChunk.z] = new Chunk(newChunk, this);
            chunksToCreate.Add(newChunk);

            // Next spiral coord
            spiralLoop.Next();
        }

        spawnPosition = GetHighestVoxel(spawnPosition) + new Vector3(0.5f, 1.1f, 0.5f);
        player.position = spawnPosition;
        CheckViewDistance();
    }

    private void CreateChunk()
    {
        ChunkCoord c = chunksToCreate[0];
        chunksToCreate.RemoveAt(0);
        chunks[c.x, c.z].Init();
    }

    private void UpdateChunks()
    {
        bool updated = false;
        int index = 0;

        lock (ChunkUpdateThreadLock)
        {
            while (!updated && index < chunksToUpdate.Count - 1)
            {
                Chunk chunkToUpdate = chunksToUpdate[index];
                if (chunkToUpdate.IsEditable)
                {
                    chunkToUpdate.UpdateChunk();
                    if (!activeChunks.Contains(chunkToUpdate.coord))
                    {
                        activeChunks.Add(chunkToUpdate.coord);
                    }

                    chunksToUpdate.RemoveAt(index);
                    updated = true;
                }
                else
                {
                    index++;
                }
            }
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

            if (chunksToUpdate.Count > 0)
                UpdateChunks();
        }
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

            // Cache chunks modified by the current modification.
            List<Chunk> modificationModifiedChunks = new List<Chunk>();

            try
            {
                while (queue.Count > 0)
                {
                    // Try getting the voxelMod, if not successful retry later
                    if (!queue.TryDequeue(out VoxelMod v)) continue;
                    ChunkCoord c = GetChunkCoordFromVector3(v.position);

                    // Only try to apply modifications if these modifications are inside the world
                    if (c.x >= 0 && c.x < VoxelData.WorldSizeInChunks && c.z >= 0 && c.z < VoxelData.WorldSizeInChunks)
                    {
                        if (chunks[c.x, c.z] == null)
                        {
                            chunks[c.x, c.z] = new Chunk(c, this);
                            chunksToCreate.Add(c);
                        }

                        Chunk chunk = chunks[c.x, c.z];
                        chunk.modifications.Enqueue(v);

                        if (!modificationModifiedChunks.Contains(chunk))
                            modificationModifiedChunks.Add(chunk);
                    }
                    else
                    {
                        Debug.Log($"World.ApplyModifications | ChunkCoord outside of world: X / Z = {c.x} / {c.z}");
                    }
                }

                // Rerender the chunks modified by the modification.
                foreach (Chunk chunk in modificationModifiedChunks)
                {
                    // TODO: Needed for neighboring chunks to update rerender their updated mesh, but will result in serious lag spikes due to the long thread lock overhead.
                    lock (ChunkUpdateThreadLock)
                    {
                        if (!chunksToUpdate.Contains(chunk))
                            chunksToUpdate.Add(chunk);
                    }
                }
            }
            catch (NullReferenceException e)
            {
                Debug.Log("NullReference Exception again: " + e);
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

    private void CheckViewDistance()
    {
        ChunkCoord coord = GetChunkCoordFromVector3(player.position);
        playerLastChunkCoord = playerChunkCoord;

        // Copy currently active chunks.
        List<ChunkCoord> previouslyActiveChunks = new List<ChunkCoord>(activeChunks);

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
                if (chunks[x, z] == null)
                {
                    chunks[x, z] = new Chunk(thisChunkCoord, this);
                    chunksToCreate.Add(thisChunkCoord);
                }
                else if (!chunks[x, z].isActive)
                {
                    chunks[x, z].isActive = true;
                }

                activeChunks.Add(thisChunkCoord);
            }

            // Check trough previously active chunks to see if this chunks is there. If it is, remove it from the list.
            for (int i = 0; i < previouslyActiveChunks.Count; i++)
            {
                if (previouslyActiveChunks[i].Equals(thisChunkCoord))
                {
                    previouslyActiveChunks.RemoveAt(i);
                }
            }

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
        if (!IsVoxelInWorld(pos))
        {
            Debug.Log($"Voxel not in world for X / Y/ Z = {(int)pos.x} / {(int)pos.y} / {(int)pos.z}");
            return new Vector3(pos.x, yMax, pos.z);
        }

        // Chunk is created and editable, calculate highest voxel using chunk function.
        if (chunks[thisChunk.x, thisChunk.z] != null && chunks[thisChunk.x, thisChunk.z].IsEditable)
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
        ChunkCoord thisChunk = new ChunkCoord(pos);

        if (!IsVoxelInWorld(pos))
            return false;

        if (chunks[thisChunk.x, thisChunk.z] != null && chunks[thisChunk.x, thisChunk.z].IsEditable)
            return blockTypes[chunks[thisChunk.x, thisChunk.z].GetVoxelFromGlobalVector3(pos).id].isSolid;

        return blockTypes[GetVoxel(pos)].isSolid;
    }

    public VoxelState GetVoxelState(Vector3 pos)
    {
        ChunkCoord thisChunk = new ChunkCoord(pos);

        if (!IsVoxelInWorld(pos))
            return null;

        if (chunks[thisChunk.x, thisChunk.z] != null && chunks[thisChunk.x, thisChunk.z].IsEditable)
            return chunks[thisChunk.x, thisChunk.z].GetVoxelFromGlobalVector3(pos);

        return new VoxelState(GetVoxel(pos));
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
        int xPos = Mathf.FloorToInt(pos.x);
        int yPos = Mathf.FloorToInt(pos.y);
        int zPos = Mathf.FloorToInt(pos.z);

        // ----- IMMUTABLE PASS -----
        // If outside of world, return air.
        if (!IsVoxelInWorld(pos))
            return 0;

        // If bottom block of chunk, return bedrock
        if (yPos == 0)
            return 8; // Bedrock

        // ----- BASIC TERRAIN PASS -----
        int terrainHeight = Mathf.FloorToInt(biome.terrainHeight * Noise.Get2DPerlin(new Vector2(pos.x, pos.z), 0, biome.terrainScale)) + biome.solidGroundHeight;
        byte voxelValue = 0;

        if (yPos == terrainHeight)
        {
            voxelValue = 2; // Grass
        }
        else if (yPos < terrainHeight && yPos > terrainHeight - 4)
        {
            voxelValue = 3; // Dirt
        }
        else if (yPos > terrainHeight)
        {
            return 0; // Air
        }
        else
        {
            voxelValue = 1; // Stone
        }

        // ----- SECOND PASS -----
        if (voxelValue == 1) // Stone
        {
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

        // ----- TREE PASS -----
        if (yPos == terrainHeight)
        {
            if (Noise.Get2DPerlin(new Vector2(pos.x, pos.z), 0, biome.treeZoneScale) > biome.treeZoneThreshold)
            {
                if (Noise.Get2DPerlin(new Vector2(pos.x, pos.z), 2500, biome.treePlacementScale) > biome.treePlacementThreshold)
                {
                    ConcurrentQueue<VoxelMod> structureQueue = Structure.MakeTree(pos, biome.minTreeHeight, biome.maxTreeHeight);
                    modifications.Enqueue(structureQueue);
                }
            }
        }


        return voxelValue;
    }

    private bool IsChunkInWorld(ChunkCoord coord)
    {
        if (coord.x > 0 && coord.x < VoxelData.WorldSizeInChunks - 1 &&
            coord.z > 0 && coord.z < VoxelData.WorldSizeInChunks - 1)
        {
            return true;
        }
        else
        {
            return false;
        }
    }

    public bool IsVoxelInWorld(Vector3 pos)
    {
        if (pos.x >= 0 && pos.x < VoxelData.WorldSizeInVoxels &&
            pos.y >= 0 && pos.y < VoxelData.ChunkHeight &&
            pos.z >= 0 && pos.z < VoxelData.WorldSizeInVoxels)
        {
            return true;
        }
        else
        {
            return false;
        }
    }
}

[System.Serializable]
public class BlockType
{
    [HideInInspector]
    public string blockName;

    public bool isSolid;
    public bool renderNeighborFaces;

    [ConditionalField(nameof(renderNeighborFaces))]
    public float transparency;

    public Sprite icon;
    public int stackSize = 64;

    [Header("Texture Values")] public int backFaceTexture;
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
    public string version = "0.0.0";

    [Header("Performance")]
    public int viewDistance = 5;

    [InitializationField]
    public bool enableThreading = true;

    [Header("Controls")]
    [Range(0.1f, 10f)]
    public float mouseSensitivityX = 1f;

    [Range(0.1f, 10f)]
    public float mouseSensitivityY = 1f;

    [Header("World Generation")]
    public int seed = 2147483647;
}