using System;
using System.Collections.Generic;
using System.Text;
using Benchmarks;
using Data;
using Editor.Validation.Framework;
using Helpers;
using UnityEditor;
using UnityEngine;

namespace Editor.Validation.PipelineBackpressure
{
    /// <summary>
    /// Truth-table suite for the P-4 §3.4/§3.5 backpressure helpers: <see cref="PipelinePassBudget"/>
    /// (rate quota + Stopwatch window ceiling that replace the fixed per-frame count caps) and
    /// <see cref="GenerationPanicGate"/> (hysteresis gate over the lighting ready-set backlog). Both
    /// are pure managed functions, so no world state or clock mocking is involved.
    /// <para>All scenarios are <b>baselines</b> (must stay green); a failure is a regression in the
    /// budget/panic policy the P-4 measurement sessions validated in-game.</para>
    /// <para><b>Prove-red (demonstrated by temporary mutation):</b> removing <c>QUOTA_EPSILON</c> in
    /// <see cref="PipelinePassBudget.ComputeQuota"/> reds B1's cap-10 identity (runtime float noise
    /// ceils 10 → 11 on a perfect 60 FPS frame; 104 of the 128 in-range caps overshoot); dropping the
    /// <c>budgetTicks &gt; 0</c> guard in <see cref="PipelinePassBudget.IsExpired"/> reds B4's
    /// unbudgeted-default pin; evaluating the closed arm against the close threshold reds B6's
    /// inside-band <c>RemainClosed</c> pin. FP-7 adds three, each isolating exactly one baseline with the
    /// other thirteen untouched: inverting the <c>AdmittedTicks == 0</c> test in
    /// <see cref="PipelineTelemetry.StampUnloaded"/> reds B13 (4 of its 6 assertions — the arrival and
    /// phase-end arms do not route through that branch); adding
    /// <see cref="TraceDisposition.AbandonedBeforeAdmission"/> to
    /// <see cref="PipelineRegimeVerdict.IsWaste"/> reds B14 on exactly its governing assertion; granting
    /// <see cref="PipelinePass.MeshProcess"/> full capability in
    /// <see cref="PipelineRegimeVerdict.CanEmit"/> reds B10 — including the §7.1.1 dilution scenario, which
    /// is the point: that assertion detects the v1 defect returning, not merely a changed constant.
    /// The FP-7 review adds three more, all isolating to B10: replacing the measured participation
    /// denominator with a nominal <c>frameCount × eligible passes</c> reds the lighting-off scenario (whose
    /// proportions are chosen so the two formulas <i>disagree</i> — a shape both accept would guard nothing);
    /// dropping the <see cref="PipelineRegimeVerdict.OutranksOnTie"/> clause reds the exact-tie assertion;
    /// and removing the
    /// <see cref="PipelineRegimeVerdict.MinOrderingTerminalTraces"/> floor reds the small-sample pair.</para>
    /// </summary>
    public static class PipelineBackpressureValidationSuite
    {
        // Representative thresholds; the gate only compares them, exact values are arbitrary.
        private const int CLOSE_AT = 256;
        private const int REOPEN_AT = 128;

        /// <summary>Runs every scenario and prints a categorized summary via the shared runner.</summary>
        [MenuItem("Minecraft Clone/Dev/Validate Pipeline Backpressure")]
        public static void RunAll() => Execute();

        /// <summary>
        /// Builds and runs the backpressure scenarios, returning the categorized result (the
        /// headless/CI entry point). <see cref="KnownBugChannel.Unimplemented"/> for parity with the
        /// other pure-logic suites; the channel is currently unused (baselines only).
        /// </summary>
        /// <param name="logToConsole">When false, runs silently and only returns the result (for headless/CI use).</param>
        /// <param name="showProgress">When false, suppresses this suite's own progress bar (the aggregate runner drives one).</param>
        /// <returns>The categorized, timed result of the run.</returns>
        public static ValidationRunResult Execute(bool logToConsole = true, bool showProgress = true)
        {
            List<Scenario> scenarios = new List<Scenario>
            {
                new Scenario("B1 Quota identity at the 60 FPS reference frame", RunB1QuotaIdentity),
                new Scenario("B2 Quota scales with frame duration (rate held constant)", RunB2QuotaScales),
                new Scenario("B3 Quota clamps: hitch ceiling, floor, degenerate inputs", RunB3QuotaClamps),
                new Scenario("B4 Window ticks + unbudgeted-default semantics", RunB4WindowSemantics),
                new Scenario("B5 Panic gate truth table (all four arms + boundaries)", RunB5GateTruthTable),
                new Scenario("B6 Panic gate hysteresis walk (band holds both ways)", RunB6HysteresisWalk),
                new Scenario("B7 Ceiling scaling: FPS-cap intent, floor, clamp, disabled passthrough", RunB7CeilingScaling),
                new Scenario("B8 Stop-reason classifier: precedence, and AllDeclined never collapsing into OutOfWork", RunB8StopReasonClassifier),
                new Scenario("B9 Nearest-rank percentile selection + histogram totality (FP-3)", RunB9TraceStatistics),
                new Scenario("B10 Regime verdict rule v2: arms, capability matrix, dilution, ordering axis (§7.1 v2)", RunB10VerdictRule),
                new Scenario("B11 Run boundary: a second capture reports only its own phases (FP-5)", RunB11RunBoundaryReset),
                new Scenario("B12 Settings snapshot: resident square + gate-threshold ratio (FP-6)", RunB12SettingsSnapshot),
                new Scenario("B13 Unload disposition: admitted work vs never-admitted request (FP-7a)", RunB13UnloadDisposition),
                new Scenario("B14 Waste predicates: numerator and denominator membership (FP-7a)", RunB14WastePredicates),
                new Scenario("B15 Report integrity banners: stale capability matrix + double-recorded pass", RunB15ReportIntegrity),
                new Scenario("B16 Primary-regime credibility: sample floor + non-measurement phases (FP-9a)", RunB16PrimaryRegimeCredibility),
                new Scenario("B17 Route geometry: waypoint constancy, route length, tour coverage (FP-9b)", RunB17RouteGeometry),
                new Scenario("B18 Tour footprint + coverage accounting: closed circuit, load inflation, no vacuous 100% (FP-11a)", RunB18TourCoverage),
                new Scenario("B19 Panic-gate thresholds scale with the resident square (P-8)", RunB19GateThresholdScaling),
                new Scenario("B20 Work amplification: pre-delivery / post-delivery / wasted never cross (P9-0 §10 q1)", RunB20Amplification),
                new Scenario("B21 Parked-time accumulation: idempotent park, multi-cycle sum, open interval at delivery (P9-0 §10 q4)", RunB21ParkedTime),
                new Scenario("B22 Pass-cost attribution: phases stay disjoint, and unmeasured never renders as 0.0 ms (P9-0)", RunB22PassCostAttribution),
            };
            return ValidationSuiteRunner.Execute("Pipeline Backpressure", scenarios, KnownBugChannel.Unimplemented, logToConsole, showProgress);
        }

        /// <summary>Logs a single assertion as PASS/FAIL and returns its result for AND-chaining.</summary>
        /// <param name="label">Human-readable assertion description.</param>
        /// <param name="condition">The asserted condition.</param>
        /// <returns><paramref name="condition"/>.</returns>
        private static bool Check(string label, bool condition)
        {
            if (condition) Debug.Log($"  [PASS] {label}");
            else Debug.LogError($"  [FAIL] {label}");
            return condition;
        }

        /// <summary>
        /// FP-2: the shared stop-reason classifier both scheduling loops route through. Two things are
        /// pinned. <b>Precedence</b> — quota → ceiling → in-flight cap, matching the loops' own check order,
        /// so a pass that broke on the first limit is never attributed to a later one that also happens to
        /// be true. <b>The readiness discrimination</b> — a pass that walked its whole queue and scheduled
        /// nothing is <c>AllDeclined</c>, never <c>OutOfWork</c>; collapsing those two would report a
        /// stalled pipeline as a healthy one, which is the single worst misreading the flight capture could
        /// produce (FLIGHT_PROFILE_CAPTURE.md §5.1). An empty queue stays <c>OutOfWork</c>, which is why the
        /// candidate count and not merely the scheduled count decides it.
        /// </summary>
        private static bool RunB8StopReasonClassifier()
        {
            // Precedence: each limit wins over the ones below it even when several are true at once.
            bool ok = Check("quota outranks ceiling and cap when all three are true",
                PipelinePassBudget.ClassifyStop(5, 9, true, true, true) == PassStopReason.Quota);
            ok &= Check("ceiling outranks the in-flight cap",
                PipelinePassBudget.ClassifyStop(5, 9, false, true, true) == PassStopReason.Ceiling);
            ok &= Check("in-flight cap reported when it is the only limit hit",
                PipelinePassBudget.ClassifyStop(5, 9, false, false, true) == PassStopReason.InFlightCap);

            // The readiness discrimination — the load-bearing pair.
            ok &= Check("walked candidates, scheduled none -> AllDeclined (readiness-bound)",
                PipelinePassBudget.ClassifyStop(0, 9, false, false, false) == PassStopReason.AllDeclined);
            ok &= Check("empty queue (no candidates) -> OutOfWork, NOT AllDeclined",
                PipelinePassBudget.ClassifyStop(0, 0, false, false, false) == PassStopReason.OutOfWork);
            ok &= Check("walked candidates and served some -> OutOfWork (healthy drain)",
                PipelinePassBudget.ClassifyStop(4, 9, false, false, false) == PassStopReason.OutOfWork);

            // A limit break with zero scheduled must still report the limit, not AllDeclined: the pass was
            // cut short, so it never learned whether the remaining candidates were eligible.
            ok &= Check("cap break with nothing scheduled -> InFlightCap, not AllDeclined",
                PipelinePassBudget.ClassifyStop(0, 9, false, false, true) == PassStopReason.InFlightCap);

            // NotRun is a caller-side sentinel for a pass that never executed; the classifier describes a
            // pass that DID run, so it must never produce it.
            ok &= Check("classifier never returns NotRun (it only describes passes that ran)",
                PipelinePassBudget.ClassifyStop(0, 0, false, false, false) != PassStopReason.NotRun);

            return ok;
        }

