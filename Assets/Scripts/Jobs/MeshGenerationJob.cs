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
        public NativeArray<CustomMeshData> customMeshes;

        [ReadOnly]
        public NativeArray<CustomFaceData> customFaces;

        [ReadOnly]
        public NativeArray<CustomVertData> customVerts;

        [ReadOnly]
        public NativeArray<int> customTris;

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
                        byte id = BurstVoxelDataBitMapping.GetId(packedData);
                        BlockTypeJobData props = blockTypes[id];

                        if (props.isSolid)
                        {
                            GenerateVoxelMeshData(new Vector3Int(x, y, z), packedData, props);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// The main mesh generation router. It decides which helper function to call
        /// based on the block's properties in the following order: fluid, custom mesh, or standard cube.
        /// </summary>
        private void GenerateVoxelMeshData(Vector3Int pos, uint packedData, BlockTypeJobData voxelProps)
        {
            byte id = BurstVoxelDataBitMapping.GetId(packedData);
            
            
            // Case 1: The block is a fluid.
            if (voxelProps.fluidType != FluidType.None)
            {
                if (voxelProps.fluidType == FluidType.Water)
                {
                    GenerateFluidMeshData(pos, packedData, voxelProps, waterVertexTemplates);
                }
                else if (voxelProps.fluidType == FluidType.Lava)
                {
                    GenerateFluidMeshData(pos, packedData, voxelProps, lavaVertexTemplates);
                }
            }

            // Case 2: The block has a custom mesh.
            else if (voxelProps.customMeshIndex > -1)
            {
                //: Get the specific mesh data to access its face count
                CustomMeshData meshData = customMeshes[voxelProps.customMeshIndex];
                
                // Loop only up to the number of faces defined in the asset.
                for (int p = 0; p < meshData.faceCount; p++)
                {
                    VoxelState? neighborVoxel = GetVoxelStateFromLocalPos(pos + BurstVoxelData.FaceChecks.Data[p]);
                    if (ShouldDrawFace(voxelProps, neighborVoxel))
                    {
                        int textureID = GetTextureID(id, p);
                        float lightLevel = neighborVoxel?.lightAsFloat ?? 1.0f;

                        // Call the new helper for custom meshes
                        VoxelMeshHelper.GenerateCustomMeshFace(p, textureID, lightLevel, pos,
                            voxelProps.customMeshIndex, ref customMeshes, ref customFaces, ref customVerts, ref customTris,
                            ref vertexIndex, ref output.vertices, ref output.triangles, ref output.transparentTriangles, ref output.uvs,
                            ref output.colors, ref output.normals, voxelProps.renderNeighborFaces);
                    }
                }
            }
            // Case 3: The block is a standard cube.
            else
            {
                byte orientation = BurstVoxelDataBitMapping.GetOrientation(packedData);
                float rotation = VoxelHelper.GetRotationAngle(orientation);

                // Iterate through all 6 faces of the voxel (Back, Front, Top, Bottom, Left, Right)
                for (int p = 0; p < 6; p++)
                {
                    // Get the neighboring voxel.
                    // This correctly checks neighbors in all directions, including across chunk boundaries, preventing crashes.
                    // We use BurstVoxelData.FaceChecks.Data to get the direction vector for the current face (p).
                    VoxelState? neighborVoxel = GetVoxelStateFromLocalPos(pos + BurstVoxelData.FaceChecks.Data[p]);

                    if (ShouldDrawFace(voxelProps, neighborVoxel))
                    {
                        // Account for the block's orientation to get the correct texture for the world-facing direction.
                        int translatedP = VoxelHelper.GetTranslatedFaceIndex(p, orientation);
                        int textureID = GetTextureID(id, translatedP);
                        float lightLevel = neighborVoxel?.lightAsFloat ?? 1.0f;

                        // Call the new helper for standard cubes
                        VoxelMeshHelper.GenerateStandardCubeFace(translatedP, textureID, lightLevel, pos, rotation,
                            ref vertexIndex, ref output.vertices, ref output.triangles, ref output.transparentTriangles,
                            ref output.uvs, ref output.colors, ref output.normals,
                            voxelProps.renderNeighborFaces);
                    }
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

            // Get all relevant neighbors to calculate smoothed corner heights and detect shorelines.
            VoxelState? n_N = GetVoxelStateFromLocalPos(pos + new Vector3Int(0, 0, 1));
            VoxelState? n_E = GetVoxelStateFromLocalPos(pos + new Vector3Int(1, 0, 0));
            VoxelState? n_S = GetVoxelStateFromLocalPos(pos + new Vector3Int(0, 0, -1));
            VoxelState? n_W = GetVoxelStateFromLocalPos(pos + new Vector3Int(-1, 0, 0));
            VoxelState? n_NE = GetVoxelStateFromLocalPos(pos + new Vector3Int(1, 0, 1));
            VoxelState? n_SE = GetVoxelStateFromLocalPos(pos + new Vector3Int(1, 0, -1));
            VoxelState? n_SW = GetVoxelStateFromLocalPos(pos + new Vector3Int(-1, 0, -1));
            VoxelState? n_NW = GetVoxelStateFromLocalPos(pos + new Vector3Int(-1, 0, 1));

            // --- 1. DETERMINE SHADER FLAGS ---
            float liquidType = props.fluidType == FluidType.Lava ? 1.0f : 0.0f;
            float shorelineFlag = 0.0f;

            // Check horizontal neighbors to see if this is a "shoreline" block.
            VoxelState?[] horizontalNeighbors = { n_N, n_E, n_S, n_W };
            foreach (var neighbor in horizontalNeighbors)
            {
                // A shoreline exists if the neighbor is a solid block that is NOT a fluid.
                if (neighbor.HasValue && blockTypes[neighbor.Value.id].isSolid && blockTypes[neighbor.Value.id].fluidType == FluidType.None)
                {
                    shorelineFlag = 1.0f;
                    break; // Found one solid neighbor, no need to check others.
                }
            }

            // --- 2. GET HEIGHT DATA ---
            byte fluidLevel = BurstVoxelDataBitMapping.GetFluidLevel(packedData);
            float topHeight = templates[fluidLevel];

            // Calculate the final Y-position for each of the four corners of the top face.
            // These are declared here to be in scope for both top and side face generation.
            float height_tr = GetSmoothedCornerHeight(props, fluidLevel, n_N, n_E, n_NE, templates); // Top-Right
            float height_tl = GetSmoothedCornerHeight(props, fluidLevel, n_N, n_W, n_NW, templates); // Top-Left
            float height_br = GetSmoothedCornerHeight(props, fluidLevel, n_S, n_E, n_SE, templates); // Bottom-Right
            float height_bl = GetSmoothedCornerHeight(props, fluidLevel, n_S, n_W, n_SW, templates); // Bottom-Left

            // --- 3. GENERATE FACES ---
            // --- 3A. Top Face ---
            VoxelState? above = GetVoxelStateFromLocalPos(pos + new Vector3Int(0, 1, 0));
            if (above == null || !blockTypes[above.Value.id].isSolid)
            {
                // Add the four vertices for the top face using the calculated corner heights.
                output.vertices.Add(pos + new Vector3(0, height_bl, 0)); // Back-Left
                output.vertices.Add(pos + new Vector3(0, height_tl, 1)); // Front-Left
                output.vertices.Add(pos + new Vector3(1, height_br, 0)); // Back-Right
                output.vertices.Add(pos + new Vector3(1, height_tr, 1)); // Front-Right

                float lightLevel = above?.lightAsFloat ?? 1.0f;
                Color vertexColor = new Color(liquidType, shorelineFlag, 0.0f, lightLevel);

                // Add the packed vertex color data for each of the 4 vertices.
                for (int i = 0; i < 4; i++)
                {
                    output.normals.Add(Vector3.up);
                    output.colors.Add(vertexColor); // Add the packed vertexColor
                    output.uvs.Add(Vector2.zero); // Uber shader doesn't use atlas UVs
                }

                output.fluidTriangles.Add(vertexIndex);
                output.fluidTriangles.Add(vertexIndex + 1);
                output.fluidTriangles.Add(vertexIndex + 2);
                output.fluidTriangles.Add(vertexIndex + 2);
                output.fluidTriangles.Add(vertexIndex + 1);
                output.fluidTriangles.Add(vertexIndex + 3);
                vertexIndex += 4;
            }

            // --- 3B. Side Faces ---
            for (int p = 0; p < 4; p++)
            {
                int faceIndex = VoxelData.HorizontalFaceChecksIndices[p];
                VoxelState? neighbor = GetVoxelStateFromLocalPos(pos + VoxelData.FaceChecks[faceIndex]);

                // TODO: The topHeight check causes internal side faces to always be rendered between different fluid levels, which is not desired.
                // A side face should NOT be drawn if the neighbor is the same fluid type AND its surface is at or above our own surface.
                if (neighbor.HasValue && blockTypes[neighbor.Value.id].fluidType == props.fluidType && templates[neighbor.Value.FluidLevel] >= topHeight)
                {
                    continue;
                }

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

                output.vertices.Add(pos + p1);
                output.vertices.Add(pos + p2);
                output.vertices.Add(pos + p3);
                output.vertices.Add(pos + p4);

                float lightLevel = neighbor?.lightAsFloat ?? 1.0f;
                Color vertexColor = new Color(liquidType, shorelineFlag, 0.0f, lightLevel);

                // Add the packed vertex color data for each of the 4 vertices.
                for (int i = 0; i < 4; i++)
                {
                    output.normals.Add(VoxelData.FaceChecks[faceIndex]);
                    output.colors.Add(vertexColor); // Add the packed vertexColor
                    output.uvs.Add(Vector2.zero); // Uber shader doesn't use atlas UVs
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
        /// Contains the face culling logic to determine if a face should be drawn.
        /// </summary>
        private bool ShouldDrawFace(BlockTypeJobData voxelProps, VoxelState? neighborVoxel)
        {
            if (!neighborVoxel.HasValue) // If the neighbor is outside the world, always draw.
                return true;

            BlockTypeJobData neighborProps = blockTypes[neighborVoxel.Value.id];

            // If this block is transparent, draw if the neighbor is not solid OR also transparent.
            if (voxelProps.renderNeighborFaces)
                return !neighborProps.isSolid || neighborProps.renderNeighborFaces;

            // If this block is solid, draw if the neighbor is transparent OR not solid.
            return neighborProps.renderNeighborFaces || !neighborProps.isSolid;
        }


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