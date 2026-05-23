using Data;
using Helpers;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Rendering;

namespace Editor.WorldTools
{
    public partial class ChunkPreview3DWindow
    {
        private static readonly VertexAttributeDescriptor[] s_vertexLayout =
        {
            new VertexAttributeDescriptor(VertexAttribute.Position),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 4, stream: 1),
            new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.Float32, 4, stream: 2),
            new VertexAttributeDescriptor(VertexAttribute.Normal, stream: 3),
        };

        /// <summary>
        /// Converts a <see cref="MeshDataJobOutput"/> into Unity <see cref="Mesh"/> objects, one per
        /// non-empty section. Section meshes are stored in <see cref="_sectionMeshes"/> for rendering.
        /// </summary>
        private void ConvertMeshOutput(ChunkCoord chunkCoord, MeshDataJobOutput output)
        {
            // Run a fast Burst job to adjust coordinate spaces from Chunk-Space to Section-Space.
            // This modifies the data in-place efficiently.
            PostProcessMeshJob postProcessJob = new PostProcessMeshJob
            {
                Vertices = output.Vertices,
                OpaqueTris = output.Triangles,
                TransparentTris = output.TransparentTriangles,
                FluidTris = output.FluidTriangles,
                Stats = output.SectionStats,
                SectionHeight = ChunkMath.SECTION_SIZE,
            };
            postProcessJob.Schedule().Complete();

            // Center the preview grid around the origin
            float centerOffset = _chunkRadius * VoxelData.ChunkWidth * 0.5f;
            // Visible chunks start at index 1 (border at 0), so subtract 1 chunk worth
            float chunkWorldX = (chunkCoord.X - 1) * VoxelData.ChunkWidth - centerOffset;
            float chunkWorldZ = (chunkCoord.Z - 1) * VoxelData.ChunkWidth - centerOffset;

            int sectionCount = output.SectionStats.Length;
            for (int s = 0; s < sectionCount; s++)
            {
                MeshSectionStats stats = output.SectionStats[s];
                int totalIndices = stats.OpaqueTriCount + stats.TransparentTriCount + stats.FluidTriCount;
                if (stats.VertexCount == 0 || totalIndices == 0) continue;

                Mesh mesh = new Mesh();
                mesh.name = $"Preview_C{chunkCoord.X}_{chunkCoord.Z}_S{s}";

                const MeshUpdateFlags flags =
                    MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds;

                // --- Vertex buffer ---
                mesh.SetVertexBufferParams(stats.VertexCount, s_vertexLayout);
                mesh.SetVertexBufferData(output.Vertices.AsArray(), stats.VertexStartIndex, 0,
                    stats.VertexCount, 0, flags);
                mesh.SetVertexBufferData(output.Uvs.AsArray(), stats.VertexStartIndex, 0,
                    stats.VertexCount, 1, flags);
                mesh.SetVertexBufferData(output.Colors.AsArray(), stats.VertexStartIndex, 0,
                    stats.VertexCount, 2, flags);
                mesh.SetVertexBufferData(output.Normals.AsArray(), stats.VertexStartIndex, 0,
                    stats.VertexCount, 3, flags);

                // --- Index buffer with submeshes ---
                mesh.SetIndexBufferParams(totalIndices, IndexFormat.UInt32);

                int currentOffset = 0;
                int subMeshCount = 0;

                NativeArray<SubMeshDescriptor> descriptors = new NativeArray<SubMeshDescriptor>(3, Allocator.Temp);

                if (stats.OpaqueTriCount > 0)
                {
                    mesh.SetIndexBufferData(output.Triangles.AsArray(),
                        stats.OpaqueTriStartIndex, currentOffset, stats.OpaqueTriCount, flags);
                    descriptors[subMeshCount] = new SubMeshDescriptor(currentOffset, stats.OpaqueTriCount)
                    {
                        firstVertex = 0,
                        vertexCount = stats.VertexCount,
                    };
                    currentOffset += stats.OpaqueTriCount;
                    subMeshCount++;
                }

                if (stats.TransparentTriCount > 0)
                {
                    mesh.SetIndexBufferData(output.TransparentTriangles.AsArray(),
                        stats.TransparentTriStartIndex, currentOffset, stats.TransparentTriCount, flags);
                    descriptors[subMeshCount] = new SubMeshDescriptor(currentOffset, stats.TransparentTriCount)
                    {
                        firstVertex = 0,
                        vertexCount = stats.VertexCount,
                    };
                    currentOffset += stats.TransparentTriCount;
                    subMeshCount++;
                }

                if (stats.FluidTriCount > 0)
                {
                    mesh.SetIndexBufferData(output.FluidTriangles.AsArray(),
                        stats.FluidTriStartIndex, currentOffset, stats.FluidTriCount, flags);
                    descriptors[subMeshCount] = new SubMeshDescriptor(currentOffset, stats.FluidTriCount)
                    {
                        firstVertex = 0,
                        vertexCount = stats.VertexCount,
                    };
                    subMeshCount++;
                }

                mesh.SetSubMeshes(descriptors, 0, subMeshCount, flags);
                descriptors.Dispose();

                mesh.RecalculateBounds();

                float sectionY = s * ChunkMath.SECTION_SIZE;
                _sectionMeshes.Add(new SectionMeshEntry
                {
                    Mesh = mesh,
                    WorldPosition = new Vector3(chunkWorldX, sectionY - VoxelData.ChunkHeight * 0.5f, chunkWorldZ),
                    SubMeshCount = subMeshCount,
                    HasOpaque = stats.OpaqueTriCount > 0,
                    HasTransparent = stats.TransparentTriCount > 0,
                    HasFluid = stats.FluidTriCount > 0,
                });
            }
        }

        /// <summary>
        /// Draws all converted section meshes using <see cref="MeshPreviewWidget.DrawMeshDirect"/>.
        /// Assigns the correct runtime material (opaque, transparent, fluid) to each submesh
        /// based on the flags set during <see cref="ConvertMeshOutput"/>.
        /// </summary>
        private void DrawAllSectionMeshes()
        {
            if (_meshPreviewWidget == null || _editorOpaqueMaterial == null) return;

            // Ensure the runtime shaders have valid lighting globals for editor preview.
            SetPreviewShaderGlobals();

            foreach (SectionMeshEntry entry in _sectionMeshes)
            {
                if (entry.Mesh == null) continue;

                Matrix4x4 localToWorld = Matrix4x4.Translate(entry.WorldPosition);

                // Submeshes are added in order: opaque → transparent → fluid.
                // Walk the submesh indices and assign the matching material.
                int sub = 0;

                if (entry.HasOpaque)
                {
                    _meshPreviewWidget.DrawMeshDirect(entry.Mesh, localToWorld, _editorOpaqueMaterial, sub);
                    sub++;
                }

                if (entry.HasTransparent)
                {
                    _meshPreviewWidget.DrawMeshDirect(entry.Mesh, localToWorld, _editorTransparentMaterial, sub);
                    sub++;
                }

                if (entry.HasFluid)
                {
                    _meshPreviewWidget.DrawMeshDirect(entry.Mesh, localToWorld, _editorFluidMaterial, sub);
                }
            }
        }

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
}
