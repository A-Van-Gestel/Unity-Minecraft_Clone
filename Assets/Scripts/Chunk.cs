using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Data;
using MyBox;
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
    private List<int> triangles = new List<int>();
    private List<int> transparantTriangles = new List<int>();
    private Material[] materials = new Material[2];
    private List<Vector2> uvs = new List<Vector2>();
    private List<Color> colors = new List<Color>();
    private List<Vector3> normals = new List<Vector3>();

    public Vector3 chunkPosition;

    private bool _isActive;

    private ChunkData chunkData;

    public Chunk(ChunkCoord _coord)
    {
        coord = _coord;
    }

    public void Init()
    {
        chunkObject = new GameObject();
        meshFilter = chunkObject.AddComponent<MeshFilter>();
        meshRenderer = chunkObject.AddComponent<MeshRenderer>();

        materials[0] = World.Instance.material;
        materials[1] = World.Instance.transparentMaterial;
        meshRenderer.materials = materials;

        meshRenderer.shadowCastingMode = ShadowCastingMode.TwoSided; // Mostly fixes lines in the shadows between voxels.
        chunkObject.transform.SetParent(World.Instance.transform);
        chunkObject.transform.position = new Vector3(coord.x * VoxelData.ChunkWidth, 0f, coord.z * VoxelData.ChunkWidth);
        chunkObject.name = $"Chunk {coord.x}, {coord.z}";
        chunkPosition = chunkObject.transform.position;

        chunkData = World.Instance.worldData.RequestChunk(new Vector2Int((int)chunkPosition.x, (int)chunkPosition.z), true);

        lock (World.Instance.ChunkUpdateThreadLock)
            World.Instance.chunksToUpdate.Add(this);
        
        PlayChunkLoadAnimation();
    }



    /// <summary>
    /// Updates chunk on the main thread.
    /// TODO: This function can raise a nullReferenceException.
    /// </summary>
    public void UpdateChunk()
    {
        ClearMeshData();

        CalculateLight();

        for (int y = 0; y < VoxelData.ChunkHeight; y++)
        {
            for (int x = 0; x < VoxelData.ChunkWidth; x++)
            {
                for (int z = 0; z < VoxelData.ChunkWidth; z++)
                {
                    if (World.Instance.blockTypes[chunkData.map[x, y, z].id].isSolid)
                        UpdateMeshData(new Vector3(x, y, z));
                }
            }
        }

        World.Instance.chunksToDraw.Enqueue(this);
    }

    private void CalculateLight()
    {
        Queue<Vector3Int> litVoxels = new Queue<Vector3Int>();

        for (int x = 0; x < VoxelData.ChunkWidth; x++)
        {
            for (int z = 0; z < VoxelData.ChunkWidth; z++)
            {
                float lightRay = 1f;

                for (int y = VoxelData.ChunkHeight - 1; y >= 0; y--)
                {
                    VoxelState thisVoxel = chunkData.map[x, y, z];

                    if (thisVoxel.id > 0 && World.Instance.blockTypes[thisVoxel.id].transparency < lightRay) // Only modify light level if block isn't air and block is less transparent then previous blocks.
                        lightRay = World.Instance.blockTypes[thisVoxel.id].transparency;

                    thisVoxel.globalLightPercent = lightRay;

                    chunkData.map[x, y, z] = thisVoxel;

                    // Only add blocks that are still bright enough to affect neighboring blocks.
                    if (lightRay > VoxelData.lightFallOff)
                        litVoxels.Enqueue(new Vector3Int(x, y, z));
                }
            }
        }

        while (litVoxels.Count > 0)
        {
            Vector3Int v = litVoxels.Dequeue();

            for (int p = 0; p < 6; p++)
            {
                Vector3Int currentVoxel = v + VoxelData.FaceChecks[p];
                Vector3Int neighbor = new Vector3Int(currentVoxel.x, currentVoxel.y, currentVoxel.z);

                if (IsVoxelInChunk(neighbor.x, neighbor.y, neighbor.z))
                {
                    // Neighboring voxel needs to be dark enough to be able to be lit up.
                    if (chunkData.map[neighbor.x, neighbor.y, neighbor.z].globalLightPercent < chunkData.map[v.x, v.y, v.z].globalLightPercent - VoxelData.lightFallOff)
                    {
                        chunkData.map[neighbor.x, neighbor.y, neighbor.z].globalLightPercent = chunkData.map[v.x, v.y, v.z].globalLightPercent - VoxelData.lightFallOff;

                        // Neighboring voxel is still lit up enough to affect neighboring voxels.
                        if (chunkData.map[neighbor.x, neighbor.y, neighbor.z].globalLightPercent > VoxelData.lightFallOff)
                        {
                            litVoxels.Enqueue(neighbor);
                        }
                    }
                }
            }
        }
    }

    private void ClearMeshData()
    {
        vertexIndex = 0;
        vertices.Clear();
        triangles.Clear();
        transparantTriangles.Clear();
        uvs.Clear();
        colors.Clear();
        normals.Clear();
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
                PlayChunkLoadAnimation();
            }
        }
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

    public Vector3 GetVoxelPositionInChunkFromGlobalVector3(Vector3 pos)
    {
        int xCheck = Mathf.FloorToInt(pos.x);
        int yCheck = Mathf.FloorToInt(pos.y);
        int zCheck = Mathf.FloorToInt(pos.z);

        xCheck -= Mathf.FloorToInt(chunkPosition.x);
        zCheck -= Mathf.FloorToInt(chunkPosition.z);

        return new Vector3(xCheck, yCheck, zCheck);
    }

    public VoxelState GetVoxelFromGlobalVector3(Vector3 pos)
    {
        int xCheck = Mathf.FloorToInt(pos.x);
        int yCheck = Mathf.FloorToInt(pos.y);
        int zCheck = Mathf.FloorToInt(pos.z);

        xCheck -= Mathf.FloorToInt(chunkPosition.x);
        zCheck -= Mathf.FloorToInt(chunkPosition.z);

        return chunkData.map[xCheck, yCheck, zCheck];
    }

    private VoxelState CheckForVoxel(Vector3 pos)
    {
        int x = Mathf.FloorToInt(pos.x);
        int y = Mathf.FloorToInt(pos.y);
        int z = Mathf.FloorToInt(pos.z);

        if (!IsVoxelInChunk(x, y, z))
        {
            return World.Instance.GetVoxelState(pos + chunkPosition);
        }

        return chunkData.map[x, y, z];
    }

    public Vector3 GetHighestVoxel(Vector3 pos)
    {
        int x = Mathf.FloorToInt(pos.x);
        int z = Mathf.FloorToInt(pos.z);

        for (int i = VoxelData.ChunkHeight - 1; i > 0; i--)
        {
            Vector3 currentVoxel = new Vector3(x, i, z);
            if (World.Instance.blockTypes[CheckForVoxel(currentVoxel).id].isSolid) return currentVoxel;
        }

        return new Vector3(x, VoxelData.ChunkHeight - 1, z);
    }

    public void EditVoxel(Vector3 pos, byte newID)
    {
        int xCheck = Mathf.FloorToInt(pos.x);
        int yCheck = Mathf.FloorToInt(pos.y);
        int zCheck = Mathf.FloorToInt(pos.z);

        xCheck -= Mathf.FloorToInt(chunkPosition.x);
        zCheck -= Mathf.FloorToInt(chunkPosition.z);

        chunkData.map[xCheck, yCheck, zCheck].id = newID;
        World.Instance.worldData.modifiedChunks.Add(chunkData);

        lock (World.Instance.ChunkUpdateThreadLock)
        {
            // Update current chunk as fast as possible.
            World.Instance.chunksToUpdate.Insert(0, this);

            // Update Surrounding Chunks
            UpdateSurroundingVoxels(xCheck, yCheck, zCheck);
        }
    }

    private void UpdateSurroundingVoxels(int x, int y, int z)
    {
        Vector3 thisVoxel = new Vector3(x, y, z);

        for (int p = 0; p < 6; p++) // p = faceIndex
        {
            Vector3 currentVoxel = thisVoxel + VoxelData.FaceChecks[p];

            // Relative position of the voxel within chunk
            int currentVoxelX = (int)currentVoxel.x;
            int currentVoxelY = (int)currentVoxel.y;
            int currentVoxelZ = (int)currentVoxel.z;

            // Absolute position of the voxel (because the UpdateSurroundingVoxels() uses the relative position of a voxel within the chunk, not the global voxel position):
            int currentVoxelWorldPosX = currentVoxelX + (int)chunkPosition.x;
            int currentVoxelWorldPosZ = currentVoxelZ + (int)chunkPosition.z;

            // If surrounding voxel is outside of current chunk and still in the world, update that chunk as well
            if (!IsVoxelInChunk(currentVoxelX, currentVoxelY, currentVoxelZ)
                && World.Instance.worldData.IsVoxelInWorld(new Vector3(currentVoxelWorldPosX, currentVoxelY, currentVoxelWorldPosZ)))
            {
                Vector3 chunkVector = currentVoxel + chunkPosition;
                Chunk chunk = World.Instance.GetChunkFromVector3(chunkVector);

                // Update current chunk as fast as possible.
                World.Instance.chunksToUpdate.Insert(0, chunk);
            }
        }
    }

    private void UpdateMeshData(Vector3 pos)
    {
        int x = Mathf.FloorToInt(pos.x);
        int y = Mathf.FloorToInt(pos.y);
        int z = Mathf.FloorToInt(pos.z);

        byte blockID = chunkData.map[x, y, z].id;
        Vector3 voxelWoldPosition = pos + chunkPosition;

        for (int p = 0; p < 6; p++) // p = faceIndex
        {
            VoxelState neighbor = CheckForVoxel(pos + VoxelData.FaceChecks[p]);

            if (neighbor != null && World.Instance.blockTypes[neighbor.id].renderNeighborFaces || // Display face if facing transparent voxel
                !World.Instance.worldData.IsVoxelInWorld(voxelWoldPosition + VoxelData.FaceChecks[p]) // Display face if facing the edge of the world
               )
            {
                vertices.Add(pos + VoxelData.VoxelVerts[VoxelData.VoxelTris[p, 0]]);
                vertices.Add(pos + VoxelData.VoxelVerts[VoxelData.VoxelTris[p, 1]]);
                vertices.Add(pos + VoxelData.VoxelVerts[VoxelData.VoxelTris[p, 2]]);
                vertices.Add(pos + VoxelData.VoxelVerts[VoxelData.VoxelTris[p, 3]]);

                for (int i = 0; i < 4; i++)
                    normals.Add(VoxelData.FaceChecks[p]);

                AddTexture(World.Instance.blockTypes[blockID].GetTextureID(p));

                // If outside of the world, neighbor would be null, so use own voxel globalLightPercent in that case.
                float lightLevel = neighbor?.globalLightPercent ?? CheckForVoxel(pos).globalLightPercent;

                colors.Add(new Color(0, 0, 0, lightLevel));
                colors.Add(new Color(0, 0, 0, lightLevel));
                colors.Add(new Color(0, 0, 0, lightLevel));
                colors.Add(new Color(0, 0, 0, lightLevel));

                if (!World.Instance.blockTypes[blockID].renderNeighborFaces)
                {
                    triangles.Add(vertexIndex);
                    triangles.Add(vertexIndex + 1);
                    triangles.Add(vertexIndex + 2);
                    triangles.Add(vertexIndex + 2);
                    triangles.Add(vertexIndex + 1);
                    triangles.Add(vertexIndex + 3);
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

    public void CreateMesh()
    {
        Mesh mesh = new Mesh();
        mesh.vertices = vertices.ToArray();

        mesh.subMeshCount = 2;
        mesh.SetTriangles(triangles.ToArray(), 0);
        mesh.SetTriangles(transparantTriangles.ToArray(), 1);

        mesh.uv = uvs.ToArray();
        mesh.colors = colors.ToArray();
        mesh.normals = normals.ToArray();

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


    #region Bonus Stuff

    private void PlayChunkLoadAnimation()
    {
        if (World.Instance.settings.enableChunkLoadAnimations && chunkObject.GetComponent<ChunkLoadAnimation>() == null)
            chunkObject.AddComponent<ChunkLoadAnimation>();
    }

    #endregion
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

[System.Serializable]
public class VoxelState
{
    public byte id;
    public float globalLightPercent;

    public VoxelState()
    {
        id = 0;
        globalLightPercent = 0f;
    }

    public VoxelState(byte _id)
    {
        id = _id;
        globalLightPercent = 0f;
    }
}