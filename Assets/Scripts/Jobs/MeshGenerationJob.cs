using Data;
using Helpers;
using Jobs.BurstData;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace Jobs
{
    [BurstCompile]
    public struct MeshGenerationJob : IJob
    {
        [ReadOnly]
        public NativeArray<uint> map;

        [ReadOnly]
        public NativeArray<BlockTypeJobData> blockTypes;

        [ReadOnly]
        public Vector3 chunkPosition;

        // Neighboring chunk data for face culling at borders
        [ReadOnly]
        public NativeArray<uint> neighborBack;

        [ReadOnly]
        public NativeArray<uint> neighborFront;

        [ReadOnly]
        public NativeArray<uint> neighborLeft;

        [ReadOnly]
        public NativeArray<uint> neighborRight;
        // Top and Bottom neighbors are not needed as chunks are only horizontal neighbors

        // --- FLUID DATA TEMPLATES ---
        [ReadOnly]
        public NativeArray<float> waterVertexTemplates;

        [ReadOnly]
        public NativeArray<float> lavaVertexTemplates;

        // --- OUTPUT ---
        public MeshDataJobOutput output;

        private int vertexIndex;

        public void Execute()
        {
            vertexIndex = 0;

            for (int y = 0; y < VoxelData.ChunkHeight; y++)
            {
                for (int x = 0; x < VoxelData.ChunkWidth; x++)
                {
                    for (int z = 0; z < VoxelData.ChunkWidth; z++)
                    {
                        int mapIndex = x + VoxelData.ChunkWidth * (y + VoxelData.ChunkHeight * z);
                        uint packedData = map[mapIndex];
                        BlockTypeJobData props = blockTypes[BurstVoxelDataBitMapping.GetId(packedData)];

                        if (props.isSolid)
                        {
                            if (props.fluidType != FluidType.None)
                            {
                                if (props.fluidType != FluidType.None)
                                {
                                    Vector3Int voxelPos = new Vector3Int(x, y, z);

                                    // Pass the correct template based on the fluid type
                                    if (props.fluidType == FluidType.Water)
                                    {
                                        GenerateFluidMeshData(voxelPos, packedData, props, waterVertexTemplates);
                                    }
                                    else if (props.fluidType == FluidType.Lava)
                                    {
                                        GenerateFluidMeshData(voxelPos, packedData, props, lavaVertexTemplates);
                                    }
                                }
                            }
                            else
                            {
                                UpdateMeshData(new Vector3Int(x, y, z), packedData);
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Generates the mesh for a standard, solid voxel (like stone, dirt, or custom blocks).
        /// It handles face culling against neighbors, block orientation, and assigns vertices to the
        /// correct sub-mesh (opaque or transparent).
        /// </summary>
        private void UpdateMeshData(Vector3Int pos, uint packedData)
        {
            byte id = BurstVoxelDataBitMapping.GetId(packedData);
            byte orientation = BurstVoxelDataBitMapping.GetOrientation(packedData);
            BlockTypeJobData voxelProps = blockTypes[id];

            float rotation = VoxelHelper.GetRotationAngle(orientation);

            // Iterate through all 6 faces of the voxel (Back, Front, Top, Bottom, Left, Right)
            for (int p = 0; p < 6; p++)
            {
                // Account for the block's orientation to get the correct texture for the world-facing direction.
                int translatedP = VoxelHelper.GetTranslatedFaceIndex(p, orientation);

                // --- INTEGRATION POINT ---
                // Get the neighboring voxel using the new, robust GetVoxelStateFromLocalPos method.
                // This correctly checks neighbors in all directions, including across chunk boundaries, preventing crashes.
                // We use BurstVoxelData.FaceChecks.Data to get the direction vector for the current face (p).
                VoxelState? neighborVoxel = GetVoxelStateFromLocalPos(pos + BurstVoxelData.FaceChecks.Data[p]);

                // --- Face Culling Logic ---
                // Determine if this face should be drawn.
                bool shouldDrawFace = false;
                if (!neighborVoxel.HasValue) // If the neighbor is outside the world or in an unloaded chunk, always draw the face.
                {
                    shouldDrawFace = true;
                }
                else
                {
                    // Get the properties of the neighboring block.
                    BlockTypeJobData neighborProps = blockTypes[neighborVoxel.Value.id];

                    // The face should be drawn if this block is transparent and the neighbor isn't,
                    // or if the neighbor is transparent. This prevents z-fighting and renders interiors correctly.
                    if (voxelProps.renderNeighborFaces)
                        shouldDrawFace = !neighborProps.isSolid || neighborProps.renderNeighborFaces;
                    else
                        shouldDrawFace = neighborProps.renderNeighborFaces || !neighborProps.isSolid;
                }


                if (shouldDrawFace)
                {
                    // Get the correct texture ID and the light level of the space the face is exposed to.
                    int textureID = GetTextureID(id, translatedP);
                    float lightLevel = neighborVoxel?.lightAsFloat ?? 1.0f;

                    // A face is a quad, which consists of 4 vertices.
                    for (int i = 0; i < 4; i++)
                    {
                        // LINTING FIX: Access Burst SharedStatic data using the .Data property.
                        int vertIndex = BurstVoxelData.VoxelTris.Data[translatedP * 4 + i];
                        Vector3 vertPos = BurstVoxelData.VoxelVerts.Data[vertIndex];

                        // Rotate the vertex around the block's center if it has an orientation.
                        Vector3 center = new Vector3(0.5f, 0.5f, 0.5f);
                        Vector3 direction = vertPos - center;
                        direction = Quaternion.Euler(0, rotation, 0) * direction;

                        // Add all vertex data to the output lists.
                        output.vertices.Add(pos + direction + center);
                        output.normals.Add(BurstVoxelData.FaceChecks.Data[p]);
                        output.colors.Add(new Color(0, 0, 0, lightLevel));
                        AddTexture(textureID, BurstVoxelData.VoxelUvs.Data[i]);
                    }

                    // --- Triangle Generation ---
                    // Add the triangle indices to the correct sub-mesh based on the block's properties.
                    if (voxelProps.renderNeighborFaces) // Transparent blocks (leaves, glass)
                    {
                        output.transparentTriangles.Add(vertexIndex);
                        output.transparentTriangles.Add(vertexIndex + 1);
                        output.transparentTriangles.Add(vertexIndex + 2);
                        output.transparentTriangles.Add(vertexIndex + 2);
                        output.transparentTriangles.Add(vertexIndex + 1);
                        output.transparentTriangles.Add(vertexIndex + 3);
                    }
                    else // Solid, opaque blocks
                    {
                        output.triangles.Add(vertexIndex);
                        output.triangles.Add(vertexIndex + 1);
                        output.triangles.Add(vertexIndex + 2);
                        output.triangles.Add(vertexIndex + 2);
                        output.triangles.Add(vertexIndex + 1);
                        output.triangles.Add(vertexIndex + 3);
                    }

                    // Increment the vertex index for the next quad.
                    vertexIndex += 4;
                }
            }
        }

        #region Fluid Meshing

        /// <summary>
        /// Generates a custom mesh for a fluid voxel, creating a sloped surface based on its fluid level
        /// and the levels of its neighbors. This method uses pre-computed vertex height templates for high performance.
        /// </summary>
        /// <param name="pos">The local position of the fluid voxel within the chunk.</param>
        /// <param name="packedData">The raw packed data of the fluid voxel.</param>
        /// <param name="props">The job-safe properties of this fluid block.</param>
        /// <param name="templates">A NativeArray containing the pre-computed vertex Y-positions for each fluid level.</param>
        private void GenerateFluidMeshData(Vector3Int pos, uint packedData, BlockTypeJobData props, NativeArray<float> templates)
        {
            // TODO: When a fluid voxel is below a an other fluid voxel, it should be rendered as a full block regardless of fluid level.
            // --- 1. Get Height Data and Neighbor States ---
            // This data is required for both the top and side faces, so we calculate it once at the beginning.

            // Get the base height of the fluid's surface from the pre-computed template array.
            byte fluidLevel = BurstVoxelDataBitMapping.GetFluidLevel(packedData);
            float topHeight = templates[fluidLevel];

            // Get all relevant neighbors to calculate smoothed corner heights for a realistic sloped surface.
            VoxelState? n_N = GetVoxelStateFromLocalPos(pos + new Vector3Int(0, 0, 1));
            VoxelState? n_E = GetVoxelStateFromLocalPos(pos + new Vector3Int(1, 0, 0));
            VoxelState? n_S = GetVoxelStateFromLocalPos(pos + new Vector3Int(0, 0, -1));
            VoxelState? n_W = GetVoxelStateFromLocalPos(pos + new Vector3Int(-1, 0, 0));
            VoxelState? n_NE = GetVoxelStateFromLocalPos(pos + new Vector3Int(1, 0, 1));
            VoxelState? n_SE = GetVoxelStateFromLocalPos(pos + new Vector3Int(1, 0, -1));
            VoxelState? n_SW = GetVoxelStateFromLocalPos(pos + new Vector3Int(-1, 0, -1));
            VoxelState? n_NW = GetVoxelStateFromLocalPos(pos + new Vector3Int(-1, 0, 1));

            // Calculate the final Y-position for each of the four corners of the top face.
            // These are declared here to be in scope for both top and side face generation.
            float height_tr = GetSmoothedCornerHeight(props, fluidLevel, n_N, n_E, n_NE, templates); // Top-Right
            float height_tl = GetSmoothedCornerHeight(props, fluidLevel, n_N, n_W, n_NW, templates); // Top-Left
            float height_br = GetSmoothedCornerHeight(props, fluidLevel, n_S, n_E, n_SE, templates); // Bottom-Right
            float height_bl = GetSmoothedCornerHeight(props, fluidLevel, n_S, n_W, n_SW, templates); // Bottom-Left

            // --- 2. Generate the Top Face (The visible surface of the fluid) ---
            VoxelState? above = GetVoxelStateFromLocalPos(pos + new Vector3Int(0, 1, 0));
            if (above == null || !blockTypes[above.Value.id].isSolid)
            {
                // Add the four vertices for the top face using the calculated corner heights.
                output.vertices.Add(pos + new Vector3(0, height_bl, 0)); // Back-Left
                output.vertices.Add(pos + new Vector3(0, height_tl, 1)); // Front-Left
                output.vertices.Add(pos + new Vector3(1, height_br, 0)); // Back-Right
                output.vertices.Add(pos + new Vector3(1, height_tr, 1)); // Front-Right

                float lightLevel = above?.lightAsFloat ?? 1.0f;
                for (int i = 0; i < 4; i++)
                {
                    output.normals.Add(Vector3.up);
                    output.colors.Add(new Color(0, 0, 0, lightLevel));
                    AddTexture(props.topFaceTexture, BurstVoxelData.VoxelUvs.Data[i]);
                }

                output.fluidTriangles.Add(vertexIndex);
                output.fluidTriangles.Add(vertexIndex + 1);
                output.fluidTriangles.Add(vertexIndex + 2);
                output.fluidTriangles.Add(vertexIndex + 2);
                output.fluidTriangles.Add(vertexIndex + 1);
                output.fluidTriangles.Add(vertexIndex + 3);
                vertexIndex += 4;
            }

            // --- 3. Generate Side Faces (and "waterfall" faces) ---
            for (int p = 0; p < 4; p++)
            {
                int faceIndex = VoxelData.HorizontalFaceChecksIndices[p];
                VoxelState? neighbor = GetVoxelStateFromLocalPos(pos + VoxelData.FaceChecks[faceIndex]);

                // TODO: The topHeight check causes side faces to always be rendered between different fluid levels, which is not desired.
                // A side face should NOT be drawn if the neighbor is the same fluid type AND its surface is at or above our own surface.
                if (neighbor.HasValue && blockTypes[neighbor.Value.id].fluidType == props.fluidType && templates[neighbor.Value.FluidLevel] >= topHeight)
                {
                    continue;
                }

                // Access Burst SharedStatic data using the .Data property.
                int v1 = BurstVoxelData.VoxelTris.Data[faceIndex * 4 + 0];
                int v2 = BurstVoxelData.VoxelTris.Data[faceIndex * 4 + 1];
                int v3 = BurstVoxelData.VoxelTris.Data[faceIndex * 4 + 2];
                int v4 = BurstVoxelData.VoxelTris.Data[faceIndex * 4 + 3];

                Vector3 p1 = BurstVoxelData.VoxelVerts.Data[v1];
                Vector3 p2 = BurstVoxelData.VoxelVerts.Data[v2];
                Vector3 p3 = BurstVoxelData.VoxelVerts.Data[v3];
                Vector3 p4 = BurstVoxelData.VoxelVerts.Data[v4];

                p1.y = (p1.y > 0.5f) ? GetVertexHeightForCorner(p1, height_tl, height_tr, height_bl, height_br) : 0;
                p2.y = (p2.y > 0.5f) ? GetVertexHeightForCorner(p2, height_tl, height_tr, height_bl, height_br) : 0;
                p3.y = (p3.y > 0.5f) ? GetVertexHeightForCorner(p3, height_tl, height_tr, height_bl, height_br) : 0;
                p4.y = (p4.y > 0.5f) ? GetVertexHeightForCorner(p4, height_tl, height_tr, height_bl, height_br) : 0;

                output.vertices.Add(pos + p1);
                output.vertices.Add(pos + p2);
                output.vertices.Add(pos + p3);
                output.vertices.Add(pos + p4);

                float lightLevel = neighbor?.lightAsFloat ?? 1.0f;

                // Get the block's ID and pass it to GetTextureID correctly.
                byte blockId = BurstVoxelDataBitMapping.GetId(packedData);
                int textureID = GetTextureID(blockId, faceIndex);

                for (int i = 0; i < 4; i++)
                {
                    output.normals.Add(VoxelData.FaceChecks[faceIndex]);
                    output.colors.Add(new Color(0, 0, 0, lightLevel));
                    AddTexture(textureID, BurstVoxelData.VoxelUvs.Data[i]);
                }

                output.fluidTriangles.Add(vertexIndex);
                output.fluidTriangles.Add(vertexIndex + 1);
                output.fluidTriangles.Add(vertexIndex + 2);
                output.fluidTriangles.Add(vertexIndex + 2);
                output.fluidTriangles.Add(vertexIndex + 1);
                output.fluidTriangles.Add(vertexIndex + 3);
                vertexIndex += 4;
            }
        }

        /// <summary>
        /// Calculates the smoothed Y-position for a fluid surface corner by averaging its height
        /// with the heights of three relevant neighbors (cardinal and diagonal).
        /// </summary>
        private float GetSmoothedCornerHeight(BlockTypeJobData centerProps, byte centerLevel, VoxelState? n1, VoxelState? n2, VoxelState? nDiag, NativeArray<float> templates)
        {
            float totalHeight = templates[centerLevel];
            int count = 1;

            // Add height from first cardinal neighbor if it's the same fluid.
            if (n1.HasValue && blockTypes[n1.Value.id].fluidType == centerProps.fluidType)
            {
                totalHeight += templates[n1.Value.FluidLevel];
                count++;
            }

            // Add height from second cardinal neighbor.
            if (n2.HasValue && blockTypes[n2.Value.id].fluidType == centerProps.fluidType)
            {
                totalHeight += templates[n2.Value.FluidLevel];
                count++;
            }

            // Add height from diagonal neighbor.
            if (nDiag.HasValue && blockTypes[nDiag.Value.id].fluidType == centerProps.fluidType)
            {
                totalHeight += templates[nDiag.Value.FluidLevel];
                count++;
            }

            return totalHeight / count;
        }

        /// <summary>
        /// A helper to select the correct pre-calculated corner height for a given vertex position.
        /// </summary>
        private float GetVertexHeightForCorner(Vector3 vertPos, float h_tl, float h_tr, float h_bl, float h_br)
        {
            if (vertPos.x > 0.5f) // Right side
                return vertPos.z > 0.5f ? h_tr : h_br;
            else // Left side
                return vertPos.z > 0.5f ? h_tl : h_bl;
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// A robust method to get a VoxelState from any local position relative to the current chunk's origin.
        /// It correctly handles positions that are outside the chunk's bounds, including diagonals, by
        /// selecting the appropriate neighbor chunk map.
        /// </summary>
        /// <param name="pos">The local position to check (e.g., (-1, 10, 16)).</param>
        /// <returns>A VoxelState if the position is in a loaded neighbor chunk, otherwise null.</returns>
        private VoxelState? GetVoxelStateFromLocalPos(Vector3Int pos)
        {
            if (pos.y < 0 || pos.y >= VoxelData.ChunkHeight) return null;

            NativeArray<uint> targetMap;
            Vector3Int localPos = pos;

            // Determine which neighbor map to use based on X and Z coordinates.
            if (pos.x < 0) // West side
            {
                localPos.x += VoxelData.ChunkWidth;
                if (pos.z < 0)
                {
                    localPos.z += VoxelData.ChunkWidth;
                    targetMap = neighborBack;
                }
                else if (pos.z >= VoxelData.ChunkWidth)
                {
                    localPos.z -= VoxelData.ChunkWidth;
                    targetMap = neighborFront;
                } // ERROR: Was neighborNW
                else
                {
                    targetMap = neighborLeft;
                }
            }
            else if (pos.x >= VoxelData.ChunkWidth) // East side
            {
                localPos.x -= VoxelData.ChunkWidth;
                if (pos.z < 0)
                {
                    localPos.z += VoxelData.ChunkWidth;
                    targetMap = neighborBack;
                } // ERROR: Was neighborSE
                else if (pos.z >= VoxelData.ChunkWidth)
                {
                    localPos.z -= VoxelData.ChunkWidth;
                    targetMap = neighborFront;
                }
                else
                {
                    targetMap = neighborRight;
                }
            }
            else // Center column (X is within bounds)
            {
                if (pos.z < 0)
                {
                    localPos.z += VoxelData.ChunkWidth;
                    targetMap = neighborBack;
                }
                else if (pos.z >= VoxelData.ChunkWidth)
                {
                    localPos.z -= VoxelData.ChunkWidth;
                    targetMap = neighborFront;
                }
                else
                {
                    targetMap = map;
                }
            }

            if (!targetMap.IsCreated || targetMap.Length == 0) return null;

            int mapIndex = localPos.x + VoxelData.ChunkWidth * (localPos.y + VoxelData.ChunkHeight * localPos.z);

            // This check prevents the job from crashing if the logic is ever incorrect.
            if (mapIndex < 0 || mapIndex >= targetMap.Length) return null;

            return new VoxelState(targetMap[mapIndex]);
        }

        #endregion


        #region Texture Methods

        private void AddTexture(int textureID, Vector2 uv)
        {
            float y = Mathf.FloorToInt((float)textureID / VoxelData.TextureAtlasSizeInBlocks);
            float x = textureID - (y * VoxelData.TextureAtlasSizeInBlocks);

            x *= VoxelData.NormalizedBlockTextureSize;
            y *= VoxelData.NormalizedBlockTextureSize;

            // To start reading the atlas from the top left
            y = 1f - y - VoxelData.NormalizedBlockTextureSize;

            x += VoxelData.NormalizedBlockTextureSize * uv.x;
            y += VoxelData.NormalizedBlockTextureSize * uv.y;

            output.uvs.Add(new Vector2(x, y));
        }

        private int GetTextureID(byte blockId, int faceIndex)
        {
            BlockTypeJobData props = blockTypes[blockId];
            switch (faceIndex)
            {
                case 0: return props.backFaceTexture;
                case 1: return props.frontFaceTexture;
                case 2: return props.topFaceTexture;
                case 3: return props.bottomFaceTexture;
                case 4: return props.leftFaceTexture;
                case 5: return props.rightFaceTexture;
                default: return 0;
            }
        }

        #endregion
    }
}