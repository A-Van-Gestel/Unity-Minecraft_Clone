using System.Diagnostics;
using UnityEngine;

namespace Benchmarks
{
    /// <summary>
    /// Opt-in, <see cref="Stopwatch"/>-based sub-phase profiler that slices the <b>interior of
    /// <c>World.Update</c></b> into the main-thread cost centers the pipeline spends its frame on: the
    /// behavior <see cref="Phase.Tick"/>, the modification <see cref="Phase.Apply"/> drain, one slot per
    /// budgeted pass, and one for each of the three <b>unbudgeted</b> lighting regions.
    /// <para>
    /// This is the measurement the <b>isolated</b> tick benchmark could not provide: those cost centers are
    /// private methods callable only from <c>World.Update</c>, so attributing the <i>real</i> ocean frame
    /// (tick vs the mesh-rebuild it triggers vs lighting) requires timing them in place. It is consumed by the
    /// full-world fluid stress pass (<c>FluidStressController</c>) and by the flight capture
    /// (<c>BenchmarkController</c>), each of which flips <see cref="Enabled"/> on for the duration of a run.
    /// </para>
    /// <para>
    /// <b>Merge and scan are separate slots (P9-0).</b> A single combined "lighting" slot could not say
    /// whether a frame's cost sat in <c>ProcessLightingJobs</c>' unbudgeted merge or in the budgeted
    /// ready-set scan — the ambiguity that forced the P9-0a capture to *model* the split with a fitted
    /// parameter instead of measuring it. <see cref="LastFrameLightMs"/> and <see cref="LastFrameMeshMs"/>
    /// remain available as the sums, so consumers wanting the old granularity are unaffected.
    /// </para>
    /// <para>
    /// <b>Stopwatch, not <c>ProfilerRecorder</c>:</b> per <c>PERFORMANCE_PROFILER_OVERHAUL.md</c>,
    /// <c>ProfilerRecorder</c> returns invalid data in non-Development/IL2CPP builds — the same reason
    /// <see cref="PerformanceMonitor"/> is Stopwatch-based. The existing <c>Chunk.TickUpdate</c> /
    /// <c>World.ApplyModifications</c> <c>ProfilerMarker</c>s only feed the Profiler window under deep profiling,
    /// so they cannot drive an IL2CPP capture. This profiler can.
    /// </para>
    /// <para>
    /// <b>Zero cost when disabled:</b> <see cref="Begin"/> returns <c>0</c> after a single bool read (no
    /// timestamp), and <see cref="Add"/> early-returns; no allocation on any path. Distinct from
    /// <see cref="PerformanceMonitor"/>, which times the whole-frame Unity lifecycle phases — this times the
    /// <c>World.Update</c> interior.
    /// </para>
    /// </summary>
    public static class WorldFrameProfiler
    {
        /// <summary>The <c>World.Update</c> cost centers this profiler attributes.</summary>
        public enum Phase
        {
            /// <summary>The behavior tick (<c>ProcessTickUpdates</c> → grass/fluid <c>Chunk.TickUpdate</c>).</summary>
            Tick = 0,

            /// <summary>The voxel-modification drain (<c>World.ApplyModifications</c>).</summary>
            Apply = 1,

            /// <summary>
            /// The <b>unbudgeted</b> lighting merge (<c>WorldJobManager.ProcessLightingJobs</c>): completing
            /// finished jobs and merging their light maps back into chunk data. Deliberately its own slot —
            /// it is the one pipeline pass that takes no budget window, so it is invisible to the stop-reason
            /// instrument and was the leading suspect for the cost P9-0a could not attribute.
            /// </summary>
            LightMerge = 2,

            /// <summary>
            /// The <b>unbudgeted</b> staging drain: the thread-safe queue of flags raised by background
            /// deserialization threads, folded into the main-thread ready set. Its own slot for the same
            /// reason as <see cref="LightFailSafeScan"/> — it runs <i>before</i> the schedule pass's budget
            /// window opens, so charging it to <see cref="LightSchedule"/> would make that slot no longer
            /// comparable to <c>lightScheduleBudgetMs</c>. Not free: it is O(staged flags) and closes a park
            /// interval per promoted entry, so a post-load-wave burst belongs somewhere visible.
            /// </summary>
            LightStagingDrain = 3,

            /// <summary>
            /// The <b>unbudgeted</b> ~1 Hz fail-safe lighting scan: a full walk of every resident chunk that
            /// re-flags missed work and re-promotes the parked frontier. Its own slot because it is a
            /// whole-world walk (thousands of chunks at high view distance) that runs <i>outside</i> the
            /// schedule pass's ms ceiling by design — the budget window deliberately starts after it — so
            /// folding it into <see cref="LightSchedule"/> would both overstate that pass against its own
            /// 8 ms ceiling and hide a cost that scales with view distance.
            /// </summary>
            LightFailSafeScan = 4,

