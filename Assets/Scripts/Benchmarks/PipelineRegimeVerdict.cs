using Helpers;

namespace Benchmarks
{
    /// <summary>Which regime a capture's numbers indicate (FLIGHT_PROFILE_CAPTURE.md §7.1).</summary>
    public enum PipelineRegime : byte
    {
        /// <summary>No capture data — the phase recorded no pass stops at all.</summary>
        NoData = 0,

        /// <summary>The pipeline kept up: the passes mostly ran out of work rather than out of budget.</summary>
        Healthy = 1,

        /// <summary>Throttled by design — the P-4 rate quota or ms ceiling is the binding constraint.</summary>
        AdmissionBound = 2,

        /// <summary>The in-flight job cap binds: jobs are not completing fast enough to free slots.</summary>
        ThroughputBound = 3,

        /// <summary>Queues hold work that no readiness gate will admit — an upstream stall.</summary>
        ReadinessBound = 4,
    }

    /// <summary>
    /// The verdict for one capture phase: the primary regime from the stop-reason plurality, plus the
    /// separately-measured ordering axis, plus the inputs that produced them.
    /// </summary>
    public readonly struct RegimeVerdict
    {
        /// <summary>The regime indicated by the dominant stop reason.</summary>
        public readonly PipelineRegime Primary;

        /// <summary>
        /// Whether the capture is <b>also</b> ordering-bound. A separate axis by design (§7.1): ordering is a
        /// property of <i>which</i> chunks were served, not of why a pass stopped, so it can co-occur with any
        /// primary regime — including <see cref="PipelineRegime.Healthy"/>, which is the specific case the
        /// flight symptom is most likely to look like.
        /// </summary>
        public readonly bool OrderingBound;

        /// <summary>The plurality stop reason across all budgeted passes (excluding the untallied sentinel).</summary>
        public readonly PassStopReason DominantReason;

        /// <summary>The runner-up stop reason — reported so a near-tie is visible rather than hidden.</summary>
        public readonly PassStopReason RunnerUpReason;

        /// <summary>Fraction of traces that ended as waste, in [0, 1].</summary>
        public readonly double WasteFraction;

        /// <summary>Initializes a verdict.</summary>
        /// <param name="primary">The regime from the dominant stop reason.</param>
        /// <param name="orderingBound">Whether the ordering axis also fired.</param>
        /// <param name="dominantReason">The plurality stop reason.</param>
        /// <param name="runnerUpReason">The second-place stop reason.</param>
        /// <param name="wasteFraction">Fraction of traces that ended as waste.</param>
        public RegimeVerdict(PipelineRegime primary, bool orderingBound, PassStopReason dominantReason,
            PassStopReason runnerUpReason, double wasteFraction)
        {
            Primary = primary;
            OrderingBound = orderingBound;
            DominantReason = dominantReason;
            RunnerUpReason = runnerUpReason;
            WasteFraction = wasteFraction;
        }
    }

    /// <summary>
    /// The §7.1 verdict rule: pure arithmetic turning a phase's stop-reason tallies and waste counts into an
    /// explicit regime. Fixed <b>before</b> any capture existed so a result can never be fitted to a rule,
    /// and pure so the validation suite pins it — a rule that silently changes meaning between captures would
    /// invalidate every comparison against an earlier report.
    /// <para>
    /// This is a <i>convenience over</i> the raw numbers, never a replacement for them: §7.2 requires every
    /// report to carry the full tallies and distributions this consumed, so a later session can apply a
    /// different rule to the same data without re-running the capture.
    /// </para>
    /// </summary>
    public static class PipelineRegimeVerdict
    {
        /// <summary>
        /// Waste fraction at or above which a phase is called ordering-bound. Pre-committed at 20 %: waste
        /// here means work the pipeline <i>completed</i> for chunks that then left range
        /// (<c>DiscardedOutOfRange</c> / <c>LoadStranded</c> / <c>UnloadedBeforeMeshApplied</c>), so it is a
        /// direct measure of "serving chunks the player has already flown past" rather than a proxy.
        /// One in five is well clear of the incidental churn a turning flight path produces, while still
        /// firing long before the pipeline is spending most of its budget on discarded work.
        /// </summary>
        public const double OrderingWasteThreshold = 0.20;

        /// <summary>
        /// Applies the rule. <paramref name="stopReasonTallies"/> is the phase's
        /// <c>[pass, reason]</c> matrix; reasons are summed across passes because the question is which
        /// constraint bound the <i>pipeline</i>, not which bound one stage.
        /// </summary>
        /// <param name="stopReasonTallies">Per-pass, per-reason frame counts.</param>
        /// <param name="wasteTraces">Traces that ended in a waste disposition.</param>
        /// <param name="terminalTraces">Traces that reached any terminal disposition (the waste denominator).</param>
        /// <returns>The verdict, including the inputs that produced it.</returns>
        public static RegimeVerdict Evaluate(int[,] stopReasonTallies, int wasteTraces, int terminalTraces)
        {
            // Sum each reason across passes. NotRun is skipped: it is the "did not execute" sentinel, not an
            // outcome, and letting it win a plurality would report an idle editor frame as a regime.
            int reasonCount = stopReasonTallies.GetLength(1);
            int passCount = stopReasonTallies.GetLength(0);

            int dominantIndex = -1, runnerUpIndex = -1;
            int dominantTotal = 0, runnerUpTotal = 0;
            int grandTotal = 0;

            for (int reason = (int)PassStopReason.OutOfWork; reason < reasonCount; reason++)
            {
                int total = 0;
                for (int pass = 0; pass < passCount; pass++) total += stopReasonTallies[pass, reason];

                grandTotal += total;

                if (total > dominantTotal)
                {
                    runnerUpIndex = dominantIndex;
                    runnerUpTotal = dominantTotal;
                    dominantIndex = reason;
                    dominantTotal = total;
                }
                else if (total > runnerUpTotal)
                {
                    runnerUpIndex = reason;
                    runnerUpTotal = total;
                }
            }

            double wasteFraction = terminalTraces > 0 ? (double)wasteTraces / terminalTraces : 0.0;
            bool orderingBound = wasteFraction >= OrderingWasteThreshold;

            if (grandTotal == 0 || dominantIndex < 0)
                return new RegimeVerdict(PipelineRegime.NoData, orderingBound,
                    PassStopReason.NotRun, PassStopReason.NotRun, wasteFraction);

            PassStopReason dominant = (PassStopReason)dominantIndex;
            PassStopReason runnerUp = runnerUpIndex < 0 ? PassStopReason.NotRun : (PassStopReason)runnerUpIndex;

            PipelineRegime primary = dominant switch
            {
                // Throttled by the P-4 budgets — the knobs are the binding constraint, by design.
                PassStopReason.Quota => PipelineRegime.AdmissionBound,
                PassStopReason.Ceiling => PipelineRegime.AdmissionBound,

                // The memory bound binds: slots are not freeing up, i.e. the jobs themselves are the limit.
                PassStopReason.InFlightCap => PipelineRegime.ThroughputBound,

                // Work exists that no gate will admit.
                PassStopReason.AllDeclined => PipelineRegime.ReadinessBound,

                _ => PipelineRegime.Healthy,
            };

            return new RegimeVerdict(primary, orderingBound, dominant, runnerUp, wasteFraction);
        }
    }
}
