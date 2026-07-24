using System.Collections.Generic;
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
