using System;
using System.Collections.Generic;
using Data;
using Jobs.BurstData;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

public class Chunk
{
    public ChunkCoord coord;

    private GameObject chunkObject;
    private MeshRenderer meshRenderer;
    private MeshFilter meshFilter;


    private Material[] materials = new Material[3];

    public Vector3 chunkPosition;

    private bool _isActive;

    public ChunkData chunkData;

    private List<Vector3Int> activeVoxels = new List<Vector3Int>();

    #region Constructor

    public Chunk(ChunkCoord _coord)
    {
        coord = _coord;
        chunkObject = new GameObject();
        meshFilter = chunkObject.AddComponent<MeshFilter>();
        meshRenderer = chunkObject.AddComponent<MeshRenderer>();

        materials[0] = World.Instance.material;
        materials[1] = World.Instance.transparentMaterial;
        materials[2] = World.Instance.waterMaterial;
        meshRenderer.materials = materials;

        meshRenderer.shadowCastingMode = ShadowCastingMode.TwoSided;
        chunkObject.transform.SetParent(World.Instance.transform);
        chunkObject.transform.position = new Vector3(coord.x * VoxelData.ChunkWidth, 0f, coord.z * VoxelData.ChunkWidth);
        chunkObject.name = $"Chunk {coord.x}, {coord.z}";
        chunkPosition = chunkObject.transform.position;

        // Request the ChunkData object. The data inside it will be populated asynchronously by a job.
        chunkData = World.Instance.worldData.RequestChunk(new Vector2Int((int)chunkPosition.x, (int)chunkPosition.z), true);
        chunkData.chunk = this;

        // The chunk is created, but its mesh can't be built until the generation job is complete.
        // The job completion logic in World.cs will add this chunk to `chunksToBuildMesh`.
    }

    #endregion

    /// <summary>
    /// A new method to be called by World.cs after the chunk's data has been populated by a job.
    /// </summary>
    public void OnDataPopulated()
    {
        // Now that the data is here, we can scan for active voxels.
        for (int i = 0; i < chunkData.map.Length; i++)
        {
            uint packedData = chunkData.map[i];
            byte id = BurstVoxelDataBitMapping.GetId(packedData);

            if (World.Instance.blockTypes[id].isActive)
            {
                // Convert flat index back to 3D position
                int x = i % VoxelData.ChunkWidth;
                int y = (i / VoxelData.ChunkWidth) % VoxelData.ChunkHeight;
                int z = i / (VoxelData.ChunkWidth * VoxelData.ChunkHeight);

                AddActiveVoxel(new Vector3Int(x, y, z));
            }
        }
    }

    /// <summary>
    /// Updates chunk using the Unity Jobs System
    /// </summary>
    public void UpdateChunk()
    {
        // The responsibility of meshing is now on the World orchestrator
        World.Instance.ScheduleMeshing(this);
    }

    #region Block Behavior Methods

    public void TickUpdate()
    {
        for (int i = activeVoxels.Count - 1; i >= 0; i--)
        {
            Vector3Int pos = activeVoxels[i];
            // Pass context to the static BlockBehavior methods
            if (!BlockBehavior.Active(chunkData, pos))
                RemoveActiveVoxel(pos);
            else
                BlockBehavior.Behave(chunkData, pos);
        }
    }

    public void AddActiveVoxel(Vector3Int pos)
    {
        if (!activeVoxels.Contains(pos))
            activeVoxels.Add(pos);
    }

    public void RemoveActiveVoxel(Vector3Int pos)
    {
        activeVoxels.Remove(pos); // List<T>.Remove is efficient enough for this
    }

    public int GetActiveVoxelCount()
    {
        return activeVoxels.Count;
    }

    #endregion

