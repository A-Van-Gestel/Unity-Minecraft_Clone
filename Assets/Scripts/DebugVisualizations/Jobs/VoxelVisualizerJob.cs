using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace DebugVisualizations.Jobs
{
    /// <summary>
    /// A Burst-compiled job that generates the mesh data required to visualize specific voxels within a chunk.
    /// Automatically culls faces between adjacent solid visualized blocks to optimize the generated geometry.
    /// </summary>
    // TODO: I believe this could be converted into an IJobParallelFor
    [BurstCompile(FloatPrecision = FloatPrecision.Standard, FloatMode = FloatMode.Fast)]
    public struct VoxelVisualizerJob : IJob
    {
        // --- INPUT DATA ---
        [ReadOnly]
        public NativeHashMap<Vector3Int, Color> VoxelsToDraw;

        [ReadOnly]
        public NativeHashMap<Vector3Int, Color> NorthVoxels;

        [ReadOnly]
        public NativeHashMap<Vector3Int, Color> SouthVoxels;

        [ReadOnly]
        public NativeHashMap<Vector3Int, Color> EastVoxels;

        [ReadOnly]
        public NativeHashMap<Vector3Int, Color> WestVoxels;

        // Static pre-cached data for high-speed access inside the job
        [ReadOnly]
        public NativeArray<Vector3Int> FaceChecks;

        [ReadOnly]
        public NativeArray<int> VoxelTris;

        [ReadOnly]
        public NativeArray<Vector3> VoxelVerts;

        // --- OUTPUT DATA ---
        public NativeList<Vector3> Vertices;
        public NativeList<int> Triangles;
        public NativeList<Color> Colors;

        /// <summary>
        /// Executes the visualization mesh generation algorithm.
        /// Iterates through the provided dictionaries and builds faces for non-occluded voxels.
        /// </summary>
        public void Execute()
        {
            const float cubeScale = 0.8f;
            const float offset = (1f - cubeScale) / 2f;
            var cubeOffset = new Vector3(offset, offset, offset);
            int vertexIndex = 0;

            // Instead of iterating the dictionary, we iterate its key array for determinism.
            var keys = VoxelsToDraw.GetKeyArray(Allocator.Temp);

            foreach (Vector3Int localPos in keys)
            {
                Color color = VoxelsToDraw[localPos];

                for (int faceIndex = 0; faceIndex < 6; faceIndex++)
                {
                    Vector3Int neighborPos = localPos + FaceChecks[faceIndex];

                    // --- CULLING LOGIC ---
                    bool shouldCull = false;
                    if (VoxelsToDraw.ContainsKey(neighborPos))
                    {
                        shouldCull = true;
                    }
                    else
                    {
                        if (neighborPos.z >= VoxelData.ChunkWidth && NorthVoxels.IsCreated && NorthVoxels.ContainsKey(new Vector3Int(neighborPos.x, neighborPos.y, 0)))
                            shouldCull = true;
                        else if (neighborPos.z < 0 && SouthVoxels.IsCreated && SouthVoxels.ContainsKey(new Vector3Int(neighborPos.x, neighborPos.y, VoxelData.ChunkWidth - 1)))
                            shouldCull = true;
                        else if (neighborPos.x >= VoxelData.ChunkWidth && EastVoxels.IsCreated && EastVoxels.ContainsKey(new Vector3Int(0, neighborPos.y, neighborPos.z)))
                            shouldCull = true;
                        else if (neighborPos.x < 0 && WestVoxels.IsCreated && WestVoxels.ContainsKey(new Vector3Int(VoxelData.ChunkWidth - 1, neighborPos.y, neighborPos.z)))
                            shouldCull = true;
                    }

                    if (shouldCull) continue;

                    // --- FACE GENERATION ---
                    for (int j = 0; j < 4; j++)
                    {
                        int vertIndex = VoxelTris[faceIndex * 4 + j];
                        Vector3 vert = VoxelVerts[vertIndex];

                        vert *= cubeScale;
                        vert += cubeOffset;

                        Vertices.Add(localPos + vert);
                        Colors.Add(color);
                    }

                    Triangles.Add(vertexIndex);
                    Triangles.Add(vertexIndex + 1);
                    Triangles.Add(vertexIndex + 2);
                    Triangles.Add(vertexIndex + 2);
                    Triangles.Add(vertexIndex + 1);
                    Triangles.Add(vertexIndex + 3);

                    vertexIndex += 4;
                }
            }

            keys.Dispose();
        }
    }
}
