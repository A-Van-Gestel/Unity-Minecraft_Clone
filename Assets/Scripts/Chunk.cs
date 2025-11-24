using System;
using System.Collections.Generic;
using Data;
using Jobs.BurstData;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

public class Chunk
{
    public readonly ChunkCoord Coord;

    private readonly GameObject _chunkObject;
    private readonly MeshRenderer _meshRenderer;
    private readonly MeshFilter _meshFilter;


    private readonly Material[] _materials = new Material[3];

    public readonly Vector3 ChunkPosition;

    private bool _isActive;

    public readonly ChunkData ChunkData;

    private List<Vector3Int> _activeVoxels = new List<Vector3Int>();

    #region Constructor

    public Chunk(ChunkCoord coord, bool createGameObject = true)
    {
        Coord = coord;
        Vector3 worldPos = new Vector3(Coord.X * VoxelData.ChunkWidth, 0f, Coord.Z * VoxelData.ChunkWidth);
        ChunkPosition = worldPos;

        if (createGameObject)
        {
            _chunkObject = new GameObject();
            _meshFilter = _chunkObject.AddComponent<MeshFilter>();
            _meshRenderer = _chunkObject.AddComponent<MeshRenderer>();

            _materials[0] = World.Instance.opaqueMaterial;
            _materials[1] = World.Instance.transparentMaterial;
            _materials[2] = World.Instance.liquidMaterial;
            _meshRenderer.materials = _materials;

            _meshRenderer.shadowCastingMode = ShadowCastingMode.TwoSided;
            _chunkObject.transform.SetParent(World.Instance.transform);
            _chunkObject.transform.position = worldPos;
            _chunkObject.name = $"Chunk {Coord.X}, {Coord.Z}";
        }

        // Request the ChunkData object. The data inside it will be populated asynchronously by a job.
        ChunkData = World.Instance.worldData.RequestChunk(new Vector2Int((int)ChunkPosition.x, (int)ChunkPosition.z), true);
        ChunkData.Chunk = this;
    }

    #endregion

    /// <summary>
    /// A new method to be called by World.cs after the chunk's data has been populated by a job.
    /// </summary>
    public void OnDataPopulated()
    {
        // Now that the data is here, we can scan for active voxels.
        // Optimization: Iterate through sections first to skip empty ones.
        for (int s = 0; s < ChunkData.sections.Length; s++)
        {
            ChunkSection section = ChunkData.sections[s];
            if (section == null || section.IsEmpty) continue;

            int startY = s * ChunkSection.SIZE;

            // Iterate only within this non-empty section
            for (int i = 0; i < section.voxels.Length; i++)
            {
                uint packedData = section.voxels[i];
                ushort id = BurstVoxelDataBitMapping.GetId(packedData);

                if (World.Instance.blockTypes[id].isActive)
                {
                    // Convert section index back to 3D position
                    int x = i % ChunkSection.SIZE; // Assuming standard size 16
                    int yOffset = (i / ChunkSection.SIZE) % ChunkSection.SIZE;
                    int z = i / (ChunkSection.SIZE * ChunkSection.SIZE);

                    AddActiveVoxel(new Vector3Int(x, startY + yOffset, z));
                }
            }
        }
    }

    /// <summary>
    /// Updates chunk using the Unity Jobs System
    /// </summary>
    public void UpdateChunk()
    {
        // The responsibility of meshing is now on the World orchestrator
        World.Instance.JobManager.ScheduleMeshing(this);
    }

    #region Block Behavior Methods

