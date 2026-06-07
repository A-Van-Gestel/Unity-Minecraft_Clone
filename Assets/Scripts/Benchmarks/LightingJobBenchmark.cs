using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Data;
using Helpers;
using Jobs;
using Jobs.BurstData;
using Jobs.Data;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.InputSystem;
using Debug = UnityEngine.Debug;
using Random = UnityEngine.Random;

namespace Benchmarks
{
    /// <summary>
    /// A benchmark utility for measuring the performance of the NeighborhoodLightingJob.
    /// Tests various scenarios including sunlight propagation, blocklight spread, darkness removal,
    /// edge consistency checks, and complex geometry. Produces structured reports with system info
    /// and optional file output for baseline tracking.
    /// </summary>
    public class LightingJobBenchmark : MonoBehaviour
    {
        #region Enums

        private enum LightingScenario
        {
            // --- Sunlight scenarios ---

            /// <summary>
            /// Flat world where sunlight travels straight down. Tests column recalculation logic.
            /// Used as the baseline for cost-factor comparisons.
            /// </summary>
            SunlightVerticalFlat,

            /// <summary>
            /// World with many holes and overhangs (Swiss Cheese). Tests sunlight spreading
            /// horizontally into caves after column recalculation.
            /// </summary>
            SunlightComplexCaves,

            /// <summary>
            /// Places a solid roof over fully-sunlit Swiss Cheese terrain. Tests vertical and
            /// horizontal darkness propagation. Requires two-phase setup: first propagate light,
            /// then measure the darkness removal.
            /// </summary>
            SunlightRemovalCovered,

            // --- Blocklight scenarios ---

            /// <summary>
            /// Single torch in a hollow room. Standard gameplay scenario for blocklight propagation.
            /// </summary>
            BlocklightSimple,

            /// <summary>
            /// ~900 light sources in a large hollow tower. Worst-case BFS queue saturation.
            /// </summary>
            BlocklightStressTest,

            /// <summary>
            /// Single light source placed, propagated, then removed. Tests darkness propagation
            /// and neighbor refill. Requires two-phase setup.
            /// </summary>
            BlocklightRemovalSimple,

            /// <summary>
            /// ~50 isolated lights placed, propagated, then all removed simultaneously.
            /// Tests mass darkness propagation with overlapping refill zones. Requires two-phase setup.
            /// </summary>
            BlocklightRemovalStress,

            // --- Edge check scenarios ---

            /// <summary>
            /// Pre-lit terrain with deliberate cross-chunk light mismatches on all 4 borders.
            /// Tests the Starlight-inspired edge consistency check pass.
            /// </summary>
            EdgeCheckConsistency,

            // --- Phase 2 RGB stubs (not yet functional) ---

            /// <summary>
            /// [Phase 2] Single colored torch — measures per-channel BFS overhead vs scalar.
            /// Skipped until RGB blocklight BFS is implemented.
            /// </summary>
            BlocklightRGBSimple,

            /// <summary>
            /// [Phase 2] Red, green, and blue sources overlapping — per-channel max stress.
            /// Skipped until RGB blocklight BFS is implemented.
            /// </summary>
            BlocklightRGBOverlap,

            /// <summary>
            /// [Phase 2] Remove one colored source from a multicolor lit area.
            /// Skipped until RGB blocklight BFS is implemented.
            /// </summary>
            BlocklightRGBRemoval,

            /// <summary>
            /// [Phase 2] ~900 colored lights cycling R/G/B — worst-case RGB BFS queue.
            /// Skipped until RGB blocklight BFS is implemented.
            /// </summary>
            BlocklightRGBStress,
        }

        /// <summary>
        /// Scenarios that require two-phase setup: first run the lighting job to establish a
        /// pre-lit state, then benchmark the second operation (removal/edge check).
        /// </summary>
        private static bool IsTwoPhaseScenario(LightingScenario scenario)
        {
            return scenario is LightingScenario.SunlightRemovalCovered
                or LightingScenario.BlocklightRemovalSimple
                or LightingScenario.BlocklightRemovalStress
                or LightingScenario.BlocklightRGBRemoval;
        }

        /// <summary>
        /// Phase 2 RGB scenarios that cannot run until the RGB blocklight BFS is implemented.
        /// </summary>
        private static bool IsPhase2Stub(LightingScenario scenario)
        {
            return scenario is LightingScenario.BlocklightRGBSimple
                or LightingScenario.BlocklightRGBOverlap
                or LightingScenario.BlocklightRGBRemoval
                or LightingScenario.BlocklightRGBStress;
        }

        #endregion

        #region Serialized Fields

        [Header("Benchmark Configuration")]
        [Tooltip("Whether the benchmark is enabled and allowed to run.")]
        [SerializeField]
        private bool _benchMarkEnabled = true;

        [Tooltip("If checked, runs all scenarios sequentially and generates a final report.")]
        [SerializeField]
        private bool _runFullComparison = true;

        [Tooltip("The number of times to run each benchmark scenario to get a stable average.")]
        [SerializeField]
        [Min(1)]
        private int _benchmarkRuns = 5;

        [Tooltip("The number of individual jobs (chunks) to execute per run.")]
        [SerializeField]
        private int _jobsToRun = 512;


