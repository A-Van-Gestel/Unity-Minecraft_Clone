using System;
using System.Collections.Generic;
using Data;
using Editor.Validation.Behavior.Framework;
using UnityEngine;

namespace Editor.Validation.Behavior
{
    /// <summary>
    /// <b>BH-D1</b> — the old-vs-new differential scenarios for the behavior-tick suite. Replays each fixture through
    /// two modeled tick drivers (<see cref="TickDriver"/>) over the same world + tick count and asserts the emitted
    /// <see cref="VoxelMod"/> streams are equivalent under the TG-4 §4.3 canonicalization (see
    /// <see cref="BehaviorDifferential"/>), plus a comparator self-test that proves the canonicalization keeps
    /// same-voxel writes order-sensitive while making independent mods order-independent.
    /// <para>
    /// This is the only guard that proves the <em>new</em> path equals the <em>old</em> one (the golden masters only
    /// prove each path equals its own frozen snapshot). It stays in the suite permanently and gates every TG-4 phase:
    /// Phase 0 wires legacy-vs-legacy (self-check) and Phase 1 adds legacy-vs-split-family (the first real reorder).
    /// </para>
    /// </summary>
    public static partial class BehaviorValidationSuite
    {
        /// <summary>A differential fixture: a named factory for a fresh world and the tick count to run it for.</summary>
        private readonly struct DiffFixture
        {
            public readonly string Name;
            public readonly Func<BehaviorTestWorld> Build;
            public readonly int Ticks;

            public DiffFixture(string name, Func<BehaviorTestWorld> build, int ticks)
            {
                Name = name;
                Build = build;
                Ticks = ticks;
            }
        }

        // BH-D1-MIX geometry: a grass-under-solid cell (deterministic grass→dirt, BH-B7 mechanic, no RNG) placed in a
        // far corner of the BH-B1 water world. It sits well outside the water's ≤3-cell reach over MIX_TICKS, so the
        // two families never interact — yet BOTH emit on tick 1 (water flow + the grass→dirt mod), giving the only
        // fixture with two non-empty family buckets AND real cross-family mods in the same tick.
        private const int MIX_GRASS_X = 2; // far corner, ≥5 cells from the water source at SOURCE_XZ (BH-B1 floor min is FLOOR_MIN_XZ=3)
        private const int MIX_GRASS_Z = 2;
        private const int MIX_TICKS = 3; // BH-B1's window: water emits each tick; grass→dirt fires on T1 then terminates

        /// <summary>
        /// The full behavior surface as differential fixtures — the seven golden worlds (BH-B1…B7) plus BH-D1-MIX,
        /// the one fixture with grass AND fluid active together (so the SplitFamily driver actually partitions two
        /// non-empty buckets — the single-family worlds collapse to the legacy order). Reuses the existing build
        /// delegates and tick-count constants so a fixture change updates both golden and differential at once.
        /// </summary>
        private static List<DiffFixture> DifferentialFixtures()
        {
            List<DiffFixture> fixtures = new List<DiffFixture>
            {
                new DiffFixture("BH-B1", BuildBh1World, BH_B1_TICKS),
                new DiffFixture("BH-B2", BuildBh2World, BH_B2_TICKS),
                new DiffFixture("BH-B3", BuildBh3World, BH_B3_TICKS),
                new DiffFixture("BH-B4", BuildBh4World, BH_B4_TICKS),
                new DiffFixture("BH-B5", BuildBh5World, BH_B5_TICKS),
                new DiffFixture("BH-B6", () => BuildBh6World(GRASS_X, GRASS_Z), BH_B6_TICKS),
                new DiffFixture("BH-B7", BuildBh7World, BH_B7_TICKS),
                new DiffFixture("BH-D1-MIX", BuildMixedFamilyWorld, MIX_TICKS),
            };

            // BH-4 (TG-4 Phase 4b): cross-chunk Tier-2 border fixtures. Green under today's managed-border drivers;
            // the prove-red → green gate for the Phase-4b halo driver (see BehaviorValidationSuite.CrossChunk.cs).
            fixtures.AddRange(CrossChunkFixtures());
            return fixtures;
        }