    public void TickUpdate()
    {
        // A temporary list to avoid modifying the activeVoxels list while iterating.
        List<Vector3Int> stillActive = new List<Vector3Int>();
        Queue<VoxelMod> modifications = new Queue<VoxelMod>();

        foreach (Vector3Int pos in _activeVoxels)
        {
            // Get the list of modifications from the behavior logic.
            List<VoxelMod> mods = BlockBehavior.Behave(ChunkData, pos);

            // If the block is still active, keep it for the next tick.
            if (BlockBehavior.Active(ChunkData, pos))
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
        _activeVoxels = stillActive;
    }

    public void AddActiveVoxel(Vector3Int pos)
    {
        if (!_activeVoxels.Contains(pos))
            _activeVoxels.Add(pos);
    }

    public void RemoveActiveVoxel(Vector3Int pos)
    {
        _activeVoxels.Remove(pos); // List<T>.Remove is efficient enough for this
    }

    public int GetActiveVoxelCount()
    {
        return _activeVoxels.Count;
    }

    #endregion

    public bool isActive
    {
        get => _isActive;
        set
        {
            _isActive = value;
            if (_chunkObject != null)
            {
                _chunkObject.SetActive(value);
                PlayChunkLoadAnimation();
            }
        }
    }

    public Vector3Int GetVoxelPositionInChunkFromGlobalVector3(Vector3 pos)
    {
        int xCheck = Mathf.FloorToInt(pos.x);
        int yCheck = Mathf.FloorToInt(pos.y);
        int zCheck = Mathf.FloorToInt(pos.z);

        xCheck -= Mathf.FloorToInt(ChunkPosition.x);
        zCheck -= Mathf.FloorToInt(ChunkPosition.z);

        return new Vector3Int(xCheck, yCheck, zCheck);
    }

    #region Mesh Generation

    // This is called by World.cs when a mesh job for this chunk is complete.
    public void ApplyMeshData(MeshDataJobOutput meshData)
    {
        Mesh mesh = new Mesh();
        mesh.vertices = meshData.Vertices.ToArray(Allocator.Temp).ToArray();
        mesh.subMeshCount = 3;
        mesh.SetTriangles(meshData.Triangles.ToArray(Allocator.Temp).ToArray(), 0);
        mesh.SetTriangles(meshData.TransparentTriangles.ToArray(Allocator.Temp).ToArray(), 1);
        mesh.SetTriangles(meshData.FluidTriangles.ToArray(Allocator.Temp).ToArray(), 2);
        mesh.uv = meshData.Uvs.ToArray(Allocator.Temp).ToArray();
        mesh.colors = meshData.Colors.ToArray(Allocator.Temp).ToArray();
        mesh.normals = meshData.Normals.ToArray(Allocator.Temp).ToArray();

        mesh.RecalculateBounds();
        _meshFilter.mesh = mesh;

        // Dispose the native lists now that we're done with them.
        meshData.Dispose();

        // Add to the draw queue to be enabled on the main thread
        World.Instance.ChunksToDraw.Enqueue(this);
    }

    // CreateMesh is now just the final step of enabling the renderer after data is applied.
    public void CreateMesh()
    {
        // The mesh is already assigned in ApplyMeshData.
        // This method could be used to enable the GameObject or an animation.
        PlayChunkLoadAnimation();
    }

    #endregion

    #region Public Getters

    /// <summary>
    /// Gets the list of active voxels in this chunk.
    /// </summary>
    public List<Vector3Int> ActiveVoxels => _activeVoxels;

    #endregion

    #region Debug Information Methods

    /// <summary>
    /// Checks if a voxel is active in this chunk.
    /// </summary>
    /// <param name="localPos">The local position of the voxel in the given chunk.</param>
    /// <returns>True if the voxel is active, false otherwise.</returns>
    public bool IsVoxelActive(Vector3Int localPos)
    {
        if (_activeVoxels.Count == 0)
            return false;

        return _activeVoxels.Contains(localPos);
    }

    #endregion


    #region Bonus Stuff

    private void PlayChunkLoadAnimation()
    {
        if (World.Instance.settings.enableChunkLoadAnimations && _chunkObject.GetComponent<ChunkLoadAnimation>() == null)
            _chunkObject.AddComponent<ChunkLoadAnimation>();
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