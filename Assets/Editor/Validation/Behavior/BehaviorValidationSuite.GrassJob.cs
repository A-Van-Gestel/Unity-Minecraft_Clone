using System.Collections.Generic;
using Data;
using Editor.Validation.Behavior.Framework;
using UnityEngine;

namespace Editor.Validation.Behavior
{
    /// <summary>
    /// <b>BH-G</b> — ES-29 grass differential fixtures for the Burst grass tick (<c>GrassTickJob</c>). The golden grass
    /// worlds (BH-B6/B7) each hold one interior voxel, and grass spreads with a 2 % roll, so on their own they barely
    /// reach the job's reservoir sampling, its seam reads or its world bounds. These fixtures do: many grass voxels with
    /// live candidates over many ticks, every kind of cross-seam read (adjacent, up-diagonal, spread-down) into each
    /// cardinal neighbor, a missing and a placeholder neighbor, and grass at the top and bottom of the world.
    /// <para>
    /// Appended to <see cref="DifferentialFixtures"/>, so every BH-D1 driver pair runs them; under
    /// <c>BH-D1[L|HB]</c> the job's stream and active flags must equal the managed grass loop's tick for tick.
    /// Cross-chunk spreads are emitted but not applied (the harness ticks the center only), so seam candidates stay
    /// live and keep rolling.
    /// </para>
    /// </summary>
    public static partial class BehaviorValidationSuite
    {
        // Enough ticks that 2 % rolls over dozens of candidate-holding grass voxels fire many times.
        private const int BHG_TICKS = 48;
        private const int BHG_SEAM_Y = 20;
        private const int BHG_RANDOM_SEED = 2029;
        private const int BHG_RANDOM_MIN_SURFACE = 12;
        private const int BHG_RANDOM_SURFACE_SPAN = 9;
        private const int BHG_RANDOM_DIRT_DEPTH = 3;
        private const double BHG_RANDOM_GRASS_CHANCE = 0.75;
        private const double BHG_RANDOM_CAP_CHANCE = 0.1;
        private const int TOP_Y = VoxelData.ChunkHeight - 1;

        // BH-G-BAND-TOP: grass two rows under a section boundary, so its highest read (the air over up-diagonal dirt)
        // is the next section's first row — the one read that only the band's +2 reach pulls into the arena.
        private const int BHG_BAND_TOP_GRASS_Y = 14;

        // Floors the non-vacuity scenario holds each fixture to under the Burst driver: a fixture below them would let
        // BH-D1[L|HB] pass without the grass job, its roll or its seam reads doing anything.
        private const int BHG_MIN_JOB_TICKED = 100;
        private const int BHG_MIN_SPREADS = 3;

        /// <summary>The BH-G fixtures, appended to <see cref="DifferentialFixtures"/>.</summary>
        private static List<DiffFixture> GrassJobFixtures() => new List<DiffFixture>
        {
            new DiffFixture("BH-G-SEAMS", BuildBhgSeamsWorld, BHG_TICKS),
            new DiffFixture("BH-G-BOUNDS", BuildBhgBoundsWorld, BHG_TICKS),
            new DiffFixture("BH-G-RANDOM", BuildBhgRandomWorld, BHG_TICKS),
            new DiffFixture("BH-G-BAND-TOP", BuildBhgBandTopWorld, BHG_TICKS),
            new DiffFixture("BH-G-MIXED", BuildBhgMixedWorld, BHG_TICKS),
        };

        /// <summary>
        /// BH-G-SEAMS: a ring of grass along all four center borders, each side reading a different kind of candidate
        /// across its seam — −X: adjacent dirt; +X: up-diagonal dirt; −Z: spread-down (air with dirt below); +Z: a
        /// placeholder (unpopulated) neighbor, which reads as void. Corner cells read two neighbors at once.
        /// </summary>
        private static BehaviorTestWorld BuildBhgSeamsWorld()
        {
            const int last = VoxelData.ChunkWidth - 1;
            BehaviorTestWorld world = new BehaviorTestWorld(s_bh4CenterOrigin);

            for (int i = 0; i < VoxelData.ChunkWidth; i++)
            {
                world.SetBlock(0, BHG_SEAM_Y, i, BlockIDs.Grass);
                world.SetBlock(last, BHG_SEAM_Y, i, BlockIDs.Grass);
                world.SetBlock(i, BHG_SEAM_Y, 0, BlockIDs.Grass);
                world.SetBlock(i, BHG_SEAM_Y, last, BlockIDs.Grass);

                world.SetNeighborBlock(-1, 0, last, BHG_SEAM_Y, i, BlockIDs.Dirt); // adjacent
                world.SetNeighborBlock(1, 0, 0, BHG_SEAM_Y + 1, i, BlockIDs.Dirt); // up-diagonal
                world.SetNeighborBlock(0, -1, i, BHG_SEAM_Y - 1, last, BlockIDs.Dirt); // spread-down
            }

            world.AddNeighborPlaceholder(0, 1);
            return world;
        }

