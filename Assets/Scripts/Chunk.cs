using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

public class Chunk
{
    public ChunkCoord coord;

    private GameObject chunkObject;
    private MeshRenderer meshRenderer;
    private MeshFilter meshFilter;

    private int vertexIndex = 0;
    private List<Vector3> vertices = new List<Vector3>();
    private List<int> triangels = new List<int>();
    private List<int> transparantTriangles = new List<int>();
    private Material[] materials = new Material[2];
    private List<Vector2> uvs = new List<Vector2>();

    public byte[,,] voxelMap = new byte[VoxelData.ChunkWidth, VoxelData.ChunkHeight, VoxelData.ChunkWidth];

    private World world;

    public Queue<VoxelMod> modifications = new Queue<VoxelMod>();

    private bool _isActive;
    public bool isInitialized = false;
    public bool isVoxelMapPopulated = false;

    public Chunk(ChunkCoord _coord, World _world, bool generateOnLoad)
    {
        coord = _coord;
        world = _world;
        isActive = true;

        if (generateOnLoad)
        {
            Init();
        }
    }

    public void Init()
    {
        chunkObject = new GameObject();
        meshFilter = chunkObject.AddComponent<MeshFilter>();
        meshRenderer = chunkObject.AddComponent<MeshRenderer>();

        materials[0] = world.material;
        materials[1] = world.transparentMaterial;
        meshRenderer.materials = materials;
        
        meshRenderer.shadowCastingMode = ShadowCastingMode.TwoSided;  // Mostly fixes lines in the shadows between voxels.
        chunkObject.transform.SetParent(world.transform);
        chunkObject.transform.position = new Vector3(coord.x * VoxelData.ChunkWidth, 0f, coord.z * VoxelData.ChunkWidth);
        chunkObject.name = $"Chunk {coord.x}, {coord.z}";


        PopulateVoxelMap();
        UpdateChunk();
        
        isInitialized = true;
    }

    public void UpdateChunk()
    {
        while (modifications.Count > 0)
        {
            VoxelMod v = modifications.Dequeue();
            Vector3 pos = v.position -= chunkPosition;
            voxelMap[(int)pos.x, (int)pos.y, (int)pos.z] = v.id;
        }
        
        ClearMeshData();

        for (int y = 0; y < VoxelData.ChunkHeight; y++)
        {
            for (int x = 0; x < VoxelData.ChunkWidth; x++)
            {
                for (int z = 0; z < VoxelData.ChunkWidth; z++)
                {
                    if (world.blockTypes[voxelMap[x, y, z]].isSolid)
                        UpdateMeshData(new Vector3(x, y, z));
                }
            }
        }

        CreateMesh();
    }

    private void ClearMeshData()
    {
        vertexIndex = 0;
        vertices.Clear();
        triangels.Clear();
        transparantTriangles.Clear();
        uvs.Clear();
    }

    private void PopulateVoxelMap()
    {
        for (int y = 0; y < VoxelData.ChunkHeight; y++)
        {
            for (int x = 0; x < VoxelData.ChunkWidth; x++)
            {
                for (int z = 0; z < VoxelData.ChunkWidth; z++)
                {
                    voxelMap[x, y, z] = world.GetVoxel(new Vector3(x, y, z) + chunkPosition);
                }
            }
        }

        isVoxelMapPopulated = true;
    }

    public bool isActive
    {
        get { return _isActive; }
        set
        {
            _isActive = value;
            if (chunkObject != null)
            {
                chunkObject.SetActive(value);
            }
        }
    }

    public Vector3 chunkPosition
    {
        get { return chunkObject.transform.position; }
    }

    private bool IsVoxelInChunk(int x, int y, int z)
    {
        if (x >= 0 && x < VoxelData.ChunkWidth &&
            y >= 0 && y < VoxelData.ChunkHeight &&
            z >= 0 && z < VoxelData.ChunkWidth)
        {
            return true;
        }
        else
        {
            return false;
        }
    }

    public byte GetVoxelFromGlobalVector3(Vector3 pos)
    {
        int xCheck = Mathf.FloorToInt(pos.x);
        int yCheck = Mathf.FloorToInt(pos.y);
        int zCheck = Mathf.FloorToInt(pos.z);

        xCheck -= Mathf.FloorToInt(chunkPosition.x);
        zCheck -= Mathf.FloorToInt(chunkPosition.z);

        return voxelMap[xCheck, yCheck, zCheck];
    }

    private bool CheckIfVoxelTransparent(Vector3 pos)
    {
        int x = Mathf.FloorToInt(pos.x);
        int y = Mathf.FloorToInt(pos.y);
        int z = Mathf.FloorToInt(pos.z);

        if (!IsVoxelInChunk(x, y, z))
        {
            return world.CheckIfVoxelTransparent(pos + chunkPosition);
        }

        return world.blockTypes[voxelMap[x, y, z]].isTransparent;
    }

    public void EditVoxel(Vector3 pos, byte newID)
    {
        int xCheck = Mathf.FloorToInt(pos.x);
        int yCheck = Mathf.FloorToInt(pos.y);
        int zCheck = Mathf.FloorToInt(pos.z);

        xCheck -= Mathf.FloorToInt(chunkPosition.x);
        zCheck -= Mathf.FloorToInt(chunkPosition.z);

        voxelMap[xCheck, yCheck, zCheck] = newID;
        
        // Update Surrounding Chunks
        UpdateSurroundingVoxels(xCheck, yCheck, zCheck);
        UpdateChunk();
    }

