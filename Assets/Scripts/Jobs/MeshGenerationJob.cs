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
        // --- VOXEL DATA ---
        [ReadOnly]
        public NativeArray<uint> Map;

        [ReadOnly]
        public NativeArray<SectionJobData> SectionData;

        [ReadOnly]
        public NativeArray<BlockTypeJobData> BlockTypes;

        // --- CUSTOM MESH DATA ---
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

        // --- NEIGHBOR MAPS ---
        // 4 Cardinal Neighbors (Used for face culling)
        [ReadOnly]
        public NativeArray<uint> NeighborBack; // South (-Z)

        [ReadOnly]
        public NativeArray<uint> NeighborFront; // North (+Z)

        [ReadOnly]
        public NativeArray<uint> NeighborLeft; // West  (-X)

        [ReadOnly]
        public NativeArray<uint> NeighborRight; // East  (+X)

        // 4 Diagonal Neighbors (Used for fluid corner smoothing)
        [ReadOnly]
        public NativeArray<uint> NeighborFrontRight; // North-East

        [ReadOnly]
        public NativeArray<uint> NeighborBackRight; // South-East

        [ReadOnly]
        public NativeArray<uint> NeighborBackLeft; // South-West

        [ReadOnly]
        public NativeArray<uint> NeighborFrontLeft; // North-West

        // --- FLUID TEMPLATES ---
        [ReadOnly]
        public NativeArray<float> WaterVertexTemplates;

        [ReadOnly]
        public NativeArray<float> LavaVertexTemplates;

        // --- OUTPUT ---
        public MeshDataJobOutput Output;

        // --- INTERNAL TRACKING ---
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
            int sectionHeight = 16;
            int sectionCount = VoxelData.ChunkHeight / sectionHeight;

            for (int s = 0; s < sectionCount; s++)
            {
                SectionJobData section = SectionData[s];

                // OPTIMIZATION: Skip completely empty sections.
                if (section.IsEmpty)
                {
                    // Record empty stats for this section so the renderer knows to disable it.
                    Output.SectionStats[s] = default;
                    continue;
                }

                // Capture start indices for this section.
                int startVerts = Output.Vertices.Length;
                int startOpaque = Output.Triangles.Length;
                int startTrans = Output.TransparentTriangles.Length;
                int startFluid = Output.FluidTriangles.Length;

                int startY = s * sectionHeight;
                int endY = startY + sectionHeight;

                // OPTIMIZATION: "Shell" Iteration for fully solid sections.
                // If a section is full of opaque blocks, we only need to check the outer boundary faces
                // because internal blocks will never be visible.
                if (section.IsFullySolid)
                {
                    IterateSolidSection(startY, endY);
                }
                else
                {
                    IterateStandardSection(startY, endY);
                }

                // Store stats for this section.
                Output.SectionStats[s] = new MeshSectionStats
                {
                    VertexStartIndex = startVerts,
                    VertexCount = Output.Vertices.Length - startVerts,
                    OpaqueTriStartIndex = startOpaque,
                    OpaqueTriCount = Output.Triangles.Length - startOpaque,
                    TransparentTriStartIndex = startTrans,
                    TransparentTriCount = Output.TransparentTriangles.Length - startTrans,
                    FluidTriStartIndex = startFluid,
                    FluidTriCount = Output.FluidTriangles.Length - startFluid
                };
            }
        }

        /// <summary>
        /// Iterates only the boundaries of a solid section (Top, Bottom, Walls).
        /// </summary>
        private void IterateSolidSection(int startY, int endY)
        {
            const int width = VoxelData.ChunkWidth;
            const int max = width - 1;

            // 1. Top and Bottom Layers (Iterate fully to check Up/Down culling)
            // Loop order: Z -> X for cache locality on horizontal planes
            for (int z = 0; z < width; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    ProcessVoxel(x, startY, z); // Bottom layer
                    ProcessVoxel(x, endY - 1, z); // Top layer
                }
            }

            // 2. Middle Layers (Iterate only the X/Z walls)
            // Loop order: Z -> Y -> X roughly maintains locality
            for (int z = 0; z < width; z++)
            {
                for (int y = startY + 1; y < endY - 1; y++)
                {
                    // Check X-boundaries
                    ProcessVoxel(0, y, z);
                    ProcessVoxel(max, y, z);

                    // Check Z-boundaries (only if not already covered by X-boundaries)
                    if (z == 0 || z == max)
                    {
                        // We need to fill the row between 1 and max-1
                        for (int x = 1; x < max; x++)
                        {
                            ProcessVoxel(x, y, z);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Standard iteration over every voxel in the section.
        /// </summary>
        private void IterateStandardSection(int startY, int endY)
        {
            // Loop Order Optimization: Z -> Y -> X
            // Memory Layout: Index = x + (y * 16) + (z * 256)
            // Iterating X innermost ensures we access the NativeArray sequentially (0, 1, 2...),
            // which maximizes CPU cache hits.
            for (int z = 0; z < VoxelData.ChunkWidth; z++)
            {
                for (int y = startY; y < endY; y++)
                {
                    for (int x = 0; x < VoxelData.ChunkWidth; x++)
                    {
                        ProcessVoxel(x, y, z);
                    }
                }
            }
        }

        private void ProcessVoxel(int x, int y, int z)
        {
            int mapIndex = ChunkMath.GetFlattenedIndexInChunk(x, y, z);
            uint packedData = Map[mapIndex];
            ushort id = BurstVoxelDataBitMapping.GetId(packedData);

            if (id == 0) return; // Skip Air

            BlockTypeJobData props = BlockTypes[id];

            // Dispatch to specific mesh generation logic based on block type (Fluid, Custom, or Standard)
            GenerateVoxelMeshData(new Vector3Int(x, y, z), packedData, props);
        }

        /// <summary>
        /// The main router that decides how to mesh a block (Standard, Custom, or Fluid).
        /// </summary>
        private void GenerateVoxelMeshData(Vector3Int pos, uint packedData, BlockTypeJobData voxelProps)
        {
            ushort id = BurstVoxelDataBitMapping.GetId(packedData);

            // --- CASE 1: FLUID ---
            if (voxelProps.FluidType != FluidType.None)
            {
                // Select template
                NativeArray<float> templates = voxelProps.FluidType == FluidType.WaterLike ? WaterVertexTemplates : LavaVertexTemplates;

                // Collect 9 neighbors for smoothing
                var neighbors = new NativeArray<OptionalVoxelState>(10, Allocator.Temp);

                for (int i = 0; i < FluidNeighborOffsets.Length; i++)
                {
                    VoxelState? neighborState = GetVoxelStateFromLocalPos(pos + FluidNeighborOffsets[i]);
                    if (neighborState.HasValue) neighbors[i] = new OptionalVoxelState(neighborState.Value);
                }

                VoxelMeshHelper.GenerateFluidMeshData(in pos, packedData, in voxelProps, in templates, in BlockTypes, in neighbors,
                    ref _vertexIndex, ref Output.Vertices, ref Output.FluidTriangles, ref Output.Uvs, ref Output.Colors, ref Output.Normals);

                // Dispose the temporary native array.
                neighbors.Dispose();
            }
            // --- CASE 2: CUSTOM MESH ---
            else if (voxelProps.CustomMeshIndex > -1)
            {
                byte orientation = BurstVoxelDataBitMapping.GetOrientation(packedData);
                float rotation = VoxelHelper.GetRotationAngle(orientation);
                CustomMeshData meshData = CustomMeshes[voxelProps.CustomMeshIndex];

                for (int p = 0; p < 6; p++)
                {
                    // Skip faces not defined in the custom mesh
                    if (p >= meshData.FaceCount) continue;

                    VoxelState? neighborVoxel = GetVoxelStateFromLocalPos(pos + BurstVoxelData.FaceChecks.Data[p]);

                    if (ShouldDrawFace(voxelProps, neighborVoxel))
                    {
                        int translatedP = VoxelHelper.GetTranslatedFaceIndex(p, orientation);
                        int textureID = GetTextureID(id, translatedP);
                        float lightLevel = neighborVoxel?.lightAsFloat ?? 1.0f;

                        VoxelMeshHelper.GenerateCustomMeshFace(translatedP, textureID, lightLevel, pos, rotation,
                            voxelProps.CustomMeshIndex, ref CustomMeshes, ref CustomFaces, ref CustomVerts, ref CustomTris,
                            ref _vertexIndex, ref Output.Vertices, ref Output.Triangles, ref Output.TransparentTriangles, ref Output.Uvs,
                            ref Output.Colors, ref Output.Normals, voxelProps.RenderNeighborFaces);
                    }
                }
            }
            // --- CASE 3: STANDARD CUBE ---
            else
            {
                byte orientation = BurstVoxelDataBitMapping.GetOrientation(packedData);
                float rotation = VoxelHelper.GetRotationAngle(orientation);

                for (int p = 0; p < 6; p++)
                {
                    VoxelState? neighborVoxel = GetVoxelStateFromLocalPos(pos + BurstVoxelData.FaceChecks.Data[p]);

                    if (ShouldDrawFace(voxelProps, neighborVoxel))
                    {
                        int translatedP = VoxelHelper.GetTranslatedFaceIndex(p, orientation);
                        int textureID = GetTextureID(id, translatedP);
                        float lightLevel = neighborVoxel?.lightAsFloat ?? 1.0f;

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
            // If neighbor is null (chunk boundary/unloaded), draw the face to prevent holes.
            if (!neighborVoxel.HasValue) return true;

            BlockTypeJobData neighborProps = BlockTypes[neighborVoxel.Value.id];

            // Logic: Draw if the neighbor does NOT occlude this face.
            if (voxelProps.RenderNeighborFaces)
            {
                // If we are transparent (leaves/glass), we draw unless neighbor is opaque.
                // But if neighbor is ALSO transparent (leaves next to leaves), we draw if RenderNeighborFaces is true.
                return !neighborProps.IsSolid || neighborProps.RenderNeighborFaces;
            }

            // If we are opaque, we draw if the neighbor is transparent or not solid.
            return neighborProps.RenderNeighborFaces || !neighborProps.IsSolid;
        }

        /// <summary>
        /// Retrieves the voxel state for any position relative to the current chunk's origin.
        /// Automatically maps coordinates to the correct neighbor array if out of bounds.
        /// </summary>
        /// <param name="pos">The local position to check (e.g., (-1, 10, 16)).</param>
        /// <returns>A VoxelState if the position is in a loaded neighbor chunk, otherwise null.</returns>
        private VoxelState? GetVoxelStateFromLocalPos(Vector3Int pos)
        {
            if (pos.y < 0 || pos.y >= VoxelData.ChunkHeight) return null;

            // Fast path for internal voxels
            if (pos.x >= 0 && pos.x < VoxelData.ChunkWidth &&
                pos.z >= 0 && pos.z < VoxelData.ChunkWidth)
            {
                int idx = ChunkMath.GetFlattenedIndexInChunk(pos.x, pos.y, pos.z);
                return new VoxelState(Map[idx]);
            }

            // Neighbor Lookup Logic
            // We use a reference to avoid copying large structs, though NativeArray is a struct pointer anyway.
            NativeArray<uint> targetMap = default;
            Vector3Int localPos = pos;

            // Determine Neighbor
            if (pos.x < 0) // WEST (-X)
            {
                localPos.x += VoxelData.ChunkWidth;
                if (pos.z < 0) // South-West
                {
                    localPos.z += VoxelData.ChunkWidth;
                    targetMap = NeighborBackLeft;
                }
                else if (pos.z >= VoxelData.ChunkWidth) // North-West
                {
                    localPos.z -= VoxelData.ChunkWidth;
                    targetMap = NeighborFrontLeft;
                }
                else // West
                {
                    targetMap = NeighborLeft;
                }
            }
            else if (pos.x >= VoxelData.ChunkWidth) // EAST (+X)
            {
                localPos.x -= VoxelData.ChunkWidth;
                if (pos.z < 0) // South-East
                {
                    localPos.z += VoxelData.ChunkWidth;
                    targetMap = NeighborBackRight;
                }
                else if (pos.z >= VoxelData.ChunkWidth) // North-East
                {
                    localPos.z -= VoxelData.ChunkWidth;
                    targetMap = NeighborFrontRight;
                }
                else // East
                {
                    targetMap = NeighborRight;
                }
            }
            else // CENTER X
            {
                if (pos.z < 0) // South
                {
                    localPos.z += VoxelData.ChunkWidth;
                    targetMap = NeighborBack;
                }
                else if (pos.z >= VoxelData.ChunkWidth) // North
                {
                    localPos.z -= VoxelData.ChunkWidth;
                    targetMap = NeighborFront;
                }
                // Center case handled by fast path at top
            }

            if (!targetMap.IsCreated || targetMap.Length == 0) return null;

            int mapIndex = ChunkMath.GetFlattenedIndexInChunk(localPos.x, localPos.y, localPos.z);
            return new VoxelState(targetMap[mapIndex]);
        }

        #endregion


        #region Texture Methods

        private int GetTextureID(ushort blockId, int faceIndex)
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
