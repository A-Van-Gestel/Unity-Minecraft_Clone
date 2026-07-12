using System.Collections.Generic;
using Editor.Validation.Framework;
using UnityEditor;
using UnityEngine;

namespace Editor.Validation.Placement
{
    /// <summary>
    /// Entry point and runner for the player block-<b>placement</b> validation suite — the placement decision now
    /// owned by <c>Placement.PlacementController</c> (which composes the pure <c>Placement.PlacementResolver</c> tag
    /// logic with the real <c>World.CheckForVoxel</c> / <c>World.IsCellOccupiedForPlacement</c> seams), driven by the
    /// same controller <c>PlayerInteraction</c> uses in-game.
    /// <para>
    /// Scenarios come in categories, mirroring the lighting/meshing suites. All currently registered scenarios are
    /// <b>baselines</b> (must stay green); the runner also supports <b>known-bug</b> scenarios (a non-null
    /// <c>KnownBugId</c>, EXPECTED to fail until fixed) for any future placement bug authored test-first.
    /// <list type="bullet">
    /// <item><b>Baseline</b> scenarios run against a controlled, correctly-configured palette and pin the placement
    /// mechanism (<c>.Baseline.cs</c>).</item>
    /// <item><b>Data-audit</b> inspects the <b>real</b> shipping <c>BlockDatabase.asset</c> — asserts no block's
    /// <c>placementCanReplaceTags</c> contains a tag that would tunnel the placement ray (<c>.DataAudit.cs</c>).</item>
    /// <item><b>Regression</b> guards reproduce the formerly-broken PLAYER_BUGS §03 cases against the real database
    /// (Coal Ore / Directional Block / Oak Log land-on-top), promoted to baselines after the fix (<c>.Regression.cs</c>).</item>
    /// </list>
    /// </para>
    /// </summary>
    public static partial class PlacementValidationSuite
    {
        /// <summary>
        /// Runs every registered scenario and prints a categorized summary via the shared
        /// <see cref="ValidationSuiteRunner"/>. Baseline failures mark the suite red; known-bug
        /// reproductions are reported as warnings.
        /// </summary>
        [MenuItem("Minecraft Clone/Dev/Validate Placement")]
        public static void RunAll() => Execute();

        /// <summary>
        /// Builds and runs the placement scenarios (baselines, the real-database data audit, and the PLAYER_BUGS §03
        /// regression guards), returning the categorized result (the headless/CI entry point).
        /// </summary>
        /// <param name="logToConsole">When false, runs silently and only returns the result (for headless/CI use).</param>
        /// <param name="showProgress">When false, suppresses this suite's own progress bar (the aggregate runner drives one).</param>
        /// <returns>The categorized, timed result of the run.</returns>
        public static ValidationRunResult Execute(bool logToConsole = true, bool showProgress = true)
        {
            List<Scenario> scenarios = new List<Scenario>();
            AddBaselineScenarios(scenarios);
            AddDataAuditScenarios(scenarios);
            AddRegressionScenarios(scenarios);
            AddKnownBugScenarios(scenarios);
            return ValidationSuiteRunner.Execute("Placement", scenarios, KnownBugChannel.Bug, logToConsole, showProgress);
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

        /// <summary>Registers the baseline regression scenarios (implemented in PlacementValidationSuite.Baseline.cs).</summary>
        static partial void AddBaselineScenarios(List<Scenario> scenarios);

        /// <summary>Registers the real-database tag-audit scenarios (implemented in PlacementValidationSuite.DataAudit.cs).</summary>
        static partial void AddDataAuditScenarios(List<Scenario> scenarios);

        /// <summary>Registers the PLAYER_BUGS §03 regression guards (implemented in PlacementValidationSuite.Regression.cs).</summary>
        static partial void AddRegressionScenarios(List<Scenario> scenarios);

        /// <summary>Registers expected-red known-bug reproductions (implemented in PlacementValidationSuite.KnownBugs.cs).</summary>
        static partial void AddKnownBugScenarios(List<Scenario> scenarios);
    }
}
