using Data;
using Helpers;
using Jobs;
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
            new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3, stream: 3),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.UNorm8, 4, stream: 3),
        };

        /// <summary>
        /// Converts a <see cref="MeshDataJobOutput"/> into Unity <see cref="Mesh"/> objects, one per
        /// non-empty section. Section meshes are stored in <see cref="_sectionMeshes"/> for rendering.
        /// </summary>
        private void ConvertMeshOutput(ChunkCoord chunkCoord, MeshDataJobOutput output)
        {
            // Run a fast Burst job to adjust coordinate spaces from Chunk-Space to Section-Space
            // and interleave Normal + LightData into stream 3.
            MeshPostProcessJob postProcessJob = new MeshPostProcessJob
            {
                Vertices = output.Vertices,
                OpaqueTris = output.Triangles,
                TransparentTris = output.TransparentTriangles,
                FluidTris = output.FluidTriangles,
                Stats = output.SectionStats,
                Normals = output.Normals,
                LightData = output.LightData,
                InterleavedStream3 = output.InterleavedStream3,
                SectionHeight = ChunkMath.SECTION_SIZE,
            };
            postProcessJob.Schedule().Complete();

            // Center the preview grid around the origin.
            // There are (_chunkRadius * 2) visible chunks, so total width is (_chunkRadius * 2 * 16).
            // Half of that is _chunkRadius * 16.
            float centerOffset = _chunkRadius * VoxelData.ChunkWidth;
            // Visible chunks start at index 1 (border at 0), so subtract 1 chunk worth
            int localX = chunkCoord.X - _gridStartX;
            int localZ = chunkCoord.Z - _gridStartZ;
            float chunkWorldX = (localX - 1) * VoxelData.ChunkWidth - centerOffset;
            float chunkWorldZ = (localZ - 1) * VoxelData.ChunkWidth - centerOffset;

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
                mesh.SetVertexBufferData(output.InterleavedStream3.AsArray(), stats.VertexStartIndex, 0,
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
                    HasOpaque = stats.OpaqueTriCount > 0,
                    HasTransparent = stats.TransparentTriCount > 0,
                    HasFluid = stats.FluidTriCount > 0,
                });
            }
        }

        /// <summary>
        /// Computes the AABB center of all visible section meshes and sets it as the
        /// <see cref="MeshPreviewWidget.PivotOffset"/> so the camera orbits around visible content.
        /// </summary>
        private void UpdatePivotOffset()
        {
            if (_meshPreviewWidget == null || _sectionMeshes.Count == 0)
            {
                if (_meshPreviewWidget != null) _meshPreviewWidget.PivotOffset = Vector3.zero;
                return;
            }

            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);

            foreach (SectionMeshEntry entry in _sectionMeshes)
            {
                if (entry.Mesh == null) continue;
                Bounds bounds = entry.Mesh.bounds;
                min = Vector3.Min(min, entry.WorldPosition + bounds.min);
                max = Vector3.Max(max, entry.WorldPosition + bounds.max);
            }

            if (min.x > max.x)
            {
                _meshPreviewWidget.PivotOffset = Vector3.zero;
                return;
            }

            _meshPreviewWidget.PivotOffset = (min + max) * 0.5f;
        }

        /// <summary>
        /// Draws all converted section meshes using <see cref="MeshPreviewWidget.DrawMeshDirect"/>.
        /// Assigns the correct runtime material (opaque, transparent, fluid) to each submesh
        /// based on the flags set during <see cref="ConvertMeshOutput"/>.
        /// </summary>
        private void DrawAllSectionMeshes()
        {
            if (_meshPreviewWidget == null || _editorOpaqueMaterial == null) return;

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
    }
}