        /// <summary>
        /// FP-3: the percentile selection every future capture is ranked by. A wrong percentile does not
        /// fail loudly — it silently mis-ranks one capture against another, so this is pinned rather than
        /// trusted. Nearest-rank (no interpolation) means every reported value is a real observed sample,
        /// which is what lets a reader reconcile the percentile table against the raw histogram beside it.
        /// Also pins that the histogram <b>drops nothing</b>: the bucket counts must sum to the sample count,
        /// or the §7.2 "raw results" block would understate the tail it exists to expose.
        /// </summary>
        private static bool RunB9TraceStatistics()
        {
            // 1..10: nearest-rank ranks are ceil(p/100*10), so p50 -> index 5 (value 5), p95 -> index 10.
            List<long> ten = new List<long> { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
            bool ok = Check("p50 of 1..10 -> 5 (nearest-rank, not the 5.5 interpolation would give)",
                TraceStatistics.Percentile(ten, 50) == 5);
            ok &= Check("p95 of 1..10 -> 10", TraceStatistics.Percentile(ten, 95) == 10);
            ok &= Check("p99 of 1..10 -> 10", TraceStatistics.Percentile(ten, 99) == 10);
            ok &= Check("p0 -> min", TraceStatistics.Percentile(ten, 0) == 1);
            ok &= Check("p100 -> max", TraceStatistics.Percentile(ten, 100) == 10);

            // Single sample: every percentile is that sample — the degenerate case a capture hits whenever
            // exactly one chunk completed in a phase.
            List<long> one = new List<long> { 42 };
            ok &= Check("single-sample series: p50 == p95 == max == the sample",
                TraceStatistics.Percentile(one, 50) == 42 && TraceStatistics.Percentile(one, 95) == 42);

            ok &= Check("empty series -> 0 (never throws)",
                TraceStatistics.Percentile(new List<long>(), 95) == 0);

            // Histogram totality — including a sample past the last edge, which must land in the overflow
            // slot rather than being discarded.
            List<double> ms = new List<double> { 0.5, 1.0, 3.0, 7.0, 99.0, 100000.0 };
            int[] buckets = TraceStatistics.Histogram(ms);
            int summed = 0;
            foreach (int b in buckets) summed += b;
            ok &= Check("histogram buckets sum to the sample count (nothing dropped)", summed == ms.Count);
            ok &= Check("a sample past the last edge lands in the overflow bucket",
                buckets[^1] == 1);
            ok &= Check("bucket count is edges + 1 (one overflow slot)",
                buckets.Length == TraceStatistics.HistogramEdgesMs.Length + 1);

            return ok;
        }

        /// <summary>
        /// FP-3 §7.1, rewritten for the <b>v2</b> rule (FP-7e): the verdict, pinned so it cannot silently
        /// change meaning between captures — two reports produced by different rules are not comparable, and
        /// nothing in a report would reveal that. Covers each regime arm, the capability matrix, the
        /// capability-weighted plurality, and the ordering axis (deliberately independent, so it can fire on
        /// top of a Healthy primary — the shape the reported flight symptom is most likely to take).
        /// <para>
        /// The load-bearing scenario is the <b>dilution regression</b>: v1 summed every reason over all four
        /// passes, so the two completion passes' near-constant <c>OutOfWork</c> outvoted a decisive
        /// <c>Quota</c> on the passes that actually carry a job quota. At FP-4's loading 200 m/s phase that
        /// decided the plurality by 68 frames out of 27,744 and printed <i>Healthy</i>. v2 must call the same
        /// shape <c>AdmissionBound</c>, or the defect §7.1.1 recorded is back.
        /// </para>
        /// </summary>
        private static bool RunB10VerdictRule()
        {
            const int passes = PipelineTelemetry.PassCount;
            const int reasons = PipelineTelemetry.StopReasonCount;

            // All fixture magnitudes clear PipelineRegimeVerdict.MinRegimeObservations (FP-9a), or every
            // regime arm below would come back NoData. Scaling a matrix by a constant leaves every share
            // exactly unchanged — numerator and denominator scale together — so the expected values are the
            // same ones this scenario has always pinned.
            //
            // Helper: one reason, reported by a pass eligible to emit it. That pass's participation is the
            // count itself, so its share is 1.0 and every other reason scores 0.
            int[,] Only(PassStopReason r, int count)
            {
                int[,] t = new int[passes, reasons];
                t[(int)PipelinePass.MeshSchedule, (int)r] = count;
                return t;
            }

            bool ok = Check("Quota dominant -> AdmissionBound",
                PipelineRegimeVerdict.Evaluate(Only(PassStopReason.Quota, 2000), 0, 100).Primary
                == PipelineRegime.AdmissionBound);
            ok &= Check("Ceiling dominant -> AdmissionBound",
                PipelineRegimeVerdict.Evaluate(Only(PassStopReason.Ceiling, 2000), 0, 100).Primary
                == PipelineRegime.AdmissionBound);
            ok &= Check("InFlightCap dominant -> ThroughputBound",
                PipelineRegimeVerdict.Evaluate(Only(PassStopReason.InFlightCap, 2000), 0, 100).Primary
                == PipelineRegime.ThroughputBound);
            ok &= Check("AllDeclined dominant -> ReadinessBound",
                PipelineRegimeVerdict.Evaluate(Only(PassStopReason.AllDeclined, 2000), 0, 100).Primary
                == PipelineRegime.ReadinessBound);
            ok &= Check("OutOfWork dominant -> Healthy",
                PipelineRegimeVerdict.Evaluate(Only(PassStopReason.OutOfWork, 2000), 0, 100).Primary
                == PipelineRegime.Healthy);
            ok &= Check("no tallies at all -> NoData",
                PipelineRegimeVerdict.Evaluate(new int[passes, reasons], 0, 0).Primary
                == PipelineRegime.NoData);

            // NotRun must never win: it is the did-not-execute sentinel, and an idle frame is not a regime.
            int[,] notRunHeavy = new int[passes, reasons];
            notRunHeavy[(int)PipelinePass.MeshSchedule, (int)PassStopReason.NotRun] = 9999;
            notRunHeavy[(int)PipelinePass.MeshSchedule, (int)PassStopReason.Quota] = 1200;
            ok &= Check("NotRun is excluded from the plurality (Quota still wins)",
                PipelineRegimeVerdict.Evaluate(notRunHeavy, 0, 100).Primary == PipelineRegime.AdmissionBound);
            ok &= Check("...and NotRun does not inflate the participation denominator either",
                Math.Abs(PipelineRegimeVerdict.Evaluate(notRunHeavy, 0, 100).DominantShare - 1.0) < 1e-9);

            // --- The capability matrix (§7.1 v2) ---
            ok &= Check("both scheduling passes can emit every real reason",
                PipelineRegimeVerdict.CanEmit(PipelinePass.LightSchedule, PassStopReason.InFlightCap)
                && PipelineRegimeVerdict.CanEmit(PipelinePass.LightSchedule, PassStopReason.AllDeclined)
                && PipelineRegimeVerdict.CanEmit(PipelinePass.MeshSchedule, PassStopReason.Quota));
            ok &= Check("GenerationProcess CAN emit Quota (its structure-mods budget — FP-7b)",
                PipelineRegimeVerdict.CanEmit(PipelinePass.GenerationProcess, PassStopReason.Quota));
            ok &= Check("...but not InFlightCap or AllDeclined",
                !PipelineRegimeVerdict.CanEmit(PipelinePass.GenerationProcess, PassStopReason.InFlightCap)
                && !PipelineRegimeVerdict.CanEmit(PipelinePass.GenerationProcess, PassStopReason.AllDeclined));
            ok &= Check("MeshProcess is genuinely ceiling-only (OutOfWork + Ceiling, nothing else)",
                PipelineRegimeVerdict.CanEmit(PipelinePass.MeshProcess, PassStopReason.OutOfWork)
                && PipelineRegimeVerdict.CanEmit(PipelinePass.MeshProcess, PassStopReason.Ceiling)
                && !PipelineRegimeVerdict.CanEmit(PipelinePass.MeshProcess, PassStopReason.Quota));
            ok &= Check("no pass can emit the NotRun sentinel",
                !PipelineRegimeVerdict.CanEmit(PipelinePass.LightSchedule, PassStopReason.NotRun)
                && !PipelineRegimeVerdict.CanEmit(PipelinePass.MeshProcess, PassStopReason.NotRun));

            // --- The dilution regression (§7.1.1), in FP-4's own proportions ---
            // Both scheduling passes report Quota on nearly every frame; both completion passes report
            // OutOfWork on nearly every frame. v1 summed these and called it Healthy by a hair.
            int[,] dilution = new int[passes, reasons];
            dilution[(int)PipelinePass.LightSchedule, (int)PassStopReason.Quota] = 990;
            dilution[(int)PipelinePass.MeshSchedule, (int)PassStopReason.Quota] = 990;
            dilution[(int)PipelinePass.GenerationProcess, (int)PassStopReason.OutOfWork] = 1000;
            dilution[(int)PipelinePass.MeshProcess, (int)PassStopReason.OutOfWork] = 1000;

            RegimeVerdict undiluted = PipelineRegimeVerdict.Evaluate(dilution, 0, 100);
            ok &= Check("the §7.1.1 dilution shape is AdmissionBound under v2 (v1 called it Healthy)",
                undiluted.Primary == PipelineRegime.AdmissionBound);
            // Quota is scored over its THREE eligible passes (FP-7b made GenerationProcess quota-capable), so
            // GenerationProcess reporting OutOfWork is a real "no" vote: 198 of the 298 reports those three
            // passes made. OutOfWork scores 200 of all four passes' 398. Quota wins 0.664 to 0.503, where
            // v1's raw sums gave 198 to 200 — i.e. Healthy.
            ok &= Check("...with Quota at 198/298 participating pass-frames vs OutOfWork at 200/398",
                Math.Abs(undiluted.DominantShare - 198.0 / 298.0) < 1e-9);
            ok &= Check("...and OutOfWork is the visible runner-up, not silently dropped",
                undiluted.RunnerUpReason == PassStopReason.OutOfWork);

            // Eligibility is per (pass, reason), so an ineligible pass cannot dilute a contested reason even
            // when it holds a large tally — the cell is ignored outright rather than down-weighted.
            int[,] ineligibleHeavy = new int[passes, reasons];
            ineligibleHeavy[(int)PipelinePass.LightSchedule, (int)PassStopReason.AllDeclined] = 600;
            ineligibleHeavy[(int)PipelinePass.MeshProcess, (int)PassStopReason.OutOfWork] = 1000;
            // AllDeclined: 60 of LightSchedule's own 60 reports = 1.00 (MeshSchedule never ran). OutOfWork:
            // 100 of the 160 reports made by its four eligible passes = 0.625.
            ok &= Check("a contested reason outranks a ceiling-only pass's 100% OutOfWork",
                PipelineRegimeVerdict.Evaluate(ineligibleHeavy, 0, 100).Primary
                == PipelineRegime.ReadinessBound);

            // --- FP-7 review finding 1: a pass that never RUNS must not be charged opportunity ---
            // LightSchedule lives inside `if (settings.enableLighting)`, so a lighting-off capture records
            // nothing for it. Dividing by frameCount x eligiblePasses charged it a full phase of chances
            // anyway, capping Quota at 2/3 while OutOfWork reached 3/4 — printing Healthy over a flat-out
            // quota stall, which is the §7.1.1 dilution rebuilt in a new place.
            // Proportions chosen so the two formulas DISAGREE — a scenario both accept would guard nothing.
            // Over 100 frames with LightSchedule silent: MeshSchedule reports Quota throughout,
            // GenerationProcess splits 25 Quota / 75 OutOfWork, MeshProcess reports OutOfWork throughout.
            //   old: Quota 125/(100x3) = 0.417 vs OutOfWork 175/(100x4) = 0.438 -> Healthy
            //   new: Quota 125/200     = 0.625 vs OutOfWork 175/300      = 0.583 -> AdmissionBound
            int[,] lightingOff = new int[passes, reasons];
            lightingOff[(int)PipelinePass.MeshSchedule, (int)PassStopReason.Quota] = 500;
            lightingOff[(int)PipelinePass.GenerationProcess, (int)PassStopReason.Quota] = 125;
            lightingOff[(int)PipelinePass.GenerationProcess, (int)PassStopReason.OutOfWork] = 375;
            lightingOff[(int)PipelinePass.MeshProcess, (int)PassStopReason.OutOfWork] = 500;

            RegimeVerdict lightsOut = PipelineRegimeVerdict.Evaluate(lightingOff, 0, 100);
            ok &= Check("a pass that never ran does not dilute the verdict (lighting disabled -> AdmissionBound)",
                lightsOut.Primary == PipelineRegime.AdmissionBound);
            ok &= Check("...with Quota at 125/200 participating pass-frames, not 125/300 nominal ones",
                Math.Abs(lightsOut.DominantShare - 125.0 / 200.0) < 1e-9);

            // --- Tie-break: an exact tie must not resolve toward "everything is fine" ---
            // Both scheduling passes split 50/50 between InFlightCap and OutOfWork; the completion passes are
            // silent. OutOfWork = 100 of its 4 eligible passes' 200 reports = 0.5. InFlightCap = 100 of its 2
            // eligible passes' 200 = 0.5. Exactly equal, and walk order reaches OutOfWork first, so a strict
            // `>` comparison would leave the phase reading Healthy.
            int[,] tied = new int[passes, reasons];
            tied[(int)PipelinePass.LightSchedule, (int)PassStopReason.InFlightCap] = 500;
            tied[(int)PipelinePass.LightSchedule, (int)PassStopReason.OutOfWork] = 500;
            tied[(int)PipelinePass.MeshSchedule, (int)PassStopReason.InFlightCap] = 500;
            tied[(int)PipelinePass.MeshSchedule, (int)PassStopReason.OutOfWork] = 500;

            RegimeVerdict tieBreak = PipelineRegimeVerdict.Evaluate(tied, 0, 100);
            ok &= Check("an exact share tie resolves to the BOUND regime, not to Healthy",
                tieBreak.Primary == PipelineRegime.ThroughputBound);
            ok &= Check("...and the tie is visible: both shares are equal in the printed inputs",
                Math.Abs(tieBreak.DominantShare - tieBreak.RunnerUpShare) < 1e-9);

            // --- The ordering axis, unchanged by v2 and still independent of the primary ---
            RegimeVerdict wasteful = PipelineRegimeVerdict.Evaluate(
                Only(PassStopReason.OutOfWork, 2000), 30, 100);
            ok &= Check("waste 30% >= threshold -> ordering-bound flagged",
                wasteful.OrderingBound && wasteful.OrderingDecidable);
            ok &= Check("...and it composes with a Healthy primary (the flight symptom's likely shape)",
                wasteful.Primary == PipelineRegime.Healthy);
            ok &= Check("waste just under the threshold -> NOT ordering-bound",
                !PipelineRegimeVerdict.Evaluate(Only(PassStopReason.OutOfWork, 2000), 19, 100).OrderingBound);
            ok &= Check("waste fraction is reported for the raw block",
                Math.Abs(wasteful.WasteFraction - 0.30) < 1e-9);

            // Minimum-sample floor: 1 waste of 3 terminal traces is 33 % and would clear the threshold, but
            // three traces cannot support a verdict. "Undecidable" must be distinguishable from "not bound".
            RegimeVerdict tinySample = PipelineRegimeVerdict.Evaluate(Only(PassStopReason.OutOfWork, 2000), 1, 3);
            ok &= Check("33% waste off 3 terminal traces is NOT reported as ordering-bound",
                !tinySample.OrderingBound);
            ok &= Check("...and is flagged undecidable, not as a clean 'well ordered' result",
                !tinySample.OrderingDecidable);
            ok &= Check("at exactly the floor the axis decides again (boundary inclusive)",
                PipelineRegimeVerdict.Evaluate(Only(PassStopReason.OutOfWork, 2000), 10,
                    PipelineRegimeVerdict.MinOrderingTerminalTraces).OrderingDecidable);

            return ok;
        }

        /// <summary>On a perfect reference frame the quota IS the cap — the flag-on steady-state contract.</summary>
        private static bool RunB1QuotaIdentity()
        {
            const float referenceDt = 1f / 60f;
            bool ok = Check("cap 32 at exactly 60 FPS -> 32 (light cap identity)",
                PipelinePassBudget.ComputeQuota(32, referenceDt) == 32);
            ok &= Check("cap 10 at exactly 60 FPS -> 10 (mesh cap identity)",
                PipelinePassBudget.ComputeQuota(10, referenceDt) == 10);
            ok &= Check("cap 128 at exactly 60 FPS -> 128 (range ceiling identity)",
                PipelinePassBudget.ComputeQuota(128, referenceDt) == 128);

            // The identities are themselves the epsilon pins: without QUOTA_EPSILON, runtime float
            // arithmetic ceils one too high for 104 of the 128 in-range caps (cap 10 → 11; power-of-two
            // caps like 32 stay exact). Proven red by temporary mutation — see the suite docstring.
            // (No unguarded-expression mirror here: Roslyn constant-folds const operands in strict
            // single precision while the JIT does not, so such a mirror is context-fragile.)
            return ok;
        }

        /// <summary>Longer frames get proportionally larger quotas — jobs/second is the invariant, not jobs/frame.</summary>
        private static bool RunB2QuotaScales()
        {
            bool ok = Check("cap 32 at 8 FPS (dt 0.125) -> 240 (32 x 7.5, the §3 inversion undone)",
                PipelinePassBudget.ComputeQuota(32, 0.125f) == 240);
            ok &= Check("cap 32 at 30 FPS (dt 1/30) -> 64",
                PipelinePassBudget.ComputeQuota(32, 1f / 30f) == 64);
            ok &= Check("cap 32 at 120 FPS (dt 1/120) -> 16 (short frames scale down)",
                PipelinePassBudget.ComputeQuota(32, 1f / 120f) == 16);
            ok &= Check("cap 10 at 20 FPS (dt 0.05) -> 30",
                PipelinePassBudget.ComputeQuota(10, 0.05f) == 30);
            return ok;
        }

        /// <summary>The quota is clamped to [1, cap x 8] and degenerate inputs fall back to legacy behavior.</summary>
        private static bool RunB3QuotaClamps()
        {
            bool ok = Check("hitch frame (dt 1s) with cap 32 clamps to 256 (8x cap, not 1920)",
                PipelinePassBudget.ComputeQuota(32, 1f) == 256);
            ok &= Check("very high FPS (dt 1/1000) with cap 1 floors at 1 (progress guaranteed)",
                PipelinePassBudget.ComputeQuota(1, 0.001f) == 1);
            ok &= Check("dt = 0 falls back to the cap (legacy per-frame behavior)",
                PipelinePassBudget.ComputeQuota(32, 0f) == 32);
            ok &= Check("negative dt falls back to the cap",
                PipelinePassBudget.ComputeQuota(32, -0.1f) == 32);
            ok &= Check("cap 0 is normalized to 1 before scaling",
                PipelinePassBudget.ComputeQuota(0, 1f / 60f) == 1);

            // Overflow guard: an absurd persisted cap must clamp before ×8, never flip the clamp
            // ceiling negative (which would halt scheduling by returning a negative quota forever).
            ok &= Check("cap int.MaxValue at a 1s hitch frame still yields a positive quota",
                PipelinePassBudget.ComputeQuota(int.MaxValue, 1f) >= 1);
            return ok;
        }

        /// <summary>Tick conversion + expiry predicate, including the default(Window)-never-expires contract.</summary>
        private static bool RunB4WindowSemantics()
        {
            long eightMs = PipelinePassBudget.TicksForMs(8f);
            bool ok = Check("8 ms converts to a positive tick budget",
                eightMs > 0);
            ok &= Check("0 ms -> 0 ticks (ceiling disabled)",
                PipelinePassBudget.TicksForMs(0f) == 0);
            ok &= Check("negative ms -> 0 ticks (ceiling disabled)",
                PipelinePassBudget.TicksForMs(-3f) == 0);

            // The unbudgeted pin: a zero budget NEVER expires, no matter how much time elapsed. This is
            // what makes `Window window = default` a safe unbudgeted parameter for the startup coroutine.
            ok &= Check("zero budget never expires (elapsed long.MaxValue)",
                !PipelinePassBudget.IsExpired(long.MaxValue, 0));
            ok &= Check("one tick under a positive budget has not expired",
                !PipelinePassBudget.IsExpired(eightMs - 1, eightMs));
            ok &= Check("exactly the budget expires (boundary inclusive)",
                PipelinePassBudget.IsExpired(eightMs, eightMs));

            PipelinePassBudget.Window unbudgeted = default;
            ok &= Check("default(Window) carries no budget",
                !unbudgeted.HasBudget);
            ok &= Check("default(Window) never reports Expired",
                !unbudgeted.Expired);
            ok &= Check("StartWindow(<= 0 ms) is also unbudgeted",
                !PipelinePassBudget.StartWindow(0f).HasBudget);
            ok &= Check("StartWindow(positive ms) carries a budget and starts unexpired",
                PipelinePassBudget.StartWindow(1000f).HasBudget && !PipelinePassBudget.StartWindow(1000f).Expired);

            // Progress guarantee: tiny positive budgets floor to MinBudgetMs (a 0.001 ms file value
            // could otherwise expire the window before a pass's first between-jobs check); zero and
            // negative stay "no ceiling", at-and-above the floor pass through untouched.
            ok &= Check("tiny positive budget floors to MinBudgetMs",
                Mathf.Approximately(PipelinePassBudget.SanitizeBudgetMs(0.001f), PipelinePassBudget.MinBudgetMs));
            ok &= Check("zero budget passes through (no ceiling)",
                ExactValue.IsZero(PipelinePassBudget.SanitizeBudgetMs(0f)));
            ok &= Check("negative budget passes through (no ceiling)",
                ExactValue.Equal(PipelinePassBudget.SanitizeBudgetMs(-3f), -3f));
            ok &= Check("exactly MinBudgetMs passes through untouched",
                ExactValue.Equal(PipelinePassBudget.SanitizeBudgetMs(PipelinePassBudget.MinBudgetMs), PipelinePassBudget.MinBudgetMs));
            ok &= Check("budgets above the floor pass through untouched",
                ExactValue.Equal(PipelinePassBudget.SanitizeBudgetMs(8f), 8f));
            return ok;
        }

        /// <summary>
        /// P-4 §3.4 ceiling scaling: a lowered FPS cap widens the ms ceiling proportionally (anchored
        /// at 60 FPS, clamped ×8, floored ×1), while a disabled ceiling and the no-cap case both pass
        /// the input through untouched (the feature-off / uncapped byte-identity contract).
        /// </summary>
        private static bool RunB7CeilingScaling()
        {
            // No cap (interval <= 0): the ceiling is returned verbatim — this is the flag-off / uncapped
            // path and MUST be byte-identical to the legacy fixed ceiling.
            bool ok = Check("no cap (interval 0) returns the ceiling unchanged",
                ExactValue.Equal(PipelinePassBudget.ScaleCeilingMs(6f, 0f), 6f));
            ok &= Check("negative interval returns the ceiling unchanged",
                ExactValue.Equal(PipelinePassBudget.ScaleCeilingMs(6f, -1f), 6f));

            // A disabled ceiling (<= 0) is never resurrected into a positive budget, at any cap.
            ok &= Check("disabled ceiling (0 ms) stays 0 even under a 15-cap",
                ExactValue.IsZero(PipelinePassBudget.ScaleCeilingMs(0f, 1f / 15f)));
            ok &= Check("disabled ceiling (negative ms) passes through under a cap",
                ExactValue.Equal(PipelinePassBudget.ScaleCeilingMs(-3f, 1f / 30f), -3f));

            // 60 FPS intent is the anchor: scale exactly 1.
            ok &= Check("60 FPS cap leaves the ceiling at 1x",
                Mathf.Approximately(PipelinePassBudget.ScaleCeilingMs(6f, 1f / 60f), 6f));
            // 30-cap doubles, 15-cap quadruples (the AFK / battery target regime).
            ok &= Check("30 FPS cap doubles the ceiling",
                Mathf.Approximately(PipelinePassBudget.ScaleCeilingMs(6f, 1f / 30f), 12f));
            ok &= Check("15 FPS cap quadruples the ceiling",
                Mathf.Approximately(PipelinePassBudget.ScaleCeilingMs(4f, 1f / 15f), 16f));

            // A >60 Hz cap must never SHRINK the ceiling (floor at 1x).
            ok &= Check("144 FPS cap does not shrink the ceiling (1x floor)",
                Mathf.Approximately(PipelinePassBudget.ScaleCeilingMs(6f, 1f / 144f), 6f));

            // An extreme low cap clamps at MAX_QUOTA_SCALE (x8): a 4 FPS intent would be x15 unclamped.
            ok &= Check("4 FPS cap clamps at x8 (48 ms from a 6 ms ceiling)",
                Mathf.Approximately(PipelinePassBudget.ScaleCeilingMs(6f, 1f / 4f), 48f));
            return ok;
        }

        /// <summary>Every arm of the gate decision, with both threshold boundaries pinned exactly.</summary>
        private static bool RunB5GateTruthTable()
        {
            bool ok = Check("open, backlog below close -> RemainOpen",
                GenerationPanicGate.Evaluate(true, CLOSE_AT - 1, CLOSE_AT, REOPEN_AT) == GenerationPanicGate.Decision.RemainOpen);
            ok &= Check("open, backlog AT close threshold -> Close (boundary inclusive)",
                GenerationPanicGate.Evaluate(true, CLOSE_AT, CLOSE_AT, REOPEN_AT) == GenerationPanicGate.Decision.Close);
            ok &= Check("closed, backlog above reopen -> RemainClosed",
                GenerationPanicGate.Evaluate(false, REOPEN_AT + 1, CLOSE_AT, REOPEN_AT) == GenerationPanicGate.Decision.RemainClosed);
            ok &= Check("closed, backlog AT reopen threshold -> Reopen (boundary inclusive)",
                GenerationPanicGate.Evaluate(false, REOPEN_AT, CLOSE_AT, REOPEN_AT) == GenerationPanicGate.Decision.Reopen);
            ok &= Check("closed, backlog below reopen -> Reopen",
                GenerationPanicGate.Evaluate(false, 0, CLOSE_AT, REOPEN_AT) == GenerationPanicGate.Decision.Reopen);

            ok &= Check("IsOpenAfter: RemainOpen + Reopen admit, Close + RemainClosed do not",
                GenerationPanicGate.IsOpenAfter(GenerationPanicGate.Decision.RemainOpen)
                && GenerationPanicGate.IsOpenAfter(GenerationPanicGate.Decision.Reopen)
                && !GenerationPanicGate.IsOpenAfter(GenerationPanicGate.Decision.Close)
                && !GenerationPanicGate.IsOpenAfter(GenerationPanicGate.Decision.RemainClosed));
            return ok;
        }

        /// <summary>
        /// A full backlog ramp through the hysteresis band: the band must hold in BOTH directions —
        /// an open gate ignores the reopen threshold, a closed gate ignores the close threshold.
        /// </summary>
        private static bool RunB6HysteresisWalk()
        {
            bool open = true;

            // Ramp up: inside the band an open gate stays open (this is the half a swapped/miswired
            // closed-arm comparison reds).
            GenerationPanicGate.Decision d = GenerationPanicGate.Evaluate(open, 200, CLOSE_AT, REOPEN_AT);
            bool ok = Check("open at 200 (inside band) stays open",
                d == GenerationPanicGate.Decision.RemainOpen);
            open = GenerationPanicGate.IsOpenAfter(d);

            d = GenerationPanicGate.Evaluate(open, 300, CLOSE_AT, REOPEN_AT);
            ok &= Check("open at 300 closes",
                d == GenerationPanicGate.Decision.Close);
            open = GenerationPanicGate.IsOpenAfter(d);

            // Drain down: inside the band a closed gate stays closed — the oscillation damping itself.
            d = GenerationPanicGate.Evaluate(open, 200, CLOSE_AT, REOPEN_AT);
            ok &= Check("closed at 200 (inside band) STAYS closed (hysteresis)",
                d == GenerationPanicGate.Decision.RemainClosed);
            open = GenerationPanicGate.IsOpenAfter(d);

            d = GenerationPanicGate.Evaluate(open, 120, CLOSE_AT, REOPEN_AT);
            ok &= Check("closed at 120 reopens",
                d == GenerationPanicGate.Decision.Reopen);
            open = GenerationPanicGate.IsOpenAfter(d);

            d = GenerationPanicGate.Evaluate(open, 200, CLOSE_AT, REOPEN_AT);
            ok &= Check("reopened at 200 (inside band) stays open again",
                d == GenerationPanicGate.Decision.RemainOpen);
            return ok;
        }

        /// <summary>
        /// FP-5: a capture must report only its own phases. This is a *regression* guard for a defect that
        /// actually shipped — <c>s_completedPhases</c> was cleared only on play-mode entry, so a second
        /// benchmark run inside one process appended to the first run's list and the report presented the
        /// earlier run's phases as its own. The vd-10 capture of 2026-07-28 carried all 9 phases of the
        /// preceding vd-5 run verbatim.
        /// <para>Drives the real static (no <c>World</c> needed), so it also pins that
        /// <see cref="PipelineTelemetry.BeginRun"/> works with the layer still disabled — it is called
        /// before <c>Enabled</c> is set, and a guard clause there would silently restore the bug.</para>
        /// <para><b>Scope, stated so it is not over-read:</b> this pins <c>BeginRun</c>'s <i>semantics</i>,
        /// not the wiring. The shipped defect was a missing <b>call</b> at
        /// <c>BenchmarkController</c>'s run start, and that site lives in a play-mode coroutine over a live
        /// <c>World</c> — unreachable from edit mode (design §7 item 1). Deleting the call would leave this
        /// scenario green. The call site remains guarded by review only.</para>
        /// </summary>
        private static bool RunB11RunBoundaryReset()
        {
            bool wasEnabled = PipelineTelemetry.Enabled;
            try
            {
                // --- Run 1: two phases ---
                PipelineTelemetry.BeginRun();
                PipelineTelemetry.Enabled = true;
                PipelineTelemetry.BeginPhase("10 m/s", "Generation Pass", 4096);
                PipelineTelemetry.EndPhase();
                PipelineTelemetry.BeginPhase("20 m/s", "Generation Pass", 4096);
                PipelineTelemetry.EndPhase();

                bool ok = Check("run 1 records its 2 phases", PipelineTelemetry.CompletedPhases.Count == 2);

                // --- Run 2, same process: one phase ---
                PipelineTelemetry.BeginRun();
                PipelineTelemetry.Enabled = true;
                PipelineTelemetry.BeginPhase("50 m/s", "Loading Pass", 4096);
                PipelineTelemetry.EndPhase();

                ok &= Check("run 2 reports ONLY its own phase (run 1 did not leak)",
                    PipelineTelemetry.CompletedPhases.Count == 1);
                ok &= Check("the surviving phase is run 2's, not run 1's first",
                    PipelineTelemetry.CompletedPhases.Count == 1 &&
                    PipelineTelemetry.CompletedPhases[0].PhaseName == "50 m/s" &&
                    PipelineTelemetry.CompletedPhases[0].GroupName == "Loading Pass");

                // BeginRun must also close an abandoned phase, or an aborted run leaves one open and the
                // next run's first EndPhase would file it under the wrong run.
                PipelineTelemetry.BeginPhase("aborted", "Generation Pass", 4096);
                PipelineTelemetry.BeginRun();
                ok &= Check("BeginRun drops an open phase left by an aborted run",
                    !PipelineTelemetry.IsPhaseActive && PipelineTelemetry.CompletedPhases.Count == 0);

                return ok;
            }
            finally
            {
                // Leave nothing behind: a live phase or an enabled layer would perturb every later suite.
                // BeginRun clears Enabled, so restore the flag AFTER it, not before.
                PipelineTelemetry.BeginRun();
                PipelineTelemetry.Enabled = wasEnabled;
            }
        }

        /// <summary>
        /// FP-7a: the unload hook's two endings, driven end-to-end through the real telemetry statics (the
        /// technique B11 established — no <c>World</c> is needed to exercise the trace table).
        /// <para>
        /// This is a <i>regression</i> guard for a defect that shipped and reached a verdict: every unloaded
        /// chunk holding a trace was stamped <c>UnloadedBeforeMeshApplied</c> — counted as waste, and waste
        /// is the sole input to the ORDERING-BOUND axis — <b>including requests the panic gate never
        /// admitted</b>, for which no stage ran and no work was thrown away. The distortion is largest
        /// exactly where the gate is closed most (92–96 % of frames at vd ≥ 10), i.e. in the regime the
        /// capture exists to weigh.
        /// </para>
        /// <para><b>Scope, stated so it is not over-read:</b> this pins the <i>classification</i>. The
        /// <c>StampUnloaded</c> call site is in <c>World.UnloadChunks</c>, a play-mode path unreachable from
        /// edit mode (design §7 item 1) — reverting that one line would leave this scenario green. The call
        /// site stays guarded by review only, exactly as B11's does.</para>
        /// </summary>
        private static bool RunB13UnloadDisposition()
        {
            bool wasEnabled = PipelineTelemetry.Enabled;
            try
            {
                // --- A request that was admitted, did work, and was then unloaded: waste. ---
                PipelineTelemetry.BeginRun();
                PipelineTelemetry.Enabled = true;
                PipelineTelemetry.BeginPhase("admitted", "FP-7a", 4096);

                ChunkCoord admitted = new ChunkCoord(1, 1);
                PipelineTelemetry.StampRequested(admitted);
                PipelineTelemetry.StampAdmitted(admitted);
                PipelineTelemetry.StampUnloaded(admitted);
                PipelineTelemetry.EndPhase();

                PipelinePhaseMetrics phase = PipelineTelemetry.CompletedPhases[0];
                bool ok = Check("admitted then unloaded -> UnloadedBeforeMeshApplied",
                    phase.DispositionCounts[(int)TraceDisposition.UnloadedBeforeMeshApplied] == 1);
                ok &= Check("...and NOT AbandonedBeforeAdmission",
                    phase.DispositionCounts[(int)TraceDisposition.AbandonedBeforeAdmission] == 0);

                // --- A request the gate never admitted, then unloaded: no work was ever performed. ---
                PipelineTelemetry.BeginRun();
                PipelineTelemetry.Enabled = true;
                PipelineTelemetry.BeginPhase("never admitted", "FP-7a", 4096);

                ChunkCoord abandoned = new ChunkCoord(2, 2);
                PipelineTelemetry.StampRequested(abandoned);
                PipelineTelemetry.StampUnloaded(abandoned);
                PipelineTelemetry.EndPhase();

                phase = PipelineTelemetry.CompletedPhases[0];
                ok &= Check("requested but never admitted, then unloaded -> AbandonedBeforeAdmission",
                    phase.DispositionCounts[(int)TraceDisposition.AbandonedBeforeAdmission] == 1);
                ok &= Check("...and NOT counted as UnloadedBeforeMeshApplied (the shipped defect)",
                    phase.DispositionCounts[(int)TraceDisposition.UnloadedBeforeMeshApplied] == 0);

                // A completed journey must still close as an arrival — the hook cannot double-count, and a
                // later unload of the same coord must find no trace to reclassify.
                PipelineTelemetry.BeginRun();
                PipelineTelemetry.Enabled = true;
                PipelineTelemetry.BeginPhase("arrival", "FP-7a", 4096);

                ChunkCoord arrived = new ChunkCoord(3, 3);
                PipelineTelemetry.StampRequested(arrived);
                PipelineTelemetry.StampAdmitted(arrived);
                PipelineTelemetry.StampMeshApplied(arrived);
                PipelineTelemetry.StampUnloaded(arrived);
                PipelineTelemetry.EndPhase();

                phase = PipelineTelemetry.CompletedPhases[0];
                ok &= Check("a chunk that reached MeshApplied stays an arrival when later unloaded",
                    phase.DispositionCounts[(int)TraceDisposition.MeshApplied] == 1
                    && phase.DispositionCounts[(int)TraceDisposition.UnloadedBeforeMeshApplied] == 0
                    && phase.DispositionCounts[(int)TraceDisposition.AbandonedBeforeAdmission] == 0);

                // An un-admitted trace still live when the phase ends is InFlightAtPhaseEnd, NOT abandoned:
                // the capture stopped first, which is a statement about the instrument, not the pipeline.
                PipelineTelemetry.BeginRun();
                PipelineTelemetry.Enabled = true;
                PipelineTelemetry.BeginPhase("cutoff", "FP-7a", 4096);
                PipelineTelemetry.StampRequested(new ChunkCoord(4, 4));
                PipelineTelemetry.EndPhase();

                phase = PipelineTelemetry.CompletedPhases[0];
                ok &= Check("un-admitted trace open at phase end -> InFlightAtPhaseEnd, not Abandoned",
                    phase.DispositionCounts[(int)TraceDisposition.InFlightAtPhaseEnd] == 1
                    && phase.DispositionCounts[(int)TraceDisposition.AbandonedBeforeAdmission] == 0);

                return ok;
            }
            finally
            {
                // Leave nothing behind. BeginRun clears Enabled, so restore the flag AFTER it (B11's gotcha).
                PipelineTelemetry.BeginRun();
                PipelineTelemetry.Enabled = wasEnabled;
            }
        }

        /// <summary>
        /// FP-7a: which dispositions the ordering axis counts, and which population it divides by. Pinned as
        /// a pair because the fraction is only meaningful if both ends agree — a disposition added to the
        /// numerator but not the denominator (or vice versa) yields a number that is not a fraction of
        /// anything. Lives on <see cref="PipelineRegimeVerdict"/> rather than the report section precisely so
        /// the verdict and the table printed under it cannot classify a disposition differently.
        /// </summary>
        private static bool RunB14WastePredicates()
        {
            // The numerator: work the pipeline completed and then threw away.
            bool ok = Check("DiscardedOutOfRange is waste",
                PipelineRegimeVerdict.IsWaste(TraceDisposition.DiscardedOutOfRange));
            ok &= Check("UnloadedBeforeMeshApplied is waste (the ordering-bound signal)",
                PipelineRegimeVerdict.IsWaste(TraceDisposition.UnloadedBeforeMeshApplied));

            // Not waste, each for a different reason — collapsing any of them inflates the ordering axis.
            ok &= Check("MeshApplied is not waste (it arrived)",
                !PipelineRegimeVerdict.IsWaste(TraceDisposition.MeshApplied));
            ok &= Check("InFlightAtPhaseEnd is not waste (the capture stopped first)",
                !PipelineRegimeVerdict.IsWaste(TraceDisposition.InFlightAtPhaseEnd));
            ok &= Check("Rerequested is not waste (churn; its work may still land)",
                !PipelineRegimeVerdict.IsWaste(TraceDisposition.Rerequested));
            ok &= Check("AbandonedBeforeAdmission is not waste (no stage ever ran)",
                !PipelineRegimeVerdict.IsWaste(TraceDisposition.AbandonedBeforeAdmission));

            // The denominator: terminal traces for which the pipeline actually performed work.
            ok &= Check("MeshApplied is in the denominator",
                PipelineRegimeVerdict.IsInWasteDenominator(TraceDisposition.MeshApplied));
            ok &= Check("every waste disposition is in the denominator (or the fraction is not a fraction)",
                PipelineRegimeVerdict.IsInWasteDenominator(TraceDisposition.DiscardedOutOfRange)
                && PipelineRegimeVerdict.IsInWasteDenominator(TraceDisposition.UnloadedBeforeMeshApplied));
            ok &= Check("InFlightAtPhaseEnd is in the denominator (terminal, and work was done)",
                PipelineRegimeVerdict.IsInWasteDenominator(TraceDisposition.InFlightAtPhaseEnd));
            ok &= Check("Pending is NOT in the denominator (not terminal)",
                !PipelineRegimeVerdict.IsInWasteDenominator(TraceDisposition.Pending));
            ok &= Check("AbandonedBeforeAdmission is NOT in the denominator (never entered the pipeline)",
                !PipelineRegimeVerdict.IsInWasteDenominator(TraceDisposition.AbandonedBeforeAdmission));

            // The consequence the choice exists for: a phase dominated by never-admitted requests must still
            // report the waste among chunks the pipeline actually served. 30 waste of 100 admitted terminal
            // traces is ordering-bound whether 0 or 9,000 requests were abandoned alongside them.
            int[,] tallies = new int[PipelineTelemetry.PassCount, PipelineTelemetry.StopReasonCount];
            tallies[(int)PipelinePass.MeshSchedule, (int)PassStopReason.OutOfWork] = 2000;
            ok &= Check("the ordering axis is unmoved by abandoned traces (they are in neither term)",
                PipelineRegimeVerdict.Evaluate(tallies, 30, 100).OrderingBound);

            return ok;
        }

        /// <summary>
        /// FP-7 review: the two integrity conditions that make a phase's tallies untrustworthy as verdict
        /// <i>inputs</i> must reach the <b>report</b>, not just a development console.
        /// <para>
        /// The development-build asserts in <c>RecordPassStop</c> are compiled out of a Release player — and
        /// Release is the build a capture should be taken in, since the P-4 budgets are frame-time-proportional
        /// and a Development Build therefore measures a different admission regime. A guard that only fires in
        /// the build nobody captures with is not a guard, so both conditions are re-derived at render time
        /// from data the report already carries.
        /// </para>
        /// <para>
        /// Renders through the real <see cref="PipelineReportSection"/> over a hand-built
        /// <see cref="PipelinePhaseMetrics"/>, and drives the double-record flag through the real telemetry
        /// statics (B11's technique), so this pins the wiring as well as the text.
        /// </para>
        /// <para><b>Emits one expected <c>LogError</c>:</b> triggering the double record necessarily trips
        /// the development-build console assert beside the flag ("GenerationProcess recorded a stop reason
        /// twice in one frame"). That line is the guard working, not a failure — the same convention as the
        /// save-durability and meshing suites' injected faults. Judge this suite by its PASS/FAIL lines, not
        /// by console errors.</para>
        /// </summary>
        private static bool RunB15ReportIntegrity()
        {
            // A clean phase must produce NO integrity banner — otherwise the warnings are noise and get
            // ignored, which is the failure mode a always-on warning is most prone to.
            PipelinePhaseMetrics clean = new PipelinePhaseMetrics { PhaseName = "clean", GroupName = "FP-7" };
            clean.StopReasonCounts[(int)PipelinePass.MeshSchedule, (int)PassStopReason.Quota] = 50;

            string cleanText = Render(clean);
            bool ok = Check("a clean phase renders no CAPABILITY MATRIX STALE banner",
                !cleanText.Contains("CAPABILITY MATRIX STALE"));
            ok &= Check("a clean phase renders no DOUBLE-RECORDED PASS banner",
                !cleanText.Contains("DOUBLE-RECORDED PASS"));

            // A tally in a cell CanEmit forbids: the matrix is stale and the verdict is computed without it.
            PipelinePhaseMetrics stale = new PipelinePhaseMetrics { PhaseName = "stale", GroupName = "FP-7" };
            stale.StopReasonCounts[(int)PipelinePass.MeshProcess, (int)PassStopReason.AllDeclined] = 7;

            string staleText = Render(stale);
            ok &= Check("an ineligible non-zero cell raises the stale-matrix banner",
                staleText.Contains("CAPABILITY MATRIX STALE"));
            ok &= Check("...naming the offending pass, reason and count so it can be acted on",
                staleText.Contains("MeshProcess") && staleText.Contains("AllDeclined")
                                                  && staleText.Contains("7"));

            // The double-record flag, set through the real statics rather than assigned directly.
            bool wasEnabled = PipelineTelemetry.Enabled;
            PipelinePhaseMetrics doubled;
            try
            {
                PipelineTelemetry.BeginRun();
                PipelineTelemetry.Enabled = true;
                PipelineTelemetry.BeginPhase("doubled", "FP-7", 4096);

                // Two reports for the SAME pass without an intervening RecordFrame — the shape
                // ForceCompleteDataJobsCoroutine would produce if telemetry were ever enabled during startup.
                PipelineTelemetry.RecordPassStop(PipelinePass.GenerationProcess, PassStopReason.OutOfWork);
                PipelineTelemetry.RecordPassStop(PipelinePass.GenerationProcess, PassStopReason.Ceiling);
                PipelineTelemetry.EndPhase();

                doubled = PipelineTelemetry.CompletedPhases[0];
            }
            finally
            {
                PipelineTelemetry.BeginRun();
                PipelineTelemetry.Enabled = wasEnabled;
            }

            ok &= Check("a second report for one pass in one frame sets that pass's flag",
                doubled.PassDoubleRecorded[(int)PipelinePass.GenerationProcess]);
            ok &= Check("...and only that pass's",
                !doubled.PassDoubleRecorded[(int)PipelinePass.MeshSchedule]
                && !doubled.PassDoubleRecorded[(int)PipelinePass.LightSchedule]);
            ok &= Check("...and AnyPassDoubleRecorded reports it",
                doubled.AnyPassDoubleRecorded);

            string doubledText = Render(doubled);
            ok &= Check("the double-record flag raises the banner, naming the pass",
                doubledText.Contains("DOUBLE-RECORDED PASS") && doubledText.Contains("GenerationProcess"));

            // A single report per pass must NOT trip it — the flag keys on a repeat within one frame, and a
            // pass reporting on every frame of a long phase is the normal case.
            ok &= Check("one report per pass per frame does not trip the flag",
                !clean.AnyPassDoubleRecorded);

            return ok;
        }

        /// <summary>
        /// FP-9a: a primary regime must not be asserted from a sample too small to support one, and a phase
        /// that is not a measurement must not be assigned a regime at all.
        /// <para>
        /// A <i>regression</i> guard for verdicts FP-8 actually printed: <c>ThroughputBound</c> for a
        /// 14-frame generation phase (56 eligible observations), <c>AdmissionBound</c> for a 148-frame one
        /// (592), and <c>AdmissionBound</c> for the drain/save/unload <b>transition</b>. The first two are
        /// sample size; the third is not, and could not be fixed by any floor — that phase carried ~1 332
        /// observations, comfortably above it. Hence two mechanisms, pinned here together.
        /// </para>
        /// <para>
        /// The three no-regime outcomes must render <b>distinguishably</b> — "no pass reported", "too little
        /// to decide", and "not a measurement" are different claims, and only the first is a statement about
        /// an idle pipeline. This is the same discrimination FP-7 established for the ordering axis.
        /// </para>
        /// </summary>
        private static bool RunB16PrimaryRegimeCredibility()
        {
            const int passes = PipelineTelemetry.PassCount;
            const int reasons = PipelineTelemetry.StopReasonCount;

            int[,] Quota(int count)
            {
                int[,] t = new int[passes, reasons];
                t[(int)PipelinePass.MeshSchedule, (int)PassStopReason.Quota] = count;
                return t;
            }

            // --- The floor itself, at the boundary ---
            RegimeVerdict below = PipelineRegimeVerdict.Evaluate(
                Quota(PipelineRegimeVerdict.MinRegimeObservations - 1), 0, 100);
            bool ok = Check("one observation below the floor -> not decidable",
                !below.PrimaryDecidable);
            ok &= Check("...and Primary is NoData, so a caller ignoring the flag cannot be misled",
                below.Primary == PipelineRegime.NoData);
            ok &= Check("...while the dominant reason and share are still reported (§7.2 inputs survive)",
                below.DominantReason == PassStopReason.Quota && Math.Abs(below.DominantShare - 1.0) < 1e-9);

            RegimeVerdict atFloor = PipelineRegimeVerdict.Evaluate(
                Quota(PipelineRegimeVerdict.MinRegimeObservations), 0, 100);
            ok &= Check("exactly at the floor decides (boundary inclusive)",
                atFloor.PrimaryDecidable && atFloor.Primary == PipelineRegime.AdmissionBound);

            // FP-8's own rejected phases, by their real observation counts.
            ok &= Check("FP-8's 14-frame phase (56 observations) would no longer assert a regime",
                !PipelineRegimeVerdict.Evaluate(Quota(56), 0, 100).PrimaryDecidable);
            ok &= Check("FP-8's 148-frame phase (592 observations) would no longer assert a regime",
                !PipelineRegimeVerdict.Evaluate(Quota(592), 0, 100).PrimaryDecidable);

            // Empty vs sparse must stay distinguishable, and EligibleObservations is what does it.
            RegimeVerdict empty = PipelineRegimeVerdict.Evaluate(new int[passes, reasons], 0, 100);
            ok &= Check("an empty phase reports 0 eligible observations",
                !empty.PrimaryDecidable && empty.EligibleObservations == 0);
            ok &= Check("a sparse phase reports its real count, not 0",
                below.EligibleObservations == PipelineRegimeVerdict.MinRegimeObservations - 1);

            // --- Rendering: the three no-regime outcomes must read differently ---
            PipelinePhaseMetrics sparse = new PipelinePhaseMetrics { PhaseName = "sparse", GroupName = "FP-9a" };
            sparse.StopReasonCounts[(int)PipelinePass.MeshSchedule, (int)PassStopReason.Quota] = 56;
            string sparseText = Render(sparse);
            ok &= Check("a sparse phase renders UNDECIDABLE with its observation count",
                sparseText.Contains("UNDECIDABLE") && sparseText.Contains("56"));
            ok &= Check("...and does NOT claim a regime",
                !sparseText.Contains("VERDICT: ThroughputBound") && !sparseText.Contains("VERDICT: AdmissionBound"));

            PipelinePhaseMetrics emptyPhase = new PipelinePhaseMetrics { PhaseName = "empty", GroupName = "FP-9a" };
            string emptyText = Render(emptyPhase);
            ok &= Check("an empty phase renders NO DATA, distinct from UNDECIDABLE",
                emptyText.Contains("NO DATA") && !emptyText.Contains("UNDECIDABLE (only"));

            // The transition: plenty of observations, but no regime is meaningful. A floor cannot catch this.
            PipelinePhaseMetrics transition = new PipelinePhaseMetrics
            {
                PhaseName = "Drain + Save + Unload", GroupName = "Transition", RegimeBearing = false,
            };
            transition.StopReasonCounts[(int)PipelinePass.MeshSchedule, (int)PassStopReason.Quota] = 5000;
            string transitionText = Render(transition);
            ok &= Check("a non-measurement phase renders NO REGIME even with ample observations",
                transitionText.Contains("NO REGIME"));
            ok &= Check("...and does NOT print AdmissionBound (the verdict FP-8 actually gave it)",
                !transitionText.Contains("VERDICT: AdmissionBound"));
            ok &= Check("...which no sample floor could have caught (it is over the floor)",
                PipelineRegimeVerdict.Evaluate(transition.StopReasonCounts, 0, 100).PrimaryDecidable);

            // Both axes, not just the primary. A drain phase that closes enough traces would otherwise print
            // ORDERING-BOUND for discarding work on purpose — the same category error, one axis over.
            PipelinePhaseMetrics wastefulTransition = new PipelinePhaseMetrics
            {
                PhaseName = "Drain + Save + Unload", GroupName = "Transition", RegimeBearing = false,
            };
            wastefulTransition.StopReasonCounts[(int)PipelinePass.MeshSchedule, (int)PassStopReason.Quota] = 5000;
            wastefulTransition.DispositionCounts[(int)TraceDisposition.UnloadedBeforeMeshApplied] = 900;
            wastefulTransition.DispositionCounts[(int)TraceDisposition.MeshApplied] = 100;

            string wastefulText = Render(wastefulTransition);
            ok &= Check("a non-measurement phase is spared the ORDERING axis too (90% waste, 1 000 traces)",
                !wastefulText.Contains("ORDERING-BOUND"));

            // A healthy measurement phase must still render its regime — the guards must not swallow results.
            PipelinePhaseMetrics good = new PipelinePhaseMetrics { PhaseName = "good", GroupName = "FP-9a" };
            good.StopReasonCounts[(int)PipelinePass.MeshSchedule, (int)PassStopReason.Quota] = 5000;
            string goodText = Render(good);
            ok &= Check("a well-sampled measurement phase still prints its regime",
                goodText.Contains("VERDICT: AdmissionBound"));

            ok &= Check("PipelinePhaseMetrics defaults to regime-bearing (only the transition opts out)",
                good.RegimeBearing);

            return ok;
        }

        /// <summary>
        /// FP-9b: the benchmark route's geometry, pinned across the view distances and speed configurations
        /// a sweep actually uses.
        /// <para>
        /// Every property here is one FP-8 violated while looking healthy. Generation waypoints collapsed
        /// <b>12 → 8 → 6 → 4 → 4</b> across vd 5/8/10/15/20, so the sweep at vd 20 was a quarter of the route
        /// it was at vd 5; the route was shorter than the speed phases needed at <i>every</i> view distance
        /// (9 344 m against 11 400 m even at the default), so the fastest generation phase was cut short and
        /// at vd ≥ 10 never ran at all; and the loading tour shrank 84 → 54 chunks because its extent was
        /// derived from <c>LoadDistance</c>. None of it was guarded, because the geometry lived inside a
        /// method that mutated instance lists and no baseline could reach it.
        /// </para>
        /// <para>
        /// The tour-coverage assertion is the load-bearing one: the generation pass is time-bounded, so it
        /// stops partway along the route, and if the tour is not inside the part actually walked then the
        /// loading pass generates terrain instead of loading it — measuring the wrong pipeline under the
        /// right label.
        /// </para>
        /// </summary>
        private static bool RunB17RouteGeometry()
        {
            float[] defaultSpeeds = { 10f, 20f, 50f, 100f, 200f };
            float[] stressSpeeds = { 10f, 20f, 50f, 100f, 200f, 300f, 500f };
            int[] viewDistances = { 5, 8, 10, 15, 20 };
            const int dataLoadBuffer = 3;

            bool ok = true;

            foreach ((string label, float[] speeds, float phaseSeconds) in new[]
                     {
                         ("default", defaultSpeeds, 30f),
                         ("stress +300/500", stressSpeeds, 30f),
                         ("60 s phases", defaultSpeeds, 60f),
                     })
            {
                // Waypoint request is the OUTER loop so constancy is asserted across view distances at a
                // fixed request — the property that matters — rather than across different requests. The
                // non-default requests are here because the default alone hid a mis-centred tour: 12
                // waypoints happened to fall inside the covered band, 24 and 64 did not.
                foreach (int requestedWaypoints in new[] { 12, 24, 64 })
                {
                    int firstWaypoints = -1;

                    foreach (int viewDistance in viewDistances)
                    {
                        int loadDistance = viewDistance + dataLoadBuffer;
                        BenchmarkRouteGeometry g = new BenchmarkRouteGeometry(loadDistance, speeds, phaseSeconds,
                            requestedWaypoints);

                        // 1. Route must outlast the timed phases, or a phase is cut short (the FP-8 defect).
                        ok &= Check($"[{label}] vd {viewDistance}: route {g.RouteLengthMeters:F0} m covers the " +
                                    $"{g.TimedTravelMeters:F0} m the phases travel",
                            g.RouteLengthMeters >= g.TimedTravelMeters);

                        // 2. Waypoint count must not depend on view distance.
                        if (firstWaypoints < 0) firstWaypoints = g.GenerationWaypoints;
                        ok &= Check($"[{label}] vd {viewDistance}: {g.GenerationWaypoints} generation waypoints, " +
                                    $"same as vd {viewDistances[0]}",
                            g.GenerationWaypoints == firstWaypoints);

                        // 3. The tour must be the same size at every view distance.
                        ok &= Check($"[{label}] vd {viewDistance}: loading tour is the full " +
                                    $"{BenchmarkRouteGeometry.LoadingTourChunks} chunks, not shrunk",
                            !g.TourWasShrunk && g.TourChunks == BenchmarkRouteGeometry.LoadingTourChunks);

                        // 4. And it must LIE INSIDE the area the timed phases cover, with a LoadDistance margin.
                        //
                        // Asserted on the final coordinates, NOT by re-running the sizing helper with the
                        // constructor's own arguments — that earlier form was a tautology of assertion 3 and let
                        // a mis-CENTRED tour pass while claiming coverage it did not have.
                        float margin = loadDistance * VoxelData.ChunkWidth;
                        bool insideZ = g.TourMinZ >= g.CoveredMinZ + margin && g.TourMaxZ <= g.CoveredMaxZ - margin;
                        bool insideX = g.TourMinX >= g.MinEdge + margin && g.TourMaxX <= g.MaxEdge - margin;

                        ok &= Check($"[{label}] vd {viewDistance}: tour Z [{g.TourMinZ:F0},{g.TourMaxZ:F0}] lies " +
                                    $"inside covered Z [{g.CoveredMinZ:F0},{g.CoveredMaxZ:F0}] with margin",
                            insideZ);
                        ok &= Check($"[{label}] vd {viewDistance}: tour X [{g.TourMinX:F0},{g.TourMaxX:F0}] lies " +
                                    $"inside swept X [{g.MinEdge:F0},{g.MaxEdge:F0}] with margin",
                            insideX);
                    }
                }
            }

            // The tour is centred on the COVERED band, not the full sweep. Pinned explicitly because the two
            // coincide only when the timed phases finish every row — which, the route carrying headroom by
            // design, they never do.
            BenchmarkRouteGeometry centred = new BenchmarkRouteGeometry(23, defaultSpeeds, 30f, 24);
            float tourCentreZ = (centred.TourMinZ + centred.TourMaxZ) * 0.5f;
            float coveredCentreZ = (centred.CoveredMinZ + centred.CoveredMaxZ) * 0.5f;
            float sweepCentreZ = (centred.MinEdgeZ + centred.MaxEdgeZ) * 0.5f;
            ok &= Check("the tour is centred on the COVERED band in Z",
                Mathf.Abs(tourCentreZ - coveredCentreZ) < 0.01f);
            ok &= Check("...which is demonstrably not the full sweep's centre (completed rows < rows)",
                centred.CompletedRows < centred.Rows && Mathf.Abs(coveredCentreZ - sweepCentreZ) > 1f);

            // A degenerate speed list cannot cover any tour — it must SAY so rather than quietly shipping a
            // loading pass that generates terrain.
            BenchmarkRouteGeometry degenerate = new BenchmarkRouteGeometry(8, new[] { 10f, 20f }, 30f, 12);
            ok &= Check("a speed list too slow to cover the tour reports TourWasShrunk",
                degenerate.TourWasShrunk);
            ok &= Check("...and floors the tour rather than collapsing it to a point",
                degenerate.TourChunks >= BenchmarkRouteGeometry.MinimumTourChunks);

            // The waypoint request is a floor: asking for fewer than the distance needs must not shorten the
            // route below what the phases travel.
            BenchmarkRouteGeometry floored = new BenchmarkRouteGeometry(8, defaultSpeeds, 30f, 4);
            ok &= Check("a small waypoint request still yields a route the phases can complete",
                floored.RouteLengthMeters >= floored.TimedTravelMeters);

            return ok;
        }

        /// <summary>
        /// FP-11a: the denominator the ensure-pass coverage figure is measured against, and the accounting
        /// that turns it into a number.
        /// <para>
        /// FP-10 could not say whether the high-view-distance loading pass measured loading or partly
        /// re-measured generation — the ensure sweep is subject to the same panic gate as everything else and
        /// ran 92.3 % throttled at vd 32, but nothing in the capture reported its actual coverage. The
        /// footprint below is what makes that answerable: the union of the resident load square swept along
        /// the tour circuit, i.e. exactly the chunks the loading pass will ask for.
        /// </para>
        /// <para>
        /// The <b>closed circuit</b> assertion is the load-bearing one, and it guards a defect this scenario
        /// was written alongside: the loading pass loops its waypoints and therefore flies the return leg from
        /// the last back to the first, while the ensure sweep walked points 0..N-1 and stopped. At vd 5 the
        /// load radius (8 chunks) does not reach across that leg from any other part of the route, so its
        /// terrain was generated by the "loading" pass. The midpoint of that leg is asserted present at vd 5
        /// specifically, where no other leg can cover it.
        /// </para>
        /// <para>
        /// <b>Negative-coordinate correctness</b> is inherited rather than re-tested: the footprint converts
        /// voxel to chunk space through <see cref="ChunkMath.VoxelToChunk"/>, whose floor-division behavior
        /// for both signs is pinned by the "Chunk Math" suite. The dependency itself is asserted here so a
        /// future switch to a truncating <c>/ 16</c> reds a baseline rather than silently mis-placing the
        /// footprint in negative chunk space (which FP-10's regions reach).
        /// </para>
        /// <para><b>Prove-red (demonstrated by temporary mutation):</b> iterating
        /// <c>i &lt; points.Count - 1</c> instead of wrapping in <c>BuildTourChunkSet</c> reds the return-leg
        /// assertion (and, downstream of it, the marking assertions that use that probe), with the other
        /// seventeen baselines untouched; passing <c>0</c> instead of <c>loadDistance</c> to
        /// <c>MarkLoadSquare</c> reds the corner and extent assertions. Note the extent assertion is written
        /// against the footprint's <i>emitted</i> min/max versus tour-derived arithmetic precisely so that it
        /// does red there — an earlier "nothing lies outside the bounds" form re-derived the rasterizer's own
        /// grid expressions, could not fail by construction, and survived that mutation.</para>
        /// </summary>
        private static bool RunB18TourCoverage()
        {
            float[] defaultSpeeds = { 10f, 20f, 50f, 100f, 200f };
            const int dataLoadBuffer = 3;

            bool ok = Check("ChunkMath.VoxelToChunk floor-divides negatives (the footprint's sign safety)",
                ChunkMath.VoxelToChunk(-1) == -1 && ChunkMath.VoxelToChunk(-16) == -1
                                                 && ChunkMath.VoxelToChunk(-17) == -2);

            HashSet<ChunkCoord> footprint = new HashSet<ChunkCoord>();

            foreach (int viewDistance in new[] { 5, 8, 20, 32 })
            {
                int loadDistance = viewDistance + dataLoadBuffer;
                BenchmarkRouteGeometry g = new BenchmarkRouteGeometry(loadDistance, defaultSpeeds, 30f, 12);
                g.BuildTourChunkSet(loadDistance, footprint);

                ok &= Check($"vd {viewDistance}: footprint is non-empty ({footprint.Count:N0} chunks)",
                    footprint.Count > 0);

                // The four tour corners are waypoints, so each carries a full load square — which means the
                // corners of the INFLATED bounding box are members. This is what fails if the sweep is not
                // inflated by the load distance at all.
                int minX = ChunkMath.VoxelToChunk(Mathf.FloorToInt(g.TourMinX)) - loadDistance;
                int maxX = ChunkMath.VoxelToChunk(Mathf.FloorToInt(g.TourMaxX)) + loadDistance;
                int minZ = ChunkMath.VoxelToChunk(Mathf.FloorToInt(g.TourMinZ)) - loadDistance;
                int maxZ = ChunkMath.VoxelToChunk(Mathf.FloorToInt(g.TourMaxZ)) + loadDistance;

                ok &= Check($"vd {viewDistance}: all four load-inflated tour corners are in the footprint",
                    footprint.Contains(new ChunkCoord(minX, minZ))
                    && footprint.Contains(new ChunkCoord(maxX, minZ))
                    && footprint.Contains(new ChunkCoord(minX, maxZ))
                    && footprint.Contains(new ChunkCoord(maxX, maxZ)));

                // The footprint's ACTUAL extent, against arithmetic derived from the tour rather than from the
                // rasterizer's own bounds expressions. A "nothing lies outside the grid" check would be a
                // tautology — the set is emitted BY walking that grid — and would survive the very mutation
                // it appears to guard. The tour spans TourChunks x 16 voxels from a multiple of 8, so it
                // always touches exactly one more chunk column than its width; the load square then adds
                // loadDistance on each side.
                int expectedSpan = g.TourChunks + 1 + 2 * loadDistance;

                int actualMinX = int.MaxValue, actualMaxX = int.MinValue;
                int actualMinZ = int.MaxValue, actualMaxZ = int.MinValue;
                foreach (ChunkCoord c in footprint)
                {
                    if (c.X < actualMinX) actualMinX = c.X;
                    if (c.X > actualMaxX) actualMaxX = c.X;
                    if (c.Z < actualMinZ) actualMinZ = c.Z;
                    if (c.Z > actualMaxZ) actualMaxZ = c.Z;
                }

                ok &= Check($"vd {viewDistance}: footprint spans {actualMaxX - actualMinX + 1} x " +
                            $"{actualMaxZ - actualMinZ + 1} chunks, the expected {expectedSpan} x {expectedSpan}",
                    actualMaxX - actualMinX + 1 == expectedSpan
                    && actualMaxZ - actualMinZ + 1 == expectedSpan);

                ok &= Check($"vd {viewDistance}: that extent is placed on the tour, not merely sized like it",
                    actualMinX == minX && actualMaxX == maxX
                                       && actualMinZ == minZ && actualMaxZ == maxZ);

                // The tour center is crossed by both mid legs, so it is always covered.
                ok &= Check($"vd {viewDistance}: the tour centre is in the footprint",
                    footprint.Contains(new ChunkCoord(
                        ChunkMath.VoxelToChunk(Mathf.FloorToInt((g.TourMinX + g.TourMaxX) * 0.5f)),
                        ChunkMath.VoxelToChunk(Mathf.FloorToInt((g.TourMinZ + g.TourMaxZ) * 0.5f)))));
            }

            // --- The closed circuit, asserted where ONLY the return leg can satisfy it ---
            // The return leg runs up the tour's left edge from (minX, minZ) to (minX, midZ) — 32 chunks at the
            // fixed 64-chunk tour. For a point 'm' chunks along it, the Chebyshev distance to the nearest
            // other leg is min(m, ceil(m/2), 32 - m): 'm' to the corner waypoint, ceil(m/2) to the main
            // diagonal, and 32 - m to both the mid-line and the (midX,maxZ)->(minX,midZ) leg. That minimum
            // peaks at m = 21 with a value of 11, comfortably outside vd 5's 8-chunk load radius — so this one
            // chunk is reachable from the return leg and from nothing else. The quarter-point, by contrast,
            // sits exactly 8 chunks from the diagonal and would pass on an OPEN circuit, pinning nothing.
            const int vd5Load = 5 + dataLoadBuffer;
            const int returnLegProbeChunks = 21;

            BenchmarkRouteGeometry near = new BenchmarkRouteGeometry(vd5Load, defaultSpeeds, 30f, 12);
            near.BuildTourChunkSet(vd5Load, footprint);

            ok &= Check("vd 5 tour is the full 64 chunks (the probe arithmetic below assumes it)",
                near.TourChunks == BenchmarkRouteGeometry.LoadingTourChunks);

            ChunkCoord returnLegProbe = new ChunkCoord(
                ChunkMath.VoxelToChunk(Mathf.FloorToInt(near.TourMinX)),
                ChunkMath.VoxelToChunk(Mathf.FloorToInt(near.TourMinZ)) + returnLegProbeChunks);

            ok &= Check($"vd 5: the return leg at +{returnLegProbeChunks} chunks is in the footprint " +
                        "(circuit is closed; no other leg reaches it)",
                footprint.Contains(returnLegProbe));

            // The tour length must account for that leg too, or the ensure sweep's derived duration — and the
            // trace-capacity hint sized from it — are short by one leg.
            ok &= Check("tour length exceeds the open-circuit walk (return leg included)",
                near.TourLengthMeters > OpenCircuitLength(near));

            // --- Coverage accounting: the numbers, and the refusal to invent one ---
            try
            {
                BenchmarkTourCoverage.Arm(near, vd5Load);

                ok &= Check("an armed but unfrozen tracker reports NO measurement",
                    !BenchmarkTourCoverage.HasMeasurement && !BenchmarkTourCoverage.IsSufficient);
                ok &= Check("...while already carrying its denominator",
                    BenchmarkTourCoverage.RequiredChunks == footprint.Count
                    && BenchmarkTourCoverage.CoveredChunks == 0);

                // Only chunks inside the footprint count — a run generates far more terrain than the tour.
                BenchmarkTourCoverage.MarkPopulated(returnLegProbe);
                BenchmarkTourCoverage.MarkPopulated(new ChunkCoord(int.MinValue / 2, int.MinValue / 2));

                ok &= Check("marking credits footprint chunks and ignores everything else",
                    BenchmarkTourCoverage.CoveredChunks == 1);

                BenchmarkTourCoverage.MarkPopulated(returnLegProbe);
                ok &= Check("re-marking the same chunk does not double-count it",
                    BenchmarkTourCoverage.CoveredChunks == 1);

                // --- The two instants: ensure-sweep snapshot, then the post-transition freeze ---
                // The gap between them is terrain the panic gate deferred out of the sweep that the
                // transition's job drain finished and saved anyway. Crediting it is the difference between a
                // capture reading inadmissible and reading clean, so both ends are pinned here.
                BenchmarkTourCoverage.SnapshotEnsurePass();
                ok &= Check("the ensure-pass snapshot records the count at that instant",
                    BenchmarkTourCoverage.HasEnsurePassSnapshot
                    && BenchmarkTourCoverage.EnsurePassCoveredChunks == 1);
                ok &= Check("...and taking it does NOT stop accrual (the transition still counts)",
                    BenchmarkTourCoverage.Armed && !BenchmarkTourCoverage.HasMeasurement);

                int marked = 0;
                foreach (ChunkCoord c in footprint)
                {
                    if (marked >= 4) break;
                    BenchmarkTourCoverage.MarkPopulated(c);
                    marked++;
                }

                ok &= Check("chunks populated after the snapshot raise the live count",
                    BenchmarkTourCoverage.CoveredChunks > 1);
                ok &= Check("...while the ensure-pass figure stays fixed at the snapshot",
                    BenchmarkTourCoverage.EnsurePassCoveredChunks == 1);

                int atFreeze = BenchmarkTourCoverage.CoveredChunks;
                BenchmarkTourCoverage.Freeze();
                ok &= Check("freezing yields a measurement, and it is far below the sufficiency floor",
                    BenchmarkTourCoverage.HasMeasurement && !BenchmarkTourCoverage.IsSufficient);

                // The whole point of freezing: the loading pass populates the same chunks by definition, so
                // marking past the transition would drive coverage to 100 % on every capture.
                foreach (ChunkCoord c in footprint) BenchmarkTourCoverage.MarkPopulated(c);
                ok &= Check("marks after the freeze are ignored (the loading pass cannot inflate coverage)",
                    BenchmarkTourCoverage.CoveredChunks == atFreeze);

                // Reset is the abort path (BenchmarkController.OnDestroy): it must leave NO measurement, so a
                // run that never froze cannot have a stale figure rendered as this run's.
                BenchmarkTourCoverage.Reset();
                ok &= Check("Reset disarms and leaves no measurement behind",
                    !BenchmarkTourCoverage.Armed && !BenchmarkTourCoverage.HasMeasurement
                                                 && !BenchmarkTourCoverage.HasEnsurePassSnapshot
                                                 && BenchmarkTourCoverage.RequiredChunks == 0);

                return ok;
            }
            finally
            {
                // The abort path is also the correct teardown: an armed tracker would charge every later
                // editor play session a lookup per populated chunk, and a stale frozen figure is precisely
                // the false green this instrument exists to prevent.
                BenchmarkTourCoverage.Reset();
            }
        }

        /// <summary>
        /// P-8: the thresholds the gate is actually evaluated against, pinned across a view-distance sweep.
        /// <para>
        /// FP-10 measured the defect this fixes: a fixed 256/128 pair is 88.6 % of the resident square at
        /// view distance 5 and 5.1 % at 32, so from vd 15 up the gate is essentially never open and the
        /// pipeline never runs in the regime its budgets were tuned for — admitted work grew only 1.5–1.7×
        /// across the sweep while requests grew 4.5–4.8×.
        /// </para>
        /// <para>
        /// <b>Expectations are literals, deliberately.</b> Asserting against a re-derivation of
        /// <c>configured × width / 17</c> would restate the implementation and pass for any consistent
        /// mistake — the tautology B18's extent assertion was rewritten to avoid. These six pairs were
        /// computed once, reviewed, and frozen; a change to the scale must change this table too, which is
        /// exactly the friction a tuning constant should have.
        /// </para>
        /// <para>
        /// The scale follows the square's <b>width</b>, not its area: FP-10 F4 found the gate simultaneously
        /// succeeding at protecting frame time (at vd ≥ 20 flying faster costs LESS CPU, because the faster
        /// phase trips the gate), so an area-proportional threshold — which would hold the ratio at vd 5's
        /// never-closes 88.6 % everywhere — would trade that away.
        /// </para>
        /// <para>
        /// The first assertion is the one that guards the <i>constant</i> rather than the arithmetic: it
        /// reads <c>new Settings().ResidentWidth</c>, so a change to the shipped default view distance reds
        /// here instead of silently rescaling every default install's gate.
        /// </para>
        /// <para><b>Prove-red (demonstrated by temporary mutation):</b> returning <c>configured</c> unscaled
        /// from the <c>scaleWithResidency</c> branch of
        /// <see cref="GenerationPanicGate.DeriveThresholds"/> reds every scaled row of the table while leaving
        /// the vd-5 identity, the flag-off rows and all eighteen other baselines green — which is the point:
        /// the identity at the reference width is what makes the default configuration byte-identical to
        /// pre-P-8 behavior, so it must NOT move.</para>
        /// </summary>
        private static bool RunB19GateThresholdScaling()
        {
            const int configuredClose = 256;
            const int configuredReopen = 128;

            // The invariant the whole design rests on, asserted against the SHIPPED default rather than
            // against the literal 5 the table below pins. ReferenceResidentWidth encodes
            // 2 x (default viewDistance + DATA_LOAD_BUFFER) + 1 with no compile-time link to either input, so
            // changing the default view distance would silently hand every default install a scaled pair —
            // the one configuration that must stay byte-identical to pre-P-8 behavior — while every row
            // below stayed green, because they name their view distance explicitly.
            bool ok = Check("the shipped default configuration sits exactly at the reference width " +
                            $"({new Settings().ResidentWidth} vs {GenerationPanicGate.ReferenceResidentWidth})",
                new Settings().ResidentWidth == GenerationPanicGate.ReferenceResidentWidth);

            GenerationPanicGate.DeriveThresholds(new Settings().ResidentWidth, configuredClose,
                configuredReopen, true, out int defaultClose, out int defaultReopen);
            ok &= Check("...so the default install's gate is byte-identical to pre-P-8 (256 / 128)",
                defaultClose == configuredClose && defaultReopen == configuredReopen);

            // vd -> resident width -> the effective pair. Hand-computed at review time, frozen here.
            (int viewDistance, int residentWidth, int close, int reopen)[] sweep =
            {
                (5, 17, 256, 128), // the reference: scaling is an identity, pre-P-8 behavior preserved
                (8, 23, 346, 173),
                (10, 27, 407, 203),
                (15, 37, 557, 279),
                (20, 47, 708, 354),
                (32, 71, 1069, 535),
            };

            foreach ((int viewDistance, int residentWidth, int close, int reopen) row in sweep)
            {
                Settings settings = new Settings
                {
                    viewDistance = row.viewDistance,
                    panicGateCloseThreshold = configuredClose,
                    panicGateReopenThreshold = configuredReopen,
                    scalePanicGateThresholdsWithResidency = true,
                };

                ok &= Check($"vd {row.viewDistance} -> resident width {row.residentWidth}",
                    settings.ResidentWidth == row.residentWidth);

                GenerationPanicGate.DeriveThresholds(settings.ResidentWidth, configuredClose, configuredReopen,
                    true, out int closeAt, out int reopenAt);

                ok &= Check($"vd {row.viewDistance} -> close {closeAt} (expected {row.close})",
                    closeAt == row.close);
                ok &= Check($"vd {row.viewDistance} -> reopen {reopenAt} (expected {row.reopen})",
                    reopenAt == row.reopen);

                // The hysteresis band must survive scaling and rounding at every point, or the gate flips
                // every frame — halving admissions and spamming two log lines per flip inside Update.
                ok &= Check($"vd {row.viewDistance}: reopen stays strictly below close",
                    reopenAt < closeAt);

                // The rollback leg must be byte-identical to pre-P-8 behavior at EVERY view distance.
                GenerationPanicGate.DeriveThresholds(settings.ResidentWidth, configuredClose, configuredReopen,
                    false, out int legacyClose, out int legacyReopen);

                ok &= Check($"vd {row.viewDistance}: scaling OFF returns the configured pair unchanged",
                    legacyClose == configuredClose && legacyReopen == configuredReopen);

                // The report must describe the run it belongs to: the snapshot's effective values come from
                // this same helper, so a divergence here is a report that lies about its own capture.
                PipelineSettingsSnapshot snap = new PipelineSettingsSnapshot(settings);
                ok &= Check($"vd {row.viewDistance}: the settings snapshot reports the same effective pair",
                    snap.EffectiveCloseThreshold == closeAt && snap.EffectiveReopenThreshold == reopenAt
                                                            && snap.ScalePanicGateWithResidency);
                ok &= Check($"vd {row.viewDistance}: ...while still reporting the configured pair verbatim",
                    snap.PanicGateCloseThreshold == configuredClose
                    && snap.PanicGateReopenThreshold == configuredReopen);
            }

            // Monotonicity: a wider resident square never yields a tighter gate. The whole premise.
            GenerationPanicGate.DeriveThresholds(17, configuredClose, configuredReopen, true,
                out int nearClose, out int _);
            GenerationPanicGate.DeriveThresholds(71, configuredClose, configuredReopen, true,
                out int farClose, out int _);
            ok &= Check("a wider resident square yields a strictly larger close threshold",
                farClose > nearClose);

            // The effective ratio must now fall as 1/width, not 1/width^2 — the change stated as one number.
            // At vd 5 -> 32 the width grows 71/17 = 4.18x, so the ratio should fall by about that factor
            // (88.6 % -> 21.2 %) rather than by its square (-> 5.1 %, the pre-P-8 figure FP-10 measured).
            PipelineSettingsSnapshot near = new PipelineSettingsSnapshot(new Settings
            {
                viewDistance = 5, panicGateCloseThreshold = configuredClose,
                panicGateReopenThreshold = configuredReopen, scalePanicGateThresholdsWithResidency = true,
            });
            PipelineSettingsSnapshot far = new PipelineSettingsSnapshot(new Settings
            {
                viewDistance = 32, panicGateCloseThreshold = configuredClose,
                panicGateReopenThreshold = configuredReopen, scalePanicGateThresholdsWithResidency = true,
            });

            double ratioDrop = near.EffectiveCloseThresholdPercentOfResident /
                               far.EffectiveCloseThresholdPercentOfResident;
            ok &= Check($"the effective ratio falls ~4.2x from vd 5 to vd 32 (linear), not ~17x (quadratic) — {ratioDrop:F2}x",
                ratioDrop > 3.5 && ratioDrop < 5.0);
            ok &= Check("...and the unscaled ratio is still reported, unchanged from FP-10's 5.1 % at vd 32",
                Math.Abs(far.PanicGateCloseThresholdPercentOfResident - 100.0 * 256 / 5041) < 1e-9);

            // --- Degenerate configurations: sanitization must survive scaling ---
            GenerationPanicGate.DeriveThresholds(71, 0, 0, true, out int zeroClose, out int zeroReopen);
            ok &= Check("a zero close threshold floors at 1, with reopen below it",
                zeroClose == 1 && zeroReopen == 0);

            GenerationPanicGate.DeriveThresholds(71, 100, 500, true, out int invClose, out int invReopen);
            ok &= Check("an inverted band is clamped back inside itself after scaling",
                invReopen < invClose);

            GenerationPanicGate.DeriveThresholds(71, 256, -50, true, out int negClose, out int negReopen);
            ok &= Check("a negative reopen cannot wedge the gate shut (floored at 0)",
                negReopen >= 0 && negReopen < negClose);

            // An absurd persisted threshold must not overflow into a negative — that would sanitize to a
            // permanently closed gate, i.e. a pipeline that admits nothing at all.
            GenerationPanicGate.DeriveThresholds(71, int.MaxValue, int.MaxValue / 2, true,
                out int hugeClose, out int hugeReopen);
            ok &= Check("an int.MaxValue threshold scales without overflowing negative",
                hugeClose > 0 && hugeReopen >= 0 && hugeReopen < hugeClose);

            // A degenerate resident width is floored, never used as a zero or negative divisor/multiplier.
            GenerationPanicGate.DeriveThresholds(0, configuredClose, configuredReopen, true,
                out int degenerateClose, out int _);
            ok &= Check("a zero resident width is floored to 1 rather than collapsing the threshold",
                degenerateClose >= 1);

            return ok;
        }

        /// <summary>Walks the tour's points in order without the return leg — the pre-FP-11a length.</summary>
        /// <param name="geometry">The route geometry whose tour is measured.</param>
        /// <returns>Total length of the open walk, in metres.</returns>
        private static float OpenCircuitLength(BenchmarkRouteGeometry geometry)
        {
            List<Vector3> points = new List<Vector3>(12);
            geometry.BuildTourPoints(0f, points);

            float total = 0f;
            for (int i = 1; i < points.Count; i++) total += Vector3.Distance(points[i - 1], points[i]);

            return total;
        }

        /// <summary>Renders one phase through the real report section and returns the text.</summary>
        /// <param name="phase">The phase to render.</param>
        /// <returns>The rendered Pipeline section.</returns>
        private static string Render(PipelinePhaseMetrics phase)
        {
            StringBuilder sb = new StringBuilder();
            PipelineReportSection.Append(sb, new List<PipelinePhaseMetrics> { phase });
            return sb.ToString();
        }

        /// <summary>
        /// FP-6: the two <i>derived</i> values on the settings snapshot, pinned because the FP-4 sweep
        /// reasons from them rather than from the raw thresholds. The resident square is the denominator for
        /// the gate ratio, and that ratio — not the absolute threshold — is what predicts whether the panic
        /// gate ever closes, since the threshold is fixed while the population it guards grows with the
        /// square of view distance.
        /// <para>Built from an explicit <see cref="Settings"/> instance rather than
        /// <c>SettingsManager.LoadSettings()</c>: a baseline must not depend on whatever the user currently
        /// has configured.</para>
        /// </summary>
        private static bool RunB12SettingsSnapshot()
        {
            // The three view distances of the FP-4 sweep, with their hand-computed geometry.
            // LoadDistance = viewDistance + DATA_LOAD_BUFFER(3).
            (int viewDistance, int expectedLoad, int expectedResident, double expectedRatio)[] cases =
            {
                (5, 8, 289, 100.0 * 256 / 289), // default — 88.6 %, gate never observed closing
                (10, 13, 729, 100.0 * 256 / 729), // 35.1 %
                (20, 23, 2209, 100.0 * 256 / 2209), // 11.6 % — gate closed 96.4 % of frames
            };

            bool ok = true;
            foreach ((int viewDistance, int expectedLoad, int expectedResident, double expectedRatio) c in cases)
            {
                Settings settings = new Settings
                {
                    viewDistance = c.viewDistance,
                    panicGateCloseThreshold = 256,
                };
                PipelineSettingsSnapshot snap = new PipelineSettingsSnapshot(settings);

                ok &= Check($"vd {c.viewDistance} -> LoadDistance {c.expectedLoad}",
                    snap.LoadDistance == c.expectedLoad);
                ok &= Check($"vd {c.viewDistance} -> resident square {c.expectedResident:N0}",
                    snap.ResidentChunks == c.expectedResident);
                ok &= Check($"vd {c.viewDistance} -> gate threshold {c.expectedRatio:F1}% of resident",
                    Math.Abs(snap.PanicGateCloseThresholdPercentOfResident - c.expectedRatio) < 1e-9);
            }

            // The ratio must FALL as view distance rises — the monotonicity the F5 argument rests on.
            PipelineSettingsSnapshot near = new PipelineSettingsSnapshot(
                new Settings { viewDistance = 5, panicGateCloseThreshold = 256 });
            PipelineSettingsSnapshot far = new PipelineSettingsSnapshot(
                new Settings { viewDistance = 20, panicGateCloseThreshold = 256 });
            ok &= Check("a fixed threshold is a SMALLER share of the resident set at higher view distance",
                far.PanicGateCloseThresholdPercentOfResident < near.PanicGateCloseThresholdPercentOfResident);

            // Degenerate guard: a view distance negative enough to drive the square width below 1 must clamp,
            // never produce a zero (divide-by-zero in the ratio) or a bogus positive from squaring a negative.
            PipelineSettingsSnapshot degenerate = new PipelineSettingsSnapshot(
                // ReSharper disable once ValueRangeAttributeViolation
                new Settings { viewDistance = -10, panicGateCloseThreshold = 256 });
            ok &= Check("width below 1 clamps: resident square is 1, not 0 and not a squared negative",
                degenerate.ResidentWidth == 1 && degenerate.ResidentChunks == 1);
            ok &= Check("the gate ratio stays finite at the clamped floor",
                !double.IsInfinity(degenerate.PanicGateCloseThresholdPercentOfResident) &&
                !double.IsNaN(degenerate.PanicGateCloseThresholdPercentOfResident));

            return ok;
        }

        /// <summary>
        /// Busy-waits for approximately <paramref name="ms"/> milliseconds of real time.
        /// </summary>
        /// <param name="ms">Milliseconds to spin.</param>
        /// <remarks>
        /// A spin, not a sleep: the parked-time and pass-cost instruments read
        /// <see cref="System.Diagnostics.Stopwatch"/> directly, so the scenario must advance the same clock
        /// they do. Thread.Sleep would also work but overshoots unpredictably on Windows, and these
        /// assertions compare two intervals against each other.
        /// </remarks>
        private static double SpinMs(double ms)
        {
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed.TotalMilliseconds < ms)
            {
            }

            return sw.Elapsed.TotalMilliseconds;
        }

