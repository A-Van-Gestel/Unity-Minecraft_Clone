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
using Debug = UnityEngine.Debug;

// ReSharper disable HeuristicUnreachableCode
#pragma warning disable CS0162 // Unreachable code detected

namespace Config
{
    /// <summary>
    /// Headless, run-once micro-benchmark that times the real production meshing and lighting jobs on a
    /// fixed, representative voxel pattern, so <see cref="DeviceCalibration"/> can derive the per-frame
    /// throughput budgets from how fast this device actually drains the pipeline (OM-1).
    /// <para>The mesh leg goes through the shared <see cref="IsolatedJobProbe"/> (same job wiring as the
    /// meshing benchmark). The lighting leg is deliberately self-contained — it stands up a minimal flat
    /// skylit scenario itself rather than coupling to the lighting benchmark's scenario machinery (a
    /// small, intentional duplication that keeps the lighting regression guard untouched).</para>
    /// <para>Each leg warms up (absorbing first-run Burst compilation and other one-time costs), then the
    /// two legs take timed batches in alternation on a deterministic pattern. Each leg's anchor is the
    /// <b>minimum of its batch medians</b>: a contention window that covers one batch, or a warmup tail,
    /// cannot make the device read slower than its fastest batch, while a device that is uniformly slow
    /// still reads slow in every batch.</para>
    /// </summary>
    public static class StartupCalibrationProbe
    {
        /// <summary>
        /// Opt-in precision mode for re-anchoring the <see cref="DeviceCalibration"/> reference constants
        /// from a clean capture. When true, the probe repeats the shipping measurement
        /// <see cref="BASELINE_REPETITIONS"/> times and anchors on the median of those repetition anchors, so a
        /// reference is the typical value of the <b>same statistic</b> a shipping launch computes. Leave
        /// <c>false</c> for shipping — the repetitions add startup latency real users must not pay. Flip to
        /// <c>true</c> only to harvest reference values, then revert.
        /// </summary>
        private const bool BASELINE_CALIBRATION = false;

        /// <summary>Minimum warmup iterations per leg, run and discarded before timing (Burst compilation,
        /// cold caches, queue block growth, worker wake-up).</summary>
        private const int WARMUP_ITERATIONS = 8;

        /// <summary>Minimum warmup wall time per leg (ms): a fast device keeps warming past
        /// <see cref="WARMUP_ITERATIONS"/> until this much time has passed.</summary>
        private const double MIN_WARMUP_MS = 20.0;

        /// <summary>Timed batches per leg. The legs alternate batch by batch, so one contention window cannot
        /// land on a single leg; the anchor is the minimum of the batch medians.</summary>
        private const int BATCH_COUNT = 3;

        /// <summary>Timed samples per batch (odd count for a clean median).</summary>
        private const int BATCH_SIZE = 11;

        /// <summary>Repetitions of the whole timed measurement: one when shipping, several in
        /// <see cref="BASELINE_CALIBRATION"/> mode.</summary>
        private const int BASELINE_REPETITIONS = BASELINE_CALIBRATION ? 15 : 1;

        /// <summary>
        /// Whether the repeated-measurement precision capture mode is compiled in. When true, callers should
        /// persist the capture to disk (see <c>DeviceCalibration.WriteBaselineReport</c>) so it can be
        /// harvested off devices whose logs are awkward to read (e.g. Android). See OM1 §3.3 / §5.
        /// </summary>
        internal static bool BaselineCalibrationEnabled => BASELINE_CALIBRATION;

        /// <summary>Surface height of the representative terrain (solid below, air above) — a realistic exposed face layer.</summary>
        private const int SURFACE_HEIGHT = VoxelData.ChunkHeight / 2;

        /// <summary>Number of voxels in one full chunk map.</summary>
        private const int MAP_LENGTH = VoxelData.ChunkWidth * VoxelData.ChunkHeight * VoxelData.ChunkWidth;

        /// <summary>Number of columns in one chunk heightmap.</summary>
        private const int HEIGHTMAP_LENGTH = VoxelData.ChunkWidth * VoxelData.ChunkWidth;