        /// <summary>
        /// Builds the mixed-family differential fixture: the BH-B1 water source on its stone floor, plus a
        /// grass-under-solid cell in a far corner (a <see cref="BlockIDs.Grass"/> block capped by
        /// <see cref="BlockIDs.Stone"/>, which deterministically turns to dirt on tick 1 — the BH-B7 mechanic, no
        /// RNG). Both families are active from the start and both emit on tick 1, so Legacy (one interleaved set) and
        /// SplitFamily (grass bucket then fluid bucket) traverse genuinely different orders — exercising the
        /// comparator's cross-family grouping and the driver partition that no single-family fixture reaches.
        /// </summary>
        private static BehaviorTestWorld BuildMixedFamilyWorld()
        {
            // Compose the exact BH-B1 water world (floor + single level-0 source, Tier-1 over MIX_TICKS) so this
            // fixture can never drift from the world it mirrors — then add only the grass feature.
            BehaviorTestWorld world = BuildBh1World();

            // Grass capped by a solid block → deterministic grass→dirt on T1 (BH-B7), in a corner the water can't reach.
            world.SetBlock(MIX_GRASS_X, GRASS_Y, MIX_GRASS_Z, BlockIDs.Grass);
            world.SetBlock(MIX_GRASS_X, GRASS_Y + 1, MIX_GRASS_Z, BlockIDs.Stone);
            return world;
        }

