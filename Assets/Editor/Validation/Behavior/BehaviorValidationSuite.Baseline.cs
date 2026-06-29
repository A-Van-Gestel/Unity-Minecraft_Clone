using System;
using System.Collections.Generic;
using Data;
using Editor.Validation.Behavior.Framework;
using Editor.Validation.Framework;
using Jobs.BurstData;
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
        /// <see cref="GoldenMaster.AssertOrCapture"/>. Null/empty → capture mode. At T3 all three cells re-appear:
        /// applying the T2 gap-regeneration mod re-wakes its two flanking-source neighbors via the Step-4
        /// six-neighbor re-activation (<see cref="BehaviorTestWorld.ApplyMod"/>), and all three then quiesce.
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
  (8,11,8) active=0 mods=[]
  (6,11,8) active=0 mods=[]
  (7,11,8) active=0 mods=[]
";

        // --- BH-B5 fixture geometry: a single LAVA source at one end of a walled, floored 1-D channel, open to
        // the east. Lava's spreadChance (0.25) routes flow through the seeded-RNG viscosity gate in
        // HandleFluidSpread (the ONLY scenario that does — water's 1.0 never branches), and flowLevels=4 caps the
        // reach at 3 cells (source level 0 → x+1 level 1 → x+2 level 2 → x+3 level 3; level 4 ≥ flowLevels stops).
        // The 1-D channel makes the per-tick staggering legible: each successful spread advances the front by one
        // cell, and skipped ticks leave the (still-active) source emitting nothing. Interior placement (Tier-1).
        private const int LAVA_FLOOR_Y = 10;
        private const int LAVA_SRC_X = 6; // source (level 0)
        private const int LAVA_END_X = 9; // farthest cell lava can reach (level 3); flowLevels=4 stops the next
        private const int LAVA_Z = 8;
        private const int BH_B5_TICKS = 11; // long enough to extend the full channel despite skips, then quiesce

        /// <summary>
        /// BH-B5 golden master (see <see cref="Bh5_LavaViscosityStaggers"/>), compared via
        /// <see cref="GoldenMaster.AssertOrCapture"/>. The 25% viscosity staggering was confirmed in-game before
        /// freezing — this is the one scenario whose snapshot depends on the per-voxel/per-tick RNG (TG-3). Read
        /// the pattern as: source rolls skip,skip,spread (T1–T3); the level-2 cell at x8 rolls skip×4 then spread
        /// (T5–T9); the front reaches level 3 at x9 and stops (level 4 ≥ flowLevels), then everything quiesces.
        /// </summary>
        private const string BH_B5_GOLDEN =
            @"T1
  (6,11,8) active=1 mods=[]
T2
  (6,11,8) active=1 mods=[]
T3
  (6,11,8) active=1 mods=[20@(7,11,8):01:0]
T4
  (6,11,8) active=0 mods=[]
  (7,11,8) active=1 mods=[20@(8,11,8):02:0]
T5
  (8,11,8) active=1 mods=[]
  (7,11,8) active=0 mods=[]
T6
  (8,11,8) active=1 mods=[]
T7
  (8,11,8) active=1 mods=[]
T8
  (8,11,8) active=1 mods=[]
T9
  (8,11,8) active=1 mods=[20@(9,11,8):03:0]
T10
  (8,11,8) active=0 mods=[]
  (9,11,8) active=0 mods=[]
