using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using MyBox;
using UnityEngine;
using UnityEngine.Serialization;
using Random = UnityEngine.Random;

public class World : MonoBehaviour
{
    [Header("World Generation Values")]
    public int seed;
    public BiomeAttributes biome;

    [Header("Performance")]
    public bool enableThreading;

    [Header("Lighting")]
    [Range(0f, 1f)]
    [Tooltip("Lower value equals darker light level.")]
    public float globalLightLevel;
    public Color day;
    public Color night;

    [Header("Player")]
    public Transform player;
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
    private List<Chunk> chunksToUpdate = new List<Chunk>();
    public ConcurrentQueue<Chunk> chunksToDraw = new ConcurrentQueue<Chunk>();

    private bool applyingModifications = false;

    private ConcurrentQueue<ConcurrentQueue<VoxelMod>> modifications = new ConcurrentQueue<ConcurrentQueue<VoxelMod>>();

    private bool _inUI = false;

    [Header("UI")]
    public GameObject debugScreen;
    public GameObject creativeInventoryWindow;
    public GameObject cursorSlot;
    
    // Shader Properties
    private static readonly int ShaderGlobalLightLevel = Shader.PropertyToID("GlobalLightLevel");
    private static readonly int ShaderMinGlobalLightLevel = Shader.PropertyToID("minGlobalLightLevel");
    private static readonly int ShaderMaxGlobalLightLevel = Shader.PropertyToID("maxGlobalLightLevel");

    private void Start()
    {
        Random.InitState(seed);
        
        Shader.SetGlobalFloat(ShaderMinGlobalLightLevel, VoxelData.minLightLevel);
        Shader.SetGlobalFloat(ShaderMaxGlobalLightLevel, VoxelData.maxLightLevel);

        spawnPosition = new Vector3(VoxelData.WorldSizeInVoxels / 2f, VoxelData.ChunkHeight - 1f, VoxelData.WorldSizeInVoxels / 2f);
        GenerateWorld();
        playerLastChunkCoord = GetChunkCoordFromVector3(player.position);
    }

    private void Update()
    {
        playerChunkCoord = GetChunkCoordFromVector3(player.position);
        
        Shader.SetGlobalFloat(ShaderGlobalLightLevel, globalLightLevel);
        Camera.main!.backgroundColor = Color.Lerp(night, day, globalLightLevel);

        // Only update the chunks if the player has moved from the chunk they where previously on.
        if (!playerChunkCoord.Equals(playerLastChunkCoord))
            CheckViewDistance();

        if (!applyingModifications)
            ApplyModifications();

        if (chunksToCreate.Count > 0)
            CreateChunk();

        if (chunksToUpdate.Count > 0)
            UpdateChunks();

        lock (chunksToDraw)
        {
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
        }


        if (Input.GetKeyDown(KeyCode.F3))
            debugScreen.SetActive(!debugScreen.activeSelf);
    }

    private void GenerateWorld()
    {
        for (int x = VoxelData.WorldSizeInChunks / 2 - VoxelData.ViewDistanceInChunks; x < VoxelData.WorldSizeInChunks / 2 + VoxelData.ViewDistanceInChunks; x++)
        {
            for (int z = VoxelData.WorldSizeInChunks / 2 - VoxelData.ViewDistanceInChunks; z < VoxelData.WorldSizeInChunks / 2 + VoxelData.ViewDistanceInChunks; z++)
            {
                chunks[x, z] = new Chunk(new ChunkCoord(x, z), this, true);
                activeChunks.Add(new ChunkCoord(x, z));
            }
        }

        spawnPosition = GetHighestVoxel(spawnPosition) + new Vector3(0.5f, 1.1f, 0.5f);
        player.position = spawnPosition;
    }

    void CreateChunk()
    {
        ChunkCoord c = chunksToCreate[0];
        chunksToCreate.RemoveAt(0);
        activeChunks.Add(c);
        chunks[c.x, c.z].Init();
    }

    void UpdateChunks()
    {
        bool updated = false;
        int index = 0;

        while (!updated && index < chunksToUpdate.Count - 1)
        {
            if (chunksToUpdate[index].IsEditable)
            {
                chunksToUpdate[index].UpdateChunk();
                chunksToUpdate.RemoveAt(index);
                updated = true;
            }
            else
            {
                index++;
            }
        }
    }

    private void ApplyModifications()
    {
        applyingModifications = true;

        while (modifications.Count > 0)
        {
            // Try getting the queue, if not successful retry later
            if (!modifications.TryDequeue(out ConcurrentQueue<VoxelMod> queue)) continue;

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
                            chunks[c.x, c.z] = new Chunk(c, this, true);
                            activeChunks.Add(c);
                        }

                        chunks[c.x, c.z].modifications.Enqueue(v);

                        if (!chunksToUpdate.Contains(chunks[c.x, c.z]))
                        {
                            chunksToUpdate.Add(chunks[c.x, c.z]);
                        }
                    }
                    else
                    {
                        Debug.Log($"World.ApplyModifications | ChunkCoord outside of world: X / Z = {c.x} / {c.z}");
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

        List<ChunkCoord> previouslyActiveChunks = new List<ChunkCoord>(activeChunks);

        // Loop trough all chunks currently within view distance of the player.
        for (int x = coord.x - VoxelData.ViewDistanceInChunks; x < coord.x + VoxelData.ViewDistanceInChunks; x++)
        {
            for (int z = coord.z - VoxelData.ViewDistanceInChunks; z < coord.z + VoxelData.ViewDistanceInChunks; z++)
            {
                // If the current chunk is in the world...
                if (IsChunkInWorld(new ChunkCoord(x, z)))
                {
                    // Check if it is active, if not, activate it.
                    if (chunks[x, z] == null)
                    {
                        chunks[x, z] = new Chunk(new ChunkCoord(x, z), this, false);
                        chunksToCreate.Add(new ChunkCoord(x, z));
                    }
                    else if (!chunks[x, z].isActive)
                    {
                        chunks[x, z].isActive = true;
                    }

                    activeChunks.Add(new ChunkCoord(x, z));
                }

                // Check trough previously active chunks to see if this chunks is there. If it is, remove it from the list.
                for (int i = 0; i < previouslyActiveChunks.Count; i++)
                {
                    if (previouslyActiveChunks[i].Equals(new ChunkCoord(x, z)))
                    {
                        previouslyActiveChunks.RemoveAt(i);
                    }
                }
            }
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
        get
        {
            return _inUI;
        }
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