        /// <summary>The timing distribution of one probe leg (mesh or lighting) over one timed measurement.</summary>
        public readonly struct LegStats
        {
            /// <summary>Fastest sample (ms).</summary>
            public readonly double Min;

            /// <summary>Median over every sample of the measurement (ms) — logged for attribution, not the anchor.</summary>
            public readonly double Median;

            /// <summary>Slowest sample (ms).</summary>
            public readonly double Max;

            /// <summary>Arithmetic mean of the samples (ms).</summary>
            public readonly double Mean;

            /// <summary>Population standard deviation of the samples (ms) — the noise-floor indicator.</summary>
            public readonly double Std;

            /// <summary>Number of timed samples the statistics were computed over.</summary>
            public readonly int SampleCount;

            /// <summary>Per-batch medians (ms), in run order.</summary>
            public readonly double[] BatchMedians;

            /// <summary>The leg's throughput anchor (ms): the minimum of <see cref="BatchMedians"/>.</summary>
            public readonly double Anchor;

            /// <summary>Initializes a leg's timing distribution.</summary>
            /// <param name="min">Fastest sample (ms).</param>
            /// <param name="median">Median over every sample (ms).</param>
            /// <param name="max">Slowest sample (ms).</param>
            /// <param name="mean">Arithmetic mean of the samples (ms).</param>
            /// <param name="std">Population standard deviation of the samples (ms).</param>
            /// <param name="sampleCount">Number of timed samples.</param>
            /// <param name="batchMedians">Per-batch medians (ms), in run order.</param>
            /// <param name="anchor">The leg's throughput anchor (ms).</param>
            public LegStats(double min, double median, double max, double mean, double std, int sampleCount,
                double[] batchMedians, double anchor)
            {
                Min = min;
                Median = median;
                Max = max;
                Mean = mean;
                Std = std;
                SampleCount = sampleCount;
                BatchMedians = batchMedians;
                Anchor = anchor;
            }

            /// <inheritdoc/>
            public override string ToString() =>
                $"anchor={Anchor:F3} batch medians=[{FormatMs(BatchMedians)}] min={Min:F3} median={Median:F3} " +
                $"max={Max:F3} mean={Mean:F3} std={Std:F3} ms over {SampleCount} samples";
        }

        /// <summary>The full result of a probe run: the per-leg timing distributions and throughput anchors.</summary>
        public readonly struct ProbeResult
        {
            /// <summary>Mesh-leg timing distribution of the first (when shipping, the only) measurement.</summary>
            public readonly LegStats Mesh;

            /// <summary>Lighting-leg timing distribution of the first (when shipping, the only) measurement.</summary>
            public readonly LegStats Light;

            /// <summary>Every mesh-leg measurement, in run order (one unless <c>BASELINE_CALIBRATION</c>).</summary>
            public readonly LegStats[] MeshRepetitions;

            /// <summary>Every lighting-leg measurement, in run order (one unless <c>BASELINE_CALIBRATION</c>).</summary>
            public readonly LegStats[] LightRepetitions;

            /// <summary>Mesh job time in milliseconds — the throughput anchor for the mesh budget.</summary>
            public readonly double MeshMs;

            /// <summary>Lighting job time in milliseconds — the throughput anchor for the light budget.</summary>
            public readonly double LightMs;

            /// <summary>Initializes a probe result from its per-leg measurements.</summary>
            /// <param name="meshRepetitions">The mesh leg's measurements (at least one).</param>
            /// <param name="lightRepetitions">The lighting leg's measurements (at least one).</param>
            public ProbeResult(LegStats[] meshRepetitions, LegStats[] lightRepetitions)
            {
                MeshRepetitions = meshRepetitions;
                LightRepetitions = lightRepetitions;
                Mesh = meshRepetitions[0];
                Light = lightRepetitions[0];
                MeshMs = ComputeRepetitionAnchor(meshRepetitions);
                LightMs = ComputeRepetitionAnchor(lightRepetitions);
            }
        }

