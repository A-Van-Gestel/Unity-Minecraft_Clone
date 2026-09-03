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

        /// <summary>
        /// Whether the ordering axis had enough terminal traces to decide at all
        /// (<see cref="PipelineRegimeVerdict.MinOrderingTerminalTraces"/>). When <c>false</c>,
        /// <see cref="OrderingBound"/> is <c>false</c> because the question was unanswerable, <b>not</b>
        /// because the pipeline was well-ordered — the report must render those differently.
        /// </summary>
        public readonly bool OrderingDecidable;

        /// <summary>The plurality stop reason across all budgeted passes (excluding the untallied sentinel).</summary>
        public readonly PassStopReason DominantReason;

        /// <summary>The runner-up stop reason — reported so a near-tie is visible rather than hidden.</summary>
        public readonly PassStopReason RunnerUpReason;

        /// <summary>Fraction of traces that ended as waste, in [0, 1].</summary>
        public readonly double WasteFraction;

        /// <summary>
        /// The dominant reason's capability-weighted share, in [0, 1] — its frames over the frames in which
        /// a pass able to report it could have. Printed so a near-tie is auditable as a number, not implied.
        /// </summary>
        public readonly double DominantShare;

        /// <summary>The runner-up's capability-weighted share, on the same scale.</summary>
        public readonly double RunnerUpShare;

        /// <summary>
        /// Whether the phase carried enough eligible pass-reports to decide a regime at all
        /// (<see cref="PipelineRegimeVerdict.MinRegimeObservations"/>). When <c>false</c>,
        /// <see cref="Primary"/> is <see cref="PipelineRegime.NoData"/> because the question was
        /// unanswerable — <b>not</b> because the pipeline was idle. The ordering axis's exact counterpart
        /// (FP-9a); FP-8 printed <c>ThroughputBound</c> from a 14-frame phase for want of this.
        /// </summary>
        public readonly bool PrimaryDecidable;

        /// <summary>
        /// Eligible pass-reports the plurality was computed from — the sample size behind
        /// <see cref="PrimaryDecidable"/>. Printed as a verdict input (§7.2) and the only thing that
        /// distinguishes "nothing was recorded" (0) from "too little was recorded" (below the floor).
        /// </summary>
        public readonly int EligibleObservations;

        /// <summary>Initializes a verdict.</summary>
        /// <param name="primary">The regime from the dominant stop reason.</param>
        /// <param name="orderingBound">Whether the ordering axis also fired.</param>
        /// <param name="orderingDecidable">Whether the ordering axis had enough terminal traces to decide.</param>
        /// <param name="dominantReason">The plurality stop reason.</param>
        /// <param name="runnerUpReason">The second-place stop reason.</param>
        /// <param name="wasteFraction">Fraction of traces that ended as waste.</param>
        /// <param name="dominantShare">The dominant reason's capability-weighted share.</param>
        /// <param name="runnerUpShare">The runner-up's capability-weighted share.</param>
        /// <param name="primaryDecidable">Whether the sample supported deciding a regime.</param>
        /// <param name="eligibleObservations">Eligible pass-reports behind the plurality.</param>
        public RegimeVerdict(PipelineRegime primary, bool orderingBound, bool orderingDecidable,
            PassStopReason dominantReason, PassStopReason runnerUpReason, double wasteFraction,
            double dominantShare, double runnerUpShare, bool primaryDecidable, int eligibleObservations)
        {
            Primary = primary;
            OrderingBound = orderingBound;
            OrderingDecidable = orderingDecidable;
            DominantReason = dominantReason;
            RunnerUpReason = runnerUpReason;
            WasteFraction = wasteFraction;
            DominantShare = dominantShare;
            RunnerUpShare = runnerUpShare;
            PrimaryDecidable = primaryDecidable;
            EligibleObservations = eligibleObservations;
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
        /// Which stop reasons each pass is <i>capable</i> of emitting (§7.1 v2). The v1 rule summed every
        /// reason across all four passes, so passes that can vote for only one outcome were added to passes
        /// genuinely contesting all five — and reliably contributed ~100 % <c>OutOfWork</c>. At FP-4's
        /// loading 200 m/s phase that dilution decided the plurality by <b>68 frames out of 27,744</b> and
        /// printed <i>Healthy</i> over what the eligible passes alone called <c>Quota</c> at 99.5 %.
        /// <para>
        /// Declared as data rather than hardcoded as "the scheduling passes", because the split is not
        /// scheduling-vs-completion: <see cref="PipelinePass.GenerationProcess"/> owns a real per-frame
        /// structure-mods quota (FP-7b), and only <see cref="PipelinePass.MeshProcess"/> is genuinely
        /// ceiling-only. A hand-written capability claim is exactly what went stale before, so
        /// <see cref="CanEmit"/> is asserted against reality at every
        /// <see cref="PipelineTelemetry.RecordPassStop"/> in development builds.
        /// </para>
        /// </summary>
        /// <param name="pass">The pass to test.</param>
        /// <param name="reason">The stop reason to test.</param>
        /// <returns><c>true</c> when <paramref name="pass"/> can legitimately report <paramref name="reason"/>.</returns>
        public static bool CanEmit(PipelinePass pass, PassStopReason reason)
        {
            // NotRun is a sentinel, never an outcome, and is excluded from the plurality entirely.
            if (reason == PassStopReason.NotRun) return false;

            // Every pass that runs can finish its queue or hit its ms ceiling.
            if (reason == PassStopReason.OutOfWork || reason == PassStopReason.Ceiling) return true;

            return pass switch
            {
                // The two scheduling loops carry a job quota, an in-flight cap, and a readiness gate.
                PipelinePass.LightSchedule => true,
                PipelinePass.MeshSchedule => true,

                // Completion pass with a quota (structure mods) but no in-flight cap of its own, and no
                // readiness gate — reaching a completed job always processes or quota-defers it (FP-7b).
                PipelinePass.GenerationProcess => reason == PassStopReason.Quota,

                // MeshProcess: genuinely ceiling-only, so it votes on nothing beyond the two above.
                _ => false,
            };
        }

        /// <summary>
        /// How many stop reasons a pass actually reported across the phase — its contribution to the v2
        /// denominator. Summed over the reasons it is <see cref="CanEmit"/>-eligible for, so a stale-matrix
        /// cell cannot inflate the denominator while being excluded from the numerator.
        /// </summary>
        /// <param name="stopReasonTallies">The phase's <c>[pass, reason]</c> matrix.</param>
        /// <param name="pass">The pass index to total.</param>
        /// <returns>The number of frames in which that pass reported an eligible reason.</returns>
        private static int Participation(int[,] stopReasonTallies, int pass)
        {
            int reasonCount = stopReasonTallies.GetLength(1);
            int participation = 0;

            for (int reason = (int)PassStopReason.OutOfWork; reason < reasonCount; reason++)
            {
                if (CanEmit((PipelinePass)pass, (PassStopReason)reason))
                    participation += stopReasonTallies[pass, reason];
            }

            return participation;
        }

        /// <summary>
        /// Tie-break for two reasons with an exactly equal share: a reason indicating a <i>bound</i> pipeline
        /// outranks <see cref="PassStopReason.OutOfWork"/>.
        /// </summary>
        /// <param name="challenger">The reason currently being scored.</param>
        /// <param name="incumbentIndex">The leading reason's index, or negative when none has been chosen.</param>
        /// <returns><c>true</c> when the challenger should take the lead despite not exceeding its share.</returns>
        /// <remarks>
        /// Without this the loop's walk order (<c>OutOfWork</c> first) plus a strict <c>&gt;</c> resolves every
        /// tie toward the "everything is fine" arm — a bias an instrument built to detect stalls should not
        /// have. Exact ties are reachable because shares are ratios over differing denominators (200⁄400 and
        /// 150⁄300 are both exactly 0.5). Ties between two <i>bound</i> reasons have no principled ordering,
        /// so they keep the deterministic walk order and are visible as near-equal
        /// <see cref="RegimeVerdict.DominantShare"/> / <see cref="RegimeVerdict.RunnerUpShare"/> values.
        /// </remarks>
        private static bool OutranksOnTie(int challenger, int incumbentIndex)
        {
            return incumbentIndex == (int)PassStopReason.OutOfWork
                   && challenger != (int)PassStopReason.OutOfWork;
        }

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
        /// Terminal traces required before the ordering axis returns a verdict at all. Below it the fraction
        /// is arithmetically fine but statistically meaningless — 1 waste of 3 terminal traces is 33 %, over
        /// the threshold, off a sample of three. 30 is the conventional small-sample floor and is deliberately
        /// far below any real phase (FP-4's ran from hundreds to thousands), so it gates truncated or
        /// near-empty phases without ever suppressing a genuine capture.
        /// </summary>
        public const int MinOrderingTerminalTraces = 30;

        /// <summary>
        /// Eligible pass-reports required before a <i>primary regime</i> is asserted at all. The ordering
        /// axis's counterpart, added because FP-8 showed the plurality had no such guard: a 14-frame
        /// generation phase printed <c>ThroughputBound</c> off 56 observations, and a 148-frame phase printed
        /// <c>AdmissionBound</c> off 592 — both truncated routes, neither a result.
        /// <para>
        /// Measured in eligible <i>observations</i> rather than frames because that is the unit
        /// <see cref="Evaluate"/> actually consumes, so the guard cannot drift from the quantity it guards.
        /// 1 000 sits an order of magnitude clear on both sides of the FP-8 evidence — it rejects 56 and 592
        /// while the smallest legitimate phase in that capture carried ~13 600.
        /// </para>
        /// </summary>
        public const int MinRegimeObservations = 1000;

        /// <summary>
        /// Whether a disposition represents work the pipeline <i>completed</i> and then threw away — the
        /// ordering axis's numerator.
        /// </summary>
        /// <param name="disposition">The terminal disposition to classify.</param>
        /// <returns><c>true</c> when the disposition counts as waste.</returns>
        /// <remarks>
        /// <see cref="TraceDisposition.InFlightAtPhaseEnd"/> is deliberately not waste — the capture stopped
        /// first, the pipeline did nothing wrong. <see cref="TraceDisposition.Rerequested"/> is not waste
        /// either: it counts churn, and its work may still land.
        /// <see cref="TraceDisposition.AbandonedBeforeAdmission"/> is not waste because no work was ever
        /// performed (FP-7a); it is excluded from the denominator too — see
        /// <see cref="IsInWasteDenominator"/>. Lives here rather than on the report section so the verdict
        /// and the table it is printed under can never classify a disposition differently (the FP-5 lesson).
        /// </remarks>
        public static bool IsWaste(TraceDisposition disposition)
        {
            return disposition == TraceDisposition.DiscardedOutOfRange
                   || disposition == TraceDisposition.LoadStranded
                   || disposition == TraceDisposition.UnloadedBeforeMeshApplied;
        }

        /// <summary>
        /// Whether a disposition belongs in the waste fraction's <i>denominator</i> — the population of
        /// traces for which the pipeline actually performed work and reached a terminal state.
        /// </summary>
        /// <param name="disposition">The disposition to classify.</param>
        /// <returns><c>true</c> when the disposition contributes to the denominator.</returns>
        /// <remarks>
        /// Excludes <see cref="TraceDisposition.Pending"/> (not terminal) and
        /// <see cref="TraceDisposition.AbandonedBeforeAdmission"/> (terminal, but the pipeline never touched
        /// it — counting it would deflate the fraction hardest exactly where the panic gate withholds the
        /// most admissions, which is the regime the ordering axis must stay readable in).
        /// </remarks>
        public static bool IsInWasteDenominator(TraceDisposition disposition)
        {
            return disposition != TraceDisposition.Pending
                   && disposition != TraceDisposition.AbandonedBeforeAdmission;
        }

        /// <summary>
        /// Applies the §7.1 <b>v2</b> rule. <paramref name="stopReasonTallies"/> is the phase's
        /// <c>[pass, reason]</c> matrix. Each reason is scored as its share of the reports actually made by
        /// the passes <see cref="CanEmit"/> admits for it — numerator and denominator are both drawn from
        /// those passes only, so a pass can never vote on an outcome it cannot express (§7.1.1).
        /// </summary>
        /// <param name="stopReasonTallies">Per-pass, per-reason frame counts.</param>
        /// <param name="wasteTraces">Traces that ended in a waste disposition.</param>
        /// <param name="terminalTraces">Traces that reached any terminal disposition (the waste denominator).</param>
        /// <returns>The verdict, including the inputs that produced it.</returns>
        /// <remarks>
        /// <b>The denominator is measured participation, not nominal opportunity.</b> An earlier draft divided
        /// by <c>frameCount × eligible passes</c>, which charges a full phase of chances to passes that never
        /// ran — <see cref="PipelinePass.LightSchedule"/> is inside <c>if (settings.enableLighting)</c>, so a
        /// lighting-off capture gave it a silent zero vote in every reason while still paying for its slot.
        /// That capped <c>Quota</c> at 2⁄3 against <c>OutOfWork</c>'s 3⁄4 and printed <i>Healthy</i> over a
        /// genuine quota stall — the §7.1.1 dilution, rebuilt in a new place. Summing each eligible pass's own
        /// reports instead makes an absent pass contribute nothing to either term.
        /// <para>
        /// Participation is derived from the matrix rather than counted separately, so it cannot desync from
        /// the numerator it divides (the FP-5 lesson), and is filtered by <see cref="CanEmit"/> for the same
        /// reason the numerator is: an ineligible non-zero cell must not inflate the denominator while being
        /// excluded from the numerator. It assumes <b>one report per pass per frame</b> — true of every
        /// current call site, and guarded by an assert in <see cref="PipelineTelemetry.RecordPassStop"/>
        /// rather than left to hold by accident.
        /// </para>
        /// </remarks>
        public static RegimeVerdict Evaluate(int[,] stopReasonTallies, int wasteTraces, int terminalTraces)
        {
            int reasonCount = stopReasonTallies.GetLength(1);
            int passCount = stopReasonTallies.GetLength(0);

            int dominantIndex = -1, runnerUpIndex = -1;
            double dominantShare = 0.0, runnerUpShare = 0.0;
            int eligibleTotal = 0;

            for (int reason = (int)PassStopReason.OutOfWork; reason < reasonCount; reason++)
            {
                int total = 0, participation = 0;
                for (int pass = 0; pass < passCount; pass++)
                {
                    // Ineligible cells are ignored, not merely down-weighted. A non-zero one means the
                    // capability declaration has gone stale — RecordPassStop asserts against exactly that.
                    if (!CanEmit((PipelinePass)pass, (PassStopReason)reason)) continue;

                    total += stopReasonTallies[pass, reason];
                    participation += Participation(stopReasonTallies, pass);
                }

                eligibleTotal += total;
                if (participation == 0) continue;

                double share = (double)total / participation;

                // Exact equality is the intended test, not an oversight: only a bit-identical share is a tie
                // worth breaking. A tolerance would hand NEAR-ties to the bound regime as well, which is a
                // broader change than this rule makes — a near-tie is already visible as the printed
                // dominant/runner-up shares, which is where §7.2 wants that judgment made.
                // ReSharper disable once CompareOfFloatsByEqualityOperator
                if (share > dominantShare || (share == dominantShare && OutranksOnTie(reason, dominantIndex)))
                {
                    runnerUpIndex = dominantIndex;
                    runnerUpShare = dominantShare;
                    dominantIndex = reason;
                    dominantShare = share;
                }
                else if (share > runnerUpShare)
                {
                    runnerUpIndex = reason;
                    runnerUpShare = share;
                }
            }

            double wasteFraction = terminalTraces > 0 ? (double)wasteTraces / terminalTraces : 0.0;

            // The ordering axis needs a population before a percentage of it means anything. Excluding
            // never-admitted requests (FP-7a) was correct but shrank this denominator, and the BOOLEAN is what
            // downstream docs quote — so below the floor the axis reports "undecidable" rather than a verdict
            // computed off a handful of traces.
            bool orderingDecidable = terminalTraces >= MinOrderingTerminalTraces;
            bool orderingBound = orderingDecidable && wasteFraction >= OrderingWasteThreshold;

            if (eligibleTotal == 0 || dominantIndex < 0)
                return new RegimeVerdict(PipelineRegime.NoData, orderingBound, orderingDecidable,
                    PassStopReason.NotRun, PassStopReason.NotRun, wasteFraction, 0.0, 0.0,
                    false, eligibleTotal);

            // Below the floor the plurality is arithmetically fine and statistically meaningless, so the
            // regime is withheld rather than asserted — but the dominant/runner-up shares are still returned,
            // because §7.2 requires the inputs a reader would need to disagree with this refusal. Primary is
            // forced to NoData so a consumer reading it WITHOUT checking PrimaryDecidable cannot be misled;
            // EligibleObservations is what separates "too little data" from "no data at all".
            bool primaryDecidable = eligibleTotal >= MinRegimeObservations;

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

            return new RegimeVerdict(primaryDecidable ? primary : PipelineRegime.NoData, orderingBound,
                orderingDecidable, dominant, runnerUp, wasteFraction, dominantShare, runnerUpShare,
                primaryDecidable, eligibleTotal);
        }
    }
}
