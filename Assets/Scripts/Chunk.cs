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

    public Chunk(ChunkCoord _coord, bool createGameObject = true)
    {
        coord = _coord;
        Vector3 worldPos = new Vector3(coord.X * VoxelData.ChunkWidth, 0f, coord.Z * VoxelData.ChunkWidth);
        chunkPosition = worldPos;

        if (createGameObject)
        {
            chunkObject = new GameObject();
            meshFilter = chunkObject.AddComponent<MeshFilter>();
            meshRenderer = chunkObject.AddComponent<MeshRenderer>();

            materials[0] = World.Instance.material;
            materials[1] = World.Instance.transparentMaterial;
            materials[2] = World.Instance.liquidMaterial;
            meshRenderer.materials = materials;

            meshRenderer.shadowCastingMode = ShadowCastingMode.TwoSided;
            chunkObject.transform.SetParent(World.Instance.transform);
            chunkObject.transform.position = worldPos;
            chunkObject.name = $"Chunk {coord.X}, {coord.Z}";
        }

        // Request the ChunkData object. The data inside it will be populated asynchronously by a job.
        chunkData = World.Instance.worldData.RequestChunk(new Vector2Int((int)chunkPosition.x, (int)chunkPosition.z), true);
        chunkData.chunk = this;
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
        // A temporary list to avoid modifying the activeVoxels list while iterating.
        List<Vector3Int> stillActive = new List<Vector3Int>();
        Queue<VoxelMod> modifications = new Queue<VoxelMod>();

        foreach (Vector3Int pos in activeVoxels)
        {
            // Get the list of modifications from the behavior logic.
            List<VoxelMod> mods = BlockBehavior.Behave(chunkData, pos);

            // If the block is still active, keep it for the next tick.
            if (BlockBehavior.Active(chunkData, pos))
            {
                stillActive.Add(pos);
            }

            // If the behavior produced any changes, add them to our queue.
            if (mods != null)
            {
                foreach (VoxelMod mod in mods)
                {
                    modifications.Enqueue(mod);
                }
            }
        }

        // If there are any modifications, submit them to the world's global queue.
        if (modifications.Count > 0)
        {
            World.Instance.EnqueueVoxelModifications(modifications);
        }

        // Update the active voxel list for the next frame.
        activeVoxels = stillActive;
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

    #region Mesh Generation

    // This is called by World.cs when a mesh job for this chunk is complete.
    public void ApplyMeshData(MeshDataJobOutput meshData)
    {
        Mesh mesh = new Mesh();
        mesh.vertices = meshData.vertices.ToArray(Allocator.Temp).ToArray();
        mesh.subMeshCount = 3;
        mesh.SetTriangles(meshData.triangles.ToArray(Allocator.Temp).ToArray(), 0);
        mesh.SetTriangles(meshData.transparentTriangles.ToArray(Allocator.Temp).ToArray(), 1);
        mesh.SetTriangles(meshData.fluidTriangles.ToArray(Allocator.Temp).ToArray(), 2);
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
    public readonly int X;
    public readonly int Z;

    #region Constructors

    public ChunkCoord()
    {
        X = 0;
        Z = 0;
    }

    public ChunkCoord(int x, int z)
    {
        X = x;
        Z = z;
    }

    public ChunkCoord(Vector2 pos)
    {
        int xInt = Mathf.FloorToInt(pos.x);
        int zInt = Mathf.FloorToInt(pos.y);

        X = xInt / VoxelData.ChunkWidth;
        Z = zInt / VoxelData.ChunkWidth;
    }

    public ChunkCoord(Vector2Int pos)
    {
        X = pos.x / VoxelData.ChunkWidth;
        Z = pos.y / VoxelData.ChunkWidth;
    }

    public ChunkCoord(Vector3 pos)
    {
        int xInt = Mathf.FloorToInt(pos.x);
        int zInt = Mathf.FloorToInt(pos.z);

        X = xInt / VoxelData.ChunkWidth;
        Z = zInt / VoxelData.ChunkWidth;
    }

    public ChunkCoord(Vector3Int pos)
    {
        X = pos.x / VoxelData.ChunkWidth;
        Z = pos.z / VoxelData.ChunkWidth;
    }

    #endregion
    
    #region Type Conversion

    public Vector2Int ToVector2Int()
    {
        return new Vector2Int(X, Z);
    }

    public static implicit operator Vector2Int(ChunkCoord coord)
    {
        return new Vector2Int(coord.X, coord.Z);
    }
    
    #endregion
    
    #region Overides

    public override int GetHashCode()
    {
        // Multiply x & y by different constant to differentiate between situations like x=12 & z=13 and x=13 & z=12.
        return 31 * X + 17 * Z;
    }

    public override bool Equals(object obj)
    {
        return obj is ChunkCoord coord && Equals(coord);
    }

    public bool Equals(ChunkCoord other)
    {
        if (other == null)
            return false;

        return other.X == X && other.Z == Z;
    }
    
    public override string ToString()
    {
        return $"ChunkCoord({X}, {Z})";
    }
    
    #endregion
}