T11
";

        /// <summary>Registers the baseline regression scenarios.</summary>
        static partial void AddBaselineScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario("Smoke: rig stands up and ticks without throwing", Smoke_RigBoots));
            scenarios.Add(new Scenario("BH-B1: single water source spreads on a flat floor", Bh1_SingleWaterSourceSpread));
            scenarios.Add(new Scenario("BH-B4: unsupported water decays to air → termination", Bh4_UnsupportedWaterDecays));
            scenarios.Add(new Scenario("BH-B2: water falls over a 1-block cliff (gravity + waterfall reset)", Bh2_WaterFallsOverCliff));
            scenarios.Add(new Scenario("BH-B3: two sources regenerate a source in the gap (infinite water)", Bh3_InfiniteSourceRegeneration));
            scenarios.Add(new Scenario("BH-B5: lava viscosity staggers via the seeded-RNG gate (TG-3)", Bh5_LavaViscosityStaggers));
            scenarios.Add(new Scenario("BH-B6: grass spreads to convertible dirt (reservoir sampling + spread roll)", Bh6_GrassSpreadsToDirt));
            scenarios.Add(new Scenario("BH-B7: grass turns to dirt under a solid block (deterministic)", Bh7_GrassUnderSolidTurnsToDirt));

            // BH-D1 old-vs-new differential (comparator self-test + driver-pair fixtures) — see BehaviorValidationSuite.Differential.cs.
            AddDifferentialScenarios(scenarios);
        }

        /// <summary>Registers the known-bug reproduction scenarios (none yet).</summary>
        static partial void AddKnownBugScenarios(List<Scenario> scenarios)
        {
        }

        /// <summary>
        /// Runs a golden-master behavior scenario with the universal invariants every <c>Bh*</c> scenario shares,
        /// so each scenario declares only what makes it distinct (build delegate, tick count, golden, optional
        /// termination, and per-scenario extra checks) instead of re-spelling the boilerplate:
        /// <list type="bullet">
        /// <item>run-to-run determinism (BH-6 — a single golden-equality check cannot prove it);</item>
        /// <item>non-vacuity (≥1 mod emitted, so the scenario can never pass vacuously);</item>
        /// <item>optional termination (the active set empties) when <paramref name="expectTermination"/> is set;</item>
        /// <item>byte-equality with <paramref name="golden"/> via <see cref="GoldenMaster.AssertOrCapture"/>
        /// (capture mode when null/empty); and a single <c>[PASS]</c> log.</item>
        /// </list>
        /// Centralizing the wording here means a new universal invariant (or a format change from TG-4/TG-5) is
        /// edited once, not hand-copied into seven scenarios where a missed copy would silently weaken a guard.
        /// </summary>
        /// <param name="label">Scenario tag (e.g. <c>"BH-B1"</c>) used in every log line and as the golden key.</param>
        /// <param name="build">Factory for a fresh fixture world; invoked once per run (twice total, for determinism).</param>
        /// <param name="ticks">Number of behavior ticks to run.</param>
        /// <param name="golden">The frozen golden snapshot, or null/empty for capture mode.</param>
        /// <param name="expectTermination">When true, asserts the active set is empty after the run.</param>
        /// <param name="extraChecks">
        /// Optional per-scenario assertions evaluated over the first run's (world, snapshot). Invoked inside the
        /// first run's <c>using</c> block, so the world's final voxel state is still live (pre-<c>Dispose</c>). The
        /// delegate logs its own <c>[FAIL]</c> detail and returns false on failure.
        /// </param>
        /// <returns>True iff every universal invariant and the extra checks passed.</returns>
        private static bool RunGoldenScenario(string label, Func<BehaviorTestWorld> build, int ticks,
            string golden, bool expectTermination = false,
            Func<BehaviorTestWorld, BehaviorSnapshot, bool> extraChecks = null)
        {
            string s1, s2;
            int totalMods;
            int activeAtEnd;
            bool ok = true;

            // TG-4 Phase 1 promotion (in-game confirmed 2026-06-22): the goldens characterize the production
            // SplitFamily traversal (per-family buckets, grass-then-fluid) rather than the retired single-set order.
            // BH-D1[L|S] proves this equals the legacy order under §4.3; every fixture is single-family, so the
            // serialized goldens are byte-identical to the legacy capture (no re-capture needed).
            using (BehaviorTestWorld world = build())
            {
                world.Driver = TickDriver.SplitFamily;
                BehaviorSnapshot snap = world.RunTicks(ticks);
                s1 = snap.Serialize();
                totalMods = snap.TotalModCount;
                activeAtEnd = world.ActiveVoxelCount;

                // Per-scenario assertions run while the world's final state is still live (before Dispose).
                if (extraChecks != null && !extraChecks(world, snap))
                    ok = false;
            }

            using (BehaviorTestWorld world = build())
            {
                world.Driver = TickDriver.SplitFamily;
                s2 = world.RunTicks(ticks).Serialize();
            }

            // BH-6: determinism (the one universal invariant a single golden-equality check cannot prove).
            if (s1 != s2)
            {
                Debug.LogError($"[FAIL] {label}: non-deterministic — two identical runs produced different snapshots.");
                ok = false;
            }

            // Non-vacuity positive control: the scenario must actually do something.
            if (totalMods == 0)
            {
                Debug.LogError($"[FAIL] {label}: vacuous — no mods emitted over {ticks} tick(s).");
                ok = false;
            }

            // Optional termination: the active set must empty once the scenario quiesces.
            if (expectTermination && activeAtEnd != 0)
            {
                Debug.LogError($"[FAIL] {label}: did not terminate — expected empty active set, got {activeAtEnd}.");
                ok = false;
            }

            // Golden master (normalization + capture-mode handled by the shared helper).
            if (!GoldenMaster.AssertOrCapture(label, s1, golden))
                ok = false;

            if (ok)
                Debug.Log($"[PASS] {label}: deterministic, {totalMods} mod(s) over {ticks} tick(s)" +
                          $"{(expectTermination ? ", terminated" : "")}, golden master matched.");
            return ok;
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
        private static bool Bh1_SingleWaterSourceSpread() =>
            RunGoldenScenario("BH-B1", BuildBh1World, BH_B1_TICKS, BH_B1_GOLDEN);

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
        private static bool Bh4_UnsupportedWaterDecays() =>
            RunGoldenScenario("BH-B4", BuildBh4World, BH_B4_TICKS, BH_B4_GOLDEN, expectTermination: true);

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
        private static bool Bh2_WaterFallsOverCliff() =>
            RunGoldenScenario("BH-B2", BuildBh2World, BH_B2_TICKS, BH_B2_GOLDEN);

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
        private static bool Bh3_InfiniteSourceRegeneration() =>
            RunGoldenScenario("BH-B3", BuildBh3World, BH_B3_TICKS, BH_B3_GOLDEN, expectTermination: true);

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

        /// <summary>
        /// BH-B5: a single lava source flows down a walled 1-D channel, gated by the seeded-RNG viscosity skip
        /// (<c>spreadChance</c> 0.25). Asserts run-to-run determinism (BH-6 — the core TG-3 guard, since the seed
        /// mixes <c>World.TickCounter</c>), non-vacuous flow, **progression** (the front advances — a position-only
        /// seed would freeze a source whose single roll lands above <c>spreadChance</c>), **staggering** (the
        /// still-active source skips on at least one tick — viscosity genuinely engages, unlike water at 1.0), and
        /// byte-equality with <see cref="BH_B5_GOLDEN"/>. The lone scenario exercising the seeded-RNG path.
        /// </summary>
        private static bool Bh5_LavaViscosityStaggers()
        {
            Vector3Int sourcePos = new Vector3Int(LAVA_SRC_X, LAVA_FLOOR_Y + 1, LAVA_Z);

            return RunGoldenScenario("BH-B5", BuildBh5World, BH_B5_TICKS, BH_B5_GOLDEN, expectTermination: true,
                extraChecks: (world, snap) =>
                {
                    bool ok = true;
                    CountSourceSpreadVsSkip(snap, sourcePos, out int sourceSpreadTicks, out int sourceSkipTicks);

                    // Progression past the first hop: the cell two out from the source (x+2) is lava at the end.
                    // This needs the gate to be passed at two distinct cells/ticks, so it cannot be satisfied by a
                    // frozen (never-advancing) flow — the TG-3 failure mode.
                    bool advancedBeyondNeighbor = BurstVoxelDataBitMapping.GetId(
                        world.ChunkData.GetVoxel(LAVA_SRC_X + 2, LAVA_FLOOR_Y + 1, LAVA_Z)) == BlockIDs.Lava;

                    // Anti-freeze (progression): the always-active level-0 source must spread on ≥1 tick AND the
                    // front must advance beyond the first neighbor. A position-only seed (the TG-3 bug) freezes a
                    // source whose single NextFloat lands above spreadChance, so it would never spread/advance.
                    if (sourceSpreadTicks == 0 || !advancedBeyondNeighbor)
                    {
                        Debug.LogError($"[FAIL] BH-B5: lava did not progress — source spread on {sourceSpreadTicks} tick(s), " +
                                       $"reached x+2={advancedBeyondNeighbor}. Expected the viscosity gate to pass and the front to advance.");
                        ok = false;
                    }

                    // Staggering (viscosity engaged): while still active (open air neighbor), the source must SKIP
                    // on ≥1 tick. Water (spreadChance 1.0) never skips; only the per-tick reseed makes both spread
                    // and skip outcomes appear at one fixed source position.
                    if (sourceSkipTicks == 0)
                    {
                        Debug.LogError("[FAIL] BH-B5: no viscosity staggering — the active source spread on every tick (behaving like water, not lava).");
                        ok = false;
                    }

                    return ok;
                });
        }

        /// <summary>
        /// Counts, over a whole run, the ticks on which the lava source at <paramref name="sourcePos"/> emitted a
        /// spread mod versus the ticks on which it was still active (an open neighbor remained) yet emitted
        /// nothing — a viscosity skip. The source never decays (level 0) and cannot fall (solid floor), so its
        /// only possible mod is a horizontal spread; thus "active and mod-less" is unambiguously a skipped spread.
        /// </summary>
        /// <param name="snap">The recorded run.</param>
        /// <param name="sourcePos">Chunk-local position of the lava source.</param>
        /// <param name="spreadTicks">Out: ticks on which the source emitted ≥1 spread mod.</param>
        /// <param name="skipTicks">Out: ticks on which the source was active but emitted no mod (a viscosity skip).</param>
        private static void CountSourceSpreadVsSkip(BehaviorSnapshot snap, Vector3Int sourcePos,
            out int spreadTicks, out int skipTicks)
        {
            spreadTicks = 0;
            skipTicks = 0;
            foreach (TickRecord tick in snap.Ticks)
            foreach (VoxelEval eval in tick.Evals)
            {
                if (eval.Pos != sourcePos) continue;
                if (eval.Mods != null && eval.Mods.Count > 0) spreadTicks++;
                else if (eval.Active) skipTicks++; // active (open neighbor) but emitted nothing ⇒ viscosity skip
            }
        }

        /// <summary>
        /// Builds the BH-B5 fixture: a solid floor under a 1-D channel [<see cref="LAVA_SRC_X"/>..<see cref="LAVA_END_X"/>]
        /// at z=<see cref="LAVA_Z"/>, walled on both z-sides and both ends at the flow level so flow is confined to
        /// the channel, with a single <see cref="BlockIDs.Lava"/> source (level 0) at the west end.
        /// </summary>
        private static BehaviorTestWorld BuildBh5World()
        {
            BehaviorTestWorld world = new BehaviorTestWorld();
            const int flowY = LAVA_FLOOR_Y + 1; // 11

            // Floor + north/south walls along the whole channel.
            for (int x = LAVA_SRC_X; x <= LAVA_END_X; x++)
            {
                world.SetBlock(x, LAVA_FLOOR_Y, LAVA_Z, BlockIDs.Stone); // floor
                world.SetBlock(x, flowY, LAVA_Z - 1, BlockIDs.Stone); // north wall
                world.SetBlock(x, flowY, LAVA_Z + 1, BlockIDs.Stone); // south wall
            }

            // End walls beyond the source and beyond the farthest reachable cell.
            world.SetBlock(LAVA_SRC_X - 1, flowY, LAVA_Z, BlockIDs.Stone); // west end
            world.SetBlock(LAVA_END_X + 1, flowY, LAVA_Z, BlockIDs.Stone); // east end

            world.SetBlock(LAVA_SRC_X, flowY, LAVA_Z, BlockIDs.Lava, meta: 0); // lava source, level 0
            return world;
        }

        // --- BH-B6 fixture geometry: a single GRASS block flanked by two convertible DIRT cells (one to each
        // horizontal side, air above each), on an otherwise empty (all-air) chunk. Grass's Behave reservoir-samples
        // the candidate dirt cells then rolls the seeded-RNG spread gate (VoxelData.GrassSpreadChance = 0.02 — very
        // low, so the grass idles most ticks and spreads rarely). The grass position is chosen (via a one-off seed
        // probe) so the per-tick reseed lands a spread within a few ticks, keeping the golden tight while still
        // showing staggering (idle ticks before the spread). Two candidates make the reservoir *choice* observable:
        // a TG-4/TG-5 change to the candidate scan order would pick the other cell and break this golden. Interior
        // placement (Tier-1). No solid block sits above the grass, so the grass→dirt branch (BH-B7) never fires here.
        private const int GRASS_Y = 11;
        private const int GRASS_X = 8; // chosen so the per-tick reseed lands a spread at T5 (early, interior); see BH_B6_TICKS
        private const int GRASS_Z = 8;
        private const int BH_B6_TICKS = 6; // T1–T4 idle, T5 spread to the chosen candidate, T6 aftermath (new grass drops)

        /// <summary>
        /// BH-B6 golden master (see <see cref="Bh6_GrassSpreadsToDirt"/>), compared via
        /// <see cref="GoldenMaster.AssertOrCapture"/>. Frozen after in-game confirmation — like BH-B5 the snapshot
        /// depends on the per-voxel/per-tick RNG (TG-3). Read it as: the grass idles four ticks (fails the 2% spread
        /// roll) then on T5 spreads to the reservoir-chosen **right** candidate (x=9) — the seed picks x=9 over x=7,
        /// so a change to the candidate scan order would break this golden — after which the new grass (no
        /// convertible neighbor) drops out on T6 while the original grass stays active for the remaining dirt.
        /// </summary>
        private const string BH_B6_GOLDEN =
            @"T1
  (8,11,8) active=1 mods=[]
T2
  (8,11,8) active=1 mods=[]
T3
  (8,11,8) active=1 mods=[]
T4
  (8,11,8) active=1 mods=[]
T5
  (8,11,8) active=1 mods=[2@(9,11,8):00:0]
T6
  (8,11,8) active=1 mods=[]
  (9,11,8) active=0 mods=[]
";

        /// <summary>
        /// BH-B6: a grass block flanked by two convertible-dirt cells spreads to one of them, gated by the seeded
        /// reservoir-sampling + 2% spread roll. Asserts run-to-run determinism (BH-6 — the TG-3 reproducibility
        /// guard; the seed mixes <c>World.TickCounter</c>), non-vacuous spread (a grass mod is emitted within the
        /// window — proving the reseed lets the rare gate eventually fire, not freeze), that the chosen target is
        /// one of the two candidates (the reservoir picked a real candidate), and byte-equality with
        /// <see cref="BH_B6_GOLDEN"/>. The second grass seeded-RNG path (the first is BH-B5 lava).
        /// </summary>
        private static bool Bh6_GrassSpreadsToDirt() =>
            RunGoldenScenario("BH-B6", () => BuildBh6World(GRASS_X, GRASS_Z), BH_B6_TICKS, BH_B6_GOLDEN,
                extraChecks: (world, snap) =>
                {
                    // A spread converts one chosen candidate dirt → grass. Confirm via final state that exactly the
                    // reservoir-chosen cell flipped (the apply path actually placed the emitted mod).
                    bool leftConverted = BurstVoxelDataBitMapping.GetId(
                        world.ChunkData.GetVoxel(GRASS_X - 1, GRASS_Y, GRASS_Z)) == BlockIDs.Grass;
                    bool rightConverted = BurstVoxelDataBitMapping.GetId(
                        world.ChunkData.GetVoxel(GRASS_X + 1, GRASS_Y, GRASS_Z)) == BlockIDs.Grass;

                    // The spread must land on exactly one of the two reservoir candidates (the apply path placed it).
                    if (leftConverted == rightConverted)
                    {
                        Debug.LogError($"[FAIL] BH-B6: expected exactly one candidate to convert to grass, got left={leftConverted} right={rightConverted}.");
                        return false;
                    }

                    return true;
                });

        /// <summary>
        /// Builds the BH-B6 fixture at the given grass column: a <see cref="BlockIDs.Grass"/> block at
        /// (<paramref name="gx"/>, <see cref="GRASS_Y"/>, <paramref name="gz"/>) with a convertible
        /// <see cref="BlockIDs.Dirt"/> cell on each horizontal side (air above each — the all-air chunk supplies it).
        /// </summary>
        /// <param name="gx">Grass column X (chunk-local).</param>
        /// <param name="gz">Grass column Z (chunk-local).</param>
        private static BehaviorTestWorld BuildBh6World(int gx, int gz)
        {
            BehaviorTestWorld world = new BehaviorTestWorld();
            world.SetBlock(gx, GRASS_Y, gz, BlockIDs.Grass);
            world.SetBlock(gx - 1, GRASS_Y, gz, BlockIDs.Dirt); // convertible (air above by default)
            world.SetBlock(gx + 1, GRASS_Y, gz, BlockIDs.Dirt); // convertible
            return world;
        }

        // --- BH-B7 fixture geometry: a single GRASS block with a solid STONE block directly on top of it, on an
        // otherwise empty (all-air) chunk. Grass's Behave "Condition 1" (solid block above → turn to dirt) fires
        // and RETURNS before any rng draw, so this scenario is DETERMINISTIC — no seed probe, no staggering. The
        // grass starts active (palette isActive=true → SetBlock registers it, mirroring production's neighbor
        // activation when the cap is placed). Interior placement (Tier-1).
        private const int CAP_GRASS_Y = 11;
        private const int CAP_GRASS_XZ = 8;
        private const int BH_B7_TICKS = 2; // T1: grass → dirt + drop from active set; T2: empty (termination)

        /// <summary>
        /// BH-B7 golden master (see <see cref="Bh7_GrassUnderSolidTurnsToDirt"/>), compared via
        /// <see cref="GoldenMaster.AssertOrCapture"/>. Frozen after a code-trace match (deterministic — the
        /// solid-on-top branch returns before any seeded-RNG use): the grass emits a single Dirt mod onto itself
        /// and immediately goes inactive, and the now-inactive dirt empties the active set on T2 (termination).
        /// </summary>
        private const string BH_B7_GOLDEN =
            @"T1
  (8,11,8) active=0 mods=[3@(8,11,8):00:0]
T2
";

        /// <summary>
        /// BH-B7: a grass block capped by a solid block turns to dirt. Asserts run-to-run determinism (BH-6),
        /// non-vacuous conversion (a Dirt mod is emitted), **termination** (the now-inactive dirt empties the
        /// active set), that the grass cell actually became <see cref="BlockIDs.Dirt"/>, and byte-equality with
        /// <see cref="BH_B7_GOLDEN"/>. Exercises the grass→dirt branch — the one grass path with no RNG gate.
        /// </summary>
        private static bool Bh7_GrassUnderSolidTurnsToDirt() =>
            RunGoldenScenario("BH-B7", BuildBh7World, BH_B7_TICKS, BH_B7_GOLDEN, expectTermination: true,
                extraChecks: (world, snap) =>
                {
                    // The grass cell must actually have flipped to dirt (the apply path placed the emitted mod).
                    bool becameDirt = BurstVoxelDataBitMapping.GetId(
                        world.ChunkData.GetVoxel(CAP_GRASS_XZ, CAP_GRASS_Y, CAP_GRASS_XZ)) == BlockIDs.Dirt;
                    if (!becameDirt)
                    {
                        Debug.LogError("[FAIL] BH-B7: the capped grass cell did not become dirt.");
                        return false;
                    }

                    return true;
                });

        /// <summary>
        /// Builds the BH-B7 fixture: a <see cref="BlockIDs.Grass"/> block at
        /// (<see cref="CAP_GRASS_XZ"/>, <see cref="CAP_GRASS_Y"/>, <see cref="CAP_GRASS_XZ"/>) with a solid
        /// <see cref="BlockIDs.Stone"/> block directly above it (the cap that triggers the grass→dirt conversion).
        /// </summary>
        private static BehaviorTestWorld BuildBh7World()
        {
            BehaviorTestWorld world = new BehaviorTestWorld();
            world.SetBlock(CAP_GRASS_XZ, CAP_GRASS_Y, CAP_GRASS_XZ, BlockIDs.Grass);
            world.SetBlock(CAP_GRASS_XZ, CAP_GRASS_Y + 1, CAP_GRASS_XZ, BlockIDs.Stone); // solid cap
            return world;
        }
    }
}
