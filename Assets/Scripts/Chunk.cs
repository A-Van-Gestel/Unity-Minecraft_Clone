using System.Collections.Generic;
using Data;
using Helpers;
using Jobs.BurstData;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Pool;
using Object = UnityEngine.Object;

public class Chunk
{
    public ChunkCoord Coord;
    public Vector3 ChunkPosition;
    public ChunkData ChunkData;
    private SectionRenderer[] _sectionRenderers;

    // Expose for pool management validation
    public readonly GameObject ChunkGameObject;

    private bool _isActive;
    private HashSet<Vector3Int> _activeVoxels = new HashSet<Vector3Int>();

    #region Constructor

    /// <summary>
    /// Creates a new Chunk Visual. 
    /// NOTE: Should only be called by the ChunkPool. Use World.GetChunkFromPool() instead.
    /// </summary>
    public Chunk(ChunkCoord chunkCoord)
    {
        Coord = chunkCoord;

        // Create GameObject hierarchy
        ChunkGameObject = new GameObject($"Chunk {Coord.X}, {Coord.Z}");
        ChunkGameObject.transform.SetParent(World.Instance.transform);

        // Initialize Section Renderers
        int sectionCount = VoxelData.ChunkHeight / ChunkMath.SECTION_SIZE;
        _sectionRenderers = new SectionRenderer[sectionCount];
        for (int i = 0; i < sectionCount; i++)
        {
            _sectionRenderers[i] = new SectionRenderer(ChunkGameObject.transform, i);
        }

        // Ensure object is inactive until properly Reset/Activated
        ChunkGameObject.SetActive(false);
    }

    #endregion

    #region Lifecycle

    /// <summary>
    /// Resets the Chunk instance for use at a new coordinate.
    /// Used by the ChunkPool.
    /// </summary>
    public void Reset(ChunkCoord chunkCoord)
    {
        Coord = chunkCoord;
        ChunkPosition = Coord.ToWorldPosition();

        // Update GameObject identity
        ChunkGameObject.name = $"Chunk {Coord.X}, {Coord.Z}";
        ChunkGameObject.transform.position = ChunkPosition;

        // Reset State
        _isActive = true;
        _activeVoxels.Clear();

        Vector2Int worldPosKey = Coord.ToVoxelOrigin();

        // Link Data
        // NOTE: We retrieve the existing data (loaded or generated) from WorldData.
        ChunkData = World.Instance.worldData.RequestChunk(worldPosKey, true);

        // CRITICAL: Link the Data to this Visual Instance
        if (ChunkData != null)
        {
            ChunkData.Chunk = this;
        }

        // Reset Visuals (clears mesh but keeps memory allocated)
        for (int i = 0; i < _sectionRenderers.Length; i++)
        {
            _sectionRenderers[i].Clear();
        }

        // Ensure object is active
        ChunkGameObject.SetActive(true);
    }

    /// <summary>
    /// Prepares the chunk to be returned to the pool.
    /// Unlinks data references to prevent ghost updates.
    /// </summary>
    public void Release()
    {
        // CRITICAL: Unlink the Data.
        // If this ChunkData is modified while the Visual is in the pool, 
        // it shouldn't try to update a disabled GameObject.
        if (ChunkData != null)
        {
            ChunkData.Chunk = null;
            ChunkData = null;
        }

        if (ChunkGameObject != null)
        {
            ChunkGameObject.SetActive(false);
        }

        _isActive = false;
    }

