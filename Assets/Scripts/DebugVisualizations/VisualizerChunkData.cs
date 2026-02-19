using System.Collections.Generic;
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
        private readonly MeshFilter _meshFilter;
        private readonly Mesh _mesh;

        // Native data for the job
        public NativeHashMap<Vector3Int, Color> VoxelsToDraw;
        public NativeHashMap<Vector3Int, Color> NorthVoxels, SouthVoxels, EastVoxels, WestVoxels;
        public NativeList<Vector3> Vertices;
        public NativeList<int> Triangles;
        public NativeList<Color> Colors;
        private bool _isJobDataAllocated = false;

        public VisualizerChunkData(ChunkCoord coord, Material mat, Transform parent)
        {
            ChunkObject = new GameObject($"Visualizer_{coord.X}_{coord.Z}");
            ChunkObject.transform.SetParent(parent);
            ChunkObject.transform.position = new Vector3(coord.X * VoxelData.ChunkWidth, 0, coord.Z * VoxelData.ChunkWidth);

            _meshFilter = ChunkObject.AddComponent<MeshFilter>();
            var mr = ChunkObject.AddComponent<MeshRenderer>();
            mr.material = mat;

            _mesh = new Mesh();
            _mesh.MarkDynamic(); // Hint that we update this often
            _meshFilter.mesh = _mesh;

            IsMeshApplied = true; // Initially, there's no pending mesh.
        }

        // --- Pooling Methods ---

        /// <summary>
        /// Resets the visualizer for use at a new coordinate.
        /// </summary>
        public void Reset(ChunkCoord coord, Material mat, Transform parent)
        {
            ChunkObject.transform.SetParent(parent);
            ChunkObject.transform.position = new Vector3(coord.X * VoxelData.ChunkWidth, 0, coord.Z * VoxelData.ChunkWidth);
            ChunkObject.name = $"Visualizer_{coord.X}_{coord.Z}";
            ChunkObject.SetActive(true);

            // Ensure material is correct (in case setting changed)
            var mr = ChunkObject.GetComponent<MeshRenderer>();
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

        public void Destroy()
        {
            Release();
            if (_mesh != null) Object.Destroy(_mesh);
            if (ChunkObject != null) Object.Destroy(ChunkObject);
        }

        // --- Logic ---

        public void PrepareJobData(Dictionary<Vector3Int, Color> main, Dictionary<Vector3Int, Color> n, Dictionary<Vector3Int, Color> s, Dictionary<Vector3Int, Color> e, Dictionary<Vector3Int, Color> w)
        {
            // Allocate all native containers for the job.
            VoxelsToDraw = ToNativeHashMap(main, Allocator.TempJob);
            NorthVoxels = ToNativeHashMap(n, Allocator.TempJob);
            SouthVoxels = ToNativeHashMap(s, Allocator.TempJob);
            EastVoxels = ToNativeHashMap(e, Allocator.TempJob);
            WestVoxels = ToNativeHashMap(w, Allocator.TempJob);

            Vertices = new NativeList<Vector3>(Allocator.TempJob);
            Triangles = new NativeList<int>(Allocator.TempJob);
            Colors = new NativeList<Color>(Allocator.TempJob);
            _isJobDataAllocated = true;
            IsMeshApplied = false; // A new job is prepared, its mesh is not yet applied.
        }

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

        public void ClearMesh()
        {
            _mesh.Clear();
            IsMeshApplied = true; // An empty mesh is considered "applied".
        }

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

        private NativeHashMap<Vector3Int, Color> ToNativeHashMap(Dictionary<Vector3Int, Color> dict, Allocator allocator)
        {
            if (dict == null || dict.Count == 0) return new NativeHashMap<Vector3Int, Color>(0, allocator);

            var nativeMap = new NativeHashMap<Vector3Int, Color>(dict.Count, allocator);
            foreach (var kvp in dict)
            {
                nativeMap.Add(kvp.Key, kvp.Value);
            }

            return nativeMap;
        }
    }
}
