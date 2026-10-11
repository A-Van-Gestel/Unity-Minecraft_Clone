using System.Collections.Generic;
using System.Text;
using Data;
using Editor.Validation.Behavior.Framework;
using Helpers;
using Jobs.BurstData;
using UnityEngine;
using Scenario = Editor.Validation.Framework.Scenario;

namespace Editor.Validation.Behavior
{
    /// <summary>
    /// <b>BH-B16–B18</b> — the ES-31 registration screen (<see cref="ActiveVoxelScreen"/>) and the two-sided population
    /// seam wake it relies on. A screened voxel ticks again only when a wake re-registers it, so the screen must drop
    /// nothing the tick would act on:
    /// <list type="bullet">
    /// <item><b>BH-B16</b> — soundness: every voxel the screen and the population wake leave unregistered is one the
    /// real tick jobs evaluate as inactive with no mods, over every differential fixture plus the screen fixtures.</item>
    /// <item><b>BH-B17</b> — screened-then-tick is §4.3-equivalent to blanket-then-tick, with byte-identical final
    /// state, on the managed and the Burst drivers.</item>
    /// <item><b>BH-B18</b> — the population wake covers both sides of a seam and spares a source facing a source.</item>
    /// <item><b>BH-B19</b> — the pending-modification replay, which bypasses <c>ApplyModifications</c>, still wakes the
    /// six neighbors of each cell it changes.</item>
    /// </list>
    /// <para><b>Prove-red:</b> drop the <c>REPLACEABLE</c> term from <see cref="ActiveVoxelScreen.CanSourceFlowInto"/>
    /// (reds BH-B16 on BH-S-RANDOM's blades), or the second <c>WakeSeamSlab</c> call in
    /// <c>World.WakeSeamBehaviorNeighborhood</c> (reds BH-B16 on the seam pocket and BH-B18's own-side legs), or the
    /// <c>WakeActiveNeighbors</c> call in <c>World.ReplayPendingMods</c> (reds BH-B19).</para>
    /// </summary>
    public static partial class BehaviorValidationSuite
    {
        private const int BHS_TICKS = 8;
        private const int BHS_RANDOM_SEED = 3131;
        private const int BHS_SEA_Y = 16;
        private const int BHS_MIN_FLOOR_Y = 6;
        private const int BHS_FLOOR_SPAN = 7;
        private const double BHS_LAND_CHANCE = 0.12;
        private const double BHS_LAVA_CHANCE = 0.04;
        private const double BHS_BLADES_CHANCE = 0.08;
        private const double BHS_POCKET_CHANCE = 0.06;
        private const double BHS_FLOWING_CHANCE = 0.05;
        private const int BHS_LAND_RISE = 3;
        private const byte BHS_FALLING_LEVEL = BurstVoxelDataBitMapping.FLUID_FALLING_FLAG + 1;
        private const int BHS_POCKET_Z = 7;
        private const int BHS_POCKET_Y = 12;
        private const int BHS_OCEAN_FLOOR_Y = 8;

        // A flat ocean bordered by ocean on all four sides: everything is inert, edges included, so the screen and the
        // source-facing-source gate must drop nearly all of it (the ES-31 win this fixture pins).
        private const double BHS_OCEAN_MIN_SCREENED_SHARE = 0.95;
        private const int BHS_MIN_SCREENED_TOTAL = 2000;

        // BH-B18 geometry: a source boxed in on its three in-chunk sides at the −X seam.
        private const int BH18_FLOOR_Y = 10;
        private const int BH18_LANE_Z = 8;

        /// <summary>The ES-31 screen fixtures, run alongside <see cref="DifferentialFixtures"/>.</summary>
        private static List<DiffFixture> ScreenFixtures() => new List<DiffFixture>
        {
            new DiffFixture("BH-S-RANDOM", BuildBhsRandomWorld, BHS_TICKS),
            new DiffFixture("BH-S-OCEAN", BuildBhsOceanWorld, BHS_TICKS),
        };

