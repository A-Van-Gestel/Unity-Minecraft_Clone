using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using Data;
using Data.WorldTypes;
using Diagnostics;
using Editor.DataGeneration;
using Editor.WorldTools.Libraries;
using Jobs.Data;
using UnityEditor;
using Debug = UnityEngine.Debug;

namespace Editor.Benchmarking
{
    /// <summary>
    /// Editor micro-benchmark of <see cref="PerfStore"/>'s per-frame overhead: a probe pair (slot-less and
    /// slotted) with slots inactive and active, <see cref="PerfStore.CommitFrame"/> at Basic, at Frame (hitch
    /// detector) and with slot and counter columns, <see cref="PerfStore.SampleFrameTiming"/>, the <c>World.Update</c>
    /// remainder bracket, a frame's counter samples, a whole Systems frame of probes, bracket, counters and commit, and one
    /// timed job's main-thread bookkeeping. Reports nanoseconds per call, and the GC collections and heap growth over the
    /// timed calls — an allocation-free path shows neither. The Editor compiles in the Profiler, so the slotted active pair
    /// includes a <c>ProfilerMarker</c> begin/end that Master players compile out — an upper bound for them.
    /// Resets the store before and after, so it discards the session's history ring.
    /// Run from <b>Minecraft Clone → Benchmarks → PerfStore Overhead</b>; the worker-side cost of timing a generation
    /// chain from <b>Minecraft Clone → Benchmarks → Job Timing Overhead</b>; a Capture session's cost and allocation from
    /// <b>Minecraft Clone → Benchmarks → Capture Export Overhead</b>.
    /// </summary>
    public static class PerfStoreProbeBenchmark
    {
        private const int ITERATIONS = 1_000_000;
        private const int WARMUP_ITERATIONS = 10_000;
        private const double NANOSECONDS_PER_MILLISECOND = 1e6;
        private const long BYTES_PER_KILOBYTE = 1024;

        /// <summary>A short, constant frame: the steady state, in which the hitch detector never opens a window.</summary>
        private const long STEADY_FRAME_TICKS = 1;

        /// <summary>Probe pairs in a Systems frame: the World slots plus about one per system outside it, rounded up.</summary>
        private const int PROBE_PAIRS_PER_FRAME = 40;

        /// <summary>A running total that grows every call, so each sample takes the adding path.</summary>
        private const int TOTAL_GROWTH = 1;

        private const string WORLD_TYPE_PATH = "Assets/Data/WorldGen/WorldTypes/Standard.asset";
        private const int GENERATION_SEED = 4242;
        private const int GENERATION_CHUNKS = 100;
        private const int GENERATION_GRID_WIDTH = 10;
        private const int GENERATION_ROUNDS = 5;
        private const double MICROSECONDS_PER_MILLISECOND = 1e3;
        private const double MILLISECONDS_PER_SECOND = 1e3;

        /// <summary>Rows in the measured Capture session — about 80 MB of CSV, deleted afterward.</summary>
        private const int EXPORT_ROWS = 200_000;

        private const int EXPORT_WARMUP_ROWS = 4096;
        private const long EXPORT_SESSION_BYTES = 1L << 32;

        /// <summary>Rows per block the exporter hands its writer (its pool's block size).</summary>
        private const int EXPORT_BLOCK_ROWS = 256;

        /// <summary>How far commits may run ahead of the writer: half its block pool, so no row is dropped.</summary>
        private const int EXPORT_MAX_LAG_ROWS = EXPORT_BLOCK_ROWS * 8;

        /// <summary>Slot and frame times with several digits, so formatting takes its full path.</summary>
        private const long EXPORT_SLOT_TICKS = 12_345;

        private const long EXPORT_WALL_TICKS = 41_667;
        private const int CONTROL_BYTES = 16;

