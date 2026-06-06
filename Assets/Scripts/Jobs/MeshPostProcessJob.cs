using Data;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace Jobs
{
    /// <summary>
    /// Burst-compiled job that post-processes raw <see cref="MeshGenerationJob"/> output:
    /// interleaves Normal + LightData into a single GPU stream, adjusts vertex positions
    /// from chunk-space to section-space, and relativizes triangle indices per section.
    /// Used by both the runtime (<see cref="Chunk.ApplyMeshData"/>) and the editor
    /// (<see cref="ChunkPreview3DWindow"/>).
    /// </summary>
    [BurstCompile]
    public struct MeshPostProcessJob : IJob
    {
        public NativeList<Vector3> Vertices;
        public NativeList<int> OpaqueTris;
        public NativeList<int> TransparentTris;
        public NativeList<int> FluidTris;

        [ReadOnly]
        public NativeArray<MeshSectionStats> Stats;

        [ReadOnly]
        public NativeList<Vector3> Normals;

        [ReadOnly]
        public NativeList<Color32> LightData;

        public NativeList<NormalLightVertex> InterleavedStream3;

        public int SectionHeight;

        public void Execute()
        {
            // Build interleaved Normal + LightData for GPU stream 3 upload.
            // Done here (Burst-compiled) instead of on the main thread.
            int totalVerts = Vertices.Length;
            InterleavedStream3.ResizeUninitialized(totalVerts);
            for (int v = 0; v < totalVerts; v++)
            {
                InterleavedStream3[v] = new NormalLightVertex
                {
                    Normal = Normals[v],
                    LightData = LightData[v],
                };
            }

            // We iterate sections inside the job to avoid overhead of scheduling many tiny jobs
            for (int i = 0; i < Stats.Length; i++)
            {
                MeshSectionStats s = Stats[i];
                if (s.VertexCount == 0) continue;

                float yOffset = i * SectionHeight;
                int vertStart = s.VertexStartIndex;

                // Adjust Vertices: Subtract section Y offset so they are local to the Section GameObject
                for (int v = 0; v < s.VertexCount; v++)
                {
                    int index = vertStart + v;
                    Vector3 pos = Vertices[index];
                    pos.y -= yOffset;
                    Vertices[index] = pos;
                }

                // Adjust Indices: Relativize indices to start at 0 for this section.
                // The indices currently point to the 'allVerts' array.
                // We need them to point to the start of the section slice.
                int offset = -vertStart;
                AdjustIndices(OpaqueTris, s.OpaqueTriStartIndex, s.OpaqueTriCount, offset);
                AdjustIndices(TransparentTris, s.TransparentTriStartIndex, s.TransparentTriCount, offset);
                AdjustIndices(FluidTris, s.FluidTriStartIndex, s.FluidTriCount, offset);
            }
        }

        private static void AdjustIndices(NativeList<int> indices, int start, int count, int offset)
        {
            for (int k = 0; k < count; k++)
            {
                indices[start + k] += offset;
            }
        }
    }
}
