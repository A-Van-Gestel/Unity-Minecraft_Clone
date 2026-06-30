using System.Collections.Generic;
using Data;
using Editor.DataGeneration;
using Editor.Validation.Placement.Framework;
using UnityEngine;
using Id = Editor.Validation.Placement.Framework.TestPlacementBlockPalette.Id;

namespace Editor.Validation.Placement
{
    /// <summary>
    /// Regression guards for player placement, promoted from known-bug repros after each fix landed and was confirmed
    /// in-game. Two formerly-documented PLAYER_BUGS §03 entries are guarded here (both now archived to
    /// <c>_FIXED_BUGS.md</c>):
    /// <list type="bullet">
    /// <item><b>World-gen tag leak</b> — holding a formerly-misconfigured block lets it land on top of the targeted
    /// surface instead of tunneling through it (Coal Ore / Directional Block / Oak Log; real <c>BlockDatabase.asset</c>).</item>
    /// <item><b>Support gate</b> — a <see cref="BlockTags.REQUIRES_SUPPORT"/> block (grass blades) cannot be placed
    /// floating on a non-supporting cell (water / air).</item>
    /// </list>
    /// These are baselines — a regression in the placement masks, the <c>placementCanReplaceTags</c> split, or the
    /// <c>World.CanPlayerPlaceAt</c> support gate re-reds them.
    /// </summary>
    public static partial class PlacementValidationSuite
    {
        static partial void AddRegressionScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario("Regression: Coal Ore lands on top of Stone",
                () => HeldBlockLandsOnTopOf(BlockIDs.CoalOre, BlockIDs.Stone)));

            scenarios.Add(new Scenario("Regression: Directional Block lands on top of Stone",
                () => HeldBlockLandsOnTopOf(BlockIDs.DirectionalBlock, BlockIDs.Stone)));

            scenarios.Add(new Scenario("Regression: Oak Log lands on top of Oak Leaves",
                () => HeldBlockLandsOnTopOf(BlockIDs.OakLog, BlockIDs.OakLeaves)));

            // Support gate (formerly PLAYER_BUGS §03, fixed June 2026): a REQUIRES_SUPPORT block must not float on a
            // non-supporting cell. Promoted from known-bug scenarios K03a/K03b after in-game confirmation.
            scenarios.Add(new Scenario("Regression: Grass Blades cannot be placed floating above water",
                GrassBladesRejectedAboveWater));

            scenarios.Add(new Scenario("Regression: REQUIRES_SUPPORT block rejected above a non-supporting block (synthetic)",
                SupportNeedingRejectedAboveWater));
        }

        /// <summary>
        /// Faithful user repro against the <b>real</b> <c>BlockDatabase.asset</c>: holding Grass Blades (id 22,
        /// tagged <see cref="BlockTags.REQUIRES_SUPPORT"/>), the cell directly above a water block must NOT be a
        /// permitted placement, because water is non-solid and does not provide support.
        /// </summary>
        private static bool GrassBladesRejectedAboveWater()
        {
            BlockDatabase database = EditorBlockDatabaseCache.Database;
            if (database == null || database.blockTypes == null)
                return Expect(false, "could not load the real BlockDatabase for the support repro.");

            using PlacementTestWorld world = new PlacementTestWorld(database.blockTypes);
            world.SetBlock(COL_X, TARGET_Y, COL_Z, BlockIDs.Water); // water directly beneath the place cell

            bool placeable = world.EvaluatePlacementAt(BlockIDs.GrassBlades, new Vector3Int(COL_X, TARGET_Y + 1, COL_Z));
            return Expect(!placeable, "Grass Blades should NOT be placeable floating above water (no solid support below)");
        }

        /// <summary>
        /// Synthetic mirror with the controlled palette: a non-solid <see cref="BlockTags.REQUIRES_SUPPORT"/> block
        /// above the water-like fluid must be rejected. Pins the mechanism independent of the shipping data.
        /// </summary>
        private static bool SupportNeedingRejectedAboveWater()
        {
            using PlacementTestWorld world = new PlacementTestWorld(TestPlacementBlockPalette.Create());
            world.SetBlock(COL_X, TARGET_Y, COL_Z, Id.Fluid); // non-solid water directly beneath the place cell

            bool placeable = world.EvaluatePlacementAt(Id.SupportNeeding, new Vector3Int(COL_X, TARGET_Y + 1, COL_Z));
            return Expect(!placeable, "a REQUIRES_SUPPORT block should NOT be placeable above a non-supporting block");
        }

        /// <summary>
        /// Seeds the real <paramref name="targetId"/> block (with air above it) and asserts that holding
        /// <paramref name="heldId"/> lets it land on top: the top-down probe stops on the target, does not replace it,
        /// and the cell above is free.
        /// </summary>
        /// <param name="heldId">The real block id held by the player.</param>
        /// <param name="targetId">The real block id the player aims at.</param>
        private static bool HeldBlockLandsOnTopOf(ushort heldId, ushort targetId)
        {
            BlockDatabase database = EditorBlockDatabaseCache.Database;
            if (database == null || database.blockTypes == null)
            {
                Debug.LogError("  [ASSERT FAILED] could not load the real BlockDatabase for the placement repro.");
                return false;
            }

            using PlacementTestWorld world = new PlacementTestWorld(database.blockTypes);
            world.SetBlock(COL_X, TARGET_Y, COL_Z, targetId);

            PlacementOutcome o = world.ResolveTopDownPlacement(heldId, COL_X, COL_Z);

            string held = database.blockTypes[heldId].blockName;
            string target = database.blockTypes[targetId].blockName;

            return Expect(o.DidHit && o.HitCell == new Vector3Int(COL_X, TARGET_Y, COL_Z),
                       $"probe should stop on '{target}' when holding '{held}' (not tunnel through it)")
                   & Expect(!o.Replaces, $"'{held}' should not replace '{target}' — it should land on top")
                   & Expect(o.LandsOnTop, $"'{held}' should land on top of '{target}'");
        }
    }
}
