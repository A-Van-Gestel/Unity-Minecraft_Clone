using System.Collections.Generic;
using Editor.Validation.Framework;
using UnityEditor;

namespace Editor.Validation.Behavior
{
    /// <summary>
    /// Entry point and runner for the block-behavior tick validation suite — the regression + parity guard
    /// for the <c>BlockBehavior</c> tick path (<c>Behave</c>/<c>Active</c>) that the <b>TG-4</b> (per-behavior
    /// native collections) and <b>TG-5</b> (Burst function-pointer dispatch) optimizations re-architect.
    /// <para>
    /// Scenario categories mirror the lighting/meshing suites: <b>baseline</b> (regression) scenarios must
    /// always pass; <b>known-bug</b> scenarios reproduce documented bugs test-first and are expected to fail
    /// until fixed. Behavior parity rests on three legs (see
    /// <c>Documentation/Architecture/Testing Framework/BEHAVIOR_VALIDATION_HARNESS_FIDELITY.md</c>): golden-master snapshots,
    /// behavioral invariants (determinism, non-vacuity), and — once both code paths exist — the BH-D1
    /// old-vs-new differential.
    /// </para>
    /// </summary>
    public static partial class BehaviorValidationSuite
    {
        /// <summary>
        /// Runs every registered scenario and prints a categorized summary via the shared
        /// <see cref="ValidationSuiteRunner"/>. Baseline failures mark the suite red; known-bug
        /// reproductions are reported as warnings.
        /// </summary>
        [MenuItem("Minecraft Clone/Dev/Validate Behavior")]
        public static void RunAll() => Execute();

        /// <summary>
        /// Builds and runs the behavior scenarios, returning the categorized result (the headless/CI entry point).
        /// </summary>
        /// <returns>The categorized, timed result of the run.</returns>
        public static ValidationRunResult Execute()
        {
            List<Scenario> scenarios = new List<Scenario>();
            AddBaselineScenarios(scenarios);
            AddKnownBugScenarios(scenarios);
            return ValidationSuiteRunner.Execute("Behavior", scenarios);
        }

        /// <summary>Registers the baseline regression scenarios (implemented in BehaviorValidationSuite.Baseline.cs).</summary>
        static partial void AddBaselineScenarios(List<Scenario> scenarios);

        /// <summary>Registers the known-bug reproduction scenarios (none yet).</summary>
        static partial void AddKnownBugScenarios(List<Scenario> scenarios);
    }
}
