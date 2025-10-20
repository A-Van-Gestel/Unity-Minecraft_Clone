using Unity.Collections;
using UnityEngine;

namespace Data
{
    /// A job-safe representation of BiomeAttributes
    public struct BiomeAttributesJobData
    {
        public int offset;
        public float scale;
        public int terrainHeight;
        public float terrainScale;
        public byte surfaceBlock;
        public byte subSurfaceBlock;
        public bool placeMajorFlora;
        public int majorFloraIndex;
        public float majorFloraZoneScale;
        public float majorFloraZoneThreshold;
        public float majorFloraPlacementScale;
        public float majorFloraPlacementThreshold;
        public int maxHeight;
        public int minHeight;


        public int lodeStartIndex;
        public int lodeCount;

        /// <summary>
        /// Constructor that creates BiomeAttributesJobData from a BiomeAttributes class.
        /// </summary>
        /// <param name="biomeAttributes">The BiomeAttributes to copy properties from.</param>
        /// <param name="currentLodeIndex">The `LodeJobData` index of the first lode in the biome.</param>
        public BiomeAttributesJobData(BiomeAttributes biomeAttributes, int currentLodeIndex)
        {
            // Biome attributes
            offset = biomeAttributes.offset;
            scale = biomeAttributes.scale;
            terrainHeight = biomeAttributes.terrainHeight;
            terrainScale = biomeAttributes.terrainScale;
            surfaceBlock = biomeAttributes.surfaceBlock;
            subSurfaceBlock = biomeAttributes.subSurfaceBlock;
            placeMajorFlora = biomeAttributes.placeMajorFlora;
            majorFloraIndex = biomeAttributes.majorFloraIndex;
            majorFloraZoneScale = biomeAttributes.majorFloraZoneScale;
            majorFloraZoneThreshold = biomeAttributes.majorFloraZoneThreshold;
            majorFloraPlacementScale = biomeAttributes.majorFloraPlacementScale;
            majorFloraPlacementThreshold = biomeAttributes.majorFloraPlacementThreshold;
            maxHeight = biomeAttributes.maxHeight;
            minHeight = biomeAttributes.minHeight;

            // Metadata
            lodeStartIndex = currentLodeIndex;
            lodeCount = biomeAttributes.lodes.Length;
        }
    }

    /// A job-safe representation of Lode
    public struct LodeJobData
    {
        public byte blockID;
        public int minHeight;
        public int maxHeight;
        public float scale;
        public float threshold;
        public float noiseOffset;

        /// <summary>
        /// Constructor that creates LodeJobData from a Lode class.
        /// </summary>
        /// <param name="lode">The Lode to copy properties from.</param>
        public LodeJobData(Lode lode)
        {
            blockID = lode.blockID;
            minHeight = lode.minHeight;
            maxHeight = lode.maxHeight;
            scale = lode.scale;
            threshold = lode.threshold;
            noiseOffset = lode.noiseOffset;
        }
    }

    /// A job-safe representation of BlockType properties needed for meshing and lighting
    public struct BlockTypeJobData
    {
        // Block properties
        public bool isSolid;
        public bool renderNeighborFaces;

        // Fluid properties
        public FluidType fluidType;
        public byte fluidLevel;
        public byte flowLevels;

        // Lighting properties
        public byte opacity;
        public byte lightEmission;

        // Block behavior
        public bool isActive;

        // Texture ID's
        public int backFaceTexture;
        public int frontFaceTexture;
        public int topFaceTexture;
        public int bottomFaceTexture;
        public int leftFaceTexture;
        public int rightFaceTexture;

        #region Constructors

        /// <summary>
        /// Constructor that creates BlockTypeJobData from a BlockType class.
        /// </summary>
        /// <param name="blockType">The BlockType to copy properties from.</param>
        public BlockTypeJobData(BlockType blockType)
        {
            // Block properties
            isSolid = blockType.isSolid;
            renderNeighborFaces = blockType.renderNeighborFaces;

            // Fluid properties
            fluidType = blockType.fluidType;
            fluidLevel = blockType.fluidLevel;
            flowLevels = blockType.flowLevels;

            // Lighting properties
            opacity = blockType.opacity;
            lightEmission = blockType.lightEmission;

            // Block behavior
            isActive = blockType.isActive;

            // Texture ID's
            backFaceTexture = blockType.backFaceTexture;
            frontFaceTexture = blockType.frontFaceTexture;
            topFaceTexture = blockType.topFaceTexture;
            bottomFaceTexture = blockType.bottomFaceTexture;
            leftFaceTexture = blockType.leftFaceTexture;
            rightFaceTexture = blockType.rightFaceTexture;
        }

        #endregion

        #region Helper Properties

        // --- HELPER PROPERTIES ---

        /// <summary>
        /// Returns true if the block has maximum opacity, effectively blocking all light.
        /// </summary>
        public bool IsOpaque => opacity >= 15;

        /// <summary>
        /// Returns true if the block has an opacity, and thus has an affect on the light.
        /// </summary>
        public bool IsLightObstructing => opacity > 0;

        /// <summary>
        /// Returns true if the block has zero opacity, allowing light to pass through without reduction.
        /// </summary>
        public bool IsFullyTransparentToLight => opacity == 0;

        /// <summary>
        /// Returns true if the block emits its own light.
        /// </summary>
        public bool IsLightSource => lightEmission > 0;

        /// <summary>
        /// Returns true if the block is considered transparent for meshing purposes,
        /// meaning it does not cull the faces of adjacent solid blocks.
        /// </summary>
        public bool IsTransparentForMesh => !isSolid || renderNeighborFaces;

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
                int[] textures = { leftFaceTexture, rightFaceTexture, backFaceTexture, frontFaceTexture };

                // Default to the first texture in case all are unique.
                int mostFrequentTexture = textures[0];
                int maxCount = 0;

                // Iterate through each texture to see how many times it appears.
                for (int i = 0; i < textures.Length; i++)
                {
                    int currentCount = 0;
                    for (int j = 0; j < textures.Length; j++)
                    {
                        if (textures[j] == textures[i])
                        {
                            currentCount++;
                        }
                    }

                    // If the texture we just counted is more frequent than our previous winner, update it.
                    if (currentCount > maxCount)
                    {
                        maxCount = currentCount;
                        mostFrequentTexture = textures[i];
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
        public NativeList<Vector3> vertices;
        public NativeList<int> triangles;
        public NativeList<int> transparentTriangles;
        public NativeList<int> fluidTriangles;
        public NativeList<Vector2> uvs;
        public NativeList<Color> colors;
        public NativeList<Vector3> normals;

        public MeshDataJobOutput(Allocator allocator)
        {
            vertices = new NativeList<Vector3>(allocator);
            triangles = new NativeList<int>(allocator);
            transparentTriangles = new NativeList<int>(allocator);
            fluidTriangles = new NativeList<int>(allocator);
            uvs = new NativeList<Vector2>(allocator);
            colors = new NativeList<Color>(allocator);
            normals = new NativeList<Vector3>(allocator);
        }

        public void Dispose()
        {
            vertices.Dispose();
            triangles.Dispose();
            transparentTriangles.Dispose();
            fluidTriangles.Dispose();
            uvs.Dispose();
            colors.Dispose();
            normals.Dispose();
        }
    }
}