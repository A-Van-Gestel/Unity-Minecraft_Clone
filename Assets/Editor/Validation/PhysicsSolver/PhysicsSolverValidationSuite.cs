using System.Collections.Generic;
using System.Globalization;
using Editor.Dev;
using Editor.Validation.Framework;
using UnityEditor;
using UnityEngine;

namespace Editor.Validation.PhysicsSolver
{
    /// <summary>
    /// Entry point and runner for the physics / collision-solver validation suite (<b>NS-4</b>) — the real
    /// <c>Physics.VoxelRigidbody</c> resolving against the real <c>World.CheckPhysicsCollision</c> over synthetic
    /// voxel fields. It is the automated guard that <c>SUB_VOXEL_COLLISION_SYSTEM.md</c> §5 Phase 6c left unchecked
    /// and that VQ-3 had to substitute a throwaway golden master for.
    /// <para>
    /// Scenarios assert final position / resolved displacement / <c>IsGrounded</c> for deterministic fixture
    /// geometry. There is no naive-solver oracle: the expectations are analytic contact faces derived from the
    /// pinned entity dimensions (<c>PhysicsTestWorld</c>'s <c>Entity*</c> constants) and the authored block volumes,
    /// plus one genuine differential (B15's substep invariance).
    /// </para>
    /// <para>
    /// <b>Namespace note:</b> this lives in <c>Editor.Validation.PhysicsSolver</c>, not <c>…Physics</c>, precisely
    /// because the system under test lives in the global <c>Physics</c> namespace — a same-named validation
    /// namespace would shadow it (and <c>UnityEngine.Physics</c>) inside every file here.
    /// </para>
    /// </summary>
    public static partial class PhysicsSolverValidationSuite
    {
        /// <summary>
        /// Positional tolerance for solver end-state assertions, in meters. Loose enough to absorb the solver's
        /// <c>COLLISION_EPSILON</c> stand-off (which is deliberately private to the solver, so the suite does not
        /// mirror its value) and float accumulation across substeps; far tighter than any defect this suite guards,
        /// all of which move the entity by a quarter block or more.
        /// </summary>
        internal const float PositionTolerance = 0.005f;

        /// <summary>
        /// Runs every registered scenario and prints a categorized summary via the shared
        /// <see cref="ValidationSuiteRunner"/>. Baseline failures mark the suite red; known-bug reproductions are
        /// reported as warnings.
        /// </summary>
        [MenuItem("Minecraft Clone/Dev/Validate Physics Solver", priority = DevMenuPriority.Validation)]
        public static void RunAll() => Execute();

        /// <summary>
        /// Builds and runs the solver scenarios, returning the categorized result (the headless/CI entry point).
        /// </summary>
        /// <param name="logToConsole">When false, runs silently and only returns the result (for headless/CI use).</param>
        /// <param name="showProgress">When false, suppresses this suite's own progress bar (the aggregate runner drives one).</param>
        /// <returns>The categorized, timed result of the run.</returns>
        public static ValidationRunResult Execute(bool logToConsole = true, bool showProgress = true)
        {
            // Known-bug scenarios: none open.
            List<Scenario> scenarios = new List<Scenario>();
            AddBaselineScenarios(scenarios);
            return ValidationSuiteRunner.Execute("Physics Solver", scenarios, KnownBugChannel.Bug, logToConsole,
                showProgress);
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

        /// <summary>
        /// Asserts a float lands within <paramref name="tolerance"/> of its expected value, reporting the actual
        /// value and the miss distance — the debuggable form for every position/displacement assertion here.
        /// </summary>
        /// <param name="actual">The measured value.</param>
        /// <param name="expected">The expected value.</param>
        /// <param name="what">What the value is (logged on failure).</param>
        /// <param name="tolerance">Allowed absolute difference; defaults to <see cref="PositionTolerance"/>.</param>
        /// <returns>True when the value is within tolerance.</returns>
        private static bool ExpectApprox(float actual, float expected, string what,
            float tolerance = PositionTolerance)
        {
            float delta = Mathf.Abs(actual - expected);
            return Expect(delta <= tolerance,
                $"{what}: expected {Format(expected)}, got {Format(actual)} (off by {Format(delta)}, " +
                $"tolerance {Format(tolerance)})");
        }

        /// <summary>Culture-invariant float formatting for assertion messages.</summary>
        /// <param name="value">The value to format.</param>
        /// <returns>The formatted value.</returns>
        private static string Format(float value) => value.ToString("F4", CultureInfo.InvariantCulture);

        /// <summary>Registers the baseline regression scenarios (implemented in PhysicsSolverValidationSuite.Baseline.cs).</summary>
        static partial void AddBaselineScenarios(List<Scenario> scenarios);
    }
}
