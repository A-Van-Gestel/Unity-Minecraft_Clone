using System.Collections.Generic;
using DebugVisualizations.Jobs;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace DebugVisualizations
{
    public class VoxelVisualizer : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("The material using the DebugVoxelShader.")]
        public Material visualizerMaterial;

        // A dictionary to hold the job data and mesh for each chunk visualization.
        private readonly Dictionary<ChunkCoord, VisualizerChunkData> _visualizerChunks = new Dictionary<ChunkCoord, VisualizerChunkData>();
        private Transform _visualizerParent;

        // Static native arrays for job data, initialized once.
        private NativeArray<Vector3Int> _faceChecks;
        private NativeArray<int> _voxelTris;
        private NativeArray<Vector3> _voxelVerts;

        private void Start()
        {
            // Create a parent object to keep the hierarchy clean.
            _visualizerParent = new GameObject("VoxelVisualizerMeshes").transform;
            _visualizerParent.SetParent(transform, false);

            // Initialize static Burst-compatible data.
            _faceChecks = new NativeArray<Vector3Int>(VoxelData.FaceChecks, Allocator.Persistent);
            _voxelTris = new NativeArray<int>(VoxelData.VoxelTris, Allocator.Persistent);
            _voxelVerts = new NativeArray<Vector3>(VoxelData.VoxelVerts, Allocator.Persistent);
        }

        private void OnDestroy()
        {
            ClearAll(); // This will handle completing jobs and disposing data.

            if (_faceChecks.IsCreated) _faceChecks.Dispose();
            if (_voxelTris.IsCreated) _voxelTris.Dispose();
            if (_voxelVerts.IsCreated) _voxelVerts.Dispose();
        }

        // We use LateUpdate to check for completed jobs from the current frame.
        private void LateUpdate()
        {
            // This is the "Apply" step of our async process.
            foreach (var chunkData in _visualizerChunks.Values)
            {
                // If a job is finished AND its results haven't been applied yet, apply them.
                if (!chunkData.IsMeshApplied && chunkData.JobHandle.IsCompleted)
                {
                    chunkData.JobHandle.Complete(); // Syncs the job.
                    chunkData.ApplyMesh(); // Apply the results to the MeshFilter.
                    chunkData.DisposeJobData(); // Dispose the Native collections used by the job.
                }
            }
        }

        /// <summary>
        /// Prepares and schedules a Burst job to generate the visualization mesh.
        /// </summary>
        /// <param name="coord">The coordinate of the chunk to update.</param>
        /// <param name="voxelsToDraw">A dictionary mapping local voxel positions to their desired color.</param>
        /// <param name="northVoxels"></param>
        /// <param name="southVoxels"></param>
        /// <param name="eastVoxels"></param>
        /// <param name="westVoxels"></param>
        public void UpdateChunkVisualization(
            ChunkCoord coord,
            Dictionary<Vector3Int, Color> voxelsToDraw,
            Dictionary<Vector3Int, Color> northVoxels,
            Dictionary<Vector3Int, Color> southVoxels,
            Dictionary<Vector3Int, Color> eastVoxels,
            Dictionary<Vector3Int, Color> westVoxels)
        {
            // Get or create the GameObject for this chunk's visualization.
            if (!_visualizerChunks.TryGetValue(coord, out var chunkData))
            {
                chunkData = new VisualizerChunkData(coord, visualizerMaterial, _visualizerParent);
                _visualizerChunks[coord] = chunkData;
            }

            // If a job for this chunk is already running, complete it before starting a new one.
            chunkData.JobHandle.Complete();
            chunkData.DisposeJobData();

            // If there's nothing to draw, clear the mesh and return.
            if (voxelsToDraw == null || voxelsToDraw.Count == 0)
            {
                chunkData.ClearMesh();
                return;
            }

            // --- This is the "Overhead": Convert managed Dictionaries to NativeHashMaps ---
            chunkData.PrepareJobData(voxelsToDraw, northVoxels, southVoxels, eastVoxels, westVoxels);

            var job = new VoxelVisualizerJob
            {
                VoxelsToDraw = chunkData.VoxelsToDraw,
                NorthVoxels = chunkData.NorthVoxels,
                SouthVoxels = chunkData.SouthVoxels,
                EastVoxels = chunkData.EastVoxels,
                WestVoxels = chunkData.WestVoxels,

                FaceChecks = _faceChecks,
                VoxelTris = _voxelTris,
                VoxelVerts = _voxelVerts,

                Vertices = chunkData.Vertices,
                Triangles = chunkData.Triangles,
                Colors = chunkData.Colors
            };

            // Schedule the job and store the handle.
            chunkData.JobHandle = job.Schedule();
        }

        /// <summary>
        /// Clears a specific chunk's visualization mesh.
        /// </summary>
        public void ClearChunkVisualization(ChunkCoord coord)
        {
            if (_visualizerChunks.TryGetValue(coord, out var chunkData))
            {
                chunkData.JobHandle.Complete();
                chunkData.Dispose();
                _visualizerChunks.Remove(coord);
            }
        }

        /// <summary>
        /// Destroys all visualization GameObjects.
        /// </summary>
        public void ClearAll()
        {
            foreach (var chunkData in _visualizerChunks.Values)
            {
                chunkData.JobHandle.Complete();
                chunkData.Dispose();
            }

            _visualizerChunks.Clear();
        }

        /// <summary>
        /// A helper class to manage the state and data for a single chunk's visualization.
        /// </summary>
        private class VisualizerChunkData
        {
            public JobHandle JobHandle;
            public bool IsMeshApplied { get; private set; } // State flag to prevent multiple applications.

            private readonly GameObject _chunkObject;
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
                _chunkObject = new GameObject($"Visualizer_{coord.X}_{coord.Z}");
                _chunkObject.transform.SetParent(parent);
                _chunkObject.transform.position = new Vector3(coord.X * VoxelData.ChunkWidth, 0, coord.Z * VoxelData.ChunkWidth);
                _meshFilter = _chunkObject.AddComponent<MeshFilter>();
                var mr = _chunkObject.AddComponent<MeshRenderer>();
                mr.material = mat;
                _mesh = new Mesh();
                _meshFilter.mesh = _mesh;
                IsMeshApplied = true; // Initially, there's no pending mesh.
            }

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
                _mesh.SetVertices(Vertices.ToArray(Allocator.Temp).ToArray());
                _mesh.SetTriangles(Triangles.ToArray(Allocator.Temp).ToArray(), 0);
                _mesh.SetColors(Colors.ToArray(Allocator.Temp).ToArray());
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

            public void Dispose()
            {
                DisposeJobData();
                if (_chunkObject != null) Destroy(_chunkObject);
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

    // Add this enum definition above the World class
    public enum DebugVisualizationMode
    {
        None,
        ActiveVoxels,
        Sunlight,
        Blocklight,
        FluidLevel
    }
}