using Data;
using Helpers;
using Jobs.BurstData;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace Jobs
{
    [BurstCompile(FloatPrecision = FloatPrecision.Standard, FloatMode = FloatMode.Fast)]
    public struct MeshGenerationJob : IJob
    {
        [ReadOnly]
        public NativeArray<uint> Map;

        [ReadOnly]
        public NativeArray<BlockTypeJobData> BlockTypes;

        [ReadOnly]
        public NativeArray<CustomMeshData> CustomMeshes;

        [ReadOnly]
        public NativeArray<CustomFaceData> CustomFaces;

        [ReadOnly]
        public NativeArray<CustomVertData> CustomVerts;

        [ReadOnly]
        public NativeArray<int> CustomTris;

        [ReadOnly]
        public Vector3 ChunkPosition;

        // Neighboring chunk data for face culling at borders
        [ReadOnly]
        public NativeArray<uint> NeighborBack;

        [ReadOnly]
        public NativeArray<uint> NeighborFront;

        [ReadOnly]
        public NativeArray<uint> NeighborLeft;

        [ReadOnly]
        public NativeArray<uint> NeighborRight;
        // Top and Bottom neighbors are not needed as chunks are only horizontal neighbors

        // --- FLUID DATA TEMPLATES ---
        [ReadOnly]
        public NativeArray<float> WaterVertexTemplates;

        [ReadOnly]
        public NativeArray<float> LavaVertexTemplates;

        // --- OUTPUT ---
        public MeshDataJobOutput Output;

        private int _vertexIndex;

        // --- HELPERS ---
        private static readonly Vector3Int[] FluidNeighborOffsets =
        {
            new Vector3Int(0, 0, 1), new Vector3Int(1, 0, 0), new Vector3Int(0, 0, -1), new Vector3Int(-1, 0, 0),
            new Vector3Int(1, 0, 1), new Vector3Int(1, 0, -1), new Vector3Int(-1, 0, -1), new Vector3Int(-1, 0, 1),
            new Vector3Int(0, 1, 0), new Vector3Int(0, -1, 0),
        };

        public void Execute()
        {
            _vertexIndex = 0;

            for (int y = 0; y < VoxelData.ChunkHeight; y++)
            {
                for (int x = 0; x < VoxelData.ChunkWidth; x++)
                {
                    for (int z = 0; z < VoxelData.ChunkWidth; z++)
                    {
                        int mapIndex = x + VoxelData.ChunkWidth * (y + VoxelData.ChunkHeight * z);
                        uint packedData = Map[mapIndex];
                        byte id = BurstVoxelDataBitMapping.GetId(packedData);
                        BlockTypeJobData props = BlockTypes[id];

                        if (props.IsSolid)
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
            if (voxelProps.FluidType != FluidType.None)
            {
                // Select the correct vertex height templates based on fluid type.
                NativeArray<float> templates = voxelProps.FluidType == FluidType.WaterLike ? WaterVertexTemplates : LavaVertexTemplates;

                // Gather all 9 required neighbors for fluid meshing.
                var neighbors = new NativeArray<OptionalVoxelState>(10, Allocator.Temp);

                for (int i = 0; i < FluidNeighborOffsets.Length; i++)
                {
                    VoxelState? neighborState = GetVoxelStateFromLocalPos(pos + FluidNeighborOffsets[i]);
                    if (neighborState.HasValue)
                    {
                        neighbors[i] = new OptionalVoxelState(neighborState.Value);
                    }
                }

                // Call the unified helper method.
                VoxelMeshHelper.GenerateFluidMeshData(in pos, packedData, in voxelProps, in templates, in BlockTypes, in neighbors,
                    ref _vertexIndex, ref Output.Vertices, ref Output.FluidTriangles, ref Output.Uvs, ref Output.Colors, ref Output.Normals);

                // Dispose the temporary native array.
                neighbors.Dispose();
            }

            // Case 2: The block has a custom mesh.
            else if (voxelProps.CustomMeshIndex > -1)
            {
                byte orientation = BurstVoxelDataBitMapping.GetOrientation(packedData);
                float rotation = VoxelHelper.GetRotationAngle(orientation);

                //: Get the specific mesh data to access its face count
                CustomMeshData meshData = CustomMeshes[voxelProps.CustomMeshIndex];

                // Iterate through all 6 WORLD directions, same as a standard cube.
                for (int p = 0; p < 6; p++)
                {
                    // Safety check: If the custom mesh asset doesn't define this face, skip it.
                    if (p >= meshData.FaceCount) continue;

                    // Check the neighbor in the current WORLD direction.
                    VoxelState? neighborVoxel = GetVoxelStateFromLocalPos(pos + BurstVoxelData.FaceChecks.Data[p]);

                    if (ShouldDrawFace(voxelProps, neighborVoxel))
                    {
                        // Translate the WORLD direction (p) to the correct ORIGINAL face index based on the block's orientation.
                        int translatedP = VoxelHelper.GetTranslatedFaceIndex(p, orientation);

                        // Use the translated index to get the correct texture for the face being rendered.
                        int textureID = GetTextureID(id, translatedP);
                        float lightLevel = neighborVoxel?.lightAsFloat ?? 1.0f;

                        // Call the helper, passing the TRANSLATED face index so it generates the correct set of vertices from the VoxelMeshData asset.
                        VoxelMeshHelper.GenerateCustomMeshFace(translatedP, textureID, lightLevel, pos, rotation,
                            voxelProps.CustomMeshIndex, ref CustomMeshes, ref CustomFaces, ref CustomVerts, ref CustomTris,
                            ref _vertexIndex, ref Output.Vertices, ref Output.Triangles, ref Output.TransparentTriangles, ref Output.Uvs,
                            ref Output.Colors, ref Output.Normals, voxelProps.RenderNeighborFaces);
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
                        VoxelMeshHelper.GenerateStandardCubeFace(translatedP, textureID, lightLevel, in pos, rotation,
                            ref _vertexIndex, ref Output.Vertices, ref Output.Triangles, ref Output.TransparentTriangles,
                            ref Output.Uvs, ref Output.Colors, ref Output.Normals,
                            voxelProps.RenderNeighborFaces);
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

            BlockTypeJobData neighborProps = BlockTypes[neighborVoxel.Value.id];

            // If this block is transparent, draw if the neighbor is not solid OR also transparent.
            if (voxelProps.RenderNeighborFaces)
                return !neighborProps.IsSolid || neighborProps.RenderNeighborFaces;

            // If this block is solid, draw if the neighbor is transparent OR not solid.
            return neighborProps.RenderNeighborFaces || !neighborProps.IsSolid;
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
                    targetMap = NeighborBack;
                }
                else if (pos.z >= VoxelData.ChunkWidth)
                {
                    localPos.z -= VoxelData.ChunkWidth;
                    targetMap = NeighborFront;
                } // ERROR: Was neighborNW
                else
                {
                    targetMap = NeighborLeft;
                }
            }
            else if (pos.x >= VoxelData.ChunkWidth) // East side
            {
                localPos.x -= VoxelData.ChunkWidth;
                if (pos.z < 0)
                {
                    localPos.z += VoxelData.ChunkWidth;
                    targetMap = NeighborBack;
                } // ERROR: Was neighborSE
                else if (pos.z >= VoxelData.ChunkWidth)
                {
                    localPos.z -= VoxelData.ChunkWidth;
                    targetMap = NeighborFront;
                }
                else
                {
                    targetMap = NeighborRight;
                }
            }
            else // Center column (X is within bounds)
            {
                if (pos.z < 0)
                {
                    localPos.z += VoxelData.ChunkWidth;
                    targetMap = NeighborBack;
                }
                else if (pos.z >= VoxelData.ChunkWidth)
                {
                    localPos.z -= VoxelData.ChunkWidth;
                    targetMap = NeighborFront;
                }
                else
                {
                    targetMap = Map;
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
            float x = textureID - y * VoxelData.TextureAtlasSizeInBlocks;

            x *= VoxelData.NormalizedBlockTextureSize;
            y *= VoxelData.NormalizedBlockTextureSize;

            // To start reading the atlas from the top left
            y = 1f - y - VoxelData.NormalizedBlockTextureSize;

            x += VoxelData.NormalizedBlockTextureSize * uv.x;
            y += VoxelData.NormalizedBlockTextureSize * uv.y;

            Output.Uvs.Add(new Vector2(x, y));
        }

        private int GetTextureID(byte blockId, int faceIndex)
        {
            BlockTypeJobData props = BlockTypes[blockId];
            switch (faceIndex)
            {
                case 0: return props.BackFaceTexture;
                case 1: return props.FrontFaceTexture;
                case 2: return props.TopFaceTexture;
                case 3: return props.BottomFaceTexture;
                case 4: return props.LeftFaceTexture;
                case 5: return props.RightFaceTexture;
                default: return 0;
            }
        }

        #endregion
    }
}