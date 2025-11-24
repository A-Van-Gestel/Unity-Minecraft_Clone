using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Data;
using Jobs;
using Jobs.BurstData;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Benchmarks
{
    /// <summary>
    /// A dedicated benchmark utility for measuring the performance of the MeshGenerationJob.
    /// It can run a single test configuration or a full comparison of all available modes and data types,
    /// generating a detailed report of the findings.
    /// </summary>
    public class MeshGenerationBenchmark : MonoBehaviour
    {
        #region Enums

        /// <summary>
        /// Defines the scheduling method to test, specifically whether to include diagonal neighbor data.
        /// </summary>
        private enum BenchmarkMode
        {
            WithDiagonals, // Passes all 8 neighbors to the job.
            CardinalsOnly // Passes only the 4 cardinal neighbors.
        }

        /// <summary>
        /// Defines the type of voxel data pattern to use for mesh generation.
        /// </summary>
        private enum ChunkDataType
        {
            Solid, // Easiest case: very few faces to generate.
            Checkerboard // Worst case: maximum number of faces to generate.
        }

        #endregion

        #region Serialized Fields

        [Header("Benchmark Configuration")]
        [Tooltip("Whether the benchmark is enabled and allowed to run.")]
        [SerializeField]
        private bool _benchMarkEnabled = true;

        [Tooltip("If checked, runs all combinations of Mode and Data Type and generates a final report.")]
        [SerializeField]
        private bool _runFullComparison = true;

        [Tooltip("The number of times to run each benchmark scenario to get a stable average.")]
        [SerializeField]
        [Min(1)]
        private int _benchmarkRuns = 3;

        [Tooltip("The number of chunk meshes to generate for each individual run.")]
        [SerializeField]
        private int _chunksToMesh = 256;


        [Header("Single Run Settings (Used if Full Comparison is false)")]
        [Tooltip("The scheduling method to test for a single run.")]
        [SerializeField]
        private BenchmarkMode _mode = BenchmarkMode.WithDiagonals;

        [Tooltip("The type of voxel data to use for a single run.")]
        [SerializeField]
        private ChunkDataType _dataType = ChunkDataType.Checkerboard;


        [Header("Execution Settings")]
        [Tooltip("If true, the benchmark will freeze the editor for the most accurate time.")]
        [SerializeField]
        private bool _useBlockingWait = true;

        [Tooltip("If checked, the benchmark will run automatically when the scene starts.")]
        [SerializeField]
        private bool _runOnStart = true;


        [Header("Keybinding")]
        [Tooltip("Press this key to manually trigger the benchmark.")]
        [SerializeField]
        private KeyCode _triggerKey = KeyCode.M;

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
                Debug.LogError("MeshGenerationBenchmark requires a World instance in the scene!", this);
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
                // Run a single test using the Inspector settings.
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
            yield return StartCoroutine(ExecuteBenchmarkRun(_mode, _dataType, result => averageTime = result));

            Debug.Log($"<color=lime>--- Single Benchmark Complete ---</color>\n" +
                      $"Configuration: {_mode} | {_dataType}\n" +
                      $"<b>Average Time over {_benchmarkRuns} runs: {averageTime} ms</b>");
        }

        /// <summary>
        /// The master coroutine that orchestrates the full comparison benchmark, running all four possible test combinations.
        /// </summary>
        private IEnumerator RunFullComparisonBenchmark()
        {
            _isBenchmarking = true;
            Debug.Log("--- Starting Full Comparison Benchmark ---");

            var results = new Dictionary<string, long>();

            // --- Run all 4 combinations sequentially ---
            yield return StartCoroutine(ExecuteBenchmarkRun(BenchmarkMode.WithDiagonals, ChunkDataType.Solid, result => results["Solid_WithDiagonals"] = result));
            yield return StartCoroutine(ExecuteBenchmarkRun(BenchmarkMode.CardinalsOnly, ChunkDataType.Solid, result => results["Solid_CardinalsOnly"] = result));
            yield return StartCoroutine(ExecuteBenchmarkRun(BenchmarkMode.WithDiagonals, ChunkDataType.Checkerboard, result => results["Checkerboard_WithDiagonals"] = result));
            yield return StartCoroutine(ExecuteBenchmarkRun(BenchmarkMode.CardinalsOnly, ChunkDataType.Checkerboard, result => results["Checkerboard_CardinalsOnly"] = result));

            Debug.Log("--- All Benchmark Runs Complete. Generating Report... ---");
            GenerateReport(results);
            _isBenchmarking = false;
        }

        /// <summary>
        /// The core logic for executing a single benchmark configuration multiple times and returning the average result.
        /// This method handles all memory allocation, job scheduling, completion, and cleanup for a test series.
        /// </summary>
        /// <param name="mode">The neighbor data mode to test.</param>
        /// <param name="dataType">The voxel pattern to test.</param>
        /// <param name="onComplete">A callback that returns the calculated average time in milliseconds.</param>
        private IEnumerator ExecuteBenchmarkRun(BenchmarkMode mode, ChunkDataType dataType, Action<long> onComplete)
        {
            _isBenchmarking = true;
            Debug.Log($"--- Running Benchmark: {mode} | {dataType} ({_benchmarkRuns} runs) ---");

            long totalMillisecondsForAllRuns = 0;

            // Generate the source data ONCE for this set of runs to ensure consistency.
            BenchmarkVoxelData benchmarkData = GenerateBenchmarkData(dataType, Allocator.Persistent);

            for (int run = 0; run < _benchmarkRuns; run++)
            {
                var jobHandles = new NativeArray<JobHandle>(_chunksToMesh, Allocator.Persistent);
                var inputDataToDispose = new List<BenchmarkVoxelData>(_chunksToMesh);
                var outputDataToDispose = new List<MeshDataJobOutput>(_chunksToMesh);
                var stopwatch = new Stopwatch();

                try
                {
                    stopwatch.Start();

                    // --- Scheduling Phase ---
                    for (int i = 0; i < _chunksToMesh; i++)
                    {
                        // For each job, create its input data with Allocator.Persistent.
                        var jobInputData = new BenchmarkVoxelData(Allocator.Persistent);
                        jobInputData.CopyFrom(benchmarkData);
                        inputDataToDispose.Add(jobInputData); // Track it for cleanup.

                        // Schedule the job.
                        var jobInfo = ScheduleBenchmarkMeshing(jobInputData, mode);
                        jobHandles[i] = jobInfo.handle;
                        outputDataToDispose.Add(jobInfo.output); // Track output for cleanup.
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
                    // The 'finally' block ensures that all native collections are disposed
                    // even if an exception occurs during the benchmark.
                    if (jobHandles.IsCreated) jobHandles.Dispose();
                    foreach (var data in inputDataToDispose) data.Dispose();
                    foreach (var data in outputDataToDispose) data.Dispose();
                }

                // If running in non-blocking mode, yield a frame between runs to keep the editor responsive.
                if (!_useBlockingWait) yield return null;
            }

            benchmarkData.Dispose(); // Clean up the source data.
            onComplete(totalMillisecondsForAllRuns / Mathf.Max(1, _benchmarkRuns)); // Return average time.
            _isBenchmarking = false;
        }

        #endregion

        #region Job and Data Handling

        /// <summary>
        /// Schedules a single MeshGenerationJob and returns its handle and output data container.
        /// This method does not manage memory disposal; that is the caller's responsibility.
        /// </summary>
        /// <param name="data">The voxel data for the job, including all 9 neighbor maps.</param>
        /// <param name="mode">The mode determining whether to pass diagonal neighbor data.</param>
        /// <returns>A tuple containing the final JobHandle and the job's output data struct.</returns>
        private (JobHandle handle, MeshDataJobOutput output) ScheduleBenchmarkMeshing(BenchmarkVoxelData data, BenchmarkMode mode)
        {
            var meshOutput = new MeshDataJobOutput(Allocator.Persistent);

            // If we're in CardinalsOnly mode, create a temporary empty array to pass to the unused job fields.
            var emptyArray = mode == BenchmarkMode.CardinalsOnly
                ? new NativeArray<uint>(0, Allocator.TempJob)
                : default;

            var job = new MeshGenerationJob
            {
                Map = data.Center,
                BlockTypes = _world.JobDataManager.BlockTypesJobData,
                NeighborBack = data.Back,
                NeighborFront = data.Front,
                NeighborLeft = data.Left,
                NeighborRight = data.Right,
                NeighborFrontRight = mode == BenchmarkMode.WithDiagonals ? data.FrontRight : emptyArray,
                NeighborBackRight = mode == BenchmarkMode.WithDiagonals ? data.BackRight : emptyArray,
                NeighborBackLeft = mode == BenchmarkMode.WithDiagonals ? data.BackLeft : emptyArray,
                NeighborFrontLeft = mode == BenchmarkMode.WithDiagonals ? data.FrontLeft : emptyArray,
                CustomMeshes = _world.JobDataManager.CustomMeshesJobData,
                CustomFaces = _world.JobDataManager.CustomFacesJobData,
                CustomVerts = _world.JobDataManager.CustomVertsJobData,
                CustomTris = _world.JobDataManager.CustomTrisJobData,
                WaterVertexTemplates = _world.FluidVertexTemplates.WaterVertexTemplates,
                LavaVertexTemplates = _world.FluidVertexTemplates.LavaVertexTemplates,
                Output = meshOutput,
            };

            // Schedule the job. If we created an empty array, chain its disposal to the job's handle.
            JobHandle handle = job.Schedule();
            if (emptyArray.IsCreated)
            {
                handle = emptyArray.Dispose(handle);
            }

            return (handle, meshOutput);
        }

        /// <summary>
        /// Generates a set of 9 chunk maps filled with a specific voxel pattern for testing.
        /// </summary>
        /// <param name="type">The pattern to generate (Solid or Checkerboard).</param>
        /// <param name="allocator">The memory allocator to use for the NativeArrays.</param>
        /// <returns>A BenchmarkVoxelData struct containing the generated maps.</returns>
        private BenchmarkVoxelData GenerateBenchmarkData(ChunkDataType type, Allocator allocator)
        {
            var data = new BenchmarkVoxelData(allocator);
            for (int i = 0; i < data.Center.Length; i++)
            {
                byte idToPlace = GetVoxelIDForPattern(type, i);
                uint packed = BurstVoxelDataBitMapping.PackVoxelData(idToPlace, 15, 0, 1, 0);
                data.FillAll(i, packed);
            }

            return data;
        }

        /// <summary>
        /// Determines the block ID for a given index based on the desired data pattern.
        /// </summary>
        /// <param name="type">The data pattern type.</param>
        /// <param name="index">The flat array index of the voxel.</param>
        /// <returns>The ushort ID of the block to place.</returns>
        private byte GetVoxelIDForPattern(ChunkDataType type, int index)
        {
            switch (type)
            {
                case ChunkDataType.Solid:
                    return 1; // Stone
                case ChunkDataType.Checkerboard:
                    int x = index % VoxelData.ChunkWidth;
                    int y = (index / VoxelData.ChunkWidth) % VoxelData.ChunkHeight;
                    int z = index / (VoxelData.ChunkWidth * VoxelData.ChunkHeight);
                    return (x + y + z) % 2 == 0 ? (byte)1 : (byte)0; // Stone or Air
                default:
                    return 0; // Air
            }
        }

        #endregion

        #region Reporting

        /// <summary>
        /// Generates the final, formatted report from the collected results and logs it to the console.
        /// </summary>
        /// <param name="results">A dictionary containing the average times for each test configuration.</param>
        private void GenerateReport(Dictionary<string, long> results)
        {
            var report = new StringBuilder();
            report.AppendLine($"<color=lime><b>--- MESH GENERATION BENCHMARK REPORT ---</b></color>");
            report.AppendLine($"Test configuration: {_chunksToMesh} chunks per run, averaged over {_benchmarkRuns} runs.\n");

            // --- Solid Data Report ---
            long solidDiagonals = results["Solid_WithDiagonals"];
            long solidCardinals = results["Solid_CardinalsOnly"];
            report.AppendLine("<b>--- Test Case: Solid Chunks (Best Case) ---</b>");
            report.AppendLine($"  - With Diagonals: {solidDiagonals} ms");
            report.AppendLine($"  - Cardinals Only: {solidCardinals} ms");
            AppendWinner(report, solidDiagonals, solidCardinals, BenchmarkMode.WithDiagonals, BenchmarkMode.CardinalsOnly);

            // --- Checkerboard Data Report ---
            long checkerDiagonals = results["Checkerboard_WithDiagonals"];
            long checkerCardinals = results["Checkerboard_CardinalsOnly"];
            report.AppendLine("\n<b>--- Test Case: Checkerboard Chunks (Worst Case) ---</b>");
            report.AppendLine($"  - With Diagonals: {checkerDiagonals} ms");
            report.AppendLine($"  - Cardinals Only: {checkerCardinals} ms");
            AppendWinner(report, checkerDiagonals, checkerCardinals, BenchmarkMode.WithDiagonals, BenchmarkMode.CardinalsOnly);

            Debug.Log(report.ToString());
        }

        /// <summary>
        /// A helper method to compare two timings, determine the winner, and append a formatted result string to the report.
        /// </summary>
        /// <param name="sb">The StringBuilder to append the results to.</param>
        /// <param name="timeA">The timing for the first mode.</param>
        /// <param name="timeB">The timing for the second mode.</param>
        /// <param name="nameA">The name of the first mode.</param>
        /// <param name="nameB">The name of the second mode.</param>
        private void AppendWinner(StringBuilder sb, long timeA, long timeB, BenchmarkMode nameA, BenchmarkMode nameB)
        {
            if (timeA == timeB)
            {
                sb.AppendLine("  - <color=yellow>Result: Tied Performance.</color>");
                return;
            }

            long winnerTime = Math.Min(timeA, timeB);
            long loserTime = Math.Max(timeA, timeB);
            string winnerName = timeA < timeB ? nameA.ToString() : nameB.ToString();

            long difference = loserTime - winnerTime;
            float percentage = (difference / (float)loserTime) * 100f;

            sb.AppendLine($"  - <color=cyan>Winner: {winnerName} by {difference} ms ({percentage:F1}% faster)</color>");
        }

        #endregion

        #region Helper Struct

        /// <summary>
        /// Helper struct to hold the 9 NativeArrays for benchmark data.
        /// This ensures all data for a single job's input can be managed together.
        /// </summary>
        private struct BenchmarkVoxelData
        {
            private const int MAP_SIZE = VoxelData.ChunkWidth * VoxelData.ChunkHeight * VoxelData.ChunkWidth;
            public NativeArray<uint> Center, Back, Front, Left, Right, FrontRight, BackRight, BackLeft, FrontLeft;

            public BenchmarkVoxelData(Allocator allocator)
            {
                Center = new NativeArray<uint>(MAP_SIZE, allocator);
                Back = new NativeArray<uint>(MAP_SIZE, allocator);
                Front = new NativeArray<uint>(MAP_SIZE, allocator);
                Left = new NativeArray<uint>(MAP_SIZE, allocator);
                Right = new NativeArray<uint>(MAP_SIZE, allocator);
                FrontRight = new NativeArray<uint>(MAP_SIZE, allocator);
                BackRight = new NativeArray<uint>(MAP_SIZE, allocator);
                BackLeft = new NativeArray<uint>(MAP_SIZE, allocator);
                FrontLeft = new NativeArray<uint>(MAP_SIZE, allocator);
            }

            /// <summary>
            /// Copies the contents from a source BenchmarkVoxelData struct into this one.
            /// </summary>
            public void CopyFrom(BenchmarkVoxelData source)
            {
                NativeArray<uint>.Copy(source.Center, Center);
                NativeArray<uint>.Copy(source.Back, Back);
                NativeArray<uint>.Copy(source.Front, Front);
                NativeArray<uint>.Copy(source.Left, Left);
                NativeArray<uint>.Copy(source.Right, Right);
                NativeArray<uint>.Copy(source.FrontRight, FrontRight);
                NativeArray<uint>.Copy(source.BackRight, BackRight);
                NativeArray<uint>.Copy(source.BackLeft, BackLeft);
                NativeArray<uint>.Copy(source.FrontLeft, FrontLeft);
            }

            /// <summary>
            /// Fills all 9 maps at a specific index with the same value.
            /// </summary>
            public void FillAll(int index, uint value)
            {
                Center[index] = value;
                Back[index] = value;
                Front[index] = value;
                Left[index] = value;
                Right[index] = value;
                FrontRight[index] = value;
                BackRight[index] = value;
                BackLeft[index] = value;
                FrontLeft[index] = value;
            }

            /// <summary>
            /// Disposes all NativeArrays contained within this struct.
            /// </summary>
            public void Dispose()
            {
                if (Center.IsCreated) Center.Dispose();
                if (Back.IsCreated) Back.Dispose();
                if (Front.IsCreated) Front.Dispose();
                if (Left.IsCreated) Left.Dispose();
                if (Right.IsCreated) Right.Dispose();
                if (FrontRight.IsCreated) FrontRight.Dispose();
                if (BackRight.IsCreated) BackRight.Dispose();
                if (BackLeft.IsCreated) BackLeft.Dispose();
                if (FrontLeft.IsCreated) FrontLeft.Dispose();
            }
        }

        #endregion
    }
}