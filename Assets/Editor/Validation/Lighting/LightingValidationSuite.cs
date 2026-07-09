using System.Collections.Generic;
using Editor.Validation.Framework;
using UnityEditor;

namespace Editor.Validation.Lighting
{
    /// <summary>
    /// Entry point and runner for the lighting engine validation suite.
    /// <para>
    /// Scenarios come in two categories with different semantics:
    /// <list type="bullet">
    /// <item><b>Baseline</b> (regression) scenarios must always pass — a failure is a regression.</item>
    /// <item><b>Known-bug</b> scenarios reproduce documented bugs from <c>LIGHTING_BUGS.md</c> test-first:
    /// they are EXPECTED to fail until the bug is fixed, and do not fail the suite. When one starts
    /// passing, the fix is a candidate for in-game confirmation and bug archival.</item>
    /// </list>
    /// Scenario implementations live in the partial files
    /// (<c>LightingValidationSuite.Baseline.cs</c>, <c>LightingValidationSuite.KnownBugs.cs</c>).
    /// </para>
    /// </summary>
    public static partial class LightingValidationSuite
    {
        /// <summary>
        /// Runs every registered scenario and prints a categorized summary via the shared
        /// <see cref="ValidationSuiteRunner"/>. Baseline failures mark the suite red; known-bug
        /// reproductions are reported as warnings.
        /// </summary>
        [MenuItem("Minecraft Clone/Dev/Validate Lighting Engine")]
        public static void RunAll() => Execute();

        /// <summary>
        /// Builds and runs the lighting scenarios, returning the categorized result (the headless/CI entry point).
        /// </summary>
        /// <param name="logToConsole">When false, runs silently and only returns the result (for headless/CI use).</param>
        /// <param name="showProgress">When false, suppresses this suite's own progress bar (the aggregate runner drives one).</param>
        /// <returns>The categorized, timed result of the run.</returns>
        public static ValidationRunResult Execute(bool logToConsole = true, bool showProgress = true)
        {
            List<Scenario> scenarios = new List<Scenario>();
            AddBaselineScenarios(scenarios);
            AddKnownBugScenarios(scenarios);
            return ValidationSuiteRunner.Execute("Lighting Engine", scenarios, KnownBugChannel.Bug, logToConsole, showProgress);
        }

        /// <summary>Registers the baseline regression scenarios (implemented in LightingValidationSuite.Baseline.cs).</summary>
        /// <param name="scenarios">The scenario list to append to.</param>
        static partial void AddBaselineScenarios(List<Scenario> scenarios);

        /// <summary>Registers the known-bug reproduction scenarios (implemented in LightingValidationSuite.KnownBugs.cs).</summary>
        /// <param name="scenarios">The scenario list to append to.</param>
        static partial void AddKnownBugScenarios(List<Scenario> scenarios);
    }
}