        /// <summary>
        /// Slack allowed between a measured spin and the interval the instrument recorded over it.
        /// </summary>
        /// <remarks>
        /// Assertions compare against what each spin <i>actually</i> took rather than against the nominal
        /// 10 ms, so a GC pause or editor hitch inside a timed window moves the expectation with the
        /// measurement and cannot redden a baseline on its own. This tolerance therefore only has to cover
        /// the handful of stamp calls bracketing each spin — it is deliberately far smaller than the ~10 ms
        /// error every bug these scenarios target would produce, so the discrimination survives.
        /// </remarks>
        private const double PARK_TOLERANCE_MS = 6.0;

        /// <summary>Reads the single parked-time sample from the phase just closed, or -1 if there is not exactly one.</summary>
        /// <returns>The parked total in milliseconds.</returns>
        private static double SingleParkedMs()
        {
            PipelinePhaseMetrics phase = PipelineTelemetry.CompletedPhases[0];
            return phase.ParkedTicksSamples.Count == 1
                ? PipelineTelemetry.TicksToMs(phase.ParkedTicksSamples[0])
                : -1;
        }

        /// <summary>Opens a fresh single-scenario parked-time phase (each runs alone so its sample is unambiguous).</summary>
        /// <param name="name">Phase name.</param>
        private static void BeginParkedPhase(string name)
        {
            PipelineTelemetry.BeginRun();
            PipelineTelemetry.Enabled = true;
            PipelineTelemetry.BeginPhase(name, "P9-0", 4096);
        }

