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
    
    /// A job-safe representation of BiomeAttributes
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

    /// A job-safe representation of Lode
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

    /// A job-safe representation of BlockType properties needed for meshing and lighting
    public struct BlockTypeJobData
    {
        // Block properties
        public readonly bool IsSolid;
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
        public bool IsOpaque => Opacity >= 15;

        /// <summary>
        /// Returns true if the block has an opacity, and thus has an effect on the light.
        /// </summary>
        public bool IsLightObstructing => Opacity > 0;

        /// <summary>
        /// Returns true if the block has zero opacity, allowing light to pass through without reduction.
        /// </summary>
        public bool IsFullyTransparentToLight => Opacity == 0;

        /// <summary>
        /// Returns true if the block emits its own light.
        /// </summary>
        public bool IsLightSource => LightEmission > 0;

        /// <summary>
        /// Returns true if the block is considered transparent for meshing purposes,
        /// meaning it does not cull the faces of adjacent solid blocks.
        /// </summary>
        public bool IsTransparentForMesh => !IsSolid || RenderNeighborFaces;

        // --- Texture Helper Properties ---

        /// <summary>
        /// Returns the most used side face texture ID of the block.
        ///
        /// It finds the most frequent texture among the 5 faces (left, right, back, front) without memory allocations.
        /// It omits topFaceTexture & bottomFaceTexture.
        /// </summary>
        public int SideFaceTexture
        {
            get
            {
                // An array is used for easier iteration.
                // This array exists only on the stack and will be optimized by the compiler.
                int[] textures = { LeftFaceTexture, RightFaceTexture, BackFaceTexture, FrontFaceTexture };

                // Default to the first texture in case all are unique.
                int mostFrequentTexture = textures[0];
                int maxCount = 0;

                // Iterate through each texture to see how many times it appears.
                foreach (int tOuter in textures)
                {
                    int currentCount = 0;
                    foreach (int tInner in textures)
                    {
                        if (tInner == tOuter)
                        {
                            currentCount++;
                        }
                    }

                    // If the texture we just counted is more frequent than our previous winner, update it.
                    if (currentCount > maxCount)
                    {
                        maxCount = currentCount;
                        mostFrequentTexture = tOuter;
                    }
                }

                return mostFrequentTexture;
            }
        }

        #endregion
    }

    /// A container for the mesh data generated by the job
    public struct MeshDataJobOutput
    {
        // Using NativeLists because we don't know the size beforehand
        public NativeList<Vector3> Vertices;
        public NativeList<int> Triangles;
        public NativeList<int> TransparentTriangles;
        public NativeList<int> FluidTriangles;
        public NativeList<Vector2> Uvs;
        public NativeList<Color> Colors;
        public NativeList<Vector3> Normals;

        public MeshDataJobOutput(Allocator allocator)
        {
            Vertices = new NativeList<Vector3>(allocator);
            Triangles = new NativeList<int>(allocator);
            TransparentTriangles = new NativeList<int>(allocator);
            FluidTriangles = new NativeList<int>(allocator);
            Uvs = new NativeList<Vector2>(allocator);
            Colors = new NativeList<Color>(allocator);
            Normals = new NativeList<Vector3>(allocator);
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
        }
    }
}