using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using Data;
using Jobs;
using Jobs.BurstData;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Benchmarks
{
    public class MeshGenerationBenchmark : MonoBehaviour
    {
        private enum BenchmarkMode
        {
            WithDiagonals, // Passes all 8 neighbors to the job.
            CardinalsOnly // Passes only the 4 cardinal neighbors.
        }

        private enum ChunkDataType
        {
            Solid, // Easiest case: very few faces to generate.
            Checkerboard // Worst case: maximum number of faces to generate.
        }

        [Header("Benchmark Settings")]
        [Tooltip("Whether the benchmark is enabled and allowed to run.")]
        [SerializeField]
        private bool _benchmarkEnabled = true;

        [Tooltip("The number of chunk meshes to generate for the test.")]
        [SerializeField]
        private int _chunksToMesh = 512;

        [Tooltip("The scheduling method to test.")]
        [SerializeField]
        private BenchmarkMode _mode = BenchmarkMode.WithDiagonals;

        [Tooltip("The type of voxel data to use for the mesh generation.")]
        [SerializeField]
        private ChunkDataType _dataType = ChunkDataType.Checkerboard;

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

        private World _world;
        private bool _isBenchmarking = false;

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
                Debug.LogError("MeshGenerationBenchmark requires a World instance in the scene!", this);
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
            Debug.Log($"--- Starting Mesh Generation Benchmark ---");
            Debug.Log($"Mode: {_mode} | Data: {_dataType} | Chunks: {_chunksToMesh} | Blocking Wait: {_useBlockingWait}");

            // --- Setup ---
            var jobHandles = new NativeArray<JobHandle>(_chunksToMesh, Allocator.Persistent);

            // Lists to track all allocated memory
            var inputDataToDispose = new List<BenchmarkVoxelData>(_chunksToMesh);
            var outputDataToDispose = new List<MeshDataJobOutput>(_chunksToMesh);

            var stopwatch = new Stopwatch();

            // Generate the source data ONCE. All jobs will read from this same data
            // to ensure we are only measuring meshing overhead, not data generation.
            BenchmarkVoxelData benchmarkData = GenerateBenchmarkData(_dataType, Allocator.Persistent);

            try
            {
                // --- Scheduling Phase ---
                stopwatch.Start();

                for (int i = 0; i < _chunksToMesh; i++)
                {
                    // For each job, create its input data with Allocator.Persistent
                    var jobInputData = new BenchmarkVoxelData(Allocator.Persistent);
                    jobInputData.CopyFrom(benchmarkData);
                    inputDataToDispose.Add(jobInputData); // Track it for cleanup

                    // Schedule the job
                    var jobInfo = ScheduleBenchmarkMeshing(jobInputData);
                    jobHandles[i] = jobInfo.handle;
                    outputDataToDispose.Add(jobInfo.output); // Track output for cleanup
                }

                Debug.Log($"Scheduled all {_chunksToMesh} jobs. Waiting for completion...");

                // --- Completion Phase ---
                var combinedHandle = JobHandle.CombineDependencies(jobHandles);

                if (_useBlockingWait)
                {
                    combinedHandle.Complete();
                }
                else
                {
                    while (!combinedHandle.IsCompleted)
                    {
                        yield return null;
                    }

                    combinedHandle.Complete();
                }

                // At this point, all jobs are 100% finished. It is now safe to handle their output data.
                stopwatch.Stop();

                // --- Results ---
                long totalMilliseconds = stopwatch.ElapsedMilliseconds;
                float avgTime = (float)totalMilliseconds / _chunksToMesh;

                Debug.Log($"<color=lime>--- Benchmark Complete ---</color>");
                Debug.Log($"<b>Total Time for {_chunksToMesh} chunks: {totalMilliseconds} ms</b>");
                Debug.Log($"Average Time per Chunk Mesh: {avgTime:F3} ms");
            }
            finally
            {
                // --- Cleanup ---
                // The 'finally' block is now only responsible for cleaning up collections that
                // were created at the start of the method, regardless of success or failure.
                Debug.Log("Cleaning up benchmark data...");
                if (jobHandles.IsCreated) jobHandles.Dispose();

                // Dispose of the original persistent source data.
                benchmarkData.Dispose();

                // Dispose of all tracked input and output data
                foreach (var data in inputDataToDispose)
                {
                    data.Dispose();
                }

                foreach (var data in outputDataToDispose)
                {
                    data.Dispose();
                }

                _isBenchmarking = false;
            }
        }

        /// <summary>
        /// Schedules a single MeshGenerationJob and returns its handle and data.
        /// </summary>
        private (JobHandle handle, MeshDataJobOutput output) ScheduleBenchmarkMeshing(BenchmarkVoxelData data)
        {
            var meshOutput = new MeshDataJobOutput(Allocator.Persistent);

            // Create an empty array for the unused diagonal slots if needed.
            // We must dispose of this ourselves now.
            var emptyArray = _mode == BenchmarkMode.CardinalsOnly
                ? new NativeArray<uint>(0, Allocator.TempJob)
                : default;

            var job = new MeshGenerationJob
            {
                Map = data.Center, // Pass the Center map
                BlockTypes = _world.JobDataManager.BlockTypesJobData,
                // Pass cardinal neighbors in all modes
                NeighborBack = data.Back,
                NeighborFront = data.Front,
                NeighborLeft = data.Left,
                NeighborRight = data.Right,
                // Conditionally pass diagonal neighbors
                NeighborFrontRight = _mode == BenchmarkMode.WithDiagonals ? data.FrontRight : emptyArray,
                NeighborBackRight = _mode == BenchmarkMode.WithDiagonals ? data.BackRight : emptyArray,
                NeighborBackLeft = _mode == BenchmarkMode.WithDiagonals ? data.BackLeft : emptyArray,
                NeighborFrontLeft = _mode == BenchmarkMode.WithDiagonals ? data.FrontLeft : emptyArray,
                // Pass other required data
                CustomMeshes = _world.JobDataManager.CustomMeshesJobData,
                CustomFaces = _world.JobDataManager.CustomFacesJobData,
                CustomVerts = _world.JobDataManager.CustomVertsJobData,
                CustomTris = _world.JobDataManager.CustomTrisJobData,
                WaterVertexTemplates = _world.FluidVertexTemplates.WaterVertexTemplates,
                LavaVertexTemplates = _world.FluidVertexTemplates.LavaVertexTemplates,
                Output = meshOutput,
            };

            // Schedule the job and, if we created an empty array, schedule its disposal dependent on the job.
            JobHandle handle = job.Schedule();
            if (emptyArray.IsCreated)
            {
                handle = emptyArray.Dispose(handle);
            }

            return (handle, meshOutput);
        }

        /// <summary>
        /// Generates a set of 9 chunk maps with a specific pattern for testing.
        /// </summary>
        private BenchmarkVoxelData GenerateBenchmarkData(ChunkDataType type, Allocator allocator)
        {
            var data = new BenchmarkVoxelData(allocator);
            byte stoneId = 1;
            byte airId = 0;

            for (int i = 0; i < data.Center.Length; i++)
            {
                byte idToPlace = airId;
                if (type == ChunkDataType.Solid)
                {
                    idToPlace = stoneId;
                }
                else if (type == ChunkDataType.Checkerboard)
                {
                    int x = i % VoxelData.ChunkWidth;
                    int y = (i / VoxelData.ChunkWidth) % VoxelData.ChunkHeight;
                    int z = i / (VoxelData.ChunkWidth * VoxelData.ChunkHeight);
                    idToPlace = (x + y + z) % 2 == 0 ? stoneId : airId;
                }

                uint packed = BurstVoxelDataBitMapping.PackVoxelData(idToPlace, 15, 0, 1, 0);

                // Fill all 9 maps with the same data for a consistent test
                data.Center[i] = packed;
                data.Back[i] = packed;
                data.Front[i] = packed;
                data.Left[i] = packed;
                data.Right[i] = packed;
                data.FrontRight[i] = packed;
                data.BackRight[i] = packed;
                data.BackLeft[i] = packed;
                data.FrontLeft[i] = packed;
            }

            return data;
        }

        /// <summary>
        /// Helper struct to hold the 9 NativeArrays for benchmark data.
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

            // New method to copy data from one struct to another
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
    }
}