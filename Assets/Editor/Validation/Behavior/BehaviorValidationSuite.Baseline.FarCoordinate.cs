using Data;
using Editor.Validation.Behavior.Framework;
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
        // The far anchor is declared as a CHUNK INDEX and multiplied up, never as a voxel coordinate divided down:
        // a chunk's voxel origin must be chunk-aligned for BehaviorTestWorld's local↔voxel mapping to mean anything,
        // and deriving it this way makes misalignment unrepresentable instead of merely asserted. Chosen to land on
        // voxel x = 2,147,000,000 — the coordinate Fluid #17 was reported at (/teleport 2147000000), where float's
        // ULP is 128, so a read one voxel west of the origin rounds several chunks away.
        // Headroom check: 134,187,500 × 16 = 2,147,000,000, which is 483,647 voxels below int.MaxValue — the
        // multiply below is where an overflow would hide, so keep that margin in mind before raising this.
        private const int FAR_CHUNK_X = 134187500;
        private const int FAR_CHUNK_Z = 8;

        /// <summary>The far-lands center chunk's voxel origin — chunk-aligned by construction (see the note above).</summary>
        private static readonly Vector2Int s_farCenterOrigin =
            new Vector2Int(FAR_CHUNK_X * VoxelData.ChunkWidth, FAR_CHUNK_Z * VoxelData.ChunkWidth);

        /// <summary>
        /// Builds the BH-B12 fixture at the given center origin: grass on the −X seam whose <b>only</b> possible
        /// spread target is convertible dirt diagonally up across that seam.
        /// </summary>
        /// <param name="centerOrigin">The center chunk's voxel origin.</param>
        /// <param name="seedNeighbor">
        /// When true the −X neighbor is seeded (populated) with that dirt target, so the grass has exactly one
        /// reachable target and must evaluate active. When false the neighbor chunk is left <b>absent</b>, so the
        /// cross-seam read resolves to void and the grass must evaluate inactive — the control that proves the
        /// activity in the seeded case comes from the seam read and not from some unrelated grass predicate.
        /// </param>
        /// <returns>A live fixture world; the caller owns disposal.</returns>
        private static BehaviorTestWorld BuildFarSeamGrassWorld(Vector2Int centerOrigin, bool seedNeighbor)
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

            if (!seedNeighbor) return world;

            // The −X neighbor's facing column: convertible dirt diagonally UP across the seam (air above it, since
            // the neighbor is otherwise all air), plus stone at the grass's own Y so the same-Y read cannot admit it.
            world.SetNeighborBlock(-1, 0, VoxelData.ChunkWidth - 1, PLACEHOLDER_FLOOR_Y + 2, PLACEHOLDER_LANE_Z,
                BlockIDs.Dirt);
            world.SetNeighborBlock(-1, 0, VoxelData.ChunkWidth - 1, PLACEHOLDER_FLOOR_Y + 1, PLACEHOLDER_LANE_Z,
                BlockIDs.Stone);

            return world;
        }

        /// <summary>
        /// BH-B12 — three legs, because a single "the grass is active far out" assertion cannot distinguish a working
        /// cross-seam read from a fixture that would have been active anyway:
        /// <list type="number">
        /// <item><b>control</b> — seeded at the world origin: active (proves the geometry produces activity at all,
        /// and that float is exact at small coordinates);</item>
        /// <item><b>the test</b> — seeded in the far lands: active (the read must resolve the seeded neighbor);</item>
        /// <item><b>non-vacuity</b> — <i>unseeded</i> in the far lands: inactive (proves leg 2's activity is caused by
        /// the across-seam dirt, so the assertion cannot pass for an unrelated reason).</item>
        /// </list>
        /// Per the WS-4 lesson that at origin (0,0) a missed conversion is invisible, only leg 2 has teeth — legs 1
        /// and 3 exist to keep it honest.
        /// <para><b>Prove-red:</b> restore <c>ChunkData.GetState</c>'s <c>new Vector3(...)</c> construction. Legs 1
        /// and 3 stay green and leg 2 goes red.</para>
        /// </summary>
        /// <returns>True when all three legs hold.</returns>
        private static bool Bh12_FarCoordinateSeamReadResolves()
        {
            bool passed;

            using (BehaviorTestWorld control = BuildFarSeamGrassWorld(s_bh4CenterOrigin, seedNeighbor: true))
            {
                control.RunTicks(1);
                passed = Check("BH-B12 control (origin chunk) — grass active from its across-seam target",
                    control.ActiveVoxelCount > 0,
                    "the near-origin fixture left the active set empty, so the geometry never made the grass " +
                    "active and the far assertion below would prove nothing");
            }

            using (BehaviorTestWorld far = BuildFarSeamGrassWorld(s_farCenterOrigin, seedNeighbor: true))
            {
                far.RunTicks(1);
                passed &= Check($"BH-B12 far lands (x={s_farCenterOrigin.x.ToString()}) — grass active from its " +
                                "across-seam target", far.ActiveVoxelCount > 0,
                    "the cross-chunk read resolved the wrong chunk past ±2²⁴, so the across-seam dirt was invisible " +
                    "and the grass evaluated inactive (BLOCK_BEHAVIOR #05)");
            }

            using (BehaviorTestWorld unseeded = BuildFarSeamGrassWorld(s_farCenterOrigin, seedNeighbor: false))
            {
                unseeded.RunTicks(1);
                passed &= Check("BH-B12 far lands, neighbor absent — grass inactive (non-vacuity control)",
                    unseeded.ActiveVoxelCount == 0,
                    "the grass evaluated ACTIVE with no across-seam target, so the seeded legs above prove nothing " +
                    "about the cross-chunk read — something else is keeping this grass awake");
            }

            return passed;
        }
    }
}
