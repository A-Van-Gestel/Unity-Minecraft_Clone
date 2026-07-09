using System.Collections.Generic;
using Editor.Validation.Framework;
using UnityEditor;

namespace Editor.Validation.Meshing
{
    /// <summary>
    /// Entry point and runner for the meshing engine validation suite (the
    /// <see cref="Jobs.MeshGenerationJob"/> standard-cube path).
    /// <para>
    /// Scenarios come in two categories with different semantics, mirroring the lighting suite:
    /// <list type="bullet">
    /// <item><b>Baseline</b> (regression) scenarios must always pass — a failure is a regression in
    /// the mesher (or in a performance optimization that was supposed to preserve output).</item>
    /// <item><b>Known-bug</b> scenarios reproduce documented mesher bugs test-first: they are
    /// EXPECTED to fail until the bug is fixed, and do not fail the suite.</item>
    /// </list>
    /// Scenario implementations live in the partial files
    /// (<c>MeshingValidationSuite.Baseline.cs</c>, and a future <c>.KnownBugs.cs</c>).
    /// </para>
    /// </summary>
    public static partial class MeshingValidationSuite
    {
        /// <summary>
        /// Runs every registered scenario and prints a categorized summary via the shared
        /// <see cref="ValidationSuiteRunner"/>. Baseline failures mark the suite red; known-bug
        /// reproductions are reported as warnings.
        /// </summary>
        [MenuItem("Minecraft Clone/Dev/Validate Meshing")]
        public static void RunAll() => Execute();

        /// <summary>
        /// Builds and runs the meshing scenarios, returning the categorized result (the headless/CI entry point).
        /// </summary>
        /// <param name="logToConsole">When false, runs silently and only returns the result (for headless/CI use).</param>
        /// <param name="showProgress">When false, suppresses this suite's own progress bar (the aggregate runner drives one).</param>
        /// <returns>The categorized, timed result of the run.</returns>
        public static ValidationRunResult Execute(bool logToConsole = true, bool showProgress = true)
        {
            List<Scenario> scenarios = new List<Scenario>();
            AddBaselineScenarios(scenarios);
            AddRendererScenarios(scenarios);
            AddKnownBugScenarios(scenarios);
            return ValidationSuiteRunner.Execute("Meshing", scenarios, KnownBugChannel.Bug, logToConsole, showProgress);
        }

        /// <summary>Registers the baseline regression scenarios (implemented in MeshingValidationSuite.Baseline.cs).</summary>
        static partial void AddBaselineScenarios(List<Scenario> scenarios);

        /// <summary>
        /// Registers the renderer apply-path baselines (MH-6, implemented in MeshingValidationSuite.Renderer.cs).
        /// These exercise <see cref="SectionRenderer.UpdateMeshNative"/> via a separate fixture, not the
        /// meshing-job <see cref="Framework.MeshingTestWorld"/>; they count as baselines (must stay green).
        /// </summary>
        static partial void AddRendererScenarios(List<Scenario> scenarios);

        /// <summary>Registers the known-bug reproduction scenarios (none yet).</summary>
        static partial void AddKnownBugScenarios(List<Scenario> scenarios);
    }
}
