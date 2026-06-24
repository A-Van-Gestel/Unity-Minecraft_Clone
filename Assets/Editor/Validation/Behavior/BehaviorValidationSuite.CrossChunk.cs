using System.Collections.Generic;
using Data;
using Editor.Validation.Behavior.Framework;
using UnityEngine;

namespace Editor.Validation.Behavior
{
    /// <summary>
    /// <b>BH-4</b> — TG-4 Phase 4b cross-chunk (Tier-2 <b>border</b>) fluid differential fixtures. Each seeds a fluid
    /// source in the <b>border ring</b> (within the pathfinder's ≤4-cell reach of a chunk seam) of an <b>interior</b>
    /// center chunk, plus static neighbor read-context, so the border voxels resolve real neighbor data through the
    /// production <c>ChunkData.GetState → WorldData.GetVoxelState</c> path (see
    /// <see cref="BehaviorTestWorld.SetNeighborBlock"/>).
    /// <para>
    /// These fixtures are appended to <see cref="DifferentialFixtures"/>, so every BH-D1 driver pair runs them. Under
    /// the <b>current</b> drivers (<see cref="TickDriver.Legacy"/>, <see cref="TickDriver.SplitFamily"/>,
    /// <see cref="TickDriver.FluidBurstHybrid"/>) the border stays on the managed path in <em>both</em> sides, so they
    /// are byte-identical (green) — this proves the multi-chunk harness models cross-seam reads + emission faithfully.
    /// They become the real <b>prove-red → green</b> gate once Phase 4b adds the halo-fed all-fluids Burst driver
    /// (<c>BH-D1[L|H]</c>), whose border voxels must reproduce this exact stream from the gathered halo.
    /// </para>
    /// </summary>
    public static partial class BehaviorValidationSuite
    {
        // Interior center origin: all 8 neighbor coords stay in-world (WorldData.IsVoxelInWorld requires
        // x,z ∈ [0, WorldSizeInVoxels=1600)), so a chunk at the world origin could not read −X/−Z neighbors.
        private static readonly Vector2Int s_bh4CenterOrigin = new Vector2Int(128, 128);
        private const int BH4_FLOOR_Y = 10;
        private const int BH4_MID = 8; // mid-axis lane the seam scenarios center on
        private const int BH4_TICKS = 5; // enough for a border source to reach the seam and spill across

        /// <summary>The BH-4 cross-chunk fixtures, appended to <see cref="DifferentialFixtures"/>.</summary>
        private static List<DiffFixture> CrossChunkFixtures() => new List<DiffFixture>
        {
            new DiffFixture("BH-4-NX-SPREAD", BuildBh4NxSpreadWorld, BH4_TICKS),
            new DiffFixture("BH-4-PX-SPREAD", BuildBh4PxSpreadWorld, BH4_TICKS),
            new DiffFixture("BH-4-NZ-SPREAD", BuildBh4NzSpreadWorld, BH4_TICKS),
            new DiffFixture("BH-4-CORNER", BuildBh4CornerWorld, BH4_TICKS),
            new DiffFixture("BH-4-MISSING", BuildBh4MissingNeighborWorld, BH4_TICKS),
        };

        /// <summary>Floors a rectangular center region with stone at <see cref="BH4_FLOOR_Y"/> (inclusive bounds).</summary>
        private static void SeedCenterFloor(BehaviorTestWorld world, int x0, int x1, int z0, int z1)
        {
            for (int x = x0; x <= x1; x++)
            for (int z = z0; z <= z1; z++)
                world.SetBlock(x, BH4_FLOOR_Y, z, BlockIDs.Stone);
        }

        /// <summary>Floors a rectangular region (neighbor-local, inclusive bounds) of the (dChunkX, dChunkZ) neighbor.</summary>
        private static void SeedNeighborFloor(BehaviorTestWorld world, int dChunkX, int dChunkZ, int x0, int x1, int z0, int z1)
        {
            for (int x = x0; x <= x1; x++)
            for (int z = z0; z <= z1; z++)
                world.SetNeighborBlock(dChunkX, dChunkZ, x, BH4_FLOOR_Y, z, BlockIDs.Stone);
        }

