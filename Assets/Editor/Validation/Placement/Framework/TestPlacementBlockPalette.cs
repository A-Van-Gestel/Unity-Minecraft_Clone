using Data;
using UnityEngine;

namespace Editor.Validation.Placement.Framework
{
    /// <summary>
    /// A small, controlled block palette for the placement <b>baseline</b> scenarios — every block here is
    /// <i>correctly</i> configured for player placement, so the baselines pin the placement mechanism itself and
    /// stay green regardless of how the shipping <c>BlockDatabase.asset</c> is later retuned. The data-audit and
    /// known-bug repros deliberately use the <b>real</b> database instead (see the suite partials).
    /// <para>
    /// Indexed by the local ids in <see cref="Id"/> (the suite controls both the placement and the lookup, so the
    /// slots need not match production <see cref="BlockIDs"/>) — except slot 0, which must stay Air because
    /// <c>World.CheckForVoxel</c> special-cases id 0.
    /// </para>
    /// </summary>
    public static class TestPlacementBlockPalette
    {
        /// <summary>Local block ids into the palette built by <see cref="Create"/>.</summary>
        public static class Id
        {
            /// <summary>Air (id 0) — empty, never a ray hit.</summary>
            public const ushort Air = 0;

            /// <summary>A well-configured solid block (the "stone" stand-in): places on top of solids, replaces soft blocks.</summary>
            public const ushort Ground = 1;

            /// <summary>A soft, non-solid <see cref="BlockTags.REPLACEABLE"/> plant (the "tall grass" stand-in).</summary>
            public const ushort SoftPlant = 2;

            /// <summary>A solid <see cref="BlockTags.UNBREAKABLE"/> block (the "bedrock" stand-in) — never replaceable.</summary>
            public const ushort Unbreakable = 3;

            /// <summary>A non-solid water-like fluid (the "water" stand-in).</summary>
            public const ushort Fluid = 4;

            /// <summary>A non-solid <see cref="BlockTags.REQUIRES_SUPPORT"/> plant (the "grass blades" stand-in) — needs a solid block beneath it.</summary>
            public const ushort SupportNeeding = 5;

            /// <summary>
            /// A solid block occupying only the lower half of its cell (the "stone half slab" stand-in), so the
            /// upper half is empty space a ray must pass through. Rotatable via
            /// <see cref="MetadataSchema.Facing6Roll2"/>, matching the shipping slab.
            /// </summary>
            public const ushort HalfSlab = 6;
        }

        /// <summary>
        /// The sane player-placement skip/replace mask: replace only tall grass (<see cref="BlockTags.REPLACEABLE"/>)
        /// and water (<see cref="BlockTags.LIQUID"/>) — never structural blocks, and never bare <see cref="BlockTags.PLANT"/>
        /// (which also tags solid leaves).
        /// </summary>
        public const BlockTags SanePlayerCanReplace =
            BlockTags.REPLACEABLE | BlockTags.LIQUID;

        /// <summary>Length of the palette array.</summary>
        public const int Count = Id.HalfSlab + 1;

        /// <summary>Builds the controlled baseline palette.</summary>
        /// <returns>A <see cref="BlockType"/> array indexed by <see cref="Id"/>.</returns>
        public static BlockType[] Create()
        {
            BlockType[] palette = new BlockType[Count];

            // Air: NONE tags (so CanReplace treats it as universally replaceable); canReplaceTags ALL mirrors the
            // real Air entry but is never consulted as a held block here.
            palette[Id.Air] = new BlockType
            {
                blockName = "TestAir",
                isSolid = false,
                tags = BlockTags.NONE,
                worldGenCanReplaceTags = (BlockTags)0xFFFFFFFF,
                placementCanReplaceTags = (BlockTags)0xFFFFFFFF,
                fluidType = FluidType.None,
            };

            palette[Id.Ground] = new BlockType
            {
                blockName = "TestGround",
                isSolid = true,
                tags = BlockTags.SOLID | BlockTags.ROCK,
                worldGenCanReplaceTags = SanePlayerCanReplace,
                placementCanReplaceTags = SanePlayerCanReplace,
                fluidType = FluidType.None,
            };

            palette[Id.SoftPlant] = new BlockType
            {
                blockName = "TestSoftPlant",
                isSolid = false,
                tags = BlockTags.REPLACEABLE | BlockTags.PLANT,
                worldGenCanReplaceTags = BlockTags.NONE,
                placementCanReplaceTags = BlockTags.NONE,
                fluidType = FluidType.None,
            };

            palette[Id.Unbreakable] = new BlockType
            {
                blockName = "TestUnbreakable",
                isSolid = true,
                tags = BlockTags.SOLID | BlockTags.UNBREAKABLE,
                worldGenCanReplaceTags = SanePlayerCanReplace,
                placementCanReplaceTags = SanePlayerCanReplace,
                fluidType = FluidType.None,
            };

            palette[Id.Fluid] = new BlockType
            {
                blockName = "TestFluid",
                isSolid = false,
                tags = BlockTags.LIQUID,
                worldGenCanReplaceTags = SanePlayerCanReplace,
                placementCanReplaceTags = SanePlayerCanReplace,
                fluidType = FluidType.WaterLike,
            };

            // Mirrors Grass Blades (id 22): non-solid, REQUIRES_SUPPORT, and able to be placed into the soft set
            // (REPLACEABLE|LIQUID) — so the only thing that should stop it floating on water is the support gate.
            palette[Id.SupportNeeding] = new BlockType
            {
                blockName = "TestSupportNeeding",
                isSolid = false,
                tags = BlockTags.REPLACEABLE | BlockTags.PLANT | BlockTags.REQUIRES_SUPPORT,
                worldGenCanReplaceTags = SanePlayerCanReplace,
                placementCanReplaceTags = SanePlayerCanReplace,
                fluidType = FluidType.None,
            };

            // Mirrors Stone Half Slab (id 17): a solid block whose authored volume fills only the lower half of the
            // cell, so the upper half is empty space the ray must pass through. Facing6Roll2 lets a scenario rotate
            // it into a top slab, which is how the shipping block expresses its flipped variant.
            palette[Id.HalfSlab] = new BlockType
            {
                blockName = "TestHalfSlab",
                isSolid = true,
                tags = BlockTags.SOLID | BlockTags.ROCK,
                worldGenCanReplaceTags = SanePlayerCanReplace,
                placementCanReplaceTags = SanePlayerCanReplace,
                fluidType = FluidType.None,
                metadataSchema = MetadataSchema.Facing6Roll2,
                collisionBounds = new BlockCollisionBounds
                {
                    mode = CollisionBoundsMode.CustomAABB,
                    min = new Vector3(0f, 0f, 0f),
                    max = new Vector3(1f, 0.5f, 1f),
                },
            };

            return palette;
        }
    }
}