    private void UpdateSurroundingVoxels(int x, int y, int z)
    {
        Vector3 thisVoxel = new Vector3(x, y, z);

        for (int p = 0; p < 6; p++) // p = faceIndex
        {
            Vector3 currentVoxel = thisVoxel + VoxelData.FaceChecks[p];
            
            // relative position of the voxel within chunk
            int currentVoxelX = (int)currentVoxel.x;
            int currentVoxelY = (int)currentVoxel.y;
            int currentVoxelZ = (int)currentVoxel.z;
            
            // absolute position of the voxel (because the UpdateSurroundingVoxels() uses the relative position of a voxel within the chunk, not the global voxel position):
            int currentVoxelWorldPosX = currentVoxelX + (int)chunkPosition.x;
            int currentVoxelWorldPosZ = currentVoxelZ + (int)chunkPosition.z;
            
            // If surrounding voxel is outside of current chunk and still in the world, update that chunk as well
            if (!IsVoxelInChunk(currentVoxelX, currentVoxelY, currentVoxelZ)
                && world.IsVoxelInWorld(new Vector3(currentVoxelWorldPosX, currentVoxelY, currentVoxelWorldPosZ)))
            { 
                world.GetChunkFromVector3(currentVoxel + chunkPosition).UpdateChunk();
            }
        }
    }

    private void UpdateMeshData(Vector3 pos)
    {
        byte blockID = voxelMap[(int)pos.x, (int)pos.y, (int)pos.z];
        bool isTransparent = world.blockTypes[blockID].isTransparent;
        Vector3 voxelWoldPosition = pos + chunkPosition;
        
        for (int p = 0; p < 6; p++) // p = faceIndex
        {
            if (CheckIfVoxelTransparent(pos + VoxelData.FaceChecks[p]) ||  // Display face if facing transparent voxel
                !world.IsVoxelInWorld(voxelWoldPosition + VoxelData.FaceChecks[p]))  // Display face if facing the edge of the world
            {
                vertices.Add(pos + VoxelData.VoxelVerts[VoxelData.VoxelTris[p, 0]]);
                vertices.Add(pos + VoxelData.VoxelVerts[VoxelData.VoxelTris[p, 1]]);
                vertices.Add(pos + VoxelData.VoxelVerts[VoxelData.VoxelTris[p, 2]]);
                vertices.Add(pos + VoxelData.VoxelVerts[VoxelData.VoxelTris[p, 3]]);

                AddTexture(world.blockTypes[blockID].GetTextureID(p));
                
                if (!isTransparent)
                {
                    triangels.Add(vertexIndex);
                    triangels.Add(vertexIndex + 1);
                    triangels.Add(vertexIndex + 2);
                    triangels.Add(vertexIndex + 2);
                    triangels.Add(vertexIndex + 1);
                    triangels.Add(vertexIndex + 3);
                }
                else
                {
                    transparantTriangles.Add(vertexIndex);
                    transparantTriangles.Add(vertexIndex + 1);
                    transparantTriangles.Add(vertexIndex + 2);
                    transparantTriangles.Add(vertexIndex + 2);
                    transparantTriangles.Add(vertexIndex + 1);
                    transparantTriangles.Add(vertexIndex + 3);
                }
                vertexIndex += 4;
            }
        }
    }

    private void CreateMesh()
    {
        Mesh mesh = new Mesh();
        mesh.vertices = vertices.ToArray();

        mesh.subMeshCount = 2;
        mesh.SetTriangles(triangels.ToArray(), 0);
        mesh.SetTriangles(transparantTriangles.ToArray(), 1);
        
        mesh.uv = uvs.ToArray();

        mesh.RecalculateNormals();

        meshFilter.mesh = mesh;
    }

    private void AddTexture(int textureID)
    {
        float y = Mathf.FloorToInt((float)textureID / VoxelData.TextureAtlasSizeInBlocks);
        float x = textureID - (y * VoxelData.TextureAtlasSizeInBlocks);

        x *= VoxelData.NormalizedBlockTextureSize;
        y *= VoxelData.NormalizedBlockTextureSize;

        // To start reading the atlas from the top left
        y = 1f - y - VoxelData.NormalizedBlockTextureSize;

        uvs.Add(new Vector2(x, y));
        uvs.Add(new Vector2(x, y + VoxelData.NormalizedBlockTextureSize));
        uvs.Add(new Vector2(x + VoxelData.NormalizedBlockTextureSize, y));
        uvs.Add(new Vector2(x + VoxelData.NormalizedBlockTextureSize, y + VoxelData.NormalizedBlockTextureSize));
    }
}

public class ChunkCoord
{
    public int x;
    public int z;

    public ChunkCoord()
    {
        x = 0;
        z = 0;
    }

    public ChunkCoord(int _x, int _z)
    {
        x = _x;
        z = _z;
    }

    public ChunkCoord(Vector3 pos)
    {
        int xCheck = Mathf.FloorToInt(pos.x);
        int zCheck = Mathf.FloorToInt(pos.z);

        x = xCheck / VoxelData.ChunkWidth;
        z = zCheck / VoxelData.ChunkWidth;
    }

    public bool Equals(ChunkCoord other)
    {
        if (other == null)
            return false;
        else if (other.x == x && other.z == z)
            return true;
        else
            return false;
    }
}