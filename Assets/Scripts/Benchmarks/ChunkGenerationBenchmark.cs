using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Data;
using Data.WorldTypes;
using Helpers;
using Jobs.Data;
using Jobs.Generators;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.InputSystem;
using Debug = UnityEngine.Debug;

namespace Benchmarks
{
    /// <summary>
    /// Runtime (IL2CPP-capable) micro-benchmark for the chunk <b>generation</b> path. For each terrain
    /// scenario it times the production <see cref="StandardChunkGenerator.ScheduleGeneration"/> → job
    /// <c>Complete</c> → release cycle under two allocation legs, isolating the main-thread alloc/free
    /// churn that the TG-6 active-voxel list pool removes:
    /// <list type="bullet">
    /// <item><b>Scenario — Land:</b> the scene world type's sea level (sparse active voxels, the common chunk).</item>
    /// <item><b>Scenario — Ocean:</b> a raised sea level (<see cref="_oceanSeaLevel"/>) so generated terrain
    /// floods — the gen job emits thousands of active water source voxels, growing the active-voxel
    /// <c>NativeList</c> past its presize. This is the realloc-growth case the warmed pool also removes.
    /// (Same mechanism as the editor <c>ActiveVoxelScanBenchmark</c>'s flooded scenario — terrain is
    /// <i>generated</i> water-heavy; nothing is fluid-ticked, so this stays a pure generation benchmark.)</item>
    /// <item><b>Leg — fresh:</b> a new <c>NativeList&lt;int&gt;(…, Persistent)</c> per chunk (pre-TG-6 path).</item>
    /// <item><b>Leg — pooled:</b> rented from a warmed <see cref="ActiveVoxelListPool"/> (TG-6); the fresh→pooled
    /// delta is the win.</item>
    /// </list>
    /// <para>
    /// <b>What the columns mean.</b> TG-6's main-thread cost is the per-chunk active-voxel-list alloc (at
    /// schedule) and free (at release); the realloc-growth saving is worker-side (inside the scan job, so it
    /// surfaces in <c>total</c>, overlapped). All columns are the <b>min over runs</b> — the clean,
    /// GC/scheduler-spike-free CPU floor (best proxy for the alloc cost). The win is a <c>Persistent</c>
    /// (native, not GC) allocation that is sub-µs and mostly off the frame's critical path, so even here the
    /// fresh→pooled delta is expected to be small; this benchmark's lasting value is as a standing,
    /// build-comparable regression guard for the whole generation path, with TG-6 riding along.
    /// </para>
    /// <para>
    /// <b>Own generator.</b> The benchmark stands up its own <see cref="StandardChunkGenerator"/> (initialized
    /// from <c>World.Instance</c>'s <see cref="World.ActiveWorldType"/> + <see cref="World.JobDataManager"/>,
    /// disposed after) so it can drive the sea-level override and the optional pool directly — it does not
    /// mutate the live world's generator. Requires a fully-started <c>World</c> of type
    /// <see cref="WorldTypeID.Standard"/> in the scene.
    /// </para>
    /// </summary>
    public class ChunkGenerationBenchmark : MonoBehaviour
    {
        #region Serialized Fields

        [Header("Benchmark Configuration")]
        [Tooltip("Whether the benchmark is enabled and allowed to run.")]
        [SerializeField]
        private bool _benchmarkEnabled = true;

        [Tooltip("Chunks generated (and held in-flight) per run. Capped at the pool's MaxRetained so the " +
                 "pooled leg stays fully warm — a batch larger than the cap would force the pooled leg to " +
                 "partially fall back to fresh allocation, muddying the A/B.")]
        [SerializeField]
        [Range(1, ActiveVoxelListPool.MaxRetained)]
        private int _chunksToGenerate = ActiveVoxelListPool.MaxRetained;

        [Tooltip("Measured runs per scenario×leg. Each run yields one sample; the report keeps the min " +
                 "(clean CPU floor). A discarded warm-up run per leg precedes these (absorbs Burst JIT and " +
                 "warms the pool's capacity).")]
        [SerializeField]
        [Min(1)]
        private int _benchmarkRuns = 10;

