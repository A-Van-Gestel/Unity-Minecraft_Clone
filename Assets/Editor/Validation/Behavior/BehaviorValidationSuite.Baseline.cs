using System.Collections.Generic;
using Data;
using Editor.Validation.Behavior.Framework;
using Editor.Validation.Framework;
using UnityEngine;

namespace Editor.Validation.Behavior
{
    /// <summary>
    /// Baseline (regression) scenarios for the behavior-tick suite. Each golden-master scenario pairs with a
    /// determinism check (BH-6) and a non-vacuity positive control so it can never pass vacuously.
    /// </summary>
    public static partial class BehaviorValidationSuite
    {
        // --- BH-B1 fixture geometry (named per the "No Magic Numbers" convention) ---
        // The source sits at SOURCE_XZ and the floor spans [FLOOR_MIN_XZ, FLOOR_MAX_XZ]. The border margin
        // (SOURCE_XZ - 0 = 8, and 15 - SOURCE_XZ = 7) MUST exceed the fluid pathfinder's reach (≤4 cells) for
        // the ticks run, so neighbor queries stay in-chunk (Tier-1). Tightening these risks pushing spread to a
        // chunk border, where cross-chunk reads return "void" (see BehaviorTestWorld) and diverge from the game.
        private const int FLOOR_Y = 10;
        private const int FLOOR_MIN_XZ = 3;
        private const int FLOOR_MAX_XZ = 13;
        private const int SOURCE_XZ = 8;
        private const int BH_B1_TICKS = 3;

        /// <summary>
        /// BH-B1 golden master — the frozen canonical snapshot of <see cref="Bh1_SingleWaterSourceSpread"/>,
        /// compared via <see cref="GoldenMaster.AssertOrCapture"/> (which normalizes line endings and, when this
        /// is null/empty, logs the actual snapshot for re-capture). Determinism + non-vacuity are asserted
        /// regardless, so even capture mode is never a vacuous pass.
        /// </summary>
        private const string BH_B1_GOLDEN =
            @"T1
  (8,11,8) active=1 mods=[19@(8,11,9):01:0, 19@(8,11,7):01:0, 19@(9,11,8):01:0, 19@(7,11,8):01:0]
T2
  (8,11,8) active=0 mods=[]
  (8,11,9) active=1 mods=[19@(8,11,10):02:0, 19@(9,11,9):02:0, 19@(7,11,9):02:0]
  (8,11,7) active=1 mods=[19@(8,11,6):02:0, 19@(9,11,7):02:0, 19@(7,11,7):02:0]
  (9,11,8) active=1 mods=[19@(9,11,9):02:0, 19@(9,11,7):02:0, 19@(10,11,8):02:0]
  (7,11,8) active=1 mods=[19@(7,11,9):02:0, 19@(7,11,7):02:0, 19@(6,11,8):02:0]
T3
  (8,11,10) active=1 mods=[19@(8,11,11):03:0]
  (8,11,9) active=0 mods=[]
  (8,11,7) active=0 mods=[]
  (9,11,8) active=0 mods=[]
  (7,11,8) active=0 mods=[]
  (9,11,9) active=1 mods=[19@(9,11,10):03:0, 19@(10,11,9):03:0]
  (7,11,9) active=1 mods=[19@(7,11,10):03:0, 19@(6,11,9):03:0]
  (8,11,6) active=1 mods=[19@(8,11,5):03:0]
  (9,11,7) active=1 mods=[19@(9,11,6):03:0, 19@(10,11,7):03:0]
  (7,11,7) active=1 mods=[19@(7,11,6):03:0, 19@(6,11,7):03:0]
  (10,11,8) active=1 mods=[19@(11,11,8):03:0]
  (6,11,8) active=1 mods=[19@(5,11,8):03:0]
";

        // --- BH-B4 fixture geometry: a single unsupported (sourceless) water cell on one solid block.
        // Models "source removed" in its simplest form — a flow cell with no level-0 source feeding it. With a
        // solid floor below (no downflow) and no fluid neighbors, HandleFluidDecay drains it to air in 1 tick;
        // the now-air slot is then dropped from the active set on tick 2 → ActiveVoxelCount returns to 0
        // (termination). Interior placement keeps every neighbor query in-chunk (Tier-1).
        private const int DECAY_FLOOR_Y = 10;
        private const int DECAY_XZ = 8;
        private const byte DECAY_START_LEVEL = 1; // non-source level (a level-0 cell would never decay)
        private const int BH_B4_TICKS = 2;

