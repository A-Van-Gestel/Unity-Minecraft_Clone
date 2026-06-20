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

        // --- BH-B2 fixture geometry: a water source boxed on 3 sides (west/north/south walls) on an upper
        // floor, open to the east over a 1-block cliff (no upper floor at the edge; a lower floor one block
        // down). Flow is forced east, over the edge, and falls one block — exercising gravity (MakeFalling),
        // optimal-flow-toward-drop, and the waterfall reset on landing. Interior placement (Tier-1).
        private const int CLIFF_UPPER_FLOOR_Y = 10; // source rests at +1 (y=11)
        private const int CLIFF_LOWER_FLOOR_Y = 9; // the cliff is one block deep
        private const int CLIFF_SRC_X = 6;
        private const int CLIFF_EDGE_X = 7; // the cliff cell: no upper floor, lower floor below
        private const int CLIFF_Z = 8;
        private const int BH_B2_TICKS = 3; // through source-spread → fall → land+waterfall-reset (T4+ just spills off the minimal lower floor)

        /// <summary>
        /// BH-B2 golden master (see <see cref="Bh2_WaterFallsOverCliff"/>), compared via
        /// <see cref="GoldenMaster.AssertOrCapture"/>. <b>Capture mode</b> until confirmed in-game — falling-fluid
        /// dynamics are intricate, so the snapshot must be eyeballed against real behavior before freezing.
        /// </summary>
        private const string BH_B2_GOLDEN =
            @"T1
  (6,11,8) active=1 mods=[19@(7,11,8):01:0]
T2
  (6,11,8) active=0 mods=[]
  (7,11,8) active=1 mods=[19@(7,10,8):09:0]
T3
  (7,10,8) active=1 mods=[19@(7,10,9):01:0, 19@(7,10,7):01:0, 19@(8,10,8):01:0]
  (7,11,8) active=0 mods=[]
";

        // --- BH-B3 fixture geometry: two water sources two cells apart (a 1-cell gap between) in a walled,
        // floored 3-cell channel. The sources fill the gap, then the gap — with two adjacent sources over a
        // solid floor — regenerates into a new source (infiniteSourceRegeneration). Everything then stabilizes
        // to sources and goes inactive (termination). Walls confine the flow to the gap; interior (Tier-1).
        private const int REGEN_FLOOR_Y = 10;
        private const int REGEN_LEFT_X = 6; // left source
        private const int REGEN_RIGHT_X = 8; // right source (gap at x=7)
        private const int REGEN_Z = 8;
        private const int BH_B3_TICKS = 3;

        /// <summary>
        /// BH-B3 golden master (see <see cref="Bh3_InfiniteSourceRegeneration"/>), compared via
        /// <see cref="GoldenMaster.AssertOrCapture"/>. Null/empty → capture mode.
        /// </summary>
        private const string BH_B3_GOLDEN =
            @"T1
  (6,11,8) active=1 mods=[19@(7,11,8):01:0]
  (8,11,8) active=1 mods=[19@(7,11,8):01:0]
T2
  (6,11,8) active=0 mods=[]
  (8,11,8) active=0 mods=[]
  (7,11,8) active=1 mods=[19@(7,11,8):00:1]
T3
  (7,11,8) active=0 mods=[]
