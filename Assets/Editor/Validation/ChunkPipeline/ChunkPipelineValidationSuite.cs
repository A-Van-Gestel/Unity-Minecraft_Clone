using System.Collections.Generic;
using Editor.Dev;
using Editor.Validation.Framework;
using UnityEditor;

namespace Editor.Validation.ChunkPipeline
{
    /// <summary>
    /// Validation suite for the chunk generation → lighting → meshing <b>state machine</b>: the readiness
    /// gates, the scheduling arms, and the unload policy, driven through adversarial multi-chunk event
    /// orders. Two assertion families: <b>convergence</b> (every chunk eventually reaches lit + meshed) and
    /// <b>flag pairing</b> (no flag ends set whose clear site is unreachable).
    /// <para>The individual decision <i>rules</i> already have suites (Chunk Unload Decision, Light Work
    /// Scheduler, Mesh Build Queue, Pipeline Backpressure, and the meshing suite's MP-2 baselines). What had
    /// no guard until now is their <b>composition over time</b> — which is where all three historical
    /// pipeline deadlocks (CHUNK_LIFECYCLE_PIPELINE.md §9.1, §9.3, §9.6) lived.</para>
    /// <para><b>B1 is the harness's own prove-red</b> and must be read first: it neuters the §9.6 strand
    /// guard and requires the pump to deadlock. Every other scenario's convergence assertion is only as
    /// trustworthy as B1's failure to converge.</para>
    /// </summary>
    public static partial class ChunkPipelineValidationSuite
    {
        /// <summary>Runs every scenario and prints a categorized summary via the shared runner.</summary>
        [MenuItem("Minecraft Clone/Dev/Validate Chunk Pipeline", priority = DevMenuPriority.Validation)]
        public static void RunAll() => Execute();

        /// <summary>
        /// Builds and runs the chunk-pipeline scenarios, returning the categorized result (the headless/CI
        /// entry point). <see cref="KnownBugChannel.Unimplemented"/> — no open pipeline bug has a repro here
        /// yet; the channel exists for parity with the other suites.
        /// </summary>
        /// <param name="logToConsole">When false, runs silently and only returns the result (for headless/CI use).</param>
        /// <param name="showProgress">When false, suppresses this suite's own progress bar (the aggregate runner drives one).</param>
        /// <returns>The categorized, timed result of the run.</returns>
        public static ValidationRunResult Execute(bool logToConsole = true, bool showProgress = true)
        {
            List<Scenario> scenarios = new List<Scenario>();
            AddBaselineScenarios(scenarios);
            return ValidationSuiteRunner.Execute("Chunk Pipeline", scenarios, KnownBugChannel.Unimplemented,
                logToConsole, showProgress);
        }

        /// <summary>Registers the baseline scenarios (implemented in the <c>.Baseline.cs</c> partial).</summary>
        /// <param name="scenarios">The scenario list to append to.</param>
        static partial void AddBaselineScenarios(List<Scenario> scenarios);
    }
}
