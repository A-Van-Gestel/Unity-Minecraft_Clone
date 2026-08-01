using System.Runtime.CompilerServices;
using UnityEngine;

namespace Helpers
{
    /// <summary>
    /// Pure hysteresis decision for the generation panic gate (P-4 §3.5). When the downstream
    /// lighting backlog (the <see cref="LightWorkScheduler"/> ready count — generation itself is
    /// already capped by §3.1, so the schedulable lighting queue is the live overload signal)
    /// exceeds the close threshold, generation <i>admissions</i> pause entirely until the backlog
    /// drains below the reopen threshold. The two thresholds form the hysteresis band that stops the
    /// gate from oscillating at a single boundary. The gate only ever withholds admissions at the
    /// <c>World.DrainGenerationRequests</c> seam — the request queue itself is untouched, so a closed
    /// gate can never strand holes (the §3.1 spiral-break lesson). Pure so the "Pipeline
    /// Backpressure" suite truth-table-tests it (the <see cref="ChunkUnloadDecision"/> pattern).
    /// </summary>
    public static class GenerationPanicGate
    {
        /// <summary>
        /// Resident-square width (in chunks) the configured thresholds are stated at — the default view
        /// distance 5, whose load distance 8 gives <c>2 × 8 + 1</c>. P-8's scaling is an identity here, so a
        /// default configuration behaves exactly as it did before the feature, and the two Settings fields
        /// keep meaning what they always meant at the view distance they were tuned for.
        /// </summary>
        public const int ReferenceResidentWidth = 17;

        /// <summary>The gate's evaluation outcome — the two steady states plus the two loggable transitions.</summary>
        public enum Decision : byte
        {
            /// <summary>Open and staying open — backlog below the close threshold.</summary>
            RemainOpen,

            /// <summary>Transition: the backlog reached the close threshold — stop admitting.</summary>
            Close,

            /// <summary>Closed and staying closed — backlog still above the reopen threshold.</summary>
            RemainClosed,

            /// <summary>Transition: the backlog drained to the reopen threshold — resume admitting.</summary>
            Reopen,
        }

        /// <summary>
        /// Evaluates the gate for this frame. Callers should configure
        /// <paramref name="reopenAt"/> &lt; <paramref name="closeAt"/>; a degenerate band still
        /// resolves (the closed arm is evaluated from the closed state only), it just loses its
        /// oscillation damping.
        /// </summary>
        /// <param name="isOpen">Whether the gate is currently open.</param>
        /// <param name="backlog">The backlog signal (lighting ready count).</param>
        /// <param name="closeAt">Backlog level at which an open gate closes.</param>
        /// <param name="reopenAt">Backlog level at or below which a closed gate reopens.</param>
        /// <returns>The decision, including transition arms for logging.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Decision Evaluate(bool isOpen, int backlog, int closeAt, int reopenAt)
        {
            if (isOpen)
                return backlog >= closeAt ? Decision.Close : Decision.RemainOpen;

            return backlog <= reopenAt ? Decision.Reopen : Decision.RemainClosed;
        }