    /// <summary>
    /// Permanently destroys the GameObject. Used when shutting down the pool.
    /// </summary>
    public void Destroy()
    {
        if (ChunkGameObject != null)
        {
            Object.Destroy(ChunkGameObject);
        }

        // Clean up renderers (Meshes)
        if (_sectionRenderers != null)
        {
            foreach (SectionRenderer sr in _sectionRenderers) sr.Destroy();
        }
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

            int startY = s * ChunkMath.SECTION_SIZE;

            // Iterate only within this non-empty section
            for (int i = 0; i < section.voxels.Length; i++)
            {
                uint packedData = section.voxels[i];
                ushort id = BurstVoxelDataBitMapping.GetId(packedData);

                if (World.Instance.blockTypes[id].isActive)
                {
                    // Convert section index back to 3D position
                    int x = i % ChunkMath.SECTION_SIZE;
                    int yOffset = (i / ChunkMath.SECTION_SIZE) % ChunkMath.SECTION_SIZE;
                    int z = i / (ChunkMath.SECTION_SIZE * ChunkMath.SECTION_SIZE);

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
        if (_activeVoxels.Count == 0) return;

        // A temporary, pooled list to track items that need to be removed
        List<Vector3Int> toRemove = ListPool<Vector3Int>.Get();

        foreach (Vector3Int pos in _activeVoxels)
        {
            // Get the list of modifications from the behavior logic.
            List<VoxelMod> mods = BlockBehavior.Behave(ChunkData, pos);

            // If the block is NO LONGER active, mark it for removal
            // TODO: Future refactor could combine Behave and Active logic to save chunk lookups
            if (!BlockBehavior.Active(ChunkData, pos))
            {
                toRemove.Add(pos);
            }

            // If the behavior produced any changes, submit them to the world's global queue.
            if (mods != null)
            {
                World.Instance.EnqueueVoxelModifications(mods);
            }
        }

        // Remove inactive voxels from the HashSet in O(1) time each
        foreach (var pos in toRemove)
        {
            _activeVoxels.Remove(pos);
        }

        // Release the temporary list back to the pool
        ListPool<Vector3Int>.Release(toRemove);
    }

    public void AddActiveVoxel(Vector3Int pos)
    {
        _activeVoxels.Add(pos);
    }

    public void RemoveActiveVoxel(Vector3Int pos)
    {
        _activeVoxels.Remove(pos);
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
            if (ChunkGameObject != null)
            {
                ChunkGameObject.SetActive(value);
                if (value) PlayChunkLoadAnimation();
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

    // Burst Job to adjust vertex positions (Global Y -> Local Section Y) and triangle indices (Global Index -> Local Index)
    [BurstCompile]
    private struct PostProcessMeshJob : IJob
    {
        public NativeList<Vector3> Vertices;
        public NativeList<int> OpaqueTris;
        public NativeList<int> TransparentTris;
        public NativeList<int> FluidTris;

        [ReadOnly]
        public NativeArray<MeshSectionStats> Stats;

        public int SectionHeight;

        public void Execute()
        {
            // We iterate sections inside the job to avoid overhead of scheduling many tiny jobs
            for (int i = 0; i < Stats.Length; i++)
            {
                MeshSectionStats s = Stats[i];
                if (s.VertexCount == 0) continue;

                float yOffset = i * SectionHeight;
                int vertStart = s.VertexStartIndex;

                // 1. Adjust Vertices: Subtract section Y offset so they are local to the Section GameObject
                for (int v = 0; v < s.VertexCount; v++)
                {
                    int index = vertStart + v;
                    Vector3 pos = Vertices[index];
                    pos.y -= yOffset;
                    Vertices[index] = pos;
                }

                // 2. Adjust Indices: Relativize indices to start at 0 for this section
                // The indices currently point to the 'allVerts' array. 
                // We need them to point to the start of the section slice.
                int offset = -vertStart;

                AdjustIndices(OpaqueTris, s.OpaqueTriStartIndex, s.OpaqueTriCount, offset);
                AdjustIndices(TransparentTris, s.TransparentTriStartIndex, s.TransparentTriCount, offset);
                AdjustIndices(FluidTris, s.FluidTriStartIndex, s.FluidTriCount, offset);
            }
        }

        private void AdjustIndices(NativeList<int> indices, int start, int count, int offset)
        {
            for (int k = 0; k < count; k++)
            {
                indices[start + k] += offset;
            }
        }
    }

    // This is called by World.cs when a mesh job for this chunk is complete.
    public void ApplyMeshData(MeshDataJobOutput meshData)
    {
        // 1. Run a fast Burst job on the main thread to adjust coordinate spaces from Chunk-Space to Section-Space.
        // This modifies the data in-place efficiently.
        var postProcessJob = new PostProcessMeshJob
        {
            Vertices = meshData.Vertices,
            OpaqueTris = meshData.Triangles,
            TransparentTris = meshData.TransparentTriangles,
            FluidTris = meshData.FluidTriangles,
            Stats = meshData.SectionStats,
            SectionHeight = ChunkMath.SECTION_SIZE
        };

        postProcessJob.Schedule().Complete();

        // 2. Pass the data to the renderers using zero-allocation NativeArray views.
        NativeArray<MeshSectionStats> stats = meshData.SectionStats;

        // Obtain raw NativeArray views from the lists
        var allVerts = meshData.Vertices.AsArray();
        var allUvs = meshData.Uvs.AsArray();
        var allColors = meshData.Colors.AsArray();
        var allNormals = meshData.Normals.AsArray();
        var allOpaqueTris = meshData.Triangles.AsArray();
        var allTransTris = meshData.TransparentTriangles.AsArray();
        var allFluidTris = meshData.FluidTriangles.AsArray();

        for (int i = 0; i < _sectionRenderers.Length; i++)
        {
            MeshSectionStats s = stats[i];

            if (s.VertexCount == 0)
            {
                // Pass empty data to clear mesh / disable object
                _sectionRenderers[i].UpdateMeshNative(
                    default, default, default, default, 0, 0,
                    default, 0, 0,
                    default, 0, 0,
                    default, 0, 0
                );
                continue;
            }

            _sectionRenderers[i].UpdateMeshNative(
                allVerts, allUvs, allColors, allNormals, s.VertexStartIndex, s.VertexCount,
                allOpaqueTris, s.OpaqueTriStartIndex, s.OpaqueTriCount,
                allTransTris, s.TransparentTriStartIndex, s.TransparentTriCount,
                allFluidTris, s.FluidTriStartIndex, s.FluidTriCount
            );
        }

        // Dispose native memory
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
    /// Gets a read-only collection of the active voxels in this chunk.
    /// </summary>
    public IReadOnlyCollection<Vector3Int> ActiveVoxels => _activeVoxels;

    #endregion

    #region Debug Information Methods

    /// <summary>
    /// Checks if a voxel is active in this chunk.
    /// </summary>
    /// <param name="localVoxelPos">The local position of the voxel in the given chunk.</param>
    /// <returns>True if the voxel is active, false otherwise.</returns>
    public bool IsVoxelActive(Vector3Int localVoxelPos)
    {
        if (_activeVoxels.Count == 0)
            return false;

        return _activeVoxels.Contains(localVoxelPos);
    }

    #endregion


    #region Bonus Stuff

    private void PlayChunkLoadAnimation()
    {
        if (World.Instance.settings.enableChunkLoadAnimations && ChunkGameObject.GetComponent<ChunkLoadAnimation>() == null)
            ChunkGameObject.AddComponent<ChunkLoadAnimation>();
    }

    #endregion
}
