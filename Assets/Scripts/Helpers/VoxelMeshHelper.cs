using System.Runtime.CompilerServices;
using Data;
using Jobs.BurstData;
using Unity.Burst;
using Unity.Burst.CompilerServices;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Helpers
{
    [BurstCompile]
    public static class VoxelMeshHelper
    {
        // This array correctly maps the vertex order for each face to the UV coordinate order.
        // This is the key to fixing the 3D preview textures and ensuring correct runtime textures.
        private static readonly int[] s_faceUvOrder =
        {
            0, 1, 2, 3, // Back Face
            2, 3, 0, 1, // Front Face
            0, 1, 2, 3, // Top Face
            0, 1, 2, 3, // Bottom Face
            1, 3, 0, 2, // Left Face
            0, 2, 1, 3, // Right Face
        };

        /// <summary>
        /// Calculates and appends the precise UV coordinates for a given texture ID to the UV list.
        /// Accounts for the normalized texture atlas size and origin alignment.
        /// </summary>
        /// <param name="textureID">The index of the texture within the atlas.</param>
        /// <param name="uv">The local UV offset for the current vertex.</param>
        /// <param name="uvs">The native list of UVs to append to.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
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
        [SkipLocalsInit] // Optimization: Skip zeroing local variables (Vector3s, Colors) as we overwrite them immediately.
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
                colors.Add(new Color(1f, 1f, 1f, lightLevel));

                // Use the FaceUvOrder array to get the correct UV for this vertex.
                int uvIndex = s_faceUvOrder[faceIndex * 4 + i];
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
        [SkipLocalsInit] // Optimization: Skip zeroing local variables.
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
                colors.Add(new Color(1f, 1f, 1f, lightLevel));
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
        [SkipLocalsInit] // Optimization: Fluid generation uses many local floats/vectors. Skipping init saves cycles.
        public static void GenerateFluidMeshData(
            in Vector3Int pos,
            uint packedData,
            in BlockTypeJobData props,
            in NativeArray<float> templates,
            in NativeArray<BlockTypeJobData> blockTypes,
            [ReadOnly] in NativeArray<OptionalVoxelState> neighbors, // 14 neighbors: N, E, S, W, NE, SE, SW, NW, Above, Below, Above_N, Above_E, Above_S, Above_W
            ref int vertexIndex,
            ref NativeList<Vector3> vertices, ref NativeList<int> fluidTriangles,
            ref NativeList<Vector2> uvs, ref NativeList<Color> colors, ref NativeList<Vector3> normals)
        {
            // Unpack neighbor states
            OptionalVoxelState n_N = neighbors[0], n_E = neighbors[1], n_S = neighbors[2], n_W = neighbors[3];
            OptionalVoxelState n_NE = neighbors[4], n_SE = neighbors[5], n_SW = neighbors[6], n_NW = neighbors[7];
            OptionalVoxelState above = neighbors[8], below = neighbors[9];
            OptionalVoxelState above_N = neighbors[10], above_E = neighbors[11], above_S = neighbors[12], above_W = neighbors[13];

            // --- 1. DETERMINE SHADER FLAGS ---
            float liquidType = props.FluidShaderID;
            float shorelineFlag = 0.0f;

            // Check horizontal neighbors (N, E, S, W) for "shoreline" effect
            for (int k = 0; k < 4; k++)
            {
                OptionalVoxelState shoreNeighbor = neighbors[k];
                // Neighboring voxels need to be solid...
                if (shoreNeighbor.HasValue && blockTypes[shoreNeighbor.State.id].IsSolid && blockTypes[shoreNeighbor.State.id].FluidType == FluidType.None)
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

            // --- 3. CALCULATE FLOW VECTOR ---
            // Calculate 4 distinct corner flow vectors symmetrically for seamless interpolation across blocks
            OptionalVoxelState centerState = new OptionalVoxelState(new VoxelState(packedData));

            Vector2 flow_bl = CalculateSymmetricCornerFlow(n_SW, n_S, n_W, centerState, props.FluidType, in templates, in blockTypes);
            Vector2 flow_tl = CalculateSymmetricCornerFlow(n_W, centerState, n_NW, n_N, props.FluidType, in templates, in blockTypes);
            Vector2 flow_br = CalculateSymmetricCornerFlow(n_S, n_SE, centerState, n_E, props.FluidType, in templates, in blockTypes);
            Vector2 flow_tr = CalculateSymmetricCornerFlow(centerState, n_E, n_N, n_NE, props.FluidType, in templates, in blockTypes);

            // Clamp smoothed corner heights to a small positive value to prevent z-fighting
            // with the floor block's top face when a corner averages down to exactly 0.0f.
            const float kMinFluidSurfaceHeight = 0.005f;
            float smooth_tr = math.max(kMinFluidSurfaceHeight, GetSmoothedCornerHeight(in props, fluidLevel, n_N, n_E, n_NE, in templates, in blockTypes));
            float smooth_tl = math.max(kMinFluidSurfaceHeight, GetSmoothedCornerHeight(in props, fluidLevel, n_N, n_W, n_NW, in templates, in blockTypes));
            float smooth_br = math.max(kMinFluidSurfaceHeight, GetSmoothedCornerHeight(in props, fluidLevel, n_S, n_E, n_SE, in templates, in blockTypes));
            float smooth_bl = math.max(kMinFluidSurfaceHeight, GetSmoothedCornerHeight(in props, fluidLevel, n_S, n_W, n_SW, in templates, in blockTypes));

            // Check if we have fluid directly above us
            bool hasFluidAbove = above.HasValue && blockTypes[above.State.id].FluidType == props.FluidType;

            // Force all corners to 1.0 when submerged so the block connects seamlessly to the one above.
            float height_tr = hasFluidAbove ? 1.0f : smooth_tr;
            float height_tl = hasFluidAbove ? 1.0f : smooth_tl;
            float height_br = hasFluidAbove ? 1.0f : smooth_br;
            float height_bl = hasFluidAbove ? 1.0f : smooth_bl;


            // --- 4. GENERATE FACES ---
            // --- 4A. Top Face ---
            // Draw unless the same fluid is directly above, that would make the face interior to the fluid body.
            // Note: opaque blocks above (e.g. stone ceiling) must NOT suppress this face.
            if (!above.HasValue || blockTypes[above.State.id].FluidType != props.FluidType)
            {
                vertices.Add(pos + new Vector3(0, height_bl, 0)); // Back-Left
                vertices.Add(pos + new Vector3(0, height_tl, 1)); // Front-Left
                vertices.Add(pos + new Vector3(1, height_br, 0)); // Back-Right
                vertices.Add(pos + new Vector3(1, height_tr, 1)); // Front-Right

                float lightLevel = above.HasValue ? above.State.lightAsFloat : 1.0f;
                // v.color.r = liquidType
                // v.color.g = shorelineFlag
                // v.color.b = Isometric Shadow Multiplier (Defaults to 1.0f for runtime fluids)
                // v.color.a = lightLevel
                Color vertexColor = new Color(liquidType, shorelineFlag, 1.0f, lightLevel);

                // Add vertices/normals/colors/uvs specifically matching winding order: BL, TL, BR, TR
                normals.Add(Vector3.up);
                colors.Add(vertexColor);
                uvs.Add(flow_bl); // Back-Left
                normals.Add(Vector3.up);
                colors.Add(vertexColor);
                uvs.Add(flow_tl); // Front-Left
                normals.Add(Vector3.up);
                colors.Add(vertexColor);
                uvs.Add(flow_br); // Back-Right
                normals.Add(Vector3.up);
                colors.Add(vertexColor);
                uvs.Add(flow_tr); // Front-Right

                fluidTriangles.Add(vertexIndex);
                fluidTriangles.Add(vertexIndex + 1);
                fluidTriangles.Add(vertexIndex + 2);
                fluidTriangles.Add(vertexIndex + 2);
                fluidTriangles.Add(vertexIndex + 1);
                fluidTriangles.Add(vertexIndex + 3);
                vertexIndex += 4;
            }

            // --- 4B. Side Faces ---
            for (int n = 0; n < 4; n++)
            {
                int faceIndex = VoxelData.HorizontalFaceChecksIndices[n];
                OptionalVoxelState sideNeighbor;
                OptionalVoxelState sideNeighborAbove;

                switch (faceIndex)
                {
                    case 1:
                        sideNeighbor = n_N;
                        sideNeighborAbove = above_N;
                        break;
                    case 0:
                        sideNeighbor = n_S;
                        sideNeighborAbove = above_S;
                        break;
                    case 5:
                        sideNeighbor = n_E;
                        sideNeighborAbove = above_E;
                        break;
                    case 4:
                        sideNeighbor = n_W;
                        sideNeighborAbove = above_W;
                        break;
                    default: continue;
                }

                bool isNeighborSameFluid = sideNeighbor.HasValue && blockTypes[sideNeighbor.State.id].FluidType == props.FluidType;

                // When true, the side face bottom is raised to the smooth surface level (waterfall curtain).
                // When false, the face runs from y=0 up to the smooth heights (shallow edge gap-fill).
                bool useSmoothBottom = false;

                if (isNeighborSameFluid)
                {
                    bool isFullHeight = hasFluidAbove || templates[fluidLevel] >= 1.0f;
                    bool neighborIsFullHeight = templates[sideNeighbor.State.FluidLevel] >= 1.0f;
                    bool neighborHasFluidAbove = sideNeighborAbove.HasValue &&
                                                 blockTypes[sideNeighborAbove.State.id].FluidType == props.FluidType;
                    bool neighborIsEffectivelyFullHeight = neighborIsFullHeight || neighborHasFluidAbove;

                    if (isFullHeight)
                    {
                        // We are submerged or a waterfall. Cull if the neighbor is also full-height — no gap between them.
                        if (neighborIsEffectivelyFullHeight) continue;

                        // Neighbor is shallower; draw a curtain from our top (1.0) down to its surface.
                        useSmoothBottom = true;
                    }
                    else
                    {
                        // We are a shallow horizontal-flow block.
                        // Cull toward any full-height neighbor — it draws the curtain on its own side.
                        if (neighborIsEffectivelyFullHeight) continue;

                        // Adjacent same-fluid surfaces tile seamlessly, so no top-surface face is needed between them.
                        // However, culling both side faces exposes the void beneath the mesh when viewed horizontally.
                        // We seal this with a gap-fill face, but only where it's actually visible:
                        //   - neighbor template > 0.0f → interior pool edge → CULL (face would show through water surface above)
                        //   - neighbor template = 0.0f → outermost pool edge → DRAW (void is directly exposed to the viewer)
                        if (templates[sideNeighbor.State.FluidLevel] > 0f) continue;
                    }
                }
                else
                {
                    // Neighbor is not the same fluid — cull only against opaque solids.
                    if (sideNeighbor.HasValue && !blockTypes[sideNeighbor.State.id].IsTransparentForMesh) continue;
                }

                int v1 = BurstVoxelData.VoxelTris.Data[faceIndex * 4 + 0];
                int v2 = BurstVoxelData.VoxelTris.Data[faceIndex * 4 + 1];
                int v3 = BurstVoxelData.VoxelTris.Data[faceIndex * 4 + 2];
                int v4 = BurstVoxelData.VoxelTris.Data[faceIndex * 4 + 3];

                Vector3 p1 = BurstVoxelData.VoxelVerts.Data[v1];
                Vector3 p2 = BurstVoxelData.VoxelVerts.Data[v2];
                Vector3 p3 = BurstVoxelData.VoxelVerts.Data[v3];
                Vector3 p4 = BurstVoxelData.VoxelVerts.Data[v4];

                // Calculate the correct bottom vertex height to seal the gap seamlessly without extending down to 0
                // - Waterfall curtain (useSmoothBottom=true): bottom raised to neighbor's surface → face fills gap from 1.0 down.
                // - Shallow gap-fill (useSmoothBottom=false):  bottom stays at y=0  → face fills gap from 0 up to our surface.
                // - Non-fluid neighbor (useSmoothBottom=false): bottom stays at y=0 → full-height wall face (original behavior).
                float bottomHeight_p1 = useSmoothBottom ? GetCornerValue(in p1, smooth_tl, smooth_tr, smooth_bl, smooth_br) : 0f;
                float bottomHeight_p2 = useSmoothBottom ? GetCornerValue(in p2, smooth_tl, smooth_tr, smooth_bl, smooth_br) : 0f;
                float bottomHeight_p3 = useSmoothBottom ? GetCornerValue(in p3, smooth_tl, smooth_tr, smooth_bl, smooth_br) : 0f;
                float bottomHeight_p4 = useSmoothBottom ? GetCornerValue(in p4, smooth_tl, smooth_tr, smooth_bl, smooth_br) : 0f;


                p1.y = p1.y > 0.5f ? GetCornerValue(in p1, height_tl, height_tr, height_bl, height_br) : bottomHeight_p1;
                p2.y = p2.y > 0.5f ? GetCornerValue(in p2, height_tl, height_tr, height_bl, height_br) : bottomHeight_p2;
                p3.y = p3.y > 0.5f ? GetCornerValue(in p3, height_tl, height_tr, height_bl, height_br) : bottomHeight_p3;
                p4.y = p4.y > 0.5f ? GetCornerValue(in p4, height_tl, height_tr, height_bl, height_br) : bottomHeight_p4;

                vertices.Add(pos + p1);
                vertices.Add(pos + p2);
                vertices.Add(pos + p3);
                vertices.Add(pos + p4);

                float lightLevel = sideNeighbor.HasValue ? sideNeighbor.State.lightAsFloat : 1.0f;
                // Use 1.0f for the b-channel so side faces default to full brightness (unshadowed) in game
                Color vertexColor = new Color(liquidType, shorelineFlag, 1.0f, lightLevel);

                Vector2 uv1, uv2, uv3, uv4;

                if (fluidLevel >= 8) // Waterfall (Falling Fluid)
                {
                    // Force a strict downward flow at higher speed (V-axis)
                    uv1 = uv2 = uv3 = uv4 = new Vector2(0f, 1.5f);
                }
                else // Horizontal Spreading Fluid
                {
                    // 1. Get raw XZ flow at the corners
                    Vector2 f1 = GetCornerValue(in p1, flow_tl, flow_tr, flow_bl, flow_br);
                    Vector2 f2 = GetCornerValue(in p2, flow_tl, flow_tr, flow_bl, flow_br);
                    Vector2 f3 = GetCornerValue(in p3, flow_tl, flow_tr, flow_bl, flow_br);
                    Vector2 f4 = GetCornerValue(in p4, flow_tl, flow_tr, flow_bl, flow_br);

                    // 2. Project XZ flow onto the 2D plane of this specific side face
                    uv1 = ProjectFlowToSideFace(f1, faceIndex);
                    uv2 = ProjectFlowToSideFace(f2, faceIndex);
                    uv3 = ProjectFlowToSideFace(f3, faceIndex);
                    uv4 = ProjectFlowToSideFace(f4, faceIndex);
                }

                normals.Add(VoxelData.FaceChecks[faceIndex]);
                colors.Add(vertexColor);
                uvs.Add(uv1);
                normals.Add(VoxelData.FaceChecks[faceIndex]);
                colors.Add(vertexColor);
                uvs.Add(uv2);
                normals.Add(VoxelData.FaceChecks[faceIndex]);
                colors.Add(vertexColor);
                uvs.Add(uv3);
                normals.Add(VoxelData.FaceChecks[faceIndex]);
                colors.Add(vertexColor);
                uvs.Add(uv4);

                fluidTriangles.Add(vertexIndex);
                fluidTriangles.Add(vertexIndex + 1);
                fluidTriangles.Add(vertexIndex + 2);
                fluidTriangles.Add(vertexIndex + 2);
                fluidTriangles.Add(vertexIndex + 1);
                fluidTriangles.Add(vertexIndex + 3);
                vertexIndex += 4;
            }

            // --- 4C. Bottom Face ---
            // Only draw bottom face if below neighboring voxel is transparent or a different fluid.
            if (!below.HasValue || blockTypes[below.State.id].IsTransparentForMesh && blockTypes[below.State.id].FluidType != props.FluidType)
            {
                vertices.Add(pos + new Vector3(0, 0, 0)); // Back-Left   (0)
                vertices.Add(pos + new Vector3(0, 0, 1)); // Front-Left  (1)
                vertices.Add(pos + new Vector3(1, 0, 0)); // Back-Right  (2)
                vertices.Add(pos + new Vector3(1, 0, 1)); // Front-Right (3)

                float lightLevel = below.HasValue ? below.State.lightAsFloat : 1.0f;
                // Use 1.0f for the b-channel so bottom faces default to full brightness (unshadowed) in game
                Color vertexColor = new Color(liquidType, shorelineFlag, 1.0f, lightLevel);

                // Add vertices/normals/colors/uvs specifically matching winding order: BL, TL, BR, TR
                normals.Add(Vector3.down);
                colors.Add(vertexColor);
                uvs.Add(flow_bl);
                normals.Add(Vector3.down);
                colors.Add(vertexColor);
                uvs.Add(flow_tl);
                normals.Add(Vector3.down);
                colors.Add(vertexColor);
                uvs.Add(flow_br);
                normals.Add(Vector3.down);
                colors.Add(vertexColor);
                uvs.Add(flow_tr);

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

        /// <summary>
        /// Calculates the smoothed height for a fluid block's corner by averaging its height
        /// with adjacent and diagonal fluid neighbors. Prevents height smoothing through solid walls.
        /// </summary>
        /// <param name="centerProps">The properties of the center fluid block.</param>
        /// <param name="centerLevel">The fluid level of the center block.</param>
        /// <param name="n1">The first adjacent orthogonal neighbor.</param>
        /// <param name="n2">The second adjacent orthogonal neighbor.</param>
        /// <param name="nDiag">The diagonal neighbor shared by n1 and n2.</param>
        /// <param name="templates">The pre-computed height templates for this fluid type.</param>
        /// <param name="blockTypes">The global block types data array.</param>
        /// <returns>The averaged height for the evaluated corner.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float GetSmoothedCornerHeight(in BlockTypeJobData centerProps, byte centerLevel, OptionalVoxelState n1, OptionalVoxelState n2, OptionalVoxelState nDiag, in NativeArray<float> templates, in NativeArray<BlockTypeJobData> blockTypes)
        {
            float totalHeight = templates[centerLevel];
            int count = 1;

            // Track if adjacent neighbors are fluids to determine if the diagonal path is open ---
            bool n1IsFluid = n1.HasValue && blockTypes[n1.State.id].FluidType == centerProps.FluidType;
            bool n2IsFluid = n2.HasValue && blockTypes[n2.State.id].FluidType == centerProps.FluidType;

            if (n1IsFluid)
            {
                totalHeight += templates[n1.State.FluidLevel];
                count++;
            }

            if (n2IsFluid)
            {
                totalHeight += templates[n2.State.FluidLevel];
                count++;
            }

            // Only consider the diagonal neighbor for smoothing if at least one of the
            // adjacent neighbors is also a fluid. This prevents height smoothing "through" solid corners.
            bool nDiagIsFluid = nDiag.HasValue && blockTypes[nDiag.State.id].FluidType == centerProps.FluidType;
            if ((n1IsFluid || n2IsFluid) && nDiagIsFluid)
            {
                totalHeight += templates[nDiag.State.FluidLevel];
                count++;
            }

            return totalHeight / count;
        }

        /// <summary>
        /// Calculates a discrete 2D flow-direction vector for a specific corner of a fluid block symmetrically.
        /// By evaluating the 4 blocks that share this corner together, it guarantees mathematically identical
        /// flow vectors across chunk and block boundaries, eliminating UV seams.
        /// </summary>
        /// <param name="b00">The block at local (-x, -z) of the corner.</param>
        /// <param name="b10">The block at local (+x, -z) of the corner.</param>
        /// <param name="b01">The block at local (-x, +z) of the corner.</param>
        /// <param name="b11">The block at local (+x, +z) of the corner.</param>
        /// <param name="fluidType">The fluid type being evaluated.</param>
        /// <param name="templates">The pre-computed height templates for this fluid type.</param>
        /// <param name="blockTypes">The global block types data array.</param>
        /// <returns>A 2D vector representing the XZ flow direction at this corner.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector2 CalculateSymmetricCornerFlow(
            OptionalVoxelState b00, OptionalVoxelState b10,
            OptionalVoxelState b01, OptionalVoxelState b11,
            FluidType fluidType,
            in NativeArray<float> templates, in NativeArray<BlockTypeJobData> blockTypes)
        {
            float h00 = GetEffectiveFluidHeight(b00, fluidType, templates, blockTypes);
            float h10 = GetEffectiveFluidHeight(b10, fluidType, templates, blockTypes);
            float h01 = GetEffectiveFluidHeight(b01, fluidType, templates, blockTypes);
            float h11 = GetEffectiveFluidHeight(b11, fluidType, templates, blockTypes);

            // Obstacle handling: Find the highest actual fluid at this corner.
            // Clamping solid walls (>1.0f) to be just slightly higher than the local fluid
            // so they gently push fluid away, without dominating the gradient vector.
            float maxFluidHeight = -1.0f;
            if (h00 <= 1.0f) maxFluidHeight = math.max(maxFluidHeight, h00);
            if (h10 <= 1.0f) maxFluidHeight = math.max(maxFluidHeight, h10);
            if (h01 <= 1.0f) maxFluidHeight = math.max(maxFluidHeight, h01);
            if (h11 <= 1.0f) maxFluidHeight = math.max(maxFluidHeight, h11);

            // Obstacle handling: Flow should mathematically push *away* from solid walls.
            float wallPushHeight = maxFluidHeight > -1.0f ? maxFluidHeight + 0.085f : 1.05f;

            if (h00 > 1.0f) h00 = wallPushHeight;
            if (h10 > 1.0f) h10 = wallPushHeight;
            if (h01 > 1.0f) h01 = wallPushHeight;
            if (h11 > 1.0f) h11 = wallPushHeight;

            // Calculate symmetric X and Z derivatives across the 2x2 block grid corner using Central Difference.
            // Positive value means height increases in positive axis, therefore flow is negative (downstream).
            float dx = ((h10 - h00) + (h11 - h01)) * 0.5f;
            float dz = ((h01 - h00) + (h11 - h10)) * 0.5f;

            Vector2 cornerFlow = new Vector2(dx, dz);
            float sqrMag = cornerFlow.sqrMagnitude;

            if (sqrMag < 0.0001f) return Vector2.zero;

            // 1. Get the pure normalized direction
            float mag = math.sqrt(sqrMag);
            Vector2 dir = cornerFlow / mag;

            // 2. Apply a smooth speed curve to the magnitude.
            // Gentle slopes (mag 0.25) get boosted to a standard speed of 1.0.
            // Steep drops/waterfalls (mag 1.0+) get boosted to 1.5.
            float speed = math.smoothstep(0.0f, 0.25f, mag) + math.smoothstep(0.8f, 1.2f, mag) * 0.5f;

            return dir * speed;
        }

        /// <summary>
        /// Determines the effective visual height of a neighboring block for fluid smoothing and flow calculations.
        /// Treats solid obstacles as high walls (2.0) and open drops as strong pulls (-1.0).
        /// </summary>
        /// <param name="neighbor">The neighbor voxel state to evaluate.</param>
        /// <param name="centerFluidType">The fluid type of the center block (Water/Lava).</param>
        /// <param name="templates">The pre-computed height templates for this fluid type.</param>
        /// <param name="blockTypes">The global block types data array.</param>
        /// <returns>The effective relative height of the neighbor.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float GetEffectiveFluidHeight(OptionalVoxelState neighbor, FluidType centerFluidType, in NativeArray<float> templates, in NativeArray<BlockTypeJobData> blockTypes)
        {
            if (!neighbor.HasValue) return 0f; // Neutral chunk edge

            BlockTypeJobData nbProps = blockTypes[neighbor.State.id];

            // Solid obstacle
            if (nbProps.IsSolid && !nbProps.IsTransparentForMesh) return 2.0f; // Represents a solid wall (higher than fluid 1.0)

            // Open Drop / Pit
            if (nbProps.FluidType == FluidType.None && !nbProps.IsSolid) return -1.0f; // Massive pull

            // Same fluid type
            if (nbProps.FluidType == centerFluidType) return templates[neighbor.State.FluidLevel];

            return 0f;
        }

        /// <summary>
        /// Retrieves the correct interpolated value (e.g., height or flow vector) for a specific vertex
        /// based on its local spatial quadrant within the 1x1x1 voxel bounds.
        /// </summary>
        /// <typeparam name="T">The type of the value being retrieved (e.g., float, Vector2).</typeparam>
        /// <param name="vertPos">The local position of the vertex.</param>
        /// <param name="val_tl">The value mapped to the top-left (North-West) corner.</param>
        /// <param name="val_tr">The value mapped to the top-right (North-East) corner.</param>
        /// <param name="val_bl">The value mapped to the bottom-left (South-West) corner.</param>
        /// <param name="val_br">The value mapped to the bottom-right (South-East) corner.</param>
        /// <returns>The specific value assigned to the evaluated corner.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static T GetCornerValue<T>(in Vector3 vertPos, T val_tl, T val_tr, T val_bl, T val_br)
        {
            if (vertPos.x > 0.5f) // Right side
                return vertPos.z > 0.5f ? val_tr : val_br;

            // Left side
            return vertPos.z > 0.5f ? val_tl : val_bl;
        }

        /// <summary>
        /// Projects a 2D world-space XZ fluid flow vector onto the 2D UV plane of a specific vertical side face.
        /// Ensures that lateral momentum across the top surface correctly translates into horizontal
        /// drift or downward gravity flow (+V) along the walls.
        /// </summary>
        /// <param name="xzFlow">The calculated XZ flow vector at the corner.</param>
        /// <param name="faceIndex">The index of the vertical face (Back, Front, Left, Right).</param>
        /// <returns>The projected 2D UV flow vector for the shader.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector2 ProjectFlowToSideFace(Vector2 xzFlow, int faceIndex)
        {
            // faceIndex: 0=Back(-Z), 1=Front(+Z), 4=Left(-X), 5=Right(+X)
            return faceIndex switch
            {
                0 or 1 => // Front or Back
                    // Face is on the XY plane.
                    // X-flow moves horizontally across the face.
                    // Z-flow is pushing off the edge, converting to downward gravity (+V).
                    new Vector2(xzFlow.x, math.abs(xzFlow.y)),
                4 or 5 => // Left or Right
                    // Face is on the YZ plane.
                    // Z-flow moves horizontally across the face (mapped to U).
                    // X-flow is pushing off the edge, converting to downward gravity (+V).
                    new Vector2(xzFlow.y, math.abs(xzFlow.x)),
                _ => Vector2.zero,
            };
        }
    }
}
