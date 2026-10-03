using System;
using System.Diagnostics;
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
    /// <b>Tiers.</b> The frame rows (wall and CPU time) are always recorded. Slot probes record only while
    /// <see cref="SlotsActive"/> — at <see cref="PerfTier.Systems"/> and above, or while <see cref="ForceSlots"/>
    /// is set — and otherwise cost one static bool read, with no timestamp taken. The ring's slot columns exist
    /// only at <see cref="PerfTier.Systems"/> and above; a forced run at a lower tier keeps the accumulators
    /// live without allocating them.
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
    /// <para>Main thread only. Native memory is freed on application quit and before an Editor assembly reload.</para>
    /// </summary>
    public static class PerfStore
    {
        /// <summary>Frames of history the ring retains.</summary>
        public const int RingCapacity = 2048;

        /// <summary>Number of <see cref="PerfSlot"/> values.</summary>
        public const int SlotCount = (int)PerfSlot.Count;

        private const double MILLISECONDS_PER_SECOND = 1000.0;

        [NoAutoStaticsCleanup] // immutable
        private static readonly double s_tickToMs = MILLISECONDS_PER_SECOND / Stopwatch.Frequency;

        /// <summary>Per-slot stopwatch ticks accumulated since the last <see cref="CommitFrame"/> or <see cref="ClearSlots"/>.</summary>
        [NoAutoStaticsCleanup] // contents cleared in DomainReset
        private static readonly long[] s_slotTicks = new long[SlotCount];

        /// <summary>Per-slot milliseconds captured by the last <see cref="PublishSlots"/>.</summary>
        [NoAutoStaticsCleanup] // contents cleared in DomainReset
        private static readonly double[] s_publishedMs = new double[SlotCount];

        [NoAutoStaticsCleanup] // scratch buffer, overwritten by every read
        private static readonly float[] s_statsScratch = new float[RingCapacity];

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

        /// <summary>The current tier.</summary>
        public static PerfTier Tier => s_tier;

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

        /// <summary>Applies a tier, allocating or freeing the ring's slot columns to match.</summary>
        /// <param name="tier">The tier to apply.</param>
        public static void SetTier(PerfTier tier)
        {
            s_tier = tier;
            RefreshSlotsActive();

            if (s_ring == null) return;

            if (tier >= PerfTier.Systems) s_ring.AllocateSlotColumns();
            else s_ring.ReleaseSlotColumns();
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
        /// Records one frame into the history ring (slot columns included when allocated), then zeroes every slot
        /// accumulator. Call once per frame, after every probed region.
        /// </summary>
        /// <param name="wallTicks">The frame's wall-clock stopwatch ticks.</param>
        /// <param name="cpuTicks">The frame's measured main-thread CPU stopwatch ticks.</param>
        /// <param name="frameIndex">The frame's <c>Time.frameCount</c>.</param>
        public static void CommitFrame(long wallTicks, long cpuTicks, int frameIndex)
        {
            if (s_isShutDown) return;

            EnsureRing();
            s_ring.Commit(new PerfFrame
            {
                FrameIndex = frameIndex,
                WallMs = (float)(wallTicks * s_tickToMs),
                CpuMs = (float)(cpuTicks * s_tickToMs),
            }, s_slotTicks, s_tickToMs);

            Array.Clear(s_slotTicks, 0, SlotCount);
        }

        /// <summary>Exact statistics of wall time over the held frames.</summary>
        /// <returns>The summary; empty when no frame is held.</returns>
        public static PerfWindowSummary SummarizeWallMs() =>
            PerfWindowStats.Summarize(s_statsScratch, s_ring?.CopyWallMs(s_statsScratch) ?? 0);

        /// <summary>Exact statistics of CPU time over the held frames.</summary>
        /// <returns>The summary; empty when no frame is held.</returns>
        public static PerfWindowSummary SummarizeCpuMs() =>
            PerfWindowStats.Summarize(s_statsScratch, s_ring?.CopyCpuMs(s_statsScratch) ?? 0);

        /// <summary>Exact statistics of one slot over the held frames that carry slot times.</summary>
        /// <param name="slot">The slot.</param>
        /// <returns>The summary; empty when no frame carries slot times.</returns>
        public static PerfWindowSummary SummarizeSlotMs(PerfSlot slot) =>
            PerfWindowStats.Summarize(s_statsScratch, s_ring?.CopySlotMs(slot, s_statsScratch) ?? 0);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void DomainReset()
        {
            DisposeRing();
            s_tier = PerfTier.Basic;
            s_forceSlots = false;
            s_slotsActive = false;
            s_isShutDown = false;
            Array.Clear(s_slotTicks, 0, SlotCount);
            Array.Clear(s_publishedMs, 0, SlotCount);
        }

        private static void RefreshSlotsActive() => s_slotsActive = s_forceSlots || s_tier >= PerfTier.Systems;

        private static void EnsureRing()
        {
            if (s_ring != null) return;

            s_ring = new PerfFrameRing(RingCapacity, SlotCount);
            if (s_tier >= PerfTier.Systems) s_ring.AllocateSlotColumns();

            Application.quitting -= ShutDown;
            Application.quitting += ShutDown;
#if UNITY_EDITOR
            AssemblyReloadEvents.beforeAssemblyReload -= ShutDown;
            AssemblyReloadEvents.beforeAssemblyReload += ShutDown;
#endif
        }

        private static void ShutDown()
        {
            DisposeRing();
            s_isShutDown = true;
        }

        private static void DisposeRing()
        {
            Application.quitting -= ShutDown;
#if UNITY_EDITOR
            AssemblyReloadEvents.beforeAssemblyReload -= ShutDown;
#endif
            s_ring?.Dispose();
            s_ring = null;
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
