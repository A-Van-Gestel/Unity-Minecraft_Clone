using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Data;
using Helpers;
using Jobs;
using Jobs.BurstData;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.InputSystem;
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
            CardinalsOnly, // Passes only the 4 cardinal neighbors.
        }

        /// <summary>
        /// Defines the type of voxel data pattern to use for mesh generation.
        /// </summary>
        private enum ChunkDataType
        {
            /// <summary>All-stone chunk. Almost no exposed faces — measures vertex-throughput floor.</summary>
            Solid,

            /// <summary>Alternating stone/air. Worst-case face-generation throughput for the standard cube path.</summary>
            Checkerboard,

            /// <summary>All-stone with cycling orientation values 0-5. Exercises the per-face rotation
            /// and `GetTranslatedFaceIndex` paths that the existing `Solid` pattern leaves at identity.
            /// This is the path Phase 2's meshing rewrite touches.</summary>
            OrientedCubes,

            /// <summary>Alternating stone/air at <see cref="Checkerboard"/> density, with the stone
            /// voxels cycling through the 4 supported horizontal orientations. Combines maximum face
            /// exposure (each stone has 6 air neighbors → 6 faces drawn) with non-identity rotations,
            /// so the per-face rotation hot path is exercised on ~98k faces/chunk instead of just
            /// the ~1.5k boundary faces that <see cref="OrientedCubes"/> sees. Strongest detector for
            /// Phase 2b regressions in the rotation/face-translation code.</summary>
            OrientedCheckerboard,

            /// <summary>All-water chunk. Exercises `GenerateFluidMeshData` (case 1) end-to-end.</summary>
            Fluid,

            /// <summary>Alternating leaves/air. Exercises the transparent triangle path with
            /// <c>renderNeighborFaces=true</c> at the same vertex throughput as <see cref="Checkerboard"/>.
            /// An all-leaves variant would render every leaf-leaf face twice (because
            /// <c>renderNeighborFaces</c> disables same-block culling), producing ~55 MB of native
            /// output per chunk and overwhelming Editor RAM at typical chunk counts.</summary>
            Transparent,

            /// <summary>Realistic mix: ~70% stone, ~10% air, ~5% directional block (custom mesh),
            /// ~5% grass blades (cross mesh), ~5% leaves (transparent), ~5% water (fluid). Closest
            /// proxy to in-game terrain, exercises all four `MeshGenerationJob` render cases.</summary>
            MixedTerrain,
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

        [Tooltip("If checked, the report (with system-info header and rich-text tags stripped) is written " +
                 "to a timestamped file under Application.persistentDataPath/Benchmarks/. The file path is " +
                 "logged to the console after each run.")]
        [SerializeField]
        private bool _writeReportToFile = true;


        [Header("Keybinding")]
        [Tooltip("Press this key to manually trigger the benchmark.")]
        [SerializeField]
        private Key _triggerKey = Key.M;

        #endregion

        #region Private Fields

        private World _world;
        private bool _isBenchmarking;

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
            if (Keyboard.current[_triggerKey].wasPressedThisFrame)
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

            // Run a single test using the Inspector settings.
            StartCoroutine(_runFullComparison ? RunFullComparisonBenchmark() : RunSingleBenchmarkFromInspector());
        }

        /// <summary>
        /// A coroutine that runs a single benchmark configuration based on the Inspector settings and logs the result.
        /// </summary>
        private IEnumerator RunSingleBenchmarkFromInspector()
        {
            long averageTime = 0;
            // Execute the benchmark and capture the average time via a callback.
            yield return StartCoroutine(ExecuteBenchmarkRun(_mode, _dataType, result => averageTime = result));

            Debug.Log("<color=lime>--- Single Benchmark Complete ---</color>\n" +
                      $"Configuration: {_mode} | {_dataType}\n" +
                      $"<b>Average Time over {_benchmarkRuns} runs: {averageTime} ms</b>");
        }

        /// <summary>
        /// The master coroutine that orchestrates the full comparison benchmark, running every
        /// (data type × mode) combination sequentially.
        /// </summary>
        private IEnumerator RunFullComparisonBenchmark()
        {
            _isBenchmarking = true;
            Debug.Log("--- Starting Full Comparison Benchmark ---");

            // Total runtime covers every pattern × mode combination plus per-scenario warm-up and
            // cleanup. Logged in the report so cross-machine comparisons can spot anomalies in
            // total wall-clock cost (which scales with `_chunksToMesh` × `_benchmarkRuns` × scenarios).
            Stopwatch totalStopwatch = Stopwatch.StartNew();

            Dictionary<string, long> results = new Dictionary<string, long>();

            // Iterate every data type × every mode in declaration order.
            foreach (ChunkDataType dataType in Enum.GetValues(typeof(ChunkDataType)))
            {
                foreach (BenchmarkMode mode in Enum.GetValues(typeof(BenchmarkMode)))
                {
                    string key = $"{dataType}_{mode}";
                    yield return StartCoroutine(ExecuteBenchmarkRun(mode, dataType, result => results[key] = result));
                }
            }

            totalStopwatch.Stop();

            Debug.Log("--- All Benchmark Runs Complete. Generating Report... ---");
            GenerateReport(results, totalStopwatch.Elapsed);
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

            // --- Discarded warm-up run ---
            // Schedule and complete one job before the timed loop to absorb Burst JIT compilation
            // cost, JobsUtility setup, and first-touch allocator overhead. Without this, the first
            // iteration of `_benchmarkRuns` is consistently 5-10× slower than subsequent ones,
            // which contaminates the average. The warm-up's timing is intentionally discarded.
            {
                BenchmarkVoxelData warmupInput = new BenchmarkVoxelData(Allocator.Persistent);
                warmupInput.CopyFrom(benchmarkData);
                (JobHandle warmupHandle, MeshDataJobOutput warmupOutput) = ScheduleBenchmarkMeshing(warmupInput, mode);
                warmupHandle.Complete();
                warmupOutput.Dispose();
                warmupInput.Dispose();
            }

            for (int run = 0; run < _benchmarkRuns; run++)
            {
                NativeArray<JobHandle> jobHandles = new NativeArray<JobHandle>(_chunksToMesh, Allocator.Persistent);
                List<BenchmarkVoxelData> inputDataToDispose = new List<BenchmarkVoxelData>(_chunksToMesh);
                List<MeshDataJobOutput> outputDataToDispose = new List<MeshDataJobOutput>(_chunksToMesh);
                Stopwatch stopwatch = new Stopwatch();

                try
                {
                    stopwatch.Start();

                    // --- Scheduling Phase ---
                    for (int i = 0; i < _chunksToMesh; i++)
                    {
                        // For each job, create its input data with Allocator.Persistent.
                        BenchmarkVoxelData jobInputData = new BenchmarkVoxelData(Allocator.Persistent);
                        jobInputData.CopyFrom(benchmarkData);
                        inputDataToDispose.Add(jobInputData); // Track it for cleanup.

                        // Schedule the job.
                        (JobHandle handle, MeshDataJobOutput output) jobInfo = ScheduleBenchmarkMeshing(jobInputData, mode);
                        jobHandles[i] = jobInfo.handle;
                        outputDataToDispose.Add(jobInfo.output); // Track output for cleanup.
                    }

                    // --- Completion Phase ---
                    JobHandle combinedHandle = JobHandle.CombineDependencies(jobHandles);

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
                    foreach (BenchmarkVoxelData data in inputDataToDispose) data.Dispose();
                    foreach (MeshDataJobOutput data in outputDataToDispose) data.Dispose();
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
            MeshDataJobOutput meshOutput = new MeshDataJobOutput(Allocator.Persistent);

            // SectionData is required by MeshGenerationJob.Execute (one entry per 16-block section).
            // We deliberately leave every entry at default (IsEmpty=false, IsFullySolid=false) so that
            // every section is processed via IterateStandardSection — the per-voxel hot path the
            // Phase 2b meshing rewrite targets. Production uses the IsFullySolid shell-iteration
            // optimization for solid sections, but that would skew the benchmark away from the
            // code path under refactor.
            const int sectionCount = VoxelData.ChunkHeight / ChunkMath.SECTION_SIZE;
            NativeArray<SectionJobData> sectionData = new NativeArray<SectionJobData>(sectionCount, Allocator.TempJob);

            // If we're in CardinalsOnly mode, create a temporary empty array to pass to the unused job fields.
            NativeArray<uint> emptyArray = mode == BenchmarkMode.CardinalsOnly
                ? new NativeArray<uint>(0, Allocator.TempJob)
                : default;

            MeshGenerationJob job = new MeshGenerationJob
            {
                Map = data.Center,
                SectionData = sectionData,
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
                MaxVisibleY = -1,
                Output = meshOutput,
            };

            // Schedule the job and chain disposal of all temporary native containers to its handle
            // so they auto-free when the job completes (avoids JobTempAlloc 4-frame leak warnings).
            JobHandle handle = job.Schedule();
            handle = sectionData.Dispose(handle);
            if (emptyArray.IsCreated)
            {
                handle = emptyArray.Dispose(handle);
            }

            return (handle, meshOutput);
        }

        /// <summary>
        /// Generates a set of 9 chunk maps filled with a specific voxel pattern for testing.
        /// </summary>
        /// <param name="type">The pattern to generate.</param>
        /// <param name="allocator">The memory allocator to use for the NativeArrays.</param>
        /// <returns>A BenchmarkVoxelData struct containing the generated maps.</returns>
        private BenchmarkVoxelData GenerateBenchmarkData(ChunkDataType type, Allocator allocator)
        {
            BenchmarkVoxelData data = new BenchmarkVoxelData(allocator);
            for (int i = 0; i < data.Center.Length; i++)
            {
                (ushort idToPlace, byte meta) = GetVoxelForPattern(type, i);
                uint packed = BurstVoxelDataBitMapping.PackVoxelData(idToPlace, 15, 0, meta);
                data.FillAll(i, packed);
            }

            return data;
        }

        /// <summary>
        /// The four horizontal orientations supported by <c>VoxelHelper.GetRotationAngle</c> at v5
        /// (0=South/Back, 1=North/Front, 4=West/Left, 5=East/Right). Top (2) and Bottom (3) are
        /// storage-encodable but not yet runtime-supported — passing them to the meshing job triggers
        /// a `Debug.LogWarning` per voxel from inside Burst, which floods Unity's log queue and leaks
        /// memory at benchmark scale. Phase 2b adds full support; this array can grow to {0..5} then.
        /// </summary>
        private static readonly byte[] s_supportedHorizontalOrientations = { 0, 1, 4, 5 };

        /// <summary>
        /// Determines the block ID and meta byte for a given voxel index based on the desired data pattern.
        /// </summary>
        /// <param name="type">The data pattern type.</param>
        /// <param name="index">The flat array index of the voxel.</param>
        /// <returns>A (block ID, meta) tuple. Meta is encoded with the legacy rule appropriate to each pattern.</returns>
        private static (ushort id, byte meta) GetVoxelForPattern(ChunkDataType type, int index)
        {
            switch (type)
            {
                case ChunkDataType.Solid:
                    return (BlockIDs.Stone, BuildSolidMeta(orientation: 1));

                case ChunkDataType.Checkerboard:
                {
                    int x = index % VoxelData.ChunkWidth;
                    int y = index / VoxelData.ChunkWidth % VoxelData.ChunkHeight;
                    int z = index / (VoxelData.ChunkWidth * VoxelData.ChunkHeight);
                    bool stone = (x + y + z) % 2 == 0;
                    return stone
                        ? (BlockIDs.Stone, BuildSolidMeta(orientation: 1))
                        : (BlockIDs.Air, (byte)0);
                }

                case ChunkDataType.OrientedCubes:
                {
                    // Cycle through the four runtime-supported horizontal orientations. This still
                    // exercises the per-face Y-rotation path (0°/90°/180°/270° all hit), which is
                    // what the Phase 2b rewrite touches. Top/Bottom would also be valuable coverage
                    // but are blocked on Phase 2b — see s_supportedHorizontalOrientations.
                    byte orient = s_supportedHorizontalOrientations[index % 4];
                    return (BlockIDs.Stone, BuildSolidMeta(orient));
                }

                case ChunkDataType.OrientedCheckerboard:
                {
                    // Stone half of a Checkerboard with non-identity orientations. Air neighbors mean
                    // every stone draws all 6 faces, so the per-face rotation/translation paths run
                    // ~64× more often than in OrientedCubes (where interior cull dominates).
                    int x = index % VoxelData.ChunkWidth;
                    int y = index / VoxelData.ChunkWidth % VoxelData.ChunkHeight;
                    int z = index / (VoxelData.ChunkWidth * VoxelData.ChunkHeight);
                    bool stone = (x + y + z) % 2 == 0;
                    if (!stone) return (BlockIDs.Air, 0);

                    byte orient = s_supportedHorizontalOrientations[index % 4];
                    return (BlockIDs.Stone, BuildSolidMeta(orient));
                }

                case ChunkDataType.Fluid:
                    // Source water (level 0). Exercises GenerateFluidMeshData on every voxel.
                    return (BlockIDs.Water, BurstVoxelDataBitMapping.BuildMetaLegacy(orientation: 0, fluidLevel: 0, isFluid: true));

                case ChunkDataType.Transparent:
                {
                    // Alternating leaves/air — same density as Checkerboard but routed through the
                    // transparent submesh. Each leaf has air neighbors so all 6 faces are drawn,
                    // exercising `ShouldDrawFace` with `renderNeighborFaces=true` at a memory budget
                    // that matches Checkerboard (~25 MB native output per chunk).
                    int x = index % VoxelData.ChunkWidth;
                    int y = index / VoxelData.ChunkWidth % VoxelData.ChunkHeight;
                    int z = index / (VoxelData.ChunkWidth * VoxelData.ChunkHeight);
                    bool leaves = (x + y + z) % 2 == 0;
                    return leaves
                        ? (BlockIDs.OakLeaves, BuildSolidMeta(orientation: 1))
                        : (BlockIDs.Air, (byte)0);
                }

                case ChunkDataType.MixedTerrain:
                {
                    // Deterministic distribution by index modulo 20:
                    //   0    → water        (5%, fluid path)
                    //   1    → grass blades (5%, cross mesh)
                    //   2    → leaves       (5%, transparent)
                    //   3    → directional  (5%, custom mesh w/ rotation)
                    //   4-5  → air          (10%)
                    //   6-19 → stone        (70%, baseline standard cube)
                    int bucket = index % 20;
                    return bucket switch
                    {
                        0 => (BlockIDs.Water, BurstVoxelDataBitMapping.BuildMetaLegacy(orientation: 0, fluidLevel: 0, isFluid: true)),
                        1 => (BlockIDs.GrassBlades, BuildSolidMeta(orientation: 1)),
                        2 => (BlockIDs.OakLeaves, BuildSolidMeta(orientation: 1)),
                        3 => (BlockIDs.DirectionalBlock, BuildSolidMeta(orientation: s_supportedHorizontalOrientations[(index / 20) % 4])), // cycle through supported horizontals only
                        4 or 5 => (BlockIDs.Air, 0),
                        _ => (BlockIDs.Stone, BuildSolidMeta(orientation: 1)),
                    };
                }

                default:
                    return (BlockIDs.Air, 0);
            }
        }

        /// <summary>Helper for the common solid-block meta encoding.</summary>
        private static byte BuildSolidMeta(byte orientation)
            => BurstVoxelDataBitMapping.BuildMetaLegacy(orientation, fluidLevel: 0, isFluid: false);

        #endregion

        #region Reporting

        /// <summary>
        /// Generates the final, formatted report from the collected results and logs it to the console.
        /// </summary>
        /// <param name="results">A dictionary containing the average ms-per-run for each test configuration.</param>
        /// <param name="totalElapsed">Wall-clock time of the full <c>RunFullComparisonBenchmark</c> coroutine, including warm-ups and cleanup.</param>
        private void GenerateReport(Dictionary<string, long> results, TimeSpan totalElapsed)
        {
            // Build the system/build/Burst header once. Goes into both console and on-disk file
            // so a captured baseline can be cross-referenced with the build it was captured against.
            string systemInfo = BenchmarkEnvironment.DescribeSystem();

            StringBuilder report = new StringBuilder();
            report.AppendLine("<color=lime><b>--- MESH GENERATION BENCHMARK REPORT ---</b></color>");
            report.AppendLine($"Test configuration: {_chunksToMesh} chunks per run, averaged over {_benchmarkRuns} runs.");
            report.AppendLine($"All numbers are: <i>ms per run</i> ({_chunksToMesh} chunks) | <i>μs per chunk</i> (derived).");
            report.AppendLine($"Total wall-clock runtime: {BenchmarkEnvironment.FormatDuration(totalElapsed)}");
            report.AppendLine();
            report.Append(systemInfo);
            report.AppendLine("=== Benchmark results ===");

            // Iterate every pattern × mode combination in declaration order so the report mirrors the run.
            foreach (ChunkDataType dataType in Enum.GetValues(typeof(ChunkDataType)))
            {
                long withDiagonals = results.GetValueOrDefault($"{dataType}_{BenchmarkMode.WithDiagonals}", 0);
                long cardinalsOnly = results.GetValueOrDefault($"{dataType}_{BenchmarkMode.CardinalsOnly}", 0);

                report.AppendLine($"<b>--- {dataType} ---</b>");
                AppendModeRow(report, "With Diagonals", withDiagonals);
                AppendModeRow(report, "Cardinals Only", cardinalsOnly);
                AppendWinner(report, withDiagonals, cardinalsOnly, BenchmarkMode.WithDiagonals, BenchmarkMode.CardinalsOnly);
                report.AppendLine();
            }

            string fullReport = report.ToString();
            Debug.Log(fullReport);

            if (_writeReportToFile)
            {
                BenchmarkEnvironment.WriteReportToDisk(fullReport, "MeshGenerationBenchmark");
            }
        }

        /// <summary>Formats one row of the report with both ms-per-run and μs-per-chunk.</summary>
        private void AppendModeRow(StringBuilder sb, string label, long msPerRun)
        {
            float microsPerChunk = _chunksToMesh > 0
                ? msPerRun * 1000f / _chunksToMesh
                : 0f;
            sb.AppendLine($"  - {label,-16}: {msPerRun,5} ms  ({microsPerChunk,7:F1} μs/chunk)");
        }

        /// <summary>
        /// Compares the WithDiagonals vs CardinalsOnly timings for a single pattern and appends a winner row.
        /// </summary>
        private static void AppendWinner(StringBuilder sb, long timeA, long timeB, BenchmarkMode nameA, BenchmarkMode nameB)
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
            float percentage = loserTime == 0 ? 0f : difference / (float)loserTime * 100f;

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
