using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using Data;
using Jobs;
using Jobs.Data;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace DebugVisualizations
{
    public class ChunkGenerationBenchmark : MonoBehaviour
    {
        private enum BenchmarkMode
        {
            Serial, // Simulates a sequential job by using ScheduleByRef.
            Parallel // Uses ScheduleParallelByRef for maximum parallelism.
        }

        [Header("Benchmark Settings")]
        [Tooltip("Whether the benchmark is enabled and allowed to run.")]
        [SerializeField]
        private bool _benchMarkEnabled = true;

        [Tooltip("The number of chunks to generate for the test. (eg: 256 for a 16x16 grid)")]
        [SerializeField]
        private int _chunksToGenerate = 256;

        [Tooltip("The method to use for generation.")]
        [SerializeField]
        private BenchmarkMode _mode = BenchmarkMode.Parallel;

        // NEW: Batch size field for parallel mode.
        [Tooltip("The batch size for parallel execution. A smaller size offers better work distribution for heavy tasks; a larger size reduces overhead for lighter tasks.")]
        [SerializeField]
        [Range(1, 128)]
        public int _parallelBatchSize = 32;

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


        private World _world;
        private bool _isBenchmarking = false;

        private void Start()
        {
            if (!_benchMarkEnabled)
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
            {
                StartCoroutine(RunBenchmark());
            }
        }

        private void Update()
        {
            if (Input.GetKeyDown(_triggerKey))
            {
                StartCoroutine(RunBenchmark());
            }
        }

        public IEnumerator RunBenchmark()
        {
            if (_isBenchmarking)
            {
                Debug.LogWarning("Benchmark is already in progress.");
                yield break;
            }

            _isBenchmarking = true;

            // UPDATED: Include batch size in the log message for clarity.
            string modeDetails = _mode == BenchmarkMode.Parallel ? $"Parallel (Batch Size: {_parallelBatchSize})" : "Serial";
            Debug.Log($"--- Starting Chunk Generation Benchmark ---");
            Debug.Log($"Mode: {modeDetails} | Chunks: {_chunksToGenerate} | Blocking Wait: {_useBlockingWait}");

            if (_useBlockingWait)
            {
                Debug.Log("The application will freeze during the benchmark. This is expected for an accurate measurement.");
            }

            // --- Setup ---
            var jobHandles = new NativeArray<JobHandle>(_chunksToGenerate, Allocator.Persistent);
            var jobDataToDispose = new List<GenerationJobData>(_chunksToGenerate);
            var stopwatch = new Stopwatch();

            try
            {
                // --- Scheduling Phase ---
                stopwatch.Start();

                int sideLength = Mathf.CeilToInt(Mathf.Sqrt(_chunksToGenerate));
                for (int i = 0; i < _chunksToGenerate; i++)
                {
                    int x = i % sideLength;
                    int z = i / sideLength;
                    var coord = new ChunkCoord(x, z);

                    GenerationJobData jobData = ScheduleBenchmarkGeneration(coord, _mode);
                    jobHandles[i] = jobData.Handle;
                    jobDataToDispose.Add(jobData);
                }

                Debug.Log($"Scheduled all {_chunksToGenerate} jobs. Waiting for completion...");

                // --- Completion Phase ---
                var combinedHandle = JobHandle.CombineDependencies(jobHandles);

                if (_useBlockingWait)
                {
                    // BLOCKING: Freeze and wait for all jobs to complete. Most accurate.
                    combinedHandle.Complete();
                }
                else
                {
                    // NON-BLOCKING: Wait across multiple frames. Keeps editor responsive.
                    while (!combinedHandle.IsCompleted)
                    {
                        yield return null;
                    }

                    combinedHandle.Complete();
                }

                stopwatch.Stop();

                // --- Results ---
                long totalMilliseconds = stopwatch.ElapsedMilliseconds;
                float avgTime = (float)totalMilliseconds / _chunksToGenerate;

                Debug.Log($"<color=lime>--- Benchmark Complete ---</color>");
                Debug.Log($"<b>Total Time: {totalMilliseconds} ms</b>");
                Debug.Log($"Average Time per Chunk: {avgTime:F2} ms");
            }
            finally
            {
                // --- Cleanup ---
                Debug.Log("Cleaning up benchmark data...");
                if (jobHandles.IsCreated)
                {
                    jobHandles.Dispose();
                }

                foreach (var data in jobDataToDispose)
                {
                    data.Dispose();
                }

                _isBenchmarking = false;
            }
        }

        /// <summary>
        /// Schedules a single ChunkGenerationJob and returns its handle and data,
        /// using the modern IJobFor scheduling API.
        /// </summary>
        private GenerationJobData ScheduleBenchmarkGeneration(ChunkCoord coord, BenchmarkMode benchmarkMode)
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
                // Schedule job to run on multiple worker threads. Batch size can be dynamically changed.
                handle = job.ScheduleParallelByRef(totalColumns, _parallelBatchSize, default);
            }
            else // Serial
            {
                // Schedule job to run all iterations sequentially on a single worker thread.
                // This perfectly simulates the behavior of a single, non-parallel IJob.
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
    }
}