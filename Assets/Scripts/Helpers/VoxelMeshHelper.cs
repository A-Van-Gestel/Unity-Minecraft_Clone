using Data;
using Jobs;
using Jobs.BurstData;
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
        private static readonly int[] FaceUvOrder = new int[24]
        {
            0, 1, 2, 3, // Back Face
            2, 3, 0, 1, // Front Face
            0, 1, 2, 3, // Top Face
            0, 1, 2, 3, // Bottom Face
            1, 3, 0, 2, // Left Face
            0, 2, 1, 3  // Right Face
        };

        /// <summary>
        /// A helper to add texture coordinates to the UV list.
        /// </summary>
        private static void AddTexture(int textureID, Vector2 uv, ref NativeList<Vector2> uvs)
        {
            float y = Mathf.FloorToInt((float)textureID / VoxelData.TextureAtlasSizeInBlocks);
            float x = textureID - y * VoxelData.TextureAtlasSizeInBlocks;

            x *= VoxelData.NormalizedBlockTextureSize;
            y *= VoxelData.NormalizedBlockTextureSize;

            y = 1f - y - VoxelData.NormalizedBlockTextureSize; // To start reading the atlas from the top left

            x += VoxelData.NormalizedBlockTextureSize * uv.x;
            y += VoxelData.NormalizedBlockTextureSize * uv.y;

            uvs.Add(new Vector2(x, y));
        }

        /// <summary>
        /// Generates a single face of a standard cube voxel.
        /// </summary>
        [BurstCompile]
        public static void GenerateStandardCubeFace(
            int faceIndex, int textureID, float lightLevel, in Vector3Int position, float rotation,
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
                int uvIndex = FaceUvOrder[faceIndex * 4 + i];
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
            int faceIndex, int textureID, float lightLevel, in Vector3Int position, float rotation,
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
            CustomFaceData faceData = customFaces[meshData.FaceStartIndex + faceIndex];

            int startVertCount = vertexIndex;

            // Add vertices and their data
            for (int i = 0; i < faceData.VertCount; i++)
            {
                CustomVertData vertData = customVerts[faceData.VertStartIndex + i];
                Vector3 vertPos = vertData.Position;

                // Rotate the vertex around the block's center (0.5, 0.5, 0.5)
                Vector3 center = new Vector3(0.5f, 0.5f, 0.5f);
                Vector3 direction = vertPos - center;
                direction = Quaternion.Euler(0, rotation, 0) * direction;

                vertices.Add(position + direction + center);

                normals.Add(BurstVoxelData.FaceChecks.Data[faceIndex]); // Assuming one normal per face for custom meshes
                colors.Add(new Color(0, 0, 0, lightLevel));
                AddTexture(textureID, vertData.UV, ref uvs);
            }

            // Add triangles to the correct list based on transparency.
            if (isTransparent)
            {
                for (int i = 0; i < faceData.TriCount; i++)
                {
                    transparentTriangles.Add(startVertCount + customTris[faceData.TriStartIndex + i]);
                }
            }
            else
            {
                for (int i = 0; i < faceData.TriCount; i++)
                {
                    triangles.Add(startVertCount + customTris[faceData.TriStartIndex + i]);
                }
            }

            vertexIndex += faceData.VertCount;
        }


        /// <summary>
        /// Generates a custom mesh for a fluid voxel, creating a sloped surface based on its fluid level
        /// and the levels of its neighbors. This method uses pre-computed vertex height templates for high performance.
        /// </summary>
        [BurstCompile]
        public static void GenerateFluidMeshData(
            in Vector3Int pos,
            uint packedData,
            in BlockTypeJobData props,
            in NativeArray<float> templates,
            in NativeArray<BlockTypeJobData> blockTypes,
            [ReadOnly] in NativeArray<OptionalVoxelState> neighbors, // 9 neighbors: N, E, S, W, NE, SE, SW, NW, Above, Below
            ref int vertexIndex,
            ref NativeList<Vector3> vertices, ref NativeList<int> fluidTriangles,
            ref NativeList<Vector2> uvs, ref NativeList<Color> colors, ref NativeList<Vector3> normals)
        {
            // Unpack neighbor states
            OptionalVoxelState n_N = neighbors[0], n_E = neighbors[1], n_S = neighbors[2], n_W = neighbors[3];
            OptionalVoxelState n_NE = neighbors[4], n_SE = neighbors[5], n_SW = neighbors[6], n_NW = neighbors[7];
            OptionalVoxelState above = neighbors[8], below = neighbors[9];

            // --- 1. DETERMINE SHADER FLAGS ---
            float liquidType = props.FluidShaderID;
            float shorelineFlag = 0.0f;

            // Check horizontal neighbors (N, E, S, W) for "shoreline" effect
            for (int i = 0; i < 4; i++)
            {
                OptionalVoxelState neighbor = neighbors[i];
                // Neighboring voxels need to be solid...
                if (neighbor.HasValue && blockTypes[neighbor.State.id].IsSolid && blockTypes[neighbor.State.id].FluidType == FluidType.None)
                {
                    // But voxel above should not be a fluid
                    if (above.HasValue && blockTypes[above.State.id].FluidType != FluidType.None) continue;

                    shorelineFlag = 1.0f;
                    break;
                }
            }

            // --- 2. GET HEIGHT DATA ---
            // First, get the LOGICAL top height based on the fluid level. This is ONLY used for face culling logic.
            byte fluidLevel = BurstVoxelDataBitMapping.GetFluidLevel(packedData);
            float topHeight = templates[fluidLevel];

            // Second, set the visual height by default to full voxel size to avoid "air gaps"
            const float fullBlockHeight = 1.0f;
            float height_tr = fullBlockHeight;
            float height_tl = fullBlockHeight;
            float height_br = fullBlockHeight;
            float height_bl = fullBlockHeight;


            // Then, if above voxel is not a fluid of the same type, calculate heights based on current fluid level and neighbors
            if (!above.HasValue || blockTypes[above.State.id].FluidType != props.FluidType)
            {
                height_tr = GetSmoothedCornerHeight(in props, fluidLevel, n_N, n_E, n_NE, in templates, in blockTypes); // Top-Right
                height_tl = GetSmoothedCornerHeight(in props, fluidLevel, n_N, n_W, n_NW, in templates, in blockTypes); // Top-Left
                height_br = GetSmoothedCornerHeight(in props, fluidLevel, n_S, n_E, n_SE, in templates, in blockTypes); // Bottom-Right
                height_bl = GetSmoothedCornerHeight(in props, fluidLevel, n_S, n_W, n_SW, in templates, in blockTypes); // Bottom-Left
            }

            // --- 3. GENERATE FACES ---
            // --- 3A. Top Face ---
            // Only draw top face if above neighboring voxel is transparent and a different fluid.
            if (!above.HasValue || blockTypes[above.State.id].IsTransparentForMesh && blockTypes[above.State.id].FluidType != props.FluidType)
            {
                vertices.Add(pos + new Vector3(0, height_bl, 0)); // Back-Left
                vertices.Add(pos + new Vector3(0, height_tl, 1)); // Front-Left
                vertices.Add(pos + new Vector3(1, height_br, 0)); // Back-Right
                vertices.Add(pos + new Vector3(1, height_tr, 1)); // Front-Right

                float lightLevel = above.HasValue ? above.State.lightAsFloat : 1.0f;
                Color vertexColor = new Color(liquidType, shorelineFlag, 0.0f, lightLevel);

                for (int i = 0; i < 4; i++)
                {
                    normals.Add(Vector3.up);
                    colors.Add(vertexColor);
                    uvs.Add(Vector2.zero);
                }

                fluidTriangles.Add(vertexIndex);
                fluidTriangles.Add(vertexIndex + 1);
                fluidTriangles.Add(vertexIndex + 2);
                fluidTriangles.Add(vertexIndex + 2);
                fluidTriangles.Add(vertexIndex + 1);
                fluidTriangles.Add(vertexIndex + 3);
                vertexIndex += 4;
            }

            // --- 3B. Side Faces ---
            for (int i = 0; i < 4; i++)
            {
                int faceIndex = VoxelData.HorizontalFaceChecksIndices[i];
                OptionalVoxelState neighbor;
                switch (faceIndex)
                {
                    case 1: neighbor = n_N; break;
                    case 0: neighbor = n_S; break;
                    case 5: neighbor = n_E; break;
                    case 4: neighbor = n_W; break;
                    default: continue;
                }

                // Rule 1: Don't draw if neighbor is a higher or equal fluid block.
                if (neighbor.HasValue && blockTypes[neighbor.State.id].FluidType == props.FluidType && templates[neighbor.State.FluidLevel] >= topHeight) continue;

                // Rule 2: Don't draw if neighbor is an opaque solid block (like stone).
                if (neighbor.HasValue && !blockTypes[neighbor.State.id].IsTransparentForMesh) continue;

                int v1 = BurstVoxelData.VoxelTris.Data[faceIndex * 4 + 0];
                int v2 = BurstVoxelData.VoxelTris.Data[faceIndex * 4 + 1];
                int v3 = BurstVoxelData.VoxelTris.Data[faceIndex * 4 + 2];
                int v4 = BurstVoxelData.VoxelTris.Data[faceIndex * 4 + 3];

                Vector3 p1 = BurstVoxelData.VoxelVerts.Data[v1];
                Vector3 p2 = BurstVoxelData.VoxelVerts.Data[v2];
                Vector3 p3 = BurstVoxelData.VoxelVerts.Data[v3];
                Vector3 p4 = BurstVoxelData.VoxelVerts.Data[v4];

                p1.y = p1.y > 0.5f ? GetVertexHeightForCorner(p1, height_tl, height_tr, height_bl, height_br) : 0;
                p2.y = p2.y > 0.5f ? GetVertexHeightForCorner(p2, height_tl, height_tr, height_bl, height_br) : 0;
                p3.y = p3.y > 0.5f ? GetVertexHeightForCorner(p3, height_tl, height_tr, height_bl, height_br) : 0;
                p4.y = p4.y > 0.5f ? GetVertexHeightForCorner(p4, height_tl, height_tr, height_bl, height_br) : 0;

                vertices.Add(pos + p1);
                vertices.Add(pos + p2);
                vertices.Add(pos + p3);
                vertices.Add(pos + p4);

                float lightLevel = neighbor.HasValue ? neighbor.State.lightAsFloat : 1.0f;
                Color vertexColor = new Color(liquidType, shorelineFlag, 0.0f, lightLevel);

                for (int j = 0; j < 4; j++)
                {
                    normals.Add(VoxelData.FaceChecks[faceIndex]);
                    colors.Add(vertexColor);
                    uvs.Add(Vector2.zero);
                }

                fluidTriangles.Add(vertexIndex);
                fluidTriangles.Add(vertexIndex + 1);
                fluidTriangles.Add(vertexIndex + 2);
                fluidTriangles.Add(vertexIndex + 2);
                fluidTriangles.Add(vertexIndex + 1);
                fluidTriangles.Add(vertexIndex + 3);
                vertexIndex += 4;
            }

            // --- 3C. Bottom Face ---
            // Only draw bottom face if below neighboring voxel is transparent or a different fluid.
            if (!below.HasValue || blockTypes[below.State.id].IsTransparentForMesh && blockTypes[below.State.id].FluidType != props.FluidType)
            {
                vertices.Add(pos + new Vector3(0, 0, 0)); // Back-Left   (0)
                vertices.Add(pos + new Vector3(0, 0, 1)); // Front-Left  (1)
                vertices.Add(pos + new Vector3(1, 0, 0)); // Back-Right  (2)
                vertices.Add(pos + new Vector3(1, 0, 1)); // Front-Right (3)

                float lightLevel = below.HasValue ? below.State.lightAsFloat : 1.0f;
                Color vertexColor = new Color(liquidType, shorelineFlag, 0.0f, lightLevel);

                for (int i = 0; i < 4; i++)
                {
                    normals.Add(Vector3.down);
                    colors.Add(vertexColor);
                    uvs.Add(Vector2.zero);
                }

                // Clockwise winding order when viewed from below.
                fluidTriangles.Add(vertexIndex); // Triangle 1: 0, 2, 1
                fluidTriangles.Add(vertexIndex + 2);
                fluidTriangles.Add(vertexIndex + 1);
                fluidTriangles.Add(vertexIndex + 1); // Triangle 2: 1, 2, 3
                fluidTriangles.Add(vertexIndex + 2);
                fluidTriangles.Add(vertexIndex + 3);
                vertexIndex += 4;
            }
        }

        private static float GetSmoothedCornerHeight(in BlockTypeJobData centerProps, byte centerLevel, OptionalVoxelState n1, OptionalVoxelState n2, OptionalVoxelState nDiag, in NativeArray<float> templates, in NativeArray<BlockTypeJobData> blockTypes)
        {
            float totalHeight = templates[centerLevel];
            int count = 1;

            if (n1.HasValue && blockTypes[n1.State.id].FluidType == centerProps.FluidType)
            {
                totalHeight += templates[n1.State.FluidLevel];
                count++;
            }

            if (n2.HasValue && blockTypes[n2.State.id].FluidType == centerProps.FluidType)
            {
                totalHeight += templates[n2.State.FluidLevel];
                count++;
            }

            if (nDiag.HasValue && blockTypes[nDiag.State.id].FluidType == centerProps.FluidType)
            {
                totalHeight += templates[nDiag.State.FluidLevel];
                count++;
            }

            return totalHeight / count;
        }

        private static float GetVertexHeightForCorner(Vector3 vertPos, float h_tl, float h_tr, float h_bl, float h_br)
        {
            if (vertPos.x > 0.5f) // Right side
                return vertPos.z > 0.5f ? h_tr : h_br;
            else // Left side
                return vertPos.z > 0.5f ? h_tl : h_bl;
        }
    }
}