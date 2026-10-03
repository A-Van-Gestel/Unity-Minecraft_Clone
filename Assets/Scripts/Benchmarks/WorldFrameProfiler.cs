using System.Diagnostics;
using Diagnostics;

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
    /// <b>A facade over <see cref="PerfStore"/>.</b> Each <see cref="Phase"/> is the <see cref="PerfSlot"/> of
    /// the same value and name, and a published value is exactly <c>ticks × (1000 / Stopwatch.Frequency)</c> as a
    /// <c>double</c>, so every capture recorded through this API is comparable with every other. Being
    /// <see cref="Stopwatch"/>-based, it reports in Master IL2CPP players, where <c>ProfilerMarker</c> timings
    /// cannot be read.
    /// </para>
    /// <para>
    /// <b>Zero cost when inactive:</b> the probes record only while <see cref="PerfStore.SlotsActive"/> — when
    /// <see cref="Enabled"/> is set, or the performance monitor's tier records slots on its own.
    /// <see cref="Begin"/> then returns <c>0</c> after a single bool read (no timestamp), and <see cref="Add"/>
    /// ignores a <c>0</c> start; no allocation on any path. Distinct from <see cref="PerformanceMonitor"/>, which
    /// times the whole-frame Unity lifecycle phases — this times the <c>World.Update</c> interior.
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
            /// The <b>instrumented-build-only</b> LP-1 skylight-queue pairing probe: a walk of
            /// <c>SkylightRecalculationQueue</c> riding the same ~1 Hz cadence as
            /// <see cref="LightFailSafeScan"/>. Its own slot so the probe's cost never lands in that scan's
            /// slot, which P9-0 carved out specifically to measure a whole-world walk against view distance.
            /// Always 0 ms in release builds, where the probe is compiled out.
            /// </summary>
            LightQueueProbe = 9,
        }

        /// <summary>Number of <see cref="Phase"/> values.</summary>
        public const int PhaseCount = 10;

        /// <summary>
        /// Forces the probes to record for the duration of a capture, whatever the performance monitor's tier
        /// (<see cref="PerfStore.ForceSlots"/>). Clearing it never stops a tier that records slots on its own.
        /// Reset to <c>false</c> on play-mode entry by <see cref="PerfStore"/>.
        /// </summary>
        public static bool Enabled
        {
            get => PerfStore.ForceSlots;
            set => PerfStore.ForceSlots = value;
        }

        /// <summary>
        /// Milliseconds spent in one phase during the frame most recently closed by <see cref="EndFrame"/>.
        /// The indexed accessor exists so an aggregator can sum every phase in a loop without a switch that
        /// would silently omit a phase added later — the named properties below are conveniences over it.
        /// </summary>
        /// <param name="phase">The phase to read.</param>
        /// <returns>That phase's milliseconds in the last closed frame (0 while disabled).</returns>
        public static double LastFrameMs(Phase phase) => PerfStore.PublishedMs((PerfSlot)phase);

        /// <summary>Milliseconds spent in <see cref="Phase.Tick"/> during the frame most recently closed by <see cref="EndFrame"/>.</summary>
        public static double LastFrameTickMs => LastFrameMs(Phase.Tick);

        /// <summary>Milliseconds spent in <see cref="Phase.Apply"/> during the frame most recently closed by <see cref="EndFrame"/>.</summary>
        public static double LastFrameApplyMs => LastFrameMs(Phase.Apply);

        /// <summary>Milliseconds spent in <see cref="Phase.LightMerge"/> during the frame most recently closed by <see cref="EndFrame"/>.</summary>
        public static double LastFrameLightMergeMs => LastFrameMs(Phase.LightMerge);

        /// <summary>Milliseconds spent in <see cref="Phase.LightStagingDrain"/> during the frame most recently closed by <see cref="EndFrame"/>.</summary>
        public static double LastFrameLightStagingDrainMs => LastFrameMs(Phase.LightStagingDrain);

        /// <summary>Milliseconds spent in <see cref="Phase.LightFailSafeScan"/> during the frame most recently closed by <see cref="EndFrame"/>.</summary>
        public static double LastFrameLightFailSafeScanMs => LastFrameMs(Phase.LightFailSafeScan);

        /// <summary>Milliseconds spent in <see cref="Phase.LightSchedule"/> during the frame most recently closed by <see cref="EndFrame"/>.</summary>
        public static double LastFrameLightScheduleMs => LastFrameMs(Phase.LightSchedule);

        /// <summary>Milliseconds spent in <see cref="Phase.MeshProcess"/> during the frame most recently closed by <see cref="EndFrame"/>.</summary>
        public static double LastFrameMeshProcessMs => LastFrameMs(Phase.MeshProcess);

        /// <summary>Milliseconds spent in <see cref="Phase.MeshSchedule"/> during the frame most recently closed by <see cref="EndFrame"/>.</summary>
        public static double LastFrameMeshScheduleMs => LastFrameMs(Phase.MeshSchedule);

        /// <summary>Milliseconds spent in <see cref="Phase.GenerationProcess"/> during the frame most recently closed by <see cref="EndFrame"/>.</summary>
        public static double LastFrameGenerationProcessMs => LastFrameMs(Phase.GenerationProcess);

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
        /// Resets the per-frame accumulators of every phase. Called once at the top of the <c>World.Update</c>
        /// body, before any timed region. No-op while the probes are inactive.
        /// </summary>
        public static void BeginFrame()
        {
            if (!PerfStore.SlotsActive) return;

            PerfStore.ClearSlots(0, PhaseCount);
        }

        /// <summary>
        /// Publishes the per-frame accumulators into the <c>LastFrame*Ms</c> properties for the collector to read.
        /// Called once at the end of <c>World.Update</c>, after every timed region. No-op while the probes are
        /// inactive, so the last published values stay readable.
        /// </summary>
        public static void EndFrame()
        {
            if (!PerfStore.SlotsActive) return;

            PerfStore.PublishSlots(0, PhaseCount);
        }

        /// <summary>
        /// Opens a timed section: returns a start timestamp to hand back to <see cref="Add"/>. Returns <c>0</c>
        /// (no <see cref="Stopwatch"/> read) while the probes are inactive. Used as a two-line pair around an
        /// existing <c>World.Update</c> region so the region's control flow is never re-bracketed or reordered
        /// (a hard invariant of the deadlock-prone chunk pipeline).
        /// </summary>
        /// <returns>The stopwatch start timestamp, or <c>0</c> when inactive.</returns>
        public static long Begin() => PerfStore.Begin();

        /// <summary>
        /// Closes a timed section opened by <see cref="Begin"/>, adding its elapsed ticks to the given phase's
        /// per-frame accumulator. A start of <c>0</c> (an inactive <see cref="Begin"/>) is a no-op.
        /// </summary>
        /// <param name="phase">The cost center the elapsed time is attributed to.</param>
        /// <param name="startTimestamp">The value returned by the paired <see cref="Begin"/> call.</param>
        public static void Add(Phase phase, long startTimestamp) => PerfStore.Accumulate((PerfSlot)phase, startTimestamp);
    }
}
