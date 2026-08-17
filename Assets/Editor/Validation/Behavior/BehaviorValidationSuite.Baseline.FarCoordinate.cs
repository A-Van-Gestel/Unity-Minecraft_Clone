using Data;
using Editor.Validation.Behavior.Framework;
using Jobs.BurstData;
using UnityEngine;

namespace Editor.Validation.Behavior
{
    /// <summary>
    /// <b>BH-B12</b> — cross-chunk behavior reads must resolve the correct neighbor voxel in the <b>far lands</b>.
    /// <para><c>ChunkData.GetState</c> routes an out-of-chunk read to the world; it used to do so by building a
    /// <see cref="Vector3"/> from integer coordinates, which rounds the cell away past ±2²⁴ and silently resolved a
    /// different chunk entirely (BLOCK_BEHAVIOR #05, the read-side twin of the Fluid #17 wake-side bug).</para>
    /// </summary>
    public static partial class BehaviorValidationSuite
    {
        /// <summary>
        /// A chunk-aligned voxel origin past ±2²⁴ where <c>float</c> has a 128-unit ULP, so a cross-seam read one
        /// voxel west of the chunk origin rounds ~3 chunks away instead of into the seeded neighbor. Chosen to match
        /// the coordinate Fluid #17 was reported at (<c>/teleport 2147000000</c>), and chunk-aligned because
        /// <see cref="BehaviorTestWorld"/> takes a chunk's voxel origin.
        /// </summary>
        private static readonly Vector2Int s_farCenterOrigin = new Vector2Int(2147000000, 128);

        /// <summary>
        /// Builds the BH-B12 fixture at the given center origin: grass on the −X seam whose <b>only</b> possible
        /// spread target is convertible dirt diagonally up across that seam, in a real (seeded, populated) neighbor.
        /// Mirrors <c>BH-B11</c>'s geometry, but the neighbor is seeded up front rather than populated mid-scenario —
        /// here the question is whether the cross-seam read lands on the right voxel at all, not whether a wake fires.
        /// </summary>
        /// <param name="centerOrigin">The center chunk's voxel origin.</param>
        /// <returns>A live fixture world; the caller owns disposal.</returns>
        private static BehaviorTestWorld BuildFarSeamGrassWorld(Vector2Int centerOrigin)
        {
            BehaviorTestWorld world = new BehaviorTestWorld(centerOrigin);
            world.Driver = TickDriver.FluidBurstHaloBand;

            // Grass on the −X seam sitting on stone, boxed in by stone in-chunk so no in-chunk cell is convertible.
            // Air above the grass keeps it grass (grass under a solid turns to dirt and would confound the assertion).
            world.SetBlock(0, PLACEHOLDER_FLOOR_Y, PLACEHOLDER_LANE_Z, BlockIDs.Stone);
            world.SetBlock(1, PLACEHOLDER_FLOOR_Y + 1, PLACEHOLDER_LANE_Z, BlockIDs.Stone);
            world.SetBlock(0, PLACEHOLDER_FLOOR_Y + 1, PLACEHOLDER_LANE_Z - 1, BlockIDs.Stone);
            world.SetBlock(0, PLACEHOLDER_FLOOR_Y + 1, PLACEHOLDER_LANE_Z + 1, BlockIDs.Stone);
            world.SetBlock(0, PLACEHOLDER_FLOOR_Y + 1, PLACEHOLDER_LANE_Z, BlockIDs.Grass);

            // The −X neighbor's facing column: convertible dirt diagonally UP across the seam (air above it, since
            // the neighbor is otherwise all air), plus stone at the grass's own Y so the same-Y read cannot admit it.
            world.SetNeighborBlock(-1, 0, VoxelData.ChunkWidth - 1, PLACEHOLDER_FLOOR_Y + 2, PLACEHOLDER_LANE_Z,
                BlockIDs.Dirt);
            world.SetNeighborBlock(-1, 0, VoxelData.ChunkWidth - 1, PLACEHOLDER_FLOOR_Y + 1, PLACEHOLDER_LANE_Z,
                BlockIDs.Stone);

            return world;
        }

        /// <summary>
        /// BH-B12 — the same fixture is asserted at the world origin (control) and in the far lands (the test). The
        /// grass must stay <b>active</b> in both: its only spread target lives across the seam, so it can only
        /// evaluate active if the cross-chunk read resolved the seeded neighbor.
        /// <para>The near/far pairing is the point. A far-only assertion cannot distinguish "the far read is broken"
        /// from "the fixture geometry never made the grass active anywhere", and — per the WS-4 lesson that at origin
        /// (0,0) a missed conversion is invisible — only the far half has teeth. Both halves must agree.</para>
        /// <para><b>Prove-red:</b> restore <c>ChunkData.GetState</c>'s <c>new Vector3(...)</c> construction. The
        /// control stays green (float is exact at small coordinates) and the far half goes red.</para>
        /// </summary>
        /// <returns>True when both the control and the far assertion hold.</returns>
        private static bool Bh12_FarCoordinateSeamReadResolves()
        {
            bool passed;

            using (BehaviorTestWorld control = BuildFarSeamGrassWorld(s_bh4CenterOrigin))
            {
                control.RunTicks(1);
                passed = Check("BH-B12 control (origin chunk) — grass active from its across-seam target",
                    control.ActiveVoxelCount > 0,
                    "the near-origin fixture left the active set empty, so the geometry never made the grass " +
                    "active and the far assertion below would prove nothing");
            }

            using (BehaviorTestWorld far = BuildFarSeamGrassWorld(s_farCenterOrigin))
            {
                far.RunTicks(1);
                passed &= Check($"BH-B12 far lands (x={s_farCenterOrigin.x.ToString()}) — grass active from its " +
                                "across-seam target", far.ActiveVoxelCount > 0,
                    "the cross-chunk read resolved the wrong chunk past ±2²⁴, so the across-seam dirt was invisible " +
                    "and the grass evaluated inactive (BLOCK_BEHAVIOR #05)");
            }

            return passed;
        }
    }
}
