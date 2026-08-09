using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
        public NativeArray<uint> NeighborS; // South (-Z)

        [ReadOnly]
        public NativeArray<uint> NeighborN; // North (+Z)

        [ReadOnly]
        public NativeArray<uint> NeighborW; // West  (-X)

        [ReadOnly]
        public NativeArray<uint> NeighborE; // East  (+X)

        // 4 Diagonal Neighbors (Used for fluid corner smoothing)
        [ReadOnly]
        public NativeArray<uint> NeighborNE; // North-East

        [ReadOnly]
        public NativeArray<uint> NeighborSE; // South-East

        [ReadOnly]
        public NativeArray<uint> NeighborSW; // South-West

        [ReadOnly]
        public NativeArray<uint> NeighborNW; // North-West

        // --- LIGHT MAPS (Phase 2 RGB) ---
        [ReadOnly]
        public NativeArray<ushort> LightMap;

        [ReadOnly]
        public NativeArray<ushort> LightS;

        [ReadOnly]
        public NativeArray<ushort> LightN;

        [ReadOnly]
        public NativeArray<ushort> LightW;

        [ReadOnly]
        public NativeArray<ushort> LightE;

        [ReadOnly]
        public NativeArray<ushort> LightNE;

        [ReadOnly]
        public NativeArray<ushort> LightSE;

        [ReadOnly]
        public NativeArray<ushort> LightSW;

        [ReadOnly]
        public NativeArray<ushort> LightNW;

        // --- FLUID TEMPLATES ---
        [ReadOnly]
        public NativeArray<float> WaterVertexTemplates;

        [ReadOnly]
        public NativeArray<float> LavaVertexTemplates;

        // --- SETTINGS ---
        public SmoothLightingQuality SmoothLighting;

        /// <summary>
        /// SS-3: when set, faces reached by ordinary <b>full cubes</b> are subdivided too, so a wall's
        /// shadow resolves as a band hugging it instead of a ramp across the whole adjoining cell.
        /// Partial occluders (slabs, posts) are subdivided regardless — that is SS-2 and not gated here.
        /// <para>
        /// Off by default: it is the one shading change in this arc that moves the world's vertex count
        /// (1.4×–1.7× measured), so it stays behind a setting until a capture says what it costs on the
        /// target build.
        /// </para>
        /// </summary>
        [MarshalAs(UnmanagedType.U1)]
        public bool FullCubeContactShadows;

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

            // MR-7: the fluid mesher's neighbor scratch is hoisted to a single allocation per Execute
            // and reused across every fluid voxel (it was previously allocated and disposed per fluid
            // voxel — thousands of times in an ocean chunk). GenerateVoxelMeshData overwrites every slot
            // unconditionally each voxel, so reuse carries no stale state. Sized by the offsets array so
            // the buffer length always matches the fill-loop bound (no hardcoded count to drift).
            int fluidNeighborCount = s_fluidNeighborOffsets.Length;
            NativeArray<OptionalVoxelState> fluidNeighbors = new NativeArray<OptionalVoxelState>(fluidNeighborCount, Allocator.Temp);
            NativeArray<ushort> fluidNeighborLights = new NativeArray<ushort>(fluidNeighborCount, Allocator.Temp);

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
                    IterateSolidSection(startY, endY, ref fluidNeighbors, ref fluidNeighborLights);
                }
                else
                {
                    IterateStandardSection(startY, endY, ref fluidNeighbors, ref fluidNeighborLights);
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

            fluidNeighbors.Dispose();
            fluidNeighborLights.Dispose();
        }

        /// <summary>
        /// Iterates only the boundaries of a solid section (Top, Bottom, Walls).
        /// </summary>
        private void IterateSolidSection(int startY, int endY,
            ref NativeArray<OptionalVoxelState> fluidNeighbors, ref NativeArray<ushort> fluidNeighborLights)
        {
            const int width = VoxelData.ChunkWidth;
            const int max = width - 1;

            // 1. Top and Bottom Layers (Iterate fully to check Up/Down culling)
            // Loop order: Z -> X for cache locality on horizontal planes
            for (int z = 0; z < width; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    ProcessVoxel(x, startY, z, ref fluidNeighbors, ref fluidNeighborLights); // Bottom layer
                    ProcessVoxel(x, endY - 1, z, ref fluidNeighbors, ref fluidNeighborLights); // Top layer
                }
            }

            // 2. Middle Layers (Iterate only the X/Z walls)
            // Loop order: Z -> Y -> X roughly maintains locality
            for (int z = 0; z < width; z++)
            {
                for (int y = startY + 1; y < endY - 1; y++)
                {
                    // Check X-boundaries
                    ProcessVoxel(0, y, z, ref fluidNeighbors, ref fluidNeighborLights);
                    ProcessVoxel(max, y, z, ref fluidNeighbors, ref fluidNeighborLights);

                    // Check Z-boundaries (only if not already covered by X-boundaries)
                    if (z is 0 or max)
                    {
                        // We need to fill the row between 1 and max-1
                        for (int x = 1; x < max; x++)
                        {
                            ProcessVoxel(x, y, z, ref fluidNeighbors, ref fluidNeighborLights);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Standard iteration over every voxel in the section.
        /// </summary>
        private void IterateStandardSection(int startY, int endY,
            ref NativeArray<OptionalVoxelState> fluidNeighbors, ref NativeArray<ushort> fluidNeighborLights)
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
                        ProcessVoxel(x, y, z, ref fluidNeighbors, ref fluidNeighborLights);
                    }
                }
            }
        }

        private void ProcessVoxel(int x, int y, int z,
            ref NativeArray<OptionalVoxelState> fluidNeighbors, ref NativeArray<ushort> fluidNeighborLights)
        {
            int mapIndex = ChunkMath.GetFlattenedIndexInChunk(x, y, z);
            uint packedData = Map[mapIndex];
            ushort id = BurstVoxelDataBitMapping.GetId(packedData);

            if (id == BlockIDs.Air) return; // Skip Air

            BlockTypeJobData props = BlockTypes[id];

            // Dispatch to specific mesh generation logic based on block type (Fluid, Custom, or Standard)
            GenerateVoxelMeshData(new Vector3Int(x, y, z), packedData, props, ref fluidNeighbors, ref fluidNeighborLights);
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
        private void GenerateVoxelMeshData(Vector3Int pos, uint packedData, BlockTypeJobData voxelProps,
            ref NativeArray<OptionalVoxelState> neighbors, ref NativeArray<ushort> neighborLights)
        {
            ushort id = BurstVoxelDataBitMapping.GetId(packedData);

            // --- CASE 1: FLUID ---
            if (voxelProps.FluidType != FluidType.None)
            {
                // Select template
                NativeArray<float> templates = voxelProps.FluidType == FluidType.WaterLike ? WaterVertexTemplates : LavaVertexTemplates;

                // Collect neighbors for smoothing & culling into the per-Execute scratch buffers
                // (hoisted by MR-7). Write EVERY slot unconditionally so a reused buffer never carries
                // a previous voxel's neighbor: `default` is HasValue=false, matching a missing neighbor.
                for (int i = 0; i < s_fluidNeighborOffsets.Length; i++)
                {
                    Vector3Int neighborPos = pos + s_fluidNeighborOffsets[i];
                    VoxelState? neighborState = GetVoxelStateFromLocalPos(neighborPos);
                    neighbors[i] = neighborState.HasValue ? new OptionalVoxelState(neighborState.Value) : default;
                    neighborLights[i] = GetLightDataFromLocalPos(neighborPos);
                }

                FluidCornerLights cornerLights = default;
                if (SmoothLighting >= SmoothLightingQuality.Standard)
                {
                    for (int face = 0; face < 6; face++)
                    {
                        CalculateCornerLights(face, pos, faceIsInteriorToSampleCell: false,
                            out Color32 l0, out Color32 l1, out Color32 l2, out Color32 l3);
                        cornerLights.SetFace(face, l0, l1, l2, l3);
                    }
                }

                VoxelMeshHelper.GenerateFluidMeshData(in pos, packedData, in voxelProps, in templates, in BlockTypes, in neighbors,
                    in neighborLights, SmoothLighting >= SmoothLightingQuality.Standard, in cornerLights,
                    ref _vertexIndex, ref Output.Vertices, ref Output.FluidTriangles, ref Output.Uvs, ref Output.Colors, ref Output.Normals,
                    ref Output.LightData);

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
                    CalculateCornerLights(2, pos, faceIsInteriorToSampleCell: false,
                        out crossLights.TopL0, out crossLights.TopL1, out crossLights.TopL2, out crossLights.TopL3);

                    if (SmoothLighting >= SmoothLightingQuality.High)
                    {
                        // Bottom-level corners: sample Top face of the block below (light at ground level).
                        Vector3Int belowPos = pos + BurstVoxelData.FaceChecks.Data[3];
                        CalculateCornerLights(2, belowPos, faceIsInteriorToSampleCell: false,
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

                // FL-1: per-voxel wind phase, hashed in voxel space (ChunkPosition is the chunk's
                // voxel-space origin) so it survives floating-origin re-anchors and re-meshes.
                float swayPhase = VoxelMeshHelper.VoxelHash01(
                    (int)ChunkPosition.x + pos.x, pos.y, (int)ChunkPosition.z + pos.z);

                VoxelMeshHelper.GenerateCrossMesh(textureID, in crossLights,
                    pos, swayPhase, ref _vertexIndex, ref Output.Vertices, ref Output.TransparentTriangles, ref Output.Uvs, ref Output.Colors, ref Output.Normals,
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
            int swayVertStart = _vertexIndex;
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

            // FL-2: leaf-block shimmer — a sway-flagged cube gets a uniform weight on every emitted
            // vert (cubes are not rooted, unlike FL-1's cross meshes) plus the same voxel-space phase.
            // A post-pass over the voxel's vertex range covers all six schema arms in one place.
            if (voxelProps.SwayStrength > 0f && _vertexIndex > swayVertStart)
                ApplySwayChannels(swayVertStart, voxelProps.SwayStrength, pos);
        }

        /// <summary>
        /// Rewrites the UV ZW sway channels (FL-2) of every vertex emitted for the current voxel:
        /// Z = the block's authored sway strength, W = the voxel's deterministic wind phase
        /// (<see cref="VoxelMeshHelper.VoxelHash01"/> over the voxel-space cell, re-anchor-safe).
        /// </summary>
        /// <param name="fromVert">First vertex index of the voxel's emitted range.</param>
        /// <param name="swayStrength">The block's authored sway strength in [0, 1].</param>
        /// <param name="pos">The voxel's chunk-local position.</param>
        private void ApplySwayChannels(int fromVert, float swayStrength, Vector3Int pos)
        {
            float swayPhase = VoxelMeshHelper.VoxelHash01(
                (int)ChunkPosition.x + pos.x, pos.y, (int)ChunkPosition.z + pos.z);
            half z = (half)swayStrength;
            half w = (half)swayPhase;

            for (int i = fromVert; i < _vertexIndex; i++)
            {
                half4 uv = Output.Uvs[i];
                Output.Uvs[i] = new half4(uv.x, uv.y, z, w);
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

                // VO-6: this path's cull check and face index are both unrotated (only the vertices take
                // the Y rotation), so the sample cell is resolved in that same unrotated frame — identity
                // matrix, unrotated face normal. A Y rotation leaves a face's own-cell-vs-boundary
                // character unchanged for the shapes this path serves.
                float3x3 identity = float3x3.identity;
                Vector3Int faceNormal = BurstVoxelData.FaceChecks.Data[p];
                Vector3Int sampleCell = ResolveFaceSampleCell(pos, in identity,
                    CustomFaces[meshData.FaceStartIndex + p].Centroid, faceNormal);
                bool faceIsInterior = sampleCell == pos;
                VoxelState? sampleVoxel = GetVoxelStateFromLocalPos(sampleCell);

                // An interior face keeps the cell's own open half in front of it, and only this block can
                // occupy that space — so nothing outside the cell can occlude it (Bug M02).
                if (faceIsInterior || ShouldDrawFace(voxelProps, sampleVoxel))
                {
                    int translatedP = VoxelHelper.GetTranslatedFaceIndex(p, orientation);
                    int textureID = GetTextureID(id, translatedP);

                    if (SmoothLighting >= SmoothLightingQuality.Standard)
                    {
                        CalculateCornerLights(p, sampleCell - faceNormal, faceIsInterior,
                            out Color32 l0, out Color32 l1, out Color32 l2, out Color32 l3);
                        VoxelMeshHelper.GenerateCustomMeshFace(translatedP, textureID, pos, rotation,
                            p, l0, l1, l2, l3,
                            voxelProps.CustomMeshIndex, in CustomMeshes, in CustomFaces, in CustomVerts, in CustomTris,
                            ref _vertexIndex, ref Output.Vertices, ref Output.Triangles, ref Output.TransparentTriangles, ref Output.Uvs,
                            ref Output.Colors, ref Output.Normals, ref Output.LightData, voxelProps.RenderNeighborFaces);
                    }
                    else
                    {
                        Color32 flatLight = BuildFlatLightData(sampleVoxel, sampleCell);
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
        /// Face culling rotates the check direction through the same rotation matrix as the vertices, then
        /// asks <see cref="ResolveFaceSampleCell"/> which cell the face looks into, so a face sitting
        /// inside its own cell is not culled by a neighbor a whole cell away.
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

                // Visibility and light both ask about the cell this face actually looks into.
                Vector3Int sampleCell = ResolveFaceSampleCell(pos, in matrix,
                    CustomFaces[meshData.FaceStartIndex + p].Centroid, rotatedOffset);
                bool faceIsInterior = sampleCell == pos;
                VoxelState? sampleVoxel = GetVoxelStateFromLocalPos(sampleCell);

                // An interior face keeps the cell's own open half in front of it, and only this block can
                // occupy that space — so nothing outside the cell can occlude it (Bug M02).
                if (faceIsInterior || ShouldDrawFace(voxelProps, sampleVoxel))
                {
                    int textureID = GetTextureID(id, p);

                    if (SmoothLighting >= SmoothLightingQuality.Standard)
                    {
                        int worldFace = DirectionToFaceIndex(rotatedOffset);

                        // The ring's LUT offsets are relative to a block whose face-normal neighbor is the
                        // sampled cell, so re-basing by one face step moves the ring and the direct term
                        // together. For a boundary face this is exactly `pos`, as before.
                        CalculateCornerLights(worldFace, sampleCell - rotatedOffset, faceIsInterior,
                            out Color32 l0, out Color32 l1, out Color32 l2, out Color32 l3);
                        VoxelMeshHelper.GenerateCustomMeshFace(p, textureID, pos, in matrix,
                            worldFace, l0, l1, l2, l3,
                            voxelProps.CustomMeshIndex, in CustomMeshes, in CustomFaces, in CustomVerts, in CustomTris,
                            ref _vertexIndex, ref Output.Vertices, ref Output.Triangles, ref Output.TransparentTriangles,
                            ref Output.Uvs, ref Output.Colors, ref Output.Normals, ref Output.LightData, voxelProps.RenderNeighborFaces);
                    }
                    else
                    {
                        Color32 flatLight = BuildFlatLightData(sampleVoxel, sampleCell);
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
                        // Sampling by world position makes the corner-light permutation unnecessary on the
                        // tessellated path: each sub-vertex asks about the cell it actually sits under.
                        if (ShadeOrEmitStandardCubeFace(p, translatedP, pos, rotation, 0, textureID,
                                voxelProps.RenderNeighborFaces,
                                out Color32 l0, out Color32 l1, out Color32 l2, out Color32 l3))
                            continue;

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
        /// Resolves one standard-cube face's shading: samples its neighborhood once, then either emits the
        /// face here as a tessellated grid or hands the four corner values back for the caller to emit.
        /// <para>
        /// The single sampling pass is the point. The 3×3 gather is the mesher's heaviest read — nine
        /// voxel states and their light — and deciding tessellation separately from emitting ran it twice
        /// for every face an occluder reached.
        /// </para>
        /// </summary>
        /// <param name="worldFace">Cardinal face index used for neighbor sampling.</param>
        /// <param name="geometryFace">Face index whose vertices and UVs are emitted (differs from
        /// <paramref name="worldFace"/> only on the legacy rotated path).</param>
        /// <param name="pos">Block position in chunk-local space.</param>
        /// <param name="rotation">Y-axis rotation in degrees applied to the emitted geometry.</param>
        /// <param name="uvQuarterTurnsCW">Number of 90° clockwise UV rotations to apply (0-3).</param>
        /// <param name="textureID">Atlas texture index for this face.</param>
        /// <param name="renderNeighborFaces">Routes the triangles to the transparent submesh.</param>
        /// <param name="l0">Light at corner 0; only meaningful when this returns <see langword="false"/>.</param>
        /// <param name="l1">Light at corner 1; only meaningful when this returns <see langword="false"/>.</param>
        /// <param name="l2">Light at corner 2; only meaningful when this returns <see langword="false"/>.</param>
        /// <param name="l3">Light at corner 3; only meaningful when this returns <see langword="false"/>.</param>
        /// <returns><see langword="true"/> when the face was emitted here; <see langword="false"/> when the
        /// caller must emit it from the corner values.</returns>
        private bool ShadeOrEmitStandardCubeFace(int worldFace, int geometryFace, Vector3Int pos,
            float rotation, int uvQuarterTurnsCW, int textureID, bool renderNeighborFaces,
            out Color32 l0, out Color32 l1, out Color32 l2, out Color32 l3)
        {
            // Deliberately not inlined into the per-face callers: they run inside the six-face loop, and a
            // stackalloc there would grow the frame once per iteration instead of once per face.
            Span<FaceOccluder> occluders = stackalloc FaceOccluder[FACE_OCCLUDER_COUNT];

            // A standard cube's faces always sit on its cell boundary — only a custom mesh can put one
            // inside its own cell (VO-6).
            PrepareFaceSampling(worldFace, pos, faceIsInteriorToSampleCell: false, occluders,
                out Vector3Int directCell, out _, out int axisA, out int axisB, out int tessellation);

            if (tessellation > 1)
            {
                EmitTessellatedStandardCubeFace(geometryFace, pos, rotation, uvQuarterTurnsCW,
                    textureID, renderNeighborFaces, tessellation, directCell, axisA, axisB, occluders);
                l0 = l1 = l2 = l3 = default;
                return true;
            }

            l0 = ShadeCorner(worldFace, 0, pos, directCell, axisA, axisB, occluders);
            l1 = ShadeCorner(worldFace, 1, pos, directCell, axisA, axisB, occluders);
            l2 = ShadeCorner(worldFace, 2, pos, directCell, axisA, axisB, occluders);
            l3 = ShadeCorner(worldFace, 3, pos, directCell, axisA, axisB, occluders);
            return false;
        }

        /// <summary>
        /// VO-9b: emits one standard cube face as an N×N grid of sub-quads, shading every sub-vertex
        /// through <see cref="ShadePoint"/> instead of blending four cell-corner values.
        /// <para>
        /// This is what gives a partial occluder a contact shadow of the right <i>width</i>. A slab's
        /// edge lies at the cell midline, where an undivided face has no vertex, so its shadow was
        /// smeared across the whole cell at a fraction of its strength — the resolution of the shading
        /// signal was pinned to the resolution of the mesh.
        /// </para>
        /// <para>
        /// Only faces that a partial occluder can actually reach take this path, and the shading
        /// function reduces to the undivided model at the face's own corners, so a tessellated face
        /// still meets an ordinary neighboring face without a seam.
        /// </para>
        /// </summary>
        /// <param name="geometryFace">Face index whose vertices and UVs are emitted (differs from the
        /// sampled world face only on the legacy rotated path).</param>
        /// <param name="pos">Block position in chunk-local space.</param>
        /// <param name="rotation">Y-axis rotation in degrees applied to the emitted geometry.</param>
        /// <param name="uvQuarterTurnsCW">Number of 90° clockwise UV rotations to apply (0-3).</param>
        /// <param name="textureID">Atlas texture index for this face.</param>
        /// <param name="renderNeighborFaces">Routes the triangles to the transparent submesh.</param>
        /// <param name="tessellation">Sub-quads per axis, from the face's own gate.</param>
        /// <param name="directCell">The cell in front of the face, from <see cref="PrepareFaceSampling"/>.</param>
        /// <param name="axisA">The face's first tangent axis.</param>
        /// <param name="axisB">The face's second tangent axis.</param>
        /// <param name="occluders">The hoisted 3x3, already sampled for this face.</param>
        private void EmitTessellatedStandardCubeFace(int geometryFace, Vector3Int pos,
            float rotation, int uvQuarterTurnsCW, int textureID, bool renderNeighborFaces,
            int tessellation, Vector3Int directCell, int axisA, int axisB,
            ReadOnlySpan<FaceOccluder> occluders)
        {
            VoxelMeshHelper.GetStandardCubeFaceQuad(geometryFace, in pos, rotation, uvQuarterTurnsCW,
                out VoxelMeshHelper.FaceQuad face);
            Vector3 normal = BurstVoxelData.FaceChecks.Data[geometryFace];

            float step = 1f / tessellation;

            for (int j = 0; j < tessellation; j++)
            {
                for (int i = 0; i < tessellation; i++)
                {
                    float u0 = i * step, v0 = j * step, u1 = (i + 1) * step, v1 = (j + 1) * step;
                    VoxelMeshHelper.GetSubQuad(in face, u0, v0, u1, v1, out VoxelMeshHelper.FaceQuad sub);

                    // Every sub-vertex asks the same question an ordinary face asks at its corners, at
                    // its own position — so a subdivided face and an undivided neighbor agree wherever
                    // they meet, without the tessellation needing to know about the seam.
                    Color32 s0 = ShadePoint(sub.P0, directCell, axisA, axisB, occluders);
                    Color32 s1 = ShadePoint(sub.P1, directCell, axisA, axisB, occluders);
                    Color32 s2 = ShadePoint(sub.P2, directCell, axisA, axisB, occluders);
                    Color32 s3 = ShadePoint(sub.P3, directCell, axisA, axisB, occluders);

                    VoxelMeshHelper.EmitFaceQuad(in sub, textureID, in normal, s0, s1, s2, s3,
                        ref _vertexIndex, ref Output.Vertices, ref Output.Triangles,
                        ref Output.TransparentTriangles, ref Output.Uvs, ref Output.Colors,
                        ref Output.Normals, ref Output.LightData, renderNeighborFaces);
                }
            }
        }

        /// <summary>
        /// How many sub-quads per axis a face is split into when a partial occluder can reach it. The
        /// sample points land on multiples of <c>1 / N</c>, so a value of 4 puts one exactly on the cell
        /// midline — where an axis-aligned slab's edge sits.
        /// </summary>
        private const int SUB_CELL_TESSELLATION = 4;

        /// <summary>
        /// SS-3: sub-quads per axis for a face reached only by <b>full-cube</b> occluders. Half the
        /// density of <see cref="SUB_CELL_TESSELLATION"/>, because a full cube's silhouette is its whole
        /// cell — there is no sub-cell edge position to resolve, only a falloff — and the vertex cost
        /// goes as the square: measured across terrain and built geometry, 4 costs 3.1×–4.7× the world's
        /// vertices where 2 costs 1.4×–1.7× (design doc §8).
        /// </summary>
        private const int FULL_CUBE_SUB_CELL_TESSELLATION = 2;

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
                if (ShadeOrEmitStandardCubeFace(worldFace, worldFace, pos, rotation: 0f, uvQuarterTurnsCW,
                        textureID, voxelProps.RenderNeighborFaces,
                        out Color32 l0, out Color32 l1, out Color32 l2, out Color32 l3))
                    return;

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
        /// VO-6: resolves which cell a custom-mesh face actually looks into — the cell containing the
        /// space immediately in front of the face's own position, rather than the block-boundary neighbor.
        /// <para>
        /// A boundary face's centroid lies on a cell wall, so stepping off it lands in the neighbor and
        /// this returns exactly <c>pos + rotatedNormal</c> — today's behavior, unchanged. A half slab's
        /// large face lies on the mid-plane, so stepping off it stays inside the block's own cell, which
        /// is the <c>MESHING_BUGS.md</c> Bug M01 fix.
        /// </para>
        /// </summary>
        /// <param name="pos">The voxel's chunk-local position.</param>
        /// <param name="matrix">The block's metadata rotation, applied about the cell center exactly as
        /// <see cref="VoxelMeshHelper.GenerateCustomMeshFace"/> applies it to the vertices.</param>
        /// <param name="centroid">The face's unrotated block-local centroid.</param>
        /// <param name="rotatedNormal">The face's rotated normal, ±1 on exactly one axis.</param>
        /// <returns>The chunk-local cell whose light this face should sample.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector3Int ResolveFaceSampleCell(Vector3Int pos, in float3x3 matrix,
            float3 centroid, Vector3Int rotatedNormal)
        {
            float3 center = BurstVoxelData.BlockCenter;
            float3 rotatedCentroid = math.mul(matrix, centroid - center) + center;
            float3 probe = rotatedCentroid
                           + FACE_SAMPLE_STEP * new float3(rotatedNormal.x, rotatedNormal.y, rotatedNormal.z);
            int3 cell = (int3)math.floor(probe);
            return new Vector3Int(pos.x + cell.x, pos.y + cell.y, pos.z + cell.z);
        }

        /// <summary>
        /// How far off a face to step, in cells, when asking which cell it faces. Must be strictly
        /// between 0 and 0.5.
        /// <para>
        /// A half-cell step — the obvious choice, and the one this phase's plan originally specified —
        /// puts a mid-plane face at exactly <c>0.5 ± 0.5</c>, a cell boundary, where <c>floor</c> breaks
        /// the tie toward the own cell for a negative normal and toward the neighbor for a positive one.
        /// That silently fixes half the orientations and leaves the other half untouched. Any step short
        /// of the boundary resolves both, and for a face that is not on the mid-plane every step in the
        /// range agrees anyway. Guarded by <c>KM01a</c> (negative normal) and <c>KM01b</c> (positive).
        /// </para>
        /// </summary>
        private const float FACE_SAMPLE_STEP = 0.25f;

        /// <summary>
        /// Computes per-vertex corner-shaded light values for smooth lighting: the four cell corners of
        /// one face, each evaluated against the 3x3 neighborhood in front of it.
        /// </summary>
        /// <param name="faceIndex">Face direction, in <c>VoxelData.FaceChecks</c> order.</param>
        /// <param name="blockPos">The shaded block's chunk-local position.</param>
        /// <param name="faceIsInteriorToSampleCell">True when the face sits inside its own cell (VO-6).</param>
        /// <param name="l0">Light at corner 0.</param>
        /// <param name="l1">Light at corner 1.</param>
        /// <param name="l2">Light at corner 2.</param>
        /// <param name="l3">Light at corner 3.</param>
        private void CalculateCornerLights(int faceIndex, Vector3Int blockPos,
            bool faceIsInteriorToSampleCell,
            out Color32 l0, out Color32 l1, out Color32 l2, out Color32 l3)
        {
            Span<FaceOccluder> occluders = stackalloc FaceOccluder[FACE_OCCLUDER_COUNT];

            // The tessellation gate this computes is discarded: only standard cubes subdivide, and they
            // go through ShadeOrEmitStandardCubeFace. A custom mesh's face keeps its authored geometry.
            PrepareFaceSampling(faceIndex, blockPos, faceIsInteriorToSampleCell, occluders,
                out Vector3Int directCell, out _, out int axisA, out int axisB, out _);

            l0 = ShadeCorner(faceIndex, 0, blockPos, directCell, axisA, axisB, occluders);
            l1 = ShadeCorner(faceIndex, 1, blockPos, directCell, axisA, axisB, occluders);
            l2 = ShadeCorner(faceIndex, 2, blockPos, directCell, axisA, axisB, occluders);
            l3 = ShadeCorner(faceIndex, 3, blockPos, directCell, axisA, axisB, occluders);
        }

        /// <summary>
        /// Evaluates <see cref="ShadePoint"/> at one of a face's four cell corners — the shading points
        /// the mesher used exclusively before VO-9 introduced sub-cell sampling, and still the only ones
        /// an undivided face emits.
        /// </summary>
        /// <param name="faceIndex">Face direction, in <c>VoxelData.FaceChecks</c> order.</param>
        /// <param name="cornerIndex">Which of the face's four corners (0-3).</param>
        /// <param name="blockPos">The shaded block's chunk-local position.</param>
        /// <param name="directCell">The cell in front of the face.</param>
        /// <param name="axisA">The face's first tangent axis.</param>
        /// <param name="axisB">The face's second tangent axis.</param>
        /// <param name="occluders">The hoisted 3x3.</param>
        /// <returns>The corner's encoded light value.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Color32 ShadeCorner(int faceIndex, int cornerIndex, Vector3Int blockPos,
            Vector3Int directCell, int axisA, int axisB, ReadOnlySpan<FaceOccluder> occluders)
        {
            int3 cornerVert = BurstVoxelData.CornerVertices.Data[faceIndex * 4 + cornerIndex];
            float3 samplePoint = new float3(
                blockPos.x + cornerVert.x, blockPos.y + cornerVert.y, blockPos.z + cornerVert.z);

            return ShadePoint(samplePoint, directCell, axisA, axisB, occluders);
        }

        /// <summary>
        /// Resolves everything a face's shading samples share: the cell in front of it, its two tangent
        /// axes, the 3x3 of occluders and light around it, and how finely it must be subdivided.
        /// <para>
        /// The 3x3 is gathered here once and read by every sample on the face, which is what lets a
        /// sub-vertex be shaded without any voxel reads of its own.
        /// </para>
        /// </summary>
        /// <param name="faceIndex">Face direction, in <c>VoxelData.FaceChecks</c> order.</param>
        /// <param name="blockPos">The shaded block's chunk-local position.</param>
        /// <param name="faceIsInteriorToSampleCell">True when the face sits inside its own cell (VO-6).</param>
        /// <param name="occluders">Receives the 3x3 in front of the face, in <see cref="OccluderIndex"/>
        /// order; must have <see cref="FACE_OCCLUDER_COUNT"/> entries.</param>
        /// <param name="directCell">The cell in front of the face (the 3x3's center).</param>
        /// <param name="normalAxis">The face's normal axis (0 = X, 1 = Y, 2 = Z).</param>
        /// <param name="axisA">The face's first tangent axis.</param>
        /// <param name="axisB">The face's second tangent axis.</param>
        /// <param name="tessellation">Sub-quads per axis this face needs: 1 leaves it undivided.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void PrepareFaceSampling(int faceIndex, Vector3Int blockPos,
            bool faceIsInteriorToSampleCell, Span<FaceOccluder> occluders,
            out Vector3Int directCell, out int normalAxis, out int axisA, out int axisB,
            out int tessellation)
        {
            Vector3Int faceNormal = BurstVoxelData.FaceChecks.Data[faceIndex];
            directCell = blockPos + faceNormal;

            normalAxis = faceNormal.x != 0 ? 0 : (faceNormal.y != 0 ? 1 : 2);
            axisA = normalAxis == 0 ? 1 : 0;
            axisB = normalAxis == 2 ? 1 : 2;

            // Bug M03: locate the shaded surface within the sampled cell. A boundary face lies on the
            // wall its normal points away from; a face interior to its own cell (a slab's mid-plane,
            // after VO-6) lies on the cell midline. Getting this wrong asks a block whether it occludes
            // its own surface — which is how a recessed slab once rendered fully black.
            int normalSign = faceNormal.x + faceNormal.y + faceNormal.z;
            bool frontIsPositive = normalSign > 0;
            bool lowHalf = faceIsInteriorToSampleCell ? normalSign < 0 : normalSign > 0;
            float planeCoord = frontIsPositive
                ? (lowHalf ? 0f : 0.5f)
                : (lowHalf ? 0.5f : 1f);

            bool hasPartialOccluder = false;
            bool hasAnyOccluder = false;

            // Hoist the whole 3x3 of cells in front of the face ONCE. Every shading point on this face
            // draws from these nine, so this replaces the sixteen overlapping per-corner fetches the
            // pre-SS-2 path made — and it is what lets a sub-vertex evaluate the full neighborhood
            // without any per-point voxel reads at all.
            for (int da = -1; da <= 1; da++)
            {
                for (int db = -1; db <= 1; db++)
                {
                    Vector3Int cell = directCell + AxisStep(axisA, da) + AxisStep(axisB, db);
                    FaceOccluder entry = default;

                    VoxelState? state = GetVoxelStateFromLocalPos(cell);
                    if (!state.HasValue)
                    {
                        // Outside the built neighborhood: treat as open sky, exactly as before.
                        entry.Sun = 15;
                        entry.HoldsLight = 1;
                    }
                    else
                    {
                        VoxelState s = state.Value;
                        BlockTypeJobData props = BlockTypes[s.ID];
                        hasPartialOccluder |= props.HasCustomBounds && props.IsOpaque;

                        if (LightAttenuation.AmbientOcclusionPlaneSilhouette(in props, s.Meta, normalAxis,
                                planeCoord, frontIsPositive, out float2 rectMin, out float2 rectMax))
                        {
                            // Shift the silhouette into the face's own parameter frame, so distances to
                            // every occluder are measured in one coordinate system.
                            entry.RectMin = rectMin + new float2(da, db);
                            entry.RectMax = rectMax + new float2(da, db);
                            entry.Casts = 1;
                            hasAnyOccluder = true;
                        }

                        // A fully-opaque cell holds only surface light and is fully shadowing wherever it
                        // matters, so skip the read — the common case, and unchanged from before SS-2.
                        if (!props.IsFullyOpaqueCell)
                        {
                            entry.HoldsLight = 1;
                            ushort lightData = GetLightDataFromLocalPos(cell);
                            entry.Sun = LightBitMapping.GetSkyLight(lightData);
                            entry.R = LightBitMapping.GetBlocklightR(lightData);
                            entry.G = LightBitMapping.GetBlocklightG(lightData);
                            entry.B = LightBitMapping.GetBlocklightB(lightData);
                        }
                    }

                    occluders[OccluderIndex(da, db)] = entry;
                }
            }

            // SS-3: a partial occluder needs the finer grid — its edge can sit anywhere inside a cell,
            // which is the resolution problem VO-9b was built for. A full cube's silhouette is the cell
            // itself, so its shadow only has to resolve a falloff, and half the density carries it at a
            // quarter of the vertex cost. Faces no occluder reaches stay a single quad, which is what
            // keeps flat ground free.
            //
            // The finer grid is worth paying for only where there is a shadow to resolve, so it requires
            // that something actually cast: a partial block merely SITTING in the neighborhood is not a
            // shadow. A top slab beside a floor is the case — its volume never reaches the floor plane.
            tessellation = hasPartialOccluder && hasAnyOccluder
                ? SUB_CELL_TESSELLATION
                : (FullCubeContactShadows && hasAnyOccluder ? FULL_CUBE_SUB_CELL_TESSELLATION : 1);
        }

        /// <summary>
        /// SS-2: one cell of the 3×3 in front of a shaded face — its silhouette on that face's plane
        /// (already shifted into the face's parameter frame) and its raw light.
        /// </summary>
        private struct FaceOccluder
        {
            /// <summary>Silhouette minimum corner, in the shaded face's parameter frame.</summary>
            public float2 RectMin;

            /// <summary>Silhouette maximum corner.</summary>
            public float2 RectMax;

            /// <summary>1 when this cell's volume reaches the shaded plane and casts a shadow.</summary>
            public byte Casts;

            /// <summary>
            /// 1 when this cell's stored light is meaningful ambient light. A fully-opaque cell holds
            /// only a surface stamp, so it contributes nothing to the light mean — its effect on the
            /// surface is carried entirely by the occlusion field.
            /// </summary>
            public byte HoldsLight;

            /// <summary>The cell's raw sky light.</summary>
            public byte Sun;

            /// <summary>The cell's raw red block light.</summary>
            public byte R;

            /// <summary>The cell's raw green block light.</summary>
            public byte G;

            /// <summary>The cell's raw blue block light.</summary>
            public byte B;
        }

        /// <summary>Number of cells hoisted per face — the 3×3 layer in front of it.</summary>
        private const int FACE_OCCLUDER_COUNT = 9;

        /// <summary>Maps a tangent-cell offset pair in <c>[-1, 1]²</c> to its slot in the hoisted 3×3.</summary>
        /// <param name="da">Offset on the face's first tangent axis.</param>
        /// <param name="db">Offset on the second tangent axis.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int OccluderIndex(int da, int db) => (da + 1) * 3 + (db + 1);

        /// <summary>
        /// SS-2: computes one smooth-lighting value at an arbitrary point on a face — the single shading
        /// function, used at a face's own corners and at every sub-vertex of a subdivided one.
        /// <para>
        /// Two fields are evaluated and multiplied. <b>Occlusion</b> is a distance field: each of the
        /// nine hoisted cells that casts a silhouette darkens the point by its share of the hemisphere,
        /// attenuated by how far the point lies from that silhouette. <b>Light</b> is a weighted mean of
        /// the surrounding cells' values, taken over the space those silhouettes leave open.
        /// </para>
        /// <para>
        /// <b>At a cell corner this reduces exactly to the pre-SS-2 model.</b> Every weight is then a
        /// quarter and every occluder is either in contact or a full cell away, so the result collapses
        /// term-for-term to a quarter of the sum of the open cells' light — the expression the engine has
        /// always evaluated, reproducing <c>255 / 191 / 128 / 64</c> for zero to three occluding
        /// neighbors. That identity is what lets this replace a coverage model without moving ordinary
        /// terrain, and baseline <b>B56</b> pins it.
        /// </para>
        /// <para>
        /// <b>Why occlusion is kept out of the interpolation weights.</b> The weights exist to blend
        /// <i>light</i>, and they collapse onto a single cell at the middle of a face — correct for
        /// light and fatal for occlusion, because evaluating a form where the two are multiplied
        /// together destroys every ring occluder's contribution toward the face center. That is
        /// precisely the defect VO-9b shipped and had to correct (an inner corner's center went 144 to
        /// 255). Separating them is what makes per-sub-vertex evaluation safe here.
        /// </para>
        /// </summary>
        /// <param name="samplePoint">The shaded point, in chunk-local space, on the face's plane.</param>
        /// <param name="directCell">The cell in front of the face (the 3x3's center).</param>
        /// <param name="axisA">The face's first tangent axis (0 = X, 1 = Y, 2 = Z).</param>
        /// <param name="axisB">The face's second tangent axis.</param>
        /// <param name="occluders">The hoisted 3x3, from <see cref="PrepareFaceSampling"/>.</param>
        /// <returns>The encoded light value at that point.</returns>
        private Color32 ShadePoint(float3 samplePoint, Vector3Int directCell, int axisA, int axisB,
            ReadOnlySpan<FaceOccluder> occluders)
        {
            float2 point = new float2(
                samplePoint[axisA] - Component(directCell, axisA),
                samplePoint[axisB] - Component(directCell, axisB));

            // Two readings of the same nine silhouettes. `shadow` is per CELL — how strongly each cell
            // shadows this point — and drives the corner seal and the light weighting, both of which are
            // questions about cells. `quadrant` is per DIRECTION, and is what the occlusion term sums:
            // occlusion is a share of the hemisphere, so it must be attributed to the quarter of the sky
            // an occluder blocks, not to the cell that happens to contain it (SS-3a).
            Span<float> shadow = stackalloc float[FACE_OCCLUDER_COUNT];
            Span<float> quadrant = stackalloc float[QUADRANT_COUNT];

            for (int i = 0; i < QUADRANT_COUNT; i++) quadrant[i] = 0f;

            for (int i = 0; i < FACE_OCCLUDER_COUNT; i++)
            {
                FaceOccluder o = occluders[i];
                if (o.Casts == 0)
                {
                    shadow[i] = 0f;
                    continue;
                }

                shadow[i] = LightAttenuation.ContactShadowFalloff(DistanceToRect(point, in o));

                // The same silhouette can darken several quadrants — a wall running past the point
                // covers two of them — and that is exactly what makes its shadow independent of where
                // the cell seams fall.
                for (int qa = -1; qa <= 1; qa += 2)
                for (int qb = -1; qb <= 1; qb += 2)
                {
                    if (!ClipToQuadrant(point, in o, qa, qb, out float2 clipMin, out float2 clipMax))
                        continue;

                    int q = QuadrantIndex(qa, qb);
                    float lit = LightAttenuation.ContactShadowFalloff(DistanceToRect(point, clipMin, clipMax));
                    quadrant[q] = math.max(quadrant[q], lit);
                }
            }

            ApplyCornerSeal(shadow, quadrant);

            float occlusion = 0f;
            for (int i = 0; i < QUADRANT_COUNT; i++)
            {
                occlusion += LightAttenuation.QuadrantOcclusionShare * quadrant[i];
            }

            occlusion = math.saturate(occlusion);

            // Light arrives from the part of the neighborhood this point can actually SEE, so the mean
            // is taken over the interpolation kernel weighted by BOTH what holds light and what is not
            // shadowed. Taking it over the same weights the occlusion term uses is what makes the two
            // factors cancel rather than compound: since the kernel weights sum to one, the visible
            // weight IS `1 - occlusion` at a corner, so `mean * (1 - occlusion)` collapses to
            // `sum(weight * open * light)` — the expression the engine evaluated before SS-2, now for an
            // arbitrary light field rather than only a uniform one. Baseline B58 pins that.
            TangentSpan(point.x, out int offsetA, out float weightDirectA, out float weightNeighborA);
            TangentSpan(point.y, out int offsetB, out float weightDirectB, out float weightNeighborB);

            Span<int> kernelCell = stackalloc int[LIGHT_KERNEL_COUNT]
            {
                OccluderIndex(0, 0),
                OccluderIndex(offsetA, 0),
                OccluderIndex(0, offsetB),
                OccluderIndex(offsetA, offsetB),
            };

            Span<float> kernelWeight = stackalloc float[LIGHT_KERNEL_COUNT]
            {
                weightDirectA * weightDirectB,
                weightNeighborA * weightDirectB,
                weightDirectA * weightNeighborB,
                weightNeighborA * weightNeighborB,
            };

            float4 visibleSum = float4.zero, litSum = float4.zero;
            float visibleWeight = 0f, litWeight = 0f;

            for (int k = 0; k < LIGHT_KERNEL_COUNT; k++)
            {
                int cell = kernelCell[k];
                float weight = kernelWeight[k];
                if (weight <= 0f || occluders[cell].HoldsLight == 0) continue;

                FaceOccluder o = occluders[cell];
                float4 value = new float4(o.Sun, o.R, o.G, o.B);

                litSum += weight * value;
                litWeight += weight;

                float visible = weight * (1f - shadow[cell]);
                visibleSum += visible * value;
                visibleWeight += visible;
            }

            // Fall back to the unshadowed mean when the kernel sees nothing: at a face center the kernel
            // collapses onto the single cell in front of the face, and if that cell occludes — a slab
            // standing on the surface — there is no visible light source left to average. The occlusion
            // term already carries the darkening there; a zero would render the face black.
            float4 light = visibleWeight > LIGHT_WEIGHT_EPSILON
                ? visibleSum / visibleWeight
                : (litWeight > LIGHT_WEIGHT_EPSILON ? litSum / litWeight : float4.zero);

            float4 shaded = light * (1f - occlusion);

            return new Color32(
                EncodeChannel(shaded.x),
                EncodeChannel(shaded.y),
                EncodeChannel(shaded.z),
                EncodeChannel(shaded.w));
        }

        /// <summary>
        /// Caps each diagonal cell's shadow at the weaker of the two cells flanking it — the smooth form
        /// of classic voxel AO's "if both sides seal the corner, skip the diagonal" rule.
        /// <para>
        /// <b>Without it an inside corner between two walls lightens from 64 to 127.</b> A point tucked
        /// into such a corner cannot see the diagonal quadrant at all, whatever that cell contains,
        /// because the two walls meeting at the corner stand between them. Treating the diagonal as
        /// independently open would let light leak through geometry — the artifact the original rule
        /// exists to prevent, and one this model would otherwise have reintroduced.
        /// </para>
        /// <para>
        /// Written as <c>max(own, sideA · sideB)</c> rather than the original boolean test, so the seal
        /// closes gradually as the flanking occluders approach: a partial block half-blocking one side
        /// half-seals the corner instead of switching it. <b>The product is the load-bearing choice</b>
        /// (SS-2a). Both sides must hide the diagonal for it to be hidden, and only a product falls off
        /// with distance from the corner in the way that conjunction demands — <c>min</c> holds the seal
        /// at full strength along the entire diagonal, sealing open floor a whole cell away from the
        /// corner and creasing where the two arguments cross. That shipped as a dark wedge running
        /// diagonally out of every concave corner. Baseline <b>B57</b> guards both halves: the seal is
        /// still whole in the corner, and it decays away from it.
        /// </para>
        /// <para>
        /// At a cell corner with full cubes the two forms are identical (every argument is 0 or 1), which
        /// is why <b>B56</b>'s reduction is untouched. Away from one they diverge sharply: measured on a
        /// concave corner, the seal's contribution half a cell out along the diagonal falls from 16 light
        /// units to 4, while the corner itself holds at 63.
        /// </para>
        /// </summary>
        /// <param name="shadow">Per-cell shadow strengths for the hoisted 3x3, modified in place.</param>
        /// <param name="quadrant">Per-direction shadow strengths, modified in place.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ApplyCornerSeal(Span<float> shadow, Span<float> quadrant)
        {
            for (int da = -1; da <= 1; da += 2)
            {
                for (int db = -1; db <= 1; db += 2)
                {
                    int diagonal = OccluderIndex(da, db);
                    float sealStrength = shadow[OccluderIndex(da, 0)] * shadow[OccluderIndex(0, db)];

                    // Applied to both readings, and it has to be: the cell copy feeds the light mean,
                    // the quadrant copy feeds the occlusion term, and the identity that keeps a corner
                    // matching the pre-SS-2 model holds only while the two agree there.
                    shadow[diagonal] = math.max(shadow[diagonal], sealStrength);

                    int q = QuadrantIndex(da, db);
                    quadrant[q] = math.max(quadrant[q], sealStrength);
                }
            }
        }

        /// <summary>
        /// Clips an occluder's silhouette to one quadrant around the shaded point, rejecting clips with
        /// no area — an occluder that merely <i>touches</i> a quadrant's boundary blocks none of it.
        /// </summary>
        /// <param name="point">The shaded point, in the face's parameter frame.</param>
        /// <param name="occluder">The occluder whose silhouette is clipped.</param>
        /// <param name="qa">Quadrant sign on the first tangent axis (-1 or +1).</param>
        /// <param name="qb">Quadrant sign on the second tangent axis.</param>
        /// <param name="clipMin">Minimum corner of the clipped rectangle.</param>
        /// <param name="clipMax">Maximum corner.</param>
        /// <returns>True when the silhouette covers area inside that quadrant.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool ClipToQuadrant(float2 point, in FaceOccluder occluder, int qa, int qb,
            out float2 clipMin, out float2 clipMax)
        {
            clipMin = new float2(
                qa > 0 ? math.max(occluder.RectMin.x, point.x) : occluder.RectMin.x,
                qb > 0 ? math.max(occluder.RectMin.y, point.y) : occluder.RectMin.y);

            clipMax = new float2(
                qa > 0 ? occluder.RectMax.x : math.min(occluder.RectMax.x, point.x),
                qb > 0 ? occluder.RectMax.y : math.min(occluder.RectMax.y, point.y));

            return clipMax.x - clipMin.x > QUADRANT_AREA_EPSILON
                   && clipMax.y - clipMin.y > QUADRANT_AREA_EPSILON;
        }

        /// <summary>Number of tangent quadrants around a shaded point.</summary>
        private const int QUADRANT_COUNT = 4;

        /// <summary>
        /// Smallest extent that counts as covering a quadrant. A silhouette lying exactly along a
        /// quadrant boundary — a neighboring cell's edge through the shaded point — has zero area on
        /// one side and must not darken it, or every occluder would darken all four.
        /// </summary>
        private const float QUADRANT_AREA_EPSILON = 1e-4f;

        /// <summary>Maps a quadrant's two signs to its slot.</summary>
        /// <param name="qa">Sign on the first tangent axis (-1 or +1).</param>
        /// <param name="qb">Sign on the second tangent axis.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int QuadrantIndex(int qa, int qb) => (qa > 0 ? 2 : 0) + (qb > 0 ? 1 : 0);

        /// <summary>
        /// How many cells the light interpolation kernel reaches: the sample box is one cell wide, so it
        /// spans two cells on each tangent axis.
        /// </summary>
        private const int LIGHT_KERNEL_COUNT = 4;

        /// <summary>
        /// Euclidean distance from a point to an occluder's silhouette rectangle, in cells; zero when
        /// the point lies inside it.
        /// <para>
        /// This metric is what makes the shadow follow the occluder's <i>shape</i>. The pre-SS-2 model
        /// weighted an occluder by a product of two per-axis ramps, whose isocontours are hyperbolic —
        /// so an isolated block's shadow reached about twice as far diagonally as it did straight out,
        /// and read as a round blob rather than a band of even width.
        /// </para>
        /// </summary>
        /// <param name="point">The sample point, in the face's parameter frame.</param>
        /// <param name="occluder">The occluder whose silhouette is measured against.</param>
        /// <returns>The distance in cells, clamped at zero inside the rectangle.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float DistanceToRect(float2 point, in FaceOccluder occluder)
        {
            return DistanceToRect(point, occluder.RectMin, occluder.RectMax);
        }

        /// <summary>
        /// Euclidean distance from a point to an axis-aligned rectangle given by its corners; zero when
        /// the point lies inside it. The form <see cref="ClipToQuadrant"/> results are measured with.
        /// </summary>
        /// <param name="point">The sample point, in the face's parameter frame.</param>
        /// <param name="rectMin">Minimum corner of the rectangle.</param>
        /// <param name="rectMax">Maximum corner.</param>
        /// <returns>The distance in cells, clamped at zero inside the rectangle.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float DistanceToRect(float2 point, float2 rectMin, float2 rectMax)
        {
            float2 outside = math.max(math.max(rectMin - point, point - rectMax), 0f);
            return math.length(outside);
        }

        /// <summary>Encodes one 0-15 light channel to UNorm8, rounding as the pre-SS-2 encode did.</summary>
        /// <param name="value">The channel value, in <c>[0, 15]</c>.</param>
        /// <returns>The encoded byte.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte EncodeChannel(float value)
        {
            return (byte)math.min(255f, value * 17f + 0.5f);
        }

        /// <summary>Below this total weight the light mean is undefined and the point is fully enclosed.</summary>
        private const float LIGHT_WEIGHT_EPSILON = 1e-6f;

        /// <summary>
        /// Splits the sample box's extent along one tangent axis between the direct cell and whichever
        /// neighbor the box reaches into.
        /// <para>
        /// The box is one cell wide and centred on the sample point, so it always spans exactly two cells
        /// along each tangent axis. Sampling at a cell corner splits it evenly; sampling at the cell's
        /// midline gives the whole box to the direct cell — correct for interpolating <i>light</i>, and
        /// the reason occlusion must not ride on these weights (see <see cref="ShadePoint"/>).
        /// </para>
        /// </summary>
        /// <param name="local">The sample point's coordinate on this axis, relative to the direct cell.</param>
        /// <param name="neighborOffset">Which neighbor the box reaches: -1 or +1 cells.</param>
        /// <param name="directWeight">Share of the box inside the direct cell, in <c>[0, 1]</c>.</param>
        /// <param name="neighborWeight">Share of the box inside the neighbor.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void TangentSpan(float local, out int neighborOffset,
            out float directWeight, out float neighborWeight)
        {
            neighborOffset = local < 0.5f ? -1 : 1;
            neighborWeight = math.saturate(neighborOffset < 0 ? 0.5f - local : local - 0.5f);
            directWeight = 1f - neighborWeight;
        }

        /// <summary>Returns a unit-axis step vector: <paramref name="amount"/> on <paramref name="axis"/>, zero elsewhere.</summary>
        /// <param name="axis">0 = X, 1 = Y, 2 = Z.</param>
        /// <param name="amount">Signed cell count to step.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector3Int AxisStep(int axis, int amount)
        {
            return new Vector3Int(axis == 0 ? amount : 0, axis == 1 ? amount : 0, axis == 2 ? amount : 0);
        }

        /// <summary>Reads one component of a <see cref="Vector3Int"/> by axis index, branchlessly enough for Burst.</summary>
        /// <param name="value">The vector to read.</param>
        /// <param name="axis">0 = X, 1 = Y, 2 = Z.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int Component(Vector3Int value, int axis)
        {
            return axis == 0 ? value.x : axis == 1 ? value.y : value.z;
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
                    targetLight = LightSW;
                }
                else if (pos.z >= VoxelData.ChunkWidth)
                {
                    localPos.z -= VoxelData.ChunkWidth;
                    targetLight = LightNW;
                }
                else
                {
                    targetLight = LightW;
                }
            }
            else if (pos.x >= VoxelData.ChunkWidth)
            {
                localPos.x -= VoxelData.ChunkWidth;
                if (pos.z < 0)
                {
                    localPos.z += VoxelData.ChunkWidth;
                    targetLight = LightSE;
                }
                else if (pos.z >= VoxelData.ChunkWidth)
                {
                    localPos.z -= VoxelData.ChunkWidth;
                    targetLight = LightNE;
                }
                else
                {
                    targetLight = LightE;
                }
            }
            else
            {
                if (pos.z < 0)
                {
                    localPos.z += VoxelData.ChunkWidth;
                    targetLight = LightS;
                }
                else if (pos.z >= VoxelData.ChunkWidth)
                {
                    localPos.z -= VoxelData.ChunkWidth;
                    targetLight = LightN;
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
                    targetMap = NeighborSW;
                }
                else if (pos.z >= VoxelData.ChunkWidth) // North-West
                {
                    localPos.z -= VoxelData.ChunkWidth;
                    targetMap = NeighborNW;
                }
                else // West
                {
                    targetMap = NeighborW;
                }
            }
            else if (pos.x >= VoxelData.ChunkWidth) // EAST (+X)
            {
                localPos.x -= VoxelData.ChunkWidth;
                if (pos.z < 0) // South-East
                {
                    localPos.z += VoxelData.ChunkWidth;
                    targetMap = NeighborSE;
                }
                else if (pos.z >= VoxelData.ChunkWidth) // North-East
                {
                    localPos.z -= VoxelData.ChunkWidth;
                    targetMap = NeighborNE;
                }
                else // East
                {
                    targetMap = NeighborE;
                }
            }
            else // CENTER X
            {
                if (pos.z < 0) // South
                {
                    localPos.z += VoxelData.ChunkWidth;
                    targetMap = NeighborS;
                }
                else if (pos.z >= VoxelData.ChunkWidth) // North
                {
                    localPos.z -= VoxelData.ChunkWidth;
                    targetMap = NeighborN;
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
