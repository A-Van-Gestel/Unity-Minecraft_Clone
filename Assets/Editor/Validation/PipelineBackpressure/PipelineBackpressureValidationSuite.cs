using System;
using System.Collections.Generic;
using Benchmarks;
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
    /// inside-band <c>RemainClosed</c> pin.</para>
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
                new Scenario("B10 Regime verdict rule: each arm, plurality, and the ordering axis (FP-3 §7.1)", RunB10VerdictRule),
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
        /// FP-3 §7.1: the verdict rule, pinned so it cannot silently change meaning between captures — two
        /// reports produced by different rules are not comparable, and nothing in a report would reveal that.
        /// Covers each regime arm, the plurality selection across passes, and the ordering axis (which is
        /// deliberately independent, so it can fire on top of a Healthy primary — the shape the reported
        /// flight symptom is most likely to take).
        /// </summary>
        private static bool RunB10VerdictRule()
        {
            const int passes = PipelineTelemetry.PassCount;
            const int reasons = PipelineTelemetry.StopReasonCount;

            // Helper: a tally matrix with a single dominant reason.
            int[,] Only(PassStopReason r, int count)
            {
                int[,] t = new int[passes, reasons];
                t[(int)PipelinePass.MeshSchedule, (int)r] = count;
                return t;
            }

            bool ok = Check("Quota dominant -> AdmissionBound",
                PipelineRegimeVerdict.Evaluate(Only(PassStopReason.Quota, 10), 0, 100).Primary
                == PipelineRegime.AdmissionBound);
            ok &= Check("Ceiling dominant -> AdmissionBound",
                PipelineRegimeVerdict.Evaluate(Only(PassStopReason.Ceiling, 10), 0, 100).Primary
                == PipelineRegime.AdmissionBound);
            ok &= Check("InFlightCap dominant -> ThroughputBound",
                PipelineRegimeVerdict.Evaluate(Only(PassStopReason.InFlightCap, 10), 0, 100).Primary
                == PipelineRegime.ThroughputBound);
            ok &= Check("AllDeclined dominant -> ReadinessBound",
                PipelineRegimeVerdict.Evaluate(Only(PassStopReason.AllDeclined, 10), 0, 100).Primary
                == PipelineRegime.ReadinessBound);
            ok &= Check("OutOfWork dominant -> Healthy",
                PipelineRegimeVerdict.Evaluate(Only(PassStopReason.OutOfWork, 10), 0, 100).Primary
                == PipelineRegime.Healthy);
            ok &= Check("no tallies at all -> NoData",
                PipelineRegimeVerdict.Evaluate(new int[passes, reasons], 0, 0).Primary
                == PipelineRegime.NoData);

            // NotRun must never win: it is the did-not-execute sentinel, and an idle frame is not a regime.
            int[,] notRunHeavy = new int[passes, reasons];
            notRunHeavy[(int)PipelinePass.MeshSchedule, (int)PassStopReason.NotRun] = 9999;
            notRunHeavy[(int)PipelinePass.MeshSchedule, (int)PassStopReason.Quota] = 3;
            ok &= Check("NotRun is excluded from the plurality (Quota still wins)",
                PipelineRegimeVerdict.Evaluate(notRunHeavy, 0, 100).Primary == PipelineRegime.AdmissionBound);

            // Reasons are summed ACROSS passes: the question is what bound the pipeline, not one stage.
            int[,] split = new int[passes, reasons];
            split[(int)PipelinePass.MeshSchedule, (int)PassStopReason.AllDeclined] = 6;
            split[(int)PipelinePass.LightSchedule, (int)PassStopReason.AllDeclined] = 6;
            split[(int)PipelinePass.GenerationProcess, (int)PassStopReason.Ceiling] = 10;
            ok &= Check("reasons sum across passes (12 AllDeclined beats 10 Ceiling)",
                PipelineRegimeVerdict.Evaluate(split, 0, 100).Primary == PipelineRegime.ReadinessBound);

            // The ordering axis is independent of the primary regime.
            RegimeVerdict wasteful = PipelineRegimeVerdict.Evaluate(Only(PassStopReason.OutOfWork, 10), 30, 100);
            ok &= Check("waste 30% >= threshold -> ordering-bound flagged",
                wasteful.OrderingBound);
            ok &= Check("...and it composes with a Healthy primary (the flight symptom's likely shape)",
                wasteful.Primary == PipelineRegime.Healthy);
            ok &= Check("waste just under the threshold -> NOT ordering-bound",
                !PipelineRegimeVerdict.Evaluate(Only(PassStopReason.OutOfWork, 10), 19, 100).OrderingBound);
            ok &= Check("waste fraction is reported for the raw block",
                Math.Abs(wasteful.WasteFraction - 0.30) < 1e-9);

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
                PipelinePassBudget.SanitizeBudgetMs(0f) == 0f);
            ok &= Check("negative budget passes through (no ceiling)",
                PipelinePassBudget.SanitizeBudgetMs(-3f) == -3f);
            ok &= Check("exactly MinBudgetMs passes through untouched",
                PipelinePassBudget.SanitizeBudgetMs(PipelinePassBudget.MinBudgetMs) == PipelinePassBudget.MinBudgetMs);
            ok &= Check("budgets above the floor pass through untouched",
                PipelinePassBudget.SanitizeBudgetMs(8f) == 8f);
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
                PipelinePassBudget.ScaleCeilingMs(6f, 0f) == 6f);
            ok &= Check("negative interval returns the ceiling unchanged",
                PipelinePassBudget.ScaleCeilingMs(6f, -1f) == 6f);

            // A disabled ceiling (<= 0) is never resurrected into a positive budget, at any cap.
            ok &= Check("disabled ceiling (0 ms) stays 0 even under a 15-cap",
                PipelinePassBudget.ScaleCeilingMs(0f, 1f / 15f) == 0f);
            ok &= Check("disabled ceiling (negative ms) passes through under a cap",
                PipelinePassBudget.ScaleCeilingMs(-3f, 1f / 30f) == -3f);

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
    }
}
