using System.Collections.Generic;
using Editor.Validation.Framework;
using Helpers;
using UnityEditor;
using UnityEngine;

namespace Editor.Validation.PoolPrune
{
    /// <summary>
    /// Truth-table suite for <see cref="PoolPruneDecision"/> (CP-7/F4 post-review): pins the linger
    /// window semantics — observed demand grants the hard (service-area) cap, a demand-free window
    /// drains to the soft (row) cap, and demand after expiry re-protects. The decision is a pure
    /// managed function over explicit time, so no world state or clock mocking is involved.
    /// <para>All scenarios are <b>baselines</b> (must stay green); a failure is a regression in the
    /// pool-retention policy the CP-7 measurement sessions validated in-game.</para>
    /// <para><b>Prove-red:</b> removing the <c>PrevGets</c> update in
    /// <see cref="PoolPruneDecision.Evaluate"/> (demand restamps every evaluation) reds exactly the
    /// expiry assertions in B3/B4/B5's demand-bearing rows; widening the expiry comparison to
    /// <c>&gt;=</c> reds B3's exact-boundary pin.</para>
    /// </summary>
    public static class PoolPruneDecisionValidationSuite
    {
        // Representative caps/window; the decision only compares them, exact values are arbitrary.
        private const int SOFT_CAP = 100;
        private const int HARD_CAP = 900;
        private const float LINGER = 90f;

        /// <summary>Runs every scenario and prints a categorized summary via the shared runner.</summary>
        [MenuItem("Minecraft Clone/Dev/Validate Pool Prune Decision")]
        public static void RunAll() => Execute();

        /// <summary>
        /// Builds and runs the decision scenarios, returning the categorized result (the headless/CI
        /// entry point). <see cref="KnownBugChannel.Unimplemented"/> for parity with the other
        /// pure-logic suites; the channel is currently unused (baselines only).
        /// </summary>
        /// <param name="logToConsole">When false, runs silently and only returns the result (for headless/CI use).</param>
        /// <param name="showProgress">When false, suppresses this suite's own progress bar (the aggregate runner drives one).</param>
        /// <returns>The categorized, timed result of the run.</returns>
        public static ValidationRunResult Execute(bool logToConsole = true, bool showProgress = true)
        {
            List<Scenario> scenarios = new List<Scenario>
            {
                new Scenario("B1 Window active, no demand -> hard cap", RunB1WindowActiveHard),
                new Scenario("B2 Demand restamps the window -> hard cap persists", RunB2DemandRestamps),
                new Scenario("B3 Demand-free past window -> soft cap (boundary exact)", RunB3ExpiryReclaims),
                new Scenario("B4 Post-expiry demand re-protects -> hard cap", RunB4PostExpiryDemand),
                new Scenario("B5 Fresh state expires without demand -> soft cap", RunB5FreshStateExpiry),
            };
            return ValidationSuiteRunner.Execute("Pool Prune Decision", scenarios, KnownBugChannel.Unimplemented, logToConsole, showProgress);
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

        /// <summary>First demand stamps the window; later demand-free evaluations inside it stay hard-capped.</summary>
        private static bool RunB1WindowActiveHard()
        {
            PoolPruneDecision.State state = default;
            bool ok = Check("first demand (gets 0->5) at t=10 grants hard cap",
                PoolPruneDecision.Evaluate(SOFT_CAP, HARD_CAP, 5, LINGER, 10f, ref state) == HARD_CAP);
            ok &= Check("no demand at t=50 (40s elapsed, within window) stays hard",
                PoolPruneDecision.Evaluate(SOFT_CAP, HARD_CAP, 5, LINGER, 50f, ref state) == HARD_CAP);
            return ok;
        }

        /// <summary>A demand mid-window restamps it: without the restamp, t=160 would be 160s past the ORIGINAL stamp and reclaim.</summary>
        private static bool RunB2DemandRestamps()
        {
            PoolPruneDecision.State state = default;
            bool ok = Check("initial demand at t=0 grants hard cap",
                PoolPruneDecision.Evaluate(SOFT_CAP, HARD_CAP, 1, LINGER, 0f, ref state) == HARD_CAP);
            ok &= Check("new demand at t=80 restamps (hard cap)",
                PoolPruneDecision.Evaluate(SOFT_CAP, HARD_CAP, 2, LINGER, 80f, ref state) == HARD_CAP);
            ok &= Check("t=160 is only 80s past the RESTAMPED demand — still hard",
                PoolPruneDecision.Evaluate(SOFT_CAP, HARD_CAP, 2, LINGER, 160f, ref state) == HARD_CAP);
            return ok;
        }

        /// <summary>A demand-free window reclaims to soft; the boundary is strict (exactly LINGER elapsed is still protected).</summary>
        private static bool RunB3ExpiryReclaims()
        {
            PoolPruneDecision.State state = default;
            bool ok = Check("demand at t=0 grants hard cap",
                PoolPruneDecision.Evaluate(SOFT_CAP, HARD_CAP, 1, LINGER, 0f, ref state) == HARD_CAP);
            ok &= Check("exactly LINGER elapsed (t=90) is still within the window (strict >)",
                PoolPruneDecision.Evaluate(SOFT_CAP, HARD_CAP, 1, LINGER, 90f, ref state) == HARD_CAP);
            ok &= Check("past the window (t=91) reclaims to soft cap",
                PoolPruneDecision.Evaluate(SOFT_CAP, HARD_CAP, 1, LINGER, 91f, ref state) == SOFT_CAP);
            return ok;
        }

        /// <summary>Demand arriving after expiry re-protects immediately (the post-expiry teleport case measured in-game).</summary>
        private static bool RunB4PostExpiryDemand()
        {
            PoolPruneDecision.State state = default;
            bool ok = Check("demand at t=0 grants hard cap",
                PoolPruneDecision.Evaluate(SOFT_CAP, HARD_CAP, 1, LINGER, 0f, ref state) == HARD_CAP);
            ok &= Check("expired at t=100 (soft cap)",
                PoolPruneDecision.Evaluate(SOFT_CAP, HARD_CAP, 1, LINGER, 100f, ref state) == SOFT_CAP);
            ok &= Check("new demand at t=101 re-protects (hard cap)",
                PoolPruneDecision.Evaluate(SOFT_CAP, HARD_CAP, 2, LINGER, 101f, ref state) == HARD_CAP);
            ok &= Check("still protected at t=150 (49s after re-stamp)",
                PoolPruneDecision.Evaluate(SOFT_CAP, HARD_CAP, 2, LINGER, 150f, ref state) == HARD_CAP);
            return ok;
        }

        /// <summary>A fresh (default) state with no demand ever: protected until the window from t=0 lapses, then soft.</summary>
        private static bool RunB5FreshStateExpiry()
        {
            PoolPruneDecision.State state = default;
            bool ok = Check("fresh state at t=50 (within window from default stamp 0) is hard",
                PoolPruneDecision.Evaluate(SOFT_CAP, HARD_CAP, 0, LINGER, 50f, ref state) == HARD_CAP);
            ok &= Check("fresh state at t=100 with no demand ever reclaims to soft",
                PoolPruneDecision.Evaluate(SOFT_CAP, HARD_CAP, 0, LINGER, 100f, ref state) == SOFT_CAP);
            return ok;
        }
    }
}