            /// <summary>
            /// The budgeted lighting ready-set scan (quota + ms ceiling + in-flight cap). Bracketed to cover
            /// exactly the loop the ceiling governs — nothing before the window opens — so the measured ms is
            /// directly comparable to <c>lightScheduleBudgetMs</c>.
            /// </summary>
            LightSchedule = 5,

            /// <summary>The budgeted completed-mesh-job pass (buffer upload + load-animation trigger).</summary>
            MeshProcess = 6,

            /// <summary>The budgeted mesh-build-queue drain (<c>MeshDrainPolicy.Drain</c>).</summary>
            MeshSchedule = 7,

            /// <summary>The budgeted completed-generation-job pass.</summary>
            GenerationProcess = 8,

            /// <summary>
            /// The <b>dev/editor-only</b> LP-1 sunlight-queue pairing probe: a walk of
            /// <c>SunlightRecalculationQueue</c> riding the same ~1 Hz cadence as
            /// <see cref="LightFailSafeScan"/>. Its own slot so the probe's cost never lands in that scan's
            /// slot, which P9-0 carved out specifically to measure a whole-world walk against view distance.
            /// Always 0 ms in release builds, where the probe is compiled out.
            /// </summary>
            LightQueueProbe = 9,
        }

        /// <summary>Number of <see cref="Phase"/> values.</summary>
        public const int PhaseCount = 10;

        /// <summary>
        /// When <c>false</c> (default) every method is a no-op guarded by a single bool read, so production
        /// frames pay nothing. Flipped on for the duration of a capture by the full-world fluid stress pass
        /// and by the flight capture; both clear it again on teardown.
        /// </summary>
        public static bool Enabled;

        private static readonly double s_tickToMs = 1000.0 / Stopwatch.Frequency;

        /// <summary>Per-frame accumulated stopwatch ticks, one slot per <see cref="Phase"/> (reset each <see cref="BeginFrame"/>).</summary>
        private static readonly long[] s_frameTicks = new long[PhaseCount];

        /// <summary>Published per-phase milliseconds for the frame most recently closed by <see cref="EndFrame"/>.</summary>
        private static readonly double[] s_lastFrameMs = new double[PhaseCount];

        /// <summary>
        /// Milliseconds spent in one phase during the frame most recently closed by <see cref="EndFrame"/>.
        /// The indexed accessor exists so an aggregator can sum every phase in a loop without a switch that
        /// would silently omit a phase added later — the named properties below are conveniences over it.
        /// </summary>
        /// <param name="phase">The phase to read.</param>
        /// <returns>That phase's milliseconds in the last closed frame (0 while disabled).</returns>
        public static double LastFrameMs(Phase phase) => s_lastFrameMs[(int)phase];

        /// <summary>Milliseconds spent in <see cref="Phase.Tick"/> during the frame most recently closed by <see cref="EndFrame"/>.</summary>
        public static double LastFrameTickMs => s_lastFrameMs[(int)Phase.Tick];

        /// <summary>Milliseconds spent in <see cref="Phase.Apply"/> during the frame most recently closed by <see cref="EndFrame"/>.</summary>
        public static double LastFrameApplyMs => s_lastFrameMs[(int)Phase.Apply];

        /// <summary>Milliseconds spent in <see cref="Phase.LightMerge"/> during the frame most recently closed by <see cref="EndFrame"/>.</summary>
        public static double LastFrameLightMergeMs => s_lastFrameMs[(int)Phase.LightMerge];

        /// <summary>Milliseconds spent in <see cref="Phase.LightStagingDrain"/> during the frame most recently closed by <see cref="EndFrame"/>.</summary>
        public static double LastFrameLightStagingDrainMs => s_lastFrameMs[(int)Phase.LightStagingDrain];

        /// <summary>Milliseconds spent in <see cref="Phase.LightFailSafeScan"/> during the frame most recently closed by <see cref="EndFrame"/>.</summary>
        public static double LastFrameLightFailSafeScanMs => s_lastFrameMs[(int)Phase.LightFailSafeScan];

        /// <summary>Milliseconds spent in <see cref="Phase.LightSchedule"/> during the frame most recently closed by <see cref="EndFrame"/>.</summary>
        public static double LastFrameLightScheduleMs => s_lastFrameMs[(int)Phase.LightSchedule];

        /// <summary>Milliseconds spent in <see cref="Phase.MeshProcess"/> during the frame most recently closed by <see cref="EndFrame"/>.</summary>
        public static double LastFrameMeshProcessMs => s_lastFrameMs[(int)Phase.MeshProcess];