        [Tooltip("Sea level for the Ocean scenario — raised well above terrain so generated chunks flood and " +
                 "emit thousands of active water source voxels (exercises the active-voxel list's realloc " +
                 "growth). The Land scenario uses the world type's own sea level.")]
        [SerializeField]
        private int _oceanSeaLevel = 110;

        [Header("Execution Settings")]
        [Tooltip("If checked, the benchmark runs automatically when the scene starts.")]
        [SerializeField]
        private bool _runOnStart = true;

        [Tooltip("If checked, the report (rich-text stripped) is written to a timestamped file under " +
                 "Application.persistentDataPath/Benchmarks/. The path is logged after the run.")]
        [SerializeField]
        private bool _writeReportToFile = true;

        [Header("Keybinding")]
        [Tooltip("Press this key to manually trigger the benchmark.")]
        [SerializeField]
        private Key _triggerKey = Key.C;

        #endregion

        #region Private Fields

        private World _world;
        private bool _isBenchmarking;

        #endregion

        #region Unity Lifecycle

        private void Start()
        {
            if (!_benchmarkEnabled)
            {
                enabled = false;
                return;
            }

            _world = World.Instance;
            if (_world == null)
            {
                Debug.LogError("ChunkGenerationBenchmark requires a World instance in the scene!", this);
                enabled = false;
                return;
            }

            if (_runOnStart)
                TriggerBenchmark();
        }

        private void Update()
        {
            if (InputManager.Instance != null && InputManager.Instance.DebugKeyPressed(_triggerKey))
                TriggerBenchmark();
        }

        #endregion

        #region Orchestration

        /// <summary>Starts the full benchmark if one is not already running.</summary>
        private void TriggerBenchmark()
        {
            if (_isBenchmarking)
            {
                Debug.LogWarning("Chunk generation benchmark is already in progress.");
                return;
            }

            if (_world.ActiveWorldType == null || _world.JobDataManager == null)
            {
                Debug.LogError("ChunkGenerationBenchmark: the world is not fully started " +
                               "(ActiveWorldType / JobDataManager not ready).", this);
                return;
            }

            if (_world.ActiveWorldType.typeID != WorldTypeID.Standard)
            {
                Debug.LogError($"ChunkGenerationBenchmark requires a Standard world type — the active type is " +
                               $"{_world.ActiveWorldType.typeID}. Aborting.", this);
                return;
            }

            // Defensive clamp: the [Range] caps the inspector, but a value serialized before the cap existed
            // (e.g. a scene carried over from the old batch-size benchmark) can still reach runtime above it.
            // A batch larger than the pool cap makes the pooled leg partially fall back to fresh allocation.
            if (_chunksToGenerate > ActiveVoxelListPool.MaxRetained)
            {
                Debug.LogWarning($"ChunkGenerationBenchmark: _chunksToGenerate ({_chunksToGenerate}) exceeds the " +
                                 $"pool cap ({ActiveVoxelListPool.MaxRetained}); clamping so the pooled leg stays " +
                                 $"warm. Re-save the component in the inspector to persist the clamp.", this);
                _chunksToGenerate = ActiveVoxelListPool.MaxRetained;
            }

            StartCoroutine(RunAllScenarios());
        }

        /// <summary>Runs every scenario × leg in sequence, then generates the report.</summary>
        private IEnumerator RunAllScenarios()
        {
            _isBenchmarking = true;
            Debug.Log("--- Starting Chunk Generation Benchmark ---");

            Stopwatch totalStopwatch = Stopwatch.StartNew();

            // The benchmark owns its generator + pool. Both are disposed in the finally so a generation
            // exception cannot leak the generator's Persistent arrays or the pool's retained lists.
            StandardChunkGenerator generator = CreateGenerator();
            ActiveVoxelListPool pool = new ActiveVoxelListPool();
            List<GenResult> results = new List<GenResult>();

            try
            {
                foreach (GenScenario scenario in Scenarios())
                {
                    foreach (GenLeg leg in s_legs)
                    {
                        GenResult result = default;
                        yield return RunScenarioLeg(generator, scenario, leg, pool, r => result = r);
                        results.Add(result);
                    }

                    yield return null; // let a frame breathe between scenarios
                }
            }
            finally
            {
                generator.Dispose();
                pool.Dispose();
            }

            totalStopwatch.Stop();
            Debug.Log("--- All Chunk Generation Runs Complete. Generating Report... ---");
            GenerateReport(results, totalStopwatch.Elapsed);
            _isBenchmarking = false;
        }

