// ReSharper disable InconsistentNaming

using System;

namespace Data
{
    /// <summary>
    /// Defines categories that a block can belong to. A block can have multiple tags.
    /// Using the [Flags] attribute allows for bit-masking and a multi-select dropdown in the Unity Inspector.
    /// Using uint to support up to 32 tags.
    /// </summary>
    [Flags]
    public enum BlockTags : uint
    {
        // Use powers of two for bit-masking.
        NONE = 0,

        // --- Core Physical Properties ---
        SOLID = 1 << 0, // 1
        LIQUID = 1 << 1, // 2
        UNBREAKABLE = 1 << 2, // 4
        GRAVITY_AFFECTED = 1 << 3, // 8 (Sand, Gravel, etc.)

        // --- Material Types (for tools, sounds, interactions) ---
        SOIL = 1 << 4, // 16 (Dirt, Grass, Sand, Gravel, etc. - Good for shovels)
        WOOD = 1 << 5, // 32 (Logs, Planks, etc. - Good for axes, flammable)
        PLANT = 1 << 6, // 64 (Non-solid flora like flowers, saplings, grass, etc.)
        LEAVES = 1 << 7, // 128 (A specific type of plant/organic material)
        ROCK = 1 << 8, // 256 (Good for pickaxes)
        MINERAL = 1 << 9, // 512 (Good for pickaxes)
        ORGANIC = 1 << 10, // 1024 (General tag for living or once-living material)

        // --- Game Logic & Behavior Tags ---
        MAN_MADE = 1 << 11, // 2048
        CLIMBABLE = 1 << 12, // 4096 (Ladders, Vines, etc.)
        REPLACEABLE = 1 << 13, // 8192 (Tall grass, etc. can be replaced by placing a block)
        REQUIRES_SUPPORT = 1 << 14, // 16384 (Breaks when supporting block beneath becomes non-solid)
        IGNORE_RAYCAST = 1 << 15, // 32768 (Raymarcher always passes through this block)

        // --- Debug Tags ---
        DEBUG = 1 << 16, // 65536 (Used for debugging purposes, will not be in inventory in production builds)
    }

    /// <summary>
    /// Defines an override rule for a VoxelMod to bypass the default Block Tag system.
    /// </summary>
    public enum ReplacementRule : byte
    {
        /// <summary>
        /// Use the default replacement rules defined in the block's BlockType tags.
        /// </summary>
        Default,

        /// <summary>
        /// Ignore all placement rules and replace any block (except Unbreakable).
        /// </summary>
        ForcePlace,

        /// <summary>
        /// A common override: only allow placement if the target block is Air.
        /// </summary>
        OnlyReplaceAir,
    }

    /// <summary>
    /// Utility class for evaluating block tag interactions.
    /// </summary>
    public static class BlockTagUtility
    {
        /// <summary>
        /// Evaluates whether an incoming block is allowed to replace an existing block
        /// based on the tag definitions and replacement rules of both blocks.
        /// </summary>
        /// <param name="incomingProps">The block properties of the block being placed.</param>
        /// <param name="existingProps">The block properties of the block being replaced.</param>
        /// <returns>True if the replacement is allowed, false otherwise.</returns>
        public static bool CanReplace(BlockType incomingProps, BlockType existingProps)
        {
            // Rule A: Nothing can replace an Unbreakable block.
            if ((existingProps.tags & BlockTags.UNBREAKABLE) != 0)
            {
                return false;
            }

            // Rule B: If the incoming block has specific replacement rules...
            if (incomingProps.canReplaceTags != BlockTags.NONE)
            {
                // ...and the existing block has NO tags that match, it can't be placed.
                // The bitwise AND (&) will be 0 if there are no common flags.
                if ((existingProps.tags & incomingProps.canReplaceTags) == 0)
                {
                    // We make one exception: anything can replace "Air", which we define as a block with NONE tags.
                    if (existingProps.tags != BlockTags.NONE)
                    {
                        return false;
                    }
                }
            }
            // Rule C: If the incoming block is set to NONE, it means it can only
            // replace Air or any block with the REPLACEABLE tag.
            else if (existingProps.tags != BlockTags.NONE &&
                     (existingProps.tags & BlockTags.REPLACEABLE) == 0)
            {
                return false;
            }

            return true;
        }
    }
}