        /// <summary>Milliseconds spent in <see cref="Phase.MeshSchedule"/> during the frame most recently closed by <see cref="EndFrame"/>.</summary>
        public static double LastFrameMeshScheduleMs => s_lastFrameMs[(int)Phase.MeshSchedule];

        /// <summary>Milliseconds spent in <see cref="Phase.GenerationProcess"/> during the frame most recently closed by <see cref="EndFrame"/>.</summary>
        public static double LastFrameGenerationProcessMs => s_lastFrameMs[(int)Phase.GenerationProcess];

        /// <summary>
        /// Total main-thread mesh milliseconds — <see cref="Phase.MeshProcess"/> + <see cref="Phase.MeshSchedule"/>
        /// — for the frame most recently closed by <see cref="EndFrame"/>. Derived rather than accumulated, so
        /// the two sub-slots stay the single source of truth and consumers predating the P9-0 split (the fluid
        /// stress collector) keep reading the same quantity they always did.
        /// </summary>
        public static double LastFrameMeshMs => LastFrameMeshProcessMs + LastFrameMeshScheduleMs;

        /// <summary>
        /// Total main-thread lighting milliseconds — <see cref="Phase.LightMerge"/> +
        /// <see cref="Phase.LightStagingDrain"/> + <see cref="Phase.LightFailSafeScan"/> +
        /// <see cref="Phase.LightSchedule"/> — for the frame most recently closed by <see cref="EndFrame"/>.
        /// Derived; see <see cref="LastFrameMeshMs"/>. The four terms cover the region the pre-P9-0 single
        /// lighting slot spanned, so a fluid-stress capture taken before the split stays comparable to one
        /// taken after it — excepting an editor-only stuck-chunk walk that runs solely when lighting is
        /// <i>disabled</i>, a configuration no capture uses.
        /// </summary>
        public static double LastFrameLightMs =>
            LastFrameLightMergeMs + LastFrameLightStagingDrainMs
                                  + LastFrameLightFailSafeScanMs + LastFrameLightScheduleMs;

        /// <summary>
        /// Clears all static state on play-mode entry so a profiler left <see cref="Enabled"/> (or holding stale
        /// per-frame values) by a previous session never leaks into the next when domain reload is disabled.
        /// Mirrors the <c>DomainReset</c> convention used by <see cref="PerformanceMonitor"/> and
        /// <c>WorldLaunchState</c>.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void DomainReset()
        {
            Enabled = false;

            for (int i = 0; i < PhaseCount; i++)
            {
                s_frameTicks[i] = 0;
                s_lastFrameMs[i] = 0;
            }
        }

        /// <summary>
        /// Resets the per-frame accumulators. Called once at the top of the <c>World.Update</c> body, before any
        /// timed region. No-op when <see cref="Enabled"/> is <c>false</c>.
        /// </summary>
        public static void BeginFrame()
        {
            if (!Enabled) return;

            for (int i = 0; i < PhaseCount; i++)
                s_frameTicks[i] = 0;
        }

        /// <summary>
        /// Publishes the per-frame accumulators into the <c>LastFrame*Ms</c> properties for the collector to read.
        /// Called once at the end of <c>World.Update</c>, after every timed region. No-op when
        /// <see cref="Enabled"/> is <c>false</c>.
        /// </summary>
        public static void EndFrame()
        {
            if (!Enabled) return;

            for (int i = 0; i < PhaseCount; i++)
                s_lastFrameMs[i] = s_frameTicks[i] * s_tickToMs;
        }

        /// <summary>
        /// Opens a timed section: returns a start timestamp to hand back to <see cref="Add"/>. Returns <c>0</c>
        /// (no <see cref="Stopwatch"/> read) when <see cref="Enabled"/> is <c>false</c>. Used as a two-line pair
        /// around an existing <c>World.Update</c> region so the region's control flow is never re-bracketed or
        /// reordered (a hard invariant of the deadlock-prone chunk pipeline).
        /// </summary>
        /// <returns>The stopwatch start timestamp, or <c>0</c> when disabled.</returns>
        public static long Begin() => Enabled ? Stopwatch.GetTimestamp() : 0L;

        /// <summary>
        /// Closes a timed section opened by <see cref="Begin"/>, adding its elapsed ticks to the given phase's
        /// per-frame accumulator. No-op when <see cref="Enabled"/> is <c>false</c>.
        /// </summary>
        /// <param name="phase">The cost center the elapsed time is attributed to.</param>
        /// <param name="startTimestamp">The value returned by the paired <see cref="Begin"/> call.</param>
        public static void Add(Phase phase, long startTimestamp)
        {
            if (!Enabled) return;

            s_frameTicks[(int)phase] += Stopwatch.GetTimestamp() - startTimestamp;
        }
    }
}
