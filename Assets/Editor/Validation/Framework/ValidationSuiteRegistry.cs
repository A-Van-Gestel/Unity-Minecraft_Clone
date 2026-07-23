using System;
using System.Collections.Generic;
using Editor.Validation.Behavior;
using Editor.Validation.ChunkUnload;
using Editor.Validation.Commands;
using Editor.Validation.DeserializationRobustness;
using Editor.Validation.Generation;
using Editor.Validation.Lighting;
using Editor.Validation.LightScheduler;
using Editor.Validation.Meshing;
using Editor.Validation.MeshQueue;
using Editor.Validation.Placement;
using Editor.Validation.PoolPrune;
using Editor.Validation.SaveDurability;

namespace Editor.Validation.Framework
{
    /// <summary>A validation suite registered for aggregate ("Validate All") and headless/CI execution.</summary>
    public readonly struct RegisteredSuite
    {
        /// <summary>Display name — matches the string the suite passes to <see cref="ValidationSuiteRunner"/>.</summary>
        public readonly string DisplayName;

        /// <summary>Runs the suite: <c>(logToConsole, showProgress) =&gt; result</c>. The aggregate runner passes <c>showProgress: false</c> so it can drive one combined bar.</summary>
        public readonly Func<bool, bool, ValidationRunResult> Run;

        /// <summary>Creates a registration entry.</summary>
        /// <param name="displayName">The suite's display name.</param>
        /// <param name="run">The suite's <c>Execute(logToConsole, showProgress)</c> entry point.</param>
        public RegisteredSuite(string displayName, Func<bool, bool, ValidationRunResult> run)
        {
            DisplayName = displayName;
            Run = run;
        }
    }

    /// <summary>
    /// The explicit list of standard validation suites the aggregate runner executes.
    /// <para>
    /// This is a hand-maintained list rather than reflection/attribute discovery: for a small, rarely-changing
    /// set the explicit form is more honest (every suite is visible in one place) and its failure mode is a
    /// compile error, not a silently dropped suite. The list order is the run and report order. Adding a suite
    /// is one line here; the aggregate runner cross-checks the count so a shrinking list can't pass unnoticed.
    /// </para>
    /// <para>
    /// Scope: the standard runner-based suites only. The deep-run/nightly variants (lighting fuzz sweeps, fluid
    /// parallel-determinism) and the not-yet-migrated standalone tests (VoxelMetadataUtility, FastNoiseLite) stay
    /// off this list — they auto-join the instant they return a <see cref="ValidationRunResult"/> and get a line here.
    /// </para>
    /// </summary>
    public static class ValidationSuiteRegistry
    {
        /// <summary>The number of standard suites expected on the list — a floor the aggregate runner asserts against.</summary>
        public const int ExpectedSuiteCount = 15;

        /// <summary>The registered suites, in run/report order.</summary>
        public static readonly IReadOnlyList<RegisteredSuite> Suites = new[]
        {
            new RegisteredSuite("Lighting Engine", LightingValidationSuite.Execute),
            new RegisteredSuite("Meshing", MeshingValidationSuite.Execute),
            new RegisteredSuite("Behavior", BehaviorValidationSuite.Execute),
            new RegisteredSuite("Placement", PlacementValidationSuite.Execute),
            new RegisteredSuite("Mesh Build Queue", MeshBuildQueueValidationSuite.Execute),
            new RegisteredSuite("Light Work Scheduler", LightWorkSchedulerValidationSuite.Execute),
            new RegisteredSuite("Chunk Math", ChunkMathValidationSuite.Execute),
            new RegisteredSuite("Chunk Unload Decision", ChunkUnloadDecisionValidationSuite.Execute),
            new RegisteredSuite("Pool Prune Decision", PoolPruneDecisionValidationSuite.Execute),
            new RegisteredSuite("Save Durability", SaveDurabilityValidationSuite.Execute),
            new RegisteredSuite("Deserialization Robustness", DeserializationRobustnessValidationSuite.Execute),
            new RegisteredSuite("Spawn", SpawnValidationSuite.Execute),
            new RegisteredSuite("Command Console", CommandConsoleValidationSuite.Execute),
            new RegisteredSuite("Worm Carver", WormCarverValidationSuite.Execute),
            new RegisteredSuite("Validation Framework", ValidationFrameworkSelfTest.Execute),
        };
    }
}
