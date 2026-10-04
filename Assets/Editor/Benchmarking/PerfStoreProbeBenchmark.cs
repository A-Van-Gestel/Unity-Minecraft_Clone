using System;
using System.Diagnostics;
using System.Reflection;
using Diagnostics;
using UnityEditor;
using Debug = UnityEngine.Debug;

namespace Editor.Benchmarking
{
    /// <summary>
    /// Editor micro-benchmark of <see cref="PerfStore"/>'s per-frame overhead: a probe pair (slot-less and
    /// slotted) with slots inactive and active, <see cref="PerfStore.CommitFrame"/> at Basic, at Frame (hitch
    /// detector) and with slot columns, and <see cref="PerfStore.SampleFrameTiming"/>. Reports nanoseconds per call, and the GC collections and heap growth over the timed calls — an
    /// allocation-free path shows neither. The Editor compiles in the Profiler, so the slotted active pair
    /// includes a <c>ProfilerMarker</c> begin/end that Master players compile out — an upper bound for them.
    /// Resets the store before and after, so it discards the session's history ring.
    /// Run from <b>Minecraft Clone → Benchmarks → PerfStore Overhead</b>.
    /// </summary>
    public static class PerfStoreProbeBenchmark
    {
        private const int ITERATIONS = 1_000_000;
        private const int WARMUP_ITERATIONS = 10_000;
        private const double NANOSECONDS_PER_MILLISECOND = 1e6;
        private const long BYTES_PER_KILOBYTE = 1024;

        /// <summary>A short, constant frame: the steady state, in which the hitch detector never opens a window.</summary>
        private const long STEADY_FRAME_TICKS = 1;

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
                Report("CommitFrame, Basic (no slot columns)", MeasureCommit);

                PerfStore.SetTier(PerfTier.Frame);
                Report("CommitFrame, Frame (hitch detector)", MeasureCommit);
                Report("SampleFrameTiming, Frame", MeasureSampleFrameTiming);

                PerfStore.SetTier(PerfTier.Systems);
                Report("Slot-less pair, active (Systems)", MeasureSlotlessPair);
                Report("Slotted pair, active (Systems, with marker)", MeasureSlottedPair);
                Report("CommitFrame, Systems (slot columns)", MeasureCommit);
            }
            finally
            {
                ResetStore();
                PerfStore.SetHitchThresholds(hitchMinMs, hitchMedianFactor);
                PerfStore.SetTier(tier);
                PerfStore.ForceSlots = forced;
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
