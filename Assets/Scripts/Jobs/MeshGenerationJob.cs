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
                // Select the correct vertex height templates based on fluid type.
                NativeArray<float> templates = (voxelProps.fluidType == FluidType.WaterLike) ? waterVertexTemplates : lavaVertexTemplates;

                // Gather all 9 required neighbors for fluid meshing.
                var neighbors = new NativeArray<OptionalVoxelState>(10, Allocator.Temp);
                Vector3Int[] neighborOffsets =
                {
                    new Vector3Int(0, 0, 1), new Vector3Int(1, 0, 0), new Vector3Int(0, 0, -1), new Vector3Int(-1, 0, 0),
                    new Vector3Int(1, 0, 1), new Vector3Int(1, 0, -1), new Vector3Int(-1, 0, -1), new Vector3Int(-1, 0, 1),
                    new Vector3Int(0, 1, 0), new Vector3Int(0, -1, 0)
                };

                for (int i = 0; i < neighborOffsets.Length; i++)
                {
                    VoxelState? neighborState = GetVoxelStateFromLocalPos(pos + neighborOffsets[i]);
                    if (neighborState.HasValue)
                    {
                        neighbors[i] = new OptionalVoxelState(neighborState.Value);
                    }
                }

                // Call the unified helper method.
                VoxelMeshHelper.GenerateFluidMeshData(pos, packedData, voxelProps, in templates, in blockTypes, in neighbors,
                    ref vertexIndex, ref output.vertices, ref output.fluidTriangles, ref output.uvs, ref output.colors, ref output.normals);

                // Dispose the temporary native array.
                neighbors.Dispose();
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