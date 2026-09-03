using System;
using Data;
using Helpers;
using UnityEngine;

namespace Editor.Validation.PhysicsSolver.Framework
{
    /// <summary>
    /// A small, controlled block palette for the physics/collision-solver scenarios — one full cube, two
    /// sub-voxel slabs of different thickness, and a fluid. Synthetic rather than the shipping
    /// <c>BlockDatabase.asset</c>, so the solver baselines pin the <i>solver</i> and stay green regardless of how
    /// the shipping blocks are later retuned or re-authored.
    /// <para>
    /// Indexed by the local ids in <see cref="Id"/> — the suite controls both the seeding and the lookup, so the
    /// slots need not match production <see cref="BlockIDs"/>. Slot 0 must stay Air: the voxel query path
    /// special-cases id 0.
    /// </para>
    /// </summary>
    public static class TestPhysicsBlockPalette
    {
        /// <summary>Local block ids into the palette built by <see cref="Create"/>.</summary>
        public static class Id
        {
            /// <summary>Air (id 0) — never collides.</summary>
            public const ushort Air = 0;

            /// <summary>A plain full-cube solid block (the "stone" stand-in) — takes the full-block fast path.</summary>
            public const ushort Ground = 1;

            /// <summary>
            /// A solid block filling only the lower half of its cell (the "stone half slab" stand-in), rotatable
            /// via <see cref="MetadataSchema.Facing6Roll2"/> exactly as the shipping slab is.
            /// </summary>
            public const ushort HalfSlab = 2;

            /// <summary>
            /// A solid block filling only the lowest quarter of its cell — the thinnest collision shape the
            /// engine supports, and the one <c>MIN_COLLISION_THICKNESS</c> in <c>VoxelRigidbody</c> is derived from.
            /// </summary>
            public const ushort QuarterSlab = 3;

            /// <summary>A non-solid water-like fluid, mirroring the shipping Water/Lava entries.</summary>
            public const ushort Fluid = 4;

            /// <summary>
            /// A fluid that is <i>also</i> flagged solid. No shipping block is authored this way, which is exactly
            /// why the fixture carries one: the solver's <c>fluidType != None</c> filter is defensive code that the
            /// real database cannot exercise (its fluids are already excluded by <c>isSolid == false</c>), so
            /// without this entry that filter would have no coverage at all.
            /// </summary>
            public const ushort SolidFlaggedFluid = 5;

            /// <summary>
            /// A water-like fluid that does <b>not</b> hold a body up — <see cref="SinkingFluidBuoyancy"/>
            /// mirrors the shipping Water block rather than <see cref="Id.Fluid"/>'s neutral 1.
            /// </summary>
            /// <remarks>
            /// The fixture carries both because the neutral one cannot expose a whole class of defect.
            /// <see cref="FluidBuoyancy"/> is 1 so a submerged body's expected momentum is a clean zero,
            /// which is what makes the buoyancy baseline readable — but it also means gravity is fully
            /// canceled, so any scenario about a body <i>losing</i> a vertical contest passes for free.
            /// The waterfall-climb baseline was green against it while the shipping tuning sank the player,
            /// which is exactly the gap this entry closes.
            /// </remarks>
            public const ushort SinkingFluid = 6;
        }

        /// <summary>Length of the palette array.</summary>
        public const int Count = Id.SinkingFluid + 1;

        /// <summary>The authored height of <see cref="Id.HalfSlab"/>'s collision volume, in block-local units.</summary>
        public const float HalfSlabHeight = 0.5f;

        /// <summary>The authored height of <see cref="Id.QuarterSlab"/>'s collision volume, in block-local units.</summary>
        public const float QuarterSlabHeight = 0.25f;

        #region Pinned fluid physics coefficients

        /// <summary>
        /// Buoyancy of <see cref="Id.Fluid"/> — exactly 1, i.e. precisely cancelling gravity at full
        /// submersion. Chosen so a fully submerged body's expected vertical momentum is a clean zero, which
        /// a scenario can assert without restating the gravity integration it is meant to be checking.
        /// </summary>
        public const float FluidBuoyancy = 1f;

        /// <summary>Vertical drag of <see cref="Id.Fluid"/>.</summary>
        public const float FluidVerticalDrag = 4f;

        /// <summary>Submerged horizontal speed multiplier of <see cref="Id.Fluid"/>.</summary>
        public const float FluidSubmergedSpeedMultiplier = 0.5f;

        /// <summary>Flow push strength of <see cref="Id.Fluid"/>, in meters per second at full flow.</summary>
        public const float FluidPushStrength = 2f;

        /// <summary>Swim-stroke ascent speed of <see cref="Id.Fluid"/>, in meters per second.</summary>
        public const float FluidSwimAscendSpeed = 3f;

        /// <summary>
        /// Buoyancy of <see cref="Id.SinkingFluid"/> — under 1, so gravity is only partly canceled and a
        /// body genuinely loses ground in a vertical contest. Matches the shipping Water block's authored
        /// value at the time this entry was added.
        /// </summary>
        public const float SinkingFluidBuoyancy = 0.55f;

