using System.Collections.Generic;
using Data;
using Editor.Validation.Behavior.Framework;
using UnityEngine;

namespace Editor.Validation.Behavior
{
    /// <summary>
    /// <b>BH-B8 / BH-B9</b> — the placeholder-neighbor regression guards, promoted from the <c>K18a</c>/<c>K18b</c>
    /// repro scenarios after Fluid Bug 18 was fixed and confirmed in-game (July 2026; archived in
    /// <c>Documentation/Bugs/_FIXED_BUGS.md</c>).
    /// <para>
    /// They pin the invariant both fluid read paths now enforce: <b>a chunk that is present in
    /// <c>WorldData.Chunks</c> but not <c>IsPopulated</c> holds no voxel data, so every read of it must resolve to
    /// void — never to <c>Air</c></b>. Nothing about that is automatic: <c>ChunkData.GetVoxel</c> returns 0 for a
    /// null section and <c>ChunkData.FillJobVoxelMap</c> zero-fills them, so a placeholder looks like a clean
    /// column of air to any reader that only null-checks.
    /// </para>
    /// <para><b>Prove-red:</b> drop <c>neighbor.IsPopulated</c> from <c>FluidBurstTicker.PrepareNeighbors</c> (reds
    /// BH-B8) or the <c>IsPopulated</c> early-out in <c>WorldData.TryGetVoxel</c> (reds BH-B9). Both were observed
    /// red in exactly that form before the fix landed.</para>
    /// </summary>
    public static partial class BehaviorValidationSuite
    {
        // Placeholder-neighbor fixture geometry. The center origin matches the BH-4 fixtures (interior, so every
        // neighbor coord is in-world) and the floor/source layout is the minimal ocean-edge model: a source that
        // cannot fall (solid below) and cannot be starved (it is a source), sitting on the −X seam, with the −X
        // neighbor held as an unpopulated placeholder.
        private const int PLACEHOLDER_FLOOR_Y = 10;
        private const int PLACEHOLDER_LANE_Z = 8;

        /// <summary>
        /// Builds the placeholder-neighbor fixture: a water source at local (0, <see cref="PLACEHOLDER_FLOOR_Y"/>+1,
        /// <see cref="PLACEHOLDER_LANE_Z"/>) over a stone floor, with the −X neighbor registered as an empty,
        /// unpopulated placeholder (the state every load-distance coord sits in until its terrain job lands).
        /// </summary>
        private static BehaviorTestWorld BuildPlaceholderNeighborWorld()
        {
            BehaviorTestWorld world = new BehaviorTestWorld(s_bh4CenterOrigin);

            for (int x = 0; x <= 3; x++)
            for (int z = PLACEHOLDER_LANE_Z - 2; z <= PLACEHOLDER_LANE_Z + 2; z++)
                world.SetBlock(x, PLACEHOLDER_FLOOR_Y, z, BlockIDs.Stone);

            world.SetBlock(0, PLACEHOLDER_FLOOR_Y + 1, PLACEHOLDER_LANE_Z, BlockIDs.Water, meta: 0);
            world.AddNeighborPlaceholder(-1, 0);
            return world;
        }

        /// <summary>
        /// Runs one tick and returns every emitted mod whose target lies OUTSIDE the center chunk on −X, i.e.
        /// inside the placeholder neighbor. The correct count is zero.
        /// </summary>
        /// <param name="driver">Which tick driver to model (the shipped Burst path or the managed path).</param>
        /// <param name="detail">A readable dump of the offending mods, empty when there are none.</param>
        /// <returns>The number of mods targeting the placeholder chunk.</returns>
        private static int CountModsIntoPlaceholder(TickDriver driver, out string detail)
        {
            List<string> offenders = new List<string>();

            using (BehaviorTestWorld world = BuildPlaceholderNeighborWorld())
            {
                world.Driver = driver;
                BehaviorSnapshot snapshot = world.RunTicks(1);

                foreach (TickRecord tick in snapshot.Ticks)
                foreach (VoxelEval eval in tick.Evals)
                {
                    if (eval.Mods == null) continue;
                    foreach (VoxelMod mod in eval.Mods)
                        if (mod.GlobalPosition.x < s_bh4CenterOrigin.x)
                            offenders.Add(BehaviorSnapshot.FormatMod(mod));
                }
            }

            detail = offenders.Count == 0 ? string.Empty : string.Join(", ", offenders);
            return offenders.Count;
        }

        /// <summary>
        /// BH-B8 — on the shipped Burst path, a water source on the −X seam must NOT spread into an unpopulated
        /// placeholder neighbor. Before the fix this emitted <c>19@(127,11,136):01</c> — Water at fluid level 1,
        /// one cell inside the placeholder — which <c>World.ApplyModifications</c> then persisted via
        /// <c>ModManager.AddPendingMod</c> and replayed over the neighbor's real terrain once it generated.
        /// </summary>
        private static bool Bh8_NoSpreadIntoPlaceholderBurst()
        {
            int count = CountModsIntoPlaceholder(TickDriver.FluidBurstHaloBand, out string detail);
            if (count == 0)
            {
                Debug.Log("[PASS] BH-B8: no fluid mods emitted into the unpopulated placeholder neighbor (Burst path).");
                return true;
            }

            Debug.LogError($"[FAIL] BH-B8: {count.ToString()} fluid mod(s) flowed into the unpopulated placeholder " +
                           $"neighbor (Burst path). Offenders: {detail}");
            return false;
        }

        /// <summary>
        /// BH-B9 — the same assertion on the managed path (<see cref="TickDriver.Legacy"/>). The defect was never
        /// Burst-specific: <c>WorldData.TryGetVoxel</c> resolved a placeholder and <c>ChunkData.GetVoxel</c>
        /// returned 0 for its null sections, so the managed reader saw the same phantom air. Because both paths
        /// agreed, the BH-D1 differential was blind to it — which is why this needs its own scenario, and why
        /// fixing only one path would red BH-D1 rather than turning these green.
        /// </summary>
        private static bool Bh9_NoSpreadIntoPlaceholderManaged()
        {
            int count = CountModsIntoPlaceholder(TickDriver.Legacy, out string detail);
            if (count == 0)
            {
                Debug.Log("[PASS] BH-B9: no fluid mods emitted into the unpopulated placeholder neighbor (managed path).");
                return true;
            }

            Debug.LogError($"[FAIL] BH-B9: {count.ToString()} fluid mod(s) flowed into the unpopulated placeholder " +
                           $"neighbor (managed path). Offenders: {detail}");
            return false;
        }
    }
}
