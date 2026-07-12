using System.Collections.Generic;
using Data;
using Editor.Validation.Placement.Framework;
using UnityEngine;
using Id = Editor.Validation.Placement.Framework.TestPlacementBlockPalette.Id;
using Scenario = Editor.Validation.Framework.Scenario;

namespace Editor.Validation.Placement
{
    /// <summary>
    /// Baseline (regression) scenarios for the placement suite. These run against the controlled, correctly-configured
    /// <see cref="TestPlacementBlockPalette"/>, so they pin the placement <i>mechanism</i> (tag resolution + the
    /// top-down <c>World.CheckForVoxel</c> probe / replace / occupancy composition) and must stay green independent
    /// of the shipping block data.
    /// </summary>
    public static partial class PlacementValidationSuite
    {
        /// <summary>The column the scenarios probe (well inside the origin chunk).</summary>
        private const int COL_X = 8;

        /// <summary>The column the scenarios probe (well inside the origin chunk).</summary>
        private const int COL_Z = 8;

        /// <summary>The Y a scenario's primary target block is seeded at, with room above for a "land on top" cell.</summary>
        private const int TARGET_Y = 8;

        static partial void AddBaselineScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario("Baseline: CanReplace tag matrix", CanReplaceMatrix));
            scenarios.Add(new Scenario("Baseline: solid lands on top of solid", SolidLandsOnTopOfSolid));
            scenarios.Add(new Scenario("Baseline: solid placed where a plant grows replaces it", SolidReplacesSoftPlant));
            scenarios.Add(new Scenario("Baseline: empty hand replaces only REPLACEABLE", EmptyHandReplacesOnlyReplaceable));
            scenarios.Add(new Scenario("Baseline: lands on top of unbreakable (never replaces)", LandsOnTopOfUnbreakable));
            scenarios.Add(new Scenario("Baseline: REQUIRES_SUPPORT block is placeable above a solid support", SupportNeedingPlaceableAboveSolid));
            scenarios.Add(new Scenario("Baseline: non-support block stays placeable above water (no over-rejection)", NonSupportBlockPlaceableAboveWater));
        }

        /// <summary>
        /// Pure <see cref="BlockTagUtility.CanReplace"/> matrix — no world. Pins the core replace decision the whole
        /// placement path defers to.
        /// </summary>
        private static bool CanReplaceMatrix()
        {
            BlockType[] p = TestPlacementBlockPalette.Create();
            BlockType air = p[Id.Air];
            BlockType ground = p[Id.Ground];
            BlockType plant = p[Id.SoftPlant];
            BlockType unbreakable = p[Id.Unbreakable];

            bool ok = true;
            ok &= Expect(BlockTagUtility.CanReplaceForPlacement(ground, air), "ground should replace air");
            ok &= Expect(!BlockTagUtility.CanReplaceForPlacement(ground, ground), "ground should NOT replace solid ground");
            ok &= Expect(BlockTagUtility.CanReplaceForPlacement(ground, plant), "ground should replace a REPLACEABLE plant");
            ok &= Expect(!BlockTagUtility.CanReplaceForPlacement(ground, unbreakable), "nothing should replace an UNBREAKABLE block");
            // A block with canReplaceTags == NONE can only replace air or a REPLACEABLE block (Rule C).
            ok &= Expect(BlockTagUtility.CanReplaceForPlacement(plant, plant), "NONE-canReplace block should replace a REPLACEABLE block");
            ok &= Expect(!BlockTagUtility.CanReplaceForPlacement(plant, ground), "NONE-canReplace block should NOT replace a solid block");
            return ok;
        }

        /// <summary>Holding a well-configured solid, aiming down at another solid: the probe stops and the block lands on top.</summary>
        private static bool SolidLandsOnTopOfSolid()
        {
            using PlacementTestWorld world = new PlacementTestWorld(TestPlacementBlockPalette.Create());
            world.SetBlock(COL_X, TARGET_Y, COL_Z, Id.Ground);

            PlacementOutcome o = world.ResolveTopDownPlacement(Id.Ground, COL_X, COL_Z);

            bool ok = true;
            ok &= Expect(o.DidHit, "probe should stop at the solid target (not tunnel through it)");
            ok &= Expect(o.HitCell == new Vector3Int(COL_X, TARGET_Y, COL_Z), "probe should hit the seeded solid block");
            ok &= Expect(!o.Replaces, "a solid should not replace another solid — it lands on top");
            ok &= Expect(o.PlaceCell == new Vector3Int(COL_X, TARGET_Y + 1, COL_Z), "place cell should be the cell above the target");
            ok &= Expect(o.LandsOnTop, "net outcome: the block lands on top of the target");
            return ok;
        }

        /// <summary>
        /// Holding a solid and aiming down a column where a REPLACEABLE plant sits on solid ground: the engine skips
        /// the plant (the held block can replace it) and the block lands in the plant's vacated cell — the intended
        /// "place a block where tall grass grows" behavior. This is the skip-then-place mechanism the §03 bug abuses
        /// for structural blocks.
        /// </summary>
        private static bool SolidReplacesSoftPlant()
        {
            using PlacementTestWorld world = new PlacementTestWorld(TestPlacementBlockPalette.Create());
            world.SetBlock(COL_X, TARGET_Y, COL_Z, Id.SoftPlant);
            world.SetBlock(COL_X, TARGET_Y - 1, COL_Z, Id.Ground); // the surface the plant grows on

            PlacementOutcome o = world.ResolveTopDownPlacement(Id.Ground, COL_X, COL_Z);

            bool ok = true;
            ok &= Expect(o.DidHit, "probe should skip the plant and stop on the ground beneath it");
            ok &= Expect(o.HitCell == new Vector3Int(COL_X, TARGET_Y - 1, COL_Z), "probe should hit the ground under the plant");
            ok &= Expect(o.PlaceCell == new Vector3Int(COL_X, TARGET_Y, COL_Z), "block should land in the plant's cell (replacing it)");
            ok &= Expect(o.Placeable, "the plant cell is not solid-occupied, so it is placeable");
            return ok;
        }

        /// <summary>With an empty hand, only REPLACEABLE blocks resolve to a replace (the punch-target fallback).</summary>
        private static bool EmptyHandReplacesOnlyReplaceable()
        {
            using PlacementTestWorld world = new PlacementTestWorld(TestPlacementBlockPalette.Create());
            world.SetBlock(COL_X, TARGET_Y, COL_Z, Id.SoftPlant);
            world.SetBlock(COL_X + 2, TARGET_Y, COL_Z, Id.Ground);

            // Empty hand → skipTags NONE → the probe stops directly on the target (no tunnel-through).
            PlacementOutcome plant = world.ResolveTopDownPlacement(null, COL_X, COL_Z);
            PlacementOutcome ground = world.ResolveTopDownPlacement(null, COL_X + 2, COL_Z);

            bool ok = true;
            ok &= Expect(plant.DidHit && plant.Replaces, "empty hand should resolve a REPLACEABLE plant to replace in place");
            ok &= Expect(ground.DidHit && !ground.Replaces, "empty hand should NOT resolve a solid block to replace");
            return ok;
        }

        /// <summary>
        /// A <see cref="BlockTags.REQUIRES_SUPPORT"/> block placed directly above a solid support is permitted —
        /// the support gate must let legitimately-supported placements through (tripwire against over-rejection).
        /// </summary>
        private static bool SupportNeedingPlaceableAboveSolid()
        {
            using PlacementTestWorld world = new PlacementTestWorld(TestPlacementBlockPalette.Create());
            world.SetBlock(COL_X, TARGET_Y, COL_Z, Id.Ground); // solid support directly beneath the place cell

            bool placeable = world.EvaluatePlacementAt(Id.SupportNeeding, new Vector3Int(COL_X, TARGET_Y + 1, COL_Z));
            return Expect(placeable, "a REQUIRES_SUPPORT block should be placeable directly above a solid support");
        }

        /// <summary>
        /// A non-<see cref="BlockTags.REQUIRES_SUPPORT"/> block remains placeable above water — the support gate is
        /// scoped to REQUIRES_SUPPORT blocks only and must not start rejecting ordinary floating placements.
        /// </summary>
        private static bool NonSupportBlockPlaceableAboveWater()
        {
            using PlacementTestWorld world = new PlacementTestWorld(TestPlacementBlockPalette.Create());
            world.SetBlock(COL_X, TARGET_Y, COL_Z, Id.Fluid); // non-solid water directly beneath the place cell

            bool placeable = world.EvaluatePlacementAt(Id.Ground, new Vector3Int(COL_X, TARGET_Y + 1, COL_Z));
            return Expect(placeable, "a non-REQUIRES_SUPPORT block should remain placeable above water (gate must not over-reject)");
        }

        /// <summary>An UNBREAKABLE target is never replaced, but a block can still be placed on top of it.</summary>
        private static bool LandsOnTopOfUnbreakable()
        {
            using PlacementTestWorld world = new PlacementTestWorld(TestPlacementBlockPalette.Create());
            world.SetBlock(COL_X, TARGET_Y, COL_Z, Id.Unbreakable);

            PlacementOutcome o = world.ResolveTopDownPlacement(Id.Ground, COL_X, COL_Z);

            bool ok = true;
            ok &= Expect(o.DidHit, "probe should stop at the unbreakable target");
            ok &= Expect(!o.Replaces, "nothing should replace an UNBREAKABLE block");
            ok &= Expect(o.LandsOnTop, "the block should still land on top of the unbreakable block");
            return ok;
        }
    }
}