        #endregion

        /// <summary>Builds the controlled solver palette.</summary>
        /// <returns>A <see cref="BlockType"/> array indexed by <see cref="Id"/>.</returns>
        public static BlockType[] Create()
        {
            BlockType[] palette = new BlockType[Count];

            palette[Id.Air] = new BlockType
            {
                blockName = "TestAir",
                isSolid = false,
                tags = BlockTags.NONE,
                fluidType = FluidType.None,
            };

            palette[Id.Ground] = new BlockType
            {
                blockName = "TestGround",
                isSolid = true,
                tags = BlockTags.SOLID | BlockTags.ROCK,
                fluidType = FluidType.None,
            };

            palette[Id.HalfSlab] = CreateSlab("TestHalfSlab", HalfSlabHeight);
            palette[Id.QuarterSlab] = CreateSlab("TestQuarterSlab", QuarterSlabHeight);

            palette[Id.Fluid] = new BlockType
            {
                blockName = "TestFluid",
                isSolid = false,
                tags = BlockTags.LIQUID,
                fluidType = FluidType.WaterLike,
                flowLevels = PhysicsTestWorld.WaterFlowLevels,
                // Pinned rather than left on BlockType's defaults, so retuning shipping water cannot move a
                // baseline. FluidBuoyancy is 1 — exactly neutral — so a fully submerged body's expected
                // vertical momentum is a clean zero rather than a number carrying gravity's sign.
                buoyancy = FluidBuoyancy,
                verticalDrag = FluidVerticalDrag,
                submergedSpeedMultiplier = FluidSubmergedSpeedMultiplier,
                pushStrength = FluidPushStrength,
                swimAscendSpeed = FluidSwimAscendSpeed,
            };

            palette[Id.SolidFlaggedFluid] = new BlockType
            {
                blockName = "TestSolidFlaggedFluid",
                isSolid = true,
                tags = BlockTags.LIQUID,
                fluidType = FluidType.WaterLike,
            };

            palette[Id.SinkingFluid] = new BlockType
            {
                blockName = "TestSinkingFluid",
                isSolid = false,
                tags = BlockTags.LIQUID,
                fluidType = FluidType.WaterLike,
                flowLevels = PhysicsTestWorld.WaterFlowLevels,
                buoyancy = SinkingFluidBuoyancy,
                verticalDrag = FluidVerticalDrag,
                submergedSpeedMultiplier = FluidSubmergedSpeedMultiplier,
                pushStrength = FluidPushStrength,
                swimAscendSpeed = FluidSwimAscendSpeed,
            };

            return palette;
        }

        /// <summary>
        /// Builds a solid block whose authored collision volume fills the cell's footprint up to
        /// <paramref name="height"/>, leaving the space above it empty.
        /// </summary>
        /// <param name="name">The block's display name (log output only).</param>
        /// <param name="height">Volume height in block-local units, in <c>(0, 1)</c>.</param>
        /// <returns>The configured <see cref="BlockType"/>.</returns>
        private static BlockType CreateSlab(string name, float height) => new BlockType
        {
            blockName = name,
            isSolid = true,
            tags = BlockTags.SOLID | BlockTags.ROCK,
            fluidType = FluidType.None,
            metadataSchema = MetadataSchema.Facing6Roll2,
            collisionBounds = new BlockCollisionBounds
            {
                mode = CollisionBoundsMode.CustomAABB,
                min = Vector3.zero,
                max = new Vector3(1f, height, 1f),
            },
        };

        /// <summary>
        /// Finds a metadata byte whose rotation of <paramref name="block"/>'s authored volume satisfies
        /// <paramref name="predicate"/>, by asking the production <see cref="BlockCollisionBoundsUtility"/> rather
        /// than assuming a <see cref="MetadataSchema.Facing6Roll2"/> encoding — so a scenario stays correct if the
        /// encoding is renumbered, and reports a <i>fixture</i> failure rather than silently testing the unrotated
        /// block. Mirrors the equivalent probe in the Placement suite's sub-voxel scenarios.
        /// </summary>
        /// <param name="block">The block whose rotations to search.</param>
        /// <param name="predicate">Accepts the resolved cell-local bounds (the cell at the origin).</param>
        /// <param name="meta">The first metadata byte satisfying <paramref name="predicate"/>, when one exists.</param>
        /// <returns>True if a matching orientation was found.</returns>
        public static bool TryFindMeta(BlockType block, Func<Bounds, bool> predicate, out byte meta)
        {
            for (int candidate = 0; candidate <= byte.MaxValue; candidate++)
            {
                Bounds bounds = BlockCollisionBoundsUtility.GetBounds(block, (byte)candidate, Vector3.zero);
                if (!predicate(bounds)) continue;

                meta = (byte)candidate;
                return true;
            }

            meta = 0;
            return false;
        }
    }
}
