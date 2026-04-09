using System.Collections.Generic;
using Data;
using DebugVisualizations.Jobs;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace DebugVisualizations
{
    /// <summary>
    /// Manages the generation and rendering of debug visualization meshes for chunks in the world.
    /// This is primarily used to view active voxels or lighting spread states.
    /// </summary>
    public class VoxelVisualizer : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("The material using the DebugVoxelShader.")]
        public Material visualizerMaterial;

        // Dictionary holds references, but objects are managed by Pool
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
            foreach (VisualizerChunkData chunkData in _visualizerChunks.Values)
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
        /// <param name="chunkCoord">The coordinate of the chunk to update.</param>
        /// <param name="voxelsToDraw">A dictionary mapping local voxel positions to their desired color.</param>
        /// <param name="northVoxels">A dictionary mapping the north neighbor's voxel positions to their desired color.</param>
        /// <param name="southVoxels">A dictionary mapping the south neighbor's voxel positions to their desired color.</param>
        /// <param name="eastVoxels">A dictionary mapping the east neighbor's voxel positions to their desired color.</param>
        /// <param name="westVoxels">A dictionary mapping the west neighbor's voxel positions to their desired color.</param>
        public void UpdateChunkVisualization(
            ChunkCoord chunkCoord,
            Dictionary<Vector3Int, Color> voxelsToDraw,
            Dictionary<Vector3Int, Color> northVoxels,
            Dictionary<Vector3Int, Color> southVoxels,
            Dictionary<Vector3Int, Color> eastVoxels,
            Dictionary<Vector3Int, Color> westVoxels)
        {
            // Get or create the GameObject for this chunk's visualization.
            if (!_visualizerChunks.TryGetValue(chunkCoord, out VisualizerChunkData chunkData))
            {
                // POOLING: Get from World.Instance.ChunkPool
                chunkData = World.Instance.ChunkPool.GetVisualizer(chunkCoord, visualizerMaterial, _visualizerParent);
                _visualizerChunks[chunkCoord] = chunkData;
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

            VoxelVisualizerJob job = new VoxelVisualizerJob
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
                Colors = chunkData.Colors,
            };

            // Schedule the job and store the handle.
            chunkData.JobHandle = job.Schedule();
        }

        /// <summary>
        /// Clears a specific chunk's visualization mesh.
        /// </summary>
        /// <param name="chunkCoord">The chunk coordinate whose visualization should be cleared.</param>
        public void ClearChunkVisualization(ChunkCoord chunkCoord)
        {
            if (_visualizerChunks.TryGetValue(chunkCoord, out VisualizerChunkData chunkData))
            {
                // POOLING: Return to World.Instance.ChunkPool
                World.Instance.ChunkPool.ReturnVisualizer(chunkData);
                _visualizerChunks.Remove(chunkCoord);
            }
        }

        /// <summary>
        /// Destroys all visualization GameObjects.
        /// </summary>
        public void ClearAll()
        {
            foreach (VisualizerChunkData chunkData in _visualizerChunks.Values)
            {
                // POOLING: Return all
                World.Instance.ChunkPool.ReturnVisualizer(chunkData);
            }

            _visualizerChunks.Clear();
        }
    }

    /// <summary>
    /// Defines the mode of visualization for chunk data in the debug view.
    /// </summary>
    public enum DebugVisualizationMode
    {
        None,
        ActiveVoxels,
        Sunlight,
        Blocklight,
        FluidLevel,

        /// <summary>
        /// Diagnostic mode: shows sunlight values for ALL voxels (air, water, solids)
        /// in a 2-block band around chunk borders. Highlights light-step anomalies
        /// to diagnose the underwater shadow wall bug.
        /// </summary>
        SunlightChunkBorder,
    }
}