";

        /// <summary>Registers the baseline regression scenarios.</summary>
        static partial void AddBaselineScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario("Smoke: rig stands up and ticks without throwing", Smoke_RigBoots));
            scenarios.Add(new Scenario("BH-B1: single water source spreads on a flat floor", Bh1_SingleWaterSourceSpread));
            scenarios.Add(new Scenario("BH-B4: unsupported water decays to air → termination", Bh4_UnsupportedWaterDecays));
            scenarios.Add(new Scenario("BH-B2: water falls over a 1-block cliff (gravity + waterfall reset)", Bh2_WaterFallsOverCliff));
            scenarios.Add(new Scenario("BH-B3: two sources regenerate a source in the gap (infinite water)", Bh3_InfiniteSourceRegeneration));
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

        /// <summary>
        /// BH-B2: a boxed water source flows east over a 1-block cliff and falls. Asserts run-to-run determinism
        /// (BH-6) and non-vacuous flow, and compares the captured fall/reset sequence against
        /// <see cref="BH_B2_GOLDEN"/>. No termination assertion — this is gravity/waterfall characterization, and
        /// the run is short, so the post-fall puddle is not driven to a stable state.
        /// </summary>
        private static bool Bh2_WaterFallsOverCliff()
        {
            string s1, s2;
            int totalMods;

            using (BehaviorTestWorld world = BuildBh2World())
            {
                BehaviorSnapshot snap = world.RunTicks(BH_B2_TICKS);
                s1 = snap.Serialize();
                totalMods = snap.TotalModCount;
            }

            using (BehaviorTestWorld world = BuildBh2World())
            {
                s2 = world.RunTicks(BH_B2_TICKS).Serialize();
            }

            bool ok = true;

            if (s1 != s2)
            {
                Debug.LogError("[FAIL] BH-B2: non-deterministic — two identical runs produced different snapshots.");
                ok = false;
            }

            if (totalMods == 0)
            {
                Debug.LogError("[FAIL] BH-B2: vacuous — the source emitted no mods.");
                ok = false;
            }

            if (!GoldenMaster.AssertOrCapture("BH-B2", s1, BH_B2_GOLDEN))
                ok = false;

            if (ok) Debug.Log($"[PASS] BH-B2: deterministic, {totalMods} mod(s) over {BH_B2_TICKS} ticks, golden master matched.");
            return ok;
        }

        /// <summary>
        /// Builds the BH-B2 fixture: an upper floor block under a <see cref="BlockIDs.Water"/> source at
        /// (<see cref="CLIFF_SRC_X"/>, <see cref="CLIFF_UPPER_FLOOR_Y"/>+1, <see cref="CLIFF_Z"/>), walled to the
        /// west/north/south at the flow level so flow is forced east, with a 1-block cliff at
        /// <see cref="CLIFF_EDGE_X"/> (no upper floor; a single lower-floor block one level down) for the water
        /// to fall onto.
        /// </summary>
        private static BehaviorTestWorld BuildBh2World()
        {
            BehaviorTestWorld world = new BehaviorTestWorld();
            const int flowY = CLIFF_UPPER_FLOOR_Y + 1; // 11

            world.SetBlock(CLIFF_SRC_X, CLIFF_UPPER_FLOOR_Y, CLIFF_Z, BlockIDs.Stone); // floor under the source
            world.SetBlock(CLIFF_SRC_X - 1, flowY, CLIFF_Z, BlockIDs.Stone); // west wall
            world.SetBlock(CLIFF_SRC_X, flowY, CLIFF_Z - 1, BlockIDs.Stone); // north wall
            world.SetBlock(CLIFF_SRC_X, flowY, CLIFF_Z + 1, BlockIDs.Stone); // south wall
            world.SetBlock(CLIFF_EDGE_X, CLIFF_LOWER_FLOOR_Y, CLIFF_Z, BlockIDs.Stone); // lower floor (1 block down)
            world.SetBlock(CLIFF_SRC_X, flowY, CLIFF_Z, BlockIDs.Water, meta: 0); // source, level 0
            return world;
        }

        /// <summary>
        /// BH-B3: two sources one gap apart in a walled channel regenerate a new source in the gap. Asserts
        /// determinism (BH-6), non-vacuous activity, termination (everything stabilizes to inactive sources),
        /// and byte-equality with <see cref="BH_B3_GOLDEN"/>. Exercises the `infiniteSourceRegeneration` branch
        /// of `CalculateExpectedFluidLevel`.
        /// </summary>
        private static bool Bh3_InfiniteSourceRegeneration()
        {
            string s1, s2;
            int totalMods;
            int activeAtEnd;

            using (BehaviorTestWorld world = BuildBh3World())
            {
                BehaviorSnapshot snap = world.RunTicks(BH_B3_TICKS);
                s1 = snap.Serialize();
                totalMods = snap.TotalModCount;
                activeAtEnd = world.ActiveVoxelCount;
            }

            using (BehaviorTestWorld world = BuildBh3World())
            {
                s2 = world.RunTicks(BH_B3_TICKS).Serialize();
            }

            bool ok = true;

            if (s1 != s2)
            {
                Debug.LogError("[FAIL] BH-B3: non-deterministic — two identical runs produced different snapshots.");
                ok = false;
            }

            if (totalMods == 0)
            {
                Debug.LogError("[FAIL] BH-B3: vacuous — the sources emitted no mods.");
                ok = false;
            }

            // Termination: once the gap regenerates to a source, all three cells are stable sources and drop out.
            if (activeAtEnd != 0)
            {
                Debug.LogError($"[FAIL] BH-B3: did not terminate — expected empty active set, got {activeAtEnd}.");
                ok = false;
            }

            if (!GoldenMaster.AssertOrCapture("BH-B3", s1, BH_B3_GOLDEN))
                ok = false;

            if (ok) Debug.Log($"[PASS] BH-B3: deterministic, {totalMods} mod(s) over {BH_B3_TICKS} ticks, terminated, golden master matched.");
            return ok;
        }

        /// <summary>
        /// Builds the BH-B3 fixture: a solid floor under a 3-cell channel
        /// [<see cref="REGEN_LEFT_X"/>..<see cref="REGEN_RIGHT_X"/>] at z=<see cref="REGEN_Z"/>, walled on the
        /// two ends and both z-sides at the flow level so flow is confined to the gap, with a
        /// <see cref="BlockIDs.Water"/> source at each end (a 1-cell gap between them).
        /// </summary>
        private static BehaviorTestWorld BuildBh3World()
        {
            BehaviorTestWorld world = new BehaviorTestWorld();
            const int flowY = REGEN_FLOOR_Y + 1; // 11

            // Floor + north/south walls along the whole channel.
            for (int x = REGEN_LEFT_X; x <= REGEN_RIGHT_X; x++)
            {
                world.SetBlock(x, REGEN_FLOOR_Y, REGEN_Z, BlockIDs.Stone); // floor
                world.SetBlock(x, flowY, REGEN_Z - 1, BlockIDs.Stone); // north wall
                world.SetBlock(x, flowY, REGEN_Z + 1, BlockIDs.Stone); // south wall
            }

            // End walls beyond each source.
            world.SetBlock(REGEN_LEFT_X - 1, flowY, REGEN_Z, BlockIDs.Stone); // west end
            world.SetBlock(REGEN_RIGHT_X + 1, flowY, REGEN_Z, BlockIDs.Stone); // east end

            // The two sources (gap at the middle cell).
            world.SetBlock(REGEN_LEFT_X, flowY, REGEN_Z, BlockIDs.Water, meta: 0);
            world.SetBlock(REGEN_RIGHT_X, flowY, REGEN_Z, BlockIDs.Water, meta: 0);
            return world;
        }
    }
}
