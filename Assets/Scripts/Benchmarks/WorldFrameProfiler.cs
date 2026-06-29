using System.Diagnostics;
using UnityEngine;

namespace Benchmarks
{
    /// <summary>
    /// Opt-in, <see cref="Stopwatch"/>-based sub-phase profiler that slices the <b>interior of
    /// <c>World.Update</c></b> into the four main-thread cost centers a fluid edit drives:
    /// the behavior <see cref="Phase.Tick"/>, the modification <see cref="Phase.Apply"/> drain, the
    /// main-thread <see cref="Phase.Mesh"/> work (mesh-job process + schedule + <c>CreateMesh</c>), and the
    /// main-thread <see cref="Phase.Light"/> work (lighting-job process + schedule).
    /// <para>
    /// This is the measurement the <b>isolated</b> tick benchmark could not provide: those cost centers are
    /// private methods callable only from <c>World.Update</c>, so attributing the <i>real</i> ocean frame
    /// (tick vs the mesh-rebuild it triggers vs lighting) requires timing them in place. It is consumed by the
    /// full-world fluid stress pass (<c>FluidStressController</c>), which flips <see cref="Enabled"/> on for the
    /// duration of a capture.
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
        /// <summary>The four <c>World.Update</c> cost centers this profiler attributes.</summary>
        public enum Phase
        {
            /// <summary>The behavior tick (<c>ProcessTickUpdates</c> → grass/fluid <c>Chunk.TickUpdate</c>).</summary>
            Tick = 0,

            /// <summary>The voxel-modification drain (<c>World.ApplyModifications</c>).</summary>
            Apply = 1,

            /// <summary>Main-thread mesh work: mesh-job process + schedule + <c>CreateMesh</c> upload.</summary>
            Mesh = 2,

            /// <summary>Main-thread lighting work: lighting-job process + dirty-set schedule.</summary>
            Light = 3,
        }

        private const int PHASE_COUNT = 4;

        /// <summary>
        /// When <c>false</c> (default) every method is a no-op guarded by a single bool read, so production
        /// frames pay nothing. Only the full-world fluid stress pass flips this on for a capture.
        /// </summary>
        public static bool Enabled;

        private static readonly double s_tickToMs = 1000.0 / Stopwatch.Frequency;

        /// <summary>Per-frame accumulated stopwatch ticks, one slot per <see cref="Phase"/> (reset each <see cref="BeginFrame"/>).</summary>
        private static readonly long[] s_frameTicks = new long[PHASE_COUNT];

        /// <summary>Milliseconds spent in <see cref="Phase.Tick"/> during the frame most recently closed by <see cref="EndFrame"/>.</summary>
        public static double LastFrameTickMs { get; private set; }

        /// <summary>Milliseconds spent in <see cref="Phase.Apply"/> during the frame most recently closed by <see cref="EndFrame"/>.</summary>
        public static double LastFrameApplyMs { get; private set; }

        /// <summary>Milliseconds spent in <see cref="Phase.Mesh"/> during the frame most recently closed by <see cref="EndFrame"/>.</summary>
        public static double LastFrameMeshMs { get; private set; }

        /// <summary>Milliseconds spent in <see cref="Phase.Light"/> during the frame most recently closed by <see cref="EndFrame"/>.</summary>
        public static double LastFrameLightMs { get; private set; }

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
            LastFrameTickMs = 0;
            LastFrameApplyMs = 0;
            LastFrameMeshMs = 0;
            LastFrameLightMs = 0;

            for (int i = 0; i < PHASE_COUNT; i++)
                s_frameTicks[i] = 0;
        }

        /// <summary>
        /// Resets the per-frame accumulators. Called once at the top of the <c>World.Update</c> body, before any
        /// timed region. No-op when <see cref="Enabled"/> is <c>false</c>.
        /// </summary>
        public static void BeginFrame()
        {
            if (!Enabled) return;

            for (int i = 0; i < PHASE_COUNT; i++)
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

            LastFrameTickMs = s_frameTicks[(int)Phase.Tick] * s_tickToMs;
            LastFrameApplyMs = s_frameTicks[(int)Phase.Apply] * s_tickToMs;
            LastFrameMeshMs = s_frameTicks[(int)Phase.Mesh] * s_tickToMs;
            LastFrameLightMs = s_frameTicks[(int)Phase.Light] * s_tickToMs;
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
