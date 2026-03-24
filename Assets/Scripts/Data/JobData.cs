using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Helpers;
using Unity.Collections;
using UnityEngine;

namespace Data
{
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
    /// A job-safe representation of BiomeAttributes.
    /// </summary>
    public struct BiomeAttributesJobData
    {
        public readonly int Offset;
        public readonly float Scale;
        public readonly int TerrainHeight;
        public readonly float TerrainScale;
        public readonly byte SurfaceBlock;
        public readonly byte SubSurfaceBlock;
        public readonly bool PlaceMajorFlora;
        public readonly int MajorFloraIndex;
        public readonly float MajorFloraZoneScale;
        public readonly float MajorFloraZoneThreshold;
        public readonly float MajorFloraPlacementScale;
        public readonly float MajorFloraPlacementThreshold;
        public int MaxHeight;
        public int MinHeight;


        public readonly int LodeStartIndex;
        public readonly int LodeCount;

        /// <summary>
        /// Constructor that creates BiomeAttributesJobData from a BiomeAttributes class.
        /// </summary>
        /// <param name="biomeAttributes">The BiomeAttributes to copy properties from.</param>
        /// <param name="currentLodeIndex">The `LodeJobData` index of the first lode in the biome.</param>
        public BiomeAttributesJobData(BiomeAttributes biomeAttributes, int currentLodeIndex)
        {
            // Biome attributes
            Offset = biomeAttributes.offset;
            Scale = biomeAttributes.scale;
            TerrainHeight = biomeAttributes.terrainHeight;
            TerrainScale = biomeAttributes.terrainScale;
            SurfaceBlock = biomeAttributes.surfaceBlock;
            SubSurfaceBlock = biomeAttributes.subSurfaceBlock;
            PlaceMajorFlora = biomeAttributes.placeMajorFlora;
            MajorFloraIndex = biomeAttributes.majorFloraIndex;
            MajorFloraZoneScale = biomeAttributes.majorFloraZoneScale;
            MajorFloraZoneThreshold = biomeAttributes.majorFloraZoneThreshold;
            MajorFloraPlacementScale = biomeAttributes.majorFloraPlacementScale;
            MajorFloraPlacementThreshold = biomeAttributes.majorFloraPlacementThreshold;
            MaxHeight = biomeAttributes.maxHeight;
            MinHeight = biomeAttributes.minHeight;

            // Metadata
            LodeStartIndex = currentLodeIndex;
            LodeCount = biomeAttributes.lodes.Length;
        }
    }

    /// <summary>
    /// A job-safe representation of Lode.
    /// </summary>
    public struct LodeJobData
    {
        public readonly byte BlockID;
        public readonly int MinHeight;
        public readonly int MaxHeight;
        public readonly float Scale;
        public readonly float Threshold;
        public readonly float NoiseOffset;

        /// <summary>
        /// Constructor that creates LodeJobData from a Lode class.
        /// </summary>
        /// <param name="lode">The Lode to copy properties from.</param>
        public LodeJobData(Lode lode)
        {
            BlockID = lode.blockID;
            MinHeight = lode.minHeight;
            MaxHeight = lode.maxHeight;
            Scale = lode.scale;
            Threshold = lode.threshold;
            NoiseOffset = lode.noiseOffset;
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

        public readonly int CustomMeshIndex; // -1 if not a custom mesh

        // Fluid properties
        public readonly FluidType FluidType;
        public readonly byte FluidShaderID;
        public readonly byte FluidLevel;
        public byte FlowLevels;

        // Lighting properties
        public readonly byte Opacity;
        public readonly byte LightEmission;

        // Block behavior
        [MarshalAs(UnmanagedType.U1)]
        public bool IsActive;

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
            CustomMeshIndex = customMeshIdx;

            // Fluid properties
            FluidType = blockType.fluidType;
            FluidShaderID = blockType.fluidShaderID;
            FluidLevel = blockType.fluidLevel;
            FlowLevels = blockType.flowLevels;

            // Lighting properties
            Opacity = blockType.opacity;
            LightEmission = blockType.lightEmission;

            // Block behavior
            IsActive = blockType.isActive;

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
    /// </summary>
    public struct MeshDataJobOutput
    {
        // Using NativeLists because we don't know the size beforehand
        public NativeList<Vector3> Vertices;
        public NativeList<int> Triangles;
        public NativeList<int> TransparentTriangles;
        public NativeList<int> FluidTriangles;
        public NativeList<Vector4> Uvs; // xy = flow/atlas UV, zw = shore push (fluid top face) or (0,0)
        public NativeList<Color> Colors;
        public NativeList<Vector3> Normals;

        // Track stats per section (Index 0 = Section 0, Index 1 = Section 1, etc.)
        public NativeArray<MeshSectionStats> SectionStats;

        public MeshDataJobOutput(Allocator allocator)
        {
            Vertices = new NativeList<Vector3>(allocator);
            Triangles = new NativeList<int>(allocator);
            TransparentTriangles = new NativeList<int>(allocator);
            FluidTriangles = new NativeList<int>(allocator);
            Uvs = new NativeList<Vector4>(allocator);
            Colors = new NativeList<Color>(allocator);
            Normals = new NativeList<Vector3>(allocator);

            // 8 Sections per chunk (128 / 16).
            SectionStats = new NativeArray<MeshSectionStats>(VoxelData.ChunkHeight / ChunkMath.SECTION_SIZE, allocator);
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
}
