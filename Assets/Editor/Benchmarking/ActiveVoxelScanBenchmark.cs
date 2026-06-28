using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Data;
using Data.WorldTypes;
using Editor.DataGeneration;
using Editor.WorldTools.Libraries;
using Helpers;
using Jobs;
using Jobs.BurstData;
using Jobs.Data;
using Jobs.Generators;
using Unity.Collections;
using Unity.Jobs;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Editor.Benchmarking
{
    /// <summary>
    /// Editor A/B microbenchmark for the TG-2 active-voxel optimization (see
    /// <c>Documentation/Design/PERFORMANCE_IMPROVEMENTS_REPORT.md</c> §TG-2). For a batch of freshly
    /// generated chunks it times four scans over the <b>same</b> finalized voxel data and reports
    /// best-batch mean µs/chunk, so the win can be re-measured after future changes:
    /// <list type="bullet">
    /// <item><b>T_old</b> — the original full managed scan (<c>World.BlockTypes[id].isActive</c> deref per voxel).</item>
    /// <item><b>T_bitmask</b> — the current <see cref="Chunk.OnDataPopulated"/> path (flat <c>bool[]</c> read per voxel); old↔this isolates the Part B (load/replay) win.</item>
    /// <item><b>T_register</b> — <see cref="Chunk.RegisterActiveVoxelsFromJob"/> (unpack the job's short list); old↔this isolates the Part A (generation main-thread) win.</item>
    /// <item><b>T_job</b> — <see cref="ActiveVoxelScanJob"/> Burst execution; the work that now overlaps generation off the main thread.</item>
    /// </list>
    /// Two scenarios are run: a normal land chunk (asset sea level, scan-dominated / sparse actives) and
    /// a flooded chunk (raised sea level, active-heavy worst case where the <c>HashSet</c> population
    /// dominates). Editor-only; never compiled into a build.
    /// </summary>
    internal static class ActiveVoxelScanBenchmark
    {
        private const string SEED_DESCRIPTION = "seed 1337, Standard world type";
        private const int CHUNK_COUNT = 100;
        private const int REPEATS = 5; // repeat the whole batch; report the best (least-noisy) batch mean
        private const int FLOODED_SEA_LEVEL = 110; // raised sea level → chunks fill with active water source voxels
        private const int LIST_CAPACITY = StandardChunkGenerator.ActiveVoxelPresizeCapacity; // single source of truth for the pre-size

        private const string WORLD_TYPE_PATH = "Assets/Data/WorldGen/WorldTypes/Standard.asset";

        /// <summary>Result of timing one scenario (one sea-level configuration).</summary>
        private struct ScenarioResult
        {
            public string Label;
            public double AvgActive;
            public double OldUs; // managed deref, all voxels
            public double BitmaskUs; // flat bool[], all voxels
            public double RegisterUs; // unpack job list only
            public double JobUs; // Burst scan (off main thread)
        }

        [MenuItem("Minecraft Clone/Benchmarks/Active-Voxel Scan (TG-2)")]
        private static void Run()
        {
            string outPath = Path.Combine(Application.temporaryCachePath, "active_voxel_scan_bench.txt");
            StringBuilder sb = new StringBuilder();
            try
            {
                WorldTypeDefinition worldType = AssetDatabase.LoadAssetAtPath<WorldTypeDefinition>(WORLD_TYPE_PATH);
                BlockDatabase db = EditorBlockDatabaseCache.Database;
                if (worldType == null || db == null)
                {
                    sb.Append("ERROR: could not load ").Append(WORLD_TYPE_PATH).Append(" / BlockDatabase");
                    Finish(outPath, sb);
                    return;
                }

                // Flat bitmask lookup, mirrors World.IsActiveById.
                bool[] isActiveById = new bool[db.blockTypes.Length];
                for (int i = 0; i < db.blockTypes.Length; i++) isActiveById[i] = db.blockTypes[i].isActive;

                ScenarioResult land = MeasureScenario("Land (asset sea level — sparse actives)", null, worldType, db, isActiveById);
                ScenarioResult flooded = MeasureScenario("Flooded (sea=" + FLOODED_SEA_LEVEL + " — active-heavy)", FLOODED_SEA_LEVEL, worldType, db, isActiveById);

                sb.Append("TG-2 Active-Voxel Scan A/B benchmark\n");
                sb.Append(CHUNK_COUNT).Append(" chunks × ").Append(REPEATS).Append(" batches (").Append(SEED_DESCRIPTION)
                    .Append("); best batch-mean µs/chunk.\n\n");
                AppendScenario(sb, land);
                sb.Append('\n');
                AppendScenario(sb, flooded);
            }
            catch (Exception e)
            {
                sb.Append("EXCEPTION: ").Append(e.GetType().Name).Append(": ").Append(e.Message).Append('\n').Append(e.StackTrace);
            }

            Finish(outPath, sb);
        }

        /// <summary>Generates a batch at the given sea level and times all four scans over it.</summary>
        private static ScenarioResult MeasureScenario(string label, int? seaLevelOverride,
            WorldTypeDefinition worldType, BlockDatabase db, bool[] isActiveById)
        {
            EditorChunkPipelineRunner runner = new EditorChunkPipelineRunner();
            runner.Initialize(1337, worldType, db);
            runner.SeaLevelOverride = seaLevelOverride;
            NativeArray<BlockTypeJobData> blockTypes = runner.JobDataManager.BlockTypesJobData;

            // Generate once; cache each chunk's finalized map (managed copy) + the job's emitted list.
            uint[][] maps = new uint[CHUNK_COUNT][];
            List<int[]> jobLists = new List<int[]>(CHUNK_COUNT);
            long totalActive = 0;
            for (int c = 0; c < CHUNK_COUNT; c++)
            {
                GenerationJobData data = runner.ScheduleGeneration(new ChunkCoord(c % 20, c / 20));
                data.Handle.Complete();

                uint[] map = new uint[data.Map.Length];
                data.Map.CopyTo(map);
                maps[c] = map;

                int[] list = new int[data.ActiveVoxels.Length];
                for (int k = 0; k < data.ActiveVoxels.Length; k++) list[k] = data.ActiveVoxels[k];
                jobLists.Add(list);
                totalActive += list.Length;

                data.Dispose();
            }

            HashSet<Vector3Int> activeSet = new HashSet<Vector3Int>();
            WarmupJob(maps[0], blockTypes); // first Run() includes Burst JIT — exclude it from timing

            double bestOld = double.MaxValue, bestBitmask = double.MaxValue, bestRegister = double.MaxValue, bestJob = double.MaxValue;
            for (int r = 0; r < REPEATS; r++)
            {
                bestOld = Math.Min(bestOld, TimeOldScan(maps, db, activeSet));
                bestBitmask = Math.Min(bestBitmask, TimeBitmaskScan(maps, isActiveById, activeSet));
                bestRegister = Math.Min(bestRegister, TimeRegister(jobLists, activeSet));
                bestJob = Math.Min(bestJob, TimeJob(maps, blockTypes));
            }

            runner.Dispose(); // disposes BlockTypesJobData — must outlive all job timing above

            const double n = CHUNK_COUNT;
            return new ScenarioResult
            {
                Label = label,
                AvgActive = totalActive / (double)CHUNK_COUNT,
                OldUs = bestOld / n,
                BitmaskUs = bestBitmask / n,
                RegisterUs = bestRegister / n,
                JobUs = bestJob / n,
            };
        }

        private static void AppendScenario(StringBuilder sb, ScenarioResult r)
        {
            sb.Append("== ").Append(r.Label).Append(" — avg active voxels/chunk = ").Append(r.AvgActive.ToString("F0")).Append(" ==\n");
            sb.Append("  T_old      (managed deref, all voxels) = ").Append(r.OldUs.ToString("F2")).Append(" µs\n");
            sb.Append("  T_bitmask  (flat bool[], all voxels)   = ").Append(r.BitmaskUs.ToString("F2")).Append(" µs   [Part B: load/replay]\n");
            sb.Append("  T_register (unpack job list only)      = ").Append(r.RegisterUs.ToString("F2")).Append(" µs   [Part A: gen main-thread]\n");
            sb.Append("  T_job      (Burst, off main thread)    = ").Append(r.JobUs.ToString("F2")).Append(" µs\n");
            sb.Append("  Part B speedup (old/bitmask) = ").Append((r.OldUs / r.BitmaskUs).ToString("F2")).Append("x");
            sb.Append("   |   Part A main-thread reduction (old/register) = ").Append((r.OldUs / r.RegisterUs).ToString("F1")).Append("x\n");
        }

        private static void Finish(string outPath, StringBuilder sb)
        {
            File.WriteAllText(outPath, sb.ToString());
            Debug.Log("[ActiveVoxelScanBench]\n" + sb + "\n(written to " + outPath + ")");
        }

        private static void WarmupJob(uint[] map, NativeArray<BlockTypeJobData> blockTypes)
        {
            NativeArray<uint> nMap = new NativeArray<uint>(map, Allocator.TempJob);
            NativeList<int> list = new NativeList<int>(LIST_CAPACITY, Allocator.TempJob);
            new ActiveVoxelScanJob { VoxelMap = nMap, BlockTypes = blockTypes, ActiveVoxels = list }.Run();
            nMap.Dispose();
            list.Dispose();
        }

        // Replica of the ORIGINAL OnDataPopulated inner loop (managed BlockType deref).
        private static double TimeOldScan(uint[][] maps, BlockDatabase db, HashSet<Vector3Int> sink)
        {
            BlockType[] blockTypes = db.blockTypes;
            Stopwatch sw = Stopwatch.StartNew();
            foreach (uint[] c in maps)
            {
                sink.Clear();
                uint[] map = c;
                for (int i = 0; i < map.Length; i++)
                {
                    ushort id = BurstVoxelDataBitMapping.GetId(map[i]);
                    if (blockTypes[id].isActive)
                    {
                        ChunkMath.GetLocalPositionFromFlattenedIndex(i, out int x, out int y, out int z);
                        sink.Add(new Vector3Int(x, y, z));
                    }
                }
            }

            sw.Stop();
            return sw.Elapsed.TotalMilliseconds * 1000.0;
        }

        // Replica of the CURRENT OnDataPopulated inner loop (flat bool[] read).
        private static double TimeBitmaskScan(uint[][] maps, bool[] isActiveById, HashSet<Vector3Int> sink)
        {
            Stopwatch sw = Stopwatch.StartNew();
            foreach (uint[] c in maps)
            {
                sink.Clear();
                uint[] map = c;
                for (int i = 0; i < map.Length; i++)
                {
                    ushort id = BurstVoxelDataBitMapping.GetId(map[i]);
                    if (isActiveById[id])
                    {
                        ChunkMath.GetLocalPositionFromFlattenedIndex(i, out int x, out int y, out int z);
                        sink.Add(new Vector3Int(x, y, z));
                    }
                }
            }

            sw.Stop();
            return sw.Elapsed.TotalMilliseconds * 1000.0;
        }

        // Replica of RegisterActiveVoxelsFromJob (unpack the precomputed short list).
        private static double TimeRegister(List<int[]> jobLists, HashSet<Vector3Int> sink)
        {
            Stopwatch sw = Stopwatch.StartNew();
            foreach (int[] c in jobLists)
            {
                sink.Clear();
                int[] list = c;
                foreach (int i in list)
                {
                    ChunkMath.GetLocalPositionFromFlattenedIndex(i, out int x, out int y, out int z);
                    sink.Add(new Vector3Int(x, y, z));
                }
            }

            sw.Stop();
            return sw.Elapsed.TotalMilliseconds * 1000.0;
        }

        // The Burst job itself (the work that now overlaps generation off the main thread).
        // NOTE: .Run() includes per-invocation scheduling overhead, so this overstates the real
        // per-chunk worker cost — the takeaway is that it is OFF the main thread, not added to it.
        private static double TimeJob(uint[][] maps, NativeArray<BlockTypeJobData> blockTypes)
        {
            double total = 0;
            foreach (uint[] c in maps)
            {
                NativeArray<uint> nMap = new NativeArray<uint>(c, Allocator.TempJob);
                NativeList<int> list = new NativeList<int>(LIST_CAPACITY, Allocator.TempJob);
                ActiveVoxelScanJob job = new ActiveVoxelScanJob { VoxelMap = nMap, BlockTypes = blockTypes, ActiveVoxels = list };
                Stopwatch sw = Stopwatch.StartNew();
                job.Run();
                sw.Stop();
                total += sw.Elapsed.TotalMilliseconds * 1000.0;
                nMap.Dispose();
                list.Dispose();
            }

            return total;
        }
    }
}
