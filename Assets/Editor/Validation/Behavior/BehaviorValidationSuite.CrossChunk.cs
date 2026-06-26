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

        // TG-4 Phase 4b Y-band fixtures. BH-4-SPLIT-Y: a high cluster + the BH4_FLOOR_Y low cluster in ONE center chunk
        // → the active-fluid Y-band spans both (a single contiguous [minY−reach, maxY+reach]); proves the band's
        // min/max scan handles separated clusters. BH-4-BAND-EDGE: a source sitting on a y%16 section boundary with a
        // real voxel on BOTH its below- and above-read, so the band's lower AND upper ±reach edges each gate a
        // meaningful read across a section seam — the direct prove-red for the band's reach padding.
        private const int BH4_SPLIT_HIGH_FLOOR_Y = 70; // high cluster floor (low cluster stays at BH4_FLOOR_Y)
        private const int BH4_EDGE_FLOOR_Y = 15; // section-0 top; source sits at 16 (section-1 base)

        /// <summary>The BH-4 cross-chunk fixtures, appended to <see cref="DifferentialFixtures"/>.</summary>
        private static List<DiffFixture> CrossChunkFixtures() => new List<DiffFixture>
        {
            new DiffFixture("BH-4-NX-SPREAD", BuildBh4NxSpreadWorld, BH4_TICKS),
            new DiffFixture("BH-4-PX-SPREAD", BuildBh4PxSpreadWorld, BH4_TICKS),
            new DiffFixture("BH-4-NZ-SPREAD", BuildBh4NzSpreadWorld, BH4_TICKS),
            new DiffFixture("BH-4-CORNER", BuildBh4CornerWorld, BH4_TICKS),
            new DiffFixture("BH-4-MISSING", BuildBh4MissingNeighborWorld, BH4_TICKS),
            new DiffFixture("BH-4-SPLIT-Y", BuildBh4SplitYWorld, BH4_TICKS),
            new DiffFixture("BH-4-BAND-EDGE", BuildBh4BandEdgeWorld, BH4_TICKS),
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

        /// <summary>
        /// BH-4-SPLIT-Y (TG-4 Phase 4b Y-band): two −X-border water sources far apart in Y — one over the
        /// <see cref="BH4_FLOOR_Y"/> floor, one over a <see cref="BH4_SPLIT_HIGH_FLOOR_Y"/> floor — in the SAME center
        /// chunk. The active-fluid band therefore spans <c>[BH4_FLOOR_Y−reach, BH4_SPLIT_HIGH_FLOOR_Y+1+reach]</c>
        /// (one contiguous window covering both clusters and the gap between them). Both clusters reach the −X seam, so
        /// the halo drivers job-tick them; <c>BH-D1[H|HB]</c> proves the band (sized from the min/max scan over both
        /// clusters) gathers/reads identically to the full-height halo. A per-cluster band model would split the window
        /// and diverge here.
        /// </summary>
        private static BehaviorTestWorld BuildBh4SplitYWorld()
        {
            BehaviorTestWorld world = new BehaviorTestWorld(s_bh4CenterOrigin);

            // Low cluster over the BH4_FLOOR_Y floor; −X neighbor floored at the same level for the cross-seam spread.
            SeedCenterFloor(world, 0, 6, BH4_MID - 2, BH4_MID + 2);
            SeedNeighborFloor(world, -1, 0, 13, 15, BH4_MID - 2, BH4_MID + 2);
            world.SetBlock(2, BH4_FLOOR_Y + 1, BH4_MID, BlockIDs.Water, meta: 0);

            // High cluster over a separate floor near the top of the chunk; same −X edge, floored to match.
            for (int x = 0; x <= 6; x++)
            for (int z = BH4_MID - 2; z <= BH4_MID + 2; z++)
                world.SetBlock(x, BH4_SPLIT_HIGH_FLOOR_Y, z, BlockIDs.Stone);
            for (int x = 13; x <= 15; x++)
            for (int z = BH4_MID - 2; z <= BH4_MID + 2; z++)
                world.SetNeighborBlock(-1, 0, x, BH4_SPLIT_HIGH_FLOOR_Y, z, BlockIDs.Stone);
            world.SetBlock(2, BH4_SPLIT_HIGH_FLOOR_Y + 1, BH4_MID, BlockIDs.Water, meta: 0);

            return world;
        }

        /// <summary>
        /// BH-4-BAND-EDGE (TG-4 Phase 4b Y-band): a single −X-border water source at y=16 — the base of section 1 —
        /// with a stone floor at <see cref="BH4_EDGE_FLOOR_Y"/>=15 (top of section 0) AND a stone cap at y=17. The
        /// source's below-read (15) lands on the band's lower edge (<c>bandMinY = 16−reach = 15</c>) across the
        /// section-0/1 seam, and its above-read (17) lands on the band's upper edge across a section seam too — so
        /// both ±<c>FLUID_VERTICAL_REACH</c> band edges gate a real, behavior-relevant voxel. Dropping either reach
        /// pad turns that edge read into void and diverges: the direct prove-red target for the band sizing.
        /// </summary>
        private static BehaviorTestWorld BuildBh4BandEdgeWorld()
        {
            BehaviorTestWorld world = new BehaviorTestWorld(s_bh4CenterOrigin);

            for (int x = 0; x <= 6; x++)
            for (int z = BH4_MID - 2; z <= BH4_MID + 2; z++)
            {
                world.SetBlock(x, BH4_EDGE_FLOOR_Y, z, BlockIDs.Stone); // floor (band lower edge, below-read)
                world.SetBlock(x, BH4_EDGE_FLOOR_Y + 2, z, BlockIDs.Stone); // cap  (band upper edge, above-read)
            }

            for (int x = 13; x <= 15; x++)
            for (int z = BH4_MID - 2; z <= BH4_MID + 2; z++)
                world.SetNeighborBlock(-1, 0, x, BH4_EDGE_FLOOR_Y, z, BlockIDs.Stone);

            world.SetBlock(2, BH4_EDGE_FLOOR_Y + 1, BH4_MID, BlockIDs.Water, meta: 0); // source at y=16 (section seam)
            return world;
        }
    }
}
