using System.Collections.Generic;
using System.Linq;
using Data;
using Helpers;
using Jobs.BurstData;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Editor.BlockEditor.Helpers
{
    public static class EditorMeshGenerator
    {
        /// <summary>
        /// Generates a Mesh for a given BlockType using its <see cref="BlockType.defaultMetadata"/>.
        /// This is for editor previews (Block Editor icons) where no per-voxel meta is available.
        /// </summary>
        public static Mesh GenerateBlockMesh(BlockType blockType, List<BlockType> allBlockTypes, int fluidLevel = 0)
            => GenerateBlockMeshInternal(blockType, allBlockTypes, blockType?.defaultMetadata ?? 0, fluidLevel);

        /// <summary>
        /// Generates a Mesh for a given BlockType with an explicit metadata byte.
        /// Used by the Structure Preview tool where each block carries its own authored meta.
        /// </summary>
        /// <param name="blockType">The block type to generate a mesh for.</param>
        /// <param name="allBlockTypes">All block types in the database (needed for fluid mock data).</param>
        /// <param name="meta">The raw metadata byte controlling orientation/axis/facing.</param>
        public static Mesh GenerateBlockMesh(BlockType blockType, List<BlockType> allBlockTypes, byte meta)
            => GenerateBlockMeshInternal(blockType, allBlockTypes, meta, fluidLevel: 0);

        /// <summary>
        /// Generates a Mesh for a given BlockType with both an explicit metadata byte and fluid level.
        /// Used by the Block Editor to preview block metadata.
        /// </summary>
        public static Mesh GenerateBlockMesh(BlockType blockType, List<BlockType> allBlockTypes, byte meta, int fluidLevel)
            => GenerateBlockMeshInternal(blockType, allBlockTypes, meta, fluidLevel);

        /// <summary>
        /// Shared implementation for block mesh generation. Dispatches on block type category
        /// (Fluid, Cross, Custom, Standard) and then on <see cref="MetadataSchema"/> for orientation.
        /// </summary>
        private static Mesh GenerateBlockMeshInternal(BlockType blockType, List<BlockType> allBlockTypes, byte meta, int fluidLevel)
        {
            if (blockType == null) return null;

            // Ensure BurstVoxelData is initialized in the Editor context before generating native meshes
            if (!BurstVoxelData.VoxelVerts.Data.IsCreated)
            {
                BurstVoxelData.Initialize();
            }

            Mesh mesh = new Mesh();
            List<Vector3> vertices = new List<Vector3>();
            List<int> opaqueTriangles = new List<int>();
            List<int> transparentTriangles = new List<int>();
            List<Vector4> uvs = new List<Vector4>();
            List<Color> colors = new List<Color>();
            List<Vector3> normals = new List<Vector3>();

            // Center point for rotating the mesh around its true center
            Vector3 centerOffset = new Vector3(-0.5f, -0.5f, -0.5f);

            // --- Unified path for all block types using VoxelMeshHelper ---
            NativeList<Vector3> nativeVertices = new NativeList<Vector3>(Allocator.Temp);
            NativeList<int> nativeOpaqueTris = new NativeList<int>(Allocator.Temp);
            NativeList<int> nativeTransparentTris = new NativeList<int>(Allocator.Temp);
            NativeList<int> nativeFluidTris = new NativeList<int>(Allocator.Temp);
            NativeList<Vector4> nativeUvs = new NativeList<Vector4>(Allocator.Temp);
            NativeList<Color> nativeColors = new NativeList<Color>(Allocator.Temp);
            NativeList<Vector3> nativeNormals = new NativeList<Vector3>(Allocator.Temp);
            NativeList<Color32> nativeLightData = new NativeList<Color32>(Allocator.Temp);
            int vertexIndex = 0;


            // Case 1: Fluid Block
            if (blockType.fluidType != FluidType.None)
            {
                // Create mock data needed by the helper that isn't available in the editor.
                BlockTypeJobData mockProps = new BlockTypeJobData(blockType);
                uint mockPackedData = BurstVoxelDataBitMapping.PackVoxelData(0,
                    BurstVoxelDataBitMapping.BuildMetaLegacy(orientation: 1, fluidLevel: (byte)fluidLevel, isFluid: false));

                // For a simple, flat preview, an empty (default) array is sufficient.
                NativeArray<OptionalVoxelState> mockNeighbors = new NativeArray<OptionalVoxelState>(14, Allocator.Temp);

                // Load fluid templates using the new helper - works perfectly in the editor.
                FluidTemplates fluidTemplates = ResourceLoader.LoadFluidTemplates();
                NativeArray<float> templates = new NativeArray<float>(16, Allocator.Temp);
                templates.CopyFrom(blockType.fluidType == FluidType.WaterLike ? fluidTemplates.WaterVertexTemplates : fluidTemplates.LavaVertexTemplates);

                // Create temporary BlockTypeJobData from the editor's list.
                NativeArray<BlockTypeJobData> blockTypesJobData = new NativeArray<BlockTypeJobData>(allBlockTypes.Select(bt => new BlockTypeJobData(bt)).ToArray(), Allocator.Temp);

                FluidCornerLights noCornerLights = default;
                NativeArray<ushort> mockNeighborLights = new NativeArray<ushort>(14, Allocator.Temp);
                ushort fullBright = LightBitMapping.PackLightData(15, 0, 0, 0);
                for (int i = 0; i < 14; i++) mockNeighborLights[i] = fullBright;

                VoxelMeshHelper.GenerateFluidMeshData(Vector3Int.zero, mockPackedData, mockProps, in templates, in blockTypesJobData, mockNeighbors,
                    in mockNeighborLights, false, in noCornerLights,
                    ref vertexIndex, ref nativeVertices, ref nativeFluidTris, ref nativeUvs, ref nativeColors, ref nativeNormals,
                    ref nativeLightData);

                // Dispose all temporary native arrays.
                mockNeighborLights.Dispose();
                mockNeighbors.Dispose();
                templates.Dispose();
                blockTypesJobData.Dispose();
            }
            // Case 2: Cross Mesh (flora)
            else if (blockType.renderShape == RenderShape.CrossMesh)
            {
                int textureID = blockType.backFaceTexture; // CrossMesh uses a single texture synced across all faces
                Color32 fullBright = new Color32(255, 255, 255, 255);
                CrossMeshCornerLights crossLights = new CrossMeshCornerLights
                {
                    TopL0 = fullBright, TopL1 = fullBright, TopL2 = fullBright, TopL3 = fullBright,
                    BotL0 = fullBright, BotL1 = fullBright, BotL2 = fullBright, BotL3 = fullBright,
                };
                VoxelMeshHelper.GenerateCrossMesh(textureID, in crossLights, Vector3Int.zero,
                    ref vertexIndex, ref nativeVertices, ref nativeTransparentTris, ref nativeUvs, ref nativeColors, ref nativeNormals,
                    ref nativeLightData);
            }
            // Case 3: Custom Mesh
            else if (blockType.renderShape == RenderShape.CustomMesh && blockType.meshData != null)
            {
                // Compute rotation matrix from the metadata byte (same logic as in-game)
                float3x3 matrix = BurstCustomMeshRotationUtility.GetRotationMatrix(
                    blockType.metadataSchema, meta, blockType.defaultMetadata);
                float3 center = new float3(0.5f, 0.5f, 0.5f);

                for (int p = 0; p < blockType.meshData.faces.Length; p++)
                {
                    if (p >= 6) continue; // Safety break for assets with >6 faces

                    FaceMeshData face = blockType.meshData.faces[p];
                    int vertCount = vertices.Count;

                    // --- Calculate the base UV from the atlas for this specific face ---
                    int textureID = blockType.GetTextureID(p);
                    float yUv = Mathf.FloorToInt((float)textureID / VoxelData.TextureAtlasSizeInBlocks);
                    float xUv = textureID - yUv * VoxelData.TextureAtlasSizeInBlocks;

                    xUv *= VoxelData.NormalizedBlockTextureSize;
                    yUv *= VoxelData.NormalizedBlockTextureSize;
                    yUv = 1f - yUv - VoxelData.NormalizedBlockTextureSize;
                    Vector2 baseUv = new Vector2(xUv, yUv);

                    // Rotate the face normal once (shared by all vertices on this face)
                    float3 rotatedNormal = math.normalize(math.mul(matrix, new float3(VoxelData.FaceChecks[p].x, VoxelData.FaceChecks[p].y, VoxelData.FaceChecks[p].z)));

                    foreach (VertData vertData in face.vertData)
                    {
                        // Apply 3D rotation around block center
                        float3 rotated = math.mul(matrix, (float3)vertData.position - center) + center;
                        vertices.Add(rotated);

                        uvs.Add(new Vector4(
                            baseUv.x + vertData.uv.x * VoxelData.NormalizedBlockTextureSize,
                            baseUv.y + vertData.uv.y * VoxelData.NormalizedBlockTextureSize,
                            0f, 0f
                        ));
                        colors.Add(Color.white);
                        normals.Add(rotatedNormal);
                    }

                    // Add triangles to the correct sub-mesh list
                    foreach (int tri in face.triangles)
                    {
                        if (blockType.renderNeighborFaces)
                            transparentTriangles.Add(vertCount + tri);
                        else
                            opaqueTriangles.Add(vertCount + tri);
                    }
                }
            }
            // Case 4: Standard Solid Block
            else
            {
                // Mirrors `MeshGenerationJob.GenerateVoxelMeshData` case 4 — schema-aware dispatch
                // so the Block Editor preview matches in-game rendering for Axis3 blocks.
                // See `PER_BLOCK_METADATA_SCHEMAS.md §8.1` ("update runtime meshing and Block Editor
                // preview meshing together").
                switch (blockType.metadataSchema)
                {
                    case MetadataSchema.Axis3:
                    {
                        // Decode the preview axis from the supplied meta byte. NormalizeMeta clamps
                        // invalid values to 0 (Y-axis) so out-of-range data renders an upright log
                        // instead of crashing on an LUT out-of-bounds read.
                        byte normalizedMeta = BurstVoxelMetadataUtility.NormalizeMeta(
                            MetadataSchema.Axis3, meta, defaultMeta: 0);
                        byte axis = BurstVoxelMetadataUtility.DecodeAxis3(normalizedMeta);

                        for (int p = 0; p < 6; p++)
                        {
                            // Texture comes from the axis-remapped block face; vertex emission uses
                            // `rotation: 0f` since the cube vertices are axis-symmetric.
                            int effectiveFace = BurstAxis3MeshUtility.GetEffectiveFace(axis, p);
                            int uvQuarterTurnsCW = BurstAxis3MeshUtility.GetUvQuarterTurnsCW(axis, p);
                            int textureID = blockType.GetTextureID(effectiveFace);
                            VoxelMeshHelper.GenerateStandardCubeFace(p, textureID, 1.0f, Vector3Int.zero, 0f, uvQuarterTurnsCW,
                                ref vertexIndex, ref nativeVertices, ref nativeOpaqueTris, ref nativeTransparentTris,
                                ref nativeUvs, ref nativeColors, ref nativeNormals,
                                ref nativeLightData, blockType.renderNeighborFaces);
                        }

                        break;
                    }

                    case MetadataSchema.Facing6:
                    {
                        byte normalizedMeta = BurstVoxelMetadataUtility.NormalizeMeta(
                            MetadataSchema.Facing6, meta, defaultMeta: 0);
                        byte facing = BurstVoxelMetadataUtility.DecodeFacing6(normalizedMeta);

                        for (int p = 0; p < 6; p++)
                        {
                            int effectiveFace = BurstFacing6MeshUtility.GetEffectiveFace(facing, p);
                            int uvQuarterTurnsCW = BurstFacing6MeshUtility.GetUvQuarterTurnsCW(facing, p);
                            int textureID = blockType.GetTextureID(effectiveFace);
                            VoxelMeshHelper.GenerateStandardCubeFace(p, textureID, 1.0f, Vector3Int.zero, 0f, uvQuarterTurnsCW,
                                ref vertexIndex, ref nativeVertices, ref nativeOpaqueTris, ref nativeTransparentTris,
                                ref nativeUvs, ref nativeColors, ref nativeNormals,
                                ref nativeLightData, blockType.renderNeighborFaces);
                        }

                        break;
                    }

                    case MetadataSchema.Facing6Roll2:
                    {
                        byte normalizedMeta = BurstVoxelMetadataUtility.NormalizeMeta(
                            MetadataSchema.Facing6Roll2, meta, defaultMeta: 0);
                        BurstVoxelMetadataUtility.DecodeFacing6Roll2(normalizedMeta, out byte facing, out byte roll);

                        for (int p = 0; p < 6; p++)
                        {
                            int effectiveFace = BurstFacing6Roll2MeshUtility.GetEffectiveFace(facing, roll, p);
                            int uvQuarterTurnsCW = BurstFacing6Roll2MeshUtility.GetUvQuarterTurnsCW(facing, roll, p);
                            int textureID = blockType.GetTextureID(effectiveFace);
                            VoxelMeshHelper.GenerateStandardCubeFace(p, textureID, 1.0f, Vector3Int.zero, 0f, uvQuarterTurnsCW,
                                ref vertexIndex, ref nativeVertices, ref nativeOpaqueTris, ref nativeTransparentTris,
                                ref nativeUvs, ref nativeColors, ref nativeNormals,
                                ref nativeLightData, blockType.renderNeighborFaces);
                        }

                        break;
                    }

                    case MetadataSchema.HorizontalOnly:
                    {
                        byte normalizedDefaultMeta = BurstVoxelMetadataUtility.NormalizeMeta(
                            MetadataSchema.HorizontalOnly, blockType.defaultMetadata, 0); // Default to North (0)
                        byte normalizedMeta = BurstVoxelMetadataUtility.NormalizeMeta(
                            MetadataSchema.HorizontalOnly, meta, normalizedDefaultMeta);

                        byte yaw = BurstVoxelMetadataUtility.DecodeHorizontalOnly(normalizedMeta);

                        byte legacyOrientation = yaw switch
                        {
                            0 => 1, // North
                            1 => 0, // South
                            2 => 4, // West
                            3 => 5, // East
                            _ => 1,
                        };

                        float rotation = VoxelHelper.GetRotationAngle(legacyOrientation);

                        for (int p = 0; p < 6; p++)
                        {
                            int translatedP = VoxelHelper.GetTranslatedFaceIndex(p, legacyOrientation);
                            int textureID = blockType.GetTextureID(translatedP);
                            VoxelMeshHelper.GenerateStandardCubeFace(translatedP, textureID, 1.0f, Vector3Int.zero, rotation,
                                ref vertexIndex, ref nativeVertices, ref nativeOpaqueTris, ref nativeTransparentTris,
                                ref nativeUvs, ref nativeColors, ref nativeNormals,
                                ref nativeLightData, blockType.renderNeighborFaces);
                        }

                        break;
                    }

                    case MetadataSchema.None:
                    default:
                    {
                        // Legacy / None preview: identity face mapping, no rotation.
                        for (int p = 0; p < 6; p++)
                        {
                            int textureID = blockType.GetTextureID(p);
                            VoxelMeshHelper.GenerateStandardCubeFace(p, textureID, 1.0f, Vector3Int.zero, 0f,
                                ref vertexIndex, ref nativeVertices, ref nativeOpaqueTris, ref nativeTransparentTris,
                                ref nativeUvs, ref nativeColors, ref nativeNormals,
                                ref nativeLightData, blockType.renderNeighborFaces);
                        }

                        break;
                    }
                }
            }

            // --- Post-processing for all native-generated meshes ---
            // If any native lists were populated, convert them to managed lists.
            if (nativeVertices.Length > 0)
            {
                vertices.AddRange(nativeVertices.AsArray());
                opaqueTriangles.AddRange(nativeOpaqueTris.AsArray());
                transparentTriangles.AddRange(nativeTransparentTris.AsArray());
                // Add fluid triangles to the transparent sub-mesh for rendering
                transparentTriangles.AddRange(nativeFluidTris.AsArray());
                uvs.AddRange(nativeUvs.AsArray());
                colors.AddRange(nativeColors.AsArray());
                normals.AddRange(nativeNormals.AsArray());
            }

            // Dispose all native collections
            nativeVertices.Dispose();
            nativeOpaqueTris.Dispose();
            nativeTransparentTris.Dispose();
            nativeFluidTris.Dispose();
            nativeUvs.Dispose();
            nativeColors.Dispose();
            nativeNormals.Dispose();
            nativeLightData.Dispose();

            // --- Final Mesh Assembly ---
            // Apply center offset to ALL vertices before creating the mesh
            for (int i = 0; i < vertices.Count; i++)
            {
                vertices[i] += centerOffset;
            }

            mesh.vertices = vertices.ToArray();
            mesh.SetUVs(0, uvs);
            mesh.colors = colors.ToArray();
            mesh.normals = normals.ToArray();

            // Populate TexCoord1 with full brightness so BlockPreviewShader and
            // FluidPreviewShader read valid light data without needing a toggle.
            Vector4 fullBrightLight = new Vector4(1f, 1f, 1f, 1f);
            Vector4[] lightUvs = new Vector4[vertices.Count];
            for (int i = 0; i < lightUvs.Length; i++) lightUvs[i] = fullBrightLight;
            mesh.SetUVs(1, lightUvs);

            mesh.subMeshCount = 2;
            mesh.SetTriangles(opaqueTriangles.ToArray(), 0);
            mesh.SetTriangles(transparentTriangles.ToArray(), 1);

            mesh.RecalculateBounds();

            return mesh;
        }
    }
}
