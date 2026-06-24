using System.Collections.Generic;
using Data;
using Editor.Validation.Behavior.Framework;
using Helpers;
using Jobs;
using Jobs.BurstData;
using Unity.Collections;
using Unity.Jobs;
using UnityEditor;
using UnityEngine;

namespace Editor.Validation.Behavior
{
    /// <summary>
    /// TG-4 Phase 4a determinism gate. Proves the <b>parallel</b> interior-fluid scheduling
    /// (<see cref="FluidBurstTicker.ScheduleInteriorFluids"/> = <c>.Schedule()</c>) produces <b>byte-identical</b>
    /// output to the <b>serial</b> path (<see cref="FluidBurstTicker.RunInteriorFluids"/> = <c>.Run()</c>) and is
    /// identical <b>run-to-run</b> (no data race). It seeds one interior fluid chunk, captures the serial baseline,
    /// then schedules that same chunk through <see cref="CONCURRENT_TICKERS"/> concurrent pooled tickers — each
    /// with its own snapshot + output scratch, exactly as <c>World.ProcessTickUpdatesParallel</c> does — and asserts
    /// every concurrent result equals the baseline, over <see cref="ROUNDS"/> rounds. A divergence means scratch
    /// bled between concurrent jobs, the schedule path differs from run, or a handle was read before completion.
    /// <para>
    /// This is the gate that lets the <c>EnableParallelFluidTick</c> flag be flipped on: the behavior suite guards
    /// the serial path + the job's per-voxel parity, and this guards the parallel scheduling correctness on top.
    /// (The World-level drain orchestration + grass interleaving are exercised in-game.)
    /// </para>
    /// </summary>
    public static class FluidParallelDeterminismValidation
    {
        private const int CONCURRENT_TICKERS = 8; // jobs in flight at once — the cross-job isolation surface
        private const int ROUNDS = 6;             // repeated rounds — run-to-run identity (race detection)
        private const int FIXED_TICK = 1;         // fixed tick salt so the viscosity RNG is deterministic
        private const int FLOOR_Y = 63;
        private const int FLUID_Y = 64;

        [MenuItem("Minecraft Clone/Dev/Validate Fluid Parallel Determinism")]
        public static void Run()
        {
            using BehaviorTestWorld rig = new BehaviorTestWorld();
            SeedInteriorFluids(rig.ChunkData);

            // Serial baseline — the path the behavior differential already guards (RunInteriorFluids = .Run()).
            FluidBurstTicker serial = new FluidBurstTicker();
            serial.RunInteriorFluids(rig.ChunkData, FIXED_TICK, rig.BlockTypesJob);
            TickerOutput baseline = Capture(serial);
            serial.Dispose();

            if (baseline.Interior.Length == 0)
            {
                Debug.LogError("[FAIL] Fluid parallel determinism: seed produced no interior fluids — the test is vacuous.");
                return;
            }

            DynamicPool<FluidBurstTicker> pool =
                new DynamicPool<FluidBurstTicker>(() => new FluidBurstTicker(), t => t.Dispose());

            int failures = 0;
            for (int round = 0; round < ROUNDS; round++)
            {
                List<FluidBurstTicker> tickers = new List<FluidBurstTicker>(CONCURRENT_TICKERS);
                NativeArray<JobHandle> handles = new NativeArray<JobHandle>(CONCURRENT_TICKERS, Allocator.Temp);

                // Schedule CONCURRENT_TICKERS interior jobs over the same chunk, each on its own pooled ticker.
                for (int k = 0; k < CONCURRENT_TICKERS; k++)
                {
                    FluidBurstTicker t = pool.Get();
                    tickers.Add(t);
                    handles[k] = t.ScheduleInteriorFluids(rig.ChunkData, FIXED_TICK, rig.BlockTypesJob);
                }

                JobHandle.ScheduleBatchedJobs();
                JobHandle.CompleteAll(handles);
                handles.Dispose();

                // Every concurrent result must equal the serial baseline (Schedule == Run, no cross-job bleed).
                for (int k = 0; k < CONCURRENT_TICKERS; k++)
                {
                    if (!Equal(baseline, Capture(tickers[k]), out string why))
                    {
                        Debug.LogError($"[FAIL] parallel ticker {k} (round {round}) diverged from serial baseline: {why}");
                        failures++;
                    }
                }

                for (int k = 0; k < CONCURRENT_TICKERS; k++)
                    pool.Return(tickers[k]);
            }

            pool.Clear(); // disposes every pooled ticker

            if (failures == 0)
            {
                Debug.Log($"<color=green>[PASS] Fluid parallel determinism: {CONCURRENT_TICKERS}×{ROUNDS} concurrent " +
                          $"runs byte-identical to the serial baseline ({baseline.Mods.Length} mods, " +
                          $"{baseline.Interior.Length} interior voxels).</color>");
            }
            else
            {
                Debug.LogError($"<color=red>[FAIL] Fluid parallel determinism: {failures} divergence(s) — see above.</color>");
            }
        }

