using System.Diagnostics;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Helpers
{
    /// <summary>
    /// Pure frame-budget math for the per-frame pipeline passes (P-4 §3.4). Two cooperating pieces:
    /// a rate <b>quota</b> that scales an existing per-frame count cap by the frame's real duration
    /// (so pipeline throughput per <i>second</i> stays roughly constant instead of collapsing with
    /// FPS — the §3 death-spiral inversion), and a Stopwatch-based <b>window</b> ceiling that bounds
    /// how much main-thread time a pass may spend regardless of the quota (hitch guard). A pass
    /// breaks when either limit is hit; the un-served remainder keeps today's count-break semantics
    /// (lighting: stays in the READY set; meshing: stays queued; completions: stay enrolled).
    /// All math is pure/static so the "Pipeline Backpressure" suite truth-table-tests it
    /// (the <see cref="PoolPruneDecision"/> pattern).
    /// </summary>
    public static class PipelinePassBudget
    {
        /// <summary>The frame rate at which a quota equals its per-frame cap exactly (the caps' historical tuning anchor).</summary>
        public const float ReferenceFps = 60f;

        // Guards the ceil against float noise at the reference point: 32 × (1/60f) × 60f lands a hair
        // above 32.0 in float math and would ceil to 33 on a perfect 60 FPS frame without it.
        private const float QUOTA_EPSILON = 1e-3f;

        // A hitch frame (teleport arrival, load stall) must not translate into an unbounded quota —
        // the window ceiling is the primary guard, this caps the quota itself as defense in depth.
        private const int MAX_QUOTA_SCALE = 8;

        /// <summary>
        /// Scales a per-frame count cap by the frame's duration so the implied per-second rate
        /// (<paramref name="capPerFrameAt60"/> × <see cref="ReferenceFps"/>) holds at any FPS.
        /// On a perfect 60 FPS frame this returns the cap unchanged.
        /// </summary>
        /// <param name="capPerFrameAt60">The per-frame cap, tuned at the 60 FPS reference (e.g. <c>maxLightJobsPerFrame</c>).</param>
        /// <param name="unscaledDeltaTime">The frame's real duration in seconds (unscaled — budgets must not stall with the time scale).</param>
        /// <returns>The item quota for this frame, in [1, cap × 8].</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ComputeQuota(int capPerFrameAt60, float unscaledDeltaTime)
        {
            if (capPerFrameAt60 < 1) capPerFrameAt60 = 1;
            if (unscaledDeltaTime <= 0f) return capPerFrameAt60; // No frame duration yet — legacy per-frame behavior.

            int quota = Mathf.CeilToInt(capPerFrameAt60 * unscaledDeltaTime * ReferenceFps - QUOTA_EPSILON);
            return Mathf.Clamp(quota, 1, capPerFrameAt60 * MAX_QUOTA_SCALE);
        }

        /// <summary>Converts a millisecond budget into raw <see cref="Stopwatch"/> ticks (0 or negative ms → 0 = no ceiling).</summary>
        /// <param name="budgetMs">The budget in milliseconds.</param>
        /// <returns>The budget in Stopwatch ticks, or 0 when the ceiling is disabled.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long TicksForMs(float budgetMs)
        {
            return budgetMs <= 0f ? 0L : (long)(budgetMs * (Stopwatch.Frequency / 1000.0));
        }

        /// <summary>
        /// The pure expiry predicate: a window with no budget (0 ticks) never expires — this is what
        /// makes <c>default(Window)</c> the "unbudgeted" value pass parameters rely on.
        /// </summary>
        /// <param name="elapsedTicks">Stopwatch ticks elapsed since the window started.</param>
        /// <param name="budgetTicks">The window's budget in Stopwatch ticks (0 = no ceiling).</param>
        /// <returns>True when a positive budget has been fully spent.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsExpired(long elapsedTicks, long budgetTicks)
        {
            return budgetTicks > 0 && elapsedTicks >= budgetTicks;
        }

        /// <summary>Starts a budget window ending <paramref name="budgetMs"/> from now (≤ 0 ms → an unbudgeted window).</summary>
        /// <param name="budgetMs">The time ceiling in milliseconds.</param>
        /// <returns>The running window.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Window StartWindow(float budgetMs)
        {
            return new Window(Stopwatch.GetTimestamp(), TicksForMs(budgetMs));
        }

        /// <summary>
        /// A zero-alloc time ceiling over a pipeline pass. <c>default(Window)</c> carries no budget and
        /// never expires, so pass methods can take <c>Window window = default</c> and stay unbudgeted
        /// for callers that opt out (the startup coroutine, benchmarks).
        /// </summary>
        public readonly struct Window
        {
            private readonly long _startTimestamp;
            private readonly long _budgetTicks;

            /// <summary>Initializes a window from a start timestamp and a tick budget (0 = no ceiling).</summary>
            /// <param name="startTimestamp">The <see cref="Stopwatch.GetTimestamp"/> value at window start.</param>
            /// <param name="budgetTicks">The budget in Stopwatch ticks (0 = no ceiling).</param>
            public Window(long startTimestamp, long budgetTicks)
            {
                _startTimestamp = startTimestamp;
                _budgetTicks = budgetTicks;
            }

            /// <summary>Whether this window carries a time ceiling at all.</summary>
            public bool HasBudget => _budgetTicks > 0;

            /// <summary>
            /// Whether the window's ceiling has been reached (always false without a budget). The
            /// zero-budget short-circuit precedes the <see cref="Stopwatch.GetTimestamp"/> read so
            /// unbudgeted windows in hot loops (the flag-off legacy legs, the startup coroutine) pay
            /// no per-iteration timer call.
            /// </summary>
            public bool Expired => _budgetTicks > 0
                                   && IsExpired(Stopwatch.GetTimestamp() - _startTimestamp, _budgetTicks);
        }
    }
}