        [Header("Single Run Settings (Used if Full Comparison is false)")]
        [Tooltip("The scenario to test for a single run.")]
        [SerializeField]
        private LightingScenario _scenario = LightingScenario.SunlightComplexCaves;


        [Header("Execution Settings")]
        [Tooltip("If true, the benchmark will freeze the editor for the most accurate time.")]
        [SerializeField]
        private bool _useBlockingWait = true;

        [Tooltip("If checked, the benchmark will run automatically when the scene starts.")]
        [SerializeField]
        private bool _runOnStart = true;

        [Tooltip("If checked, the report (with rich-text tags stripped) is written to a timestamped " +
                 "file under Application.persistentDataPath/Benchmarks/. The file path is logged " +
                 "to the console after each run.")]
        [SerializeField]
        private bool _writeReportToFile = true;


        [Header("Keybinding")]
        [Tooltip("Press this key to manually trigger the benchmark.")]
        [SerializeField]
        private Key _triggerKey = Key.L;

        #endregion

        #region Private Fields

        private World _world;
        private bool _isBenchmarking;

        #endregion

        #region Unity Lifecycle

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
                Debug.LogError("LightingJobBenchmark requires a World instance in the scene!", this);
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
        /// The main entry point for starting a benchmark. Decides whether to run a full
        /// comparison or a single test based on inspector settings.
        /// </summary>
        private void TriggerBenchmark()
        {
            if (_isBenchmarking)
            {
                Debug.LogWarning("Benchmark is already in progress.");
                return;
            }

            StartCoroutine(_runFullComparison ? RunFullComparisonBenchmark() : RunSingleBenchmarkFromInspector());
        }

        /// <summary>
        /// Runs a single benchmark configuration based on the Inspector settings and logs the result.
        /// </summary>
        private IEnumerator RunSingleBenchmarkFromInspector()
        {
            _isBenchmarking = true;
            long averageTime = 0;
            yield return StartCoroutine(ExecuteBenchmarkRun(_scenario, result => averageTime = result));

            Debug.Log("<color=lime>--- Single Benchmark Complete ---</color>\n" +
                      $"Scenario: {_scenario}\n" +
                      $"<b>Average Time over {_benchmarkRuns} runs: {averageTime} ms</b>");
            _isBenchmarking = false;
        }

        /// <summary>
        /// Orchestrates the full comparison benchmark, running every scenario sequentially.
        /// </summary>
        private IEnumerator RunFullComparisonBenchmark()
        {
            _isBenchmarking = true;
            Debug.Log("--- Starting Full Comparison Lighting Benchmark ---");

            Stopwatch totalStopwatch = Stopwatch.StartNew();
            Dictionary<string, long> results = new Dictionary<string, long>();

            foreach (LightingScenario scenario in Enum.GetValues(typeof(LightingScenario)))
            {
                if (IsPhase2Stub(scenario))
                {
                    results[scenario.ToString()] = RESULT_STUB;
                    continue;
                }

                yield return StartCoroutine(ExecuteBenchmarkRun(scenario, result => results[scenario.ToString()] = result));
            }

            totalStopwatch.Stop();

            Debug.Log("--- All Benchmark Runs Complete. Generating Report... ---");
            GenerateReport(results, totalStopwatch.Elapsed);
            _isBenchmarking = false;
        }

