using System.Runtime.CompilerServices;

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