        /// <summary>
        /// Runs one scenario×leg: sets the scenario sea level, runs a discarded warm-up, then
        /// <see cref="_benchmarkRuns"/> measured runs, keeping the min of each metric (the clean CPU floor).
        /// </summary>
        /// <param name="generator">The benchmark-owned generator (sea level is set here per scenario).</param>
        /// <param name="scenario">The terrain scenario (sea-level override; null = world sea level).</param>
        /// <param name="leg">The allocation leg (fresh vs pooled).</param>
        /// <param name="pool">The shared pool; passed to the generator only on the pooled leg.</param>
        /// <param name="onComplete">Callback receiving the aggregated result.</param>
        private IEnumerator RunScenarioLeg(StandardChunkGenerator generator, GenScenario scenario, GenLeg leg,
            ActiveVoxelListPool pool, Action<GenResult> onComplete)
        {
            int seaLevel = scenario.SeaLevel ?? _world.ActiveWorldType.seaLevel;
            generator.SeaLevel = seaLevel;
            ActiveVoxelListPool legPool = leg.Pooled ? pool : null;
            Debug.Log($"--- Running: {scenario.Name} [{leg.Label}] " +
                      $"(sea={seaLevel}, {_chunksToGenerate} chunks × {_benchmarkRuns} runs) ---");

            // Discarded warm-up run: absorbs Burst JIT and (pooled leg) grows the pool's lists to this
            // scenario's active-voxel capacity, so the measured pooled runs hit the warm, realloc-free path.
            RunOnce(generator, legPool, out _, out _, out _, out _);
            yield return null;

            double minSchedUs = double.MaxValue, minFreeUs = double.MaxValue, minTotalMs = double.MaxValue;
            double avgActive = 0;

            for (int run = 0; run < _benchmarkRuns; run++)
            {
                RunOnce(generator, legPool, out double schedMs, out double completeMs, out double freeMs,
                    out long totalActive);

                minSchedUs = Math.Min(minSchedUs, schedMs * 1000.0 / _chunksToGenerate);
                minFreeUs = Math.Min(minFreeUs, freeMs * 1000.0 / _chunksToGenerate);
                minTotalMs = Math.Min(minTotalMs, (schedMs + completeMs + freeMs) / _chunksToGenerate);
                avgActive = totalActive / (double)_chunksToGenerate; // deterministic across runs — last wins

                yield return null; // keep the editor/player responsive between runs
            }

            onComplete(new GenResult(scenario.Name, leg.Label, _chunksToGenerate, avgActive,
                minSchedUs, minFreeUs, minTotalMs));
        }