        /// <summary>
        /// P9-0 (§10 q1): work amplification is split three ways — units spent <i>before</i> a chunk's first
        /// delivery, units with no live trace (dominated by post-delivery corrections), and units spent on a
        /// chunk that was unloaded before ever being delivered. Driven end-to-end through the real telemetry
        /// statics (B11's technique).
        /// <para>
        /// The <b>split</b> is what this pins, with hand-computed expected values rather than a
        /// self-consistency check: the ratio decides whether a deliver-then-refine lever has anything to
        /// reorder, and a denominator that quietly absorbed wasted or post-delivery work would still produce
        /// a plausible number — the failure mode a derived ratio makes easy and expensive.
        /// </para>
        /// </summary>
        private static bool RunB20Amplification()
        {
            bool wasEnabled = PipelineTelemetry.Enabled;
            try
            {
                PipelineTelemetry.BeginRun();
                PipelineTelemetry.Enabled = true;
                PipelineTelemetry.BeginPhase("amplification", "P9-0", 4096);

                // Chunk A — delivered after 3 lighting + 1 mesh schedule, then corrected twice.
                ChunkCoord delivered = new ChunkCoord(1, 1);
                PipelineTelemetry.StampRequested(delivered);
                PipelineTelemetry.StampAdmitted(delivered);
                PipelineTelemetry.StampLightScheduled(delivered);
                PipelineTelemetry.StampLightScheduled(delivered);
                PipelineTelemetry.StampLightScheduled(delivered);
                PipelineTelemetry.StampMeshScheduled(delivered);
                PipelineTelemetry.StampMeshApplied(delivered);

                // Post-delivery corrections: the trace is closed, so these must NOT reach pre-delivery.
                PipelineTelemetry.StampLightScheduled(delivered);
                PipelineTelemetry.StampLightScheduled(delivered);
                PipelineTelemetry.StampMeshScheduled(delivered);

                // Chunk B — 4 lighting + 2 mesh schedules, then unloaded mid-flight. Real work, bought nothing.
                ChunkCoord wasted = new ChunkCoord(2, 2);
                PipelineTelemetry.StampRequested(wasted);
                PipelineTelemetry.StampAdmitted(wasted);
                for (int i = 0; i < 4; i++) PipelineTelemetry.StampLightScheduled(wasted);
                PipelineTelemetry.StampMeshScheduled(wasted);
                PipelineTelemetry.StampMeshScheduled(wasted);
                PipelineTelemetry.StampUnloaded(wasted);

                // Chunk C — never traced at all (the saturation / out-of-phase population).
                PipelineTelemetry.StampLightScheduled(new ChunkCoord(3, 3));

                PipelineTelemetry.EndPhase();
                PipelinePhaseMetrics phase = PipelineTelemetry.CompletedPhases[0];

                bool ok = Check("pre-delivery lighting schedules = 3 (chunk A only, before its delivery)",
                    phase.PreDeliveryLightSchedules == 3);
                ok &= Check("pre-delivery mesh schedules = 1",
                    phase.PreDeliveryMeshSchedules == 1);
                ok &= Check("delivered chunks = 1, so lighting per delivered chunk = 3.00",
                    phase.DispositionCounts[(int)TraceDisposition.MeshApplied] == 1);

                // The three load-bearing non-crossings.
                ok &= Check("post-delivery corrections do NOT reach pre-delivery (2 light + 1 mesh, plus 1 untraced)",
                    phase.UntracedLightSchedules == 3 && phase.UntracedMeshSchedules == 1);
                ok &= Check("wasted-chunk work is counted, and counted SEPARATELY (4 light + 2 mesh)",
                    phase.WastedLightSchedules == 4 && phase.WastedMeshSchedules == 2);
                ok &= Check("wasted work never inflates the delivered denominator's numerator",
                    phase.PreDeliveryLightSchedules == 3 && phase.PreDeliveryMeshSchedules == 1);

                // EXHAUSTIVENESS. The three trace arms plus the no-live-trace bucket must account for every
                // schedule stamped — 3+2 light and 1+1 mesh on chunk A, 4/2 on the wasted chunk B, 1 untraced
                // on C. A disposition falling through all three arms (Rerequested and InFlightAtPhaseEnd both
                // did) silently deletes its quota units, and every surviving ratio still looks plausible.
                ok &= Check("every lighting schedule lands in exactly one bucket (3 + 3 + 4 + 0 = 10)",
                    phase.PreDeliveryLightSchedules + phase.UntracedLightSchedules +
                    phase.WastedLightSchedules + phase.UnresolvedLightSchedules == 10);
                ok &= Check("every mesh schedule lands in exactly one bucket (1 + 1 + 2 + 0 = 4)",
                    phase.PreDeliveryMeshSchedules + phase.UntracedMeshSchedules +
                    phase.WastedMeshSchedules + phase.UnresolvedMeshSchedules == 4);

                // The population that fell through before: a trace superseded mid-flight, and one still in
                // flight when the phase ended. Both consumed real quota.
                PipelineTelemetry.BeginRun();
                PipelineTelemetry.Enabled = true;
                PipelineTelemetry.BeginPhase("unresolved", "P9-0", 4096);

                ChunkCoord superseded = new ChunkCoord(8, 8);
                PipelineTelemetry.StampRequested(superseded);
                PipelineTelemetry.StampAdmitted(superseded);
                PipelineTelemetry.StampLightScheduled(superseded);
                PipelineTelemetry.StampLightScheduled(superseded);
                PipelineTelemetry.StampRequested(superseded); // flush-and-restart -> Rerequested

                ChunkCoord stillInFlight = new ChunkCoord(9, 9);
                PipelineTelemetry.StampRequested(stillInFlight);
                PipelineTelemetry.StampAdmitted(stillInFlight);
                PipelineTelemetry.StampMeshScheduled(stillInFlight);
                PipelineTelemetry.EndPhase(); // -> InFlightAtPhaseEnd

                phase = PipelineTelemetry.CompletedPhases[0];
                ok &= Check("a re-requested trace's 2 lighting schedules are counted as unresolved, not dropped",
                    phase.UnresolvedLightSchedules == 2);
                ok &= Check("a trace still in flight at phase end keeps its mesh schedule too",
                    phase.UnresolvedMeshSchedules == 1);
                ok &= Check("...and neither leaks into pre-delivery or wasted",
                    phase.PreDeliveryLightSchedules == 0 && phase.PreDeliveryMeshSchedules == 0 &&
                    phase.WastedLightSchedules == 0 && phase.WastedMeshSchedules == 0);

                // Schedules are counted, completions are not — the two differ by whatever is in flight, and
                // only the schedule spends quota.
                PipelineTelemetry.BeginRun();
                PipelineTelemetry.Enabled = true;
                PipelineTelemetry.BeginPhase("schedule vs completion", "P9-0", 4096);
                ChunkCoord both = new ChunkCoord(4, 4);
                PipelineTelemetry.StampRequested(both);
                PipelineTelemetry.StampAdmitted(both);
                PipelineTelemetry.StampLightScheduled(both);
                PipelineTelemetry.StampLightScheduled(both);
                PipelineTelemetry.StampLit(both);
                PipelineTelemetry.StampMeshApplied(both);
                PipelineTelemetry.EndPhase();

                phase = PipelineTelemetry.CompletedPhases[0];
                ok &= Check("2 schedules and 1 completion are recorded as 2 and 1, not conflated",
                    phase.PreDeliveryLightSchedules == 2);

                return ok;
            }
            finally
            {
                PipelineTelemetry.BeginRun();
                PipelineTelemetry.Enabled = wasEnabled;
            }
        }