        /// <summary>
        /// Seeds <paramref name="cd"/> with a margin-4 interior fluid pool over a stone floor — enough active
        /// interior fluids to exercise decay, vertical flow, horizontal spread, and the drop-search BFS (the
        /// threaded per-Execute scratch). One floor cell is left open so the BFS finds a real drop path.
        /// </summary>
        private static void SeedInteriorFluids(ChunkData cd)
        {
            // Solid floor beneath the fluids, spanning the interior + 2 cells of margin so neighbour/below reads
            // resolve to a real block. One interior hole gives the drop-search BFS an actual drop to find.
            for (int x = 2; x <= 13; x++)
            for (int z = 2; z <= 13; z++)
            {
                if (x == 8 && z == 8) continue; // drop hole
                cd.SetVoxel(x, FLOOR_Y, z, BurstVoxelDataBitMapping.PackVoxelData(BlockIDs.Stone, 0));
            }

            // Interior water pool (x,z in [4,11] = margin-4 interior) with a deterministic level gradient so a mix
            // of sources (level 0) and flowing levels emit decay/spread/vertical mods. All registered active.
            for (int x = 4; x <= 11; x++)
            for (int z = 4; z <= 11; z++)
            {
                byte level = (byte)((x + z) % 8); // 0 = source
                byte meta = BurstVoxelDataBitMapping.BuildMetaLegacy(0, level, true);
                cd.SetVoxel(x, FLUID_Y, z, BurstVoxelDataBitMapping.PackVoxelData(BlockIDs.Water, meta));
                cd.AddActiveVoxel(new Vector3Int(x, FLUID_Y, z), BlockIDs.Water);
            }
        }

        /// <summary>An immutable copy of a ticker's per-run outputs, for comparing serial vs parallel.</summary>
        private readonly struct TickerOutput
        {
            public readonly VoxelMod[] Mods;
            public readonly int[] ModsPerSource;
            public readonly int[] Inactive;
            public readonly int[] Interior;

            public TickerOutput(VoxelMod[] mods, int[] modsPerSource, int[] inactive, int[] interior)
            {
                Mods = mods;
                ModsPerSource = modsPerSource;
                Inactive = inactive;
                Interior = interior;
            }
        }

        private static TickerOutput Capture(FluidBurstTicker t) =>
            new TickerOutput(ToArray(t.Mods), ToArray(t.ModsPerSource), ToArray(t.InactiveInterior), ToArray(t.InteriorIndices));

        private static VoxelMod[] ToArray(NativeList<VoxelMod> list)
        {
            VoxelMod[] a = new VoxelMod[list.Length];
            for (int i = 0; i < list.Length; i++) a[i] = list[i];
            return a;
        }

        private static int[] ToArray(NativeList<int> list)
        {
            int[] a = new int[list.Length];
            for (int i = 0; i < list.Length; i++) a[i] = list[i];
            return a;
        }

        private static bool Equal(TickerOutput a, TickerOutput b, out string why)
        {
            if (a.Mods.Length != b.Mods.Length)
            {
                why = $"mod count {a.Mods.Length} vs {b.Mods.Length}";
                return false;
            }

            for (int i = 0; i < a.Mods.Length; i++)
            {
                if (!a.Mods[i].Equals(b.Mods[i]))
                {
                    why = $"mod[{i}] {a.Mods[i]} vs {b.Mods[i]}";
                    return false;
                }
            }

            return SeqEqual(a.ModsPerSource, b.ModsPerSource, "modsPerSource", out why)
                   && SeqEqual(a.Inactive, b.Inactive, "inactive", out why)
                   && SeqEqual(a.Interior, b.Interior, "interior", out why);
        }

        private static bool SeqEqual(int[] a, int[] b, string name, out string why)
        {
            if (a.Length != b.Length)
            {
                why = $"{name} count {a.Length} vs {b.Length}";
                return false;
            }

            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                {
                    why = $"{name}[{i}] {a[i]} vs {b[i]}";
                    return false;
                }
            }

            why = null;
            return true;
        }
    }
}
