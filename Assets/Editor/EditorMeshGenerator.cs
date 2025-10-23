using System.Collections.Generic;
using Data;
using UnityEngine;

namespace Editor
{
    public static class EditorMeshGenerator
    {
        // This new array correctly maps the vertex order for each face to the UV coordinate order.
        // This is the key to fixing the 3D preview textures.
        private static readonly int[,] FaceUvOrder = new int[6, 4]
        {
            { 0, 1, 2, 3 }, // Back Face
            { 2, 3, 0, 1 }, // Front Face
            { 0, 1, 2, 3 }, // Top Face
            { 0, 1, 2, 3 }, // Bottom Face
            { 1, 3, 0, 2 }, // Left Face
            { 0, 2, 1, 3 } // Right Face
        };


        /// <summary>
        /// Generates a Mesh for a given BlockType. This is for editor previews only.
        /// It mirrors the logic of MeshGenerationJob but uses standard C# lists.
        /// </summary>
        public static Mesh GenerateBlockMesh(BlockType blockType)
        {
            if (blockType == null) return null;

            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            var uvs = new List<Vector2>();

            //Center point for rotating the mesh around its true center
            Vector3 centerOffset = new Vector3(0.5f, 0.5f, 0.5f);

            // Case 1: Liquids
            if (blockType.fluidType != FluidType.None)
            {
                // Generate a full 6-sided mesh for the liquid, not just a plane.
                // A height of ~0.9 represents a source block that isn't completely full.
                float fluidHeight = 0.9f;

                // Define 8 vertices for a block, with the top 4 lowered to the fluidHeight.
                Vector3[] liquidVerts = new Vector3[8]
                {
                    new Vector3(0.0f, 0.0f, 0.0f),
                    new Vector3(1.0f, 0.0f, 0.0f),
                    new Vector3(1.0f, fluidHeight, 0.0f),
                    new Vector3(0.0f, fluidHeight, 0.0f),
                    new Vector3(0.0f, 0.0f, 1.0f),
                    new Vector3(1.0f, 0.0f, 1.0f),
                    new Vector3(1.0f, fluidHeight, 1.0f),
                    new Vector3(0.0f, fluidHeight, 1.0f),
                };

                // Loop through all 6 faces, just like a standard cube.
                for (int p = 0; p < 6; p++)
                {
                    int vertCount = vertices.Count;
                    int textureID = blockType.GetTextureID(p);
                    float yUv = Mathf.FloorToInt((float)textureID / VoxelData.TextureAtlasSizeInBlocks);
                    float xUv = textureID - (yUv * VoxelData.TextureAtlasSizeInBlocks);
                    xUv *= VoxelData.NormalizedBlockTextureSize;
                    yUv *= VoxelData.NormalizedBlockTextureSize;
                    yUv = 1f - yUv - VoxelData.NormalizedBlockTextureSize;
                    Vector2 baseUv = new Vector2(xUv, yUv);

                    for (int i = 0; i < 4; i++)
                    {
                        int vertIndex = VoxelData.VoxelTris[p * 4 + i];
                        // Use our custom liquidVerts instead of VoxelData.VoxelVerts
                        vertices.Add(liquidVerts[vertIndex] - centerOffset);

                        int uvIndex = FaceUvOrder[p, i];
                        Vector2 localUv = VoxelData.VoxelUvs[uvIndex];
                        uvs.Add(new Vector2(
                            baseUv.x + localUv.x * VoxelData.NormalizedBlockTextureSize,
                            baseUv.y + localUv.y * VoxelData.NormalizedBlockTextureSize
                        ));
                    }

                    triangles.Add(vertCount);
                    triangles.Add(vertCount + 1);
                    triangles.Add(vertCount + 2);
                    triangles.Add(vertCount + 2);
                    triangles.Add(vertCount + 1);
                    triangles.Add(vertCount + 3);
                }
            }
            // Case 2: The block has a custom mesh.
            else if (blockType.meshData != null)
            {
                // --- Iterate with an index to get the correct texture ID for each face ---
                for (int p = 0; p < blockType.meshData.faces.Length; p++)
                {
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
                        vertices.Add(vertData.position - centerOffset);

                        // --- Combine the base atlas UV with the vertex's local UV ---
                        uvs.Add(new Vector2(
                            baseUv.x + vertData.uv.x * VoxelData.NormalizedBlockTextureSize,
                            baseUv.y + vertData.uv.y * VoxelData.NormalizedBlockTextureSize
                        ));
                    }

                    foreach (var tri in face.triangles)
                    {
                        triangles.Add(vertCount + tri);
                    }
                }
            }
            // Case 3: The block is a standard cube.
            else
            {
                for (int p = 0; p < 6; p++) // Iterate through 6 faces
                {
                    int vertCount = vertices.Count;

                    // --- Calculate correct UVs for this specific face ---
                    int textureID = blockType.GetTextureID(p);
                    float yUv = Mathf.FloorToInt((float)textureID / VoxelData.TextureAtlasSizeInBlocks);
                    float xUv = textureID - (yUv * VoxelData.TextureAtlasSizeInBlocks);
                    xUv *= VoxelData.NormalizedBlockTextureSize;
                    yUv *= VoxelData.NormalizedBlockTextureSize;
                    yUv = 1f - yUv - VoxelData.NormalizedBlockTextureSize;
                    Vector2 baseUv = new Vector2(xUv, yUv);

                    for (int i = 0; i < 4; i++)
                    {
                        int vertIndex = VoxelData.VoxelTris[p * 4 + i];
                        vertices.Add(VoxelData.VoxelVerts[vertIndex] - centerOffset);

                        // Use the FaceUvOrder array to get the correct UV for this vertex.
                        int uvIndex = FaceUvOrder[p, i];
                        Vector2 localUv = VoxelData.VoxelUvs[uvIndex];
                        uvs.Add(new Vector2(
                            baseUv.x + localUv.x * VoxelData.NormalizedBlockTextureSize,
                            baseUv.y + localUv.y * VoxelData.NormalizedBlockTextureSize
                        ));
                    }

                    triangles.Add(vertCount);
                    triangles.Add(vertCount + 1);
                    triangles.Add(vertCount + 2);
                    triangles.Add(vertCount + 2);
                    triangles.Add(vertCount + 1);
                    triangles.Add(vertCount + 3);
                }
            }

            Mesh mesh = new Mesh();
            mesh.vertices = vertices.ToArray();
            mesh.triangles = triangles.ToArray();
            mesh.uv = uvs.ToArray();
            mesh.RecalculateNormals(); // Use RecalculateNormals as it's simpler for editor previews
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}