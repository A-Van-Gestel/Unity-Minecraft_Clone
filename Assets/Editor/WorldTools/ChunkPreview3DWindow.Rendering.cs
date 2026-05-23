using Data;
using Helpers;
using Unity.Collections;
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
            // Center the preview grid around the origin
            float centerOffset = _chunkRadius * VoxelData.ChunkWidth * 0.5f;
            // Visible chunks start at index 1 (border at 0), so subtract 1 chunk worth
            float chunkWorldX = (chunkCoord.X - 1) * VoxelData.ChunkWidth - centerOffset;
            float chunkWorldZ = (chunkCoord.Z - 1) * VoxelData.ChunkWidth - centerOffset;

            int sectionCount = output.SectionStats.Length;
            for (int s = 0; s < sectionCount; s++)
            {
                MeshSectionStats stats = output.SectionStats[s];
                if (stats.VertexCount == 0) continue;

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
                int totalIndices = stats.OpaqueTriCount + stats.TransparentTriCount + stats.FluidTriCount;
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
            if (_meshPreviewWidget == null || _opaqueMaterial == null) return;

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
                    _meshPreviewWidget.DrawMeshDirect(entry.Mesh, localToWorld, _opaqueMaterial, sub);
                    sub++;
                }

                if (entry.HasTransparent)
                {
                    _meshPreviewWidget.DrawMeshDirect(entry.Mesh, localToWorld,
                        _transparentMaterial != null ? _transparentMaterial : _opaqueMaterial, sub);
                    sub++;
                }

                if (entry.HasFluid)
                {
                    _meshPreviewWidget.DrawMeshDirect(entry.Mesh, localToWorld,
                        _fluidMaterial != null ? _fluidMaterial : _opaqueMaterial, sub);
                }
            }
        }
    }
}