        /// <summary>BH-4-NX: a border water source spreads west across the −X seam onto the floored −X neighbor.</summary>
        private static BehaviorTestWorld BuildBh4NxSpreadWorld()
        {
            BehaviorTestWorld world = new BehaviorTestWorld(s_bh4CenterOrigin);
            SeedCenterFloor(world, 0, 6, BH4_MID - 2, BH4_MID + 2);
            SeedNeighborFloor(world, -1, 0, 13, 15, BH4_MID - 2, BH4_MID + 2);
            world.SetBlock(2, BH4_FLOOR_Y + 1, BH4_MID, BlockIDs.Water, meta: 0); // source in the −X border ring
            return world;
        }

        /// <summary>BH-4-PX: mirror of NX toward the +X seam.</summary>
        private static BehaviorTestWorld BuildBh4PxSpreadWorld()
        {
            BehaviorTestWorld world = new BehaviorTestWorld(s_bh4CenterOrigin);
            SeedCenterFloor(world, 9, 15, BH4_MID - 2, BH4_MID + 2);
            SeedNeighborFloor(world, 1, 0, 0, 2, BH4_MID - 2, BH4_MID + 2);
            world.SetBlock(13, BH4_FLOOR_Y + 1, BH4_MID, BlockIDs.Water, meta: 0); // source in the +X border ring
            return world;
        }

        /// <summary>BH-4-NZ: a border water source spreads south across the −Z seam onto the floored −Z neighbor.</summary>
        private static BehaviorTestWorld BuildBh4NzSpreadWorld()
        {
            BehaviorTestWorld world = new BehaviorTestWorld(s_bh4CenterOrigin);
            SeedCenterFloor(world, BH4_MID - 2, BH4_MID + 2, 0, 6);
            SeedNeighborFloor(world, 0, -1, BH4_MID - 2, BH4_MID + 2, 13, 15);
            world.SetBlock(BH4_MID, BH4_FLOOR_Y + 1, 2, BlockIDs.Water, meta: 0); // source in the −Z border ring
            return world;
        }

        /// <summary>
        /// BH-4-CORNER: a source in the −X−Z corner ring, whose pathfinder/expected-level reads reach the −X, −Z,
        /// AND the diagonal −X−Z neighbor (the (±2,±2) diagonal read the square halo must cover). All three are
        /// floored so the corner reads resolve to real data.
        /// </summary>
        private static BehaviorTestWorld BuildBh4CornerWorld()
        {
            BehaviorTestWorld world = new BehaviorTestWorld(s_bh4CenterOrigin);
            SeedCenterFloor(world, 0, 6, 0, 6);
            SeedNeighborFloor(world, -1, 0, 13, 15, 0, 6); // −X edge
            SeedNeighborFloor(world, 0, -1, 0, 6, 13, 15); // −Z edge
            SeedNeighborFloor(world, -1, -1, 13, 15, 13, 15); // −X−Z diagonal corner
            world.SetBlock(2, BH4_FLOOR_Y + 1, 2, BlockIDs.Water, meta: 0); // source in the corner border ring
            return world;
        }

        /// <summary>
        /// BH-4-MISSING: identical center to BH-4-NX but the −X neighbor is left <b>unseeded</b>, so its coord
        /// resolves to null (void) — the missing/ungenerated-neighbor case the halo's <c>uint.MaxValue</c> sentinel
        /// must reproduce. The managed border reads it as void in both drivers (green).
        /// </summary>
        private static BehaviorTestWorld BuildBh4MissingNeighborWorld()
        {
            BehaviorTestWorld world = new BehaviorTestWorld(s_bh4CenterOrigin);
            SeedCenterFloor(world, 0, 6, BH4_MID - 2, BH4_MID + 2);
            world.SetBlock(2, BH4_FLOOR_Y + 1, BH4_MID, BlockIDs.Water, meta: 0); // −X neighbor deliberately not seeded
            return world;
        }
    }
}
