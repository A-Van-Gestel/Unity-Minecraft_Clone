using Data;
using Jobs;
using Unity.Burst;
using Unity.Collections;
using UnityEngine;

namespace Helpers
{
    [BurstCompile]
    public static class VoxelMeshHelper
    {
        // This array correctly maps the vertex order for each face to the UV coordinate order.
        // This is the key to fixing the 3D preview textures and ensuring correct runtime textures.
        private static readonly int[,] FaceUvOrder = new int[6, 4]
        {
            { 0, 1, 2, 3 }, // Back Face
            { 2, 3, 0, 1 }, // Front Face
            { 0, 1, 2, 3 }, // Top Face
            { 0, 1, 2, 3 }, // Bottom Face
            { 1, 3, 0, 2 }, // Left Face
            { 0, 2, 1, 3 }  // Right Face
        };

        /// <summary>
        /// Generates a single face of a standard cube voxel.
        /// </summary>
        [BurstCompile]
        public static void GenerateStandardCubeFace(
            int faceIndex, int textureID, float lightLevel, Vector3Int position, float rotation,
            ref int vertexIndex,
            ref NativeList<Vector3> vertices, ref NativeList<int> triangles, ref NativeList<int> transparentTriangles,
            ref NativeList<Vector2> uvs, ref NativeList<Color> colors, ref NativeList<Vector3> normals,
            bool isTransparent)
        {
            // A face is a quad, which consists of 4 vertices.
            for (int i = 0; i < 4; i++)
            {
                int vertIndex = BurstVoxelData.VoxelTris.Data[faceIndex * 4 + i];
                Vector3 vertPos = BurstVoxelData.VoxelVerts.Data[vertIndex];

                // Rotate the vertex around the block's center if it has an orientation.
                Vector3 center = new Vector3(0.5f, 0.5f, 0.5f);
                Vector3 direction = vertPos - center;
                direction = Quaternion.Euler(0, rotation, 0) * direction;

                vertices.Add(position + direction + center);
                normals.Add(BurstVoxelData.FaceChecks.Data[faceIndex]);
                colors.Add(new Color(0, 0, 0, lightLevel));

                // Use the FaceUvOrder array to get the correct UV for this vertex.
                int uvIndex = FaceUvOrder[faceIndex, i];
                AddTexture(textureID, BurstVoxelData.VoxelUvs.Data[uvIndex], ref uvs);
            }

            // Add the triangle indices to the correct sub-mesh.
            if (isTransparent)
            {
                transparentTriangles.Add(vertexIndex);
                transparentTriangles.Add(vertexIndex + 1);
                transparentTriangles.Add(vertexIndex + 2);
                transparentTriangles.Add(vertexIndex + 2);
                transparentTriangles.Add(vertexIndex + 1);
                transparentTriangles.Add(vertexIndex + 3);
            }
            else
            {
                triangles.Add(vertexIndex);
                triangles.Add(vertexIndex + 1);
                triangles.Add(vertexIndex + 2);
                triangles.Add(vertexIndex + 2);
                triangles.Add(vertexIndex + 1);
                triangles.Add(vertexIndex + 3);
            }

            vertexIndex += 4;
        }

        /// <summary>
        /// Generates a single face of a custom mesh voxel.
        /// </summary>
        [BurstCompile]
        public static void GenerateCustomMeshFace(
            int faceIndex, int textureID, float lightLevel, Vector3Int position,
            int customMeshIndex,
            [ReadOnly] ref NativeArray<CustomMeshData> customMeshes,
            [ReadOnly] ref NativeArray<CustomFaceData> customFaces,
            [ReadOnly] ref NativeArray<CustomVertData> customVerts,
            [ReadOnly] ref NativeArray<int> customTris,
            ref int vertexIndex,
            ref NativeList<Vector3> vertices, ref NativeList<int> triangles, ref NativeList<int> transparentTriangles,
            ref NativeList<Vector2> uvs, ref NativeList<Color> colors, ref NativeList<Vector3> normals,
            bool isTransparent)
        {
            CustomMeshData meshData = customMeshes[customMeshIndex];
            CustomFaceData faceData = customFaces[meshData.faceStartIndex + faceIndex];

            int startVertCount = vertexIndex;

            // Add vertices and their data
            for (int i = 0; i < faceData.vertCount; i++)
            {
                CustomVertData vertData = customVerts[faceData.vertStartIndex + i];
                vertices.Add(position + vertData.position);
                normals.Add(BurstVoxelData.FaceChecks.Data[faceIndex]); // Assuming one normal per face for custom meshes
                colors.Add(new Color(0, 0, 0, lightLevel));
                AddTexture(textureID, vertData.uv, ref uvs);
            }
            
            // Add triangles to the correct list based on transparency.
            if (isTransparent)
            {
                for (int i = 0; i < faceData.triCount; i++)
                {
                    transparentTriangles.Add(startVertCount + customTris[faceData.triStartIndex + i]);
                }
            }
            else
            {
                for (int i = 0; i < faceData.triCount; i++)
                {
                    triangles.Add(startVertCount + customTris[faceData.triStartIndex + i]);
                }
            }

            vertexIndex += faceData.vertCount;
        }


        /// <summary>
        /// A helper to add texture coordinates to the UV list.
        /// </summary>
        private static void AddTexture(int textureID, Vector2 uv, ref NativeList<Vector2> uvs)
        {
            float y = Mathf.FloorToInt((float)textureID / VoxelData.TextureAtlasSizeInBlocks);
            float x = textureID - (y * VoxelData.TextureAtlasSizeInBlocks);

            x *= VoxelData.NormalizedBlockTextureSize;
            y *= VoxelData.NormalizedBlockTextureSize;

            y = 1f - y - VoxelData.NormalizedBlockTextureSize; // To start reading the atlas from the top left

            x += VoxelData.NormalizedBlockTextureSize * uv.x;
            y += VoxelData.NormalizedBlockTextureSize * uv.y;

            uvs.Add(new Vector2(x, y));
        }
    }
}