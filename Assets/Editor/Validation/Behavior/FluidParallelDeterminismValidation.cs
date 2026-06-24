using System;
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
    /// TG-4 Phase 4a/4b fluid-tick parallel determinism gates. Both prove that <b>parallel</b> fluid scheduling
    /// (<c>.Schedule()</c>) produces <b>byte-identical</b> output to the <b>serial</b> path (<c>.Run()</c>) and is
    /// identical <b>run-to-run</b> (no data race): capture serial baselines, then schedule the work concurrently on
    /// pooled tickers — each with its own snapshot + output scratch, exactly as <c>World.ProcessTickUpdatesParallel</c>
    /// does — and assert every concurrent result equals its baseline, over <see cref="ROUNDS"/> rounds. A divergence
    /// means scratch bled between concurrent jobs, the schedule path differs from run, or a handle was read before
    /// completion.
    /// <list type="bullet">
    /// <item><b>Phase 4a — <see cref="Run"/>:</b> the interior path
    /// (<see cref="FluidBurstTicker.ScheduleInteriorFluids"/> vs <see cref="FluidBurstTicker.RunInteriorFluids"/>),
    /// scheduling one chunk through <see cref="CONCURRENT_TICKERS"/> concurrent tickers. Gates the
    /// <c>EnableParallelFluidTick</c> flag.</item>
    /// <item><b>Phase 4b — <see cref="RunHalo"/>:</b> the full halo path
    /// (<see cref="FluidBurstTicker.ScheduleFluids"/> vs <see cref="FluidBurstTicker.RunFluids"/>) over a 3×3 grid of
    /// <b>distinct</b> chunks scheduled concurrently — each ticker fills its own 8 neighbor-snapshot buffers
    /// (<c>FluidBurstTicker._neighborBuffers</c>) from <b>different</b> neighbor data, the additional race surface the
    /// interior gate can't reach. Gates the <c>EnableFluidBorderBurst</c> flag.</item>
    /// </list>
    /// <para>
    /// The behavior suite (BH-D1[L|F]/[L|H]) guards the serial path + the job's per-voxel parity; these guard the
    /// parallel scheduling correctness on top. (The World-level drain orchestration + grass interleaving are
    /// exercised in-game.)
    /// </para>
    /// </summary>
    public static class FluidParallelDeterminismValidation
    {
        private const int CONCURRENT_TICKERS = 8; // jobs in flight at once — the cross-job isolation surface
        private const int ROUNDS = 6; // repeated rounds — run-to-run identity (race detection)
        private const int FIXED_TICK = 1; // fixed tick salt so the viscosity RNG is deterministic
        private const int FLOOR_Y = 63;
        private const int FLUID_Y = 64;

        // Phase 4b cross-seam scenario: an interior center origin so all 8 neighbor coords are in-world (the
        // IsVoxelInWorld bound SetNeighborBlock requires) and can be seeded. Chunk coord (8,8).
        private static readonly Vector2Int s_haloCenterOrigin = new Vector2Int(128, 128);

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

            // Hoist the captured members into locals so the schedule lambda doesn't close over the using-scoped rig
            // (the capture is safe — StressAgainstBaseline runs synchronously before rig disposes — but this silences
            // the "captured variable disposed in outer scope" inspection).
            ChunkData cd = rig.ChunkData;
            NativeArray<BlockTypeJobData> blockTypes = rig.BlockTypesJob;
            int failures = StressAgainstBaseline(baseline,
                t => t.ScheduleInteriorFluids(cd, FIXED_TICK, blockTypes));

            if (failures == 0)
            {
                Debug.Log($"<color=green>[PASS] Fluid parallel determinism (interior): {CONCURRENT_TICKERS}×{ROUNDS} " +
                          $"concurrent runs byte-identical to the serial baseline ({baseline.Mods.Length} mods, " +
                          $"{baseline.Interior.Length} interior voxels).</color>");
            }
            else
            {
                Debug.LogError($"<color=red>[FAIL] Fluid parallel determinism (interior): {failures} divergence(s) — see above.</color>");
            }
        }

        /// <summary>
        /// TG-4 Phase 4b determinism gate: the <b>full halo</b> path (<see cref="FluidBurstTicker.ScheduleFluids"/> vs
        /// <see cref="FluidBurstTicker.RunFluids"/>) over a <b>multi-chunk</b> cross-seam flood. Unlike the interior
        /// gate (<see cref="Run"/>), which replays one chunk N times, this floods a 3×3 grid of <b>distinct</b> chunks
        /// — each registered in the shared <see cref="WorldData"/> so it is its neighbors' neighbor, each with
        /// <b>different</b> content — and schedules all 9 concurrently, exactly as <c>World.ProcessTickUpdatesParallel</c>
        /// does. This is the only configuration that exercises the unique Phase-4b race surface: the per-ticker
        /// neighbor-snapshot buffers. <see cref="FluidBurstTicker.PrepareNeighbors"/> fills them on the main thread at
        /// schedule time, but the job reads them later (after completion), so each in-flight job needs its <b>own</b>
        /// buffers that a subsequent chunk's prepare can't overwrite — a shared/bled buffer would feed a job the wrong
        /// neighbor and diverge. Each chunk's parallel output must equal its own serial <see cref="RunFluids"/>
        /// baseline, over <see cref="ROUNDS"/> rounds (run-to-run identity). Guards flipping
        /// <c>EnableFluidBorderBurst</c> on.
        /// </summary>
        [MenuItem("Minecraft Clone/Dev/Validate Fluid Parallel Determinism (Cross-Chunk Halo)")]
        public static void RunHalo()
        {
            using BehaviorTestWorld rig = new BehaviorTestWorld(s_haloCenterOrigin);
            WorldData wd = rig.WorldData;

            // Build a 3×3 grid of distinct flooded chunks around the center, each registered in the shared WorldData so
            // every chunk is a real neighbor of the others. The center (index 4) reads all 8 distinct neighbors; edge
            // chunks read fewer (the rest resolve to void). rig owns the center ChunkData; we own + dispose the other 8.
            List<ChunkData> chunks = new List<ChunkData>(9);
            List<ChunkData> owned = new List<ChunkData>(8);
            for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                Vector2Int origin = new Vector2Int(
                    s_haloCenterOrigin.x + dx * VoxelData.ChunkWidth,
                    s_haloCenterOrigin.y + dz * VoxelData.ChunkWidth);

                ChunkData c;
                if (dx == 0 && dz == 0)
                {
                    c = rig.ChunkData; // rig-owned (disposed by the rig)
                }
                else
                {
                    c = new ChunkData(origin);
                    owned.Add(c);
                }

                SeedChunkFlood(c, dx, dz); // distinct per-chunk content so neighbor buffers differ
                wd.Chunks[origin] = c;
                chunks.Add(c);
            }

            try
            {
                // Serial baselines, one per chunk (RunFluids = .Run(), the BH-D1[L|H]-guarded path). RunFluids reads a
                // pre-tick snapshot and never mutates, so computing every baseline leaves all 9 chunks at their seeded
                // state — exactly what the concurrent pass reads.
                TickerOutput[] baselines = new TickerOutput[chunks.Count];
                for (int i = 0; i < chunks.Count; i++)
                {
                    FluidBurstTicker serial = new FluidBurstTicker();
                    serial.RunFluids(chunks[i], FIXED_TICK, rig.BlockTypesJob, wd);
                    baselines[i] = Capture(serial);
                    serial.Dispose();
                }

                // Non-vacuity: the center chunk must actually have border voxels reading its 8 distinct neighbors,
                // else we'd be exercising nothing the interior gate doesn't already cover.
                if (baselines[4].Interior.Length == 0 || !HasBorderVoxel(baselines[4].Interior))
                {
                    Debug.LogError("[FAIL] Fluid parallel determinism (halo): center chunk has no Tier-2 border fluids — " +
                                   "the neighbor-halo scratch is never exercised, so the test is vacuous.");
                    return;
                }

                int failures = StressMultiChunk(chunks, baselines, wd, rig.BlockTypesJob);

                int totalMods = 0, totalFluids = 0;
                foreach (TickerOutput b in baselines)
                {
                    totalMods += b.Mods.Length;
                    totalFluids += b.Interior.Length;
                }

                if (failures == 0)
                {
                    Debug.Log($"<color=green>[PASS] Fluid parallel determinism (cross-chunk halo): {chunks.Count} distinct " +
                              $"chunks × {ROUNDS} concurrent rounds byte-identical to per-chunk serial baselines " +
                              $"({totalFluids} fluids, {totalMods} mods; center reads 8 distinct neighbors).</color>");
                }
                else
                {
                    Debug.LogError($"<color=red>[FAIL] Fluid parallel determinism (cross-chunk halo): {failures} divergence(s) — see above.</color>");
                }
            }
            finally
            {
                foreach (ChunkData c in owned)
                    c.Dispose();
            }
        }

        /// <summary>
        /// Schedules <see cref="CONCURRENT_TICKERS"/> concurrent jobs (via <paramref name="schedule"/>, each on its own
        /// pooled ticker) over <see cref="ROUNDS"/> rounds and asserts every concurrent result is byte-identical to
        /// <paramref name="baseline"/>. Shared by the interior (<see cref="Run"/>) and halo (<see cref="RunHalo"/>)
        /// gates — only the schedule call differs. Returns the number of divergences (0 = pass); each is logged.
        /// </summary>
        /// <param name="baseline">The serial baseline every concurrent run must match.</param>
        /// <param name="schedule">Schedules one job on the supplied pooled ticker and returns its handle.</param>
        private static int StressAgainstBaseline(TickerOutput baseline, Func<FluidBurstTicker, JobHandle> schedule)
        {
            DynamicPool<FluidBurstTicker> pool =
                new DynamicPool<FluidBurstTicker>(() => new FluidBurstTicker(), t => t.Dispose());

            int failures = 0;
            for (int round = 0; round < ROUNDS; round++)
            {
                List<FluidBurstTicker> tickers = new List<FluidBurstTicker>(CONCURRENT_TICKERS);
                NativeArray<JobHandle> handles = new NativeArray<JobHandle>(CONCURRENT_TICKERS, Allocator.Temp);

                // Schedule CONCURRENT_TICKERS jobs over the same chunk, each on its own pooled ticker.
                for (int k = 0; k < CONCURRENT_TICKERS; k++)
                {
                    FluidBurstTicker t = pool.Get();
                    tickers.Add(t);
                    handles[k] = schedule(t);
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
            return failures;
        }

        /// <summary>
        /// Phase 4b: schedules all <paramref name="chunks"/> (distinct chunks) concurrently — each on its own pooled
        /// ticker via <see cref="FluidBurstTicker.ScheduleFluids"/> — and asserts each chunk's parallel output is
        /// byte-identical to its own serial <paramref name="baselines"/> entry, over <see cref="ROUNDS"/> rounds. This
        /// is the real <c>ProcessTickUpdatesParallel</c> shape: N in-flight jobs, each reading its own neighbor halo
        /// gathered from a different region of the shared <paramref name="wd"/>. Returns the divergence count (0 = pass).
        /// </summary>
        private static int StressMultiChunk(List<ChunkData> chunks, TickerOutput[] baselines, WorldData wd,
            NativeArray<BlockTypeJobData> blockTypes)
        {
            DynamicPool<FluidBurstTicker> pool =
                new DynamicPool<FluidBurstTicker>(() => new FluidBurstTicker(), t => t.Dispose());

            int n = chunks.Count;
            int failures = 0;
            for (int round = 0; round < ROUNDS; round++)
            {
                List<FluidBurstTicker> tickers = new List<FluidBurstTicker>(n);
                NativeArray<JobHandle> handles = new NativeArray<JobHandle>(n, Allocator.Temp);

                // Schedule all N distinct chunks concurrently, each on its own pooled ticker — the real parallel pass.
                // The per-ticker neighbor buffers must survive until completion, so a shared buffer overwritten by a
                // later chunk's prepare would feed an earlier job stale neighbor data and diverge below.
                for (int i = 0; i < n; i++)
                {
                    FluidBurstTicker t = pool.Get();
                    tickers.Add(t);
                    handles[i] = t.ScheduleFluids(chunks[i], FIXED_TICK, blockTypes, wd);
                }

                JobHandle.ScheduleBatchedJobs();
                JobHandle.CompleteAll(handles);
                handles.Dispose();

                for (int i = 0; i < n; i++)
                {
                    if (!Equal(baselines[i], Capture(tickers[i]), out string why))
                    {
                        Debug.LogError($"[FAIL] chunk {i} (round {round}) parallel output diverged from its serial baseline: {why}");
                        failures++;
                    }
                }

                for (int i = 0; i < n; i++)
                    pool.Return(tickers[i]);
            }

            pool.Clear(); // disposes every pooled ticker
            return failures;
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

        /// <summary>
        /// Phase 4b: floods one chunk of the multi-chunk grid — a full 16×16 water pool over a stone floor, so every
        /// edge/corner column is a <b>Tier-2 border</b> voxel that reads the neighbor halo. The level gradient is
        /// shifted by a per-cell <paramref name="dx"/>/<paramref name="dz"/> salt so <b>each chunk's content differs</b>:
        /// adjacent chunks therefore present different neighbor snapshots, which is what makes a shared/bled neighbor
        /// buffer observable (identical content would mask it).
        /// </summary>
        private static void SeedChunkFlood(ChunkData c, int dx, int dz)
        {
            int salt = (dx + 1) * 3 + (dz + 1); // 0..8, distinct per grid cell — shifts each chunk's level gradient
            for (int x = 0; x < VoxelData.ChunkWidth; x++)
            for (int z = 0; z < VoxelData.ChunkWidth; z++)
            {
                c.SetVoxel(x, FLOOR_Y, z, BurstVoxelDataBitMapping.PackVoxelData(BlockIDs.Stone, 0));

                byte level = (byte)((x + z + salt) % 8); // 0 = source
                byte meta = BurstVoxelDataBitMapping.BuildMetaLegacy(0, level, true);
                c.SetVoxel(x, FLUID_Y, z, BurstVoxelDataBitMapping.PackVoxelData(BlockIDs.Water, meta));
                c.AddActiveVoxel(new Vector3Int(x, FLUID_Y, z), BlockIDs.Water);
            }
        }

        /// <summary>
        /// True if any flat index in <paramref name="fluidFlatIndices"/> is a Tier-2 <b>border</b> voxel (outside the
        /// margin-4 interior) — i.e. a voxel that reads the gathered neighbor halo. Used to assert the halo gate is
        /// non-vacuous (the neighbor-buffer scratch is actually exercised).
        /// </summary>
        private static bool HasBorderVoxel(int[] fluidFlatIndices)
        {
            foreach (int flat in fluidFlatIndices)
            {
                ChunkMath.GetLocalPositionFromFlattenedIndex(flat, out int x, out int y, out int z);
                if (!FluidTierClassifier.IsTier1Interior(x, y, z))
                    return true;
            }

            return false;
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