        /// <summary>
        /// Schedules, completes, and releases one batch of <see cref="_chunksToGenerate"/> generations,
        /// returning the three wall-clock splits and the total active-voxel count emitted.
        /// </summary>
        /// <param name="generator">The generator to schedule on.</param>
        /// <param name="legPool">The pool for the pooled leg, or <c>null</c> for the fresh leg.</param>
        /// <param name="scheduleMs">Main-thread time scheduling the batch (where the per-chunk active-voxel alloc lives).</param>
        /// <param name="completeMs">Worker-thread completion wait (where the scan's realloc growth surfaces).</param>
        /// <param name="releaseMs">Main-thread time releasing the batch (pool return / dispose — where the free lives).</param>
        /// <param name="totalActive">Total active voxels emitted across the batch (for the avg-active column).</param>
        private void RunOnce(StandardChunkGenerator generator, ActiveVoxelListPool legPool,
            out double scheduleMs, out double completeMs, out double releaseMs, out long totalActive)
        {
            // Assigned up front so the out-params are definitely assigned even if a scheduled generation
            // throws (their values are unused on the exception path, which propagates out of RunOnce).
            scheduleMs = completeMs = releaseMs = 0;
            totalActive = 0;

            NativeArray<JobHandle> handles = new NativeArray<JobHandle>(_chunksToGenerate, Allocator.Persistent);
            GenerationJobData[] jobs = new GenerationJobData[_chunksToGenerate];
            int sideLength = Mathf.CeilToInt(Mathf.Sqrt(_chunksToGenerate));
            int scheduled = 0;
            bool released = false;

            try
            {
                Stopwatch sw = Stopwatch.StartNew();

                // --- Schedule (main thread): per-chunk Persistent allocs incl. the active-voxel list (fresh)
                //     or a pool Rent (pooled), plus the job scheduling shared by both legs.
                for (int i = 0; i < _chunksToGenerate; i++)
                {
                    ChunkCoord coord = new ChunkCoord(i % sideLength, i / sideLength);
                    GenerationJobData jobData = generator.ScheduleGeneration(coord, legPool);
                    jobs[i] = jobData;
                    handles[i] = jobData.Handle;
                    scheduled = i + 1;
                }

                sw.Stop();
                scheduleMs = sw.Elapsed.TotalMilliseconds;

                // --- Complete (worker threads): generation runs; the active-voxel scan's realloc growth
                //     (Ocean) happens here, overlapped with the heavier noise/cave/worm work.
                sw.Restart();
                JobHandle.CombineDependencies(handles).Complete();
                sw.Stop();
                completeMs = sw.Elapsed.TotalMilliseconds;

                // --- Count active voxels (outside timing) ---
                for (int i = 0; i < _chunksToGenerate; i++)
                    totalActive += jobs[i].ActiveVoxels.IsCreated ? jobs[i].ActiveVoxels.Length : 0;

                // --- Release (main thread): the per-chunk free — pool Return (pooled) or NativeList Dispose
                //     (fresh). Mirrors WorldJobManager.ReleaseGenerationJobData.
                sw.Restart();
                for (int i = 0; i < _chunksToGenerate; i++)
                    ReleaseJob(jobs[i], legPool);

                released = true;
                sw.Stop();
                releaseMs = sw.Elapsed.TotalMilliseconds;
            }
            finally
            {
                // Exception-safe cleanup: if a scheduled generation threw, the normal release never ran.
                // Complete the in-flight jobs FIRST — otherwise the caller's generator.Dispose() would free
                // the generator's [ReadOnly] arrays while these jobs are still reading them (use-after-free)
                // — then release the scheduled jobs. The happy path already released them (released == true).
                if (!released)
                {
                    JobHandle.CombineDependencies(handles).Complete();
                    for (int i = 0; i < scheduled; i++)
                        ReleaseJob(jobs[i], legPool);
                }

                handles.Dispose();
            }
        }

        /// <summary>
        /// Releases one completed generation job, mirroring <c>WorldJobManager.ReleaseGenerationJobData</c>:
        /// a pooled active-voxel list is returned to the pool (and skipped by <c>Dispose</c> via
        /// <see cref="GenerationJobData.ActiveVoxelsFromPool"/>); everything else is disposed.
        /// </summary>
        private static void ReleaseJob(in GenerationJobData jobData, ActiveVoxelListPool legPool)
        {
            if (legPool != null && jobData.ActiveVoxelsFromPool)
                legPool.Return(jobData.ActiveVoxels);

            jobData.Dispose();
        }

        /// <summary>
        /// Creates and initializes the benchmark-owned generator from the live world's type + global job data
        /// (same seed as the world, so generated terrain matches it). Caller owns disposal.
        /// </summary>
        private StandardChunkGenerator CreateGenerator()
        {
            StandardChunkGenerator generator = new StandardChunkGenerator();
            generator.Initialize(VoxelData.Seed, _world.ActiveWorldType, _world.JobDataManager);
            generator.FeatureFlags = GenerationFeatureFlags.Default;
            return generator;
        }

        /// <summary>The two terrain scenarios: Land (world sea level) then Ocean (raised, water-heavy).</summary>
        private IEnumerable<GenScenario> Scenarios()
        {
            yield return new GenScenario("Land", null);
            yield return new GenScenario("Ocean", _oceanSeaLevel);
        }

        /// <summary>The two allocation legs, ordered so each scenario's fresh/pooled rows sit adjacent.</summary>
        private static readonly GenLeg[] s_legs = { new GenLeg("fresh", false), new GenLeg("pooled", true) };

        #endregion

        #region Result / Scenario / Leg Types

