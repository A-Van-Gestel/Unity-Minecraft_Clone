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
    /// TG-4 fluid-tick parallel determinism gate. Proves that <b>parallel</b> fluid scheduling (<c>.Schedule()</c>)
    /// produces <b>byte-identical</b> output to the <b>serial</b> path (<c>.Run()</c>) and is identical
    /// <b>run-to-run</b> (no data race): capture serial baselines, then schedule the work concurrently on pooled
    /// tickers — each with its own snapshot + neighbor-halo scratch, exactly as <c>World.TickChunksParallel</c> does —
    /// and assert every concurrent result equals its baseline, over <see cref="ROUNDS"/> rounds. A divergence means
    /// scratch bled between concurrent jobs, the schedule path differs from run, or a handle was read before completion.
    /// <para>
    /// <b><see cref="RunHaloBand"/>:</b> the shipped Y-band halo path (<see cref="FluidBurstTicker.ScheduleFluids"/> vs
    /// <see cref="FluidBurstTicker.RunFluids"/>) over a 3×3 grid of <b>distinct</b> chunks scheduled concurrently —
    /// each ticker fills its own 8 neighbor-snapshot buffers (<c>FluidBurstTicker._neighborBuffers</c>) from
    /// <b>different</b> neighbor data, the cross-job race surface. The behavior suite (BH-D1[L|HB]) guards the serial
    /// path + the job's per-voxel parity; this guards the parallel scheduling correctness on top. (The World-level
    /// drain orchestration + grass interleaving are exercised in-game.)
    /// </para>
    /// </summary>
    public static class FluidParallelDeterminismValidation
    {
        private const int ROUNDS = 6; // repeated rounds — run-to-run identity (race detection)
        private const int FIXED_TICK = 1; // fixed tick salt so the viscosity RNG is deterministic
        private const int FLOOR_Y = 63;
        private const int FLUID_Y = 64;

        // Phase 4b cross-seam scenario: an interior center origin so all 8 neighbor coords are in-world (the
        // IsVoxelInWorld bound SetNeighborBlock requires) and can be seeded. Chunk coord (8,8).
        private static readonly Vector2Int s_haloCenterOrigin = new Vector2Int(128, 128);

        /// <summary>
        /// TG-4 determinism gate: the shipped <b>Y-band halo</b> path (<see cref="FluidBurstTicker.ScheduleFluids"/> vs
        /// <see cref="FluidBurstTicker.RunFluids"/>) over a <b>multi-chunk</b> cross-seam flood.
        /// Floods a 3×3 grid of <b>distinct</b> chunks — each registered in the shared <see cref="WorldData"/> so it is
        /// its neighbors' neighbor, each with <b>different</b> content — and schedules all 9 concurrently, exactly as
        /// <c>World.TickChunksParallel</c> does. This exercises the Phase-4b race surface: the per-ticker
        /// neighbor-snapshot buffers. <see cref="FluidBurstTicker.PrepareNeighbors"/> fills them on the main thread at
        /// schedule time, but the job reads them later (after completion), so each in-flight job needs its <b>own</b>
        /// buffers that a subsequent chunk's prepare can't overwrite — a shared/bled buffer would feed a job the wrong
        /// neighbor and diverge. Each chunk's parallel output must equal its own serial <see cref="FluidBurstTicker.RunFluids"/>
        /// baseline, over <see cref="ROUNDS"/> rounds (run-to-run identity). The band extent is computed per-ticker
        /// from each chunk's own active fluids, so this also guards that band sizing is deterministic across the serial
        /// baseline, the concurrent schedule, and run-to-run.
        /// </summary>
        [MenuItem("Minecraft Clone/Dev/Validate Fluid Parallel Determinism (Cross-Chunk Halo, Y-band)")]
        public static void RunHaloBand() => RunHaloInternal();

        private static void RunHaloInternal()
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
                wd.SetChunk(origin, c);
                chunks.Add(c);
            }

            try
            {
                // Serial baselines, one per chunk (RunFluids = .Run(), the BH-D1[L|HB]-guarded path). RunFluids reads a
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
                // else the neighbor-halo scratch is never exercised.
                if (baselines[4].Interior.Length == 0 || !HasBorderVoxel(baselines[4].Interior))
                {
                    Debug.LogError("[FAIL] Fluid parallel determinism (Y-band halo): center chunk has no border " +
                                   "fluids — the neighbor-halo scratch is never exercised, so the test is vacuous.");
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
                    Debug.Log($"<color=green>[PASS] Fluid parallel determinism (cross-chunk halo, Y-band): {chunks.Count} distinct " +
                              $"chunks × {ROUNDS} concurrent rounds byte-identical to per-chunk serial baselines " +
                              $"({totalFluids} fluids, {totalMods} mods; center reads 8 distinct neighbors).</color>");
                }
                else
                {
                    Debug.LogError($"<color=red>[FAIL] Fluid parallel determinism (cross-chunk halo, Y-band): {failures} divergence(s) — see above.</color>");
                }
            }
            finally
            {
                foreach (ChunkData c in owned)
                    c.Dispose();
            }
        }

        /// <summary>
        /// Phase 4b: schedules all <paramref name="chunks"/> (distinct chunks) concurrently — each on its own pooled
        /// ticker via <see cref="FluidBurstTicker.ScheduleFluids"/> — and asserts each chunk's parallel output is
        /// byte-identical to its own serial <paramref name="baselines"/> entry, over <see cref="ROUNDS"/> rounds. This
        /// is the real <c>World.TickChunksParallel</c> shape: N in-flight jobs, each reading its own neighbor halo
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
        /// True if any flat index in <paramref name="fluidFlatIndices"/> is a <b>border</b> voxel — within
        /// <see cref="FluidTierClassifier.MaxFlowSearchDepth"/> (the max horizontal read reach) of an X/Z chunk edge,
        /// so its flow BFS can read across the seam into the gathered neighbor halo. Used to assert the halo gate is
        /// non-vacuous (the neighbor-buffer scratch is actually exercised).
        /// </summary>
        private static bool HasBorderVoxel(int[] fluidFlatIndices)
        {
            const int margin = FluidTierClassifier.MaxFlowSearchDepth;
            foreach (int flat in fluidFlatIndices)
            {
                ChunkMath.GetLocalPositionFromFlattenedIndex(flat, out int x, out int _, out int z);
                if (x < margin || x >= VoxelData.ChunkWidth - margin ||
                    z < margin || z >= VoxelData.ChunkWidth - margin)
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