        /// <summary>Runs every measurement and logs one line per case.</summary>
        [MenuItem("Minecraft Clone/Benchmarks/PerfStore Overhead")]
        public static void RunBenchmark()
        {
            PerfTier tier = PerfStore.Tier;
            bool forced = PerfStore.ForceSlots;
            float hitchMinMs = PerfStore.HitchMinMs;
            float hitchMedianFactor = PerfStore.HitchMedianFactor;
            try
            {
                ResetStore();
                Report("Slot-less pair, inactive (Basic)", MeasureSlotlessPair);
                Report("Slotted pair, inactive (Basic)", MeasureSlottedPair);
                Report("World bracket, inactive (Basic)", MeasureWorldBracket);
                Report("CommitFrame, Basic (no slot columns)", MeasureCommit);

                PerfStore.SetTier(PerfTier.Frame);
                Report("CommitFrame, Frame (hitch detector)", MeasureCommit);
                Report("SampleFrameTiming, Frame", MeasureSampleFrameTiming);

                PerfStore.SetTier(PerfTier.Systems);
                Report("Slot-less pair, active (Systems)", MeasureSlotlessPair);
                Report("Slotted pair, active (Systems, with marker)", MeasureSlottedPair);
                Report("CommitFrame, Systems (slot and counter columns)", MeasureCommit);
                Report("World bracket, active (Systems)", MeasureWorldBracket);
                Report("Counter samples, one frame's worth (Systems)", MeasureCounterSamples);
                Report($"Whole Systems frame ({PROBE_PAIRS_PER_FRAME} pairs + bracket + counters + commit)", MeasureSystemsFrame);
                using (PerfJobTimingPool pool = new PerfJobTimingPool())
                    Report("Job timing bookkeeping, one job (Systems, replica of WorldJobManager's)", n => MeasureJobBookkeeping(pool, n));
            }
            finally
            {
                ResetStore();
                PerfStore.SetHitchThresholds(hitchMinMs, hitchMedianFactor);
                PerfStore.SetTier(tier);
                PerfStore.ForceSlots = forced;
            }
        }

        /// <summary>
        /// Generates the same chunks with the busy-time timer and without it, interleaved per round so drift hits both
        /// arms alike, and logs each arm's best round in milliseconds per chunk. Prices the timer's worker-side cost —
        /// every terrain column times itself — on the production generation chain.
        /// </summary>
        [MenuItem("Minecraft Clone/Benchmarks/Job Timing Overhead")]
        public static void RunJobTimingBenchmark()
        {
            WorldTypeDefinition worldType = AssetDatabase.LoadAssetAtPath<WorldTypeDefinition>(WORLD_TYPE_PATH);
            BlockDatabase database = EditorBlockDatabaseCache.Database;
            if (worldType == null || database == null)
            {
                Debug.LogError($"[BENCHMARK] Job timing: could not load {WORLD_TYPE_PATH} or the block database.");
                return;
            }

            EditorChunkPipelineRunner runner = new EditorChunkPipelineRunner();
            using PerfJobTimingPool pool = new PerfJobTimingPool();
            try
            {
                runner.Initialize(GENERATION_SEED, worldType, database);
                GenerateBatch(runner, pool, timed: true); // Burst compiles each job on first schedule; keep that out of both arms.

                double bestUntimed = double.MaxValue;
                double bestTimed = double.MaxValue;
                for (int round = 0; round < GENERATION_ROUNDS; round++)
                {
                    bestUntimed = Math.Min(bestUntimed, GenerateBatch(runner, pool, timed: false));
                    bestTimed = Math.Min(bestTimed, GenerateBatch(runner, pool, timed: true));
                }

                Debug.Log($"[BENCHMARK] Job timing, generation chain (best of {GENERATION_ROUNDS} rounds × {GENERATION_CHUNKS} chunks): " +
                          $"untimed {bestUntimed:F3} ms/chunk, timed {bestTimed:F3} ms/chunk, " +
                          $"difference {(bestTimed - bestUntimed) * MICROSECONDS_PER_MILLISECOND:F1} µs/chunk");
            }
            finally
            {
                runner.Dispose();
            }
        }

        /// <summary>
        /// Writes a Capture session of <see cref="EXPORT_ROWS"/> frames, every slot and counter set, to a temporary folder,
        /// and logs the main thread's commit cost, the writer's rows per second, and the GC collections and heap growth over
        /// the session — the writer formats on its own thread, but its garbage would land in the process-wide figures. A
        /// control arm allocating 16 B per frame on the main thread shows what an allocating path looks like. Commits are
        /// paced so the writer keeps up and no row is dropped.
        /// Run from <b>Minecraft Clone → Benchmarks → Capture Export Overhead</b>.
        /// </summary>
        [MenuItem("Minecraft Clone/Benchmarks/Capture Export Overhead")]
        public static void RunCaptureExportBenchmark()
        {
            PerfTier tier = PerfStore.Tier;
            bool forced = PerfStore.ForceSlots;
            float hitchMinMs = PerfStore.HitchMinMs;
            float hitchMedianFactor = PerfStore.HitchMedianFactor;
            try
            {
                ReportExport("Capture session", allocateControl: false);
                ReportExport("Capture session + 16 B/frame control", allocateControl: true);
            }
            finally
            {
                ResetStore();
                PerfStore.SetHitchThresholds(hitchMinMs, hitchMedianFactor);
                PerfStore.SetTier(tier);
                PerfStore.ForceSlots = forced;
            }
        }