        /// <summary>
        /// Derives the thresholds the gate is actually evaluated against, scaling them with the resident
        /// square when P-8's scaling is enabled and sanitizing them either way (P-8).
        /// </summary>
        /// <param name="residentWidth">Resident load-square side in chunks (<c>2 × LoadDistance + 1</c>).</param>
        /// <param name="configuredClose">The persisted close threshold, stated at the reference width.</param>
        /// <param name="configuredReopen">The persisted reopen threshold, stated at the reference width.</param>
        /// <param name="scaleWithResidency">Whether to scale with the resident square; false is the rollback leg.</param>
        /// <param name="closeAt">Backlog level at which an open gate closes.</param>
        /// <param name="reopenAt">Backlog level at or below which a closed gate reopens.</param>
        /// <remarks>
        /// <b>Scaling is OFF by default and this method is dormant in the shipping configuration.</b> Its
        /// capture came back NO-GO: the backlog grows to meet whatever threshold it is given, so at view
        /// distance 32 a 4.2× threshold moved gate closure by 0.1 points while completions fell 16 %, and
        /// loading-pass minimum FPS dropped roughly a third. The derivation is kept — with its guard
        /// (baseline B19) — because the fix is premature rather than wrong: the binding constraint is the
        /// lighting/mesh schedule <c>Quota</c>, and once that ceiling moves the gate becomes binding again
        /// and this is the right shape. See
        /// <c>Documentation/Performance/CHUNK_PIPELINE_P8_GATE_SCALING_IL2CPP_2026-08-01_BENCHMARK.md</c>.
        /// <para>
        /// <b>Why scale, and why linearly in the square's width</b> (the original argument, retained for the
        /// re-test). The thresholds are counts of backlogged
        /// chunks, but the population they guard is the resident square, which grows as
        /// <c>(2 × LoadDistance + 1)²</c>. A fixed 256 is therefore 88.6 % of residency at view distance 5 and
        /// 5.1 % at view distance 32 — an unreachable emergency brake at the default and a near-permanent
        /// throttle at the top, which is what held admitted work to 1.5–1.7× growth while requests grew
        /// 4.5–4.8× across FP-10's sweep. Scaling with the square's <i>width</i> rather than its area is the
        /// deliberate middle: it loosens the gate substantially at high view distance (×4.2 at vd 32) while
        /// keeping it reachable, because the gate is simultaneously succeeding at the other half of its job —
        /// protecting frame time — and an area-proportional threshold would reproduce vd 5's never-closes
        /// behavior everywhere and trade that away.
        /// </para>
        /// <para>
        /// The backlog signal is <c>LightWorkScheduler.ReadyCount</c>, whose entries are removed on unload and
        /// on work completion, so it tracks resident chunks — bounded by residency apart from a transient
        /// stale tail after a mass unload that the next scan launders. A residency-proportional threshold is
        /// therefore reachable in principle at every view distance, which an absolute one is not.
        /// </para>
        /// <para>
        /// Sanitization lives here rather than at the call site so the value the gate uses and the value the
        /// benchmark report prints cannot diverge: <paramref name="closeAt"/> is floored at 1, and
        /// <paramref name="reopenAt"/> clamped to <c>[0, closeAt - 1]</c>. A degenerate band (reopen ≥ close)
        /// would flip the gate every frame — halving admissions and spamming two interpolated log strings per
        /// flip inside <c>Update</c> — and a negative reopen could never be reached by a non-negative backlog,
        /// wedging a closed gate shut forever.
        /// </para>
        /// </remarks>
        public static void DeriveThresholds(int residentWidth, int configuredClose, int configuredReopen,
            bool scaleWithResidency, out int closeAt, out int reopenAt)
        {
            int close = configuredClose;
            int reopen = configuredReopen;

            if (scaleWithResidency)
            {
                int width = Mathf.Max(1, residentWidth);
                close = Scale(close, width);
                reopen = Scale(reopen, width);
            }

            closeAt = Mathf.Max(1, close);
            reopenAt = Mathf.Clamp(reopen, 0, closeAt - 1);
        }

        /// <summary>
        /// Scales one threshold from the reference width to this run's, rounded to nearest.
        /// </summary>
        /// <param name="configured">The threshold as persisted, stated at <see cref="ReferenceResidentWidth"/>.</param>
        /// <param name="residentWidth">This run's resident square side, in chunks (already floored at 1).</param>
        /// <returns>The scaled threshold, saturated into <see cref="int"/> range.</returns>
        /// <remarks>
        /// Widened to <see cref="long"/> before multiplying: an absurd persisted threshold times a large width
        /// overflows <see cref="int"/>, and a wrapped negative product would sanitize into a gate that is
        /// closed forever. Rounded rather than truncated so the scale is symmetric about the reference — plain
        /// integer division biases every non-default view distance downward, i.e. always toward a tighter gate,
        /// which is the direction P-8 exists to correct.
        /// </remarks>
        private static int Scale(int configured, int residentWidth)
        {
            long numerator = (long)configured * residentWidth;
            const long half = ReferenceResidentWidth / 2;

            // Round half away from zero; a negative configured value is nonsense but must not round the
            // wrong way into the sanitizing clamp below.
            long rounded = numerator >= 0
                ? (numerator + half) / ReferenceResidentWidth
                : (numerator - half) / ReferenceResidentWidth;

            if (rounded > int.MaxValue) return int.MaxValue;
            if (rounded < int.MinValue) return int.MinValue;
            return (int)rounded;
        }

        /// <summary>Whether the gate is open after applying a decision (admissions may proceed).</summary>
        /// <param name="decision">The decision returned by <see cref="Evaluate"/>.</param>
        /// <returns>True for the open-side arms.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsOpenAfter(Decision decision)
        {
            return decision == Decision.RemainOpen || decision == Decision.Reopen;
        }
    }
}