        /// <summary>
        /// Measures the device's per-chunk mesh and lighting job cost.
        /// </summary>
        /// <param name="jobData">Injected block-type / custom-mesh native job data.</param>
        /// <param name="fluidTemplates">Injected water/lava vertex templates.</param>
        /// <returns>The per-leg timing distributions; <see cref="ProbeResult.MeshMs"/> /
        /// <see cref="ProbeResult.LightMs"/> are the throughput anchors.</returns>
        public static ProbeResult Measure(
            JobDataManager jobData, FluidVertexTemplatesNativeData fluidTemplates)
        {
            using MeshLeg mesh = new MeshLeg(jobData, fluidTemplates);
            using LightLeg light = new LightLeg(jobData);

            WarmUp(mesh, light);

            LegStats[] meshRepetitions = new LegStats[BASELINE_REPETITIONS];
            LegStats[] lightRepetitions = new LegStats[BASELINE_REPETITIONS];
            for (int r = 0; r < BASELINE_REPETITIONS; r++)
            {
                MeasureInterleaved(mesh, light, out meshRepetitions[r], out lightRepetitions[r]);
            }

            ProbeResult result = new ProbeResult(meshRepetitions, lightRepetitions);

            // Always logged: a calibration that resolves an unexpected budget must be attributable from the
            // player log alone (one slow batch vs a uniformly slow device).
            Debug.Log($"[StartupCalibrationProbe] mesh: {result.Mesh}.");
            Debug.Log($"[StartupCalibrationProbe] light: {result.Light}.");
            return result;
        }

        /// <summary>
        /// Warms both legs, alternating one iteration each, until every leg has run at least
        /// <see cref="WARMUP_ITERATIONS"/> iterations and <see cref="MIN_WARMUP_MS"/> of timed work.
        /// </summary>
        private static void WarmUp(IProbeLeg mesh, IProbeLeg light)
        {
            int meshIterations = 0, lightIterations = 0;
            double meshMs = 0.0, lightMs = 0.0;

            while (!IsWarm(meshIterations, meshMs) || !IsWarm(lightIterations, lightMs))
            {
                if (!IsWarm(meshIterations, meshMs))
                {
                    meshMs += mesh.Sample();
                    meshIterations++;
                }

                if (!IsWarm(lightIterations, lightMs))
                {
                    lightMs += light.Sample();
                    lightIterations++;
                }
            }
        }

        private static bool IsWarm(int iterations, double elapsedMs) =>
            iterations >= WARMUP_ITERATIONS && elapsedMs >= MIN_WARMUP_MS;

        /// <summary>Runs one timed measurement: <see cref="BATCH_COUNT"/> batches per leg, alternating legs.</summary>
        private static void MeasureInterleaved(IProbeLeg mesh, IProbeLeg light, out LegStats meshStats, out LegStats lightStats)
        {
            double[][] meshBatches = new double[BATCH_COUNT][];
            double[][] lightBatches = new double[BATCH_COUNT][];

            for (int b = 0; b < BATCH_COUNT; b++)
            {
                meshBatches[b] = SampleBatch(mesh);
                lightBatches[b] = SampleBatch(light);
            }

            meshStats = ComputeLegStats(meshBatches);
            lightStats = ComputeLegStats(lightBatches);
        }

        private static double[] SampleBatch(IProbeLeg leg)
        {
            double[] samples = new double[BATCH_SIZE];
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] = leg.Sample();
            }

