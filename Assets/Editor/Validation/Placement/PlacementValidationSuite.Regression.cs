using System.Collections.Generic;
using Data;
using Editor.DataGeneration;
using Editor.Validation.Placement.Framework;
using UnityEngine;

namespace Editor.Validation.Placement
{
    /// <summary>
    /// Regression guards for PLAYER_BUGS §03 (promoted from the original known-bug repros after the fix landed and was
    /// confirmed in-game). Driven against the <b>real</b> <c>BlockDatabase.asset</c>, each asserts that holding a
    /// formerly-misconfigured block lets it land on top of the targeted surface instead of tunnelling through it. These
    /// are baselines — a regression in the placement masks or the <c>placementCanReplaceTags</c> split re-reds them.
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
            world.SetBlock(ColX, TargetY, ColZ, targetId);

            PlacementOutcome o = world.ResolveTopDownPlacement(heldId, ColX, ColZ);

            string held = database.blockTypes[heldId].blockName;
            string target = database.blockTypes[targetId].blockName;

            return Expect(o.DidHit && o.HitCell == new Vector3Int(ColX, TargetY, ColZ),
                       $"probe should stop on '{target}' when holding '{held}' (not tunnel through it)")
                   & Expect(!o.Replaces, $"'{held}' should not replace '{target}' — it should land on top")
                   & Expect(o.LandsOnTop, $"'{held}' should land on top of '{target}'");
        }
    }
}