        /// <summary>
        /// The core logic for executing a single benchmark configuration multiple times and
        /// returning the average result. Handles warm-up, data generation (including two-phase
        /// setup for removal scenarios), job scheduling, completion, and cleanup.
        /// </summary>
        /// <param name="scenario">The lighting scenario to benchmark.</param>
        /// <param name="onComplete">Callback that returns the calculated average time in milliseconds.</param>
        private IEnumerator ExecuteBenchmarkRun(LightingScenario scenario, Action<long> onComplete)
        {
            Debug.Log($"--- Running Benchmark: {scenario} ({_benchmarkRuns} runs) ---");

            bool edgeCheck = scenario == LightingScenario.EdgeCheckConsistency;

            // Generate the source data ONCE for this set of runs to ensure consistency.
            LightingBenchmarkData sourceData = GenerateScenarioData(scenario, Allocator.Persistent);

            try
            {
                // For two-phase scenarios, run the lighting job once to establish the "lit" state,
                // then set up the removal/modification operation on the result.
                if (IsTwoPhaseScenario(scenario))
                {
                    PreLightSourceData(ref sourceData);
                    SetupRemovalPhase(ref sourceData, scenario);
                }

                long totalMilliseconds = 0;

                // --- Discarded warm-up run ---
                // Schedule and complete one job to absorb Burst JIT compilation cost, JobsUtility
                // setup, and first-touch allocator overhead. Without this, the first iteration is
                // consistently 5-10x slower, contaminating the average.
                {
                    LightingJobData warmupData = PrepareJob(sourceData, Allocator.Persistent);
                    try
                    {
                        JobHandle warmupHandle = ScheduleJob(warmupData, edgeCheck);
                        warmupHandle.Complete();
                    }
                    finally
                    {
                        warmupData.Dispose();
                    }
                }

                for (int run = 0; run < _benchmarkRuns; run++)
                {
                    NativeArray<JobHandle> jobHandles = new NativeArray<JobHandle>(_jobsToRun, Allocator.Persistent);
                    List<LightingJobData> activeJobDataList = new List<LightingJobData>(_jobsToRun);
                    Stopwatch stopwatch = new Stopwatch();

                    try
                    {
                        stopwatch.Start();

                        // --- Scheduling Phase ---
                        for (int i = 0; i < _jobsToRun; i++)
                        {
                            LightingJobData jobData = PrepareJob(sourceData, Allocator.Persistent);
                            activeJobDataList.Add(jobData);
                            jobHandles[i] = ScheduleJob(jobData, edgeCheck);
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
                        totalMilliseconds += stopwatch.ElapsedMilliseconds;
                    }
                    finally
                    {
                        if (jobHandles.IsCreated) jobHandles.Dispose();
                        foreach (LightingJobData jobData in activeJobDataList) jobData.Dispose();
                    }

                    if (!_useBlockingWait) yield return null;
                }

                onComplete(totalMilliseconds / Mathf.Max(1, _benchmarkRuns));
            }
            finally
            {
                sourceData.Dispose();
            }
        }

        #endregion

        #region Job Scheduling

        /// <summary>
        /// Creates a fresh copy of the source data for a single job execution.
        /// </summary>
        private LightingJobData PrepareJob(LightingBenchmarkData sourceData, Allocator allocator)
        {
            LightingJobData jobData = new LightingJobData
            {
                Input = new LightingJobInputData
                {
                    Heightmap = new NativeArray<ushort>(sourceData.HeightMap, allocator),
                    NeighborN = new NativeArray<uint>(sourceData.NeighborN, allocator),
                    NeighborE = new NativeArray<uint>(sourceData.NeighborE, allocator),
                    NeighborS = new NativeArray<uint>(sourceData.NeighborS, allocator),
                    NeighborW = new NativeArray<uint>(sourceData.NeighborW, allocator),
                    NeighborNE = new NativeArray<uint>(sourceData.NeighborNE, allocator),
                    NeighborSE = new NativeArray<uint>(sourceData.NeighborSE, allocator),
                    NeighborSW = new NativeArray<uint>(sourceData.NeighborSW, allocator),
                    NeighborNW = new NativeArray<uint>(sourceData.NeighborNW, allocator),
                    LightN = new NativeArray<ushort>(sourceData.LightN, allocator),
                    LightE = new NativeArray<ushort>(sourceData.LightE, allocator),
                    LightS = new NativeArray<ushort>(sourceData.LightS, allocator),
                    LightW = new NativeArray<ushort>(sourceData.LightW, allocator),
                    LightNE = new NativeArray<ushort>(sourceData.LightNE, allocator),
                    LightSE = new NativeArray<ushort>(sourceData.LightSE, allocator),
                    LightSW = new NativeArray<ushort>(sourceData.LightSW, allocator),
                    LightNW = new NativeArray<ushort>(sourceData.LightNW, allocator),
                },
                Map = new NativeArray<uint>(sourceData.Center, allocator),
                LightMap = new NativeArray<ushort>(sourceData.CenterLight, allocator),

                SunLightQueue = new NativeQueue<LightQueueNode>(allocator),
                BlockLightQueue = new NativeQueue<LightQueueNode>(allocator),
                SunLightRecalcQueue = new NativeQueue<Vector2Int>(allocator),

                Mods = new NativeList<LightModification>(allocator),
                IsStable = new NativeArray<bool>(1, allocator),
            };

            foreach (LightQueueNode lightQueueNode in sourceData.SourceSunLightQueue)
                jobData.SunLightQueue.Enqueue(lightQueueNode);

            foreach (LightQueueNode lightQueueNode in sourceData.SourceBlockLightQueue)
                jobData.BlockLightQueue.Enqueue(lightQueueNode);

            foreach (Vector2Int vector2Int in sourceData.SourceSunRecalcQueue)
                jobData.SunLightRecalcQueue.Enqueue(vector2Int);

            return jobData;
        }

        /// <summary>
        /// Schedules a <see cref="NeighborhoodLightingJob"/> from a prepared data container and
        /// returns the <see cref="JobHandle"/>. The <paramref name="data"/> fields (especially
        /// <c>PerformEdgeCheck</c>) control whether the edge consistency pass runs.
        /// </summary>
        private JobHandle ScheduleJob(LightingJobData data, bool performEdgeCheck = false)
        {
            NeighborhoodLightingJob job = new NeighborhoodLightingJob
            {
                Map = data.Map,
                LightMap = data.LightMap,
                ChunkPosition = new Vector2Int(0, 0),
                SunlightBfsQueue = data.SunLightQueue,
                BlocklightBfsQueue = data.BlockLightQueue,
                SunlightColumnRecalcQueue = data.SunLightRecalcQueue,

                Heightmap = data.Input.Heightmap,
                NeighborN = data.Input.NeighborN,
                NeighborE = data.Input.NeighborE,
                NeighborS = data.Input.NeighborS,
                NeighborW = data.Input.NeighborW,
                NeighborNE = data.Input.NeighborNE,
                NeighborSE = data.Input.NeighborSE,
                NeighborSW = data.Input.NeighborSW,
                NeighborNW = data.Input.NeighborNW,
                LightN = data.Input.LightN,
                LightE = data.Input.LightE,
                LightS = data.Input.LightS,
                LightW = data.Input.LightW,
                LightNE = data.Input.LightNE,
                LightSE = data.Input.LightSE,
                LightSW = data.Input.LightSW,
                LightNW = data.Input.LightNW,

                BlockTypes = _world.JobDataManager.BlockTypesJobData,
                CrossChunkLightMods = data.Mods,
                IsStable = data.IsStable,
                PerformEdgeCheck = performEdgeCheck,
            };

            return job.Schedule();
        }

        #endregion

        #region Two-Phase Setup

        /// <summary>
        /// Runs the lighting job on the source data to establish a fully-lit "before" state.
        /// After this call, the Center map contains propagated light values. The BFS queues
        /// are cleared so the source data represents a stable, lit world.
        /// </summary>
        private void PreLightSourceData(ref LightingBenchmarkData sourceData)
        {
            LightingJobData preLight = PrepareJob(sourceData, Allocator.Persistent);
            try
            {
                JobHandle handle = ScheduleJob(preLight);
                handle.Complete();

                // Copy the now-lit center map and light map back into the source data.
                NativeArray<uint>.Copy(preLight.Map, sourceData.Center);
                NativeArray<ushort>.Copy(preLight.LightMap, sourceData.CenterLight);

                // Apply cross-chunk light modifications to the neighbor arrays so that
                // subsequent removal benchmarks start from correct border light state.
                foreach (LightModification mod in preLight.Mods)
                {
                    ApplyModToNeighbor(ref sourceData, mod);
                }

                // Clear the queues — the pre-lighting phase consumed them. The removal phase
                // will populate new queue entries for the actual operation being benchmarked.
                sourceData.SourceSunLightQueue.Clear();
                sourceData.SourceBlockLightQueue.Clear();
                sourceData.SourceSunRecalcQueue.Clear();
            }
            finally
            {
                preLight.Dispose();
            }
        }

        /// <summary>
        /// Applies a single <see cref="LightModification"/> to the appropriate neighbor array
        /// in the source data. The benchmark uses ChunkPosition=(0,0), so global positions
        /// map directly: x&lt;0 = West, x&gt;=16 = East, z&lt;0 = South, z&gt;=16 = North.
        /// </summary>
        private static void ApplyModToNeighbor(ref LightingBenchmarkData sourceData, LightModification mod)
        {
            int gx = mod.GlobalPosition.x;
            int gy = mod.GlobalPosition.y;
            int gz = mod.GlobalPosition.z;

            if (gy < 0 || gy >= VoxelData.ChunkHeight) return;

            int localX = gx;
            int localZ = gz;
            NativeArray<uint> target;
            NativeArray<ushort> lightTarget;

            if (gx < 0)
            {
                localX = gx + VoxelData.ChunkWidth;
                if (gz < 0)
                {
                    localZ = gz + VoxelData.ChunkWidth;
                    target = sourceData.NeighborSW;
                    lightTarget = sourceData.LightSW;
                }
                else if (gz >= VoxelData.ChunkWidth)
                {
                    localZ = gz - VoxelData.ChunkWidth;
                    target = sourceData.NeighborNW;
                    lightTarget = sourceData.LightNW;
                }
                else
                {
                    target = sourceData.NeighborW;
                    lightTarget = sourceData.LightW;
                }
            }
            else if (gx >= VoxelData.ChunkWidth)
            {
                localX = gx - VoxelData.ChunkWidth;
                if (gz < 0)
                {
                    localZ = gz + VoxelData.ChunkWidth;
                    target = sourceData.NeighborSE;
                    lightTarget = sourceData.LightSE;
                }
                else if (gz >= VoxelData.ChunkWidth)
                {
                    localZ = gz - VoxelData.ChunkWidth;
                    target = sourceData.NeighborNE;
                    lightTarget = sourceData.LightNE;
                }
                else
                {
                    target = sourceData.NeighborE;
                    lightTarget = sourceData.LightE;
                }
            }
            else if (gz < 0)
            {
                localZ = gz + VoxelData.ChunkWidth;
                target = sourceData.NeighborS;
                lightTarget = sourceData.LightS;
            }
            else if (gz >= VoxelData.ChunkWidth)
            {
                localZ = gz - VoxelData.ChunkWidth;
                target = sourceData.NeighborN;
                lightTarget = sourceData.LightN;
            }
            else
            {
                return;
            }

            if (!target.IsCreated || target.Length == 0) return;

            int idx = ChunkMath.GetFlattenedIndexInChunk(localX, gy, localZ);
            uint packed = target[idx];
            uint updated = mod.Channel == LightChannel.Sun
                ? BurstVoxelDataBitMapping.SetSunLight(packed, mod.LightLevel)
                : BurstVoxelDataBitMapping.SetBlockLight(packed, mod.LightLevel);
            target[idx] = updated;

            // Also update the parallel ushort light array so the BFS job reads correct values
            ushort light = lightTarget[idx];
            if (mod.Channel == LightChannel.Sun)
                lightTarget[idx] = LightBitMapping.SetSkyLight(light, mod.LightLevel);
            else
                lightTarget[idx] = LightBitMapping.SetBlocklightRGB(light, mod.BlockR, mod.BlockG, mod.BlockB);
        }

        /// <summary>
        /// After pre-lighting, sets up the removal operation by modifying the source data
        /// (removing blocks/light sources) and queuing the appropriate BFS entries.
        /// </summary>
        private static void SetupRemovalPhase(ref LightingBenchmarkData sourceData, LightingScenario scenario)
        {
            switch (scenario)
            {
                case LightingScenario.SunlightRemovalCovered:
                    // Place a solid platform at Y=100, blocking light from above.
                    // Queue column recalculations so the job processes the darkness.
                    for (int x = 0; x < VoxelData.ChunkWidth; x++)
                    {
                        for (int z = 0; z < VoxelData.ChunkWidth; z++)
                        {
                            int index = ChunkMath.GetFlattenedIndexInChunk(x, 100, z);
                            sourceData.Center[index] = BurstVoxelDataBitMapping.PackVoxelData(
                                BlockIDs.Stone, 0, 0,
                                BurstVoxelDataBitMapping.BuildMetaLegacy(orientation: 1, fluidLevel: 0, isFluid: false));
                            sourceData.HeightMap[x + VoxelData.ChunkWidth * z] = 100;

                            sourceData.SourceSunRecalcQueue.Add(new Vector2Int(x, z));
                        }
                    }

                    break;

                case LightingScenario.BlocklightRemovalSimple:
                    RemoveLightSource(sourceData, new Vector3Int(7, 7, 7));
                    break;

                case LightingScenario.BlocklightRemovalStress:
                    for (int y = 5; y < 118; y += 16)
                    {
                        for (int x = 4; x < 13; x += 4)
                        {
                            for (int z = 4; z < 13; z += 4)
                            {
                                RemoveLightSource(sourceData, new Vector3Int(x, y, z));
                            }
                        }
                    }

                    break;

                case LightingScenario.BlocklightRGBRemoval:
                    // Phase 2 stub — will be populated when RGB BFS is implemented.
                    break;
            }
        }

        #endregion

        #region Data Generation

        /// <summary>
        /// Generates the initial source data for a scenario. For two-phase scenarios, this creates
        /// the "setup" state (terrain + light sources placed). The caller is responsible for running
        /// <see cref="PreLightSourceData"/> and <see cref="SetupRemovalPhase"/> afterwards.
        /// </summary>
        private static LightingBenchmarkData GenerateScenarioData(LightingScenario scenario, Allocator allocator)
        {
            LightingBenchmarkData data = new LightingBenchmarkData(allocator);

            // Most scenarios start with solid terrain up to Y=60.
            FillDefaultTerrain(data, 60);

            switch (scenario)
            {
                case LightingScenario.SunlightVerticalFlat:
                    QueueAllColumns(data);
                    break;

                case LightingScenario.SunlightComplexCaves:
                    CarveSwissCheese(data);
                    QueueAllColumns(data);
                    break;

                case LightingScenario.SunlightRemovalCovered:
                    // Phase 1: Create Swiss Cheese terrain and propagate sunlight.
                    // Phase 2 (SetupRemovalPhase): Place a roof and queue column recalculations.
                    CarveSwissCheese(data);
                    QueueAllColumns(data);
                    break;

                case LightingScenario.BlocklightSimple:
                    CarveRoom(data, 5, 5, 5, 10, 10, 10);
                    PlaceLightSource(data, new Vector3Int(7, 7, 7), 15);
                    break;

                case LightingScenario.BlocklightStressTest:
                    CarveRoom(data, 1, 1, 1, 14, 120, 14);
                    for (int y = 2; y < 118; y += 2)
                    {
                        for (int x = 2; x < 13; x += 3)
                        {
                            for (int z = 2; z < 13; z += 3)
                            {
                                PlaceLightSource(data, new Vector3Int(x, y, z), 15);
                            }
                        }
                    }

                    break;

                case LightingScenario.BlocklightRemovalSimple:
                    // Phase 1: Create room with one light source (pre-lit by PreLightSourceData).
                    // Phase 2 (SetupRemovalPhase): Remove the source and queue darkness.
                    CarveRoom(data, 1, 1, 1, 14, 14, 14);
                    PlaceLightSource(data, new Vector3Int(7, 7, 7), 15);
                    break;

                case LightingScenario.BlocklightRemovalStress:
                    // Phase 1: Create tower with ~50 lights (pre-lit by PreLightSourceData).
                    // Phase 2 (SetupRemovalPhase): Remove all sources and queue darkness.
                    CarveRoom(data, 1, 1, 1, 14, 120, 14);
                    for (int y = 5; y < 118; y += 16)
                    {
                        for (int x = 4; x < 13; x += 4)
                        {
                            for (int z = 4; z < 13; z += 4)
                            {
                                PlaceLightSource(data, new Vector3Int(x, y, z), 15);
                            }
                        }
                    }

                    break;

                case LightingScenario.EdgeCheckConsistency:
                    SetupEdgeCheckScenario(data);
                    break;

                // Phase 2 RGB stubs — no data generation yet.
                case LightingScenario.BlocklightRGBSimple:
                case LightingScenario.BlocklightRGBOverlap:
                case LightingScenario.BlocklightRGBRemoval:
                case LightingScenario.BlocklightRGBStress:
                    break;
            }

            return data;
        }

        #region Generation Helpers

        private static void FillDefaultTerrain(LightingBenchmarkData data, int height)
        {
            byte legacySolidMeta = BurstVoxelDataBitMapping.BuildMetaLegacy(orientation: 1, fluidLevel: 0, isFluid: false);
            uint solid = BurstVoxelDataBitMapping.PackVoxelData(BlockIDs.Stone, 0, 0, legacySolidMeta);
            uint air = BurstVoxelDataBitMapping.PackVoxelData(BlockIDs.Air, 0, 0, 0);

            for (int y = 0; y < VoxelData.ChunkHeight; y++)
            {
                uint val = y <= height ? solid : air;
                for (int z = 0; z < VoxelData.ChunkWidth; z++)
                {
                    for (int x = 0; x < VoxelData.ChunkWidth; x++)
                    {
                        int i = ChunkMath.GetFlattenedIndexInChunk(x, y, z);
                        data.Center[i] = val;
                        data.NeighborN[i] = val;
                        data.NeighborE[i] = val;
                        data.NeighborS[i] = val;
                        data.NeighborW[i] = val;
                        data.NeighborNE[i] = val;
                        data.NeighborSE[i] = val;
                        data.NeighborSW[i] = val;
                        data.NeighborNW[i] = val;
                    }
                }
            }

            for (int i = 0; i < data.HeightMap.Length; i++) data.HeightMap[i] = (ushort)height;
        }

        private static void QueueAllColumns(LightingBenchmarkData data)
        {
            for (int x = 0; x < VoxelData.ChunkWidth; x++)
            for (int z = 0; z < VoxelData.ChunkWidth; z++)
                data.SourceSunRecalcQueue.Add(new Vector2Int(x, z));
        }

        private static void CarveSwissCheese(LightingBenchmarkData data)
        {
            Random.InitState(12345);
            for (int x = 0; x < VoxelData.ChunkWidth; x++)
            {
                for (int z = 0; z < VoxelData.ChunkWidth; z++)
                {
                    for (int y = 10; y < 55; y++)
                    {
                        if (Random.value > 0.6f)
                        {
                            int index = ChunkMath.GetFlattenedIndexInChunk(x, y, z);
                            data.Center[index] = BurstVoxelDataBitMapping.SetId(data.Center[index], BlockIDs.Air);
                        }
                    }
                }
            }
        }

        private static void CarveRoom(LightingBenchmarkData data, int startX, int startY, int startZ, int sizeX, int sizeY, int sizeZ)
        {
            for (int x = startX; x < startX + sizeX; x++)
            {
                for (int y = startY; y < startY + sizeY; y++)
                {
                    for (int z = startZ; z < startZ + sizeZ; z++)
                    {
                        int index = ChunkMath.GetFlattenedIndexInChunk(x, y, z);
                        data.Center[index] = BurstVoxelDataBitMapping.SetId(data.Center[index], BlockIDs.Air);
                    }
                }
            }
        }

        private static void PlaceLightSource(LightingBenchmarkData data, Vector3Int pos, byte level)
        {
            int index = ChunkMath.GetFlattenedIndexInChunk(pos.x, pos.y, pos.z);
            data.Center[index] = BurstVoxelDataBitMapping.PackVoxelData(BlockIDs.Lava, 0, level,
                BurstVoxelDataBitMapping.BuildMetaLegacy(orientation: 1, fluidLevel: 0, isFluid: false));
            data.CenterLight[index] = LightBitMapping.PackLightData(0, level, level, level);

            data.SourceBlockLightQueue.Add(new LightQueueNode
            {
                Position = pos,
                OldLightLevel = 0,
            });
        }

        /// <summary>
        /// Removes a light source at the given position by replacing it with air and queuing
        /// the old light level for darkness removal. Reads the current blocklight from the
        /// map to correctly seed the removal BFS.
        /// </summary>
        private static void RemoveLightSource(LightingBenchmarkData data, Vector3Int pos)
        {
            int index = ChunkMath.GetFlattenedIndexInChunk(pos.x, pos.y, pos.z);
            byte oldLevel = LightBitMapping.GetMaxBlocklight(data.CenterLight[index]);

            data.Center[index] = BurstVoxelDataBitMapping.PackVoxelData(BlockIDs.Air, 0, 0, 0);
            data.CenterLight[index] = 0;

            data.SourceBlockLightQueue.Add(new LightQueueNode
            {
                Position = pos,
                OldLightLevel = oldLevel,
            });
        }

        /// <summary>
        /// Sets up the edge consistency check scenario. Creates terrain with light on the
        /// center chunk but artificially stale (lower) light on the border voxels, so the
        /// edge check pass has mismatches to detect and correct.
        /// </summary>
        private static void SetupEdgeCheckScenario(LightingBenchmarkData data)
        {
            // Fill all neighbor maps with lit air above the terrain (sunlight=15).
            uint litAir = BurstVoxelDataBitMapping.PackVoxelData(BlockIDs.Air, 15, 0, 0);
            for (int y = 61; y < VoxelData.ChunkHeight; y++)
            {
                for (int z = 0; z < VoxelData.ChunkWidth; z++)
                {
                    for (int x = 0; x < VoxelData.ChunkWidth; x++)
                    {
                        int i = ChunkMath.GetFlattenedIndexInChunk(x, y, z);
                        data.NeighborN[i] = litAir;
                        data.NeighborE[i] = litAir;
                        data.NeighborS[i] = litAir;
                        data.NeighborW[i] = litAir;
                        data.NeighborNE[i] = litAir;
                        data.NeighborSE[i] = litAir;
                        data.NeighborSW[i] = litAir;
                        data.NeighborNW[i] = litAir;
                    }
                }
            }

            // Center chunk border voxels above terrain are air with sunlight=0 (stale).
            // The edge check should detect that neighbors have light=15 and correct these.
            for (int y = 61; y < VoxelData.ChunkHeight; y++)
            {
                // South border (z=0)
                for (int x = 0; x < VoxelData.ChunkWidth; x++)
                {
                    int idx = ChunkMath.GetFlattenedIndexInChunk(x, y, 0);
                    data.Center[idx] = BurstVoxelDataBitMapping.PackVoxelData(BlockIDs.Air, 0, 0, 0);
                }

                // North border (z=15)
                for (int x = 0; x < VoxelData.ChunkWidth; x++)
                {
                    int idx = ChunkMath.GetFlattenedIndexInChunk(x, y, VoxelData.ChunkWidth - 1);
                    data.Center[idx] = BurstVoxelDataBitMapping.PackVoxelData(BlockIDs.Air, 0, 0, 0);
                }

                // West border (x=0)
                for (int z = 0; z < VoxelData.ChunkWidth; z++)
                {
                    int idx = ChunkMath.GetFlattenedIndexInChunk(0, y, z);
                    data.Center[idx] = BurstVoxelDataBitMapping.PackVoxelData(BlockIDs.Air, 0, 0, 0);
                }

                // East border (x=15)
                for (int z = 0; z < VoxelData.ChunkWidth; z++)
                {
                    int idx = ChunkMath.GetFlattenedIndexInChunk(VoxelData.ChunkWidth - 1, y, z);
                    data.Center[idx] = BurstVoxelDataBitMapping.PackVoxelData(BlockIDs.Air, 0, 0, 0);
                }
            }

            // No BFS queues needed — the edge check itself seeds the placement queue internally.
            // The benchmark measures how long the edge check + resulting propagation takes.
        }

        #endregion

        #endregion

        #region Reporting

        private const long RESULT_STUB = -2;

        /// <summary>
        /// Generates a structured report from the collected results, including system info,
        /// timing breakdown, and optional file output.
        /// </summary>
        /// <param name="results">Average ms per run for each scenario.</param>
        /// <param name="totalElapsed">Wall-clock time of the full comparison run.</param>
        private void GenerateReport(Dictionary<string, long> results, TimeSpan totalElapsed)
        {
            string systemInfo = BenchmarkEnvironment.DescribeSystem();

            StringBuilder report = new StringBuilder();
            report.AppendLine("<color=cyan><b>--- NEIGHBORHOOD LIGHTING BENCHMARK REPORT ---</b></color>");
            report.AppendLine($"Test configuration: {_jobsToRun} jobs per run, averaged over {_benchmarkRuns} runs.");
            report.AppendLine($"All numbers are: <i>ms per run</i> ({_jobsToRun} jobs) | <i>μs per job</i> (derived).");
            report.AppendLine($"Total wall-clock runtime: {BenchmarkEnvironment.FormatDuration(totalElapsed)}");
            report.AppendLine();
            report.Append(systemInfo);
            report.AppendLine("=== Benchmark Results ===");
            report.AppendLine();

            long baseline = results.GetValueOrDefault(nameof(LightingScenario.SunlightVerticalFlat), 1);
            if (baseline <= 0) baseline = 1;

            foreach (LightingScenario scenario in Enum.GetValues(typeof(LightingScenario)))
            {
                string key = scenario.ToString();
                if (!results.TryGetValue(key, out long time)) continue;

                report.AppendLine($"<b>--- {FormatScenarioName(key)} ---</b>");

                if (time == RESULT_STUB)
                {
                    report.AppendLine("  <color=orange>STUB (Phase 2 — not yet implemented)</color>");
                    report.AppendLine();
                    continue;
                }

                AppendTimingRow(report, time);

                if (scenario != LightingScenario.SunlightVerticalFlat)
                {
                    float factor = (float)time / baseline;
                    report.AppendLine($"  vs Baseline:  {factor,8:F1}x");
                }

                report.AppendLine();
            }

            string fullReport = report.ToString();
            Debug.Log(fullReport);

            if (_writeReportToFile)
            {
                BenchmarkEnvironment.WriteReportToDisk(fullReport, "LightingJobBenchmark");
            }
        }

        /// <summary>Formats one row of the report with both ms-per-run and μs-per-job.</summary>
        private void AppendTimingRow(StringBuilder sb, long msPerRun)
        {
            float microsPerJob = _jobsToRun > 0
                ? msPerRun * 1000f / _jobsToRun
                : 0f;
            sb.AppendLine($"  Total:        {msPerRun,5} ms  ({microsPerJob,7:F1} μs/job)");
        }

        /// <summary>Inserts spaces before uppercase letters to produce a readable name.</summary>
        private static string FormatScenarioName(string enumName)
        {
            StringBuilder sb = new StringBuilder(enumName.Length + 8);
            for (int i = 0; i < enumName.Length; i++)
            {
                char c = enumName[i];
                if (i > 0 && char.IsUpper(c) && !char.IsUpper(enumName[i - 1]))
                    sb.Append(' ');
                sb.Append(c);
            }

            return sb.ToString();
        }

        #endregion

        #region Helper Struct

        /// <summary>
        /// Holds the raw NativeArrays and managed queue lists needed to populate jobs.
        /// Generated once per scenario, then copied into each job instance.
        /// </summary>
        private struct LightingBenchmarkData
        {
            private const int MAP_SIZE = VoxelData.ChunkWidth * VoxelData.ChunkHeight * VoxelData.ChunkWidth;
            private const int HEIGHTMAP_SIZE = VoxelData.ChunkWidth * VoxelData.ChunkWidth;

            public NativeArray<uint> Center;
            public NativeArray<ushort> CenterLight;
            public NativeArray<ushort> HeightMap;
            public NativeArray<uint> NeighborN, NeighborE, NeighborS, NeighborW;
            public NativeArray<uint> NeighborNE, NeighborSE, NeighborSW, NeighborNW;
            public NativeArray<ushort> LightN, LightE, LightS, LightW;
            public NativeArray<ushort> LightNE, LightSE, LightSW, LightNW;

            // No current scenario populates SourceSunLightQueue (sunlight uses column
            // recalc via SourceSunRecalcQueue), but the field mirrors the job struct's
            // SunlightBfsQueue for future scenarios that need direct sun BFS entries.
            public List<LightQueueNode> SourceSunLightQueue;
            public List<LightQueueNode> SourceBlockLightQueue;
            public List<Vector2Int> SourceSunRecalcQueue;

            public LightingBenchmarkData(Allocator allocator)
            {
                Center = new NativeArray<uint>(MAP_SIZE, allocator);
                CenterLight = new NativeArray<ushort>(MAP_SIZE, allocator);
                HeightMap = new NativeArray<ushort>(HEIGHTMAP_SIZE, allocator);

                NeighborN = new NativeArray<uint>(MAP_SIZE, allocator);
                NeighborE = new NativeArray<uint>(MAP_SIZE, allocator);
                NeighborS = new NativeArray<uint>(MAP_SIZE, allocator);
                NeighborW = new NativeArray<uint>(MAP_SIZE, allocator);
                NeighborNE = new NativeArray<uint>(MAP_SIZE, allocator);
                NeighborSE = new NativeArray<uint>(MAP_SIZE, allocator);
                NeighborSW = new NativeArray<uint>(MAP_SIZE, allocator);
                NeighborNW = new NativeArray<uint>(MAP_SIZE, allocator);

                LightN = new NativeArray<ushort>(MAP_SIZE, allocator);
                LightE = new NativeArray<ushort>(MAP_SIZE, allocator);
                LightS = new NativeArray<ushort>(MAP_SIZE, allocator);
                LightW = new NativeArray<ushort>(MAP_SIZE, allocator);
                LightNE = new NativeArray<ushort>(MAP_SIZE, allocator);
                LightSE = new NativeArray<ushort>(MAP_SIZE, allocator);
                LightSW = new NativeArray<ushort>(MAP_SIZE, allocator);
                LightNW = new NativeArray<ushort>(MAP_SIZE, allocator);

                SourceSunLightQueue = new List<LightQueueNode>();
                SourceBlockLightQueue = new List<LightQueueNode>();
                SourceSunRecalcQueue = new List<Vector2Int>();
            }

            public void Dispose()
            {
                if (Center.IsCreated) Center.Dispose();
                if (CenterLight.IsCreated) CenterLight.Dispose();
                if (HeightMap.IsCreated) HeightMap.Dispose();
                if (NeighborN.IsCreated) NeighborN.Dispose();
                if (NeighborE.IsCreated) NeighborE.Dispose();
                if (NeighborS.IsCreated) NeighborS.Dispose();
                if (NeighborW.IsCreated) NeighborW.Dispose();
                if (NeighborNE.IsCreated) NeighborNE.Dispose();
                if (NeighborSE.IsCreated) NeighborSE.Dispose();
                if (NeighborSW.IsCreated) NeighborSW.Dispose();
                if (NeighborNW.IsCreated) NeighborNW.Dispose();
                if (LightN.IsCreated) LightN.Dispose();
                if (LightE.IsCreated) LightE.Dispose();
                if (LightS.IsCreated) LightS.Dispose();
                if (LightW.IsCreated) LightW.Dispose();
                if (LightNE.IsCreated) LightNE.Dispose();
                if (LightSE.IsCreated) LightSE.Dispose();
                if (LightSW.IsCreated) LightSW.Dispose();
                if (LightNW.IsCreated) LightNW.Dispose();
            }
        }

        #endregion
    }
}