        /// <summary>
        /// P9-0 (§10 q4): per-chunk parked time — the interval a chunk spends flagged for lighting work but
        /// <i>ineligible</i>, sitting in MT-2's waiting set. Invisible to the stop-reason instrument by
        /// construction (a parked chunk leaves the ready set, so it is never walked and never counted as an
        /// <c>AllDeclined</c> candidate), which is why no capture has ever named this class of latency.
        /// <para>
        /// The <b>idempotence</b> assertion is the one with teeth: a re-park that reset the entry timestamp
        /// would under-report exactly the chunks that waited longest, since those are the ones most likely to
        /// be re-parked, and the instrument would then report its healthiest numbers for its worst cases.
        /// </para>
        /// </summary>
        private static bool RunB21ParkedTime()
        {
            bool wasEnabled = PipelineTelemetry.Enabled;
            try
            {
                // --- One park/unpark cycle records the time that actually elapsed. ---
                BeginParkedPhase("single cycle");
                ChunkCoord simple = new ChunkCoord(1, 1);
                Vector2Int simplePos = simple.ToVoxelOrigin();
                PipelineTelemetry.StampRequested(simple);
                PipelineTelemetry.StampAdmitted(simple);
                PipelineTelemetry.StampParked(simplePos);
                double simpleSpin = SpinMs(10);
                PipelineTelemetry.StampUnparked(simplePos);
                PipelineTelemetry.StampMeshApplied(simple);
                PipelineTelemetry.EndPhase();

                double simpleMs = SingleParkedMs();
                bool ok = Check($"one cycle records the elapsed wait (got {simpleMs:F1} ms, spun {simpleSpin:F1} ms)",
                    simpleMs >= simpleSpin - PARK_TOLERANCE_MS && simpleMs <= simpleSpin + PARK_TOLERANCE_MS);

                // --- Idempotent re-park: the second park must NOT restart the interval. ---
                BeginParkedPhase("re-park");
                ChunkCoord reparked = new ChunkCoord(2, 2);
                Vector2Int reparkedPos = reparked.ToVoxelOrigin();
                PipelineTelemetry.StampRequested(reparked);
                PipelineTelemetry.StampAdmitted(reparked);
                PipelineTelemetry.StampParked(reparkedPos);
                double repark1 = SpinMs(10);
                PipelineTelemetry.StampParked(reparkedPos);
                double repark2 = SpinMs(10);
                PipelineTelemetry.StampUnparked(reparkedPos);
                PipelineTelemetry.StampMeshApplied(reparked);
                PipelineTelemetry.EndPhase();

                double reparkedMs = SingleParkedMs();
                double reparkExpected = repark1 + repark2;
                ok &= Check($"re-parking does not restart the interval (got {reparkedMs:F1} ms, both spins " +
                            $"total {reparkExpected:F1} ms — a reset would give ~{repark2:F1} ms)",
                    reparkedMs >= reparkExpected - PARK_TOLERANCE_MS &&
                    reparkedMs <= reparkExpected + PARK_TOLERANCE_MS);

                // --- Never parked: a zero, and a legitimate sample rather than a missing one. ---
                BeginParkedPhase("never parked");
                ChunkCoord never = new ChunkCoord(3, 3);
                PipelineTelemetry.StampRequested(never);
                PipelineTelemetry.StampAdmitted(never);
                PipelineTelemetry.StampUnparked(never.ToVoxelOrigin()); // unpark without park: a no-op
                PipelineTelemetry.StampMeshApplied(never);
                PipelineTelemetry.EndPhase();

                ok &= Check("a chunk that never parked records exactly 0 ms, and still contributes a sample",
                    SingleParkedMs() == 0.0);

                // --- Delivered while STILL parked: the open interval must be closed, not discarded. ---
                BeginParkedPhase("open at delivery");
                ChunkCoord openAtDelivery = new ChunkCoord(4, 4);
                PipelineTelemetry.StampRequested(openAtDelivery);
                PipelineTelemetry.StampAdmitted(openAtDelivery);
                PipelineTelemetry.StampParked(openAtDelivery.ToVoxelOrigin());
                double openSpin = SpinMs(10);
                PipelineTelemetry.StampMeshApplied(openAtDelivery);
                PipelineTelemetry.EndPhase();

                double openMs = SingleParkedMs();
                ok &= Check($"an interval still open at delivery is closed, not dropped (got {openMs:F1} ms, " +
                            $"spun {openSpin:F1} ms)",
                    openMs >= openSpin - PARK_TOLERANCE_MS && openMs <= openSpin + PARK_TOLERANCE_MS);

                // --- Two cycles SUM, and the ELIGIBLE gap between them is excluded. ---
                BeginParkedPhase("two cycles");
                ChunkCoord twice = new ChunkCoord(5, 5);
                Vector2Int twicePos = twice.ToVoxelOrigin();
                PipelineTelemetry.StampRequested(twice);
                PipelineTelemetry.StampAdmitted(twice);
                PipelineTelemetry.StampParked(twicePos);
                double cycle1 = SpinMs(10);
                PipelineTelemetry.StampUnparked(twicePos);
                double eligibleGap = SpinMs(10); // eligible, not parked — must NOT be counted
                PipelineTelemetry.StampParked(twicePos);
                double cycle2 = SpinMs(10);
                PipelineTelemetry.StampUnparked(twicePos);
                PipelineTelemetry.StampMeshApplied(twice);
                PipelineTelemetry.EndPhase();

                double twiceMs = SingleParkedMs();
                double twiceExpected = cycle1 + cycle2;
                ok &= Check($"two park cycles SUM (got {twiceMs:F1} ms, cycles total {twiceExpected:F1} ms)",
                    twiceMs >= twiceExpected - PARK_TOLERANCE_MS);
                ok &= Check($"the eligible gap of {eligibleGap:F1} ms between them is NOT counted " +
                            $"(got {twiceMs:F1} ms, would be ~{twiceExpected + eligibleGap:F1} ms if it were)",
                    twiceMs <= twiceExpected + PARK_TOLERANCE_MS);

                // --- Flush-and-restart: the coord stays in the WAITING set across a re-request, so the
                //     replacement trace must open its own interval or the chunk reports zero forever. ---
                BeginParkedPhase("re-requested while parked");
                ChunkCoord rerequested = new ChunkCoord(6, 6);
                Vector2Int rerequestedPos = rerequested.ToVoxelOrigin();
                PipelineTelemetry.StampRequested(rerequested);
                PipelineTelemetry.StampAdmitted(rerequested);
                PipelineTelemetry.StampParked(rerequestedPos);
                double beforeRestart = SpinMs(10); // waited under the FIRST journey
                PipelineTelemetry.StampRequested(rerequested); // flush-and-restart, still parked
                PipelineTelemetry.StampAdmitted(rerequested);
                double afterRestart = SpinMs(10); // waited under the SECOND journey
                PipelineTelemetry.StampUnparked(rerequestedPos);
                PipelineTelemetry.StampMeshApplied(rerequested);
                PipelineTelemetry.EndPhase();

                double rerequestedMs = SingleParkedMs();
                double rerequestExpected = beforeRestart + afterRestart;
                ok &= Check($"a chunk re-requested while parked still records its wait (got {rerequestedMs:F1} ms, " +
                            "not 0)", rerequestedMs > 0);
                ok &= Check($"...and the WHOLE wait, spanning the re-request (got {rerequestedMs:F1} ms, both " +
                            $"spins total {rerequestExpected:F1} ms — the coord stayed ineligible throughout, " +
                            "so a per-trace timestamp would have lost the first half)",
                    rerequestedMs >= rerequestExpected - PARK_TOLERANCE_MS &&
                    rerequestedMs <= rerequestExpected + PARK_TOLERANCE_MS);

                // --- Across a PHASE boundary: BeginPhase drops every trace, but the scheduler's waiting set
                //     survives, so a chunk parked at a speed-tier boundary must not report zero. This is the
                //     §10 q4 population — an un-populated placeholder parked pending neighbour terrain. ---
                BeginParkedPhase("phase N");
                ChunkCoord straddler = new ChunkCoord(8, 8);
                Vector2Int straddlerPos = straddler.ToVoxelOrigin();
                PipelineTelemetry.StampRequested(straddler);
                PipelineTelemetry.StampAdmitted(straddler);
                PipelineTelemetry.StampParked(straddlerPos);
                double beforeBoundary = SpinMs(10);
                PipelineTelemetry.EndPhase();

                // Next speed tier: fresh phase, fresh trace, same still-parked coord.
                PipelineTelemetry.BeginPhase("phase N+1", "P9-0", 4096);
                PipelineTelemetry.StampRequested(straddler);
                PipelineTelemetry.StampAdmitted(straddler);
                double afterBoundary = SpinMs(10);
                PipelineTelemetry.StampUnparked(straddlerPos);
                PipelineTelemetry.StampMeshApplied(straddler);
                PipelineTelemetry.EndPhase();

                // CompletedPhases[0] is phase N (no delivery); the sample belongs to phase N+1.
                PipelinePhaseMetrics tierTwo = PipelineTelemetry.CompletedPhases[1];
                double straddleMs = tierTwo.ParkedTicksSamples.Count == 1
                    ? PipelineTelemetry.TicksToMs(tierTwo.ParkedTicksSamples[0])
                    : -1;
                double straddleExpected = beforeBoundary + afterBoundary;
                ok &= Check($"a wait spanning a phase boundary is recorded, not zeroed (got {straddleMs:F1} ms)",
                    straddleMs > 0);
                ok &= Check($"...in full, and credited to the phase it ENDS in (got {straddleMs:F1} ms, both " +
                            $"spins total {straddleExpected:F1} ms)",
                    straddleMs >= straddleExpected - PARK_TOLERANCE_MS &&
                    straddleMs <= straddleExpected + PARK_TOLERANCE_MS);

                // --- LightWorkScheduler.Clear() closes open intervals, driven through the REAL production
                //     hooks rather than the telemetry API (Clear is reachable mid-capture via
                //     World.ForceUnloadAllChunks, which the benchmark's transition phase calls). ---
                BeginParkedPhase("scheduler clear");
                ChunkCoord cleared = new ChunkCoord(7, 7);
                Vector2Int clearedPos = cleared.ToVoxelOrigin();
                LightWorkScheduler scheduler = new LightWorkScheduler();
                PipelineTelemetry.StampRequested(cleared);
                PipelineTelemetry.StampAdmitted(cleared);
                scheduler.MarkWaiting(clearedPos);
                double clearSpin = SpinMs(10);
                scheduler.Clear();
                PipelineTelemetry.StampMeshApplied(cleared);
                PipelineTelemetry.EndPhase();

                double clearedMs = SingleParkedMs();
                ok &= Check($"MarkWaiting opens an interval and Clear() closes it (got {clearedMs:F1} ms, " +
                            $"spun {clearSpin:F1} ms)",
                    clearedMs >= clearSpin - PARK_TOLERANCE_MS && clearedMs <= clearSpin + PARK_TOLERANCE_MS);
                ok &= Check("...leaving no open interval behind", PipelineTelemetry.OpenParkIntervals == 0);

                // --- Lifetime: the side table survives phases ON PURPOSE, so it is the one structure here
                //     that a leak would grow silently across a whole run. A park with no matching unpark must
                //     stay open across a phase boundary, and must NOT survive the run boundary. ---
                BeginParkedPhase("leak check");
                ChunkCoord leaked = new ChunkCoord(9, 9);
                PipelineTelemetry.StampRequested(leaked);
                PipelineTelemetry.StampAdmitted(leaked);
                PipelineTelemetry.StampParked(leaked.ToVoxelOrigin());
                PipelineTelemetry.EndPhase();

                ok &= Check("an unmatched park stays open across a phase boundary (that is the fix)",
                    PipelineTelemetry.OpenParkIntervals == 1);

                PipelineTelemetry.BeginRun();
                ok &= Check("...but never survives a run boundary", PipelineTelemetry.OpenParkIntervals == 0);

                return ok;
            }
            finally
            {
                PipelineTelemetry.BeginRun();
                PipelineTelemetry.Enabled = wasEnabled;
            }
        }

