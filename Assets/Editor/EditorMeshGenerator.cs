using System.Collections.Generic;
using Data;
using Helpers;
using Unity.Collections;
using UnityEngine;

namespace Editor
{
    public static class EditorMeshGenerator
    {
        /// <summary>
        /// Generates a Mesh for a given BlockType. This is for editor previews only.
        /// It mirrors the logic of MeshGenerationJob but uses standard C# lists.
        /// </summary>
        public static Mesh GenerateBlockMesh(BlockType blockType)
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


            // Case 1: Fluid Block (generates mesh with vertex colors)
            if (blockType.fluidType != FluidType.None)
            {
                float liquidType = (blockType.fluidType == FluidType.Lava) ? 1.0f : 0.0f;
                // Pack data: r=type, g=shore(0), b=unused(0), a=light(1)
                Color liquidColor = new Color(liquidType, 0, 0, 1);

                for (int p = 0; p < 6; p++) // Iterate through 6 faces
                {
                    int vertCount = vertices.Count;

                    for (int i = 0; i < 4; i++)
                    {
                        int vertIndex = VoxelData.VoxelTris[p * 4 + i];
                        vertices.Add(VoxelData.VoxelVerts[vertIndex] + centerOffset);
                        normals.Add(VoxelData.FaceChecks[p]);
                        colors.Add(liquidColor); // Add the packed color data
                        uvs.Add(VoxelData.VoxelUvs[i]); // Add dummy UVs
                    }

                    // All fluid faces go to a transparent sub-mesh
                    transparentTriangles.Add(vertCount);
                    transparentTriangles.Add(vertCount + 1);
                    transparentTriangles.Add(vertCount + 2);
                    transparentTriangles.Add(vertCount + 2);
                    transparentTriangles.Add(vertCount + 1);
                    transparentTriangles.Add(vertCount + 3);
                }
            }
            // Case 2: Custom Mesh
            else if (blockType.meshData != null)
            {
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
                        vertices.Add(vertData.position + centerOffset);
                        // Combine the base atlas UV with the vertex's local UV
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
                var nativeVertices = new NativeList<Vector3>(Allocator.Temp);
                var nativeOpaqueTris = new NativeList<int>(Allocator.Temp);
                var nativeTransparentTris = new NativeList<int>(Allocator.Temp);
                var nativeUvs = new NativeList<Vector2>(Allocator.Temp);
                var nativeColors = new NativeList<Color>(Allocator.Temp);
                var nativeNormals = new NativeList<Vector3>(Allocator.Temp);
                int vertexIndex = 0;

                for (int p = 0; p < 6; p++)
                {
                    int textureID = blockType.GetTextureID(p);
                    // Pass BOTH triangle lists to the helper
                    VoxelMeshHelper.GenerateStandardCubeFace(p, textureID, 1.0f, Vector3Int.zero, 0f,
                        ref vertexIndex, ref nativeVertices, ref nativeOpaqueTris, ref nativeTransparentTris, ref nativeUvs, ref nativeColors, ref nativeNormals,
                        blockType.renderNeighborFaces);
                }

                // Adjust all generated vertices to be centered for the preview camera.
                for (int i = 0; i < nativeVertices.Length; i++)
                {
                    nativeVertices[i] += centerOffset;
                }

                // Convert from native to managed lists
                vertices.AddRange(nativeVertices.AsArray());
                opaqueTriangles.AddRange(nativeOpaqueTris.AsArray());
                transparentTriangles.AddRange(nativeTransparentTris.AsArray());
                uvs.AddRange(nativeUvs.AsArray());
                colors.AddRange(nativeColors.AsArray());
                normals.AddRange(nativeNormals.AsArray());

                // Dispose native collections
                nativeVertices.Dispose();
                nativeOpaqueTris.Dispose();
                nativeTransparentTris.Dispose();
                nativeUvs.Dispose();
                nativeColors.Dispose();
                nativeNormals.Dispose();
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