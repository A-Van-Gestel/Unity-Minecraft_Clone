using System.Collections.Generic;
using Editor.Validation.Framework;
using UnityEditor;
using UnityEngine;

namespace Editor.Validation.Commands
{
    /// <summary>
    /// Entry point and runner for the <b>command console engine</b> validation suite (CMD-0) — the pure-C#
    /// <c>Commands.CommandEngine</c> pipeline (tokenizer → registry → selector resolution → dispatch),
    /// its confirmation state machine, and its output/recall history rings, driven headless exactly the
    /// way the CMD-1 UI will drive them (<c>engine.Execute(string)</c>).
    /// <para>
    /// All currently registered scenarios are <b>baselines</b> (must stay green); the runner also supports
    /// <b>known-bug</b> scenarios (a non-null <c>KnownBugId</c>, EXPECTED to fail until fixed) for any
    /// future console bug authored test-first.
    /// </para>
    /// </summary>
    public static partial class CommandConsoleValidationSuite
    {
        /// <summary>
        /// Runs every registered scenario and prints a categorized summary via the shared
        /// <see cref="ValidationSuiteRunner"/>. Baseline failures mark the suite red; known-bug
        /// reproductions are reported as warnings.
        /// </summary>
        [MenuItem("Minecraft Clone/Dev/Validate Command Console")]
        public static void RunAll() => Execute();

        /// <summary>
        /// Builds and runs the command console scenarios, returning the categorized result
        /// (the headless/CI entry point).
        /// </summary>
        /// <param name="logToConsole">When false, runs silently and only returns the result (for headless/CI use).</param>
        /// <param name="showProgress">When false, suppresses this suite's own progress bar (the aggregate runner drives one).</param>
        /// <returns>The categorized, timed result of the run.</returns>
        public static ValidationRunResult Execute(bool logToConsole = true, bool showProgress = true)
        {
            List<Scenario> scenarios = new List<Scenario>();
            AddBaselineScenarios(scenarios);
            AddTeleportScenarios(scenarios);
            AddCommandPackScenarios(scenarios);
            AddKnownBugScenarios(scenarios);
            return ValidationSuiteRunner.Execute("Command Console", scenarios, KnownBugChannel.Bug, logToConsole, showProgress);
        }

        /// <summary>
        /// Logs and evaluates a single assertion. Returns <paramref name="condition"/> so a scenario can AND its
        /// assertions together: <c>bool ok = Expect(...); ok &amp;= Expect(...); return ok;</c>.
        /// </summary>
        /// <param name="condition">The asserted condition.</param>
        /// <param name="message">Description of what was expected (logged on failure).</param>
        /// <returns><paramref name="condition"/>, unchanged.</returns>
        private static bool Expect(bool condition, string message)
        {
            if (!condition)
                Debug.LogError($"  [ASSERT FAILED] {message}");
            return condition;
        }

        /// <summary>Registers the baseline regression scenarios (implemented in CommandConsoleValidationSuite.Baseline.cs).</summary>
        static partial void AddBaselineScenarios(List<Scenario> scenarios);

        /// <summary>Registers the CMD-2 /teleport matrix (implemented in CommandConsoleValidationSuite.Teleport.cs).</summary>
        static partial void AddTeleportScenarios(List<Scenario> scenarios);

        /// <summary>Registers the CMD-3 command-pack baselines (implemented in CommandConsoleValidationSuite.Pack.cs).</summary>
        static partial void AddCommandPackScenarios(List<Scenario> scenarios);

        /// <summary>Registers expected-red known-bug reproductions (none yet; the seam for future console bugs authored test-first).</summary>
        static partial void AddKnownBugScenarios(List<Scenario> scenarios);
    }
}
