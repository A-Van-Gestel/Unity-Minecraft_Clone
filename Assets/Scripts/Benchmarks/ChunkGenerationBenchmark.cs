using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Data;
using Jobs;
using Jobs.Data;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Benchmarks
{
    /// <summary>
    /// A benchmark utility for measuring the performance of the ChunkGenerationJob.
    /// It can run a single test or a full comparison of Serial vs. Parallel modes with various batch sizes,
    /// generating a detailed report to identify the most optimal settings.
    /// </summary>
    public class ChunkGenerationBenchmark : MonoBehaviour
    {
        #region Enums and Constants

        /// <summary>
        /// Defines the generation method to test.
        /// </summary>
        private enum BenchmarkMode
        {
            Serial, // Simulates a sequential job by using ScheduleByRef.
            Parallel // Uses ScheduleParallelByRef for maximum parallelism.
        }

        /// <summary>
        /// A predefined list of batch sizes to test during a full comparison run.
        /// These are powers of two, which are common and efficient batch sizes.
        /// </summary>
        private static readonly int[] SBatchSizesToTest = { 1, 2, 4, 8, 16, 32, 64, 128 };

        #endregion

        #region Serialized Fields

        [Header("Benchmark Configuration")]
        [Tooltip("Whether the benchmark is enabled and allowed to run.")]
        [SerializeField]
        private bool _benchMarkEnabled = true;

        [Tooltip("If checked, runs a full comparison of Serial vs. all Parallel batch sizes and generates a report.")]
        [SerializeField]
        private bool _runFullComparison = true;

        [Tooltip("The number of times to run each benchmark scenario to get a stable average.")]
        [SerializeField]
        [Min(1)]
        private int _benchmarkRuns = 5;

        [Tooltip("The number of chunks to generate for each individual run. (e.g., 256 for a 16x16 grid)")]
        [SerializeField]
        private int _chunksToGenerate = 256;


        [Header("Single Run Settings (Used if Full Comparison is false)")]
        [Tooltip("The generation method to use for a single test run.")]
        [SerializeField]
        private BenchmarkMode _mode = BenchmarkMode.Parallel;

        [Tooltip("The batch size for a single parallel execution. A smaller size offers better work distribution for heavy tasks; a larger size reduces overhead for lighter tasks.")]
        [SerializeField]
        [Range(1, 128)]
        private int _parallelBatchSize = 32;


        [Header("Execution Settings")]
        [Tooltip("If true, the benchmark will freeze the editor for the most accurate time. If false, it will run over multiple frames, keeping the editor responsive but providing a less accurate time.")]
        [SerializeField]
        private bool _useBlockingWait = true;

        [Tooltip("If checked, the benchmark will run automatically when the scene starts.")]
        [SerializeField]
        private bool _runOnStart = true;


        [Header("Keybinding")]
        [Tooltip("Press this key to manually trigger the benchmark.")]
        [SerializeField]
        private KeyCode _triggerKey = KeyCode.B;

        #endregion

        #region Private Fields

        private World _world;
        private bool _isBenchmarking = false;

        #endregion

        #region Unity Lifecycle Methods

        private void Start()
        {
            // Fully disable the benchmark script if benchmark mode is disabled
            if (!_benchMarkEnabled)
            {
                enabled = false;
                return;
            }

            // Configure benchmark
            _world = World.Instance;
            if (_world == null)
            {
                Debug.LogError("ChunkGenerationBenchmark requires a World instance in the scene!", this);
                enabled = false;
                return;
            }

            if (_runOnStart)
            {
                TriggerBenchmark();
            }
        }

        private void Update()
        {
            if (Input.GetKeyDown(_triggerKey))
            {
                TriggerBenchmark();
            }
        }

        #endregion

        #region Benchmark Orchestration

        /// <summary>
        /// The main entry point for starting a benchmark. It decides whether to run a full comparison or a single test.
        /// </summary>
        private void TriggerBenchmark()
        {
            if (_isBenchmarking)
            {
                Debug.LogWarning("Benchmark is already in progress.");
                return;
            }

            if (_runFullComparison)
            {
                StartCoroutine(RunFullComparisonBenchmark());
            }
            else
            {
                StartCoroutine(RunSingleBenchmarkFromInspector());
            }
        }

        /// <summary>
        /// A coroutine that runs a single benchmark configuration based on the Inspector settings and logs the result.
        /// </summary>
        private IEnumerator RunSingleBenchmarkFromInspector()
        {
            long averageTime = 0;
            // Execute the benchmark and capture the average time via a callback.
            yield return StartCoroutine(ExecuteBenchmarkRun(_mode, _parallelBatchSize, result => averageTime = result));

            string modeDetails = _mode == BenchmarkMode.Parallel ? $"Parallel (Batch Size: {_parallelBatchSize})" : "Serial";
            Debug.Log($"<color=lime>--- Single Benchmark Complete ---</color>\n" +
                      $"Configuration: {modeDetails}\n" +
                      $"<b>Average Time over {_benchmarkRuns} runs: {averageTime} ms</b>");
        }

        /// <summary>
        /// The master coroutine that orchestrates the full comparison benchmark.
        /// It tests Serial mode once and then iterates through all predefined Parallel batch sizes.
        /// </summary>
        private IEnumerator RunFullComparisonBenchmark()
        {
            _isBenchmarking = true;
            Debug.Log("--- Starting Full Comparison Benchmark ---");

            var results = new Dictionary<string, long>();

            // --- Run Serial Benchmark ---
            yield return StartCoroutine(ExecuteBenchmarkRun(BenchmarkMode.Serial, 0, result => results["Serial"] = result));

            // --- Run Parallel Benchmarks ---
            foreach (int batchSize in SBatchSizesToTest)
            {
                yield return StartCoroutine(ExecuteBenchmarkRun(BenchmarkMode.Parallel, batchSize, result => results[$"Parallel_{batchSize}"] = result));
            }

            Debug.Log("--- All Benchmark Runs Complete. Generating Report... ---");
            GenerateReport(results);
            _isBenchmarking = false;
        }

        /// <summary>
        /// The core logic for executing a single benchmark configuration multiple times and returning the average result.
        /// This method handles all memory allocation, job scheduling, completion, and cleanup for a test series.
        /// </summary>
        /// <param name="mode">The generation mode to test (Serial or Parallel).</param>
        /// <param name="batchSize">The batch size to use if in Parallel mode (ignored for Serial).</param>
        /// <param name="onComplete">A callback that returns the calculated average time in milliseconds.</param>
        private IEnumerator ExecuteBenchmarkRun(BenchmarkMode mode, int batchSize, Action<long> onComplete)
        {
            _isBenchmarking = true;
            string modeDetails = mode == BenchmarkMode.Parallel ? $"Parallel (Batch Size: {batchSize})" : "Serial";
            Debug.Log($"--- Running Benchmark: {modeDetails} ({_benchmarkRuns} runs) ---");

            long totalMillisecondsForAllRuns = 0;

            for (int run = 0; run < _benchmarkRuns; run++)
            {
                var jobHandles = new NativeArray<JobHandle>(_chunksToGenerate, Allocator.Persistent);
                var jobDataToDispose = new List<GenerationJobData>(_chunksToGenerate);
                var stopwatch = new Stopwatch();

                try
                {
                    stopwatch.Start();

                    // --- Scheduling Phase ---
                    int sideLength = Mathf.CeilToInt(Mathf.Sqrt(_chunksToGenerate));
                    for (int i = 0; i < _chunksToGenerate; i++)
                    {
                        var coord = new ChunkCoord(i % sideLength, i / sideLength);
                        GenerationJobData jobData = ScheduleBenchmarkGeneration(coord, mode, batchSize);
                        jobHandles[i] = jobData.Handle;
                        jobDataToDispose.Add(jobData);
                    }

                    // --- Completion Phase ---
                    var combinedHandle = JobHandle.CombineDependencies(jobHandles);

                    if (_useBlockingWait)
                    {
                        combinedHandle.Complete();
                    }
                    else
                    {
                        while (!combinedHandle.IsCompleted) yield return null;
                        combinedHandle.Complete();
                    }

                    stopwatch.Stop();
                    totalMillisecondsForAllRuns += stopwatch.ElapsedMilliseconds;
                }
                finally
                {
                    // --- Cleanup for this single run ---
                    if (jobHandles.IsCreated) jobHandles.Dispose();
                    foreach (var data in jobDataToDispose) data.Dispose();
                }

                if (!_useBlockingWait) yield return null;
            }

            onComplete(totalMillisecondsForAllRuns / Mathf.Max(1, _benchmarkRuns)); // Return average time.
            _isBenchmarking = false;
        }

        #endregion

        #region Job and Report Handling

        /// <summary>
        /// Schedules a single ChunkGenerationJob and returns its handle and data.
        /// </summary>
        /// <param name="coord">The coordinate of the chunk to generate.</param>
        /// <param name="benchmarkMode">The scheduling mode to use (Serial or Parallel).</param>
        /// <param name="parallelBatchSize">The batch size to use for a Parallel job.</param>
        /// <returns>A GenerationJobData struct containing the job handle and output data containers.</returns>
        private GenerationJobData ScheduleBenchmarkGeneration(ChunkCoord coord, BenchmarkMode benchmarkMode, int parallelBatchSize)
        {
            var modificationsQueue = new NativeQueue<VoxelMod>(Allocator.Persistent);
            var job = new ChunkGenerationJob
            {
                Seed = VoxelData.Seed,
                ChunkPosition = new Vector2Int(coord.X * VoxelData.ChunkWidth, coord.Z * VoxelData.ChunkWidth),
                BlockTypes = _world.JobDataManager.BlockTypesJobData,
                Biomes = _world.JobDataManager.BiomesJobData,
                AllLodes = _world.JobDataManager.AllLodesJobData,
                OutputMap = new NativeArray<uint>(VoxelData.ChunkWidth * VoxelData.ChunkHeight * VoxelData.ChunkWidth, Allocator.Persistent),
                OutputHeightMap = new NativeArray<byte>(VoxelData.ChunkWidth * VoxelData.ChunkWidth, Allocator.Persistent),
                Modifications = modificationsQueue.AsParallelWriter()
            };

            JobHandle handle;
            const int totalColumns = VoxelData.ChunkWidth * VoxelData.ChunkWidth;

            if (benchmarkMode == BenchmarkMode.Parallel)
            {
                // Schedule job to run on multiple worker threads.
                handle = job.ScheduleParallelByRef(totalColumns, parallelBatchSize, default);
            }
            else // Serial
            {
                // Schedule job to run all iterations sequentially on a single worker thread.
                handle = job.ScheduleByRef(totalColumns, default);
            }

            // Return the data needed for completion and cleanup.
            return new GenerationJobData
            {
                Handle = handle,
                Map = job.OutputMap,
                HeightMap = job.OutputHeightMap,
                Mods = modificationsQueue
            };
        }

        /// <summary>
        /// Generates the final, formatted report from the collected results and logs it to the console.
        /// </summary>
        /// <param name="results">A dictionary containing the average times for each test configuration.</param>
        private void GenerateReport(Dictionary<string, long> results)
        {
            var report = new StringBuilder();
            report.AppendLine($"<color=lime><b>--- CHUNK GENERATION BENCHMARK REPORT ---</b></color>");
            report.AppendLine($"Test configuration: {_chunksToGenerate} chunks per run, averaged over {_benchmarkRuns} runs.\n");

            // --- Baseline: Serial ---
            long serialTime = results["Serial"];
            report.AppendLine("<b>--- Baseline ---</b>");
            report.AppendLine($"  - Serial Mode: {serialTime} ms\n");

            // --- Parallel Results ---
            report.AppendLine("<b>--- Parallel Mode Results ---</b>");
            long bestParallelTime = long.MaxValue;
            int bestBatchSize = 0;

            foreach (int batchSize in SBatchSizesToTest)
            {
                string key = $"Parallel_{batchSize}";
                if (results.TryGetValue(key, out long time))
                {
                    report.AppendLine($"  - Batch Size {batchSize,-3}: {time} ms");
                    if (time < bestParallelTime)
                    {
                        bestParallelTime = time;
                        bestBatchSize = batchSize;
                    }
                }
            }

            // --- Final Summary and Recommendation ---
            report.AppendLine("\n<b>--- Summary ---</b>");
            report.AppendLine($"  - Slowest (Serial): {serialTime} ms");
            report.AppendLine($"  - Fastest (Parallel): {bestParallelTime} ms with a batch size of <b>{bestBatchSize}</b>");

            long difference = serialTime - bestParallelTime;
            float percentage = (difference / (float)serialTime) * 100f;

            report.AppendLine($"  - <color=cyan>Conclusion: Parallel mode with a batch size of {bestBatchSize} was <b>{percentage:F1}% faster</b> than Serial mode.</color>");
            report.AppendLine($"  - <color=yellow>Recommendation: For this system, a batch size of <b>{bestBatchSize}</b> is optimal for chunk generation.</color>");

            Debug.Log(report.ToString());
        }

        #endregion
    }
}