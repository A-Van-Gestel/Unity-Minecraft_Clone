using System;
using System.Diagnostics;
using Unity.Profiling.LowLevel.Unsafe;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
#if ENABLE_PROFILER
using Unity.Profiling;
#endif
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Diagnostics
{
    /// <summary>
    /// The engine-wide, zero-allocation performance store: per-slot <see cref="Stopwatch"/> accumulators fed by
    /// begin/end probes, and a <see cref="PerfFrameRing"/> history of raw per-frame timings from which exact
    /// worst-frame and percentile statistics are read.
    /// <para>
    /// <b>Tiers.</b> The frame rows (wall and CPU time, managed allocation and collections) are always recorded.
    /// At <see cref="PerfTier.Frame"/> and above, <see cref="SampleFrameTiming"/> adds GPU, render-thread and
    /// present-wait times to the rows, and a <see cref="PerfHitchDetector"/> keeps the frames around each hitch.
    /// Slot probes record only while <see cref="SlotsActive"/> — at <see cref="PerfTier.Systems"/> and above, or
    /// while <see cref="ForceSlots"/> is set — and otherwise cost one static bool read, with no timestamp taken.
    /// The ring's slot columns exist only at <see cref="PerfTier.Systems"/> and above; a forced run at a lower
    /// tier keeps the accumulators live without allocating them.
    /// </para>
    /// <para>
    /// <b>Allocation.</b> A frame's figure is the managed heap's growth since the previous frame, which includes
    /// worker-thread allocations. A frame in which a collection completed has no usable figure, so it is flagged
    /// (<see cref="PerfGcState.Collected"/>) and left out of allocation statistics rather than recorded as 0.
    /// </para>
    /// <para>
    /// <b>Frame timings</b> arrive a few frames after their frame. Each is matched to its row by timestamp
    /// (<see cref="PerfFrameRing.TryBackfillFrameTiming"/>); a row whose timing never arrives keeps NaN, and so does
    /// the GPU time of a timing that reports none (0).
    /// </para>
    /// <para>
    /// <b>Frame boundary.</b> <see cref="CommitFrame"/>, called once at the end of every frame, stores the
    /// frame and then zeroes every slot accumulator, so a frame in which a probed region never ran records 0
    /// for that slot instead of repeating an earlier frame's value. <see cref="ClearSlots"/> and
    /// <see cref="PublishSlots"/> serve callers that need their own boundary inside the frame.
    /// </para>
    /// <para>
    /// <b>Profiler markers.</b> In builds with the Profiler compiled in, <see cref="Begin(PerfSlot)"/> /
    /// <see cref="End"/> also open and close the slot's <c>ProfilerMarker</c> (named <c>PerfStore.&lt;slot&gt;</c>)
    /// while slots are active, so Unity Profiler captures show the same regions. The slot-less
    /// <see cref="Begin()"/> / <see cref="Accumulate"/> pair cannot open a marker, because a marker must be
    /// opened by name before the region runs.
    /// </para>
    /// <para>
    /// <b>Unattributed remainder.</b> <see cref="BeginWorldFrame"/> / <see cref="EndWorldFrame"/> bracket
    /// <c>World.Update</c> and charge <see cref="PerfSlot.WorldUnattributed"/> with the bracket's time minus the slot
    /// time recorded <i>inside</i> it, so slot time recorded elsewhere in the frame never distorts it. A negative
    /// remainder means two slots overlapped; it is kept as measured and counted in <see cref="NegativeRemainderFrames"/>.
    /// </para>
    /// <para>
    /// <b>Counters.</b> Each <see cref="PerfCounter"/> is stored beside the slot times, in columns that exist with the
    /// slot columns: gauges set with <see cref="SetGauge"/> hold their level until set again, while per-frame counts
    /// added by <see cref="SampleTotal"/> are zeroed by every commit.
    /// </para>
    /// <para>
    /// <b>Job samples.</b> At <see cref="PerfTier.Systems"/> and above, <see cref="RecordJob"/> keeps the latency and busy
    /// time of the newest completed jobs per <see cref="PerfJobType"/> — per job, not per frame — for exact per-job
    /// percentiles.
    /// </para>
    /// <para>Main thread only; <see cref="SlotsActive"/> may also be read from worker threads to gate their counting. Native memory is freed on application quit and before an Editor assembly reload.</para>
    /// </summary>
    public static class PerfStore
    {
        /// <summary>Frames of history the ring retains.</summary>
        public const int RingCapacity = 2048;

        /// <summary>Number of <see cref="PerfSlot"/> values.</summary>
        public const int SlotCount = (int)PerfSlot.Count;

        /// <summary>Number of <see cref="PerfCounter"/> values.</summary>
        public const int CounterCount = (int)PerfCounter.Count;

        /// <summary>The first per-frame count; every <see cref="PerfCounter"/> before it is a gauge.</summary>
        public const PerfCounter FirstPerFrameCount = PerfCounter.SectionPoolMisses;

        /// <summary>Number of <see cref="PerfJobType"/> values.</summary>
        public const int JobTypeCount = (int)PerfJobType.Count;

        /// <summary>Completed jobs per type whose latency and busy time are held for per-job statistics.</summary>
        public const int JobSampleCapacity = 1024;

        private const double MILLISECONDS_PER_SECOND = 1000.0;
        private const double MICROSECONDS_PER_SECOND = 1000000.0;
        private const double NANOSECONDS_PER_MILLISECOND = 1000000.0;
        private const double NANOSECONDS_PER_MICROSECOND = 1000.0;
        private const double MICROSECONDS_PER_MILLISECOND = 1000.0;

        /// <summary>Oldest row a late frame timing is matched against; timings arrive a few frames after their frame.</summary>
        private const int FRAME_TIMING_MAX_AGE = 16;

        [NoAutoStaticsCleanup] // immutable
        private static readonly double s_tickToMs = MILLISECONDS_PER_SECOND / Stopwatch.Frequency;

        [NoAutoStaticsCleanup] // immutable
        private static readonly double s_tickToMicros = MICROSECONDS_PER_SECOND / Stopwatch.Frequency;

        /// <summary>Nanoseconds per <see cref="ProfilerUnsafeUtility.Timestamp"/> tick, the clock jobs time themselves on.</summary>
        [NoAutoStaticsCleanup] // immutable
        private static readonly double s_profilerTickToNs = CreateProfilerTickToNs();

        /// <summary>Per-slot stopwatch ticks accumulated since the last <see cref="CommitFrame"/> or <see cref="ClearSlots"/>.</summary>
        [NoAutoStaticsCleanup] // contents cleared in DomainReset
        private static readonly long[] s_slotTicks = new long[SlotCount];

        /// <summary>Per-slot milliseconds captured by the last <see cref="PublishSlots"/>.</summary>
        [NoAutoStaticsCleanup] // contents cleared in DomainReset
        private static readonly double[] s_publishedMs = new double[SlotCount];

        [NoAutoStaticsCleanup] // scratch buffer, overwritten by every read
        private static readonly float[] s_statsScratch = new float[RingCapacity];

        /// <summary>Per-counter values for the frame being recorded: gauges held across frames, per-frame counts zeroed by each commit.</summary>
        [NoAutoStaticsCleanup] // contents cleared in DomainReset
        private static readonly int[] s_counterValues = new int[CounterCount];

        /// <summary>Per-counter running total last passed to <see cref="SampleTotal"/>.</summary>
        [NoAutoStaticsCleanup] // contents cleared in DomainReset
        private static readonly long[] s_lastTotals = new long[CounterCount];

        /// <summary>Whether <see cref="s_lastTotals"/> holds a baseline for the counter.</summary>
        [NoAutoStaticsCleanup] // contents cleared in DomainReset
        private static readonly bool[] s_hasTotal = new bool[CounterCount];

#if ENABLE_PROFILER
        private const string MARKER_PREFIX = "PerfStore.";

        [NoAutoStaticsCleanup] // immutable table
        private static readonly ProfilerMarker[] s_markers = CreateMarkers();
#endif

        [NoAutoStaticsCleanup] // reset in DomainReset
        private static PerfTier s_tier;

        [NoAutoStaticsCleanup] // reset in DomainReset
        private static bool s_forceSlots;

        [NoAutoStaticsCleanup] // reset in DomainReset
        private static bool s_slotsActive;

        [NoAutoStaticsCleanup] // reset in DomainReset
        private static PerfFrameRing s_ring;

        /// <summary>Set once the native memory has been freed for quit or reload; later commits are ignored rather than reallocating.</summary>
        [NoAutoStaticsCleanup] // reset in DomainReset
        private static bool s_isShutDown;

        /// <summary>The hitch detector; exists at <see cref="PerfTier.Frame"/> and above.</summary>
        [NoAutoStaticsCleanup] // reset in DomainReset
        private static PerfHitchDetector s_hitches;

        /// <summary>The frame-timing reader; exists at <see cref="PerfTier.Frame"/> and above.</summary>
        [NoAutoStaticsCleanup] // reset in DomainReset
        private static PerfFrameTimingSource s_frameTiming;

        [NoAutoStaticsCleanup] // reset in DomainReset
        private static float s_hitchMinMs;

        [NoAutoStaticsCleanup] // reset in DomainReset
        private static float s_hitchMedianFactor;

        /// <summary>Whether the previous commit's heap and collection readings exist to subtract from.</summary>
        [NoAutoStaticsCleanup] // reset in DomainReset
        private static bool s_hasGcBaseline;

        [NoAutoStaticsCleanup] // reset in DomainReset
        private static long s_lastHeapBytes;

        [NoAutoStaticsCleanup] // reset in DomainReset
        private static int s_lastGcCollectionCount;

        [NoAutoStaticsCleanup] // reset in DomainReset
        private static int s_gcShrinkFrames;

        /// <summary>Start of the newest frame timing submitted, so a timing read again on a later frame is not resubmitted.</summary>
        [NoAutoStaticsCleanup] // reset in DomainReset
        private static long s_lastTimingStart;

        [NoAutoStaticsCleanup] // reset in DomainReset
        private static int s_timingsReceived;

        [NoAutoStaticsCleanup] // reset in DomainReset
        private static int s_timingsMatched;

        /// <summary>Slot ticks recorded before the open <see cref="BeginWorldFrame"/> bracket, excluding the remainder slot.</summary>
        [NoAutoStaticsCleanup] // reset in DomainReset
        private static long s_worldFrameStartSlotTicks;

        [NoAutoStaticsCleanup] // reset in DomainReset
        private static int s_negativeRemainderFrames;

        [NoAutoStaticsCleanup] // reset in DomainReset
        private static bool s_isWorldFrameOpen;

        /// <summary>Per-type latency of the newest completed jobs, in milliseconds; exists at <see cref="PerfTier.Systems"/> and above.</summary>
        [NoAutoStaticsCleanup] // reset in DomainReset
        private static PerfSampleRing[] s_jobLatencyMs;

        /// <summary>Per-type busy time of the newest completed jobs whose execution was timed, in milliseconds; exists with <see cref="s_jobLatencyMs"/>.</summary>
        [NoAutoStaticsCleanup] // reset in DomainReset
        private static PerfSampleRing[] s_jobBusyMs;

        /// <summary>The current tier.</summary>
        public static PerfTier Tier => s_tier;

        /// <summary>The hitch detector, or null below <see cref="PerfTier.Frame"/>.</summary>
        public static PerfHitchDetector Hitches => s_hitches;

        /// <summary>The minimum hitch threshold last passed to <see cref="SetHitchThresholds"/>, as given.</summary>
        public static float HitchMinMs => s_hitchMinMs;

        /// <summary>The median factor last passed to <see cref="SetHitchThresholds"/>, as given.</summary>
        public static float HitchMedianFactor => s_hitchMedianFactor;

        /// <summary>Frames whose heap shrank with no collection counted (<see cref="PerfGcState.Shrank"/>) since the last reset.</summary>
        public static int GcShrinkFrames => s_gcShrinkFrames;

        /// <summary>Frame timings submitted since the last reset.</summary>
        public static int FrameTimingsReceived => s_timingsReceived;

        /// <summary>Submitted frame timings that matched a held row.</summary>
        public static int FrameTimingsMatched => s_timingsMatched;

        /// <summary>World-update brackets whose remainder came out negative — slots that overlapped — since the last reset.</summary>
        public static int NegativeRemainderFrames => s_negativeRemainderFrames;

        /// <summary>
        /// Whether a <see cref="BeginWorldFrame"/> bracket is open. A probe for work that can run either inside
        /// <c>World.Update</c> or outside it checks this, so that inside it the enclosing slot keeps the time.
        /// </summary>
        public static bool IsWorldFrameOpen => s_isWorldFrameOpen;

        /// <summary>Garbage collections recorded across the held frames.</summary>
        public static int GcCollectionsHeld => s_ring?.SumGcCollections() ?? 0;

        /// <summary>
        /// Keeps the slot probes recording regardless of tier. Independent of the tier, so clearing it never turns
        /// off a tier that records slots on its own.
        /// </summary>
        public static bool ForceSlots
        {
            get => s_forceSlots;
            set
            {
                s_forceSlots = value;
                RefreshSlotsActive();
            }
        }

        /// <summary>Whether slot probes are recording (tier at least <see cref="PerfTier.Systems"/>, or <see cref="ForceSlots"/>).</summary>
        public static bool SlotsActive => s_slotsActive;

        /// <summary>Frames held in the history ring.</summary>
        public static int FramesRecorded => s_ring?.Count ?? 0;

        /// <summary>Held frames that carry per-slot times.</summary>
        public static int SlotFramesRecorded => s_ring?.SlotFrameCount ?? 0;

        /// <summary>
        /// Applies a tier, allocating or freeing the ring's slot columns, the hitch detector and the frame-timing
        /// reader to match. Leaving <see cref="PerfTier.Frame"/> discards the held hitch records.
        /// </summary>
        /// <param name="tier">The tier to apply.</param>
        public static void SetTier(PerfTier tier)
        {
            // Totals sampled before the counter columns existed would span every frame since; start over instead.
            if (tier >= PerfTier.Systems && s_tier < PerfTier.Systems) Array.Clear(s_hasTotal, 0, CounterCount);

            s_tier = tier;
            RefreshSlotsActive();
            ApplyFrameTierResources();
            ApplyJobSamples();

            if (s_ring == null) return;

            if (tier >= PerfTier.Systems) s_ring.AllocateSlotColumns();
            else s_ring.ReleaseSlotColumns();
        }

        /// <summary>Sets the hitch thresholds, now and for detectors created later; invalid values fall back to the defaults.</summary>
        /// <param name="minMs">Wall milliseconds above which a frame is always a hitch.</param>
        /// <param name="medianFactor">Multiple of the recent median frame time above which a frame is a hitch.</param>
        public static void SetHitchThresholds(float minMs, float medianFactor)
        {
            s_hitchMinMs = minMs;
            s_hitchMedianFactor = medianFactor;
            s_hitches?.SetThresholds(minMs, medianFactor);
        }

        /// <summary>Opens a timed region with no Profiler marker; close it with <see cref="Accumulate"/>.</summary>
        /// <returns>The start timestamp, or 0 when slots are not active.</returns>
        public static long Begin() => s_slotsActive ? Stopwatch.GetTimestamp() : 0L;

        /// <summary>Opens a timed region for <paramref name="slot"/>, with its Profiler marker; close it with <see cref="End"/>.</summary>
        /// <param name="slot">The slot the region is attributed to.</param>
        /// <returns>The start timestamp, or 0 when slots are not active.</returns>
        public static long Begin(PerfSlot slot)
        {
            if (!s_slotsActive) return 0L;

#if ENABLE_PROFILER
            s_markers[(int)slot].Begin();
#endif
            return Stopwatch.GetTimestamp();
        }

        /// <summary>Closes a region opened by <see cref="Begin(PerfSlot)"/>, adding its time to the slot. A start of 0 is a no-op.</summary>
        /// <param name="slot">The slot passed to <see cref="Begin(PerfSlot)"/>.</param>
        /// <param name="startTimestamp">The value <see cref="Begin(PerfSlot)"/> returned.</param>
        public static void End(PerfSlot slot, long startTimestamp)
        {
            if (startTimestamp == 0L) return;

            s_slotTicks[(int)slot] += Stopwatch.GetTimestamp() - startTimestamp;
#if ENABLE_PROFILER
            s_markers[(int)slot].End();
#endif
        }

        /// <summary>Closes a region opened by <see cref="Begin()"/>, adding its time to a slot. A start of 0 is a no-op.</summary>
        /// <param name="slot">The slot the region is attributed to.</param>
        /// <param name="startTimestamp">The value <see cref="Begin()"/> returned.</param>
        public static void Accumulate(PerfSlot slot, long startTimestamp)
        {
            if (startTimestamp == 0L) return;

            s_slotTicks[(int)slot] += Stopwatch.GetTimestamp() - startTimestamp;
        }

        /// <summary>
        /// Opens the <c>World.Update</c> bracket from which <see cref="PerfSlot.WorldUnattributed"/> is derived. Open it
        /// after any <see cref="ClearSlots"/> in the same update; close it with <see cref="EndWorldFrame"/>.
        /// </summary>
        /// <returns>The start timestamp, or 0 when slots are not active.</returns>
        public static long BeginWorldFrame()
        {
            if (!s_slotsActive) return 0L;

            s_worldFrameStartSlotTicks = SumAttributedSlotTicks();
            s_isWorldFrameOpen = true;
            return Stopwatch.GetTimestamp();
        }

        /// <summary>Closes the bracket opened by <see cref="BeginWorldFrame"/>. A start of 0 is a no-op.</summary>
        /// <param name="startTimestamp">The value <see cref="BeginWorldFrame"/> returned.</param>
        public static void EndWorldFrame(long startTimestamp)
        {
            if (startTimestamp == 0L) return;

            EndWorldFrameAt(startTimestamp, Stopwatch.GetTimestamp());
        }

        /// <summary>
        /// <see cref="EndWorldFrame"/> with an explicit end, so the remainder can be checked against known ticks.
        /// </summary>
        /// <param name="startTimestamp">The value <see cref="BeginWorldFrame"/> returned; 0 is a no-op.</param>
        /// <param name="endTimestamp">The bracket's end, on the <see cref="Stopwatch"/> clock.</param>
        public static void EndWorldFrameAt(long startTimestamp, long endTimestamp)
        {
            if (startTimestamp == 0L) return;

            s_isWorldFrameOpen = false;
            long attributedInBracket = SumAttributedSlotTicks() - s_worldFrameStartSlotTicks;
            long remainder = endTimestamp - startTimestamp - attributedInBracket;
            if (remainder < 0) s_negativeRemainderFrames++;

            s_slotTicks[(int)PerfSlot.WorldUnattributed] += remainder;
        }

        /// <summary>Sets a gauge for the frame being recorded; it holds until set again.</summary>
        /// <param name="counter">A gauge — a counter before <see cref="FirstPerFrameCount"/>.</param>
        /// <param name="value">The gauge's level.</param>
        public static void SetGauge(PerfCounter counter, int value) => s_counterValues[(int)counter] = value;

        /// <summary>
        /// Adds a per-frame count's growth since the previous sample of its running total. The first sample, the first
        /// after the counter columns are allocated, and a total that went down (a new owner, counting from 0) only set
        /// the baseline and add nothing.
        /// </summary>
        /// <param name="counter">A per-frame count — <see cref="FirstPerFrameCount"/> or later.</param>
        /// <param name="total">The counter's running total.</param>
        public static void SampleTotal(PerfCounter counter, long total)
        {
            int index = (int)counter;
            if (s_hasTotal[index] && total >= s_lastTotals[index])
                s_counterValues[index] = (int)Math.Min(s_counterValues[index] + (total - s_lastTotals[index]), int.MaxValue);

            s_lastTotals[index] = total;
            s_hasTotal[index] = true;
        }

        /// <summary>
        /// Zeroes every counter, forgets every running-total baseline and empties the job samples. For a monitor starting
        /// on a new world, whose gauges would otherwise hold the previous world's levels until first set.
        /// </summary>
        public static void ResetCounters()
        {
            Array.Clear(s_counterValues, 0, CounterCount);
            Array.Clear(s_lastTotals, 0, CounterCount);
            Array.Clear(s_hasTotal, 0, CounterCount);

            if (s_jobLatencyMs == null) return;

            for (int i = 0; i < JobTypeCount; i++)
            {
                s_jobLatencyMs[i].Clear();
                s_jobBusyMs[i].Clear();
            }
        }

        /// <summary>
        /// Holds one completed job's latency and, when its execution was timed, its busy time for per-job statistics.
        /// A no-op below <see cref="PerfTier.Systems"/>.
        /// </summary>
        /// <param name="type">The job's type.</param>
        /// <param name="latencyTicks">Schedule to result consumed, in <see cref="Stopwatch"/> ticks.</param>
        /// <param name="busyProfilerTicks">Worker execute time summed over the job's chain, in <see cref="ProfilerUnsafeUtility.Timestamp"/> ticks.</param>
        /// <param name="hasBusy">Whether the execution was timed; an untimed job adds latency only.</param>
        public static void RecordJob(PerfJobType type, long latencyTicks, long busyProfilerTicks, bool hasBusy)
        {
            if (s_jobLatencyMs == null) return;

            s_jobLatencyMs[(int)type].Add((float)(latencyTicks * s_tickToMs));
            if (hasBusy) s_jobBusyMs[(int)type].Add((float)(busyProfilerTicks * s_profilerTickToNs / NANOSECONDS_PER_MILLISECOND));
        }

        /// <summary>Exact statistics of the held jobs' latency — schedule to result consumed — in milliseconds.</summary>
        /// <param name="type">The job type.</param>
        /// <returns>The summary; empty when no job is held.</returns>
        public static PerfWindowSummary SummarizeJobLatencyMs(PerfJobType type) =>
            PerfWindowStats.Summarize(s_statsScratch, s_jobLatencyMs?[(int)type].CopyTo(s_statsScratch) ?? 0);

        /// <summary>Exact statistics of the held jobs' worker busy time, in milliseconds, over the jobs whose execution was timed.</summary>
        /// <param name="type">The job type.</param>
        /// <returns>The summary; empty when no timed job is held.</returns>
        public static PerfWindowSummary SummarizeJobBusyMs(PerfJobType type) =>
            PerfWindowStats.Summarize(s_statsScratch, s_jobBusyMs?[(int)type].CopyTo(s_statsScratch) ?? 0);

        /// <summary>Sums one counter over the held frames that carry counter values.</summary>
        /// <param name="counter">The counter.</param>
        /// <returns>The sum; 0 when no frame carries counters.</returns>
        public static long SumCounter(PerfCounter counter)
        {
            int frames = s_ring?.SlotFrameCount ?? 0;
            long sum = 0;
            for (int age = 0; age < frames; age++)
                sum += s_ring.GetCounter(age, counter);
            return sum;
        }

        /// <summary>Sums wall time over the held frames that carry counter values — the frames <see cref="SumCounter"/> covers.</summary>
        /// <returns>The milliseconds; 0 when no frame carries counters.</returns>
        public static double SumCounterFramesWallMs()
        {
            int frames = s_ring?.SlotFrameCount ?? 0;
            double sum = 0;
            for (int age = 0; age < frames; age++)
                sum += s_ring.GetFrame(age).WallMs;
            return sum;
        }

        /// <summary>
        /// The share of the job workers' time the timed jobs kept busy over the held frames that carry counters:
        /// their busy time over workers × wall time. Busy time is charged to the frame its job's result was consumed in, so
        /// only a window much longer than a job is meaningful, and a single frame can exceed 1.
        /// </summary>
        /// <param name="workerCount">The job worker threads.</param>
        /// <returns>The fraction, or NaN when no frame carries counters or there are no workers.</returns>
        public static double WorkerUtilization(int workerCount)
        {
            double wallMs = SumCounterFramesWallMs();
            if (wallMs <= 0.0 || workerCount <= 0) return double.NaN;

            long busyUs = SumCounter(PerfCounter.GenerationBusyUs) + SumCounter(PerfCounter.LightBusyUs)
                          + SumCounter(PerfCounter.MeshBusyUs) + SumCounter(PerfCounter.FluidBusyUs)
                          + SumCounter(PerfCounter.FluidSoundScanBusyUs);
            return busyUs / MICROSECONDS_PER_MILLISECOND / (workerCount * wallMs);
        }

        /// <summary>Converts <see cref="Stopwatch"/> ticks to whole microseconds.</summary>
        /// <param name="ticks">The ticks.</param>
        /// <returns>The microseconds, truncated.</returns>
        public static long TicksToMicros(long ticks) => (long)(ticks * s_tickToMicros);

        /// <summary>Converts <see cref="ProfilerUnsafeUtility.Timestamp"/> ticks to whole microseconds.</summary>
        /// <param name="ticks">The ticks.</param>
        /// <returns>The microseconds, truncated.</returns>
        public static long ProfilerTicksToMicros(long ticks) => (long)(ticks * s_profilerTickToNs / NANOSECONDS_PER_MICROSECOND);

        /// <summary>Zeroes a contiguous range of slot accumulators.</summary>
        /// <param name="firstSlot">Index of the first slot.</param>
        /// <param name="count">Number of slots.</param>
        public static void ClearSlots(int firstSlot, int count) => Array.Clear(s_slotTicks, firstSlot, count);

        /// <summary>Captures a contiguous range of slot accumulators as milliseconds, read back with <see cref="PublishedMs"/>.</summary>
        /// <param name="firstSlot">Index of the first slot.</param>
        /// <param name="count">Number of slots.</param>
        public static void PublishSlots(int firstSlot, int count)
        {
            int end = firstSlot + count;
            for (int i = firstSlot; i < end; i++)
                s_publishedMs[i] = s_slotTicks[i] * s_tickToMs;
        }

        /// <summary>A slot's milliseconds as of the last <see cref="PublishSlots"/> that covered it.</summary>
        /// <param name="slot">The slot.</param>
        /// <returns>The published milliseconds.</returns>
        public static double PublishedMs(PerfSlot slot) => s_publishedMs[(int)slot];

        /// <summary>
        /// Records one frame into the history ring (slot and counter columns included when allocated), zeroes every slot
        /// accumulator and per-frame count, then lets the hitch detector test the frame. Call once per frame, after
        /// every probed region.
        /// </summary>
        /// <param name="readings">The frame's raw end-of-frame readings.</param>
        public static void CommitFrame(in PerfFrameReadings readings)
        {
            if (s_isShutDown) return;

            // A bracket left open by an exception in World.Update must not outlive its frame.
            s_isWorldFrameOpen = false;
            EnsureRing();
            PerfFrame frame = new PerfFrame
            {
                FrameIndex = readings.FrameIndex,
                WallMs = (float)(readings.WallTicks * s_tickToMs),
                CpuMs = (float)(readings.CpuTicks * s_tickToMs),
                GpuMs = float.NaN,
                RenderThreadMs = float.NaN,
                PresentWaitMs = float.NaN,
                EndTimestamp = readings.EndTimestamp,
            };
            RecordGc(ref frame, readings.HeapBytes, readings.GcCollectionCount);
            s_ring.Commit(frame, s_slotTicks, s_tickToMs, s_counterValues);

            Array.Clear(s_slotTicks, 0, SlotCount);
            Array.Clear(s_counterValues, (int)FirstPerFrameCount, CounterCount - (int)FirstPerFrameCount);
            s_hitches?.OnFrameCommitted(s_ring);
        }

        /// <summary>
        /// Reads the latest frame timings and writes each into its row. A no-op below <see cref="PerfTier.Frame"/>.
        /// Call once per frame, after <see cref="CommitFrame"/>.
        /// </summary>
        public static void SampleFrameTiming()
        {
            if (s_frameTiming == null) return;

            // Newest first: walk oldest to newest so the first-read check only moves forward. Re-reads are written
            // again, so a later read of a frame replaces an earlier one; only the first read is counted.
            for (int i = s_frameTiming.Capture() - 1; i >= 0; i--)
            {
                FrameTiming timing = s_frameTiming.GetTiming(i);
                long start = (long)timing.frameStartTimestamp;
                bool isFirstRead = start > s_lastTimingStart;
                if (isFirstRead) s_lastTimingStart = start;

                SubmitFrameTiming(start, (float)timing.gpuFrameTime, (float)timing.cpuRenderThreadFrameTime,
                    (float)timing.cpuMainThreadPresentWaitTime, isFirstRead);
            }
        }

        /// <summary>
        /// Writes one frame timing into the held row it describes. A GPU time of 0 or less is stored as NaN — the
        /// platform reporting none, not a frame that cost nothing. A first read is counted as received and, when a
        /// row matched, matched.
        /// </summary>
        /// <param name="frameStartTimestamp">The timing's frame start, on the <see cref="PerfFrame.EndTimestamp"/> clock.</param>
        /// <param name="gpuMs">GPU milliseconds.</param>
        /// <param name="renderThreadMs">Render-thread milliseconds.</param>
        /// <param name="presentWaitMs">Present-wait milliseconds.</param>
        /// <param name="isFirstRead">Whether this frame's timing is submitted for the first time; re-reads are written but not counted.</param>
        /// <returns>True when a held row matched.</returns>
        public static bool SubmitFrameTiming(long frameStartTimestamp, float gpuMs, float renderThreadMs, float presentWaitMs,
            bool isFirstRead = true)
        {
            if (s_ring == null) return false;

            float reportedGpuMs = gpuMs > 0f ? gpuMs : float.NaN;
            bool matched = s_ring.TryBackfillFrameTiming(frameStartTimestamp, reportedGpuMs, renderThreadMs, presentWaitMs,
                FRAME_TIMING_MAX_AGE);
            if (!isFirstRead) return matched;

            s_timingsReceived++;
            if (matched) s_timingsMatched++;
            return matched;
        }

        /// <summary>
        /// Forgets the previous heap and collection readings, so the next commit records
        /// <see cref="PerfGcState.NoBaseline"/>. For a monitor starting after frames it did not see, whose heap delta
        /// would otherwise span them.
        /// </summary>
        public static void ResetGcBaseline() => s_hasGcBaseline = false;

        /// <summary>Exact statistics of wall time over the held frames.</summary>
        /// <returns>The summary; empty when no frame is held.</returns>
        public static PerfWindowSummary SummarizeWallMs() => Summarize(PerfFrameField.WallMs);

        /// <summary>Exact statistics of CPU time over the held frames.</summary>
        /// <returns>The summary; empty when no frame is held.</returns>
        public static PerfWindowSummary SummarizeCpuMs() => Summarize(PerfFrameField.CpuMs);

        /// <summary>Exact statistics of one frame field over the held frames that have a value for it (see <see cref="PerfFrameRing.CopyField"/>).</summary>
        /// <param name="field">The field.</param>
        /// <returns>The summary; empty when no held frame has a value.</returns>
        public static PerfWindowSummary Summarize(PerfFrameField field) =>
            PerfWindowStats.Summarize(s_statsScratch, s_ring?.CopyField(field, s_statsScratch) ?? 0);

        /// <summary>Exact statistics of one slot over the held frames that carry slot times.</summary>
        /// <param name="slot">The slot.</param>
        /// <returns>The summary; empty when no frame carries slot times.</returns>
        public static PerfWindowSummary SummarizeSlotMs(PerfSlot slot) =>
            PerfWindowStats.Summarize(s_statsScratch, s_ring?.CopySlotMs(slot, s_statsScratch) ?? 0);

        /// <summary>Exact statistics of one counter over the held frames that carry counter values (those with slot times).</summary>
        /// <param name="counter">The counter.</param>
        /// <returns>The summary; empty when no frame carries counter values.</returns>
        public static PerfWindowSummary SummarizeCounter(PerfCounter counter) =>
            PerfWindowStats.Summarize(s_statsScratch, s_ring?.CopyCounter(counter, s_statsScratch) ?? 0);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void DomainReset()
        {
            DisposeNative();
            s_tier = PerfTier.Basic;
            s_forceSlots = false;
            s_slotsActive = false;
            s_isShutDown = false;
            s_hitchMinMs = PerfHitchDetector.DefaultMinMs;
            s_hitchMedianFactor = PerfHitchDetector.DefaultMedianFactor;
            s_hasGcBaseline = false;
            s_lastHeapBytes = 0;
            s_lastGcCollectionCount = 0;
            s_gcShrinkFrames = 0;
            s_lastTimingStart = 0;
            s_timingsReceived = 0;
            s_timingsMatched = 0;
            s_worldFrameStartSlotTicks = 0;
            s_isWorldFrameOpen = false;
            s_negativeRemainderFrames = 0;
            Array.Clear(s_slotTicks, 0, SlotCount);
            Array.Clear(s_publishedMs, 0, SlotCount);
            ResetCounters();
        }

        private static void RefreshSlotsActive() => s_slotsActive = s_forceSlots || s_tier >= PerfTier.Systems;

        /// <summary>The sum of every slot accumulator except <see cref="PerfSlot.WorldUnattributed"/>.</summary>
        private static long SumAttributedSlotTicks()
        {
            long sum = 0;
            for (int i = 0; i < SlotCount; i++)
                sum += s_slotTicks[i];
            return sum - s_slotTicks[(int)PerfSlot.WorldUnattributed];
        }

        /// <summary>Fills a frame's allocation fields from the heap and collection readings, then keeps them as the next baseline.</summary>
        private static void RecordGc(ref PerfFrame frame, long heapBytes, int collectionCount)
        {
            if (!s_hasGcBaseline)
            {
                frame.GcState = PerfGcState.NoBaseline;
            }
            else
            {
                int collections = Math.Max(collectionCount - s_lastGcCollectionCount, 0);
                long delta = heapBytes - s_lastHeapBytes;
                frame.GcCollections = (byte)Math.Min(collections, byte.MaxValue);

                if (collections > 0)
                {
                    frame.GcState = PerfGcState.Collected;
                }
                else if (delta < 0)
                {
                    frame.GcState = PerfGcState.Shrank;
                    s_gcShrinkFrames++;
                }
                else
                {
                    frame.GcState = PerfGcState.Measured;
                    frame.GcAllocBytes = (int)Math.Min(delta, int.MaxValue);
                }
            }

            s_lastHeapBytes = heapBytes;
            s_lastGcCollectionCount = collectionCount;
            s_hasGcBaseline = true;
        }

        /// <summary>Creates or frees the hitch detector, its slot block and the frame-timing reader for the current tier.</summary>
        private static void ApplyFrameTierResources()
        {
            if (s_tier < PerfTier.Frame || s_isShutDown)
            {
                ReleaseFrameTierResources();
                return;
            }

            s_hitches ??= new PerfHitchDetector(SlotCount, s_hitchMinMs, s_hitchMedianFactor, CounterCount);
            s_frameTiming ??= new PerfFrameTimingSource();
            if (s_tier >= PerfTier.Systems) s_hitches.AllocateSlotBlock();
            else s_hitches.ReleaseSlotBlock();

            RegisterShutDown();
        }

        /// <summary>Creates the job sample rings at <see cref="PerfTier.Systems"/> and above, and drops them below.</summary>
        private static void ApplyJobSamples()
        {
            if (s_tier < PerfTier.Systems || s_isShutDown)
            {
                s_jobLatencyMs = null;
                s_jobBusyMs = null;
                return;
            }

            if (s_jobLatencyMs != null) return;

            s_jobLatencyMs = new PerfSampleRing[JobTypeCount];
            s_jobBusyMs = new PerfSampleRing[JobTypeCount];
            for (int i = 0; i < JobTypeCount; i++)
            {
                s_jobLatencyMs[i] = new PerfSampleRing(JobSampleCapacity);
                s_jobBusyMs[i] = new PerfSampleRing(JobSampleCapacity);
            }
        }

        private static double CreateProfilerTickToNs()
        {
            ProfilerUnsafeUtility.TimestampConversionRatio ratio = ProfilerUnsafeUtility.TimestampToNanosecondsConversionRatio;
            return ratio.Denominator != 0 ? (double)ratio.Numerator / ratio.Denominator : 0.0;
        }

        private static void ReleaseFrameTierResources()
        {
            s_hitches?.Dispose();
            s_hitches = null;
            s_frameTiming?.Dispose();
            s_frameTiming = null;
        }

        private static void EnsureRing()
        {
            if (s_ring != null) return;

            s_ring = new PerfFrameRing(RingCapacity, SlotCount, CounterCount);
            if (s_tier >= PerfTier.Systems) s_ring.AllocateSlotColumns();
            RegisterShutDown();
        }

        private static void RegisterShutDown()
        {
            Application.quitting -= ShutDown;
            Application.quitting += ShutDown;
#if UNITY_EDITOR
            AssemblyReloadEvents.beforeAssemblyReload -= ShutDown;
            AssemblyReloadEvents.beforeAssemblyReload += ShutDown;
#endif
        }

        private static void ShutDown()
        {
            DisposeNative();
            s_isShutDown = true;
        }

        /// <summary>Frees the ring, the hitch detector and the frame-timing reader, and stops listening for shutdown.</summary>
        private static void DisposeNative()
        {
            Application.quitting -= ShutDown;
#if UNITY_EDITOR
            AssemblyReloadEvents.beforeAssemblyReload -= ShutDown;
#endif
            ReleaseFrameTierResources();
            s_ring?.Dispose();
            s_ring = null;
            s_jobLatencyMs = null;
            s_jobBusyMs = null;
        }

#if ENABLE_PROFILER
        private static ProfilerMarker[] CreateMarkers()
        {
            ProfilerMarker[] markers = new ProfilerMarker[SlotCount];
            for (int i = 0; i < SlotCount; i++)
                markers[i] = new ProfilerMarker(MARKER_PREFIX + (PerfSlot)i);
            return markers;
        }
#endif
    }
}
