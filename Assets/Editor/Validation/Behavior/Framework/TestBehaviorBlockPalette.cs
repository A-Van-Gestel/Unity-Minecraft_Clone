using Data;

namespace Editor.Validation.Behavior.Framework
{
    /// <summary>
    /// Synthetic block palette for the behavior-tick validation suite, assigned to the stub
    /// <see cref="BlockDatabase.blockTypes"/> that <see cref="BehaviorTestWorld"/> exposes through
    /// <c>World.Instance.BlockTypes</c>.
    /// <para>
    /// <b>Unlike <see cref="Meshing.Framework.TestMeshBlockPalette"/>, this palette is indexed by the
    /// REAL <see cref="BlockIDs"/> values</b> — not test-local indices. The behavior code hardcodes block
    /// identities (<c>id == BlockIDs.Grass</c>, <c>BlockIDs.Dirt</c>, <c>BlockIDs.Air</c>), so the array slot
    /// for a block MUST equal its production ID or the <see cref="BlockBehavior"/> branches would take the
    /// wrong path. The array is sized to cover the highest ID used (<see cref="BlockIDs.Lava"/>); unused
    /// slots hold a harmless air-like inert block.
    /// </para>
    /// <para>
    /// Only the fields the behavior tick actually reads are set (fluid flow params, <c>isSolid</c>,
    /// <c>isActive</c>, <c>opacity</c>); rendering/lighting fields are left at their defaults because no
    /// mesh or light job runs in this harness.
    /// </para>
    /// </summary>
    public static class TestBehaviorBlockPalette
    {
        /// <summary>Length of the palette array — covers IDs 0..<see cref="BlockIDs.Lava"/> (20). Ids outside
        /// this range are treated as inert by <c>BehaviorTestWorld.PaletteOf</c>, so this need not cover every
        /// <see cref="BlockIDs"/> value — only the blocks the scenarios actually place.</summary>
        public const int Count = BlockIDs.Lava + 1;

        // Fluid/solid profiles, named per the "No Magic Numbers" convention. These mirror the documented
        // production water/lava parameters; keep them in sync if the real block data changes.
        private const byte WATER_FLOW_LEVELS = 8;
        private const float WATER_SPREAD_CHANCE = 1.0f; // deterministic — water always spreads
        private const byte LAVA_FLOW_LEVELS = 4;
        private const float LAVA_SPREAD_CHANCE = 0.25f; // viscous — lava spreads ~1 tick in 4
        private const byte OPAQUE_OPACITY = 15; // full light blocking (solids)
        private const byte TRANSPARENT_OPACITY = 0; // no light blocking (air/fluids)

        /// <summary>
        /// Builds the palette as a managed <see cref="BlockType"/> array indexed by real <see cref="BlockIDs"/>.
        /// </summary>
        /// <returns>A <see cref="BlockType"/> array of length <see cref="Count"/>, ready to assign to a stub
        /// <see cref="BlockDatabase.blockTypes"/>.</returns>
        public static BlockType[] Create()
        {
            BlockType[] palette = new BlockType[Count];

            // Inert air-like default in every slot, so an accidentally-referenced ID never NREs and never
            // ticks. Real entries below overwrite the slots the suite places.
            for (int i = 0; i < palette.Length; i++)
                palette[i] = MakeInert("TestUnused_" + i);

            palette[BlockIDs.Air] = MakeInert("TestAir");

            palette[BlockIDs.Stone] = MakeSolid("TestStone");
            palette[BlockIDs.Dirt] = MakeSolid("TestDirt");

            // Grass is a behavior block (spreads to adjacent dirt) → isActive.
            BlockType grass = MakeSolid("TestGrass");
            grass.isActive = true;
            palette[BlockIDs.Grass] = grass;

            // Water source: WaterLike, waterfall max-spread, infinite-source regeneration — the documented
            // water profile. isActive so it ticks.
            palette[BlockIDs.Water] = MakeFluid("TestWater", FluidType.WaterLike, WATER_FLOW_LEVELS, WATER_SPREAD_CHANCE,
                infiniteSourceRegeneration: true);

            // Lava: LavaLike, viscous, no infinite source — for later scenarios.
            palette[BlockIDs.Lava] = MakeFluid("TestLava", FluidType.LavaLike, LAVA_FLOW_LEVELS, LAVA_SPREAD_CHANCE,
                infiniteSourceRegeneration: false);

            return palette;
        }

        /// <summary>An inert, non-solid, non-active, fully-transparent block (the air / unused-slot default).</summary>
        private static BlockType MakeInert(string name)
        {
            return new BlockType
            {
                blockName = name,
                isSolid = false,
                opacity = TRANSPARENT_OPACITY,
                isActive = false,
                fluidType = FluidType.None,
            };
        }

        /// <summary>An opaque solid block (floor / dirt / grass body). Not a fluid, not active by default.</summary>
        private static BlockType MakeSolid(string name)
        {
            return new BlockType
            {
                blockName = name,
                isSolid = true,
                opacity = OPAQUE_OPACITY,
                isActive = false,
                fluidType = FluidType.None,
            };
        }

        /// <summary>
        /// A fluid block (non-solid, transparent, active) wired with the flow parameters the
        /// <see cref="BlockBehavior"/> fluid path reads.
        /// </summary>
        /// <param name="name">Diagnostic block name.</param>
        /// <param name="fluidType">Water-like or lava-like.</param>
        /// <param name="flowLevels">Horizontal flow distance from a source (8 water, 4 lava).</param>
        /// <param name="spreadChance">Per-tick horizontal-spread probability (1.0 water, 0.25 lava).</param>
        /// <param name="infiniteSourceRegeneration">Whether two adjacent sources over a solid floor regenerate a source.</param>
        private static BlockType MakeFluid(string name, FluidType fluidType, byte flowLevels, float spreadChance,
            bool infiniteSourceRegeneration)
        {
            return new BlockType
            {
                blockName = name,
                isSolid = false,
                opacity = TRANSPARENT_OPACITY,
                isActive = true,
                fluidType = fluidType,
                fluidLevel = 0,
                flowLevels = flowLevels,
                spreadChance = spreadChance,
                waterfallsMaxSpread = true,
                infiniteSourceRegeneration = infiniteSourceRegeneration,
            };
        }
    }
}