    public bool isActive
    {
        get => _isActive;
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

    public Vector3Int GetVoxelPositionInChunkFromGlobalVector3(Vector3 pos)
    {
        int xCheck = Mathf.FloorToInt(pos.x);
        int yCheck = Mathf.FloorToInt(pos.y);
        int zCheck = Mathf.FloorToInt(pos.z);

        xCheck -= Mathf.FloorToInt(chunkPosition.x);
        zCheck -= Mathf.FloorToInt(chunkPosition.z);

        return new Vector3Int(xCheck, yCheck, zCheck);
    }

    public void EditVoxel(Vector3 pos, byte newID)
    {
        int xCheck = Mathf.FloorToInt(pos.x);
        int yCheck = Mathf.FloorToInt(pos.y);
        int zCheck = Mathf.FloorToInt(pos.z);

        xCheck -= Mathf.FloorToInt(chunkPosition.x);
        zCheck -= Mathf.FloorToInt(chunkPosition.z);

        chunkData.ModifyVoxel(new Vector3Int(xCheck, yCheck, zCheck), newID, World.Instance.player.orientation, true);

        // // Update Surrounding Chunks
        // UpdateSurroundingVoxels(xCheck, yCheck, zCheck, immediate: true);
    }

    // private void UpdateSurroundingVoxels(int x, int y, int z, bool immediate = false)
    // {
    //     for (int p = 0; p < 6; p++) // p = faceIndex
    //     {
    //         // Skip top (2) and bottom (3) faces, as we don't have chunk neighbors for those
    //         if (p == 2 || p == 3) continue;
    //
    //         Vector3Int neighborPos = new Vector3Int(x, y, z) + VoxelData.FaceChecks[p];
    //
    //         // If the neighbor is outside the current chunk...
    //         if (!chunkData.IsVoxelInChunk(neighborPos))
    //         {
    //             // We don't need to check IsVoxelInWorld here, as GetChunkFromVector3 will handle it.
    //             Vector3 neighborWorldPos = new Vector3(neighborPos.x, neighborPos.y, neighborPos.z) + chunkPosition;
    //             
    //             Chunk neighborChunk = World.Instance.GetChunkFromVector3(neighborWorldPos);
    //
    //             // If we found a valid, active neighbor chunk, request a mesh rebuild for it.
    //             if (neighborChunk != null && neighborChunk.isActive)
    //             {
    //                 World.Instance.RequestChunkMeshRebuild(neighborChunk, immediate: immediate);
    //             }
    //         }
    //     }
    // }

    #region Mesh Generation

    // This is called by World.cs when a mesh job for this chunk is complete.
    public void ApplyMeshData(MeshDataJobOutput meshData)
    {
        Mesh mesh = new Mesh();
        mesh.vertices = meshData.vertices.ToArray(Allocator.Temp).ToArray();
        mesh.subMeshCount = 3;
        mesh.SetTriangles(meshData.triangles.ToArray(Allocator.Temp).ToArray(), 0);
        mesh.SetTriangles(meshData.transparentTriangles.ToArray(Allocator.Temp).ToArray(), 1);
        mesh.SetTriangles(meshData.waterTriangles.ToArray(Allocator.Temp).ToArray(), 2);
        mesh.uv = meshData.uvs.ToArray(Allocator.Temp).ToArray();
        mesh.colors = meshData.colors.ToArray(Allocator.Temp).ToArray();
        mesh.normals = meshData.normals.ToArray(Allocator.Temp).ToArray();

        mesh.RecalculateBounds();
        meshFilter.mesh = mesh;

        // Dispose the native lists now that we're done with them.
        meshData.Dispose();

        // Add to the draw queue to be enabled on the main thread
        World.Instance.chunksToDraw.Enqueue(this);
    }

    // CreateMesh is now just the final step of enabling the renderer after data is applied.
    public void CreateMesh()
    {
        // The mesh is already assigned in ApplyMeshData.
        // This method could be used to enable the GameObject or an animation.
        PlayChunkLoadAnimation();
    }

    #endregion


    #region Bonus Stuff

    private void PlayChunkLoadAnimation()
    {
        if (World.Instance.settings.enableChunkLoadAnimations && chunkObject.GetComponent<ChunkLoadAnimation>() == null)
            chunkObject.AddComponent<ChunkLoadAnimation>();
    }

    #endregion
}

public class ChunkCoord : IEquatable<ChunkCoord>
{
    public readonly int x;
    public readonly int z;

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

    public override int GetHashCode()
    {
        // Multiply x & y by different constant to differentiate between situations like x=12 & z=13 and x=13 & z=12.
        return 31 * x + 17 * z;
    }

    public override bool Equals(object obj)
    {
        return obj is ChunkCoord coord && Equals(coord);
    }

    public bool Equals(ChunkCoord other)
    {
        if (other == null)
            return false;

        return other.x == x && other.z == z;
    }
}