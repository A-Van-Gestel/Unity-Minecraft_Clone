using System;
using MyBox;
using UnityEngine;

namespace Data
{
    /// <summary>
    /// Represents a single block type in the game.
    /// </summary>
    [Serializable]
    public class BlockType
    {
        [Header("Block Properties")]
        public string blockName;

        [InitializationField]
        public Sprite icon;

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

        [Header("Block Behavior")]
        [Tooltip("Indicates whether the block has any block behavior.")]
        public bool isActive;

        [Header("Texture Values")]
        public int backFaceTexture;

        public int frontFaceTexture;
        public int topFaceTexture;
        public int bottomFaceTexture;
        public int leftFaceTexture;
        public int rightFaceTexture;

        // Back, Front, Top, Bottom, Left, Right
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
        public bool IsOpaque => opacity >= 15;

        /// <summary>
        /// Returns true if the block has an opacity, and thus has an effect on the light.
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

        public override string ToString()
        {
            return $"BlockType: {{ Name = {blockName}, IsSolid = {isSolid}, fluidType = {fluidType}, fluidLevel = {fluidLevel.GetType().Name}, Opacity = {opacity}, RenderNeighborFaces = {renderNeighborFaces}, Icon = {icon}, StackSize = {stackSize} }}";
        }
    }

    public enum FluidType : byte // Using byte makes it job-safe and memory-efficient
    {
        None,
        Water,
        Lava
    }
}