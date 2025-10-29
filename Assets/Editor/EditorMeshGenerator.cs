using System.Collections.Generic;
using System.Linq;
using Data;
using Helpers;
using Jobs.BurstData;
using Unity.Collections;
using UnityEngine;

namespace Editor
{
    public static class EditorMeshGenerator
    {
        /// <summary>
        /// Generates a Mesh for a given BlockType. This is for editor previews only.
        /// It now contains self-sufficient logic for all block types, including a simplified fluid preview.
        /// </summary>
        public static Mesh GenerateBlockMesh(BlockType blockType, List<BlockType> allBlockTypes, int fluidLevel = 0)
        {
            if (blockType == null) return null;

            Mesh mesh = new Mesh();
            var vertices = new List<Vector3>();
            var opaqueTriangles = new List<int>();
            var transparentTriangles = new List<int>();
            var uvs = new List<Vector2>();
            var colors = new List<Color>();
            var normals = new List<Vector3>();

            // Center point for rotating the mesh around its true center
            Vector3 centerOffset = new Vector3(-0.5f, -0.5f, -0.5f);

            // --- Unified path for all block types using VoxelMeshHelper ---
            var nativeVertices = new NativeList<Vector3>(Allocator.Temp);
            var nativeOpaqueTris = new NativeList<int>(Allocator.Temp);
            var nativeTransparentTris = new NativeList<int>(Allocator.Temp);
            var nativeFluidTris = new NativeList<int>(Allocator.Temp);
            var nativeUvs = new NativeList<Vector2>(Allocator.Temp);
            var nativeColors = new NativeList<Color>(Allocator.Temp);
            var nativeNormals = new NativeList<Vector3>(Allocator.Temp);
            int vertexIndex = 0;


            // Case 1: Fluid Block
            if (blockType.fluidType != FluidType.None)
            {
                // Create mock data needed by the helper that isn't available in the editor.
                var mockProps = new BlockTypeJobData(blockType);
                // Use fluid level 0 (full block) and full sunlight (15) for the preview.
                uint mockPackedData = BurstVoxelDataBitMapping.PackVoxelData(0, 15, 0, 1, (byte)fluidLevel);

                // For a simple, flat preview, an empty (default) array is sufficient.
                var mockNeighbors = new NativeArray<OptionalVoxelState>(10, Allocator.Temp);

                // Load fluid templates using the new helper - works perfectly in the editor.
                FluidTemplates fluidTemplates = ResourceLoader.LoadFluidTemplates();
                var templates = new NativeArray<float>(16, Allocator.Temp);
                templates.CopyFrom(blockType.fluidType == FluidType.WaterLike ? fluidTemplates.WaterVertexTemplates : fluidTemplates.LavaVertexTemplates);

                // Create temporary BlockTypeJobData from the editor's list.
                var blockTypesJobData = new NativeArray<BlockTypeJobData>(allBlockTypes.Select(bt => new BlockTypeJobData(bt)).ToArray(), Allocator.Temp);

                VoxelMeshHelper.GenerateFluidMeshData(Vector3Int.zero, mockPackedData, mockProps, in templates, in blockTypesJobData, mockNeighbors,
                    ref vertexIndex, ref nativeVertices, ref nativeFluidTris, ref nativeUvs, ref nativeColors, ref nativeNormals);

                // Dispose all temporary native arrays.
                mockNeighbors.Dispose();
                templates.Dispose();
                blockTypesJobData.Dispose();
            }
            // Case 2: Custom Mesh
            else if (blockType.meshData != null)
            {
                // This logic does not use native lists, so it remains unchanged for now.
                // It could be unified in a future pass.
                for (int p = 0; p < blockType.meshData.faces.Length; p++)
                {
                    if (p >= 6) continue; // Safety break for assets with >6 faces

                    FaceMeshData face = blockType.meshData.faces[p];
                    int vertCount = vertices.Count;

                    // --- Calculate the base UV from the atlas for this specific face ---
                    int textureID = blockType.GetTextureID(p);
                    float yUv = Mathf.FloorToInt((float)textureID / VoxelData.TextureAtlasSizeInBlocks);
                    float xUv = textureID - (yUv * VoxelData.TextureAtlasSizeInBlocks);
                    xUv *= VoxelData.NormalizedBlockTextureSize;
                    yUv *= VoxelData.NormalizedBlockTextureSize;
                    yUv = 1f - yUv - VoxelData.NormalizedBlockTextureSize;
                    Vector2 baseUv = new Vector2(xUv, yUv);

                    foreach (var vertData in face.vertData)
                    {
                        vertices.Add(vertData.position);
                        uvs.Add(new Vector2(
                            baseUv.x + vertData.uv.x * VoxelData.NormalizedBlockTextureSize,
                            baseUv.y + vertData.uv.y * VoxelData.NormalizedBlockTextureSize
                        ));
                        colors.Add(Color.white);
                        normals.Add(VoxelData.FaceChecks[p]);
                    }

                    // Add triangles to the correct sub-mesh list
                    foreach (var tri in face.triangles)
                    {
                        if (blockType.renderNeighborFaces)
                            transparentTriangles.Add(vertCount + tri);
                        else
                            opaqueTriangles.Add(vertCount + tri);
                    }
                }
            }
            // Case 3: Standard Solid Block
            else
            {
                for (int p = 0; p < 6; p++)
                {
                    int textureID = blockType.GetTextureID(p);
                    VoxelMeshHelper.GenerateStandardCubeFace(p, textureID, 1.0f, Vector3Int.zero, 0f,
                        ref vertexIndex, ref nativeVertices, ref nativeOpaqueTris, ref nativeTransparentTris, ref nativeUvs, ref nativeColors, ref nativeNormals,
                        blockType.renderNeighborFaces);
                }
            }

            // --- Post-processing for all native-generated meshes ---
            // If any native lists were populated, convert them to managed lists.
            if (nativeVertices.Length > 0)
            {
                vertices.AddRange(nativeVertices.AsArray());
                opaqueTriangles.AddRange(nativeOpaqueTris.AsArray());
                transparentTriangles.AddRange(nativeTransparentTris.AsArray());
                // Add fluid triangles to the transparent sub-mesh for rendering
                transparentTriangles.AddRange(nativeFluidTris.AsArray());
                uvs.AddRange(nativeUvs.AsArray());
                colors.AddRange(nativeColors.AsArray());
                normals.AddRange(nativeNormals.AsArray());
            }

            // Dispose all native collections
            nativeVertices.Dispose();
            nativeOpaqueTris.Dispose();
            nativeTransparentTris.Dispose();
            nativeFluidTris.Dispose();
            nativeUvs.Dispose();
            nativeColors.Dispose();
            nativeNormals.Dispose();

            // --- Final Mesh Assembly ---
            // Apply center offset to ALL vertices before creating the mesh
            for (int i = 0; i < vertices.Count; i++)
            {
                vertices[i] += centerOffset;
            }

            mesh.vertices = vertices.ToArray();
            mesh.uv = uvs.ToArray();
            mesh.colors = colors.ToArray();
            mesh.normals = normals.ToArray();

            mesh.subMeshCount = 2;
            mesh.SetTriangles(opaqueTriangles.ToArray(), 0);
            mesh.SetTriangles(transparentTriangles.ToArray(), 1);

            mesh.RecalculateBounds();

            return mesh;
        }
    }
}