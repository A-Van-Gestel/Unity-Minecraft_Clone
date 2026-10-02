using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization;
using System.Text;
using Data;
using Data.WorldTypes;
using Editor.DataGeneration;
using Editor.Validation.Framework;
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
    /// generated chunks it times these scans over the <b>same</b> finalized voxel data and reports
    /// best-batch mean µs/chunk, so the win can be re-measured after future changes:
    /// <list type="bullet">
    /// <item><b>T_old</b> — the original full managed scan (<c>World.BlockTypes[id].isActive</c> deref per voxel).</item>
    /// <item><b>T_bitmask</b> — the TG-2-era flat-map copy of <see cref="Chunk.OnDataPopulated"/> (flat <c>bool[]</c> read per voxel into a managed set), kept for continuity; old↔this isolates the Part B (load/replay) win.</item>
    /// <item><b>T_register</b> — <see cref="Chunk.RegisterActiveVoxelsFromJob"/> (unpack the job's short list); old↔this isolates the Part A (generation main-thread) win.</item>
    /// <item><b>T_job</b> — <see cref="ActiveVoxelScanJob"/> Burst execution; the work that now overlaps generation off the main thread.</item>
    /// <item><b>T_current first / re-entry</b> — the shipped <see cref="Chunk.OnDataPopulated"/> itself (section skip,
    /// <see cref="ChunkData.AddActiveVoxel(Vector3Int, ushort)"/> into the native buckets) on a populated
    /// <see cref="ChunkData"/>: once with empty buckets (a data-only chunk's first time in view) and once with them
    /// already full (re-entry). This is the ES-6.1 price; a stubbed <c>World.Instance</c> carries the real block
    /// database, and a bucket-count parity check against the job's list proves the scan did the work.</item>
    /// </list>
    /// Two scenarios are run: a normal land chunk (asset sea level, scan-dominated / sparse actives) and
    /// a flooded chunk (raised sea level, active-heavy worst case where bucket population dominates).
    /// Editor-only; never compiled into a build, and refuses to run in play mode (it replaces <c>World.Instance</c>).
    /// </summary>
    internal static class ActiveVoxelScanBenchmark
    {
        private const string SEED_DESCRIPTION = "seed 1337, Standard world type";
        private const int CHUNK_COUNT = 100;
        private const int REPEATS = 5; // repeat the whole batch; report the best (least-noisy) batch mean
        private const int FLOODED_SEA_LEVEL = 110; // raised sea level → chunks fill with active water source voxels
        private const int LIST_CAPACITY = StandardChunkGenerator.ActiveVoxelPresizeCapacity; // single source of truth for the pre-size

        private const string WORLD_TYPE_PATH = "Assets/Data/WorldGen/WorldTypes/Standard.asset";

        // ES-6.1 per-crossing pricing: a square view of radius vd admits 2·vd + 1 chunks per boundary crossing.
        private const int NEAR_VIEW_DISTANCE = 10;
        private const int FAR_VIEW_DISTANCE = 32;
        private const double PROPOSED_BAR_MS = 0.5; // ES-6.1's proposed materiality bar per crossing at FAR_VIEW_DISTANCE
        private const double PERCENTILE_95 = 0.95;

        /// <summary>Result of timing one scenario (one sea-level configuration).</summary>
        private struct ScenarioResult
        {
            public string Label;
            public double AvgActive;
            public double AvgNonEmptySections;
            public double OldUs; // managed deref, all voxels
            public double BitmaskUs; // flat bool[], all voxels
            public double RegisterUs; // unpack job list only
            public double JobUs; // Burst scan (off main thread)
            public double CurrentFirstUs; // shipped OnDataPopulated, empty buckets (best-batch mean)
            public double CurrentReentryUs; // shipped OnDataPopulated, buckets already full (best-batch mean)
            public double[] CurrentFirstSamplesUs; // every per-chunk sample across all batches
            public double[] CurrentReentrySamplesUs; // the same, for the re-entry leg
        }

        [MenuItem("Minecraft Clone/Benchmarks/Active-Voxel Scan (TG-2)")]
        private static void Run()
        {
            string outPath = Path.Combine(Application.temporaryCachePath, "active_voxel_scan_bench.txt");
            StringBuilder sb = new StringBuilder();
            if (EditorApplication.isPlaying)
            {
                sb.Append("ERROR: run outside play mode — the T_current legs replace World.Instance");
                Finish(outPath, sb);
                return;
            }

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
            try
            {
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
                double bestFirst = double.MaxValue, bestReentry = double.MaxValue;
                double[] firstSamples = new double[REPEATS * CHUNK_COUNT];
                double[] reentrySamples = new double[REPEATS * CHUNK_COUNT];
                double nonEmptySections = 0;

                using (new StubWorld(db, isActiveById))
                {
                    for (int r = 0; r < REPEATS; r++)
                    {
                        bestOld = Math.Min(bestOld, TimeOldScan(maps, db, activeSet));
                        bestBitmask = Math.Min(bestBitmask, TimeBitmaskScan(maps, isActiveById, activeSet));
                        bestRegister = Math.Min(bestRegister, TimeRegister(jobLists, activeSet));
                        bestJob = Math.Min(bestJob, TimeJob(maps, blockTypes));

                        TimeCurrentScan(maps, jobLists, r * CHUNK_COUNT, firstSamples, reentrySamples,
                            out double firstTotal, out double reentryTotal, out nonEmptySections);
                        bestFirst = Math.Min(bestFirst, firstTotal);
                        bestReentry = Math.Min(bestReentry, reentryTotal);
                    }
                }

                const double n = CHUNK_COUNT;
                return new ScenarioResult
                {
                    Label = label,
                    AvgActive = totalActive / (double)CHUNK_COUNT,
                    AvgNonEmptySections = nonEmptySections / n,
                    OldUs = bestOld / n,
                    BitmaskUs = bestBitmask / n,
                    RegisterUs = bestRegister / n,
                    JobUs = bestJob / n,
                    CurrentFirstUs = bestFirst / n,
                    CurrentReentryUs = bestReentry / n,
                    CurrentFirstSamplesUs = firstSamples,
                    CurrentReentrySamplesUs = reentrySamples,
                };
            }
            finally
            {
                runner.Dispose(); // disposes BlockTypesJobData — after all job timing, on every exit (incl. a failed parity check)
            }
        }

        private static void AppendScenario(StringBuilder sb, ScenarioResult r)
        {
            sb.Append("== ").Append(r.Label).Append(" — avg active voxels/chunk = ").Append(r.AvgActive.ToString("F1")).Append(" ==\n");
            sb.Append("  T_old      (managed deref, all voxels) = ").Append(r.OldUs.ToString("F2")).Append(" µs\n");
            sb.Append("  T_bitmask  (flat bool[], all voxels)   = ").Append(r.BitmaskUs.ToString("F2")).Append(" µs   [Part B: load/replay]\n");
            sb.Append("  T_register (unpack job list only)      = ").Append(r.RegisterUs.ToString("F2")).Append(" µs   [Part A: gen main-thread]\n");
            sb.Append("  T_job      (Burst, off main thread)    = ").Append(r.JobUs.ToString("F2")).Append(" µs\n");
            sb.Append("  Part B speedup (old/bitmask) = ").Append((r.OldUs / r.BitmaskUs).ToString("F2")).Append("x");
            sb.Append("   |   Part A main-thread reduction (old/register) = ").Append((r.OldUs / r.RegisterUs).ToString("F1")).Append("x\n");

            sb.Append("  -- ES-6.1: shipped Chunk.OnDataPopulated (avg non-empty sections/chunk = ")
                .Append(r.AvgNonEmptySections.ToString("F2")).Append("; parity OK) --\n");
            AppendCurrentLeg(sb, "T_current first    (empty buckets)", r.CurrentFirstUs, r.CurrentFirstSamplesUs);
            AppendCurrentLeg(sb, "T_current re-entry (buckets full) ", r.CurrentReentryUs, r.CurrentReentrySamplesUs);
        }

        /// <summary>Appends one shipped-scan leg: best-batch mean, per-chunk distribution, and per-crossing cost.</summary>
        private static void AppendCurrentLeg(StringBuilder sb, string label, double bestMeanUs, double[] samplesUs)
        {
            double[] sorted = (double[])samplesUs.Clone();
            Array.Sort(sorted);
            double medianUs = sorted[sorted.Length / 2];
            double p95Us = sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * PERCENTILE_95))];
            double maxUs = sorted[^1];

            sb.Append("  ").Append(label).Append(" = ").Append(bestMeanUs.ToString("F2")).Append(" µs")
                .Append("   [per-chunk median / p95 / max over ").Append(sorted.Length).Append(" samples = ")
                .Append(medianUs.ToString("F2")).Append(" / ").Append(p95Us.ToString("F2")).Append(" / ")
                .Append(maxUs.ToString("F2")).Append(" µs]\n");

            foreach (int vd in new[] { NEAR_VIEW_DISTANCE, FAR_VIEW_DISTANCE })
            {
                int entering = 2 * vd + 1;
                double meanMs = bestMeanUs * entering / 1000.0;
                double worstMs = maxUs * entering / 1000.0;
                sb.Append("      per crossing at vd ").Append(vd).Append(" (").Append(entering).Append(" chunks): ")
                    .Append(meanMs.ToString("F3")).Append(" ms mean, ").Append(worstMs.ToString("F3")).Append(" ms if every chunk hit the max");
                if (vd == FAR_VIEW_DISTANCE)
                    sb.Append("   [proposed bar ").Append(PROPOSED_BAR_MS.ToString("F1")).Append(" ms: ")
                        .Append(meanMs > PROPOSED_BAR_MS ? "ABOVE" : "below").Append(" on the mean]");
                sb.Append('\n');
            }
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

        /// <summary>
        /// Times the shipped <see cref="Chunk.OnDataPopulated"/> twice per chunk on a freshly populated
        /// <see cref="ChunkData"/> — empty buckets, then full — and checks that the buckets end up holding exactly the
        /// job's active list (the scan-vs-job parity invariant), so a vacuous scan cannot report a cheap time.
        /// </summary>
        private static void TimeCurrentScan(uint[][] maps, List<int[]> jobLists, int sampleOffset,
            double[] firstSamplesUs, double[] reentrySamplesUs,
            out double firstTotalUs, out double reentryTotalUs, out double nonEmptySections)
        {
            double ticksToUs = 1_000_000.0 / Stopwatch.Frequency;
            firstTotalUs = 0;
            reentryTotalUs = 0;
            nonEmptySections = 0;
            long totalRegistered = 0;

            for (int c = 0; c < maps.Length; c++)
            {
                ChunkData data = new ChunkData(Vector2Int.zero);
                NativeArray<uint> flat = new NativeArray<uint>(maps[c], Allocator.Temp);
                try
                {
                    data.PopulateFromFlattened(flat);
                    foreach (ChunkSection section in data.sections)
                    {
                        if (section != null && !section.IsEmpty) nonEmptySections++;
                    }

                    // The production call is on a pooled visual; an uninitialized Chunk carries only the data link
                    // OnDataPopulated reads (MeshingValidationSuite.Scheduling uses the same construction).
                    Chunk chunk = (Chunk)FormatterServices.GetUninitializedObject(typeof(Chunk));
                    chunk.ChunkData = data;

                    long t0 = Stopwatch.GetTimestamp();
                    chunk.OnDataPopulated();
                    long t1 = Stopwatch.GetTimestamp();
                    int afterFirst = RegisteredCount(data);
                    chunk.OnDataPopulated();
                    long t2 = Stopwatch.GetTimestamp();
                    int afterReentry = RegisteredCount(data);

                    if (afterFirst != jobLists[c].Length || afterReentry != afterFirst)
                        throw new InvalidOperationException(
                            $"Parity failed on chunk {c}: job list {jobLists[c].Length}, buckets after first scan " +
                            $"{afterFirst}, after re-entry {afterReentry}.");
                    totalRegistered += afterFirst;

                    double firstUs = (t1 - t0) * ticksToUs;
                    double reentryUs = (t2 - t1) * ticksToUs;
                    firstSamplesUs[sampleOffset + c] = firstUs;
                    reentrySamplesUs[sampleOffset + c] = reentryUs;
                    firstTotalUs += firstUs;
                    reentryTotalUs += reentryUs;
                }
                finally
                {
                    flat.Dispose();
                    data.Reset(Vector2Int.zero); // returns the sections to the stub world's pool
                    data.Dispose();
                }
            }

            if (totalRegistered == 0)
                throw new InvalidOperationException("Parity failed: the batch registered no active voxels at all.");
        }

        /// <summary>Total entries in a chunk's two active-voxel buckets (read by reflection: the buckets are internal).</summary>
        private static int RegisteredCount(ChunkData data) =>
            BucketCount(data, "_activeGrass") + BucketCount(data, "_activeFluids");

        private static int BucketCount(ChunkData data, string fieldName)
        {
            NativeHashSet<int> bucket = (NativeHashSet<int>)ValidationReflection.GetInstanceField(data, fieldName);
            return bucket.IsCreated ? bucket.Count : 0;
        }

        /// <summary>
        /// Stands in for <c>World.Instance</c> while the T_current legs run: the real block database (the bucket
        /// routing reads its fluid types), its active-id table, and a section pool. Restores the previous instance
        /// on dispose; the database is a shared asset and is never destroyed here.
        /// </summary>
        private sealed class StubWorld : IDisposable
        {
            private readonly World _previousInstance;
            private readonly GameObject _worldGo;

            /// <summary>Installs the stub as <c>World.Instance</c>.</summary>
            /// <param name="db">The block database the stub serves as <c>World.BlockTypes</c>.</param>
            /// <param name="isActiveById">The active-id table the shipped scan reads.</param>
            public StubWorld(BlockDatabase db, bool[] isActiveById)
            {
                _previousInstance = World.Instance;
                _worldGo = new GameObject("ActiveVoxelScanBenchmark_StubWorld");
                try
                {
                    // AddComponent runs no Awake in edit mode, so no world initialization fires.
                    World world = _worldGo.AddComponent<World>();
                    ValidationReflection.SetInstanceField(world, "_blockDatabase", db);
                    world.settings = new Settings();
                    world.worldData = new WorldData("ActiveVoxelScanBenchmark", 0);
                    world.IsActiveById = isActiveById;
                    ValidationReflection.SetInstanceProperty(world, nameof(World.ChunkPool), new ChunkPoolManager(_worldGo.transform));
                    ValidationReflection.SetStaticProperty(typeof(World), nameof(World.Instance), world);
                }
                catch
                {
                    Dispose();
                    throw;
                }
            }

            /// <inheritdoc/>
            public void Dispose()
            {
                ValidationReflection.SetStaticProperty(typeof(World), nameof(World.Instance), _previousInstance);
                if (_worldGo != null) UnityEngine.Object.DestroyImmediate(_worldGo);
            }
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
