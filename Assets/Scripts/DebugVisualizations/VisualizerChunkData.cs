using System.Collections.Generic;
using Data;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace DebugVisualizations
{
    /// <summary>
    /// A helper class to manage the state and data for a single chunk's visualization.
    /// Now pooled by ChunkPoolManager to recycle Meshes and GameObjects.
    /// </summary>
    public class VisualizerChunkData
    {
        public JobHandle JobHandle;
        public bool IsMeshApplied { get; private set; }

        public readonly GameObject ChunkObject;
        private readonly Mesh _mesh;

        // Native data for the job
        public NativeHashMap<Vector3Int, Color> VoxelsToDraw;
        public NativeHashMap<Vector3Int, Color> NorthVoxels, SouthVoxels, EastVoxels, WestVoxels;
        public NativeList<Vector3> Vertices;
        public NativeList<int> Triangles;
        public NativeList<Color> Colors;
        private bool _isJobDataAllocated;

        /// <summary>
        /// Initializes a new instance of the <see cref="VisualizerChunkData"/> class.
        /// </summary>
        /// <param name="chunkCoord">The chunk coordinate to visualize.</param>
        /// <param name="mat">The material used to render the visualization.</param>
        /// <param name="parent">The parent transform to attach the GameObject to.</param>
        public VisualizerChunkData(ChunkCoord chunkCoord, Material mat, Transform parent)
        {
            ChunkObject = new GameObject($"Visualizer_{chunkCoord.X}_{chunkCoord.Z}");
            ChunkObject.transform.SetParent(parent);
            ChunkObject.transform.position = chunkCoord.ToWorldPosition();

            MeshFilter meshFilter = ChunkObject.AddComponent<MeshFilter>();
            MeshRenderer mr = ChunkObject.AddComponent<MeshRenderer>();
            mr.material = mat;

            _mesh = new Mesh();
            _mesh.MarkDynamic(); // Hint that we update this often
            meshFilter.mesh = _mesh;

            IsMeshApplied = true; // Initially, there's no pending mesh.
        }

        // --- Pooling Methods ---

        /// <summary>
        /// Resets the visualizer for use at a new coordinate.
        /// </summary>
        /// <param name="chunkCoord">The new chunk coordinate.</param>
        /// <param name="mat">The material used to render the visualization.</param>
        /// <param name="parent">The parent transform to attach the GameObject to.</param>
        public void Reset(ChunkCoord chunkCoord, Material mat, Transform parent)
        {
            ChunkObject.transform.SetParent(parent);
            ChunkObject.transform.position = chunkCoord.ToWorldPosition();
            ChunkObject.name = $"Visualizer_{chunkCoord.X}_{chunkCoord.Z}";
            ChunkObject.SetActive(true);

            // Ensure material is correct (in case setting changed)
            MeshRenderer mr = ChunkObject.GetComponent<MeshRenderer>();
            if (mr.sharedMaterial != mat) mr.material = mat;

            IsMeshApplied = true;
        }

        /// <summary>
        /// Cleans up native memory and disables object before returning to pool.
        /// </summary>
        public void Release()
        {
            // Ensure job is done before we recycle the object
            JobHandle.Complete();
            DisposeJobData();
            ClearMesh();
            ChunkObject.SetActive(false);
        }

        /// <summary>
        /// Destroys the associated GameObject and frees the mesh and native resources.
        /// </summary>
        public void Destroy()
        {
            Release();
            if (_mesh != null) Object.Destroy(_mesh);
            if (ChunkObject != null) Object.Destroy(ChunkObject);
        }

        // --- Logic ---

        /// <summary>
        /// Prepares the native job data required for the visualization job.
        /// Converts managed dictionaries into <see cref="NativeHashMap{TKey,TValue}"/>.
        /// </summary>
        /// <param name="main">The main chunk's voxel colors.</param>
        /// <param name="n">The north neighbor's voxel colors.</param>
        /// <param name="s">The south neighbor's voxel colors.</param>
        /// <param name="e">The east neighbor's voxel colors.</param>
        /// <param name="w">The west neighbor's voxel colors.</param>
        public void PrepareJobData(Dictionary<Vector3Int, Color> main, Dictionary<Vector3Int, Color> n, Dictionary<Vector3Int, Color> s, Dictionary<Vector3Int, Color> e, Dictionary<Vector3Int, Color> w)
        {
            // Allocate all native containers for the job.
            VoxelsToDraw = ToNativeHashMap(main, Allocator.Persistent);
            NorthVoxels = ToNativeHashMap(n, Allocator.Persistent);
            SouthVoxels = ToNativeHashMap(s, Allocator.Persistent);
            EastVoxels = ToNativeHashMap(e, Allocator.Persistent);
            WestVoxels = ToNativeHashMap(w, Allocator.Persistent);

            Vertices = new NativeList<Vector3>(Allocator.Persistent);
            Triangles = new NativeList<int>(Allocator.Persistent);
            Colors = new NativeList<Color>(Allocator.Persistent);
            _isJobDataAllocated = true;
            IsMeshApplied = false; // A new job is prepared, its mesh is not yet applied.
        }

        /// <summary>
        /// Applies the generated vertices, triangles, and colors from the job output to the mesh.
        /// </summary>
        public void ApplyMesh()
        {
            if (!_isJobDataAllocated) return;

            _mesh.Clear();
            _mesh.SetVertices(Vertices.AsArray());
            _mesh.SetTriangles(Triangles.AsArray().ToArray(), 0);
            _mesh.SetColors(Colors.AsArray());
            _mesh.RecalculateBounds();
            IsMeshApplied = true; // Mark as applied.
        }

        /// <summary>
        /// Clears the existing mesh data.
        /// </summary>
        public void ClearMesh()
        {
            _mesh.Clear();
            IsMeshApplied = true; // An empty mesh is considered "applied".
        }

        /// <summary>
        /// Disposes all the native data allocations associated with the job.
        /// </summary>
        public void DisposeJobData()
        {
            if (!_isJobDataAllocated) return;

            // Dispose all native containers associated with the job.
            if (VoxelsToDraw.IsCreated) VoxelsToDraw.Dispose();
            if (NorthVoxels.IsCreated) NorthVoxels.Dispose();
            if (SouthVoxels.IsCreated) SouthVoxels.Dispose();
            if (EastVoxels.IsCreated) EastVoxels.Dispose();
            if (WestVoxels.IsCreated) WestVoxels.Dispose();

            if (Vertices.IsCreated) Vertices.Dispose();
            if (Triangles.IsCreated) Triangles.Dispose();
            if (Colors.IsCreated) Colors.Dispose();
            _isJobDataAllocated = false;
        }

        private static NativeHashMap<Vector3Int, Color> ToNativeHashMap(Dictionary<Vector3Int, Color> dict, Allocator allocator)
        {
            if (dict == null || dict.Count == 0) return new NativeHashMap<Vector3Int, Color>(0, allocator);

            NativeHashMap<Vector3Int, Color> nativeMap = new NativeHashMap<Vector3Int, Color>(dict.Count, allocator);
            foreach (KeyValuePair<Vector3Int, Color> kvp in dict)
            {
                nativeMap.Add(kvp.Key, kvp.Value);
            }

            return nativeMap;
        }
    }
}