        /// <summary>
        /// P9-0: per-pass main-thread attribution — that the sub-phase regions stay disjoint, that the
        /// pre-split totals are still recoverable, and that an unmeasured phase says so.
        /// <para>
        /// The <b>NOT MEASURED</b> assertion guards the instrument's worst failure mode, which is a false
        /// green rather than a red: if the profiler never runs, every pass totals 0.0 ms, and a table of
        /// zeros reads as "the pipeline's scheduling is free" — a finding-shaped conclusion pointing the
        /// reader away from the cost centre the phase exists to locate.
        /// </para>
        /// </summary>
        private static bool RunB22PassCostAttribution()
        {
            bool wasEnabled = PipelineTelemetry.Enabled;
            bool profilerWasEnabled = WorldFrameProfiler.Enabled;
            try
            {
                // --- Disjointness: time charged to one phase appears in no other. ---
                WorldFrameProfiler.Enabled = true;
                WorldFrameProfiler.BeginFrame();
                long mergeStart = WorldFrameProfiler.Begin();
                SpinMs(10);
                WorldFrameProfiler.Add(WorldFrameProfiler.Phase.LightMerge, mergeStart);
                WorldFrameProfiler.EndFrame();

                bool ok = Check("time charged to LightMerge lands in LightMerge",
                    WorldFrameProfiler.LastFrameLightMergeMs >= 8.0);
                ok &= Check("...and in NO other lighting slot",
                    WorldFrameProfiler.LastFrameLightScheduleMs == 0.0 &&
                    WorldFrameProfiler.LastFrameLightStagingDrainMs == 0.0 &&
                    WorldFrameProfiler.LastFrameLightFailSafeScanMs == 0.0);
                ok &= Check("...and in no mesh or generation slot",
                    WorldFrameProfiler.LastFrameMeshProcessMs == 0.0 &&
                    WorldFrameProfiler.LastFrameMeshScheduleMs == 0.0 &&
                    WorldFrameProfiler.LastFrameGenerationProcessMs == 0.0);

                // The pre-split aggregate must still be recoverable, or fluid-stress captures taken across
                // the split are silently incomparable.
                WorldFrameProfiler.BeginFrame();
                long scanStart = WorldFrameProfiler.Begin();
                SpinMs(5);
                WorldFrameProfiler.Add(WorldFrameProfiler.Phase.LightSchedule, scanStart);
                long processStart = WorldFrameProfiler.Begin();
                SpinMs(5);
                WorldFrameProfiler.Add(WorldFrameProfiler.Phase.MeshProcess, processStart);
                WorldFrameProfiler.EndFrame();

                ok &= Check("LastFrameLightMs == merge + stagingDrain + failsafe + schedule",
                    Math.Abs(WorldFrameProfiler.LastFrameLightMs -
                             (WorldFrameProfiler.LastFrameLightMergeMs +
                              WorldFrameProfiler.LastFrameLightStagingDrainMs +
                              WorldFrameProfiler.LastFrameLightFailSafeScanMs +
                              WorldFrameProfiler.LastFrameLightScheduleMs)) < 1e-9);
                ok &= Check("LastFrameMeshMs == process + schedule",
                    Math.Abs(WorldFrameProfiler.LastFrameMeshMs -
                             (WorldFrameProfiler.LastFrameMeshProcessMs +
                              WorldFrameProfiler.LastFrameMeshScheduleMs)) < 1e-9);

                // Disabled: no timestamp is even read, and the published values FREEZE rather than zero —
                // BeginFrame and EndFrame are both no-ops, so the last enabled frame's numbers persist.
                // Asserted rather than assumed: a reader could reasonably expect either behaviour, and the
                // fold in RecordFrame is only safe because it gates on Enabled rather than on these values.
                double frozenSchedule = WorldFrameProfiler.LastFrameLightScheduleMs;
                double frozenProcess = WorldFrameProfiler.LastFrameMeshProcessMs;

                WorldFrameProfiler.Enabled = false;
                WorldFrameProfiler.BeginFrame();
                long disabledStart = WorldFrameProfiler.Begin();
                SpinMs(5);
                WorldFrameProfiler.Add(WorldFrameProfiler.Phase.LightMerge, disabledStart);
                WorldFrameProfiler.EndFrame();

                ok &= Check("disabled: Begin returns 0 (no Stopwatch read at all)", disabledStart == 0L);
                ok &= Check("disabled: published values FREEZE at the last enabled frame, they do not zero",
                    ExactValue.Equal(WorldFrameProfiler.LastFrameLightScheduleMs, frozenSchedule) &&
                    ExactValue.Equal(WorldFrameProfiler.LastFrameMeshProcessMs, frozenProcess) &&
                    frozenSchedule > 0 && frozenProcess > 0);

                // --- The false-green guard: an unmeasured phase must NOT render as 0.0 ms. ---
                PipelineTelemetry.BeginRun();
                PipelineTelemetry.Enabled = true;
                WorldFrameProfiler.Enabled = false;
                PipelineTelemetry.BeginPhase("unprofiled", "P9-0", 4096);
                PipelineTelemetry.RecordFrame(0, 0, 0, 0, 0, true);
                PipelineTelemetry.EndPhase();

                PipelinePhaseMetrics unprofiled = PipelineTelemetry.CompletedPhases[0];
                ok &= Check("a phase captured without the profiler records 0 profiled frames",
                    unprofiled.ProfiledFrameCount == 0);

                StringBuilder sb = new StringBuilder();
                PipelineReportSection.Append(sb, PipelineTelemetry.CompletedPhases);
                string unprofiledText = sb.ToString();
                ok &= Check("...and the report says NOT MEASURED rather than printing a table of zeros",
                    unprofiledText.Contains("NOT MEASURED"));

                // --- And a genuinely profiled phase must accumulate, and must NOT print the banner. ---
                PipelineTelemetry.BeginRun();
                PipelineTelemetry.Enabled = true;
                WorldFrameProfiler.Enabled = true;
                PipelineTelemetry.BeginPhase("profiled", "P9-0", 4096);

                WorldFrameProfiler.BeginFrame();
                long realStart = WorldFrameProfiler.Begin();
                SpinMs(10);
                WorldFrameProfiler.Add(WorldFrameProfiler.Phase.LightMerge, realStart);
                WorldFrameProfiler.EndFrame();
                PipelineTelemetry.RecordFrame(0, 0, 0, 0, 0, true);
                PipelineTelemetry.EndPhase();

                PipelinePhaseMetrics profiled = PipelineTelemetry.CompletedPhases[0];
                ok &= Check("a profiled phase counts its frame", profiled.ProfiledFrameCount == 1);
                ok &= Check("...and accumulates that frame's LightMerge cost",
                    profiled.PassMsTotals[(int)WorldFrameProfiler.Phase.LightMerge] >= 8.0);

                sb = new StringBuilder();
                PipelineReportSection.Append(sb, PipelineTelemetry.CompletedPhases);
                ok &= Check("...and the report renders the cost table, not the NOT MEASURED banner",
                    !sb.ToString().Contains("NOT MEASURED"));

                // Quota utilisation is recorded per pass and never conflated between passes.
                PipelineTelemetry.BeginRun();
                PipelineTelemetry.Enabled = true;
                PipelineTelemetry.BeginPhase("quota", "P9-0", 4096);
                PipelineTelemetry.RecordPassWork(PipelinePass.LightSchedule, 7, 24);
                PipelineTelemetry.RecordPassWork(PipelinePass.MeshSchedule, 3, 11);
                PipelineTelemetry.EndPhase();

                PipelinePhaseMetrics quota = PipelineTelemetry.CompletedPhases[0];
                ok &= Check("light schedule served 7 of 24 granted",
                    quota.PassItemsServed[(int)PipelinePass.LightSchedule] == 7 &&
                    quota.PassQuotaGranted[(int)PipelinePass.LightSchedule] == 24);
                ok &= Check("mesh schedule served 3 of 11 granted, in its own slot",
                    quota.PassItemsServed[(int)PipelinePass.MeshSchedule] == 3 &&
                    quota.PassQuotaGranted[(int)PipelinePass.MeshSchedule] == 11);
                ok &= Check("a pass that never reported has no work frames",
                    quota.PassWorkFrames[(int)PipelinePass.MeshProcess] == 0);

                // The utilisation DENOMINATOR rule (P9-0 review finding #2). Both scheduling passes must
                // count the same kind of frame, or the two columns are not comparable — and comparing them
                // is exactly what P9-1 does to judge whether the quota binds. An idle frame contributes a
                // full quota and zero served, so including it on one pass and not the other silently makes
                // that pass look starved. Pinned as an arithmetic identity over recorded counters.
                //
                // SCOPE, stated so it is not over-read: this pins the ACCUMULATION, not the call-site policy.
                // Which frames call RecordPassWork is decided in World.Update — a play-mode path unreachable
                // from edit mode — so re-adding the lighting call on idle frames would leave this scenario
                // green. Same limitation B13 documents for StampUnloaded; the call sites stay review-guarded.
                PipelineTelemetry.BeginRun();
                PipelineTelemetry.Enabled = true;
                PipelineTelemetry.BeginPhase("denominator", "P9-0", 4096);

                // Two frames with work: served 5 of 24, then 24 of 24. One "idle" frame contributes nothing.
                PipelineTelemetry.RecordPassWork(PipelinePass.LightSchedule, 5, 24);
                PipelineTelemetry.RecordPassWork(PipelinePass.LightSchedule, 24, 24);
                // Mesh: one drained frame plus one in-flight-cap frame, which serves 0 against a real quota.
                PipelineTelemetry.RecordPassWork(PipelinePass.MeshSchedule, 8, 11);
                PipelineTelemetry.RecordPassWork(PipelinePass.MeshSchedule, 0, 11);
                PipelineTelemetry.EndPhase();

                PipelinePhaseMetrics denom = PipelineTelemetry.CompletedPhases[0];
                ok &= Check("work frames count only reported frames, per pass (2 light, 2 mesh)",
                    denom.PassWorkFrames[(int)PipelinePass.LightSchedule] == 2 &&
                    denom.PassWorkFrames[(int)PipelinePass.MeshSchedule] == 2);
                ok &= Check("light utilisation = 29/48, i.e. granted accrues per REPORTED frame only",
                    denom.PassItemsServed[(int)PipelinePass.LightSchedule] == 29 &&
                    denom.PassQuotaGranted[(int)PipelinePass.LightSchedule] == 48);
                ok &= Check("a 0-served frame still contributes its granted quota (mesh 8/22, not 8/11)",
                    denom.PassItemsServed[(int)PipelinePass.MeshSchedule] == 8 &&
                    denom.PassQuotaGranted[(int)PipelinePass.MeshSchedule] == 22);

                return ok;
            }
            finally
            {
                PipelineTelemetry.BeginRun();
                PipelineTelemetry.Enabled = wasEnabled;
                WorldFrameProfiler.Enabled = profilerWasEnabled;
            }
        }
    }
}
