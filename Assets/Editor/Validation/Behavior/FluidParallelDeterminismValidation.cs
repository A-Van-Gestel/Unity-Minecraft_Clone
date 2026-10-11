using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using Data;
using Editor.Dev;
using Editor.Validation.Behavior.Framework;
using Editor.Validation.Framework;
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
    /// tickers and assert every concurrent result equals its baseline, over <see cref="ROUNDS"/> rounds. A divergence
    /// means scratch bled between concurrent jobs, the schedule path differs from run, or a handle was read before
    /// completion.
    /// <para>
    /// <b><see cref="RunHaloBand"/>:</b> the shipped Y-band halo path over a 3×3 grid of <b>distinct</b> chunks, in two
    /// shapes. First each ticker schedules through <see cref="FluidBurstTicker.ScheduleFluids"/> on its own arena, the
    /// per-ticker race surface. Then the production <c>World.TickChunksParallel</c> ticks all nine through the one
    /// shared <see cref="TickSnapshotArena"/>: every chunk prepared before any job is scheduled, each section copied
    /// once for every job that reads it, and the drained modification stream in snapshot order. The behavior suite
    /// (BH-D1[L|HB]) guards the serial path + the job's per-voxel parity; this guards the parallel scheduling and the
    /// shared snapshot on top.
    /// </para>
    /// </summary>
    public static class FluidParallelDeterminismValidation
    {
        private const int ROUNDS = 6; // repeated rounds — run-to-run identity (race detection)
        private const int FIXED_TICK = 1; // fixed tick salt so the viscosity RNG is deterministic
        private const int FLOOR_Y = 63;
        private const int FLUID_Y = 64;

        // Sections the shared arena copies for the grid: every chunk's fluid band covers sections 3–4 (the floor at 63,
        // the water at 64) and its grass band section 2, and each of the nine chunks is copied once, though its own jobs
        // and up to eight neighbors' read it.
        private const int GRID_ARENA_SECTIONS = 9 * 3;

        // Grass seeded per grid chunk, at an X shifted by the chunk's salt: one under a stone cap (a deterministic dirt
        // modification at a chunk-specific position) and one beside convertible dirt (stays active).
        private const int GRASS_Y = 40;
        private const int CAPPED_GRASS_Z = 2;
        private const int ACTIVE_GRASS_Z = 8;

        // Phase 4b cross-seam scenario: an interior center origin so all 8 neighbor coords are in-world (the
        // IsVoxelInWorld bound SetNeighborBlock requires) and can be seeded. Chunk coord (8,8).
        private static readonly Vector2Int s_haloCenterOrigin = new Vector2Int(128, 128);

        /// <summary>
        /// TG-4 determinism gate: the shipped <b>Y-band halo</b> path (<see cref="FluidBurstTicker.ScheduleFluids"/> vs
        /// <see cref="FluidBurstTicker.RunFluids"/>) over a <b>multi-chunk</b> cross-seam flood.
        /// Floods a 3×3 grid of <b>distinct</b> chunks — each registered in the shared <see cref="WorldData"/> so it is
        /// its neighbors' neighbor, each with <b>different</b> content — and schedules all 9 concurrently, exactly as
        /// <c>World.TickChunksParallel</c> does. This exercises the Phase-4b race surface: each in-flight job's padded
        /// volume and section table must be its own, or a later chunk's prepare would feed an earlier job the wrong
        /// neighbor and diverge; and the shared arena must hand every job the right sections. Each chunk's parallel
        /// output must equal its own serial <see cref="FluidBurstTicker.RunFluids"/> baseline, over <see cref="ROUNDS"/>
        /// rounds (run-to-run identity). The band extent is computed per-ticker
        /// from each chunk's own active fluids, so this also guards that band sizing is deterministic across the serial
        /// baseline, the concurrent schedule, and run-to-run.
        /// </summary>
        [MenuItem("Minecraft Clone/Dev/Validate Fluid Parallel Determinism (Cross-Chunk Halo, Y-band)", priority = DevMenuPriority.DeepValidation)]
        public static void RunHaloBand() => RunHaloInternal();

        private static void RunHaloInternal()
        {
            using BehaviorTestWorld rig = new BehaviorTestWorld(s_haloCenterOrigin);
            rig.EnableModifyVoxel(); // gives the stub World the job palette StressWorldTick's real tick schedules with
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
                c.IsPopulated = true; // a seeded chunk models a GENERATED one; the halo skips placeholders (Fluid Bug 18)
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
                failures += StressWorldTick(chunks, baselines, GrassBaselines(chunks, wd, rig.BlockTypesJob));

                int totalMods = 0, totalFluids = 0;
                foreach (TickerOutput b in baselines)
                {
                    totalMods += b.Mods.Length;
                    totalFluids += b.Interior.Length;
                }

                if (failures == 0)
                {
                    Debug.Log($"<color=green>[PASS] Fluid parallel determinism (cross-chunk halo, Y-band): {chunks.Count} distinct " +
                              $"chunks × {ROUNDS} concurrent rounds, on per-ticker arenas and through World.TickChunksParallel's " +
                              $"shared arena, byte-identical to per-chunk serial baselines ({totalFluids} fluids, {totalMods} mods; " +
                              $"center reads 8 distinct neighbors; {GRID_ARENA_SECTIONS} sections copied per shared tick).</color>");
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
        /// The production shape: ticks all <paramref name="chunks"/> through <c>World.TickChunksParallel</c> on the rig's
        /// stub World, so every job reads the one shared <see cref="TickSnapshotArena"/>. Each round re-seeds the buckets
        /// the drain emptied (the drain only enqueues modifications, so the voxels stay at their seeded state), then
        /// asserts the drained stream equals the baselines' modifications concatenated in snapshot order, each chunk's
        /// bucket lost exactly its baseline's inactive voxels, and the arena copied <see cref="GRID_ARENA_SECTIONS"/>
        /// sections. Grass rides along: each chunk's grass baseline precedes its fluid baseline in the expected stream, as
        /// the drain replays grass first. Returns the divergence count (0 = pass).
        /// </summary>
        private static int StressWorldTick(List<ChunkData> chunks, TickerOutput[] baselines, TickerOutput[] grassBaselines)
        {
            World world = World.Instance;
            Queue<VoxelMod> modifications = (Queue<VoxelMod>)ValidationReflection.GetInstanceField(world, "_modifications");
            MethodInfo tick = typeof(World).GetMethod("TickChunksParallel", BindingFlags.NonPublic | BindingFlags.Instance);
            List<Chunk> snapshot = new List<Chunk>(chunks.Count);
            foreach (ChunkData c in chunks)
            {
                Chunk chunk = (Chunk)FormatterServices.GetUninitializedObject(typeof(Chunk));
                chunk.ChunkData = c;
                snapshot.Add(chunk);
            }

            List<VoxelMod> expected = new List<VoxelMod>();
            for (int i = 0; i < chunks.Count; i++)
            {
                expected.AddRange(grassBaselines[i].Mods);
                expected.AddRange(baselines[i].Mods);
            }

            int failures = 0;
            try
            {
                for (int round = 0; round < ROUNDS; round++)
                {
                    for (int i = 0; i < chunks.Count; i++)
                        SeedChunkFlood(chunks[i], i % 3 - 1, i / 3 - 1);
                    modifications.Clear();

                    tick.Invoke(world, new object[] { snapshot });

                    if (!SameStream(modifications, expected, out string why))
                    {
                        Debug.LogError($"[FAIL] World tick (round {round}) drained stream diverged from the serial baselines: {why}");
                        failures++;
                    }

                    for (int i = 0; i < chunks.Count; i++)
                    {
                        int expectedLeft = baselines[i].Interior.Length - baselines[i].Inactive.Length +
                                           grassBaselines[i].Interior.Length - grassBaselines[i].Inactive.Length;
                        if (chunks[i].GetActiveVoxelCount() != expectedLeft)
                        {
                            Debug.LogError($"[FAIL] World tick (round {round}) chunk {i} kept {chunks[i].GetActiveVoxelCount()} " +
                                           $"active voxels, expected {expectedLeft} after its baselines' inactive removals.");
                            failures++;
                        }
                    }

                    TickSnapshotArena arena = (TickSnapshotArena)ValidationReflection.GetInstanceField(world, "_tickArena");
                    if (arena == null || arena.SectionsCopied != GRID_ARENA_SECTIONS)
                    {
                        Debug.LogError($"[FAIL] World tick (round {round}) copied {(arena == null ? 0 : arena.SectionsCopied)} " +
                                       $"sections into the shared arena, expected {GRID_ARENA_SECTIONS} (each chunk's band once).");
                        failures++;
                    }
                }
            }
            finally
            {
                modifications.Clear();
                // World.OnDestroy, which frees the ticker pool and the arena, never runs for this edit-mode stub.
                (ValidationReflection.GetInstanceField(world, "_fluidTickerPool") as DynamicPool<FluidBurstTicker>)?.Clear();
                (ValidationReflection.GetInstanceField(world, "_grassTickerPool") as DynamicPool<GrassBurstTicker>)?.Clear();
                (ValidationReflection.GetInstanceField(world, "_tickArena") as TickSnapshotArena)?.Dispose();
            }

            return failures;
        }

        /// <summary>
        /// Serial grass baselines, one per chunk, on the stub World's tick salt (the one <c>World.TickChunksParallel</c>
        /// uses); the grass job's spread roll depends on it, unlike the water's.
        /// </summary>
        private static TickerOutput[] GrassBaselines(List<ChunkData> chunks, WorldData wd, NativeArray<BlockTypeJobData> blockTypes)
        {
            TickerOutput[] grass = new TickerOutput[chunks.Count];
            GrassBurstTicker ticker = new GrassBurstTicker();
            try
            {
                for (int i = 0; i < chunks.Count; i++)
                {
                    ticker.RunGrass(chunks[i], World.Instance.TickCounter, blockTypes, wd);
                    grass[i] = new TickerOutput(ToArray(ticker.Mods), ToArray(ticker.ModsPerSource),
                        ToArray(ticker.InactiveIndices), ToArray(ticker.Indices));
                }
            }
            finally
            {
                ticker.Dispose();
            }

            return grass;
        }

        private static bool SameStream(Queue<VoxelMod> queue, List<VoxelMod> expected, out string why)
        {
            if (queue.Count != expected.Count)
            {
                why = $"mod count {queue.Count} vs {expected.Count}";
                return false;
            }

            int i = 0;
            foreach (VoxelMod mod in queue)
            {
                if (!mod.Equals(expected[i]))
                {
                    why = $"mod[{i}] {mod} vs {expected[i]}";
                    return false;
                }

                i++;
            }

            why = null;
            return true;
        }

        /// <summary>
        /// Phase 4b: floods one chunk of the multi-chunk grid — a full 16×16 water pool over a stone floor, so every
        /// edge/corner column is a <b>Tier-2 border</b> voxel that reads the neighbor halo. The level gradient is
        /// shifted by a per-cell <paramref name="dx"/>/<paramref name="dz"/> salt so <b>each chunk's content differs</b>:
        /// adjacent chunks therefore present different neighbor snapshots, which is what makes a shared/bled neighbor
        /// buffer observable (identical content would mask it). Two grass voxels at a salt-shifted X ride along at
        /// <see cref="GRASS_Y"/>, far below the water's band, for the World-tick stress.
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

            c.SetVoxel(salt, GRASS_Y, CAPPED_GRASS_Z, BurstVoxelDataBitMapping.PackVoxelData(BlockIDs.Grass, 0));
            c.SetVoxel(salt, GRASS_Y + 1, CAPPED_GRASS_Z, BurstVoxelDataBitMapping.PackVoxelData(BlockIDs.Stone, 0));
            c.AddActiveVoxel(new Vector3Int(salt, GRASS_Y, CAPPED_GRASS_Z), BlockIDs.Grass);
            c.SetVoxel(salt, GRASS_Y, ACTIVE_GRASS_Z, BurstVoxelDataBitMapping.PackVoxelData(BlockIDs.Grass, 0));
            c.SetVoxel(salt + 1, GRASS_Y, ACTIVE_GRASS_Z, BurstVoxelDataBitMapping.PackVoxelData(BlockIDs.Dirt, 0));
            c.AddActiveVoxel(new Vector3Int(salt, GRASS_Y, ACTIVE_GRASS_Z), BlockIDs.Grass);
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