        /// <summary>
        /// BH-B4 golden master (see <see cref="Bh4_UnsupportedWaterDecays"/>), compared via
        /// <see cref="GoldenMaster.AssertOrCapture"/>. Null/empty → capture mode (determinism, non-vacuity, and
        /// termination are still asserted, so it is never a vacuous pass).
        /// </summary>
        private const string BH_B4_GOLDEN =
            @"T1
  (8,11,8) active=1 mods=[0@(8,11,8):00:1]
T2
";

        /// <summary>Registers the baseline regression scenarios.</summary>
        static partial void AddBaselineScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario("Smoke: rig stands up and ticks without throwing", Smoke_RigBoots));
            scenarios.Add(new Scenario("BH-B1: single water source spreads on a flat floor", Bh1_SingleWaterSourceSpread));
            scenarios.Add(new Scenario("BH-B4: unsupported water decays to air → termination", Bh4_UnsupportedWaterDecays));
        }

        /// <summary>Registers the known-bug reproduction scenarios (none yet).</summary>
        static partial void AddKnownBugScenarios(List<Scenario> scenarios)
        {
        }

        /// <summary>
        /// Smoke canary: the harness stands up (World stub + ChunkData), the water source is registered active,
        /// and one tick runs without throwing. The earliest-failure signal for rig breakage; determinism and
        /// behavior are covered by BH-B1, so this deliberately does not re-run or re-assert them.
        /// </summary>
        private static bool Smoke_RigBoots()
        {
            int activeAfterSetup;
            int tickRecords;

            using (BehaviorTestWorld world = BuildBh1World())
            {
                activeAfterSetup = world.ActiveVoxelCount;
                tickRecords = world.RunTicks(1).Ticks.Count; // must not throw
            }

            bool ok = true;

            if (activeAfterSetup < 1)
            {
                Debug.LogError($"[FAIL] Smoke: expected ≥1 active voxel after setup, got {activeAfterSetup}.");
                ok = false;
            }

            if (tickRecords != 1)
            {
                Debug.LogError($"[FAIL] Smoke: expected 1 tick record, got {tickRecords}.");
                ok = false;
            }

            if (ok) Debug.Log($"[PASS] Smoke: rig booted, {activeAfterSetup} active voxel(s), 1 tick ran.");
            return ok;
        }

        /// <summary>
        /// BH-B1: a single water source on a flat solid floor, run for <see cref="BH_B1_TICKS"/> ticks. Asserts
        /// run-to-run determinism (BH-6), non-vacuous spread (the water actually emits mods), and byte-equality
        /// with the <see cref="BH_B1_GOLDEN"/> characterization baseline.
        /// </summary>
        private static bool Bh1_SingleWaterSourceSpread()
        {
            string s1, s2;
            int totalMods;

            using (BehaviorTestWorld world = BuildBh1World())
            {
                BehaviorSnapshot snap = world.RunTicks(BH_B1_TICKS);
                s1 = snap.Serialize();
                totalMods = snap.TotalModCount;
            }

            using (BehaviorTestWorld world = BuildBh1World())
            {
                s2 = world.RunTicks(BH_B1_TICKS).Serialize();
            }

            bool ok = true;

            // BH-6: determinism (the one universal invariant — a single golden-equality check cannot prove it).
            if (s1 != s2)
            {
                Debug.LogError("[FAIL] BH-B1: non-deterministic — two identical runs produced different snapshots.");
                ok = false;
            }

            // Non-vacuity positive control: the source must actually spread.
            if (totalMods == 0)
            {
                Debug.LogError("[FAIL] BH-B1: vacuous — the water source emitted no mods over 3 ticks.");
                ok = false;
            }

            // Golden master (normalization + capture-mode handled by the shared helper).
            if (!GoldenMaster.AssertOrCapture("BH-B1", s1, BH_B1_GOLDEN))
                ok = false;

            if (ok) Debug.Log($"[PASS] BH-B1: deterministic, {totalMods} mod(s) over {BH_B1_TICKS} ticks, golden master matched.");
            return ok;
        }

        /// <summary>
        /// Builds the shared BH-B1 fixture: a solid <see cref="BlockIDs.Stone"/> floor at <see cref="FLOOR_Y"/>
        /// spanning [<see cref="FLOOR_MIN_XZ"/>, <see cref="FLOOR_MAX_XZ"/>]² with a single
        /// <see cref="BlockIDs.Water"/> source (level 0) at (<see cref="SOURCE_XZ"/>, FLOOR_Y+1,
        /// <see cref="SOURCE_XZ"/>). The interior margin keeps the spread in-chunk (Tier-1) — see the constants' note.
        /// </summary>
        private static BehaviorTestWorld BuildBh1World()
        {
            BehaviorTestWorld world = new BehaviorTestWorld();

            for (int x = FLOOR_MIN_XZ; x <= FLOOR_MAX_XZ; x++)
            for (int z = FLOOR_MIN_XZ; z <= FLOOR_MAX_XZ; z++)
                world.SetBlock(x, FLOOR_Y, z, BlockIDs.Stone);

            world.SetBlock(SOURCE_XZ, FLOOR_Y + 1, SOURCE_XZ, BlockIDs.Water, meta: 0); // source: fluid level 0
            return world;
        }

        /// <summary>
        /// BH-B4: a single unsupported (sourceless) water cell on a solid block. Asserts run-to-run determinism
        /// (BH-6), non-vacuous decay, **termination** (the active set empties once the cell drains to air), and
        /// byte-equality with <see cref="BH_B4_GOLDEN"/>. Exercises the `HandleFluidDecay` drain-to-air branch
        /// and the active-voxel drop — the first scenario that asserts termination.
        /// </summary>
        private static bool Bh4_UnsupportedWaterDecays()
        {
            string s1, s2;
            int totalMods;
            int activeAtEnd;

            using (BehaviorTestWorld world = BuildBh4World())
            {
                BehaviorSnapshot snap = world.RunTicks(BH_B4_TICKS);
                s1 = snap.Serialize();
                totalMods = snap.TotalModCount;
                activeAtEnd = world.ActiveVoxelCount;
            }

            using (BehaviorTestWorld world = BuildBh4World())
            {
                s2 = world.RunTicks(BH_B4_TICKS).Serialize();
            }

            bool ok = true;

            // BH-6: determinism.
            if (s1 != s2)
            {
                Debug.LogError("[FAIL] BH-B4: non-deterministic — two identical runs produced different snapshots.");
                ok = false;
            }

            // Non-vacuity positive control: the cell must actually emit a decay mod.
            if (totalMods == 0)
            {
                Debug.LogError("[FAIL] BH-B4: vacuous — the unsupported cell emitted no mods.");
                ok = false;
            }

            // Termination: once the cell drains to air, the tick pump drops it and the active set empties.
            if (activeAtEnd != 0)
            {
                Debug.LogError($"[FAIL] BH-B4: did not terminate — expected empty active set, got {activeAtEnd}.");
                ok = false;
            }

            // Golden master (normalization + capture-mode handled by the shared helper).
            if (!GoldenMaster.AssertOrCapture("BH-B4", s1, BH_B4_GOLDEN))
                ok = false;

            if (ok) Debug.Log($"[PASS] BH-B4: deterministic, {totalMods} mod(s), terminated (active set empty), golden master matched.");
            return ok;
        }

        /// <summary>
        /// Builds the BH-B4 fixture: one solid <see cref="BlockIDs.Stone"/> block at
        /// (<see cref="DECAY_XZ"/>, <see cref="DECAY_FLOOR_Y"/>, <see cref="DECAY_XZ"/>) with a single
        /// unsupported <see cref="BlockIDs.Water"/> cell at level <see cref="DECAY_START_LEVEL"/> directly above
        /// it — no level-0 source anywhere, so the cell has no support and must drain.
        /// </summary>
        private static BehaviorTestWorld BuildBh4World()
        {
            BehaviorTestWorld world = new BehaviorTestWorld();
            world.SetBlock(DECAY_XZ, DECAY_FLOOR_Y, DECAY_XZ, BlockIDs.Stone); // solid floor blocks downflow
            world.SetBlock(DECAY_XZ, DECAY_FLOOR_Y + 1, DECAY_XZ, BlockIDs.Water, meta: DECAY_START_LEVEL);
            return world;
        }
    }
}
