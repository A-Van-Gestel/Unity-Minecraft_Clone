using System.Collections.Generic;
using UnityEngine;

namespace DebugVisualizations
{
    public class VoxelVisualizer : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("The material using the DebugVoxelShader.")]
        public Material visualizerMaterial;

        // A dictionary to hold the GameObject for each chunk's visualization mesh.
        private readonly Dictionary<ChunkCoord, GameObject> _visualizerChunks = new Dictionary<ChunkCoord, GameObject>();
        private Transform _visualizerParent;

        private void Start()
        {
            // Create a parent object to keep the hierarchy clean.
            _visualizerParent = new GameObject("VoxelVisualizerMeshes").transform;
            _visualizerParent.SetParent(transform, false);
        }

        /// <summary>
        /// Updates or creates the visualization mesh for a specific chunk using an optimized face-culling algorithm.
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
            if (!_visualizerChunks.TryGetValue(coord, out GameObject chunkObject))
            {
                chunkObject = new GameObject($"Visualizer_{coord.X}_{coord.Z}");
                chunkObject.transform.SetParent(_visualizerParent);
                chunkObject.transform.position = new Vector3(coord.X * VoxelData.ChunkWidth, 0, coord.Z * VoxelData.ChunkWidth);
                chunkObject.AddComponent<MeshFilter>();
                var mr = chunkObject.AddComponent<MeshRenderer>();
                mr.material = visualizerMaterial;
                _visualizerChunks[coord] = chunkObject;
            }

            MeshFilter meshFilter = chunkObject.GetComponent<MeshFilter>();

            // If there's nothing to draw, clear the mesh and return.
            if (voxelsToDraw == null || voxelsToDraw.Count == 0)
            {
                meshFilter.mesh = null;
                return;
            }

            // --- Optimized Mesh Generation with Cross-Chunk Face Culling ---
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            var colors = new List<Color>();
            int vertexIndex = 0;

            const float cubeScale = 0.8f; // Slightly smaller to prevent z-fighting
            float offset = (1f - cubeScale) / 2f;
            var cubeOffset = new Vector3(offset, offset, offset);

            // Iterate through each voxel that needs to be visualized.
            foreach (var voxel in voxelsToDraw)
            {
                Vector3Int localPos = voxel.Key;
                Color color = voxel.Value;

                for (int faceIndex = 0; faceIndex < 6; faceIndex++)
                {
                    Vector3Int neighborPos = localPos + VoxelData.FaceChecks[faceIndex];

                    // --- CULLING LOGIC ---
                    bool shouldCull = false;

                    // 1. Check for neighbor within the same chunk.
                    if (voxelsToDraw.ContainsKey(neighborPos))
                    {
                        shouldCull = true;
                    }
                    // 2. If not culled, check neighbor chunks for border faces.
                    else
                    {
                        // Check North (+Z) border
                        if (neighborPos.z >= VoxelData.ChunkWidth && northVoxels != null && northVoxels.ContainsKey(new Vector3Int(neighborPos.x, neighborPos.y, 0)))
                            shouldCull = true;
                        // Check South (-Z) border
                        else if (neighborPos.z < 0 && southVoxels != null && southVoxels.ContainsKey(new Vector3Int(neighborPos.x, neighborPos.y, VoxelData.ChunkWidth - 1)))
                            shouldCull = true;
                        // Check East (+X) border
                        else if (neighborPos.x >= VoxelData.ChunkWidth && eastVoxels != null && eastVoxels.ContainsKey(new Vector3Int(0, neighborPos.y, neighborPos.z)))
                            shouldCull = true;
                        // Check West (-X) border
                        else if (neighborPos.x < 0 && westVoxels != null && westVoxels.ContainsKey(new Vector3Int(VoxelData.ChunkWidth - 1, neighborPos.y, neighborPos.z)))
                            shouldCull = true;
                    }

                    if (shouldCull)
                    {
                        continue; // Skip face generation.
                    }


                    // --- If we get here, the face is visible and should be generated ---

                    // Add the 4 vertices for this face.
                    for (int i = 0; i < 4; i++)
                    {
                        int vertIndex = VoxelData.VoxelTris[faceIndex * 4 + i];
                        Vector3 vert = VoxelData.VoxelVerts[vertIndex];

                        // Scale and center the cube.
                        vert *= cubeScale;
                        vert += cubeOffset;

                        vertices.Add(localPos + vert);
                        colors.Add(color);
                    }

                    // Add the 2 triangles for this face.
                    triangles.Add(vertexIndex);
                    triangles.Add(vertexIndex + 1);
                    triangles.Add(vertexIndex + 2);
                    triangles.Add(vertexIndex + 2);
                    triangles.Add(vertexIndex + 1);
                    triangles.Add(vertexIndex + 3);

                    vertexIndex += 4;
                }
            }

            Mesh mesh = new Mesh();
            mesh.vertices = vertices.ToArray();
            mesh.triangles = triangles.ToArray();
            mesh.colors = colors.ToArray();
            mesh.RecalculateBounds(); // Bounds are important for visibility.

            meshFilter.mesh = mesh;
        }

        /// <summary>
        /// Clears a specific chunk's visualization mesh.
        /// </summary>
        public void ClearChunkVisualization(ChunkCoord coord)
        {
            if (_visualizerChunks.TryGetValue(coord, out GameObject chunkObject) && chunkObject != null)
            {
                chunkObject.GetComponent<MeshFilter>().mesh = null;
            }
        }

        /// <summary>
        /// Destroys all visualization GameObjects.
        /// </summary>
        public void ClearAll()
        {
            foreach (var chunkObject in _visualizerChunks.Values)
            {
                if (chunkObject != null)
                {
                    Destroy(chunkObject);
                }
            }

            _visualizerChunks.Clear();
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