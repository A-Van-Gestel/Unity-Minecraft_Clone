using System.Collections.Generic;
using Data;
using Editor.DataGeneration;
using Editor.Validation.Placement.Framework;
using UnityEngine;

namespace Editor.Validation.Placement
{
    /// <summary>
    /// Known-bug reproductions for PLAYER_BUGS §03, driven against the <b>real</b> <c>BlockDatabase.asset</c> so they
    /// reproduce the player-visible symptom exactly: holding one of the misconfigured blocks, the placement ray
    /// tunnels through the targeted surface instead of letting the block land on top of it. Each asserts the
    /// <i>desired</i> outcome (<see cref="PlacementOutcome.LandsOnTop"/>), so it fails today and flips green once the
    /// offending block's <c>canReplaceTags</c> is retuned to the soft player-placement set.
    /// </summary>
    public static partial class PlacementValidationSuite
    {
        static partial void AddKnownBugScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario("Known bug: Coal Ore lands on top of Stone",
                () => HeldBlockLandsOnTopOf(BlockIDs.CoalOre, BlockIDs.Stone),
                knownBugId: "PA-KB-CoalOre (PLAYER_BUGS §03)"));

            scenarios.Add(new Scenario("Known bug: Directional Block lands on top of Stone",
                () => HeldBlockLandsOnTopOf(BlockIDs.DirectionalBlock, BlockIDs.Stone),
                knownBugId: "PA-KB-DirectionalBlock (PLAYER_BUGS §03)"));

            scenarios.Add(new Scenario("Known bug: Oak Log lands on top of Oak Leaves",
                () => HeldBlockLandsOnTopOf(BlockIDs.OakLog, BlockIDs.OakLeaves),
                knownBugId: "PA-KB-OakLog (PLAYER_BUGS §03)"));
        }

        /// <summary>
        /// Seeds the real <paramref name="targetId"/> block (with air above it) and asserts that holding
        /// <paramref name="heldId"/> lets it land on top: the top-down probe stops on the target, does not replace it,
        /// and the cell above is free. Returns the actual outcome, so a still-broken block reproduces the bug (red)
        /// and a retuned one passes.
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
