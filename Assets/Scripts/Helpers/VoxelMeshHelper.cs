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
        /// The ZW components are zeroed; they are only meaningful for fluid top faces (shore push).
        /// </summary>
        /// <param name="textureID">The index of the texture within the atlas.</param>
        /// <param name="uv">The local UV offset for the current vertex.</param>
        /// <param name="uvs">The native list of UVs to append to.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void AddTexture(int textureID, Vector2 uv, ref NativeList<Vector4> uvs)
        {
            float y = Mathf.FloorToInt((float)textureID / VoxelData.TextureAtlasSizeInBlocks);
            float x = textureID - y * VoxelData.TextureAtlasSizeInBlocks;

            x *= VoxelData.NormalizedBlockTextureSize;
            y *= VoxelData.NormalizedBlockTextureSize;

            y = 1f - y - VoxelData.NormalizedBlockTextureSize; // To start reading the atlas from the top left

            x += VoxelData.NormalizedBlockTextureSize * uv.x;
            y += VoxelData.NormalizedBlockTextureSize * uv.y;

            uvs.Add(new Vector4(x, y, 0f, 0f)); // zw = 0; shore push is fluid-only
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
            ref NativeList<Vector4> uvs, ref NativeList<Color> colors, ref NativeList<Vector3> normals,
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
            [ReadOnly] in NativeArray<CustomMeshData> customMeshes,
            [ReadOnly] in NativeArray<CustomFaceData> customFaces,
            [ReadOnly] in NativeArray<CustomVertData> customVerts,
            [ReadOnly] in NativeArray<int> customTris,
            ref int vertexIndex,
            ref NativeList<Vector3> vertices, ref NativeList<int> triangles, ref NativeList<int> transparentTriangles,
            ref NativeList<Vector4> uvs, ref NativeList<Color> colors, ref NativeList<Vector3> normals,
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
            ref NativeList<Vector4> uvs, ref NativeList<Color> colors, ref NativeList<Vector3> normals)
        {
            // Unpack neighbor states
            OptionalVoxelState n_N = neighbors[0], n_E = neighbors[1], n_S = neighbors[2], n_W = neighbors[3];
            OptionalVoxelState n_NE = neighbors[4], n_SE = neighbors[5], n_SW = neighbors[6], n_NW = neighbors[7];
            OptionalVoxelState above = neighbors[8], below = neighbors[9];
            OptionalVoxelState above_N = neighbors[10], above_E = neighbors[11], above_S = neighbors[12], above_W = neighbors[13];

            // --- 1. DETERMINE SHADER FLAGS ---
            float liquidType = props.FluidShaderID;

            // --- 2. GET HEIGHT DATA ---
            // First, get the LOGICAL top height based on the fluid level. This is ONLY used for face culling logic.
            byte fluidLevel = BurstVoxelDataBitMapping.GetFluidLevel(packedData);

            // --- 3. CALCULATE FLOW VECTORS & SHORE DATA ---
            // Calculate 4 distinct corner flow vectors symmetrically for seamless interpolation across blocks
            OptionalVoxelState centerState = new OptionalVoxelState(new VoxelState(packedData));

            // Calculate 4 distinct corner flow vectors for bilinear interpolation across the top face
            Vector2 flow_bl = CalculateSymmetricCornerFlow(n_SW, n_S, n_W, centerState, props.FluidType, in templates, in blockTypes);
            Vector2 flow_tl = CalculateSymmetricCornerFlow(n_W, centerState, n_NW, n_N, props.FluidType, in templates, in blockTypes);
            Vector2 flow_br = CalculateSymmetricCornerFlow(n_S, n_SE, centerState, n_E, props.FluidType, in templates, in blockTypes);
            Vector2 flow_tr = CalculateSymmetricCornerFlow(centerState, n_E, n_N, n_NE, props.FluidType, in templates, in blockTypes);

            // Shore push directions — symmetric 4-block neighborhood matching flow corners above.
            CalculateSymmetricCornerShorePush(n_SW, n_S, n_W, centerState, in blockTypes, out Vector2 shore_push_bl);
            CalculateSymmetricCornerShorePush(n_W, centerState, n_NW, n_N, in blockTypes, out Vector2 shore_push_tl);
            CalculateSymmetricCornerShorePush(n_S, n_SE, centerState, n_E, in blockTypes, out Vector2 shore_push_br);
            CalculateSymmetricCornerShorePush(centerState, n_E, n_N, n_NE, in blockTypes, out Vector2 shore_push_tr);

            // Clamp smoothed corner heights to a small positive value to prevent z-fighting.
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

                // Add vertices/normals/colors/uvs specifically matching winding order: BL, TL, BR, TR
                // v.color.r = liquidType
                // v.color.g = packedShoreMask — 8-bit wall neighbor flags packed into one float.
                //             Encoding: (wallN*1 + wallS*2 + wallE*4 + wallW*8 +
                //                        diagNE*16 + diagNW*32 + diagSE*64 + diagSW*128) / 255.0
                //             Identical at all 4 vertices so the GPU does not interpolate it.
                //             The shader decodes and computes per-pixel min-distance to the nearest wall.
                // v.color.b = Isometric Shadow Multiplier (1.0f at runtime)
                // v.color.a = lightLevel
                // v.uv.xy   = localFlowVector
                // v.uv.zw   = shorePush (normalized direction for displacement)
                bool wallN = IsSolidWall(n_N, in blockTypes);
                bool wallS = IsSolidWall(n_S, in blockTypes);
                bool wallE = IsSolidWall(n_E, in blockTypes);
                bool wallW = IsSolidWall(n_W, in blockTypes);
                // Diagonal corners only matter if not already covered by two adjacent cardinal walls
                bool diagNE = !wallN && !wallE && IsSolidWall(n_NE, in blockTypes);
                bool diagNW = !wallN && !wallW && IsSolidWall(n_NW, in blockTypes);
                bool diagSE = !wallS && !wallE && IsSolidWall(n_SE, in blockTypes);
                bool diagSW = !wallS && !wallW && IsSolidWall(n_SW, in blockTypes);

                float packedShoreMask = (
                    (wallN ? 1f : 0f) + (wallS ? 2f : 0f) + (wallE ? 4f : 0f) + (wallW ? 8f : 0f) +
                    (diagNE ? 16f : 0f) + (diagNW ? 32f : 0f) + (diagSE ? 64f : 0f) + (diagSW ? 128f : 0f)
                ) / 255f;

                Color c = new Color(liquidType, packedShoreMask, 1.0f, lightLevel);

                // Add vertices/normals/colors/uvs specifically matching winding order: BL, TL, BR, TR
                normals.Add(Vector3.up);
                colors.Add(c);
                uvs.Add(new Vector4(flow_bl.x, flow_bl.y, shore_push_bl.x, shore_push_bl.y));
                normals.Add(Vector3.up);
                colors.Add(c);
                uvs.Add(new Vector4(flow_tl.x, flow_tl.y, shore_push_tl.x, shore_push_tl.y));
                normals.Add(Vector3.up);
                colors.Add(c);
                uvs.Add(new Vector4(flow_br.x, flow_br.y, shore_push_br.x, shore_push_br.y));
                normals.Add(Vector3.up);
                colors.Add(c);
                uvs.Add(new Vector4(flow_tr.x, flow_tr.y, shore_push_tr.x, shore_push_tr.y));

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

                // Side faces carry no shore data — g=0 (no walls), zw = 0
                Color sideColor = new Color(liquidType, 0.0f, 1.0f, lightLevel);

                Vector4 uv1, uv2, uv3, uv4;

                if (fluidLevel >= 8) // Waterfall (Falling Fluid)
                {
                    // Force a strict downward flow at higher speed (V-axis)
                    uv1 = uv2 = uv3 = uv4 = new Vector4(0f, 1.5f, 0f, 0f);
                }
                else // Horizontal Spreading Fluid
                {
                    // 1. Get raw XZ flow at the corners
                    Vector2 f1 = GetCornerValue(in p1, flow_tl, flow_tr, flow_bl, flow_br);
                    Vector2 f2 = GetCornerValue(in p2, flow_tl, flow_tr, flow_bl, flow_br);
                    Vector2 f3 = GetCornerValue(in p3, flow_tl, flow_tr, flow_bl, flow_br);
                    Vector2 f4 = GetCornerValue(in p4, flow_tl, flow_tr, flow_bl, flow_br);

                    // 2. Project XZ flow onto the 2D plane of this specific side face
                    Vector2 p_uv1 = ProjectFlowToSideFace(f1, faceIndex);
                    Vector2 p_uv2 = ProjectFlowToSideFace(f2, faceIndex);
                    Vector2 p_uv3 = ProjectFlowToSideFace(f3, faceIndex);
                    Vector2 p_uv4 = ProjectFlowToSideFace(f4, faceIndex);

                    uv1 = new Vector4(p_uv1.x, p_uv1.y, 0f, 0f);
                    uv2 = new Vector4(p_uv2.x, p_uv2.y, 0f, 0f);
                    uv3 = new Vector4(p_uv3.x, p_uv3.y, 0f, 0f);
                    uv4 = new Vector4(p_uv4.x, p_uv4.y, 0f, 0f);
                }

                normals.Add(VoxelData.FaceChecks[faceIndex]);
                colors.Add(sideColor);
                uvs.Add(uv1);
                normals.Add(VoxelData.FaceChecks[faceIndex]);
                colors.Add(sideColor);
                uvs.Add(uv2);
                normals.Add(VoxelData.FaceChecks[faceIndex]);
                colors.Add(sideColor);
                uvs.Add(uv3);
                normals.Add(VoxelData.FaceChecks[faceIndex]);
                colors.Add(sideColor);
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
                // Bottom faces are internal. Hardcode shore mask (g) to 0.0f (no walls).
                // Use 1.0f for the b-channel so bottom faces default to full brightness (unshadowed) in game
                Color bottomColor = new Color(liquidType, 0.0f, 1.0f, lightLevel);

                // Add vertices/normals/colors/uvs specifically matching winding order: BL, TL, BR, TR
                normals.Add(Vector3.down);
                colors.Add(bottomColor);
                uvs.Add(new Vector4(flow_bl.x, flow_bl.y, 0f, 0f));
                normals.Add(Vector3.down);
                colors.Add(bottomColor);
                uvs.Add(new Vector4(flow_tl.x, flow_tl.y, 0f, 0f));
                normals.Add(Vector3.down);
                colors.Add(bottomColor);
                uvs.Add(new Vector4(flow_br.x, flow_br.y, 0f, 0f));
                normals.Add(Vector3.down);
                colors.Add(bottomColor);
                uvs.Add(new Vector4(flow_tr.x, flow_tr.y, 0f, 0f));

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
            bool w00 = IsSolidWall(b00, in blockTypes);
            bool w10 = IsSolidWall(b10, in blockTypes);
            bool w01 = IsSolidWall(b01, in blockTypes);
            bool w11 = IsSolidWall(b11, in blockTypes);

            // Accessibility guard: a non-wall, non-fluid block (e.g., air) is only included
            // if at least one of its two grid-adjacent neighbors is matching fluid. This prevents
            // isolated non-fluid blocks (diagonal air behind two walls) from creating artificial
            // pull gradients, while preserving the natural pull toward waterfall edges and drops
            // where the air IS accessible from the fluid surface.
            bool f00 = IsMatchingFluid(b00, fluidType, in blockTypes);
            bool f10 = IsMatchingFluid(b10, fluidType, in blockTypes);
            bool f01 = IsMatchingFluid(b01, fluidType, in blockTypes);
            bool f11 = IsMatchingFluid(b11, fluidType, in blockTypes);

            // b00 adjacent to b10, b01 — inaccessible if neither is fluid
            if (!w00 && !f00 && !f10 && !f01) w00 = true;
            // b10 adjacent to b00, b11 — inaccessible if neither is fluid
            if (!w10 && !f10 && !f00 && !f11) w10 = true;
            // b01 adjacent to b00, b11 — inaccessible if neither is fluid
            if (!w01 && !f01 && !f00 && !f11) w01 = true;
            // b11 adjacent to b10, b01 — inaccessible if neither is fluid
            if (!w11 && !f11 && !f10 && !f01) w11 = true;

            float h00 = w00 ? 0 : GetEffectiveFluidHeight(b00, fluidType, templates, blockTypes);
            float h10 = w10 ? 0 : GetEffectiveFluidHeight(b10, fluidType, templates, blockTypes);
            float h01 = w01 ? 0 : GetEffectiveFluidHeight(b01, fluidType, templates, blockTypes);
            float h11 = w11 ? 0 : GetEffectiveFluidHeight(b11, fluidType, templates, blockTypes);

            float dx = 0f;
            int dx_count = 0;
            // Only calculate the X derivative if the fluid actually exists across the boundary.
            // This prevents walls from creating artificial slopes that pull flow backward!
            if (!w01 && !w11)
            {
                dx += (h11 - h01);
                dx_count++;
            }

            if (!w00 && !w10)
            {
                dx += (h10 - h00);
                dx_count++;
            }

            if (dx_count > 0) dx /= dx_count;

            float dz = 0f;
            int dz_count = 0;
            // Only calculate the Z derivative if the fluid actually exists across the boundary.
            if (!w10 && !w11)
            {
                dz += (h11 - h10);
                dz_count++;
            }

            if (!w00 && !w01)
            {
                dz += (h01 - h00);
                dz_count++;
            }

            if (dz_count > 0) dz /= dz_count;

            Vector2 cornerFlow = new Vector2(dx, dz);
            float sqrMag = cornerFlow.sqrMagnitude;

            if (sqrMag < 0.0001f) return Vector2.zero;

            // Get the pure normalized direction
            float mag = math.sqrt(sqrMag);
            Vector2 dir = cornerFlow / mag;

            // Apply a smooth speed curve to the magnitude.
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
        /// Computes the shore push direction for a shared fluid mesh corner,
        /// using the identical 4-block neighborhood pattern as <see cref="CalculateSymmetricCornerFlow"/>.
        /// <para>
        /// Returns a normalized XZ direction pointing away from the wall(s), used for
        /// the shore push displacement effect. The shore gradient itself is computed
        /// per-pixel in the shader using the 8-neighbor wall mask (see <c>GetShoreData</c>).
        /// </para>
        /// <para>
        /// Because the neighborhood is defined by absolute world-space block positions, two adjacent
        /// fluid quads always compute identical push vectors at their shared corner vertex, ensuring
        /// seamless displacement across voxel boundaries.
        /// </para>
        /// </summary>
        /// <param name="b00">Block at position (-x, -z) relative to the corner.</param>
        /// <param name="b10">Block at position (+x, -z) relative to the corner.</param>
        /// <param name="b01">Block at position (-x, +z) relative to the corner.</param>
        /// <param name="b11">Block at position (+x, +z) relative to the corner.</param>
        /// <param name="blockTypes">Shared block type data array.</param>
        /// <param name="shorePush">
        /// Output: normalized XZ direction pointing away from the solid wall(s) at this corner,
        /// or <see cref="Vector2.zero"/> when no wall is present.
        /// </param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void CalculateSymmetricCornerShorePush(
            OptionalVoxelState b00, OptionalVoxelState b10,
            OptionalVoxelState b01, OptionalVoxelState b11,
            in NativeArray<BlockTypeJobData> blockTypes,
            out Vector2 shorePush)
        {
            bool s00 = IsSolidWall(b00, in blockTypes); // (-x, -z) = SW
            bool s10 = IsSolidWall(b10, in blockTypes); // (+x, -z) = SE
            bool s01 = IsSolidWall(b01, in blockTypes); // (-x, +z) = NW
            bool s11 = IsSolidWall(b11, in blockTypes); // (+x, +z) = NE

            // Accessibility guard: if a NON-FLUID, non-wall block is enclosed by walls on both
            // grid-adjacent edges, promote it to wall status. This prevents diagonal air (e.g., SW)
            // behind two walls (S + W) from breaking wall-pair detection for shore push.
            // IMPORTANT: fluid blocks must NEVER be promoted — they are valid fluid surfaces.
            if (!s00 && s10 && s01 && b00.HasValue && blockTypes[b00.State.id].FluidType == FluidType.None) s00 = true;
            if (!s10 && s00 && s11 && b10.HasValue && blockTypes[b10.State.id].FluidType == FluidType.None) s10 = true;
            if (!s01 && s00 && s11 && b01.HasValue && blockTypes[b01.State.id].FluidType == FluidType.None) s01 = true;
            if (!s11 && s10 && s01 && b11.HasValue && blockTypes[b11.State.id].FluidType == FluidType.None) s11 = true;

            float x_push = 0f;
            float z_push = 0f;

            // A solid wall pushes the visible flow pattern away from itself.
            // Because UV offsets shift the sampling window, shifting UVs West (-x) makes the texture appear to flow East (+x).

            // West wall (NW and SW are solid) -> sample West (-x)
            if (s00 && s01) x_push -= 1f;
            // East wall (NE and SE are solid) -> sample East (+x)
            if (s10 && s11) x_push += 1f;

            // South wall (SW and SE are solid) -> sample South (-z)
            if (s00 && s10) z_push -= 1f;
            // North wall (NW and NE are solid) -> sample North (+z)
            if (s01 && s11) z_push += 1f;

            // Outer corner fallback: if no flat wall is present but a diagonal block is solid,
            // push slightly away from that single isolated corner point.
            if (x_push == 0f && z_push == 0f)
            {
                if (s00)
                {
                    x_push -= 1f;
                    z_push -= 1f;
                }
                else if (s10)
                {
                    x_push += 1f;
                    z_push -= 1f;
                }
                else if (s01)
                {
                    x_push -= 1f;
                    z_push += 1f;
                }
                else if (s11)
                {
                    x_push += 1f;
                    z_push += 1f;
                }
            }

            // Normalize the push vector so it only encodes direction, not magnitude.
            float len = math.sqrt(x_push * x_push + z_push * z_push);
            shorePush = len > 0.001f ? new Vector2(x_push / len, z_push / len) : Vector2.zero;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsSolidWall(OptionalVoxelState state, in NativeArray<BlockTypeJobData> blockTypes)
        {
            return state.HasValue && blockTypes[state.State.id].IsSolid && blockTypes[state.State.id].FluidType == FluidType.None;
        }

        /// <summary>
        /// Returns true if the given voxel contains the same type of fluid as the center block.
        /// Used by <see cref="CalculateSymmetricCornerFlow"/> to restrict derivative computation
        /// to same-type fluid blocks, preventing air and walls from creating artificial gradients.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsMatchingFluid(OptionalVoxelState state, FluidType fluidType, in NativeArray<BlockTypeJobData> blockTypes)
        {
            return state.HasValue && blockTypes[state.State.id].FluidType == fluidType;
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
