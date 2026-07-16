using System.Collections.Generic;
using Editor.Validation.Framework;
using UnityEditor;
using UnityEngine;

namespace Editor.Validation
{
    /// <summary>
    /// Validation suite for startup spawn <b>policy</b> — the "Spawn" suite. Drives the real
    /// <see cref="global::Spawn.SpawnResolution"/> that <c>World.StartWorld</c> uses, covering the source
    /// classification, both startup phases, and the canonical-spawn-point decision. Every scenario is a baseline
    /// (must stay green). Scenario implementations live in the partial file (<c>.Baseline.cs</c>).
    /// </summary>
    /// <remarks>
    /// Deliberately kept in <c>namespace Editor.Validation</c> (not <c>Editor.Validation.Spawn</c>), matching the
    /// Chunk Math suite: a <c>.Spawn</c> child namespace would shadow the production <c>Spawn</c> namespace this
    /// suite exists to test, and C# would resolve the nearer one — the collision class this convention avoids.
    /// <para>
    /// The unit under test is pure and takes its terrain probe as a delegate, so these scenarios need no
    /// <c>World</c>, no scene, and no fixture: they assert on returned values <i>and</i> on which positions the
    /// probe was aimed at, which is where the three sources actually differ.
    /// </para>
    /// </remarks>
    public static partial class SpawnValidationSuite
    {
        /// <summary>
        /// Runs every registered scenario and prints a categorized summary via the shared
        /// <see cref="ValidationSuiteRunner"/>. Baseline failures mark the suite red.
        /// </summary>
        [MenuItem("Minecraft Clone/Dev/Validate Spawn")]
        public static void RunAll() => Execute();

        /// <summary>
        /// Builds and runs the spawn scenarios, returning the categorized result (the headless/CI entry point).
        /// </summary>
        /// <param name="logToConsole">When false, runs silently and only returns the result (for headless/CI use).</param>
        /// <param name="showProgress">When false, suppresses this suite's own progress bar (the aggregate runner drives one).</param>
        /// <returns>The categorized, timed result of the run.</returns>
        public static ValidationRunResult Execute(bool logToConsole = true, bool showProgress = true)
        {
            List<Scenario> scenarios = new List<Scenario>();
            AddBaselineScenarios(scenarios);
            return ValidationSuiteRunner.Execute("Spawn", scenarios, KnownBugChannel.Bug, logToConsole, showProgress);
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

        /// <summary>Registers the spawn-policy baselines (implemented in SpawnValidationSuite.Baseline.cs).</summary>
        static partial void AddBaselineScenarios(List<Scenario> scenarios);
    }
}