        /// <summary>
        /// BH-G-BOUNDS: grass whose reads leave the world — at the top row (its "above" and its candidates' "above" are
        /// out of the world) and at the bottom row (the spread-down dirt would be below y = 0) — next to grass that does
        /// spread, plus a grass voxel under a solid cap one row below the top (turns to dirt on tick 1).
        /// </summary>
        private static BehaviorTestWorld BuildBhgBoundsWorld()
        {
            BehaviorTestWorld world = new BehaviorTestWorld(s_bh4CenterOrigin);

            for (int x = 2; x <= 12; x += 2)
            {
                world.SetBlock(x, TOP_Y, 4, BlockIDs.Grass);
                world.SetBlock(x + 1, TOP_Y, 4, BlockIDs.Dirt); // its air-above read is outside the world
                world.SetBlock(x, TOP_Y - 1, 8, BlockIDs.Grass);
                world.SetBlock(x + 1, TOP_Y, 8, BlockIDs.Dirt); // up-diagonal, same out-of-world air read

                world.SetBlock(x, 0, 12, BlockIDs.Grass);
                world.SetBlock(x + 1, 0, 12, BlockIDs.Dirt); // convertible: air above it at y = 1
            }

            world.SetBlock(14, TOP_Y - 1, 14, BlockIDs.Grass);
            world.SetBlock(14, TOP_Y, 14, BlockIDs.Stone);
            return world;
        }

        /// <summary>
        /// BH-G-MIXED: grass with one candidate from each of the three candidate kinds — adjacent dirt at −X,
        /// up-diagonal dirt at +X, spread-down dirt under the air at −Z — so every spread picks among three candidates
        /// met in three different loops. The pick depends on the order the loops meet them, which the other fixtures
        /// (one candidate kind per grass) never exercise.
        /// </summary>
        private static BehaviorTestWorld BuildBhgMixedWorld()
        {
            BehaviorTestWorld world = new BehaviorTestWorld(s_bh4CenterOrigin);
            for (int x = 2; x <= 10; x += 4)
            for (int z = 2; z <= 14; z += 3)
            {
                world.SetBlock(x, BHG_SEAM_Y, z, BlockIDs.Grass);
                world.SetBlock(x - 1, BHG_SEAM_Y, z, BlockIDs.Dirt); // adjacent
                world.SetBlock(x + 1, BHG_SEAM_Y + 1, z, BlockIDs.Dirt); // up-diagonal
                world.SetBlock(x, BHG_SEAM_Y - 1, z - 1, BlockIDs.Dirt); // spread-down, under the air at −Z
            }

            return world;
        }

        /// <summary>
        /// BH-G-BAND-TOP: a row of grass at y = 14 with up-diagonal dirt at y = 15, whose "air above" read lands on
        /// y = 16 — the first row of section 1, two above the highest grass. The grass band must reach that section;
        /// one row short, the arena never copies it and the job's read of it trips the band check.
        /// </summary>
        private static BehaviorTestWorld BuildBhgBandTopWorld()
        {
            BehaviorTestWorld world = new BehaviorTestWorld(s_bh4CenterOrigin);
            for (int x = 2; x <= 12; x += 2)
            for (int z = 2; z <= 12; z += 2)
            {
                world.SetBlock(x, BHG_BAND_TOP_GRASS_Y, z, BlockIDs.Grass);
                world.SetBlock(x + 1, BHG_BAND_TOP_GRASS_Y + 1, z, BlockIDs.Dirt);
            }

            return world;
        }