        /// <summary>One terrain scenario: a display name and an optional sea-level override (null = world sea level).</summary>
        private readonly struct GenScenario
        {
            public readonly string Name;
            public readonly int? SeaLevel;

            public GenScenario(string name, int? seaLevel)
            {
                Name = name;
                SeaLevel = seaLevel;
            }
        }

        /// <summary>One allocation leg: a label and whether the generator rents from the pool.</summary>
        private readonly struct GenLeg
        {
            public readonly string Label;
            public readonly bool Pooled;

            public GenLeg(string label, bool pooled)
            {
                Label = label;
                Pooled = pooled;
            }
        }

        /// <summary>The aggregated measurement for one scenario × leg (each metric is the min over runs).</summary>
        private readonly struct GenResult
        {
            public readonly string Scenario;
            public readonly string Leg;
            public readonly int Chunks;
            public readonly double AvgActive;
            public readonly double SchedUsPerChunk;
            public readonly double FreeUsPerChunk;
            public readonly double TotalMsPerChunk;

            public GenResult(string scenario, string leg, int chunks, double avgActive,
                double schedUsPerChunk, double freeUsPerChunk, double totalMsPerChunk)
            {
                Scenario = scenario;
                Leg = leg;
                Chunks = chunks;
                AvgActive = avgActive;
                SchedUsPerChunk = schedUsPerChunk;
                FreeUsPerChunk = freeUsPerChunk;
                TotalMsPerChunk = totalMsPerChunk;
            }
        }

        #endregion

        #region Reporting

        /// <summary>Logs (and optionally writes) the system info + results table.</summary>
        /// <param name="results">Per scenario×leg aggregated results.</param>
        /// <param name="totalElapsed">Wall-clock time of the whole benchmark.</param>
        private void GenerateReport(List<GenResult> results, TimeSpan totalElapsed)
        {
            StringBuilder report = new StringBuilder();
            report.AppendLine("<color=cyan><b>--- CHUNK GENERATION BENCHMARK REPORT ---</b></color>");
            report.AppendLine($"Measured over {_benchmarkRuns} run(s) per scenario×leg (1 warm-up run discarded), " +
                              $"{_chunksToGenerate} chunks/run.");
            report.AppendLine("Path: production StandardChunkGenerator.ScheduleGeneration → Complete → release " +
                              "(own generator, own pool; no world mutation). Isolated — no mesh/light/render.");
            report.AppendLine("Scenario: 'Land' = world sea level (sparse actives); 'Ocean' = raised sea level " +
                              "(generated water-heavy → active-voxel-list realloc growth). Leg: 'fresh' = new " +
                              "NativeList per chunk; 'pooled' = warmed ActiveVoxelListPool (TG-6).");
            report.AppendLine("Columns are the MIN over runs (clean CPU floor). sched µs/ch = main-thread " +
                              "schedule (where the per-chunk active-voxel alloc lives); free µs/ch = main-thread " +
                              "release (pool Return / NativeList Dispose); total ms/ch = schedule→complete→release " +
                              "(the standing whole-gen regression number; Ocean's worker-side realloc saving " +
                              "surfaces here). TG-6's win = the fresh→pooled delta on sched+free (native, sub-µs).");
            report.AppendLine($"Total wall-clock runtime: {BenchmarkEnvironment.FormatDuration(totalElapsed)}");
            report.AppendLine();
            report.Append(BenchmarkEnvironment.DescribeSystem());
            report.AppendLine("=== Results ===");
            report.AppendLine();

            ReportTable table = new ReportTable(
                "Scenario", "Leg", "Chunks", "AvgActive", "sched µs/ch", "free µs/ch", "total ms/ch");

            foreach (GenResult r in results)
            {
                table.AddRow(
                    r.Scenario,
                    r.Leg,
                    r.Chunks.ToString(),
                    r.AvgActive.ToString("F0"),
                    r.SchedUsPerChunk.ToString("F2"),
                    r.FreeUsPerChunk.ToString("F2"),
                    r.TotalMsPerChunk.ToString("F3"));
            }

            table.AppendTo(report);
            report.AppendLine();

            string fullReport = report.ToString();
            Debug.Log(fullReport);

            if (_writeReportToFile)
                BenchmarkEnvironment.WriteReportToDisk(fullReport, "ChunkGenerationBenchmark");
        }

        #endregion
    }
}
