using System;
using System.Diagnostics;
using Benchmarks;
using Data;
using Data.JobData;
using Data.NativeData;
using Helpers;
using Jobs;
using Jobs.BurstData;
using Jobs.Data;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace Config
{
    /// <summary>
    /// Headless, run-once micro-benchmark that times the real production meshing and lighting jobs on a
    /// fixed, representative voxel pattern, so <see cref="DeviceCalibration"/> can derive the per-frame
    /// throughput budgets from how fast this device actually drains the pipeline (OM-1).
    /// <para>The mesh leg goes through the shared <see cref="IsolatedJobProbe"/> (same job wiring as the
    /// meshing benchmark). The lighting leg is deliberately self-contained — it stands up a minimal flat
    /// sunlit scenario itself rather than coupling to the lighting benchmark's scenario machinery (a
    /// small, intentional duplication that keeps the lighting regression guard untouched).</para>
    /// <para>Each leg runs warmup iterations (absorbing first-run Burst compilation) then takes the
    /// median over several timed iterations on a deterministic pattern, so the result is stable.</para>
    /// </summary>
    public static class StartupCalibrationProbe
    {
        /// <summary>Iterations run and discarded before timing, to absorb Burst compilation / cold caches.</summary>
        private const int WARMUP_ITERATIONS = 2;

        /// <summary>Timed iterations; the reported cost is the median over these (odd count for a clean median).</summary>
        private const int MEASURE_ITERATIONS = 7;

        /// <summary>Surface height of the representative terrain (solid below, air above) — a realistic exposed face layer.</summary>
        private const int SURFACE_HEIGHT = VoxelData.ChunkHeight / 2;

        /// <summary>Number of voxels in one full chunk map.</summary>
        private const int MAP_LENGTH = VoxelData.ChunkWidth * VoxelData.ChunkHeight * VoxelData.ChunkWidth;

        /// <summary>Number of columns in one chunk heightmap.</summary>
        private const int HEIGHTMAP_LENGTH = VoxelData.ChunkWidth * VoxelData.ChunkWidth;

        /// <summary>
        /// Measures the device's per-chunk mesh and lighting job cost.
        /// </summary>
        /// <param name="jobData">Injected block-type / custom-mesh native job data.</param>
        /// <param name="fluidTemplates">Injected water/lava vertex templates.</param>
        /// <returns>The median mesh and lighting job times in milliseconds.</returns>
        public static (double meshMs, double lightMs) Measure(
            JobDataManager jobData, FluidVertexTemplatesNativeData fluidTemplates)
        {
            double meshMs = MeasureMesh(jobData, fluidTemplates);
            double lightMs = MeasureLighting(jobData);
            return (meshMs, lightMs);
        }

        #region Mesh leg (shared IsolatedJobProbe)

        private static double MeasureMesh(JobDataManager jobData, FluidVertexTemplatesNativeData fluidTemplates)
        {
            // 9 voxel maps (center + 8 neighbors) all carrying the same representative terrain, plus 9
            // zero-filled light maps so the schedule passes editor job-safety (all containers constructed).
            NativeArray<uint>[] maps = new NativeArray<uint>[9];
            for (int m = 0; m < maps.Length; m++)
            {
                maps[m] = new NativeArray<uint>(MAP_LENGTH, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                FillRepresentativeTerrain(maps[m]);
            }

            NativeArray<ushort>[] lightMaps = new NativeArray<ushort>[9];
            for (int m = 0; m < lightMaps.Length; m++)
            {
                lightMaps[m] = new NativeArray<ushort>(MAP_LENGTH, Allocator.Persistent); // zero-initialized
            }

            MeshProbeInput input = new MeshProbeInput
            {
                Center = maps[0],
                Back = maps[1], Front = maps[2], Left = maps[3], Right = maps[4],
                FrontRight = maps[5], BackRight = maps[6], BackLeft = maps[7], FrontLeft = maps[8],
                LightCenter = lightMaps[0],
                LightBack = lightMaps[1], LightFront = lightMaps[2], LightLeft = lightMaps[3], LightRight = lightMaps[4],
                LightFrontRight = lightMaps[5], LightBackRight = lightMaps[6], LightBackLeft = lightMaps[7], LightFrontLeft = lightMaps[8],
                IncludeDiagonals = true,
            };

            try
            {
                for (int i = 0; i < WARMUP_ITERATIONS; i++)
                {
                    (JobHandle handle, MeshDataJobOutput output) = IsolatedJobProbe.ScheduleMesh(input, jobData, fluidTemplates);
                    handle.Complete();
                    output.Dispose();
                }

                double[] samples = new double[MEASURE_ITERATIONS];
                Stopwatch sw = new Stopwatch();
                for (int i = 0; i < MEASURE_ITERATIONS; i++)
                {
                    sw.Restart();
                    (JobHandle handle, MeshDataJobOutput output) = IsolatedJobProbe.ScheduleMesh(input, jobData, fluidTemplates);
                    handle.Complete();
                    sw.Stop();
                    output.Dispose();
                    samples[i] = sw.Elapsed.TotalMilliseconds;
                }

                return Median(samples);
            }
            finally
            {
                foreach (NativeArray<uint> map in maps)
                {
                    if (map.IsCreated) map.Dispose();
                }

                foreach (NativeArray<ushort> lightMap in lightMaps)
                {
                    if (lightMap.IsCreated) lightMap.Dispose();
                }
            }
        }

        #endregion

        #region Lighting leg (self-contained)

        private static double MeasureLighting(JobDataManager jobData)
        {
            // Persistent source maps — gather sources are read-only to the job, so they are reused across
            // iterations. The consumable per-iteration containers (padded volumes, queues) are fresh each run.
            NativeArray<uint> center = NewTerrainVoxelMap();
            NativeArray<uint> nN = NewTerrainVoxelMap(), nE = NewTerrainVoxelMap(), nS = NewTerrainVoxelMap(), nW = NewTerrainVoxelMap();
            NativeArray<uint> nNE = NewTerrainVoxelMap(), nSE = NewTerrainVoxelMap(), nSW = NewTerrainVoxelMap(), nNW = NewTerrainVoxelMap();
            NativeArray<ushort> centerLight = NewLightMap();
            NativeArray<ushort> lN = NewLightMap(), lE = NewLightMap(), lS = NewLightMap(), lW = NewLightMap();
            NativeArray<ushort> lNE = NewLightMap(), lSE = NewLightMap(), lSW = NewLightMap(), lNW = NewLightMap();
            NativeArray<ushort> heightmap = new NativeArray<ushort>(HEIGHTMAP_LENGTH, Allocator.Persistent);
            for (int i = 0; i < heightmap.Length; i++) heightmap[i] = SURFACE_HEIGHT;

            NeighborMapSet neighbors = new NeighborMapSet
            {
                NeighborN = nN, NeighborE = nE, NeighborS = nS, NeighborW = nW,
                NeighborNE = nNE, NeighborSE = nSE, NeighborSW = nSW, NeighborNW = nNW,
                LightN = lN, LightE = lE, LightS = lS, LightW = lW,
                LightNE = lNE, LightSE = lSE, LightSW = lSW, LightNW = lNW,
            };

            try
            {
                double[] samples = new double[MEASURE_ITERATIONS];
                Stopwatch sw = new Stopwatch();

                for (int i = -WARMUP_ITERATIONS; i < MEASURE_ITERATIONS; i++)
                {
                    // Fresh per-iteration consumables — the job drains the recalc queue and writes the padded volumes.
                    NativeArray<uint> paddedVoxels = new NativeArray<uint>(ChunkMath.PADDED_LIGHTING_VOLUME, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                    NativeArray<ushort> paddedLight = new NativeArray<ushort>(ChunkMath.PADDED_LIGHTING_VOLUME, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                    NativeQueue<LightQueueNode> sunQueue = new NativeQueue<LightQueueNode>(Allocator.Persistent);
                    NativeQueue<LightQueueNode> blockQueue = new NativeQueue<LightQueueNode>(Allocator.Persistent);
                    NativeQueue<Vector2Int> recalcQueue = new NativeQueue<Vector2Int>(Allocator.Persistent);
                    NativeList<LightModification> mods = new NativeList<LightModification>(Allocator.Persistent);
                    NativeArray<bool> isStable = new NativeArray<bool>(1, Allocator.Persistent);

                    for (int x = 0; x < VoxelData.ChunkWidth; x++)
                    for (int z = 0; z < VoxelData.ChunkWidth; z++)
                        recalcQueue.Enqueue(new Vector2Int(x, z));

                    NeighborhoodLightingJob job = new NeighborhoodLightingJob
                    {
                        PaddedVoxels = paddedVoxels,
                        PaddedLight = paddedLight,
                        ChunkPosition = new Vector2Int(0, 0),
                        SunlightBfsQueue = sunQueue,
                        BlocklightBfsQueue = blockQueue,
                        SunlightColumnRecalcQueue = recalcQueue,
                        Heightmap = heightmap,
                        BlockTypes = jobData.BlockTypesJobData,
                        CrossChunkLightMods = mods,
                        IsStable = isStable,
                        PerformEdgeCheck = false,
                    };
                    job.SetGatherSources(neighbors, center, centerLight);

                    sw.Restart();
                    JobHandle handle = job.Schedule();
                    handle.Complete();
                    sw.Stop();

                    if (i >= 0) samples[i] = sw.Elapsed.TotalMilliseconds;

                    paddedVoxels.Dispose();
                    paddedLight.Dispose();
                    sunQueue.Dispose();
                    blockQueue.Dispose();
                    recalcQueue.Dispose();
                    mods.Dispose();
                    isStable.Dispose();
                }

                return Median(samples);
            }
            finally
            {
                center.Dispose();
                nN.Dispose();
                nE.Dispose();
                nS.Dispose();
                nW.Dispose();
                nNE.Dispose();
                nSE.Dispose();
                nSW.Dispose();
                nNW.Dispose();
                centerLight.Dispose();
                lN.Dispose();
                lE.Dispose();
                lS.Dispose();
                lW.Dispose();
                lNE.Dispose();
                lSE.Dispose();
                lSW.Dispose();
                lNW.Dispose();
                heightmap.Dispose();
            }
        }

        private static NativeArray<uint> NewTerrainVoxelMap()
        {
            NativeArray<uint> map = new NativeArray<uint>(MAP_LENGTH, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            FillRepresentativeTerrain(map);
            return map;
        }

        private static NativeArray<ushort> NewLightMap() =>
            new NativeArray<ushort>(MAP_LENGTH, Allocator.Persistent); // zero-initialized

        #endregion

        #region Helpers

        /// <summary>Fills a chunk map with solid stone below <see cref="SURFACE_HEIGHT"/> and air above.</summary>
        private static void FillRepresentativeTerrain(NativeArray<uint> map)
        {
            uint solid = BurstVoxelDataBitMapping.PackVoxelData(BlockIDs.Stone, 0);
            uint air = BurstVoxelDataBitMapping.PackVoxelData(BlockIDs.Air, 0);

            for (int y = 0; y < VoxelData.ChunkHeight; y++)
            {
                uint val = y <= SURFACE_HEIGHT ? solid : air;
                for (int z = 0; z < VoxelData.ChunkWidth; z++)
                for (int x = 0; x < VoxelData.ChunkWidth; x++)
                    map[ChunkMath.GetFlattenedIndexInChunk(x, y, z)] = val;
            }
        }

        /// <summary>Returns the median of the samples (does not mutate the input).</summary>
        private static double Median(double[] samples)
        {
            double[] sorted = (double[])samples.Clone();
            Array.Sort(sorted);
            return sorted[sorted.Length / 2];
        }

        #endregion
    }
}