        /// <summary>
        /// BH-G-RANDOM: seeded random terrain in the center and its −X/+X/−Z neighbors (the +Z neighbor is absent):
        /// columns of stone under a few rows of dirt, topped by grass or exposed dirt at a varying height so slopes
        /// expose dirt on every side, with an occasional stone cap over the surface. Hundreds of grass voxels with live
        /// candidates, inside the chunk and across three seams, make the reservoir sampling and the roll fire every tick.
        /// </summary>
        private static BehaviorTestWorld BuildBhgRandomWorld()
        {
            BehaviorTestWorld world = new BehaviorTestWorld(s_bh4CenterOrigin);
            System.Random rng = new System.Random(BHG_RANDOM_SEED);

            int[,] offsets = { { 0, 0 }, { -1, 0 }, { 1, 0 }, { 0, -1 } };
            for (int c = 0; c < offsets.GetLength(0); c++)
            {
                int dx = offsets[c, 0];
                int dz = offsets[c, 1];
                for (int x = 0; x < VoxelData.ChunkWidth; x++)
                for (int z = 0; z < VoxelData.ChunkWidth; z++)
                {
                    int surface = BHG_RANDOM_MIN_SURFACE + rng.Next(BHG_RANDOM_SURFACE_SPAN);
                    ushort top = rng.NextDouble() < BHG_RANDOM_GRASS_CHANCE ? BlockIDs.Grass : BlockIDs.Dirt;
                    bool cap = rng.NextDouble() < BHG_RANDOM_CAP_CHANCE;

                    for (int y = surface - BHG_RANDOM_DIRT_DEPTH - 1; y <= surface; y++)
                    {
                        ushort id = y == surface ? top : y < surface - BHG_RANDOM_DIRT_DEPTH ? BlockIDs.Stone : BlockIDs.Dirt;
                        Place(world, dx, dz, x, y, z, id);
                    }

                    if (cap)
                        Place(world, dx, dz, x, surface + 1, z, BlockIDs.Stone);
                }
            }

            return world;
        }

        /// <summary>
        /// Non-vacuity guard for <c>BH-D1[L|HB]</c>'s grass leg: runs each BH-G fixture under the Burst driver and
        /// requires that the grass job, not the managed fallback, ticked at least <see cref="BHG_MIN_JOB_TICKED"/> grass
        /// voxels and that at least <see cref="BHG_MIN_SPREADS"/> spreads (grass placed) were emitted — so the parity
        /// compare actually covered the reservoir sampling and the roll. BH-G-BOUNDS spreads only along the bottom row.
        /// </summary>
        private static bool BhG_GrassFixturesReachTheJob()
        {
            bool ok = true;
            foreach (DiffFixture f in GrassJobFixtures())
            {
                using BehaviorTestWorld world = f.Build();
                world.Driver = TickDriver.BurstHaloBand;
                BehaviorSnapshot snapshot = world.RunTicks(f.Ticks);

                int spreads = 0;
                foreach (TickRecord tick in snapshot.Ticks)
                foreach (VoxelEval eval in tick.Evals)
                    if (eval.Mods != null)
                        foreach (VoxelMod mod in eval.Mods)
                            if (mod.ID == BlockIDs.Grass)
                                spreads++;

                bool fixtureOk = world.JobTickedGrassVoxels >= BHG_MIN_JOB_TICKED && spreads >= BHG_MIN_SPREADS;
                if (!fixtureOk)
                    Debug.LogError($"[FAIL] BH-G non-vacuity — {f.Name}: the grass job ticked {world.JobTickedGrassVoxels} voxels " +
                                   $"(floor {BHG_MIN_JOB_TICKED}) and emitted {spreads} spreads (floor {BHG_MIN_SPREADS}).");
                else
                    Debug.Log($"  {f.Name}: the grass job ticked {world.JobTickedGrassVoxels} voxels and emitted {spreads} spreads.");
                ok &= fixtureOk;
            }

            return ok;
        }

        /// <summary>Writes into the ticked center, or into a neighbor as read-only context.</summary>
        private static void Place(BehaviorTestWorld world, int dx, int dz, int x, int y, int z, ushort id)
        {
            if (dx == 0 && dz == 0)
                world.SetBlock(x, y, z, id);
            else
                world.SetNeighborBlock(dx, dz, x, y, z, id);
        }
    }
}