        /// <summary>Registers the BH-D1 differential scenarios (called from <see cref="AddBaselineScenarios"/>).</summary>
        private static void AddDifferentialScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario("BH-D1 self-test: §4.3 canonicalization has teeth", BhD1_ComparatorSelfTest));
            scenarios.Add(new Scenario("BH-D1: legacy vs legacy over all fixtures (comparator self-check)",
                () => BhD1_DifferentialOverFixtures("BH-D1[L|L]", TickDriver.Legacy, TickDriver.Legacy)));
            scenarios.Add(new Scenario("BH-D1: legacy vs split-family over all fixtures (§4.3 reorder gate)",
                () => BhD1_DifferentialOverFixtures("BH-D1[L|S]", TickDriver.Legacy, TickDriver.SplitFamily)));
            scenarios.Add(new Scenario("BH-D1: legacy vs fluid-Burst-hybrid over all fixtures (TG-4 Phase 3 interior-Burst gate)",
                () => BhD1_DifferentialOverFixtures("BH-D1[L|F]", TickDriver.Legacy, TickDriver.FluidBurstHybrid)));
            scenarios.Add(new Scenario("BH-D1: legacy vs fluid-Burst-halo over all fixtures (TG-4 Phase 4b border-Burst gate)",
                () => BhD1_DifferentialOverFixtures("BH-D1[L|H]", TickDriver.Legacy, TickDriver.FluidBurstHalo)));
        }

        /// <summary>
        /// Runs the BH-D1 differential over every <see cref="DifferentialFixtures"/> entry, comparing the two drivers
        /// under §4.3. Fails (and keeps going to report all offenders) if any fixture diverges.
        /// </summary>
        /// <param name="tag">Log tag identifying the driver pair (e.g. <c>"BH-D1[L|S]"</c>).</param>
        /// <param name="driverA">First driver.</param>
        /// <param name="driverB">Second driver.</param>
        /// <returns>True iff every fixture is §4.3-equivalent between the two drivers.</returns>
        private static bool BhD1_DifferentialOverFixtures(string tag, TickDriver driverA, TickDriver driverB)
        {
            bool ok = true;
            List<DiffFixture> fixtures = DifferentialFixtures();
            foreach (DiffFixture f in fixtures)
                if (!RunDifferential($"{tag}/{f.Name}", f.Build, f.Ticks, driverA, driverB))
                    ok = false;

            if (ok)
                Debug.Log($"[PASS] {tag}: all {fixtures.Count} fixtures equivalent under §4.3 ({driverA} vs {driverB}).");
            return ok;
        }

        /// <summary>
        /// Replays one fixture through <paramref name="driverA"/> and <paramref name="driverB"/> over identical fresh
        /// worlds and the same tick count, then asserts §4.3 stream-equivalence + final-state byte-identity.
        /// </summary>
        private static bool RunDifferential(string label, Func<BehaviorTestWorld> build, int ticks,
            TickDriver driverA, TickDriver driverB)
        {
            BehaviorSnapshot snapA;
            string stateA;
            using (BehaviorTestWorld world = build())
            {
                world.Driver = driverA;
                snapA = world.RunTicks(ticks);
                stateA = world.DumpVoxels();
            }

            BehaviorSnapshot snapB;
            string stateB;
            using (BehaviorTestWorld world = build())
            {
                world.Driver = driverB;
                snapB = world.RunTicks(ticks);
                stateB = world.DumpVoxels();
            }

            return BehaviorDifferential.AssertEquivalent(label, snapA, snapB, stateA, stateB);
        }

        /// <summary>
        /// Proves the §4.3 canonicalization (the heart of BH-D1) actually discriminates: a benign reorder of two
        /// <em>independent</em> mods across evals must canonicalize <b>equal</b>, while reordering two writes to the
        /// <em>same</em> target within a tick must canonicalize <b>different</b>. Without both, BH-D1 could either
        /// reject a correct TG-4 (false alarm) or mask a genuine same-voxel behavior change (false confidence).
        /// Compares canonical forms directly (not via <see cref="BehaviorDifferential.AssertEquivalent"/>) so the
        /// expected-divergent case does not emit a misleading <c>[FAIL]</c> log line.
        /// </summary>
        private static bool BhD1_ComparatorSelfTest()
        {
            bool ok = true;

            // (1) Two independent targets, emission order swapped across evals → MUST be equivalent.
            BehaviorSnapshot independentA = OneTick(
                Eval(new Vector3Int(0, 0, 0), new Vector3Int(1, 0, 0), BlockIDs.Water),
                Eval(new Vector3Int(1, 0, 0), new Vector3Int(2, 0, 0), BlockIDs.Lava));
            BehaviorSnapshot independentB = OneTick(
                Eval(new Vector3Int(1, 0, 0), new Vector3Int(2, 0, 0), BlockIDs.Lava),
                Eval(new Vector3Int(0, 0, 0), new Vector3Int(1, 0, 0), BlockIDs.Water));
            if (BehaviorDifferential.Canonicalize(independentA) != BehaviorDifferential.Canonicalize(independentB))
            {
                Debug.LogError("[FAIL] BH-D1 self-test: independent-mod reorder was NOT treated as equivalent.");
                ok = false;
            }

            // (2) Two writes to the SAME target, order swapped → MUST be distinct (order-sensitive).
            BehaviorSnapshot sameTargetA = OneTick(
                Eval(new Vector3Int(0, 0, 0), new Vector3Int(5, 0, 0), BlockIDs.Water),
                Eval(new Vector3Int(1, 0, 0), new Vector3Int(5, 0, 0), BlockIDs.Lava));
            BehaviorSnapshot sameTargetB = OneTick(
                Eval(new Vector3Int(0, 0, 0), new Vector3Int(5, 0, 0), BlockIDs.Lava),
                Eval(new Vector3Int(1, 0, 0), new Vector3Int(5, 0, 0), BlockIDs.Water));
            if (BehaviorDifferential.Canonicalize(sameTargetA) == BehaviorDifferential.Canonicalize(sameTargetB))
            {
                Debug.LogError("[FAIL] BH-D1 self-test: same-voxel write reorder was masked (order-sensitivity lost).");
                ok = false;
            }

            if (ok)
                Debug.Log("[PASS] BH-D1 self-test: same-voxel writes stay order-sensitive, independent mods order-independent.");
            return ok;
        }

        /// <summary>Builds a single-eval record: voxel at <paramref name="evalPos"/> emits one mod placing <paramref name="id"/> at <paramref name="target"/>.</summary>
        private static VoxelEval Eval(Vector3Int evalPos, Vector3Int target, ushort id) =>
            new VoxelEval(evalPos, true, new List<VoxelMod> { new VoxelMod(target, id) });

        /// <summary>Wraps the given evals (in order) into a one-tick snapshot for the comparator self-test.</summary>
        private static BehaviorSnapshot OneTick(params VoxelEval[] evals) =>
            new BehaviorSnapshot(new List<TickRecord> { new TickRecord(1, new List<VoxelEval>(evals)) });
    }
}
