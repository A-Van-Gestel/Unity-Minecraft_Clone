using System.Collections.Generic;
using Editor.Validation.Framework;
using Helpers;
using UnityEditor;
using UnityEngine;

namespace Editor.Validation.ChunkUnload
{
    /// <summary>
    /// Entry point and runner for the <see cref="ChunkUnloadDecision"/> validation suite (CP-5). The
    /// decision is a pure map from a candidate chunk's facts (distance, job presence, lighting flags,
    /// neighbor strand scan) to the unload arm — a small managed function with no Burst, jobs, or world
    /// state — so it is truth-table-tested in isolation here, pinning the exact arm each fact combination
    /// takes and the historical precedence (job → light → strand → unload).
    /// <para>All scenarios are <b>baselines</b> (must stay green); a failure is a regression in the unload
    /// policy — including the §9.6 stranding guard, whose deadlock history makes this suite its now-testable
    /// witness.</para>
    /// <para><b>Prove-red:</b> each scenario names, in its docstring, the one-line mutation that turns it
    /// red. Inverting the strand term in <see cref="ChunkUnloadDecision.Evaluate"/> reds exactly the
    /// stranding baselines (B4/B5) — the §9.6 cases — and no others.</para>
    /// <para>Scenario bodies live in <c>ChunkUnloadDecisionValidationSuite.Baseline.cs</c>.</para>
    /// </summary>
    public static partial class ChunkUnloadDecisionValidationSuite
    {
        /// <summary>Runs every scenario and prints a categorized summary via the shared runner.</summary>
        [MenuItem("Minecraft Clone/Dev/Validate Chunk Unload Decision")]
        public static void RunAll() => Execute();

        /// <summary>
        /// Builds and runs the decision scenarios, returning the categorized result (the headless/CI entry
        /// point). Uses <see cref="KnownBugChannel.Unimplemented"/> for parity with the other pure-logic
        /// suites; the channel is currently unused (baselines only).
        /// </summary>
        /// <param name="logToConsole">When false, runs silently and only returns the result (for headless/CI use).</param>
        /// <param name="showProgress">When false, suppresses this suite's own progress bar (the aggregate runner drives one).</param>
        /// <returns>The categorized, timed result of the run.</returns>
        public static ValidationRunResult Execute(bool logToConsole = true, bool showProgress = true)
        {
            List<Scenario> scenarios = new List<Scenario>();
            AddBaselineScenarios(scenarios);
            return ValidationSuiteRunner.Execute("Chunk Unload Decision", scenarios, KnownBugChannel.Unimplemented, logToConsole, showProgress);
        }

        /// <summary>Registers the baseline regression scenarios (implemented in ChunkUnloadDecisionValidationSuite.Baseline.cs).</summary>
        static partial void AddBaselineScenarios(List<Scenario> scenarios);

        // --- Shared fixture & assertion helpers -------------------------------------------------

        /// <summary>Evaluates the decision for the given facts (all bools, in declaration order).</summary>
        /// <param name="beyondUnloadDistance">Chunk lies beyond the unload boundary.</param>
        /// <param name="jobRunning">A job is currently keyed on this chunk.</param>
        /// <param name="processingLight">Pending main-thread lighting work on this chunk.</param>
        /// <param name="wouldStrandInRangeNeighbor">A populated, in-range neighbor still needs this chunk's data.</param>
        /// <returns>The evaluated unload arm.</returns>
        private static ChunkUnloadDecision.Result Eval(
            bool beyondUnloadDistance, bool jobRunning, bool processingLight, bool wouldStrandInRangeNeighbor) =>
            ChunkUnloadDecision.Evaluate(new ChunkUnloadDecision.ChunkUnloadFacts(
                beyondUnloadDistance, jobRunning, processingLight, wouldStrandInRangeNeighbor));

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

        /// <summary>Asserts the decision for a fact tuple equals the expected arm, logging the mismatch.</summary>
        /// <param name="label">Assertion description.</param>
        /// <param name="expected">Expected arm.</param>
        /// <param name="actual">Arm returned by <see cref="Eval"/>.</param>
        /// <returns>True if the arms match.</returns>
        private static bool CheckArm(string label, ChunkUnloadDecision.Result expected, ChunkUnloadDecision.Result actual)
        {
            if (expected == actual) return Check(label, true);
            return Check($"{label} — expected {expected}, got {actual}", false);
        }
    }
}
