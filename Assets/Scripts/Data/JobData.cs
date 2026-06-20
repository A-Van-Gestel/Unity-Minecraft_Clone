using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Helpers;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Data
{
    /// <summary>
    /// MR-2: a unit normal packed into 4 signed bytes (GPU <see cref="UnityEngine.Rendering.VertexAttributeFormat.SNorm8"/>×4).
    /// Each component stores <c>round(clamp(n, -1, 1) * 127)</c>; the GPU unpacks it back to [-1, 1].
    /// Replaces the former <c>Float32×3</c> (12-byte) normal — voxel normals are axis-aligned or
    /// 45° cross/custom-mesh directions, all well within 8-bit signed precision.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PackedNormal
    {
        public sbyte X, Y, Z, W;

        /// <summary>
        /// Packs a (typically unit) normal into <see cref="PackedNormal"/> (SNorm8×4). Burst-safe.
        /// </summary>
        /// <param name="n">The normal to pack; clamped to [-1, 1] per component before quantization.</param>
        /// <returns>The packed normal with <c>W = 0</c>.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static PackedNormal FromFloat3(float3 n)
        {
            float3 c = math.clamp(n, -1f, 1f) * 127f;
            return new PackedNormal
            {
                X = (sbyte)math.round(c.x),
                Y = (sbyte)math.round(c.y),
                Z = (sbyte)math.round(c.z),
                W = 0,
            };
        }

        /// <summary>
        /// Unpacks this <see cref="PackedNormal"/> back to a <see cref="float3"/> in [-1, 1] — the exact
        /// inverse of <see cref="FromFloat3"/>. Co-located with the pack so the 1/127 scale can never drift.
        /// </summary>
        /// <returns>The unpacked normal (the result of quantization, not necessarily unit length).</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly float3 ToFloat3() => new float3(X, Y, Z) / 127f;
    }

    /// <summary>
    /// Interleaved struct for mesh stream 3: Normal (4 bytes, SNorm8×4) + LightData (4 bytes, UNorm8×4) = 8 bytes.
    /// Packs both attributes into a single stream to stay within Unity's 4-stream limit.
    /// Built by <see cref="Jobs.MeshPostProcessJob"/> (Burst-compiled) to avoid main-thread interleaving.
    /// MR-2 repacked the normal from <c>Float32×3</c> (12 B) to <see cref="PackedNormal"/> (4 B).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NormalLightVertex
    {
        public PackedNormal Normal;
        public Color32 LightData;
    }

    /// <summary>
    /// Precomputed corner-averaged light values for all 6 faces of a fluid block.
    /// Built by <see cref="MeshGenerationJob"/> via <c>CalculateCornerLights</c> and passed
    /// into <see cref="VoxelMeshHelper.GenerateFluidMeshData"/> for smooth lighting.
    /// 6 faces × 4 corners × 4 bytes = 96 bytes (stack-friendly, Burst-safe).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FluidCornerLights
    {
        // Face index order: 0=Back, 1=Front, 2=Top, 3=Bottom, 4=Left, 5=Right.
        // Within each face, (L0, L1, L2, L3) match CalculateCornerLights output order.
        public Color32 BackL0, BackL1, BackL2, BackL3;
        public Color32 FrontL0, FrontL1, FrontL2, FrontL3;
        public Color32 TopL0, TopL1, TopL2, TopL3;
        public Color32 BottomL0, BottomL1, BottomL2, BottomL3;
        public Color32 LeftL0, LeftL1, LeftL2, LeftL3;
        public Color32 RightL0, RightL1, RightL2, RightL3;

        /// <summary>
        /// Returns the 4 corner lights for the given face index (0-5).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly void GetFace(int faceIndex,
            out Color32 l0, out Color32 l1, out Color32 l2, out Color32 l3)
        {
            switch (faceIndex)
            {
                case 0: // -Z
                    l0 = BackL0;
                    l1 = BackL1;
                    l2 = BackL2;
                    l3 = BackL3;
                    return;
                case 1: // +Z
                    l0 = FrontL0;
                    l1 = FrontL1;
                    l2 = FrontL2;
                    l3 = FrontL3;
                    return;
                case 2: // +Y
                    l0 = TopL0;
                    l1 = TopL1;
                    l2 = TopL2;
                    l3 = TopL3;
                    return;
                case 3: // -Y
                    l0 = BottomL0;
                    l1 = BottomL1;
                    l2 = BottomL2;
                    l3 = BottomL3;
                    return;
                case 4: // -X
                    l0 = LeftL0;
                    l1 = LeftL1;
                    l2 = LeftL2;
                    l3 = LeftL3;
                    return;
                default: // +X
                    l0 = RightL0;
                    l1 = RightL1;
                    l2 = RightL2;
                    l3 = RightL3;
                    return;
            }
        }

        /// <summary>
        /// Stores the 4 corner lights for the given face index (0-5).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetFace(int faceIndex, Color32 l0, Color32 l1, Color32 l2, Color32 l3)
        {
            switch (faceIndex)
            {
                case 0: // -Z
                    BackL0 = l0;
                    BackL1 = l1;
                    BackL2 = l2;
                    BackL3 = l3;
                    return;
                case 1: // +Z
                    FrontL0 = l0;
                    FrontL1 = l1;
                    FrontL2 = l2;
                    FrontL3 = l3;
                    return;
                case 2: // +Y
                    TopL0 = l0;
                    TopL1 = l1;
                    TopL2 = l2;
                    TopL3 = l3;
                    return;
                case 3: // -Y
                    BottomL0 = l0;
                    BottomL1 = l1;
                    BottomL2 = l2;
                    BottomL3 = l3;
                    return;
                case 4: // -X
                    LeftL0 = l0;
                    LeftL1 = l1;
                    LeftL2 = l2;
                    LeftL3 = l3;
                    return;
                default: // +X
                    RightL0 = l0;
                    RightL1 = l1;
                    RightL2 = l2;
                    RightL3 = l3;
                    return;
            }
        }
    }

    /// <summary>
    /// Precomputed corner-averaged light values for a cross mesh block at two Y levels.
    /// Top corners sample <c>CalculateCornerLights(Top, pos)</c> (light above the flora).
    /// Bottom corners sample <c>CalculateCornerLights(Top, pos + down)</c> (light at ground level)
    /// at <see cref="SmoothLightingQuality.High"/>; at <see cref="SmoothLightingQuality.Standard"/>
    /// they are copies of the top corners (no vertical gradient). At <see cref="SmoothLightingQuality.Off"/>
    /// all 8 fields carry the same flat light value.
    /// 2 levels × 4 corners × 4 bytes = 32 bytes (stack-friendly, Burst-safe).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CrossMeshCornerLights
    {
        // Top-level corners: sampled at pos (flora's head height).
        // Corner layout matches CalculateCornerLights output: L0=(0,0), L1=(0,1), L2=(1,0), L3=(1,1).
        public Color32 TopL0, TopL1, TopL2, TopL3;

        // Bottom-level corners: sampled at pos+down (ground level) at High quality; copies of top at Standard.
        public Color32 BotL0, BotL1, BotL2, BotL3;
    }

    /// <summary>
    /// A job-safe representation of a nullable VoxelState.
    /// </summary>
    public struct OptionalVoxelState
    {
        public VoxelState State;
        public readonly bool HasValue;

        public OptionalVoxelState(VoxelState state)
        {
            State = state;
            HasValue = true;
        }
    }

    /// <summary>
    /// A job-safe representation of a custom mesh vertex.
    /// </summary>
    public struct CustomVertData
    {
        public Vector3 Position;
        public Vector2 UV;
    }

    /// <summary>
    /// A job-safe representation of a custom mesh face.
    /// </summary>
    public struct CustomFaceData
    {
        public int VertStartIndex;
        public int VertCount;
        public int TriStartIndex;
        public int TriCount;
    }

    /// <summary>
    /// A job-safe representation of a custom mesh.
    /// </summary>
    public struct CustomMeshData
    {
        public int FaceStartIndex;
        public int FaceCount;
    }

    /// <summary>
    /// A job-safe representation of BlockType properties needed for meshing and lighting.
    /// </summary>
    public struct BlockTypeJobData
    {
        // Block properties
        [MarshalAs(UnmanagedType.U1)]
        public readonly bool IsSolid;

        [MarshalAs(UnmanagedType.U1)]
        public readonly bool RenderNeighborFaces;

        public readonly RenderShape RenderShape;
        public readonly int CustomMeshIndex; // -1 if not a custom mesh

        // Fluid properties
        public readonly FluidType FluidType;
        public readonly byte FluidShaderID;
        public readonly byte FluidLevel;
        public byte FlowLevels;

        // Lighting properties
        public readonly byte Opacity;
        public readonly byte LightEmission;
        public readonly byte EmissionR;
        public readonly byte EmissionG;
        public readonly byte EmissionB;

        // Block behavior
        [MarshalAs(UnmanagedType.U1)]
        public bool IsActive;

        // Metadata schema
        public readonly MetadataSchema MetadataSchema;
        public readonly PlacementMetadataMode PlacementMetadataMode;
        public readonly byte DefaultMetadata;

        // Texture ID's
        public readonly int BackFaceTexture;
        public readonly int FrontFaceTexture;
        public readonly int TopFaceTexture;
        public readonly int BottomFaceTexture;
        public readonly int LeftFaceTexture;
        public readonly int RightFaceTexture;

        #region Constructors

        /// <summary>
        /// Constructor that creates BlockTypeJobData from a BlockType class.
        /// </summary>
        /// <param name="blockType">The BlockType to copy properties from.</param>
        /// <param name="customMeshIdx">The index of the custom mesh in the flattened data arrays. -1 if none.</param>
        public BlockTypeJobData(BlockType blockType, int customMeshIdx = -1)
        {
            // Block properties
            IsSolid = blockType.isSolid;
            RenderNeighborFaces = blockType.renderNeighborFaces;
            RenderShape = blockType.renderShape;
            CustomMeshIndex = customMeshIdx;

            // Fluid properties
            FluidType = blockType.fluidType;
            FluidShaderID = blockType.fluidShaderID;
            FluidLevel = blockType.fluidLevel;
            FlowLevels = blockType.flowLevels;

            // Lighting properties
            Opacity = blockType.opacity;
            Color emColor = blockType.lightEmissionColor;
            float maxComponent = Mathf.Max(emColor.r, Mathf.Max(emColor.g, emColor.b));
            float scale = maxComponent > 0 ? blockType.lightEmission / maxComponent : 0;
            EmissionR = (byte)Mathf.Clamp(Mathf.RoundToInt(emColor.r * scale), 0, 15);
            EmissionG = (byte)Mathf.Clamp(Mathf.RoundToInt(emColor.g * scale), 0, 15);
            EmissionB = (byte)Mathf.Clamp(Mathf.RoundToInt(emColor.b * scale), 0, 15);
            LightEmission = (byte)Mathf.Max(EmissionR, Mathf.Max(EmissionG, EmissionB));

            // Block behavior
            IsActive = blockType.isActive;

            // Metadata schema
            MetadataSchema = blockType.metadataSchema;
            PlacementMetadataMode = blockType.placementMetadataMode;
            DefaultMetadata = blockType.defaultMetadata;

            // Texture ID's
            BackFaceTexture = blockType.backFaceTexture;
            FrontFaceTexture = blockType.frontFaceTexture;
            TopFaceTexture = blockType.topFaceTexture;
            BottomFaceTexture = blockType.bottomFaceTexture;
            LeftFaceTexture = blockType.leftFaceTexture;
            RightFaceTexture = blockType.rightFaceTexture;
        }

        #endregion

        #region Helper Properties

        // --- HELPER PROPERTIES ---

        /// <summary>
        /// Returns true if the block has maximum opacity, effectively blocking all light.
        /// </summary>
        public bool IsOpaque
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Opacity >= 15;
        }

        /// <summary>
        /// Returns true if the block has an opacity, and thus has an effect on the light.
        /// </summary>
        public bool IsLightObstructing
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Opacity > 0;
        }

        /// <summary>
        /// Returns true if the block has zero opacity, allowing light to pass through without reduction.
        /// </summary>
        public bool IsFullyTransparentToLight
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Opacity == 0;
        }

        /// <summary>
        /// Returns true if the block emits its own light.
        /// </summary>
        public bool IsLightSource
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => LightEmission > 0;
        }

        /// <summary>
        /// Returns true if the block is considered transparent for meshing purposes,
        /// meaning it does not cull the faces of adjacent solid blocks.
        /// </summary>
        public bool IsTransparentForMesh
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => !IsSolid || RenderNeighborFaces;
        }

        // --- Texture Helper Properties ---

        /// <summary>
        /// Returns the most used side face texture ID of the block.
        ///
        /// It finds the most frequent texture among the 5 faces (left, right, back, front) without memory allocations.
        /// It omits topFaceTexture & bottomFaceTexture.
        /// </summary>
        /// <returns>The integer ID of the most common side texture.</returns>
        /// <remarks>
        /// <para><b>Tie-Breaking Logic:</b></para>
        /// In a situation where frequencies are tied (e.g., two of texture A and two of texture B),
        /// a texture that appears earlier in the sequence (Left, then Right, then Back) is given priority.
        ///
        /// <para><b>Default Behavior:</b></para>
        /// If all four side textures are unique, <c>LeftFaceTexture</c> is returned as a deterministic default.
        /// </remarks>
        public int SideFaceTexture
        {
            get
            {
                // Assign face textures to local variables. This can improve readability
                // and makes the logic below cleaner.
                int left = LeftFaceTexture;
                int right = RightFaceTexture;
                int back = BackFaceTexture;
                int front = FrontFaceTexture;

                // --- Early Exit Checks ---
                // The structure of these checks establishes a clear priority.
                // We check for duplicates of 'left' first. If found, it's the winner.
                if (left == right || left == back || left == front)
                {
                    return left;
                }

                // If 'left' was unique, we proceed to check for duplicates of 'right'.
                if (right == back || right == front)
                {
                    return right;
                }

                // Finally, check the last remaining pair for a match.
                if (back == front)
                {
                    // It doesn't matter if we return 'back' or 'front' since they are equal.
                    return back;
                }

                // --- Fallback Case ---
                // If we reach this point, it means no duplicates were found.
                // We return the first texture as a consistent, predictable fallback.
                return left;
            }
        }

        #endregion
    }

    /// <summary>
    /// Tracks the start indices and lengths for a specific section within the unified mesh buffers.
    /// </summary>
    public struct MeshSectionStats
    {
        public int VertexStartIndex;
        public int VertexCount;

        public int OpaqueTriStartIndex;
        public int OpaqueTriCount;

        public int TransparentTriStartIndex;
        public int TransparentTriCount;

        public int FluidTriStartIndex;
        public int FluidTriCount;
    }

    /// <summary>
    /// A container for the mesh data generated by the job.
    /// <para>MR-6: the per-vertex / per-triangle <see cref="NativeList{T}"/>s are pre-sized at
    /// construction (see <see cref="DefaultVertexCapacity"/>) so a typical surface chunk's meshing job
    /// no longer pays an in-job grow → reallocate → memcpy chain. <see cref="NativeList{T}"/> retains
    /// capacity across <c>Clear()</c>, so pooling whole instances (<see cref="Helpers.MeshOutputPool"/>)
    /// fully amortizes growth beyond this hint — the pool buffer "learns" the dense-chunk peak and never
    /// regrows after warm-up. Over-sizing only costs memory, so the hint targets the median, not the
    /// worst case (dense Checkerboard chunks still grow once, then the pool retains that capacity).</para>
    /// </summary>
    public struct MeshDataJobOutput
    {
        /// <summary>
        /// Initial vertex capacity hint for a typical surface chunk (all sections combined). Derived
        /// from the meshing benchmark's "Mesh output sizes" table; the dense Checkerboard worst case
        /// (~278k verts) is intentionally NOT targeted — pooling amortizes that after the first grow.
        /// </summary>
        public const int DefaultVertexCapacity = 24576;

        /// <summary>Initial opaque triangle-index capacity (≈ vertices × 1.5: a quad is 4 verts → 6 indices).</summary>
        public const int DefaultOpaqueTriangleCapacity = DefaultVertexCapacity * 3 / 2;

        /// <summary>
        /// Initial capacity for the transparent and fluid triangle lists. Kept small: most chunks emit
        /// little or no transparent/fluid geometry, so a large pre-size would waste pooled memory; the
        /// lists grow on demand and the pool retains the grown capacity.
        /// </summary>
        public const int DefaultSecondaryTriangleCapacity = 4096;

        /// <summary>
        /// True when this instance was rented from <see cref="Helpers.MeshOutputPool"/> and must be
        /// returned to it (its buffers <c>Clear()</c>ed, capacity retained) instead of disposed. False
        /// for the per-job allocations used by the editor / preview / benchmark paths, which dispose via
        /// <see cref="Dispose"/>. Not read by any Burst job.
        /// </summary>
        [MarshalAs(UnmanagedType.U1)]
        public bool FromPool;

        // Using NativeLists because we don't know the size beforehand
        public NativeList<Vector3> Vertices;
        public NativeList<int> Triangles;
        public NativeList<int> TransparentTriangles;
        public NativeList<int> FluidTriangles;
        public NativeList<half4> Uvs; // MR-2: Float16×4. xy = flow/atlas UV, zw = shore push (fluid top face) or (0,0)
        public NativeList<Color32> Colors; // MR-2: UNorm8×4. White for blocks; fluid encodes (FluidShaderID, shoreMask, shadowMul, 0)
        public NativeList<Vector3> Normals; // Full-precision working buffer; packed to SNorm8×4 in MeshPostProcessJob
        public NativeList<Color32> LightData; // TexCoord1 UNorm8: (sunlight, reserved, reserved, blocklight)
        public NativeList<NormalLightVertex> InterleavedStream3; // Packed Normal + LightData interleaved for GPU upload

        // Track stats per section (Index 0 = Section 0, Index 1 = Section 1, etc.)
        public NativeArray<MeshSectionStats> SectionStats;

        public MeshDataJobOutput(Allocator allocator)
        {
            FromPool = false;

            // MR-6: pre-size every per-vertex / per-triangle list so the meshing job appends without
            // reallocating for a typical chunk. The per-vertex streams (UVs, colors, normals, light,
            // interleaved) share the vertex capacity since they emit one element per vertex.
            Vertices = new NativeList<Vector3>(DefaultVertexCapacity, allocator);
            Triangles = new NativeList<int>(DefaultOpaqueTriangleCapacity, allocator);
            TransparentTriangles = new NativeList<int>(DefaultSecondaryTriangleCapacity, allocator);
            FluidTriangles = new NativeList<int>(DefaultSecondaryTriangleCapacity, allocator);
            Uvs = new NativeList<half4>(DefaultVertexCapacity, allocator);
            Colors = new NativeList<Color32>(DefaultVertexCapacity, allocator);
            Normals = new NativeList<Vector3>(DefaultVertexCapacity, allocator);
            LightData = new NativeList<Color32>(DefaultVertexCapacity, allocator);
            InterleavedStream3 = new NativeList<NormalLightVertex>(DefaultVertexCapacity, allocator);

            // 8 Sections per chunk (128 / 16).
            SectionStats = new NativeArray<MeshSectionStats>(VoxelData.ChunkHeight / ChunkMath.SECTION_SIZE, allocator);
        }

        /// <summary>
        /// MR-6: resets every per-vertex / per-triangle list to length 0 while retaining its allocated
        /// capacity, so a pooled instance can be reused by the next meshing job without re-growing.
        /// <para><b>Correctness:</b> <see cref="Jobs.MeshGenerationJob"/> <i>appends</i> to these lists
        /// (it reads <c>Length</c> as each section's start offset) and never clears them itself, so a
        /// reused buffer that was not cleared would leak the previous chunk's vertices into the next
        /// mesh. <see cref="SectionStats"/> is deliberately NOT cleared — the job overwrites every
        /// section index every run (skipped sections written as <c>default</c>). Guarded by baseline
        /// B17 (MH-2 pooled-output stale-data guard).</para>
        /// </summary>
        public void ClearForReuse()
        {
            Vertices.Clear();
            Triangles.Clear();
            TransparentTriangles.Clear();
            FluidTriangles.Clear();
            Uvs.Clear();
            Colors.Clear();
            Normals.Clear();
            LightData.Clear();
            InterleavedStream3.Clear();
        }

        public void Dispose()
        {
            Vertices.Dispose();
            Triangles.Dispose();
            TransparentTriangles.Dispose();
            FluidTriangles.Dispose();
            Uvs.Dispose();
            Colors.Dispose();
            Normals.Dispose();
            LightData.Dispose();
            InterleavedStream3.Dispose();
            if (SectionStats.IsCreated) SectionStats.Dispose();
        }
    }

    /// <summary>
    /// A container for the section data generated by the job
    /// </summary>
    public struct SectionJobData
    {
        public bool IsEmpty;
        public bool IsFullySolid;
    }

    /// <summary>
    /// Burst-safe configuration flags for <see cref="Jobs.StandardChunkGenerationJob"/>.
    /// Controls which optional generation passes (caves, lodes, water) are executed.
    /// Use <see cref="Default"/> for full generation with all passes enabled.
    /// </summary>
    public struct GenerationFeatureFlags
    {
        /// <summary>When false, cave carving (Cheese, Spaghetti, Noodle, WormCarver) is skipped.</summary>
        [MarshalAs(UnmanagedType.U1)]
        public bool EnableCaves;

        /// <summary>When false, lode/ore vein replacement in stone is skipped.</summary>
        [MarshalAs(UnmanagedType.U1)]
        public bool EnableLodes;

        /// <summary>When false, water fill below sea level is replaced with air.</summary>
        [MarshalAs(UnmanagedType.U1)]
        public bool EnableWater;

        /// <summary>When false, major flora structure markers (trees, cacti, boulders) are not emitted.</summary>
        [MarshalAs(UnmanagedType.U1)]
        public bool EnableMajorFlora;

        /// <summary>When false, minor flora structure markers (grass, flowers, decorations) are not emitted.</summary>
        [MarshalAs(UnmanagedType.U1)]
        public bool EnableMinorFlora;

        /// <summary>When false, WormCarver cave layers (both trunk and local) are skipped. Only effective when <see cref="EnableCaves"/> is true.</summary>
        [MarshalAs(UnmanagedType.U1)]
        public bool EnableWormCarver;

        /// <summary>When false, Cheese (blob) cave layers are skipped. Only effective when <see cref="EnableCaves"/> is true.</summary>
        [MarshalAs(UnmanagedType.U1)]
        public bool EnableCheese;

        /// <summary>When false, Noodle (isoband) cave layers are skipped. Only effective when <see cref="EnableCaves"/> is true.</summary>
        [MarshalAs(UnmanagedType.U1)]
        public bool EnableNoodle;

        /// <summary>When false, Spaghetti2D and Spaghetti3D cave layers are skipped. Only effective when <see cref="EnableCaves"/> is true.</summary>
        [MarshalAs(UnmanagedType.U1)]
        public bool EnableSpaghetti;

        /// <summary>When false, per-biome local worm carver layers are skipped (trunk worms are controlled separately via TrunkWormConfig). Only effective when <see cref="EnableCaves"/> and <see cref="EnableWormCarver"/> are true.</summary>
        [MarshalAs(UnmanagedType.U1)]
        public bool EnableLocalWormCarver;

        /// <summary>
        /// Returns feature flags with all passes enabled.
        /// </summary>
        public static GenerationFeatureFlags Default => new GenerationFeatureFlags
        {
            EnableCaves = true,
            EnableLodes = true,
            EnableWater = true,
            EnableMajorFlora = true,
            EnableMinorFlora = true,
            EnableWormCarver = true,
            EnableCheese = true,
            EnableNoodle = true,
            EnableSpaghetti = true,
            EnableLocalWormCarver = true,
        };
    }

    /// <summary>
    /// Axis-aligned clip bounds for <see cref="Jobs.MeshGenerationJob"/>. Voxels at coordinates
    /// greater than or equal to each Max value are treated as air during face culling.
    /// Use <see cref="Disabled"/> for no clipping (all axes set to <see cref="int.MaxValue"/>).
    /// </summary>
    public struct MeshClipBounds
    {
        /// <summary>Global X coordinate at or above which voxels are treated as air.</summary>
        public int MaxX;

        /// <summary>World Y coordinate at or above which voxels are treated as air.</summary>
        public int MaxY;

        /// <summary>Global Z coordinate at or above which voxels are treated as air.</summary>
        public int MaxZ;

        /// <summary>
        /// Returns clip bounds with all axes disabled (set to <see cref="int.MaxValue"/>).
        /// </summary>
        public static MeshClipBounds Disabled => new MeshClipBounds
        {
            MaxX = int.MaxValue,
            MaxY = int.MaxValue,
            MaxZ = int.MaxValue,
        };
    }
}
