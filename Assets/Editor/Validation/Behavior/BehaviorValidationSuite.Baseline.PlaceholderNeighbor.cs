using System.Collections.Generic;
using Data;
using Editor.Validation.Behavior.Framework;
using Jobs.BurstData;
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
        /// BH-B10 — the wake half of the placeholder contract (<c>_FIXED_BUGS.md</c> Fluid §19): a source that
        /// correctly quiesced against a placeholder must resume when that neighbor populates with a receptive cell
        /// facing the seam. Nothing else re-registers it — population registers only the newly populated chunk's
        /// own voxels, and <c>ApplyModifications</c>'s cross-chunk wake needs an applied mod next to the sleeping
        /// cell — so without <c>World.WakeSeamBehaviorNeighborhood</c> the water never flows in.
        /// <para>The fixture walls the source in on all three in-chunk sides, so the placeholder seam is its
        /// <b>only</b> receptive direction: that is what makes it genuinely quiesce (asserted before the populate,
        /// so this can never pass vacuously against a source that simply stayed awake).</para>
        /// <para><b>Prove-red:</b> early-return from <c>World.WakeSeamBehaviorNeighborhood</c>; the post-populate
        /// assertion goes red while the pre-populate one stays green.</para>
        /// </summary>
        private static bool Bh10_SeamWakesWhenPlaceholderPopulates()
        {
            using BehaviorTestWorld world = new BehaviorTestWorld(s_bh4CenterOrigin);
            world.Driver = TickDriver.FluidBurstHaloBand;

            // Source at the −X seam, boxed in on every in-chunk side so the placeholder is its only way out.
            world.SetBlock(0, PLACEHOLDER_FLOOR_Y, PLACEHOLDER_LANE_Z, BlockIDs.Stone); // floor
            world.SetBlock(1, PLACEHOLDER_FLOOR_Y + 1, PLACEHOLDER_LANE_Z, BlockIDs.Stone); // +X wall
            world.SetBlock(0, PLACEHOLDER_FLOOR_Y + 1, PLACEHOLDER_LANE_Z - 1, BlockIDs.Stone); // −Z wall
            world.SetBlock(0, PLACEHOLDER_FLOOR_Y + 1, PLACEHOLDER_LANE_Z + 1, BlockIDs.Stone); // +Z wall
            world.SetBlock(0, PLACEHOLDER_FLOOR_Y + 1, PLACEHOLDER_LANE_Z, BlockIDs.Water, meta: 0);
            world.AddNeighborPlaceholder(-1, 0);

            // Tick 1: the void seam satisfies no spread test, so the source must drop out of the active set.
            world.RunTicks(1);
            bool passed = Check("BH-B10 source quiesced against the placeholder", world.ActiveVoxelCount == 0,
                $"expected 0 active voxels after ticking against a placeholder, got {world.ActiveVoxelCount.ToString()}");

            // The neighbor's terrain job lands: a floored, open cell directly across the seam.
            world.PopulateNeighborPlaceholder(-1, 0, neighbor =>
            {
                neighbor.SetVoxel(VoxelData.ChunkWidth - 1, PLACEHOLDER_FLOOR_Y, PLACEHOLDER_LANE_Z,
                    BurstVoxelDataBitMapping.PackVoxelData(BlockIDs.Stone, 0));
            });

            passed &= Check("BH-B10 seam wake re-registered the source", world.ActiveVoxelCount > 0,
                "the populate event left the active set empty — the seam wake did not run");

            // Tick 2: the woken source must now spread across the seam into the freshly generated cell.
            BehaviorSnapshot after = world.RunTicks(1);
            int intoNeighbor = 0;
            foreach (TickRecord tick in after.Ticks)
            foreach (VoxelEval eval in tick.Evals)
            {
                if (eval.Mods == null) continue;
                foreach (VoxelMod mod in eval.Mods)
                    if (mod.GlobalPosition.x < s_bh4CenterOrigin.x)
                        intoNeighbor++;
            }

            passed &= Check("BH-B10 water flows into the newly populated neighbor", intoNeighbor > 0,
                "no mod targeted the neighbor after it populated — the quiesced seam was never re-woken");

            return passed;
        }

        /// <summary>
        /// BH-B11 — the seam wake must cover <b>grass</b>, not just fluids. Grass's up-diagonal spread target
        /// (<c>s_grassSpreadVectors</c>' "Above Adjacent" entries → <c>IsConvertibleDirt(pos + dir + up)</c>) is
        /// <see cref="BlockIDs.Dirt"/>, which is <b>solid</b> — so a wake gate that samples only the same-Y cell
        /// across the seam skips the grass voxel, and it never wakes.
        /// <para>The fixture puts grass on the −X seam with every in-chunk neighbor non-convertible, so the only
        /// spread target it can ever have is diagonally up across the seam. Asserted inactive before the populate
        /// (the premise) and active after it.</para>
        /// <para><b>Prove-red:</b> drop the <c>CanReceiveGrassAbove</c> term from <c>SeamWakeDecision</c>'s gate —
        /// the same-Y sample sees stone, skips, and the post-populate assertion goes red.</para>
        /// </summary>
        private static bool Bh11_SeamWakeCoversGrass()
        {
            using BehaviorTestWorld world = new BehaviorTestWorld(s_bh4CenterOrigin);
            world.Driver = TickDriver.FluidBurstHaloBand;

            // Grass on the −X seam, sitting on stone, with stone (not dirt) all around it in-chunk so no in-chunk
            // target can keep it awake. Air above keeps the grass itself alive (grass under a solid turns to dirt).
            world.SetBlock(0, PLACEHOLDER_FLOOR_Y, PLACEHOLDER_LANE_Z, BlockIDs.Stone);
            world.SetBlock(1, PLACEHOLDER_FLOOR_Y + 1, PLACEHOLDER_LANE_Z, BlockIDs.Stone);
            world.SetBlock(0, PLACEHOLDER_FLOOR_Y + 1, PLACEHOLDER_LANE_Z - 1, BlockIDs.Stone);
            world.SetBlock(0, PLACEHOLDER_FLOOR_Y + 1, PLACEHOLDER_LANE_Z + 1, BlockIDs.Stone);
            world.SetBlock(0, PLACEHOLDER_FLOOR_Y + 1, PLACEHOLDER_LANE_Z, BlockIDs.Grass);
            world.AddNeighborPlaceholder(-1, 0);

            world.RunTicks(1);
            bool passed = Check("BH-B11 grass quiesced against the placeholder", world.ActiveVoxelCount == 0,
                $"expected 0 active voxels, got {world.ActiveVoxelCount.ToString()}");

            // The neighbor generates with convertible dirt DIAGONALLY UP across the seam: solid, so only the
            // y+1 gate sample can admit it. Air above the dirt is what makes it convertible.
            world.PopulateNeighborPlaceholder(-1, 0, neighbor =>
            {
                neighbor.SetVoxel(VoxelData.ChunkWidth - 1, PLACEHOLDER_FLOOR_Y + 2, PLACEHOLDER_LANE_Z,
                    BurstVoxelDataBitMapping.PackVoxelData(BlockIDs.Dirt, 0));
                // Stone at the same Y as the grass, so the same-Y gate sample rejects and only the y+1 sample
                // can wake it — without this the scenario would pass on the same-Y term and prove nothing.
                neighbor.SetVoxel(VoxelData.ChunkWidth - 1, PLACEHOLDER_FLOOR_Y + 1, PLACEHOLDER_LANE_Z,
                    BurstVoxelDataBitMapping.PackVoxelData(BlockIDs.Stone, 0));
            });

            passed &= Check("BH-B11 seam wake re-registered the grass", world.ActiveVoxelCount > 0,
                "the populate event left the active set empty — the gate's y+1 sample did not admit the grass");

            return passed;
        }

        /// <summary>Logs a pass/fail line for one BH-B10 assertion and returns the condition.</summary>
        /// <param name="label">The assertion label.</param>
        /// <param name="condition">The asserted condition.</param>
        /// <param name="failDetail">Detail appended to the failure log.</param>
        /// <returns><paramref name="condition"/>, unchanged.</returns>
        private static bool Check(string label, bool condition, string failDetail)
        {
            if (condition) Debug.Log($"[PASS] {label}.");
            else Debug.LogError($"[FAIL] {label} — {failDetail}");
            return condition;
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
