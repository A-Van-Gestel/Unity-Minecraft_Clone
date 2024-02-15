using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

public class World : MonoBehaviour
{
    public int seed;
    public BiomeAttributes biome;

    public Transform player;
    public Vector3 spawnPosition;

    public Material material;
    public BlockType[] blockTypes;

    private Chunk[,] chunks = new Chunk[VoxelData.WorldSizeInChunks, VoxelData.WorldSizeInChunks];

    private List<ChunkCoord> activeChunks = new List<ChunkCoord>();
    public ChunkCoord playerChunkCoord;
    private ChunkCoord playerLastChunkCoord;

    private List<ChunkCoord> chunksToCreate = new List<ChunkCoord>();
    private bool isCreatingChunks;

    public GameObject debugScreen;

    private void Start()
    {
        Random.InitState(seed);

        spawnPosition = new Vector3(VoxelData.WorldSizeInVoxels / 2f, VoxelData.ChunkHeight - 50f, VoxelData.WorldSizeInVoxels / 2f);
        GenerateWorld();
        playerLastChunkCoord = GetChunkCoordFromVector3(spawnPosition);
    }

    private void Update()
    {
        playerChunkCoord = GetChunkCoordFromVector3(player.position);
        // Only update the chunks if the player has moved from the chunk they where previously on.
        if (!playerChunkCoord.Equels(playerLastChunkCoord))
        {
            CheckViewDistance();
        }

        if (chunksToCreate.Count > 0 && !isCreatingChunks)
        {
            StartCoroutine(nameof(CreateChunks));
        }

        if (Input.GetKeyDown(KeyCode.F3))
        {
            debugScreen.SetActive(!debugScreen.activeSelf);
        }
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

        player.position = spawnPosition;
    }

    private IEnumerator CreateChunks()
    {
        isCreatingChunks = true;

        while (chunksToCreate.Count > 0)
        {
            chunks[chunksToCreate[0].x, chunksToCreate[0].z].Init();
            chunksToCreate.RemoveAt(0);
            yield return null;
        }

        isCreatingChunks = false;
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
                    if (previouslyActiveChunks[i].Equels(new ChunkCoord(x, z)))
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

    public bool CheckForVoxel(Vector3 pos)
    {
        ChunkCoord thisChunk = new ChunkCoord(pos);

        if (!IsVoxelInWorld(pos))
            return false;

        if (chunks[thisChunk.x, thisChunk.z] != null && chunks[thisChunk.x, thisChunk.z].isVoxelMapPopulated)
            return blockTypes[chunks[thisChunk.x, thisChunk.z].GetVoxelFromGlobalVector3(pos)].isSolid;

        return blockTypes[GetVoxel(pos)].isSolid;
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

    private bool IsVoxelInWorld(Vector3 pos)
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
    public string blockName;
    public bool isSolid;

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