        /// <summary>Registers the ES-31 scenarios (called from <see cref="AddBaselineScenarios"/>).</summary>
        private static void AddRegistrationScreenScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario("BH-B16: the registration screen drops only voxels the tick would drop unchanged",
                Bh16_ScreenIsSound));
            scenarios.Add(new Scenario("BH-B17: screened-then-tick equals blanket-then-tick (managed + Burst drivers)",
                Bh17_ScreenedEqualsBlanket));
            scenarios.Add(new Scenario("BH-B18: the population seam wake covers both sides and spares source-facing-source",
                Bh18_TwoSidedSeamWake));
            scenarios.Add(new Scenario("BH-B19: the pending-modification replay wakes a screened neighbor",
                Bh19_PendingReplayWakes));
        }

        private static List<DiffFixture> AllScreenFixtures()
        {
            List<DiffFixture> fixtures = DifferentialFixtures();
            fixtures.AddRange(ScreenFixtures());
            return fixtures;
        }

        /// <summary>
        /// BH-B16 — soundness. For each fixture, a blanket world runs one tick on the real jobs; a second build of the
        /// same fixture applies the screen and the population wake. Every voxel the second leaves unregistered must have
        /// evaluated inactive with no mods in the first. Floors keep it from passing on a screen that drops nothing.
        /// </summary>
        private static bool Bh16_ScreenIsSound()
        {
            bool ok = true;
            int screenedTotal = 0;
            StringBuilder summary = new StringBuilder();

            foreach (DiffFixture f in AllScreenFixtures())
            {
                HashSet<Vector3Int> registered;
                int blanketCount;
                using (BehaviorTestWorld screened = f.Build())
                {
                    blanketCount = screened.ActiveVoxelCount;
                    registered = screened.ApplyRegistrationScreen();
                }

                List<string> offenders = new List<string>();
                using (BehaviorTestWorld blanket = f.Build())
                {
                    blanket.Driver = TickDriver.BurstHaloBand;
                    BehaviorSnapshot snapshot = blanket.RunTicks(1);
                    foreach (VoxelEval eval in snapshot.Ticks[0].Evals)
                    {
                        if (registered.Contains(eval.Pos)) continue;
                        bool emitted = eval.Mods != null && eval.Mods.Count > 0;
                        if (eval.Active || emitted)
                            offenders.Add($"{eval.Pos} active={eval.Active} mods={(eval.Mods?.Count ?? 0).ToString()}");
                    }
                }

                int screenedCount = blanketCount - registered.Count;
                screenedTotal += screenedCount;
                summary.Append($" {f.Name} {screenedCount.ToString()}/{blanketCount.ToString()};");

                if (offenders.Count > 0)
                {
                    Debug.LogError($"[FAIL] BH-B16 {f.Name}: {offenders.Count.ToString()} screened voxel(s) the tick acts on: " +
                                   string.Join(", ", offenders.GetRange(0, Mathf.Min(10, offenders.Count))));
                    ok = false;
                }

                if (f.Name == "BH-S-OCEAN" && screenedCount < BHS_OCEAN_MIN_SCREENED_SHARE * blanketCount)
                {
                    Debug.LogError($"[FAIL] BH-B16 {f.Name}: screened {screenedCount.ToString()} of {blanketCount.ToString()} " +
                                   $"(floor {BHS_OCEAN_MIN_SCREENED_SHARE:P0}) — an inert ocean stayed registered.");
                    ok = false;
                }
            }

            ok &= Check("BH-B16 the screen dropped voxels (non-vacuity)", screenedTotal >= BHS_MIN_SCREENED_TOTAL,
                $"only {screenedTotal.ToString()} voxels screened over all fixtures (floor {BHS_MIN_SCREENED_TOTAL.ToString()})");

            if (ok)
                Debug.Log($"[PASS] BH-B16: every screened voxel is inert on the real jobs; screened/blanket:{summary}");
            return ok;
        }

        /// <summary>
        /// BH-B17 — blanket vs screened registration over every fixture, on the managed and the Burst drivers. Both legs
        /// evaluate in canonical position order: the screened leg's bucket has a different removal history, and a
        /// history-dependent order would let two same-target writes swap — a difference production's own buckets show
        /// across any reload, not one the screen introduces.
        /// </summary>
        private static bool Bh17_ScreenedEqualsBlanket()
        {
            bool ok = true;
            List<DiffFixture> fixtures = AllScreenFixtures();
            foreach (TickDriver driver in new[] { TickDriver.Legacy, TickDriver.BurstHaloBand })
            foreach (DiffFixture f in fixtures)
            {
                BehaviorSnapshot snapA, snapB;
                string stateA, stateB;
                using (BehaviorTestWorld world = f.Build())
                {
                    world.Driver = driver;
                    world.CanonicalOrder = true;
                    snapA = world.RunTicks(f.Ticks);
                    stateA = world.DumpVoxels();
                }

                using (BehaviorTestWorld world = f.Build())
                {
                    world.Driver = driver;
                    world.CanonicalOrder = true;
                    world.ApplyRegistrationScreen();
                    snapB = world.RunTicks(f.Ticks);
                    stateB = world.DumpVoxels();
                }

                ok &= BehaviorDifferential.AssertEquivalent($"BH-B17[{driver}]/{f.Name}", snapA, snapB, stateA, stateB);
            }

            if (ok)
                Debug.Log($"[PASS] BH-B17: all {fixtures.Count.ToString()} fixtures equivalent, blanket vs screened, on both drivers.");
            return ok;
        }

        /// <summary>
        /// BH-B18 — the population wake on a −X seam, both directions. The center holds a boxed source at x=0 that the
        /// in-chunk screen drops; the −X neighbor holds boxed sources at x=15. Across an open cell each side must be
        /// woken; across a source of the same fluid neither may be.
        /// </summary>
        private static bool Bh18_TwoSidedSeamWake()
        {
            bool passed = true;
            foreach (bool open in new[] { true, false })
            {
                string label = open ? "open" : "source";
                using BehaviorTestWorld world = new BehaviorTestWorld(s_bh4CenterOrigin);

                // Center: a source at x=0 walled in on +X/−Z/+Z over a floor — inert inside its chunk.
                world.SetBlock(0, BH18_FLOOR_Y, BH18_LANE_Z, BlockIDs.Stone);
                world.SetBlock(1, BH18_FLOOR_Y + 1, BH18_LANE_Z, BlockIDs.Stone);
                world.SetBlock(0, BH18_FLOOR_Y + 1, BH18_LANE_Z - 1, BlockIDs.Stone);
                world.SetBlock(0, BH18_FLOOR_Y + 1, BH18_LANE_Z + 1, BlockIDs.Stone);
                world.SetBlock(0, BH18_FLOOR_Y + 1, BH18_LANE_Z, BlockIDs.Water, meta: 0);

                // −X neighbor: the mirror box at x=15, one lane over, plus the cell across from each source.
                const int last = VoxelData.ChunkWidth - 1;
                int otherLane = BH18_LANE_Z + 3;
                world.SetNeighborBlock(-1, 0, last, BH18_FLOOR_Y, otherLane, BlockIDs.Stone);
                world.SetNeighborBlock(-1, 0, last - 1, BH18_FLOOR_Y + 1, otherLane, BlockIDs.Stone);
                world.SetNeighborBlock(-1, 0, last, BH18_FLOOR_Y + 1, otherLane - 1, BlockIDs.Stone);
                world.SetNeighborBlock(-1, 0, last, BH18_FLOOR_Y + 1, otherLane + 1, BlockIDs.Stone);
                world.SetNeighborBlock(-1, 0, last, BH18_FLOOR_Y + 1, otherLane, BlockIDs.Water, meta: 0);
                world.SetNeighborBlock(-1, 0, last, BH18_FLOOR_Y + 1, BH18_LANE_Z, open ? BlockIDs.Air : BlockIDs.Water, meta: 0);
                if (open) world.SetNeighborBlock(-1, 0, last, BH18_FLOOR_Y, BH18_LANE_Z, BlockIDs.Stone);
                world.SetBlock(0, BH18_FLOOR_Y + 1, otherLane, open ? BlockIDs.Air : BlockIDs.Water, meta: 0);
                if (open) world.SetBlock(0, BH18_FLOOR_Y, otherLane, BlockIDs.Stone);

                Vector3Int centerSource = new Vector3Int(0, BH18_FLOOR_Y + 1, BH18_LANE_Z);
                ChunkDataScreenSource source = new ChunkDataScreenSource(world.ChunkData, World.Instance.BlockTypes);
                passed &= Check($"BH-B18 [{label}] premise: the in-chunk screen drops the center's seam source",
                    !ActiveVoxelScreen.MustRegister(ref source, centerSource.x, centerSource.y, centerSource.z,
                        world.ChunkData.GetVoxel(centerSource.x, centerSource.y, centerSource.z)),
                    "the boxed source was not screened, so the wake below would be untested");

                HashSet<Vector3Int> registered = world.ApplyRegistrationScreen();
                passed &= Check($"BH-B18 [{label}] own side: the center's seam source {(open ? "is" : "is not")} woken",
                    registered.Contains(centerSource) == open,
                    $"registered={registered.Contains(centerSource)} across {(open ? "an open cell" : "a water source")}");

                Vector2Int neighborOrigin = new Vector2Int(s_bh4CenterOrigin.x - VoxelData.ChunkWidth, s_bh4CenterOrigin.y);
                world.WorldData.TryGetChunk(neighborOrigin, out ChunkData neighbor);
                bool neighborWoken = neighbor.IsVoxelActive(new Vector3Int(last, BH18_FLOOR_Y + 1, otherLane));
                passed &= Check($"BH-B18 [{label}] neighbor side: the −X seam source {(open ? "is" : "is not")} woken",
                    neighborWoken == open,
                    $"woken={neighborWoken} across {(open ? "an open cell" : "a water source")}");
            }

            if (passed)
                Debug.Log("[PASS] BH-B18: the population wake woke both seam sources across an open cell and neither across a source.");
            return passed;
        }

        /// <summary>
        /// BH-B19 — a source boxed in on all four sides inside the center, screened at registration; a pending
        /// modification then opens its +X wall. The replay must re-register the source, or it never flows.
        /// </summary>
        private static bool Bh19_PendingReplayWakes()
        {
            using BehaviorTestWorld world = new BehaviorTestWorld(s_bh4CenterOrigin);
            world.EnableModifyVoxel();

            Vector3Int src = new Vector3Int(BH18_LANE_Z, BH18_FLOOR_Y + 1, BH18_LANE_Z);
            world.SetBlock(src.x, BH18_FLOOR_Y, src.z, BlockIDs.Stone);
            world.SetBlock(src.x + 1, BH18_FLOOR_Y, src.z, BlockIDs.Stone);
            world.SetBlock(src.x + 1, src.y, src.z, BlockIDs.Stone);
            world.SetBlock(src.x - 1, src.y, src.z, BlockIDs.Stone);
            world.SetBlock(src.x, src.y, src.z - 1, BlockIDs.Stone);
            world.SetBlock(src.x, src.y, src.z + 1, BlockIDs.Stone);
            world.SetBlock(src.x, src.y, src.z, BlockIDs.Water, meta: 0);

            world.ApplyRegistrationScreen();
            bool passed = Check("BH-B19 premise: the boxed source is screened", !world.ChunkData.IsVoxelActive(src),
                "the boxed source was registered, so the replay's wake would be untested");

            Vector3Int wall = new Vector3Int(s_bh4CenterOrigin.x + src.x + 1, src.y, s_bh4CenterOrigin.y + src.z);
            world.ReplayPendingMods(new List<VoxelMod> { new VoxelMod(wall, BlockIDs.Air) });

            passed &= Check("BH-B19 the replay re-registered the source beside the opened wall", world.ChunkData.IsVoxelActive(src),
                "the source stayed asleep after a replayed mod opened its wall");
            if (passed)
                Debug.Log("[PASS] BH-B19: the pending-modification replay woke the screened source beside the cell it changed.");
            return passed;
        }

        /// <summary>
        /// BH-S-RANDOM: seeded random seabed in the center and its −X/+X/−Z neighbors (+Z absent): water sources to a sea
        /// level over a varying stone floor, with flowing and falling cells, air pockets, grass blades under the water,
        /// lava columns, and land columns of dirt and grass above the sea. A fixed air pocket faces the center's −X seam
        /// sources, so the own-side wake has a cell to wake against.
        /// </summary>
        private static BehaviorTestWorld BuildBhsRandomWorld()
        {
            BehaviorTestWorld world = new BehaviorTestWorld(s_bh4CenterOrigin);
            System.Random rng = new System.Random(BHS_RANDOM_SEED);

            int[,] offsets = { { 0, 0 }, { -1, 0 }, { 1, 0 }, { 0, -1 } };
            for (int c = 0; c < offsets.GetLength(0); c++)
            {
                int dx = offsets[c, 0];
                int dz = offsets[c, 1];
                for (int x = 0; x < VoxelData.ChunkWidth; x++)
                for (int z = 0; z < VoxelData.ChunkWidth; z++)
                {
                    int floor = BHS_MIN_FLOOR_Y + rng.Next(BHS_FLOOR_SPAN);
                    for (int y = 0; y <= floor; y++)
                        Place(world, dx, dz, x, y, z, BlockIDs.Stone);

                    if (rng.NextDouble() < BHS_LAND_CHANCE)
                    {
                        int top = BHS_SEA_Y + 1 + rng.Next(BHS_LAND_RISE);
                        for (int y = floor + 1; y < top; y++)
                            Place(world, dx, dz, x, y, z, BlockIDs.Dirt);
                        Place(world, dx, dz, x, top, z, rng.NextDouble() < 0.5 ? BlockIDs.Grass : BlockIDs.Dirt);
                        continue;
                    }

                    ushort fluid = rng.NextDouble() < BHS_LAVA_CHANCE ? BlockIDs.Lava : BlockIDs.Water;
                    int pocketY = rng.NextDouble() < BHS_POCKET_CHANCE ? floor + 1 + rng.Next(BHS_SEA_Y - floor) : -1;
                    int flowY = rng.NextDouble() < BHS_FLOWING_CHANCE ? floor + 1 + rng.Next(BHS_SEA_Y - floor) : -1;
                    bool blades = rng.NextDouble() < BHS_BLADES_CHANCE;
                    byte flowLevel = rng.NextDouble() < 0.5 ? BHS_FALLING_LEVEL : (byte)(1 + rng.Next(6));

                    for (int y = floor + 1; y <= BHS_SEA_Y; y++)
                    {
                        if (y == pocketY) continue; // air
                        if (blades && y == floor + 1)
                        {
                            Place(world, dx, dz, x, y, z, BlockIDs.GrassBlades);
                            continue;
                        }

                        PlaceFluid(world, dx, dz, x, y, z, fluid, y == flowY ? flowLevel : (byte)0);
                    }
                }
            }

            // The seam pocket: an open cell in the −X neighbor across from a center source, floored so it holds.
            const int last = VoxelData.ChunkWidth - 1;
            world.SetNeighborBlock(-1, 0, last, BHS_POCKET_Y, BHS_POCKET_Z, BlockIDs.Air);
            world.SetNeighborBlock(-1, 0, last, BHS_POCKET_Y - 1, BHS_POCKET_Z, BlockIDs.Stone);
            for (int y = 0; y < BHS_POCKET_Y; y++)
                world.SetBlock(0, y, BHS_POCKET_Z, BlockIDs.Stone);
            world.SetBlock(1, BHS_POCKET_Y, BHS_POCKET_Z, BlockIDs.Stone);
            world.SetBlock(0, BHS_POCKET_Y, BHS_POCKET_Z - 1, BlockIDs.Stone);
            world.SetBlock(0, BHS_POCKET_Y, BHS_POCKET_Z + 1, BlockIDs.Stone);
            world.SetBlock(0, BHS_POCKET_Y, BHS_POCKET_Z, BlockIDs.Water, meta: 0);
            return world;
        }

        /// <summary>BH-S-OCEAN: a flat ocean of water sources over a stone floor in the center and all four neighbors.</summary>
        private static BehaviorTestWorld BuildBhsOceanWorld()
        {
            BehaviorTestWorld world = new BehaviorTestWorld(s_bh4CenterOrigin);
            int[,] offsets = { { 0, 0 }, { -1, 0 }, { 1, 0 }, { 0, -1 }, { 0, 1 } };
            for (int c = 0; c < offsets.GetLength(0); c++)
            for (int x = 0; x < VoxelData.ChunkWidth; x++)
            for (int z = 0; z < VoxelData.ChunkWidth; z++)
            {
                Place(world, offsets[c, 0], offsets[c, 1], x, BHS_OCEAN_FLOOR_Y, z, BlockIDs.Stone);
                for (int y = BHS_OCEAN_FLOOR_Y + 1; y <= BHS_SEA_Y; y++)
                    PlaceFluid(world, offsets[c, 0], offsets[c, 1], x, y, z, BlockIDs.Water, 0);
            }

            return world;
        }

        /// <summary>Writes a fluid at a level into the ticked center, or into a neighbor as read-only context.</summary>
        private static void PlaceFluid(BehaviorTestWorld world, int dx, int dz, int x, int y, int z, ushort id, byte level)
        {
            if (dx == 0 && dz == 0)
                world.SetBlock(x, y, z, id, level);
            else
                world.SetNeighborBlock(dx, dz, x, y, z, id, level);
        }
    }
}
