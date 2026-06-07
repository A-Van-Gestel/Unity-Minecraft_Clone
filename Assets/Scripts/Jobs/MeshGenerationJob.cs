using System.Runtime.CompilerServices;
using Data;
using Data.Enums;
using Helpers;
using Jobs.BurstData;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
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

        /// <summary>
        /// Axis-aligned clip bounds. Voxels at coordinates &gt;= each Max value are treated as air.
        /// Use <see cref="MeshClipBounds.Disabled"/> for no clipping.
        /// </summary>
        public MeshClipBounds ClipBounds;

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

        // --- LIGHT MAPS (Phase 2 RGB) ---
        [ReadOnly]
        public NativeArray<ushort> LightMap;

        [ReadOnly]
        public NativeArray<ushort> LightBack;

        [ReadOnly]
        public NativeArray<ushort> LightFront;

        [ReadOnly]
        public NativeArray<ushort> LightLeft;

        [ReadOnly]
        public NativeArray<ushort> LightRight;

        [ReadOnly]
        public NativeArray<ushort> LightFrontRight;

        [ReadOnly]
        public NativeArray<ushort> LightBackRight;

        [ReadOnly]
        public NativeArray<ushort> LightBackLeft;

        [ReadOnly]
        public NativeArray<ushort> LightFrontLeft;

        // --- FLUID TEMPLATES ---
        [ReadOnly]
        public NativeArray<float> WaterVertexTemplates;

        [ReadOnly]
        public NativeArray<float> LavaVertexTemplates;

        // --- SETTINGS ---
        public SmoothLightingQuality SmoothLighting;

        // --- OUTPUT ---
        public MeshDataJobOutput Output;

        // --- INTERNAL TRACKING ---
        private int _vertexIndex;
        private int _clipMaxY;
        private int _clipLocalMaxX;
        private int _clipLocalMaxZ;

        // --- HELPERS ---
        private static readonly Vector3Int[] s_fluidNeighborOffsets =
        {
            new Vector3Int(0, 0, 1), new Vector3Int(1, 0, 0), new Vector3Int(0, 0, -1), new Vector3Int(-1, 0, 0), // N, E, S, W
            new Vector3Int(1, 0, 1), new Vector3Int(1, 0, -1), new Vector3Int(-1, 0, -1), new Vector3Int(-1, 0, 1), // NE, SE, SW, NW
            new Vector3Int(0, 1, 0), new Vector3Int(0, -1, 0), // Above, Below
            new Vector3Int(0, 1, 1), new Vector3Int(1, 1, 0), new Vector3Int(0, 1, -1), new Vector3Int(-1, 1, 0), // Above_N, Above_E, Above_S, Above_W
        };

        /// <summary>
        /// Executes the mesh generation logic across all sections of the chunk, iterating through voxels to build visual face data.
        /// </summary>
        public void Execute()
        {
            _vertexIndex = 0;

            // Precompute effective clip bounds once per job execution.
            // Y has no vertical neighbor chunks, so the disabled fallback is ChunkHeight (128).
            // X/Z neighbor lookups reach pos 16/-1, so disabled must exceed that range.
            _clipMaxY = ClipBounds.MaxY < int.MaxValue ? ClipBounds.MaxY : VoxelData.ChunkHeight;
            int originX = (int)ChunkPosition.x;
            int originZ = (int)ChunkPosition.z;
            _clipLocalMaxX = ClipBounds.MaxX < int.MaxValue ? ClipBounds.MaxX - originX : int.MaxValue;
            _clipLocalMaxZ = ClipBounds.MaxZ < int.MaxValue ? ClipBounds.MaxZ - originZ : int.MaxValue;

            const int sectionHeight = 16;
            const int sectionCount = VoxelData.ChunkHeight / sectionHeight;

            // Early-out: if the chunk is entirely beyond any clip axis, emit empty stats.
            if (_clipMaxY <= 0 || _clipLocalMaxX <= 0 || _clipLocalMaxZ <= 0)
            {
                for (int s = 0; s < sectionCount; s++)
                    Output.SectionStats[s] = default;
                return;
            }

            for (int s = 0; s < sectionCount; s++)
            {
                int startY = s * sectionHeight;

                // Skip sections entirely above the visible Y limit.
                if (startY >= _clipMaxY)
                {
                    Output.SectionStats[s] = default;
                    continue;
                }

                SectionJobData section = SectionData[s];

                // OPTIMIZATION: Skip completely empty sections.
                if (section.IsEmpty)
                {
                    Output.SectionStats[s] = default;
                    continue;
                }

                // Capture start indices for this section.
                int startVerts = Output.Vertices.Length;
                int startOpaque = Output.Triangles.Length;
                int startTrans = Output.TransparentTriangles.Length;
                int startFluid = Output.FluidTriangles.Length;

                int endY = math.min(startY + sectionHeight, _clipMaxY);
                bool isSectionFullyVisible = endY == startY + sectionHeight;
                bool isXZFullyVisible = _clipLocalMaxX >= VoxelData.ChunkWidth
                                        && _clipLocalMaxZ >= VoxelData.ChunkWidth;

                // OPTIMIZATION: "Shell" Iteration for fully solid sections.
                // Only valid when the full section is visible on all axes — any clip
                // boundary creates internal faces that the shell optimization does not cover.
                if (section.IsFullySolid && isSectionFullyVisible && isXZFullyVisible)
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
                    FluidTriCount = Output.FluidTriangles.Length - startFluid,
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
                    if (z is 0 or max)
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
            int xEnd = math.min(VoxelData.ChunkWidth, _clipLocalMaxX);
            int zEnd = math.min(VoxelData.ChunkWidth, _clipLocalMaxZ);
            for (int z = 0; z < zEnd; z++)
            {
                for (int y = startY; y < endY; y++)
                {
                    for (int x = 0; x < xEnd; x++)
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

            if (id == BlockIDs.Air) return; // Skip Air

            BlockTypeJobData props = BlockTypes[id];

            // Dispatch to specific mesh generation logic based on block type (Fluid, Custom, or Standard)
            GenerateVoxelMeshData(new Vector3Int(x, y, z), packedData, props);
        }

        /// <summary>
        /// The main router that decides how to mesh a block (Standard, Custom, Cross, or Fluid).
        /// </summary>
        /// <remarks>
        /// <para>For the standard-cube and custom-mesh cases, the router additionally dispatches on
        /// the block's <see cref="MetadataSchema"/> per <c>PER_BLOCK_METADATA_SCHEMAS.md §7.5</c>.
        /// Today every schema routes to the legacy world-face/orientation-storage-index path; Phase 2b
        /// adds dedicated arms (e.g. <see cref="MetadataSchema.Axis3"/>) that read the meta byte
        /// directly and use precomputed face/UV variants instead of per-voxel quaternion rotation.</para>
        /// <para>The Fluid (case 1) and CrossMesh (case 2) paths are not schema-dispatched — fluids
        /// always interpret the meta byte as a fluid level via the existing <c>GenerateFluidMeshData</c>
        /// path, and cross meshes do not use orientation at all.</para>
        /// </remarks>
        private void GenerateVoxelMeshData(Vector3Int pos, uint packedData, BlockTypeJobData voxelProps)
        {
            ushort id = BurstVoxelDataBitMapping.GetId(packedData);

            // --- CASE 1: FLUID ---
            if (voxelProps.FluidType != FluidType.None)
            {
                // Select template
                NativeArray<float> templates = voxelProps.FluidType == FluidType.WaterLike ? WaterVertexTemplates : LavaVertexTemplates;

                // Collect 14 neighbors for smoothing & culling
                NativeArray<OptionalVoxelState> neighbors = new NativeArray<OptionalVoxelState>(14, Allocator.Temp);

                for (int i = 0; i < s_fluidNeighborOffsets.Length; i++)
                {
                    VoxelState? neighborState = GetVoxelStateFromLocalPos(pos + s_fluidNeighborOffsets[i]);
                    if (neighborState.HasValue) neighbors[i] = new OptionalVoxelState(neighborState.Value);
                }

                FluidCornerLights cornerLights = default;
                if (SmoothLighting >= SmoothLightingQuality.Standard)
                {
                    for (int face = 0; face < 6; face++)
                    {
                        // Reuse the already-fetched 14-neighbor array instead of re-calling GetVoxelStateFromLocalPos.
                        // Mapping: Back(-Z)→2(S), Front(+Z)→0(N), Top(+Y)→8(Above), Bottom(-Y)→9(Below), Left(-X)→3(W), Right(+X)→1(E)
                        int neighborIdx = face switch { 0 => 2, 1 => 0, 2 => 8, 3 => 9, 4 => 3, 5 => 1, _ => 0 };
                        OptionalVoxelState cached = neighbors[neighborIdx];
                        VoxelState? directNeighbor = cached.HasValue ? new VoxelState?(cached.State) : null;
                        CalculateCornerLights(face, pos, directNeighbor,
                            out Color32 l0, out Color32 l1, out Color32 l2, out Color32 l3);
                        cornerLights.SetFace(face, l0, l1, l2, l3);
                    }
                }

                VoxelMeshHelper.GenerateFluidMeshData(in pos, packedData, in voxelProps, in templates, in BlockTypes, in neighbors,
                    SmoothLighting >= SmoothLightingQuality.Standard, in cornerLights,
                    ref _vertexIndex, ref Output.Vertices, ref Output.FluidTriangles, ref Output.Uvs, ref Output.Colors, ref Output.Normals,
                    ref Output.LightData);

                // Dispose the temporary native array.
                neighbors.Dispose();
                return; // Fluid blocks are never also a custom mesh or standard cube.
            }

            // --- CASE 2: CROSS MESH ---
            if (voxelProps.RenderShape == RenderShape.CrossMesh)
            {
                int textureID = voxelProps.SideFaceTexture;
                CrossMeshCornerLights crossLights = default;

                if (SmoothLighting >= SmoothLightingQuality.Standard)
                {
                    // Top-level corners: sample the block above the flora (Top face at pos).
                    VoxelState? aboveNeighbor = GetVoxelStateFromLocalPos(pos + BurstVoxelData.FaceChecks.Data[2]);
                    CalculateCornerLights(2, pos, aboveNeighbor,
                        out crossLights.TopL0, out crossLights.TopL1, out crossLights.TopL2, out crossLights.TopL3);

                    if (SmoothLighting >= SmoothLightingQuality.High)
                    {
                        // Bottom-level corners: sample Top face of the block below (light at ground level).
                        // The direct neighbor for this shifted sample is the flora block itself.
                        VoxelState? centerVoxel = GetVoxelStateFromLocalPos(pos);
                        Vector3Int belowPos = pos + BurstVoxelData.FaceChecks.Data[3];
                        CalculateCornerLights(2, belowPos, centerVoxel,
                            out crossLights.BotL0, out crossLights.BotL1, out crossLights.BotL2, out crossLights.BotL3);
                    }
                    else
                    {
                        // Standard: bottom vertices use the same light as top (no vertical gradient).
                        crossLights.BotL0 = crossLights.TopL0;
                        crossLights.BotL1 = crossLights.TopL1;
                        crossLights.BotL2 = crossLights.TopL2;
                        crossLights.BotL3 = crossLights.TopL3;
                    }
                }
                else
                {
                    // Off: flat lighting from the flora block's own light level.
                    ushort blockLightData = GetLightDataFromLocalPos(pos);
                    Color32 flat = new Color32(
                        (byte)(LightBitMapping.GetSkyLight(blockLightData) * 17),
                        (byte)(LightBitMapping.GetBlocklightR(blockLightData) * 17),
                        (byte)(LightBitMapping.GetBlocklightG(blockLightData) * 17),
                        (byte)(LightBitMapping.GetBlocklightB(blockLightData) * 17));
                    crossLights.TopL0 = crossLights.TopL1 = crossLights.TopL2 = crossLights.TopL3 = flat;
                    crossLights.BotL0 = crossLights.BotL1 = crossLights.BotL2 = crossLights.BotL3 = flat;
                }

                VoxelMeshHelper.GenerateCrossMesh(textureID, in crossLights,
                    pos, ref _vertexIndex, ref Output.Vertices, ref Output.TransparentTriangles, ref Output.Uvs, ref Output.Colors, ref Output.Normals,
                    ref Output.LightData);
                return;
            }

            // --- CASE 3: CUSTOM MESH ---
            if (voxelProps.RenderShape == RenderShape.CustomMesh && voxelProps.CustomMeshIndex > -1)
            {
                switch (voxelProps.MetadataSchema)
                {
                    case MetadataSchema.None:
                    case MetadataSchema.Axis3:
                    case MetadataSchema.Facing6:
                    case MetadataSchema.Facing6Roll2:
                    case MetadataSchema.HorizontalOnly:
                        GenerateCustomBlockMesh_SchemaAware(pos, packedData, id, voxelProps);
                        break;
                    default:
                        GenerateCustomBlockMesh_Legacy(pos, packedData, id, voxelProps);
                        break;
                }

                return;
            }

            // --- CASE 4: STANDARD CUBE ---
            switch (voxelProps.MetadataSchema)
            {
                case MetadataSchema.None:
                    GenerateStandardCubeMesh_None(pos, id, voxelProps);
                    break;
                case MetadataSchema.Axis3:
                    GenerateStandardCubeMesh_Axis3(pos, packedData, id, voxelProps);
                    break;
                case MetadataSchema.Facing6:
                    GenerateStandardCubeMesh_Facing6(pos, packedData, id, voxelProps);
                    break;
                case MetadataSchema.Facing6Roll2:
                    GenerateStandardCubeMesh_Facing6Roll2(pos, packedData, id, voxelProps);
                    break;
                case MetadataSchema.HorizontalOnly:
                    GenerateStandardCubeMesh_HorizontalOnly(pos, packedData, id, voxelProps);
                    break;
                default:
                    GenerateStandardCubeMesh_Legacy(pos, packedData, id, voxelProps);
                    break;
            }
        }

        /// <summary>
        /// Legacy custom-mesh meshing path: decodes a world-face orientation from the packed voxel,
        /// converts it to a Y-axis rotation angle via <see cref="VoxelHelper.GetRotationAngle"/>, and
        /// emits each face of the custom mesh with that rotation applied.
        /// </summary>
        /// <remarks>
        /// Called by <see cref="GenerateVoxelMeshData"/> for blocks whose <see cref="MetadataSchema"/>
        /// has not yet been migrated to a schema-aware variant. Phase 2b adds dedicated variants for
        /// <see cref="MetadataSchema.Axis3"/> and (later) <see cref="MetadataSchema.Facing6"/>.
        /// </remarks>
        private void GenerateCustomBlockMesh_Legacy(Vector3Int pos, uint packedData, ushort id, BlockTypeJobData voxelProps)
        {
            byte orientation = BurstVoxelDataBitMapping.GetOrientation(packedData);
            float rotation = VoxelHelper.GetRotationAngle(orientation);
            CustomMeshData meshData = CustomMeshes[voxelProps.CustomMeshIndex];

            for (int p = 0; p < 6; p++)
            {
                // Skip faces not defined in the custom mesh
                if (p >= meshData.FaceCount) continue;

                Vector3Int neighborPos = pos + BurstVoxelData.FaceChecks.Data[p];
                VoxelState? neighborVoxel = GetVoxelStateFromLocalPos(neighborPos);

                if (ShouldDrawFace(voxelProps, neighborVoxel))
                {
                    int translatedP = VoxelHelper.GetTranslatedFaceIndex(p, orientation);
                    int textureID = GetTextureID(id, translatedP);

                    if (SmoothLighting >= SmoothLightingQuality.Standard)
                    {
                        CalculateCornerLights(p, pos, neighborVoxel, out Color32 l0, out Color32 l1, out Color32 l2, out Color32 l3);
                        VoxelMeshHelper.GenerateCustomMeshFace(translatedP, textureID, pos, rotation,
                            p, l0, l1, l2, l3,
                            voxelProps.CustomMeshIndex, in CustomMeshes, in CustomFaces, in CustomVerts, in CustomTris,
                            ref _vertexIndex, ref Output.Vertices, ref Output.Triangles, ref Output.TransparentTriangles, ref Output.Uvs,
                            ref Output.Colors, ref Output.Normals, ref Output.LightData, voxelProps.RenderNeighborFaces);
                    }
                    else
                    {
                        Color32 flatLight = BuildFlatLightData(neighborVoxel, neighborPos);
                        VoxelMeshHelper.GenerateCustomMeshFace(translatedP, textureID, flatLight, pos, rotation,
                            voxelProps.CustomMeshIndex, in CustomMeshes, in CustomFaces, in CustomVerts, in CustomTris,
                            ref _vertexIndex, ref Output.Vertices, ref Output.Triangles, ref Output.TransparentTriangles, ref Output.Uvs,
                            ref Output.Colors, ref Output.Normals, ref Output.LightData, voxelProps.RenderNeighborFaces);
                    }
                }
            }
        }

        /// <summary>
        /// Schema-aware custom-mesh meshing path: decodes the rotation matrix from the metadata
        /// byte via <see cref="BurstCustomMeshRotationUtility.GetRotationMatrix"/> and applies
        /// full 3D rotation to every custom mesh vertex and normal.
        /// </summary>
        /// <remarks>
        /// Handles <see cref="MetadataSchema.Axis3"/>, <see cref="MetadataSchema.Facing6"/>,
        /// <see cref="MetadataSchema.Facing6Roll2"/>, and <see cref="MetadataSchema.HorizontalOnly"/>.
        /// Face culling rotates the neighbor-check direction through the same rotation matrix
        /// as the vertices, ensuring correct occlusion for all orientations.
        /// </remarks>
        private void GenerateCustomBlockMesh_SchemaAware(Vector3Int pos, uint packedData, ushort id, BlockTypeJobData voxelProps)
        {
            byte meta = BurstVoxelDataBitMapping.GetMeta(packedData);
            float3x3 matrix = BurstCustomMeshRotationUtility.GetRotationMatrix(
                voxelProps.MetadataSchema, meta, voxelProps.DefaultMetadata);

            CustomMeshData meshData = CustomMeshes[voxelProps.CustomMeshIndex];

            for (int p = 0; p < 6; p++)
            {
                // Skip faces not defined in the custom mesh
                if (p >= meshData.FaceCount) continue;

                // Rotate the cull-check direction through the same matrix as the vertices.
                // All rotation matrices are 90° multiples, so the result is always exactly ±1
                // on one axis after rounding — no floating-point edge cases.
                Vector3Int faceCheck = BurstVoxelData.FaceChecks.Data[p];
                float3 rotatedCheck = math.round(math.mul(matrix, new float3(faceCheck.x, faceCheck.y, faceCheck.z)));
                Vector3Int rotatedOffset = new Vector3Int((int)rotatedCheck.x, (int)rotatedCheck.y, (int)rotatedCheck.z);
                Vector3Int neighborPos2 = pos + rotatedOffset;
                VoxelState? neighborVoxel = GetVoxelStateFromLocalPos(neighborPos2);

                if (ShouldDrawFace(voxelProps, neighborVoxel))
                {
                    int textureID = GetTextureID(id, p);

                    if (SmoothLighting >= SmoothLightingQuality.Standard)
                    {
                        int worldFace = DirectionToFaceIndex(rotatedOffset);
                        CalculateCornerLights(worldFace, pos, neighborVoxel, out Color32 l0, out Color32 l1, out Color32 l2, out Color32 l3);
                        VoxelMeshHelper.GenerateCustomMeshFace(p, textureID, pos, in matrix,
                            worldFace, l0, l1, l2, l3,
                            voxelProps.CustomMeshIndex, in CustomMeshes, in CustomFaces, in CustomVerts, in CustomTris,
                            ref _vertexIndex, ref Output.Vertices, ref Output.Triangles, ref Output.TransparentTriangles,
                            ref Output.Uvs, ref Output.Colors, ref Output.Normals, ref Output.LightData, voxelProps.RenderNeighborFaces);
                    }
                    else
                    {
                        Color32 flatLight = BuildFlatLightData(neighborVoxel, neighborPos2);
                        VoxelMeshHelper.GenerateCustomMeshFace(p, textureID, flatLight, pos, in matrix,
                            voxelProps.CustomMeshIndex, in CustomMeshes, in CustomFaces, in CustomVerts, in CustomTris,
                            ref _vertexIndex, ref Output.Vertices, ref Output.Triangles, ref Output.TransparentTriangles,
                            ref Output.Uvs, ref Output.Colors, ref Output.Normals, ref Output.LightData, voxelProps.RenderNeighborFaces);
                    }
                }
            }
        }


        /// <summary>
        /// Standard-cube meshing path for <see cref="MetadataSchema.None"/> blocks (Air, Facade,
        /// Cactus, etc.). No rotation is applied — each world face maps 1:1 to the matching block
        /// face texture, with no UV rotation.
        /// </summary>
        private void GenerateStandardCubeMesh_None(Vector3Int pos, ushort id, BlockTypeJobData voxelProps)
        {
            for (int p = 0; p < 6; p++)
                EmitStandardCubeFaceIfVisible(pos, id, voxelProps, worldFace: p, effectiveFace: p, uvQuarterTurnsCW: 0);
        }

        /// <summary>
        /// Legacy standard-cube meshing path: decodes a world-face orientation from the packed voxel
        /// and delegates to <see cref="GenerateStandardCubeWithLegacyOrientation"/>.
        /// </summary>
        /// <remarks>
        /// Called by <see cref="GenerateVoxelMeshData"/> for blocks whose <see cref="MetadataSchema"/>
        /// has not yet been migrated to a schema-aware variant. Phase 2b will add a dedicated
        /// <see cref="MetadataSchema.Axis3"/> variant that selects precomputed X/Y/Z face arrays
        /// instead of running per-voxel quaternion rotation in this hot path.
        /// </remarks>
        private void GenerateStandardCubeMesh_Legacy(Vector3Int pos, uint packedData, ushort id, BlockTypeJobData voxelProps)
        {
            byte orientation = BurstVoxelDataBitMapping.GetOrientation(packedData);
            GenerateStandardCubeWithLegacyOrientation(pos, id, voxelProps, orientation);
        }

        /// <summary>
        /// Schema-aware standard-cube meshing path for <see cref="MetadataSchema.HorizontalOnly"/> blocks.
        /// Maps the 4-way yaw to a legacy orientation index and delegates to
        /// <see cref="GenerateStandardCubeWithLegacyOrientation"/>.
        /// </summary>
        private void GenerateStandardCubeMesh_HorizontalOnly(Vector3Int pos, uint packedData, ushort id, BlockTypeJobData voxelProps)
        {
            byte meta = BurstVoxelDataBitMapping.GetMeta(packedData);
            byte normalizedDefaultMeta = BurstVoxelMetadataUtility.NormalizeMeta(
                MetadataSchema.HorizontalOnly, voxelProps.DefaultMetadata, 0); // Default to North (0)
            byte normalizedMeta = BurstVoxelMetadataUtility.NormalizeMeta(
                MetadataSchema.HorizontalOnly, meta, normalizedDefaultMeta);

            byte yaw = BurstVoxelMetadataUtility.DecodeHorizontalOnly(normalizedMeta);

            // Map the HorizontalOnly yaw (0=North, 1=South, 2=West, 3=East)
            // to the legacy orientation indices (1=North, 0=South, 4=West, 5=East)
            // so we can reuse VoxelHelper.GetRotationAngle and GetTranslatedFaceIndex.
            byte legacyOrientation = yaw switch
            {
                0 => VoxelOrientation.North, // North
                1 => VoxelOrientation.South, // South
                2 => VoxelOrientation.West, // West
                3 => VoxelOrientation.East, // East
                _ => VoxelOrientation.North,
            };

            GenerateStandardCubeWithLegacyOrientation(pos, id, voxelProps, legacyOrientation);
        }

        /// <summary>
        /// Shared inner loop for legacy-orientation standard-cube meshing. Converts a legacy
        /// world-face orientation index to a Y-axis rotation angle and emits each visible face.
        /// </summary>
        /// <remarks>
        /// Called by both <see cref="GenerateStandardCubeMesh_Legacy"/> (orientation decoded
        /// directly from packed data) and <see cref="GenerateStandardCubeMesh_HorizontalOnly"/>
        /// (yaw mapped to a legacy orientation index before calling here).
        /// </remarks>
        private void GenerateStandardCubeWithLegacyOrientation(Vector3Int pos, ushort id, BlockTypeJobData voxelProps, byte orientation)
        {
            float rotation = VoxelHelper.GetRotationAngle(orientation);

            for (int p = 0; p < 6; p++)
            {
                Vector3Int neighborPos = pos + BurstVoxelData.FaceChecks.Data[p];
                VoxelState? neighborVoxel = GetVoxelStateFromLocalPos(neighborPos);

                if (ShouldDrawFace(voxelProps, neighborVoxel))
                {
                    int translatedP = VoxelHelper.GetTranslatedFaceIndex(p, orientation);
                    int textureID = GetTextureID(id, translatedP);

                    if (SmoothLighting >= SmoothLightingQuality.Standard)
                    {
                        CalculateCornerLights(p, pos, neighborVoxel, out Color32 l0, out Color32 l1, out Color32 l2, out Color32 l3);
                        PermuteCornerLightsForYRotation(p, rotation, ref l0, ref l1, ref l2, ref l3);
                        VoxelMeshHelper.GenerateStandardCubeFace(translatedP, textureID, in pos, rotation,
                            0, l0, l1, l2, l3,
                            ref _vertexIndex, ref Output.Vertices, ref Output.Triangles, ref Output.TransparentTriangles,
                            ref Output.Uvs, ref Output.Colors, ref Output.Normals,
                            ref Output.LightData, voxelProps.RenderNeighborFaces);
                    }
                    else
                    {
                        Color32 flat = BuildFlatLightData(neighborVoxel, neighborPos);
                        VoxelMeshHelper.GenerateStandardCubeFace(translatedP, textureID, in pos, rotation,
                            0, flat, flat, flat, flat,
                            ref _vertexIndex, ref Output.Vertices, ref Output.Triangles, ref Output.TransparentTriangles,
                            ref Output.Uvs, ref Output.Colors, ref Output.Normals,
                            ref Output.LightData, voxelProps.RenderNeighborFaces);
                    }
                }
            }
        }

        /// <summary>
        /// Checks visibility of a single cube face and, if visible, emits its vertices, UVs, and
        /// color into the output mesh buffers.
        /// </summary>
        /// <param name="pos">Block position in chunk-local space.</param>
        /// <param name="id">Block type ID used for texture lookup.</param>
        /// <param name="voxelProps">Block properties (transparency, render-neighbor-faces flag, etc.).</param>
        /// <param name="worldFace">Cardinal face index (0-5) used for neighbor sampling and vertex emission.</param>
        /// <param name="effectiveFace">Remapped face index used for texture selection after schema rotation.</param>
        /// <param name="uvQuarterTurnsCW">Number of 90° clockwise UV rotations to apply (0-3).</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EmitStandardCubeFaceIfVisible(
            Vector3Int pos, ushort id, BlockTypeJobData voxelProps,
            int worldFace, int effectiveFace, int uvQuarterTurnsCW)
        {
            Vector3Int neighborPos = pos + BurstVoxelData.FaceChecks.Data[worldFace];
            VoxelState? neighborVoxel = GetVoxelStateFromLocalPos(neighborPos);
            if (!ShouldDrawFace(voxelProps, neighborVoxel)) return;

            int textureID = GetTextureID(id, effectiveFace);

            if (SmoothLighting >= SmoothLightingQuality.Standard)
            {
                CalculateCornerLights(worldFace, pos, neighborVoxel, out Color32 l0, out Color32 l1, out Color32 l2, out Color32 l3);
                VoxelMeshHelper.GenerateStandardCubeFace(worldFace, textureID, in pos, rotation: 0f, uvQuarterTurnsCW,
                    l0, l1, l2, l3,
                    ref _vertexIndex, ref Output.Vertices, ref Output.Triangles, ref Output.TransparentTriangles,
                    ref Output.Uvs, ref Output.Colors, ref Output.Normals,
                    ref Output.LightData, voxelProps.RenderNeighborFaces);
            }
            else
            {
                Color32 flat = BuildFlatLightData(neighborVoxel, neighborPos);
                VoxelMeshHelper.GenerateStandardCubeFace(worldFace, textureID, in pos, rotation: 0f, uvQuarterTurnsCW,
                    flat, flat, flat, flat,
                    ref _vertexIndex, ref Output.Vertices, ref Output.Triangles, ref Output.TransparentTriangles,
                    ref Output.Uvs, ref Output.Colors, ref Output.Normals,
                    ref Output.LightData, voxelProps.RenderNeighborFaces);
            }
        }

        /// <summary>
        /// Schema-aware standard-cube meshing path for <see cref="MetadataSchema.Axis3"/> blocks
        /// (logs, pillars, fallen trunks). Performs no per-voxel rotation — the cube vertices are
        /// emitted in their canonical positions and the per-face texture is selected via the
        /// frozen face-remap LUT in <see cref="BurstAxis3MeshUtility"/>.
        /// </summary>
        /// <remarks>
        /// <para>This is the Phase 2b primary cost-reduction path: replaces
        /// <see cref="VoxelHelper.GetRotationAngle"/> + <see cref="UnityEngine.Quaternion.Euler"/>
        /// per face with one O(1) byte-array lookup. The baseline (<c>Documentation/Performance/PHASE_02_BASELINE.md</c>)
        /// measured the legacy rotation overhead at ~1.3 ns/face — this path should land well under that.</para>
        /// <para>UV rotation per axis (so wood-grain side textures align with the log's long axis) is
        /// not yet implemented. Without it, side-face bark grain stays "vertical" regardless of axis;
        /// this is a visual defect to be addressed in a follow-up commit, not a correctness defect.</para>
        /// </remarks>
        private void GenerateStandardCubeMesh_Axis3(Vector3Int pos, uint packedData, ushort id, BlockTypeJobData voxelProps)
        {
            byte meta = BurstVoxelDataBitMapping.GetMeta(packedData);
            byte normalizedDefaultMeta = BurstVoxelMetadataUtility.NormalizeMeta(
                MetadataSchema.Axis3, voxelProps.DefaultMetadata, BurstVoxelMetadataUtility.AXIS_Y);
            byte normalizedMeta = BurstVoxelMetadataUtility.NormalizeMeta(
                MetadataSchema.Axis3, meta, normalizedDefaultMeta);
            byte axis = BurstVoxelMetadataUtility.DecodeAxis3(normalizedMeta);

            for (int p = 0; p < 6; p++)
            {
                // Texture comes from the axis-remapped block face. Vertex emission uses the
                // un-rotated world face index `p`, since cube vertices are axis-symmetric.
                EmitStandardCubeFaceIfVisible(pos, id, voxelProps, worldFace: p,
                    effectiveFace: BurstAxis3MeshUtility.GetEffectiveFace(axis, p),
                    uvQuarterTurnsCW: BurstAxis3MeshUtility.GetUvQuarterTurnsCW(axis, p));
            }
        }

        /// <summary>
        /// Schema-aware standard-cube meshing path for <see cref="MetadataSchema.Facing6"/> blocks
        /// (directional blocks, observers, dispensers). Uses precomputed face-remap LUTs in
        /// <see cref="BurstFacing6MeshUtility"/> — no per-voxel quaternion rotation.
        /// </summary>
        private void GenerateStandardCubeMesh_Facing6(Vector3Int pos, uint packedData, ushort id, BlockTypeJobData voxelProps)
        {
            byte meta = BurstVoxelDataBitMapping.GetMeta(packedData);
            byte normalizedDefaultMeta = BurstVoxelMetadataUtility.NormalizeMeta(
                MetadataSchema.Facing6, voxelProps.DefaultMetadata, 0); // 0 = South, always valid
            byte facing = BurstVoxelMetadataUtility.NormalizeMeta(
                MetadataSchema.Facing6, meta, normalizedDefaultMeta);

            for (int p = 0; p < 6; p++)
            {
                EmitStandardCubeFaceIfVisible(pos, id, voxelProps, worldFace: p,
                    effectiveFace: BurstFacing6MeshUtility.GetEffectiveFace(facing, p),
                    uvQuarterTurnsCW: BurstFacing6MeshUtility.GetUvQuarterTurnsCW(facing, p));
            }
        }

        /// <summary>
        /// Schema-aware standard-cube meshing path for <see cref="MetadataSchema.Facing6Roll2"/> blocks.
        /// Uses precomputed face-remap LUTs in <see cref="BurstFacing6Roll2MeshUtility"/>.
        /// </summary>
        private void GenerateStandardCubeMesh_Facing6Roll2(Vector3Int pos, uint packedData, ushort id, BlockTypeJobData voxelProps)
        {
            byte meta = BurstVoxelDataBitMapping.GetMeta(packedData);
            byte normalizedDefaultMeta = BurstVoxelMetadataUtility.NormalizeMeta(
                MetadataSchema.Facing6Roll2, voxelProps.DefaultMetadata, 0); // 0 = South+Roll0, always valid
            byte normalizedMeta = BurstVoxelMetadataUtility.NormalizeMeta(
                MetadataSchema.Facing6Roll2, meta, normalizedDefaultMeta);
            BurstVoxelMetadataUtility.DecodeFacing6Roll2(normalizedMeta, out byte facing, out byte roll);

            for (int p = 0; p < 6; p++)
            {
                EmitStandardCubeFaceIfVisible(pos, id, voxelProps, worldFace: p,
                    effectiveFace: BurstFacing6Roll2MeshUtility.GetEffectiveFace(facing, roll, p),
                    uvQuarterTurnsCW: BurstFacing6Roll2MeshUtility.GetUvQuarterTurnsCW(facing, roll, p));
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

            BlockTypeJobData neighborProps = BlockTypes[neighborVoxel.Value.ID];

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
        /// Builds a flat (uniform) light Color32 from a neighbor voxel state with separate
        /// sun and block channels. Used by the flat lighting fallback paths.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Color32 BuildFlatLightData(VoxelState? neighborVoxel, Vector3Int neighborPos)
        {
            if (!neighborVoxel.HasValue)
            {
                const byte fullSun = 15 * 17; // 255
                return new Color32(fullSun, 0, 0, 0);
            }

            ushort lightData = GetLightDataFromLocalPos(neighborPos);
            byte sun = (byte)(LightBitMapping.GetSkyLight(lightData) * 17);
            byte blockR = (byte)(LightBitMapping.GetBlocklightR(lightData) * 17);
            byte blockG = (byte)(LightBitMapping.GetBlocklightG(lightData) * 17);
            byte blockB = (byte)(LightBitMapping.GetBlocklightB(lightData) * 17);
            return new Color32(sun, blockR, blockG, blockB);
        }

        /// <summary>
        /// Maps a cardinal direction <see cref="Vector3Int"/> to the corresponding face index (0–5).
        /// Only valid for exact axis-aligned unit vectors (the 6 entries in <c>FaceChecks</c>).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int DirectionToFaceIndex(Vector3Int dir)
        {
            if (dir.z == -1) return 0; // Back
            if (dir.z == 1) return 1; // Front
            if (dir.y == 1) return 2; // Top
            if (dir.y == -1) return 3; // Bottom
            if (dir.x == -1) return 4; // Left
            return 5; // Right
        }

        /// <summary>
        /// Permutes smooth-light corner values to compensate for Y-axis rotation on horizontal
        /// faces (Top/Bottom). Side faces do not need permutation because
        /// <see cref="VoxelHelper.GetTranslatedFaceIndex"/> remaps the face index so the rotated
        /// vertex ordering already matches the world corner positions.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void PermuteCornerLightsForYRotation(int worldFaceIndex, float rotation,
            ref Color32 l0, ref Color32 l1, ref Color32 l2, ref Color32 l3)
        {
            if (worldFaceIndex != 2 && worldFaceIndex != 3) return;

            int steps = (int)math.round(rotation / 90f) & 3;
            if (steps == 0) return;

            Color32 t0 = l0, t1 = l1, t2 = l2, t3 = l3;

            if (worldFaceIndex == 2) // Top face
            {
                switch (steps)
                {
                    case 1: // 90° CW
                        l0 = t1;
                        l1 = t3;
                        l2 = t0;
                        l3 = t2;
                        break;
                    case 2: // 180°
                        l0 = t3;
                        l1 = t2;
                        l2 = t1;
                        l3 = t0;
                        break;
                    case 3: // 270° CW
                        l0 = t2;
                        l1 = t0;
                        l2 = t3;
                        l3 = t1;
                        break;
                }
            }
            else // Bottom face
            {
                switch (steps)
                {
                    case 1: // 90° CW
                        l0 = t2;
                        l1 = t0;
                        l2 = t3;
                        l3 = t1;
                        break;
                    case 2: // 180°
                        l0 = t3;
                        l1 = t2;
                        l2 = t1;
                        l3 = t0;
                        break;
                    case 3: // 270° CW
                        l0 = t1;
                        l1 = t3;
                        l2 = t0;
                        l3 = t2;
                        break;
                }
            }
        }

        /// <summary>
        /// Computes per-vertex corner-averaged light values for smooth lighting.
        /// Samples 4 neighboring blocks per corner (direct + sideA + sideB + diagonal)
        /// and averages sunlight and blocklight channels independently.
        /// </summary>
        private void CalculateCornerLights(int faceIndex, Vector3Int blockPos,
            VoxelState? directNeighbor,
            out Color32 l0, out Color32 l1, out Color32 l2, out Color32 l3)
        {
            // Extract light from the pre-fetched direct neighbor (same for all 4 corners).
            byte directSun, directR, directG, directB;
            if (!directNeighbor.HasValue)
            {
                directSun = 15;
                directR = 0;
                directG = 0;
                directB = 0;
            }
            else
            {
                VoxelState ds = directNeighbor.Value;
                bool directOpaque = BlockTypes[ds.ID].IsOpaque;
                if (directOpaque)
                {
                    directSun = 0;
                    directR = 0;
                    directG = 0;
                    directB = 0;
                }
                else
                {
                    // Read RGB from the ushort light array via the face neighbor position
                    Vector3Int neighborPos = blockPos + BurstVoxelData.FaceChecks.Data[faceIndex];
                    ushort lightData = GetLightDataFromLocalPos(neighborPos);
                    directSun = LightBitMapping.GetSkyLight(lightData);
                    directR = LightBitMapping.GetBlocklightR(lightData);
                    directG = LightBitMapping.GetBlocklightG(lightData);
                    directB = LightBitMapping.GetBlocklightB(lightData);
                }
            }

            l0 = SampleCorner(faceIndex, 0, blockPos, directSun, directR, directG, directB);
            l1 = SampleCorner(faceIndex, 1, blockPos, directSun, directR, directG, directB);
            l2 = SampleCorner(faceIndex, 2, blockPos, directSun, directR, directG, directB);
            l3 = SampleCorner(faceIndex, 3, blockPos, directSun, directR, directG, directB);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Color32 SampleCorner(int faceIndex, int cornerIndex, Vector3Int blockPos,
            byte directSun, byte directR, byte directG, byte directB)
        {
            int lutBase = faceIndex * 12 + cornerIndex * 3;
            int3 sideAOffset = BurstVoxelData.CornerOffsets.Data[lutBase + 0];
            int3 sideBOffset = BurstVoxelData.CornerOffsets.Data[lutBase + 1];
            int3 diagOffset = BurstVoxelData.CornerOffsets.Data[lutBase + 2];

            SampleNeighborLight(blockPos + new Vector3Int(sideAOffset.x, sideAOffset.y, sideAOffset.z),
                out byte sideASun, out byte sideAR, out byte sideAG, out byte sideAB, out bool sideAOpaque);

            SampleNeighborLight(blockPos + new Vector3Int(sideBOffset.x, sideBOffset.y, sideBOffset.z),
                out byte sideBSun, out byte sideBR, out byte sideBG, out byte sideBB, out bool sideBOpaque);

            byte diagSun = 0, diagR = 0, diagG = 0, diagB = 0;
            if (!(sideAOpaque && sideBOpaque))
            {
                SampleNeighborLight(blockPos + new Vector3Int(diagOffset.x, diagOffset.y, diagOffset.z),
                    out diagSun, out diagR, out diagG, out diagB, out _);
            }

            // Average all 4 channels independently (always divide by 4).
            // Encode to UNorm8: value * 17 maps 0-15 → 0-255 (with rounding: (sum * 17 + 2) / 4).
            int sunSum = directSun + sideASun + sideBSun + diagSun;
            int rSum = directR + sideAR + sideBR + diagR;
            int gSum = directG + sideAG + sideBG + diagG;
            int bSum = directB + sideAB + sideBB + diagB;

            return new Color32(
                (byte)((sunSum * 17 + 2) / 4),
                (byte)((rSum * 17 + 2) / 4),
                (byte)((gSum * 17 + 2) / 4),
                (byte)((bSum * 17 + 2) / 4)
            );
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SampleNeighborLight(Vector3Int pos, out byte sun, out byte blockR, out byte blockG, out byte blockB, out bool isOpaque)
        {
            VoxelState? state = GetVoxelStateFromLocalPos(pos);
            if (!state.HasValue)
            {
                sun = 15;
                blockR = 0;
                blockG = 0;
                blockB = 0;
                isOpaque = false;
                return;
            }

            VoxelState s = state.Value;
            isOpaque = BlockTypes[s.ID].IsOpaque;
            if (isOpaque)
            {
                sun = 0;
                blockR = 0;
                blockG = 0;
                blockB = 0;
            }
            else
            {
                ushort lightData = GetLightDataFromLocalPos(pos);
                sun = LightBitMapping.GetSkyLight(lightData);
                blockR = LightBitMapping.GetBlocklightR(lightData);
                blockG = LightBitMapping.GetBlocklightG(lightData);
                blockB = LightBitMapping.GetBlocklightB(lightData);
            }
        }

        /// <summary>
        /// Retrieves the packed ushort light data for any position relative to the current chunk.
        /// Mirrors the coordinate routing of <see cref="GetVoxelStateFromLocalPos"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ushort GetLightDataFromLocalPos(Vector3Int pos)
        {
            if (pos.y < 0 || pos.y >= _clipMaxY) return 0;

            if (pos.x >= 0 && pos.x < VoxelData.ChunkWidth &&
                pos.z >= 0 && pos.z < VoxelData.ChunkWidth)
            {
                int idx = ChunkMath.GetFlattenedIndexInChunk(pos.x, pos.y, pos.z);
                return LightMap.IsCreated ? LightMap[idx] : (ushort)0;
            }

            NativeArray<ushort> targetLight = default;
            Vector3Int localPos = pos;

            if (pos.x < 0)
            {
                localPos.x += VoxelData.ChunkWidth;
                if (pos.z < 0)
                {
                    localPos.z += VoxelData.ChunkWidth;
                    targetLight = LightBackLeft;
                }
                else if (pos.z >= VoxelData.ChunkWidth)
                {
                    localPos.z -= VoxelData.ChunkWidth;
                    targetLight = LightFrontLeft;
                }
                else
                {
                    targetLight = LightLeft;
                }
            }
            else if (pos.x >= VoxelData.ChunkWidth)
            {
                localPos.x -= VoxelData.ChunkWidth;
                if (pos.z < 0)
                {
                    localPos.z += VoxelData.ChunkWidth;
                    targetLight = LightBackRight;
                }
                else if (pos.z >= VoxelData.ChunkWidth)
                {
                    localPos.z -= VoxelData.ChunkWidth;
                    targetLight = LightFrontRight;
                }
                else
                {
                    targetLight = LightRight;
                }
            }
            else
            {
                if (pos.z < 0)
                {
                    localPos.z += VoxelData.ChunkWidth;
                    targetLight = LightBack;
                }
                else if (pos.z >= VoxelData.ChunkWidth)
                {
                    localPos.z -= VoxelData.ChunkWidth;
                    targetLight = LightFront;
                }
            }

            if (!targetLight.IsCreated || targetLight.Length == 0) return 0;

            if (localPos.x < 0 || localPos.x >= VoxelData.ChunkWidth ||
                localPos.z < 0 || localPos.z >= VoxelData.ChunkWidth)
                return 0;

            int mapIdx = ChunkMath.GetFlattenedIndexInChunk(localPos.x, localPos.y, localPos.z);
            return targetLight[mapIdx];
        }

        /// <summary>
        /// Retrieves the voxel state for any position relative to the current chunk's origin.
        /// Automatically maps coordinates to the correct neighbor array if out of bounds.
        /// </summary>
        /// <param name="pos">The local position to check (e.g., (-1, 10, 16)).</param>
        /// <returns>A VoxelState if the position is in a loaded neighbor chunk, otherwise null.</returns>
        private VoxelState? GetVoxelStateFromLocalPos(Vector3Int pos)
        {
            if (pos.y < 0 || pos.y >= _clipMaxY ||
                pos.x >= _clipLocalMaxX || pos.z >= _clipLocalMaxZ) return null;

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

            // Defensive validation: ensure remapped coordinates are within chunk bounds.
            if (localPos.x < 0 || localPos.x >= VoxelData.ChunkWidth ||
                localPos.z < 0 || localPos.z >= VoxelData.ChunkWidth)
                return null;

            int mapIndex = ChunkMath.GetFlattenedIndexInChunk(localPos.x, localPos.y, localPos.z);
            return new VoxelState(targetMap[mapIndex]);
        }

        #endregion


        #region Texture Methods

        private int GetTextureID(ushort blockId, int faceIndex)
        {
            BlockTypeJobData props = BlockTypes[blockId];
            return faceIndex switch
            {
                0 => props.BackFaceTexture,
                1 => props.FrontFaceTexture,
                2 => props.TopFaceTexture,
                3 => props.BottomFaceTexture,
                4 => props.LeftFaceTexture,
                5 => props.RightFaceTexture,
                _ => 0,
            };
        }

        #endregion
    }
}