        private static void ReportExport(string label, bool allocateControl)
        {
            string directory = Path.Combine(Path.GetTempPath(), "PerfExportBenchmark_" + Guid.NewGuid().ToString("N"));
            try
            {
                ResetStore();
                PerfStore.SetTier(PerfTier.Capture);
                PerfStore.ConfigureExport(directory, EXPORT_SESSION_BYTES, EXPORT_SESSION_BYTES);
                PerfSessionExporter exporter = PerfStore.Exporter;

                // The warmup opens the file and JIT-compiles both threads' paths, outside the measured window.
                int frame = CommitCaptureFrames(exporter, 0, EXPORT_WARMUP_ROWS, false, out _);
                WaitForWriter(exporter, frame - PerfStore.RowFinalAge);

                int collectionsBefore = GC.CollectionCount(0);
                long heapBefore = GC.GetTotalMemory(false);
                Stopwatch stopwatch = Stopwatch.StartNew();
                frame = CommitCaptureFrames(exporter, frame, EXPORT_ROWS, allocateControl, out long commitTicks);
                WaitForWriter(exporter, frame - PerfStore.RowFinalAge);
                stopwatch.Stop();
                long heapDeltaKb = (GC.GetTotalMemory(false) - heapBefore) / BYTES_PER_KILOBYTE;
                int collections = GC.CollectionCount(0) - collectionsBefore;

                double commitNs = commitTicks * (NANOSECONDS_PER_MILLISECOND * MILLISECONDS_PER_SECOND / Stopwatch.Frequency) / EXPORT_ROWS;
                double rowsPerSecond = EXPORT_ROWS / stopwatch.Elapsed.TotalSeconds;
                long megabytes = exporter.BytesWritten / PerfSessionExporter.BytesPerMegabyte;
                PerfStore.ConfigureExport(null, 0, 0);
                Debug.Log($"[BENCHMARK] {label}: CommitFrame {commitNs:F0} ns/frame, writer {rowsPerSecond:N0} rows/s, " +
                          $"{collections} GC collections, heap Δ {heapDeltaKb} KB over {EXPORT_ROWS} rows " +
                          $"({megabytes} MB written, {exporter.RowsDropped} dropped)");
            }
            finally
            {
                PerfStore.ConfigureExport(null, 0, 0);
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        /// <summary>
        /// Commits frames with every slot and counter set, waiting whenever the writer falls more than a block pool behind.
        /// </summary>
        /// <returns>The frame index after the last one committed.</returns>
        private static int CommitCaptureFrames(PerfSessionExporter exporter, int firstFrame, int count, bool allocateControl,
            out long commitTicks)
        {
            commitTicks = 0;
            long[] slotTicks = (long[])typeof(PerfStore).GetField("s_slotTicks", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)
                               ?? throw new MissingFieldException(nameof(PerfStore), "s_slotTicks");
            for (int frame = firstFrame; frame < firstFrame + count; frame++)
            {
                for (int slot = 0; slot < PerfStore.SlotCount; slot++)
                    slotTicks[slot] = EXPORT_SLOT_TICKS + frame + slot;
                SampleCounters(frame);
                byte[] control = allocateControl ? new byte[CONTROL_BYTES] : null;
                GC.KeepAlive(control);

                long start = Stopwatch.GetTimestamp();
                PerfStore.CommitFrame(new PerfFrameReadings { WallTicks = EXPORT_WALL_TICKS + frame, CpuTicks = EXPORT_WALL_TICKS, FrameIndex = frame });
                commitTicks += Stopwatch.GetTimestamp() - start;

                if (frame % EXPORT_BLOCK_ROWS == 0) WaitForWriter(exporter, frame - EXPORT_MAX_LAG_ROWS);
            }

            return firstFrame + count;
        }

        /// <summary>
        /// Waits until the writer has written the whole blocks among the first <paramref name="rows"/> rows — a partial block
        /// is not handed over until it fills — sleeping between checks, which allocates nothing.
        /// </summary>
        private static void WaitForWriter(PerfSessionExporter exporter, long rows)
        {
            long target = rows / EXPORT_BLOCK_ROWS * EXPORT_BLOCK_ROWS;
            while (exporter.RowsWritten + exporter.RowsDropped < target)
                Thread.Sleep(1);
        }

        /// <summary>Generates <see cref="GENERATION_CHUNKS"/> chunks one at a time, each completed before the next.</summary>
        /// <returns>Milliseconds per chunk.</returns>
        private static double GenerateBatch(EditorChunkPipelineRunner runner, PerfJobTimingPool pool, bool timed)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            for (int c = 0; c < GENERATION_CHUNKS; c++)
            {
                JobBusyTimer timer = default;
                int record = timed ? pool.Rent(out timer) : 0;
                GenerationJobData data = runner.ScheduleGeneration(new ChunkCoord(c % GENERATION_GRID_WIDTH, c / GENERATION_GRID_WIDTH), timer);
                data.Handle.Complete();
                data.Dispose();
                pool.Return(record);
            }

            return stopwatch.Elapsed.TotalMilliseconds / GENERATION_CHUNKS;
        }

        /// <summary>
        /// The main thread's per-job cost at Systems, replicated from <c>WorldJobManager.BeginJobTiming</c> /
        /// <c>EndJobTiming</c>: two timestamps, a record rented, read and returned, and the job sample held.
        /// </summary>
        private static void MeasureJobBookkeeping(PerfJobTimingPool pool, int iterations)
        {
            for (int i = 0; i < iterations; i++)
            {
                long scheduled = Stopwatch.GetTimestamp();
                int record = pool.Rent(out JobBusyTimer _);
                long latency = Stopwatch.GetTimestamp() - scheduled;
                pool.Read(record, out long busy, out long links);
                pool.Return(record);
                PerfStore.RecordJob(PerfJobType.Meshing, latency, busy, links > 0);
            }
        }

        private static void MeasureSlotlessPair(int iterations)
        {
            for (int i = 0; i < iterations; i++)
                PerfStore.Accumulate(PerfSlot.Tick, PerfStore.Begin());
        }

        private static void MeasureSlottedPair(int iterations)
        {
            for (int i = 0; i < iterations; i++)
                PerfStore.End(PerfSlot.Tick, PerfStore.Begin(PerfSlot.Tick));
        }

        private static void MeasureCommit(int iterations)
        {
            for (int i = 0; i < iterations; i++)
                PerfStore.CommitFrame(new PerfFrameReadings { WallTicks = STEADY_FRAME_TICKS, CpuTicks = STEADY_FRAME_TICKS, FrameIndex = i });
        }

        private static void MeasureWorldBracket(int iterations)
        {
            for (int i = 0; i < iterations; i++)
                PerfStore.EndWorldFrame(PerfStore.BeginWorldFrame());
        }

        private static void MeasureCounterSamples(int iterations)
        {
            for (int i = 0; i < iterations; i++)
                SampleCounters(i);
        }

        private static void MeasureSystemsFrame(int iterations)
        {
            for (int i = 0; i < iterations; i++)
            {
                long bracket = PerfStore.BeginWorldFrame();
                for (int pair = 0; pair < PROBE_PAIRS_PER_FRAME; pair++)
                    PerfStore.End(PerfSlot.Tick, PerfStore.Begin(PerfSlot.Tick));
                PerfStore.EndWorldFrame(bracket);
                SampleCounters(i);
                PerfStore.CommitFrame(new PerfFrameReadings { WallTicks = STEADY_FRAME_TICKS, CpuTicks = STEADY_FRAME_TICKS, FrameIndex = i });
            }
        }

        /// <summary>Sets every gauge and samples every running total once: one frame's worth of counter samples.</summary>
        private static void SampleCounters(int frame)
        {
            for (int c = 0; c < (int)PerfStore.FirstPerFrameCount; c++)
                PerfStore.SetGauge((PerfCounter)c, frame);
            for (int c = (int)PerfStore.FirstPerFrameCount; c < PerfStore.CounterCount; c++)
                PerfStore.SampleTotal((PerfCounter)c, (long)frame * TOTAL_GROWTH);
        }

        private static void MeasureSampleFrameTiming(int iterations)
        {
            for (int i = 0; i < iterations; i++)
                PerfStore.SampleFrameTiming();
        }

        private static void Report(string label, Action<int> body)
        {
            body(WARMUP_ITERATIONS);

            // Even 16 B per call would show as ~16 MB of heap growth or a collection over the timed run.
            int collectionsBefore = GC.CollectionCount(0);
            long heapBefore = GC.GetTotalMemory(false);
            Stopwatch stopwatch = Stopwatch.StartNew();
            body(ITERATIONS);
            stopwatch.Stop();
            long heapDeltaKb = (GC.GetTotalMemory(false) - heapBefore) / BYTES_PER_KILOBYTE;
            int collections = GC.CollectionCount(0) - collectionsBefore;

            double nsPerCall = stopwatch.Elapsed.TotalMilliseconds * NANOSECONDS_PER_MILLISECOND / ITERATIONS;
            Debug.Log($"[BENCHMARK] PerfStore {label}: {nsPerCall:F1} ns/call, " +
                      $"{collections} GC collections, heap Δ {heapDeltaKb} KB over {ITERATIONS} calls");
        }

        private static void ResetStore()
        {
            MethodInfo reset = typeof(PerfStore).GetMethod("DomainReset", BindingFlags.NonPublic | BindingFlags.Static)
                               ?? throw new MissingMethodException(nameof(PerfStore), "DomainReset");
            reset.Invoke(null, null);
        }
    }
}
