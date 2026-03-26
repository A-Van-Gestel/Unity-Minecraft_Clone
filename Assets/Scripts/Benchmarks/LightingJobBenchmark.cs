using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Data;
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
    /// It tests various scenarios including Sunlight propagation, Blocklight spread, Darkness propagation, and complex geometry.
    /// </summary>
    public class LightingJobBenchmark : MonoBehaviour
    {
        #region Enums

        private enum LightingScenario
        {
            /// <summary>
            /// Simulates a flat world where sunlight just needs to travel straight down.
            /// Tests the column recalculation logic.
            /// </summary>
            SunlightVerticalFlat,

            /// <summary>
            /// Simulates a world with many holes and overhangs (Swiss Cheese).
            /// Tests sunlight spreading horizontally into caves.
            /// </summary>
            SunlightComplexCaves,

            /// <summary>
            /// Simulates placing a few torches in an enclosed room.
            /// Standard gameplay scenario.
            /// </summary>
            BlocklightSimple,

            /// <summary>
            /// Simulates a massive number of light sources updating at once.
            /// Worst-case scenario for the BFS queue.
            /// </summary>
            BlocklightStressTest,

            /// <summary>
            /// Simulates placing a solid roof over complex terrain that was previously fully sunlit.
            /// Tests vertical and horizontal darkness propagation for sunlight into caves.
            /// </summary>
            SunlightRemovalCovered,

            /// <summary>
            /// Simulates breaking a single light source.
            /// Tests darkness propagation and potential refill from neighbors.
            /// </summary>
            BlocklightRemovalSimple,

            /// <summary>
            /// Simulates breaking hundreds of overlapping lights.
            /// This forces the engine to calculate darkness propagation AND neighbor refill logic simultaneously.
            /// </summary>
            BlocklightRemovalStress,
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

        [Header("Single Run Settings")]
        [SerializeField]
        private LightingScenario _scenario = LightingScenario.SunlightComplexCaves;

        [Header("Execution Settings")]
        [SerializeField]
        private bool _useBlockingWait = true;

        [SerializeField]
        private bool _runOnStart = true;

        [Header("Keybinding")]
        [SerializeField]
        private Key _triggerKey = Key.L;

        #endregion

        #region Private Fields

        private World _world;
        private bool _isBenchmarking;

        // Reusable setup data to avoid regenerating the test scenario for every single job iteration
        private LightingBenchmarkData _sourceData;

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
                Debug.LogError("LightingBenchmark requires a World instance!", this);
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

        private void OnDestroy()
        {
            if (_sourceData.IsCreated) _sourceData.Dispose();
        }

        #endregion

        #region Orchestration

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

        private IEnumerator RunSingleBenchmarkFromInspector()
        {
            long averageTime = 0;
            yield return StartCoroutine(ExecuteBenchmarkRun(_scenario, result => averageTime = result));

            Debug.Log("<color=lime>--- Single Benchmark Complete ---</color>\n" +
                      $"Scenario: {_scenario}\n" +
                      $"<b>Average Time over {_benchmarkRuns} runs: {averageTime} ms</b>");
        }

        private IEnumerator RunFullComparisonBenchmark()
        {
            _isBenchmarking = true;
            Debug.Log("--- Starting Full Comparison Lighting Benchmark ---");

            Dictionary<string, long> results = new Dictionary<string, long>();

            foreach (LightingScenario scenario in Enum.GetValues(typeof(LightingScenario)))
            {
                // SKIP REMOVAL SCENARIOS FOR NOW
                // These are currently producing unreliable results (0ms - 1ms) due to setup complexity
                // or neighbor interactions that are hard to simulate in a single-job isolated benchmark.
                if (scenario.ToString().Contains("Removal"))
                {
                    results[scenario.ToString()] = -1; // Sentinel value for "Skipped"
                    continue;
                }

                yield return StartCoroutine(ExecuteBenchmarkRun(scenario, result => results[scenario.ToString()] = result));
            }

            GenerateReport(results);
            _isBenchmarking = false;
        }

        private IEnumerator ExecuteBenchmarkRun(LightingScenario scenario, Action<long> onComplete)
        {
            _isBenchmarking = true;
            Debug.Log($"--- Running Benchmark: {scenario} ({_benchmarkRuns} runs) ---");

            // 1. Generate the source data for this specific scenario ONCE.
            // We will copy this into the jobs to ensure every job runs on identical clean data.
            if (_sourceData.IsCreated) _sourceData.Dispose();
            _sourceData = GenerateScenarioData(scenario, Allocator.Persistent);

            long totalMilliseconds = 0;

            for (int run = 0; run < _benchmarkRuns; run++)
            {
                NativeArray<JobHandle> jobHandles = new NativeArray<JobHandle>(_jobsToRun, Allocator.Persistent);
                // We need to track the data for every single job to dispose of it later.
                List<LightingJobData> activeJobDataList = new List<LightingJobData>(_jobsToRun);

                Stopwatch stopwatch = new Stopwatch();

                try
                {
                    // Prepare all jobs BEFORE starting the timer to measure execution time, not allocation time.
                    // However, allocation is technically part of the overhead.
                    // For strict algorithm testing, we prepare first.
                    for (int i = 0; i < _jobsToRun; i++)
                    {
                        activeJobDataList.Add(PrepareJob(Allocator.Persistent));
                    }

                    stopwatch.Start();

                    // --- Scheduling Phase ---
                    for (int i = 0; i < _jobsToRun; i++)
                    {
                        // Schedule the pre-prepared job
                        LightingJobData data = activeJobDataList[i];

                        // Re-create the job struct here to link the schedule
                        NeighborhoodLightingJob job = new NeighborhoodLightingJob
                        {
                            Map = data.Map,
                            ChunkPosition = new Vector2Int(0, 0), // Dummy position
                            SunlightBfsQueue = data.SunLightQueue,
                            BlocklightBfsQueue = data.BlockLightQueue,
                            SunlightColumnRecalcQueue = data.SunLightRecalcQueue,

                            Heightmap = data.Input.Heightmap,
                            NeighborN = data.Input.NeighborN, NeighborE = data.Input.NeighborE,
                            NeighborS = data.Input.NeighborS, NeighborW = data.Input.NeighborW,
                            NeighborNE = data.Input.NeighborNE, NeighborSE = data.Input.NeighborSE,
                            NeighborSW = data.Input.NeighborSW, NeighborNW = data.Input.NeighborNW,

                            BlockTypes = _world.JobDataManager.BlockTypesJobData,
                            CrossChunkLightMods = data.Mods,
                            IsStable = data.IsStable,
                        };

                        jobHandles[i] = job.Schedule();
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
                    // Cleanup
                    if (jobHandles.IsCreated) jobHandles.Dispose();
                    foreach (LightingJobData jobData in activeJobDataList)
                    {
                        jobData.Dispose();
                    }
                }

                if (!_useBlockingWait) yield return null;
            }

            onComplete(totalMilliseconds / Mathf.Max(1, _benchmarkRuns));
            _isBenchmarking = false;
        }

        #endregion

        #region Data Generation & Job Prep

        /// <summary>
        /// Creates a fresh copy of the data for a single job execution.
        /// </summary>
        private LightingJobData PrepareJob(Allocator allocator)
        {
            LightingJobData jobData = new LightingJobData
            {
                Input = new LightingJobInputData
                {
                    Heightmap = new NativeArray<ushort>(_sourceData.HeightMap, allocator),
                    NeighborN = new NativeArray<uint>(_sourceData.NeighborN, allocator),
                    NeighborE = new NativeArray<uint>(_sourceData.NeighborE, allocator),
                    NeighborS = new NativeArray<uint>(_sourceData.NeighborS, allocator),
                    NeighborW = new NativeArray<uint>(_sourceData.NeighborW, allocator),
                    NeighborNE = new NativeArray<uint>(_sourceData.NeighborNE, allocator),
                    NeighborSE = new NativeArray<uint>(_sourceData.NeighborSE, allocator),
                    NeighborSW = new NativeArray<uint>(_sourceData.NeighborSW, allocator),
                    NeighborNW = new NativeArray<uint>(_sourceData.NeighborNW, allocator),
                },
                Map = new NativeArray<uint>(_sourceData.Center, allocator),

                // Create Queues and populate them from source lists
                SunLightQueue = new NativeQueue<LightQueueNode>(allocator),
                BlockLightQueue = new NativeQueue<LightQueueNode>(allocator),
                SunLightRecalcQueue = new NativeQueue<Vector2Int>(allocator),

                Mods = new NativeList<LightModification>(allocator),
                IsStable = new NativeArray<bool>(1, allocator),
            };

            // Populate Queues
            foreach (LightQueueNode node in _sourceData.SourceSunLightQueue) jobData.SunLightQueue.Enqueue(node);
            foreach (LightQueueNode node in _sourceData.SourceBlockLightQueue) jobData.BlockLightQueue.Enqueue(node);
            foreach (Vector2Int col in _sourceData.SourceSunRecalcQueue) jobData.SunLightRecalcQueue.Enqueue(col);

            return jobData;
        }

        private LightingBenchmarkData GenerateScenarioData(LightingScenario scenario, Allocator allocator)
        {
            LightingBenchmarkData data = new LightingBenchmarkData(allocator);

            // 1. Fill Default Terrain (Solid Stone up to Y=60)
            FillDefaultTerrain(data, 60);

            switch (scenario)
            {
                case LightingScenario.SunlightVerticalFlat:
                    // Just flat terrain. Queue every column for recalculation.
                    for (int x = 0; x < VoxelData.ChunkWidth; x++)
                    for (int z = 0; z < VoxelData.ChunkWidth; z++)
                        data.SourceSunRecalcQueue.Add(new Vector2Int(x, z));
                    break;

                case LightingScenario.SunlightComplexCaves:
                    // Create a "Swiss Cheese" effect: randomly remove blocks inside the solid area.
                    CarveSwissCheese(data);
                    // Queue recalc
                    for (int x = 0; x < VoxelData.ChunkWidth; x++)
                    for (int z = 0; z < VoxelData.ChunkWidth; z++)
                        data.SourceSunRecalcQueue.Add(new Vector2Int(x, z));
                    break;

                case LightingScenario.BlocklightSimple:
                    // Make a hollow room
                    CarveRoom(data, 5, 5, 5, 10, 10, 10);
                    // Place a light source
                    PlaceLightSource(data, new Vector3Int(7, 7, 7), 15);
                    break;

                case LightingScenario.BlocklightStressTest:
                    // Huge hollow tower
                    CarveRoom(data, 1, 1, 1, 14, 120, 14);
                    // High density placement (Every 3 blocks) -> ~900 lights
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

                case LightingScenario.SunlightRemovalCovered:
                    // 1. Setup "Swiss Cheese" terrain
                    CarveSwissCheese(data);

                    // 2. Pre-fill "Daytime" state: All Air is fully lit (15).
                    for (int i = 0; i < data.Center.Length; i++)
                    {
                        // If ID is Air (0), set sunlight to 15.
                        if (BurstVoxelDataBitMapping.GetId(data.Center[i]) == BlockIDs.Air)
                        {
                            data.Center[i] = BurstVoxelDataBitMapping.SetSunLight(data.Center[i], 15);
                        }
                    }

                    // 3. Place a solid platform at Y=100 (blocking light)
                    for (int x = 0; x < VoxelData.ChunkWidth; x++)
                    {
                        for (int z = 0; z < VoxelData.ChunkWidth; z++)
                        {
                            int index = x + VoxelData.ChunkWidth * (100 + VoxelData.ChunkHeight * z);

                            // Set to Stone (Solid, Opacity 15, Light 0)
                            data.Center[index] = BurstVoxelDataBitMapping.PackVoxelData(BlockIDs.Stone, 0, 0, 1, 0);

                            // Trigger vertical darkness logic via Column Recalc
                            data.SourceSunRecalcQueue.Add(new Vector2Int(x, z));
                        }
                    }

                    break;

                case LightingScenario.BlocklightRemovalSimple:
                    CarveRoom(data, 1, 1, 1, 14, 14, 14);
                    PrecalculateBlockLight(data, new Vector3Int(7, 7, 7), 15);
                    RemoveLightSource(data, new Vector3Int(7, 7, 7), 15);
                    break;

                case LightingScenario.BlocklightRemovalStress:
                    // 1. Huge hollow tower
                    CarveRoom(data, 1, 1, 1, 14, 120, 14);

                    // 2. ISOLATED lights.
                    for (int y = 5; y < 118; y += 16)
                    {
                        for (int x = 4; x < 13; x += 4)
                        {
                            for (int z = 4; z < 13; z += 4)
                            {
                                Vector3Int pos = new Vector3Int(x, y, z);
                                // Pre-populate the light field so there is something to remove
                                PrecalculateBlockLight(data, pos, 15);
                                // Queue removal
                                RemoveLightSource(data, pos, 15);
                            }
                        }
                    }

                    break;
            }

            return data;
        }

        #region Generation Helpers

        private static void FillDefaultTerrain(LightingBenchmarkData data, int height)
        {
            uint solid = BurstVoxelDataBitMapping.PackVoxelData(BlockIDs.Stone, 0, 0, 1, 0); // Stone
            uint air = BurstVoxelDataBitMapping.PackVoxelData(BlockIDs.Air, 0, 0, 1, 0); // Air

            for (int i = 0; i < data.Center.Length; i++)
            {
                int y = i / VoxelData.ChunkWidth % VoxelData.ChunkHeight;
                uint val = y <= height ? solid : air;

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

            // Set Heightmap
            for (int i = 0; i < data.HeightMap.Length; i++) data.HeightMap[i] = (ushort)height;
        }

        private static void CarveSwissCheese(LightingBenchmarkData data)
        {
            Random.InitState(12345); // Fixed seed for consistency
            for (int x = 0; x < VoxelData.ChunkWidth; x++)
            {
                for (int z = 0; z < VoxelData.ChunkWidth; z++)
                {
                    for (int y = 10; y < 55; y++)
                    {
                        if (Random.value > 0.6f) // 40% chance of air hole
                        {
                            int index = x + VoxelData.ChunkWidth * (y + VoxelData.ChunkHeight * z);
                            data.Center[index] = BurstVoxelDataBitMapping.SetId(data.Center[index], BlockIDs.Air); // Set to Air
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
                        int index = x + VoxelData.ChunkWidth * (y + VoxelData.ChunkHeight * z);
                        data.Center[index] = BurstVoxelDataBitMapping.SetId(data.Center[index], BlockIDs.Air);
                    }
                }
            }
        }

        private static void PlaceLightSource(LightingBenchmarkData data, Vector3Int pos, byte level)
        {
            int index = pos.x + VoxelData.ChunkWidth * (pos.y + VoxelData.ChunkHeight * pos.z);

            // Use Lava as the light-emitting block
            data.Center[index] = BurstVoxelDataBitMapping.PackVoxelData(BlockIDs.Lava, 0, level, 1, 0);

            // Add to queue
            data.SourceBlockLightQueue.Add(new LightQueueNode
            {
                Position = pos,
                OldLightLevel = 0,
            });
        }

        /// <summary>
        /// Manually propagates light in the setup array to simulate an existing light source ("Before" state).
        /// This simplified BFS only modifies the Center chunk array.
        /// </summary>
        private void PrecalculateBlockLight(LightingBenchmarkData data, Vector3Int srcPos, byte level)
        {
            // 1. Set Source in Map
            int srcIdx = srcPos.x + VoxelData.ChunkWidth * (srcPos.y + VoxelData.ChunkHeight * srcPos.z);
            data.Center[srcIdx] = BurstVoxelDataBitMapping.PackVoxelData(BlockIDs.Lava, 0, level, 1, 0);

            Queue<(Vector3Int p, int l)> queue = new Queue<(Vector3Int p, int l)>();
            queue.Enqueue((srcPos, level));

            while (queue.Count > 0)
            {
                (Vector3Int pos, int l) = queue.Dequeue();

                if (!IsInsideChunk(pos)) continue;

                int idx = pos.x + VoxelData.ChunkWidth * (pos.y + VoxelData.ChunkHeight * pos.z);
                uint currentPacked = data.Center[idx];

                // If solid (and not the source itself), stop.
                ushort id = BurstVoxelDataBitMapping.GetId(currentPacked);
                if (id == BlockIDs.Stone) continue; // Stone

                // If current light is already higher/equal, skip (simple visited check logic)
                byte currentLight = BurstVoxelDataBitMapping.GetBlockLight(currentPacked);
                if (currentLight >= l) continue;

                // Update Light
                data.Center[idx] = BurstVoxelDataBitMapping.SetBlockLight(currentPacked, (byte)l);

                if (l <= 1) continue;

                // Enqueue neighbors
                queue.Enqueue((pos + Vector3Int.up, l - 1));
                queue.Enqueue((pos + Vector3Int.down, l - 1));
                queue.Enqueue((pos + Vector3Int.left, l - 1));
                queue.Enqueue((pos + Vector3Int.right, l - 1));
                queue.Enqueue((pos + Vector3Int.forward, l - 1));
                queue.Enqueue((pos + Vector3Int.back, l - 1));
            }
        }

        private static void RemoveLightSource(LightingBenchmarkData data, Vector3Int pos, byte oldLevel)
        {
            // 1. Set Block to Air (ID 0), Light 0.
            int idx = pos.x + VoxelData.ChunkWidth * (pos.y + VoxelData.ChunkHeight * pos.z);
            data.Center[idx] = BurstVoxelDataBitMapping.PackVoxelData(BlockIDs.Air, 0, 0, 1, 0);

            // 2. Queue Removal
            data.SourceBlockLightQueue.Add(new LightQueueNode
            {
                Position = pos,
                OldLightLevel = oldLevel,
            });
        }

        private static bool IsInsideChunk(Vector3Int pos)
        {
            return pos.x >= 0 && pos.x < VoxelData.ChunkWidth &&
                   pos.y >= 0 && pos.y < VoxelData.ChunkHeight &&
                   pos.z >= 0 && pos.z < VoxelData.ChunkWidth;
        }

        #endregion

        #endregion

        #region Reporting

        private void GenerateReport(Dictionary<string, long> results)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("<color=cyan><b>--- NEIGHBORHOOD LIGHTING BENCHMARK REPORT ---</b></color>");
            sb.AppendLine($"Configuration: {_jobsToRun} jobs per run, {_benchmarkRuns} runs average.\n");

            long baseline = results.ContainsKey(nameof(LightingScenario.SunlightVerticalFlat))
                ? results[nameof(LightingScenario.SunlightVerticalFlat)]
                : 1; // Avoid divide by zero if baseline is skipped/missing

            foreach ((string key, long time) in results)
            {
                string name = key.Replace("_", " ");

                if (time == -1)
                {
                    sb.AppendLine($"<b>{name}:</b>");
                    sb.AppendLine("  <color=orange>SKIPPED (Currently Unreliable)</color>");
                    sb.AppendLine();
                    continue;
                }

                float perJob = (float)time / _jobsToRun * 1000f; // Microseconds per job approx

                sb.AppendLine($"<b>{name}:</b>");
                sb.AppendLine($"  Total Time: {time} ms");
                sb.AppendLine($"  ~Time per Chunk: {perJob:F2} μs"); // Microseconds

                if (name != nameof(LightingScenario.SunlightVerticalFlat).Replace("_", " "))
                {
                    float factor = (float)time / baseline;
                    sb.AppendLine($"  Cost Factor: {factor:F1}x baseline");
                }

                sb.AppendLine();
            }

            Debug.Log(sb.ToString());
        }

        #endregion

        #region Helper Structs

        /// <summary>
        /// Holds the raw NativeArrays and Lists needed to populate a job.
        /// Used to generate data once and copy it many times.
        /// </summary>
        private struct LightingBenchmarkData
        {
            public NativeArray<uint> Center;
            public NativeArray<ushort> HeightMap;
            public NativeArray<uint> NeighborN, NeighborE, NeighborS, NeighborW;
            public NativeArray<uint> NeighborNE, NeighborSE, NeighborSW, NeighborNW;

            // Using managed lists here to easily copy into NativeQueues later
            public List<LightQueueNode> SourceSunLightQueue;
            public readonly List<LightQueueNode> SourceBlockLightQueue;
            public readonly List<Vector2Int> SourceSunRecalcQueue;

            public bool IsCreated;

            public LightingBenchmarkData(Allocator allocator)
            {
                const int mapSize = VoxelData.ChunkWidth * VoxelData.ChunkHeight * VoxelData.ChunkWidth;
                const int heightMapSize = VoxelData.ChunkWidth * VoxelData.ChunkWidth;

                Center = new NativeArray<uint>(mapSize, allocator);
                HeightMap = new NativeArray<ushort>(heightMapSize, allocator);

                NeighborN = new NativeArray<uint>(mapSize, allocator);
                NeighborE = new NativeArray<uint>(mapSize, allocator);
                NeighborS = new NativeArray<uint>(mapSize, allocator);
                NeighborW = new NativeArray<uint>(mapSize, allocator);
                NeighborNE = new NativeArray<uint>(mapSize, allocator);
                NeighborSE = new NativeArray<uint>(mapSize, allocator);
                NeighborSW = new NativeArray<uint>(mapSize, allocator);
                NeighborNW = new NativeArray<uint>(mapSize, allocator);

                SourceSunLightQueue = new List<LightQueueNode>();
                SourceBlockLightQueue = new List<LightQueueNode>();
                SourceSunRecalcQueue = new List<Vector2Int>();

                IsCreated = true;
            }

            public void Dispose()
            {
                if (!IsCreated) return;
                if (Center.IsCreated) Center.Dispose();
                if (HeightMap.IsCreated) HeightMap.Dispose();
                if (NeighborN.IsCreated) NeighborN.Dispose();
                if (NeighborE.IsCreated) NeighborE.Dispose();
                if (NeighborS.IsCreated) NeighborS.Dispose();
                if (NeighborW.IsCreated) NeighborW.Dispose();
                if (NeighborNE.IsCreated) NeighborNE.Dispose();
                if (NeighborSE.IsCreated) NeighborSE.Dispose();
                if (NeighborSW.IsCreated) NeighborSW.Dispose();
                if (NeighborNW.IsCreated) NeighborNW.Dispose();
                IsCreated = false;
            }
        }

        #endregion
    }
}