            return samples;
        }

        /// <summary>One probe leg: fixed source data owned for the whole probe, timed one job at a time.</summary>
        private interface IProbeLeg : IDisposable
        {
            /// <summary>Schedules and completes one job.</summary>
            /// <returns>The timed schedule + complete cost in milliseconds.</returns>
            double Sample();
        }

        #region Mesh leg (shared IsolatedJobProbe)

        /// <summary>The mesh leg, scheduled through the shared <see cref="IsolatedJobProbe"/>.</summary>
        private sealed class MeshLeg : IProbeLeg
        {
            private readonly JobDataManager _jobData;
            private readonly FluidVertexTemplatesNativeData _fluidTemplates;
            private readonly NativeArray<uint>[] _maps = new NativeArray<uint>[9];
            private readonly NativeArray<ushort>[] _lightMaps = new NativeArray<ushort>[9];
            private readonly MeshProbeInput _input;
            private readonly Stopwatch _stopwatch = new Stopwatch();

            /// <summary>Allocates the leg's source maps.</summary>
            /// <param name="jobData">Injected block-type / custom-mesh native job data.</param>
            /// <param name="fluidTemplates">Injected water/lava vertex templates.</param>
            public MeshLeg(JobDataManager jobData, FluidVertexTemplatesNativeData fluidTemplates)
            {
                _jobData = jobData;
                _fluidTemplates = fluidTemplates;

                // 9 voxel maps (center + 8 neighbors) all carrying the same representative terrain, plus 9
                // zero-filled light maps so the schedule passes editor job-safety (all containers constructed).
                for (int m = 0; m < _maps.Length; m++)
                {
                    _maps[m] = new NativeArray<uint>(MAP_LENGTH, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                    FillRepresentativeTerrain(_maps[m]);
                }

                for (int m = 0; m < _lightMaps.Length; m++)
                {
                    _lightMaps[m] = new NativeArray<ushort>(MAP_LENGTH, Allocator.Persistent); // zero-initialized
                }

                _input = new MeshProbeInput
                {
                    Center = _maps[0],
                    NeighborS = _maps[1], NeighborN = _maps[2], NeighborW = _maps[3], NeighborE = _maps[4],
                    NeighborNE = _maps[5], NeighborSE = _maps[6], NeighborSW = _maps[7], NeighborNW = _maps[8],
                    LightCenter = _lightMaps[0],
                    LightS = _lightMaps[1], LightN = _lightMaps[2], LightW = _lightMaps[3], LightE = _lightMaps[4],
                    LightNE = _lightMaps[5], LightSE = _lightMaps[6], LightSW = _lightMaps[7], LightNW = _lightMaps[8],
                    IncludeDiagonals = true,
                };
            }

            /// <inheritdoc/>
            public double Sample()
            {
                _stopwatch.Restart();
                (JobHandle handle, MeshDataJobOutput output) = IsolatedJobProbe.ScheduleMesh(_input, _jobData, _fluidTemplates);
                handle.Complete();
                _stopwatch.Stop();
                output.Dispose();
                return _stopwatch.Elapsed.TotalMilliseconds;
            }

            /// <inheritdoc/>
            public void Dispose()
            {
                foreach (NativeArray<uint> map in _maps)
                {
                    if (map.IsCreated) map.Dispose();
                }

                foreach (NativeArray<ushort> lightMap in _lightMaps)
                {
                    if (lightMap.IsCreated) lightMap.Dispose();
                }
            }
        }

        #endregion

        #region Lighting leg (self-contained)

        /// <summary>The lighting leg: a minimal flat skylit neighborhood, full-height recalc of every column.</summary>
        private sealed class LightLeg : IProbeLeg
        {
            private readonly JobDataManager _jobData;
            private readonly Stopwatch _stopwatch = new Stopwatch();

            // Persistent source maps — gather sources are read-only to the job, so they are reused across
            // iterations. The consumable per-iteration containers (padded volumes, queues) are fresh each run.
            private NativeArray<uint> _center;
            private NativeArray<ushort> _centerLight;
            private NativeArray<ushort> _heightmap;
            private NeighborMapSet _neighbors;

            /// <summary>Allocates the leg's source maps.</summary>
            /// <param name="jobData">Injected block-type native job data.</param>
            public LightLeg(JobDataManager jobData)
            {
                _jobData = jobData;
                _center = NewTerrainVoxelMap();
                _centerLight = NewLightMap();
                _heightmap = new NativeArray<ushort>(HEIGHTMAP_LENGTH, Allocator.Persistent);
                for (int i = 0; i < _heightmap.Length; i++) _heightmap[i] = SURFACE_HEIGHT;

                _neighbors = new NeighborMapSet
                {
                    NeighborN = NewTerrainVoxelMap(), NeighborE = NewTerrainVoxelMap(),
                    NeighborS = NewTerrainVoxelMap(), NeighborW = NewTerrainVoxelMap(),
                    NeighborNE = NewTerrainVoxelMap(), NeighborSE = NewTerrainVoxelMap(),
                    NeighborSW = NewTerrainVoxelMap(), NeighborNW = NewTerrainVoxelMap(),
                    LightN = NewLightMap(), LightE = NewLightMap(), LightS = NewLightMap(), LightW = NewLightMap(),
                    LightNE = NewLightMap(), LightSE = NewLightMap(), LightSW = NewLightMap(), LightNW = NewLightMap(),
                };
            }

            /// <inheritdoc/>
            public double Sample()
            {
                // Fresh per-iteration consumables — the job drains the recalc queue and writes the padded volumes.
                NativeArray<uint> paddedVoxels = new NativeArray<uint>(ChunkMath.PADDED_LIGHTING_VOLUME, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                NativeArray<ushort> paddedLight = new NativeArray<ushort>(ChunkMath.PADDED_LIGHTING_VOLUME, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                NativeQueue<LightQueueNode> skyQueue = new NativeQueue<LightQueueNode>(Allocator.Persistent);
                NativeQueue<LightQueueNode> blockQueue = new NativeQueue<LightQueueNode>(Allocator.Persistent);
                NativeQueue<Vector2Int> recalcQueue = new NativeQueue<Vector2Int>(Allocator.Persistent);
                NativeList<LightModification> mods = new NativeList<LightModification>(Allocator.Persistent);
                NativeList<PullBackClaim> pullBackClaims = new NativeList<PullBackClaim>(Allocator.Persistent);
                NativeArray<bool> isStable = new NativeArray<bool>(1, Allocator.Persistent);

                for (int x = 0; x < VoxelData.ChunkWidth; x++)
                for (int z = 0; z < VoxelData.ChunkWidth; z++)
                    recalcQueue.Enqueue(new Vector2Int(x, z));

                NeighborhoodLightingJob job = new NeighborhoodLightingJob
                {
                    PaddedVoxels = paddedVoxels,
                    PaddedLight = paddedLight,
                    BandHeight = ChunkMath.CHUNK_HEIGHT, // LI-2: the probe calibrates the full-height path
                    BandMinY = 0,
                    ChunkPosition = new Vector2Int(0, 0),
                    SkylightBfsQueue = skyQueue,
                    BlocklightBfsQueue = blockQueue,
                    SkylightColumnRecalcQueue = recalcQueue,
                    Heightmap = _heightmap,
                    BlockTypes = _jobData.BlockTypesJobData,
                    CrossChunkLightMods = mods,
                    PullBackClaims = pullBackClaims,
                    IsStable = isStable,
                    PerformEdgeCheck = false,
                };
                job.SetGatherSources(_neighbors, _center, _centerLight);

                _stopwatch.Restart();
                JobHandle handle = job.Schedule();
                handle.Complete();
                _stopwatch.Stop();

                paddedVoxels.Dispose();
                paddedLight.Dispose();
                skyQueue.Dispose();
                blockQueue.Dispose();
                recalcQueue.Dispose();
                mods.Dispose();
                pullBackClaims.Dispose();
                isStable.Dispose();

                return _stopwatch.Elapsed.TotalMilliseconds;
            }

            /// <inheritdoc/>
            public void Dispose()
            {
                if (_center.IsCreated) _center.Dispose();
                if (_centerLight.IsCreated) _centerLight.Dispose();
                if (_heightmap.IsCreated) _heightmap.Dispose();
                _neighbors.Dispose();
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

        /// <summary>
        /// Computes a leg's timing distribution over one measurement's batches: min / median / max / mean /
        /// std over every sample (the spread that attributes a slow reading), the per-batch medians, and the
        /// anchor (<see cref="ComputeAnchor"/> of those medians). Does not mutate the input.
        /// </summary>
        /// <param name="batches">The timed batches in milliseconds, in run order (at least one, none empty).</param>
        /// <returns>The distribution statistics and anchor for the measurement.</returns>
        public static LegStats ComputeLegStats(double[][] batches)
        {
            if (batches == null || batches.Length == 0)
                throw new ArgumentException("At least one batch is required.", nameof(batches));

            int sampleCount = 0;
            foreach (double[] batch in batches)
            {
                if (batch == null || batch.Length == 0)
                    throw new ArgumentException("Every batch needs at least one sample.", nameof(batches));
                sampleCount += batch.Length;
            }

            double[] all = new double[sampleCount];
            double[] batchMedians = new double[batches.Length];
            int offset = 0;
            for (int b = 0; b < batches.Length; b++)
            {
                Array.Copy(batches[b], 0, all, offset, batches[b].Length);
                offset += batches[b].Length;
                batchMedians[b] = ComputeMedian(batches[b]);
            }

            Array.Sort(all);
            double sum = 0.0;
            foreach (double t in all)
                sum += t;

            double mean = sum / all.Length;

            double sumSq = 0.0;
            foreach (double t in all)
            {
                double d = t - mean;
                sumSq += d * d;
            }

            double std = Math.Sqrt(sumSq / all.Length);

            return new LegStats(all[0], all[all.Length / 2], all[^1], mean, std, all.Length,
                batchMedians, ComputeAnchor(batchMedians));
        }

        /// <summary>The median of <paramref name="samples"/> (the upper middle value for an even count).
        /// Does not mutate the input.</summary>
        /// <param name="samples">The values (at least one).</param>
        /// <returns>The median value.</returns>
        public static double ComputeMedian(double[] samples)
        {
            if (samples == null || samples.Length == 0)
                throw new ArgumentException("At least one sample is required.", nameof(samples));

            double[] sorted = (double[])samples.Clone();
            Array.Sort(sorted);
            return sorted[sorted.Length / 2];
        }

        /// <summary>
        /// The throughput anchor of one measurement: the minimum of its batch medians. Each batch median
        /// already rejects isolated spikes; taking the fastest batch rejects a contention window that
        /// covered a whole batch, so only a device that is slow in every batch reads slow.
        /// </summary>
        /// <param name="batchMedians">The per-batch medians in milliseconds (at least one).</param>
        /// <returns>The anchor in milliseconds.</returns>
        public static double ComputeAnchor(double[] batchMedians)
        {
            if (batchMedians == null || batchMedians.Length == 0)
                throw new ArgumentException("At least one batch median is required.", nameof(batchMedians));

            double min = batchMedians[0];
            for (int i = 1; i < batchMedians.Length; i++)
            {
                if (batchMedians[i] < min) min = batchMedians[i];
            }

            return min;
        }

        /// <summary>
        /// The anchor over repeated measurements: a single measurement's own anchor, or the median of the
        /// repetition anchors (a <c>BASELINE_CALIBRATION</c> capture), which is the typical value of the
        /// shipping statistic on the capturing device.
        /// </summary>
        /// <param name="repetitions">The measurements (at least one).</param>
        /// <returns>The anchor in milliseconds.</returns>
        public static double ComputeRepetitionAnchor(LegStats[] repetitions)
        {
            if (repetitions == null || repetitions.Length == 0)
                throw new ArgumentException("At least one measurement is required.", nameof(repetitions));
            if (repetitions.Length == 1) return repetitions[0].Anchor;

            double[] anchors = new double[repetitions.Length];
            for (int r = 0; r < repetitions.Length; r++)
            {
                anchors[r] = repetitions[r].Anchor;
            }

            return ComputeMedian(anchors);
        }

        /// <summary>Formats millisecond values for a log line (separator avoids decimal-comma locales).</summary>
        private static string FormatMs(double[] values) =>
            values == null ? string.Empty : string.Join(" / ", Array.ConvertAll(values, v => v.ToString("F3")));

        #endregion
    }
}
