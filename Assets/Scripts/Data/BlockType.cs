using System;
using System.Runtime.CompilerServices;
using MyBox;
using UnityEngine;
using UnityEngine.Serialization;

namespace Data
{
    /// <summary>
    /// Represents a single block type in the game.
    /// </summary>
    [Serializable]
    public class BlockType
    {
        [Header("Block Properties")]
        [Tooltip("The display name of the block.")]
        public string blockName;

        [Tooltip("The icon that appears in the toolbar and inventory.")]
        [InitializationField]
        public Sprite icon;

        [Tooltip("The custom mesh data for this block, if it's not a standard cube.")]
        [InitializationField]
        public VoxelMeshData meshData;

        [Tooltip("The maximum amount of this block that can be stacked.")]
        [Range(0, 64)]
        public int stackSize = 64;

        [Tooltip("Indicates whether the player collides with this block.")]
        public bool isSolid;

        [Tooltip("Indicates whether the neighbouring faces should still be rendered when this block is placed.")]
        public bool renderNeighborFaces;

        [Header("Fluid Properties")]
        [Tooltip("The type of fluid this block represents. 'None' for solid blocks.")]
        public FluidType fluidType = FluidType.None;

        [Tooltip("The ID passed to the liquid shader (e.g., 0 for Water, 1 for Lava). This controls the visual style.")]
        [Range(0, 16)] // 0 = Water, 1 = Lava, range can be expanded up to 256 (byte)
        public byte fluidShaderID = 0;

        [Tooltip("The pre-computed mesh data for this fluid.")]
        [InitializationField]
        public FluidMeshData fluidMeshData;

        [Tooltip("Default fluid level.")]
        [Range(0, 15)]
        public byte fluidLevel = 0;

        [Tooltip("How many blocks a fluid can flow horizontally from a source block.\nWater is 8 (levels 0-7), Lava is typically 4.")]
        [Range(1, 8)]
        public byte flowLevels = 8;

        [Header("Lighting Properties")]
        [Tooltip("How many light levels will be blocked by this block.")]
        [Range(0, 15)]
        public byte opacity = 15;

        [Tooltip("How many light levels will be emitted by this block.")]
        [Range(0, 15)]
        public byte lightEmission = 0;

        [Header("Placement Rules")]
        [Tooltip("Apply a preset for the tags below. This is a workflow helper and doesn't affect the game directly. After applying, the values are copied to the fields below.")]
        public BlockTagPreset tagPreset;

        [Tooltip("What tags does this block have? A block can have multiple tags.")]
        public BlockTags tags;

        [FormerlySerializedAs("canBeReplacedByTags")]
        [Tooltip("What tags can this block replace? If NONE, it can only replace Air. If ALL tags are selected, it can replace anything (except Unbreakable).")]
        public BlockTags canReplaceTags;

        [Header("Block Behavior")]
        [Tooltip("Indicates whether the block has any block behavior.")]
        public bool isActive;

        [Header("Texture Values")]
        [Tooltip("Texture ID for the Negative Z face.")]
        public int backFaceTexture;

        [Tooltip("Texture ID for the Positive Z face.")]
        public int frontFaceTexture;

        [Tooltip("Texture ID for the Positive Y face.")]
        public int topFaceTexture;

        [Tooltip("Texture ID for the Negative Y face.")]
        public int bottomFaceTexture;

        [Tooltip("Texture ID for the Negative X face.")]
        public int leftFaceTexture;

        [Tooltip("Texture ID for the Positive X face.")]
        public int rightFaceTexture;

        /// <summary>
        /// Gets the texture atlas ID for a specific face of this block type.
        /// </summary>
        /// <param name="faceIndex">The index of the face (0=Back, 1=Front, 2=Top, 3=Bottom, 4=Left, 5=Right).</param>
        /// <returns>The integer ID linking to the texture mapping atlas.</returns>
        /// <example><c>GetTextureID(2)</c> -> Returns the <c>topFaceTexture</c> ID.</example>
        public int GetTextureID(int faceIndex)
        {
            switch (faceIndex)
            {
                case 0:
                    return backFaceTexture;
                case 1:
                    return frontFaceTexture;
                case 2:
                    return topFaceTexture;
                case 3:
                    return bottomFaceTexture;
                case 4:
                    return leftFaceTexture;
                case 5:
                    return rightFaceTexture;
                default:
                    Debug.LogError("Error in GetTextureID; invalid face index");
                    return 0;
            }
        }

        // --- HELPER PROPERTIES ---

        /// <summary>
        /// Returns true if the block has maximum opacity, effectively blocking all light.
        /// </summary>
        public bool IsOpaque
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => opacity >= 15;
        }

        /// <summary>
        /// Returns true if the block has an opacity, and thus has an effect on the light.
        /// </summary>
        public bool IsLightObstructing
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => opacity > 0;
        }

        /// <summary>
        /// Returns true if the block has zero opacity, allowing light to pass through without reduction.
        /// </summary>
        public bool IsFullyTransparentToLight
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => opacity == 0;
        }

        /// <summary>
        /// Returns true if the block emits its own light.
        /// </summary>
        public bool IsLightSource
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => lightEmission > 0;
        }

        /// <summary>
        /// Returns true if the block is considered transparent for meshing purposes,
        /// meaning it does not cull the faces of adjacent solid blocks.
        /// </summary>
        public bool IsTransparentForMesh
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => !isSolid || renderNeighborFaces;
        }

        public override string ToString()
        {
            return $"BlockType: {{ Name = {blockName}, IsSolid = {isSolid}, fluidType = {fluidType}, fluidLevel = {fluidLevel.GetType().Name}, Opacity = {opacity}, RenderNeighborFaces = {renderNeighborFaces}, Icon = {icon}, StackSize = {stackSize} }}";
        }
    }

    /// <summary>
    /// Enumerates the different types of fluids that can be used in the game.
    /// This is used to determine the behavior of the fluid.
    /// </summary>
    public enum FluidType : byte // Using byte makes it job-safe and memory-efficient
    {
        None,
        WaterLike,
        LavaLike
    }
}
