using System;
using System.Collections.Generic;

namespace Editor.Validation.Framework
{
    /// <summary>The outcome of a single <see cref="Scenario"/> within a suite run.</summary>
    public sealed class ScenarioResult
    {
        /// <summary>The scenario name.</summary>
        public string Name;

        /// <summary>The documented bug/feature id, or null when this was a baseline scenario.</summary>
        public string KnownBugId;

        /// <summary>True when the scenario body returned true and did not throw.</summary>
        public bool Passed;

        /// <summary>Wall-clock time the scenario body took, in milliseconds.</summary>
        public double ElapsedMs;

        /// <summary>The exception the scenario threw, or null. A throw is always a failure (<see cref="Passed"/> is false).</summary>
        public Exception Exception;

        /// <summary>True when this scenario reproduces a documented bug/feature (has a non-null <see cref="KnownBugId"/>).</summary>
        public bool IsKnownBug => KnownBugId != null;
    }

    /// <summary>
    /// The machine-readable result of running a validation suite through <see cref="ValidationSuiteRunner"/>.
    /// <para>
    /// This is the single object every consumer reads from: the interactive menu runner formats its console
    /// summary from these counts, and the (future) VS-2 CI entry point derives its exit code from
    /// <see cref="Success"/> and emits NUnit-XML from <see cref="Scenarios"/>. It is intentionally a plain
    /// data carrier — returning it (rather than <c>void</c>) is the VS-1 design constraint that lets VS-2/VS-3
    /// and a future Unity Test Framework bridge attach without re-plumbing the runner.
    /// </para>
    /// <para>Contract edges:
    /// <list type="bullet">
    /// <item>A scenario that <b>throws</b> is recorded on its <see cref="ScenarioResult.Exception"/>, counts as
    /// a baseline failure, and is still timed.</item>
    /// <item>A known-bug scenario that starts <b>passing</b> increments <see cref="BugsFixCandidates"/>. This is
    /// informational only — it never fails <see cref="Success"/>.</item>
    /// <item>An <b>empty</b> suite yields <see cref="Success"/> == true vacuously; <see cref="RanNothing"/> flags
    /// it so callers (CI) can treat "0 baselines ran" as suspicious.</item>
    /// </list>
    /// </para>
    /// </summary>
    public sealed class ValidationRunResult
    {
        /// <summary>The suite's display name.</summary>
        public string SuiteName;

        /// <summary>Per-scenario outcomes in registration order.</summary>
        public IReadOnlyList<ScenarioResult> Scenarios;

        /// <summary>Number of baseline (regression) scenarios that passed.</summary>
        public int BaselinePassed;

        /// <summary>Number of baseline (regression) scenarios that failed — the regression signal.</summary>
        public int BaselineFailed;

        /// <summary>Number of known-bug scenarios that still reproduce their documented bug/feature (expected failures).</summary>
        public int BugsReproduced;

        /// <summary>Number of known-bug scenarios that now pass (fix/implementation candidates — informational).</summary>
        public int BugsFixCandidates;

        /// <summary>Total wall-clock time for all scenarios, in milliseconds.</summary>
        public double TotalMs;

        /// <summary>True when no baseline scenario failed. The CI exit-code source (VS-2).</summary>
        public bool Success => BaselineFailed == 0;

        /// <summary>True when the suite registered no scenarios — a vacuous pass that CI should treat as suspicious.</summary>
        public bool RanNothing => Scenarios == null || Scenarios.Count == 0;
    }
}
