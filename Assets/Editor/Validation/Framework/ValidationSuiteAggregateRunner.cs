using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Editor.Validation.Framework
{
    /// <summary>
    /// Runs every registered validation suite (VS-2 "Validate All") in one pass and returns a combined
    /// <see cref="AggregateRunResult"/>. This is the shared core behind both the interactive menu item and the
    /// headless/CI entry point; the CI wrapper (<c>ValidationSuiteCI</c>) adds NUnit-XML emission and the exit code.
    /// <para>
    /// <b>Isolation is enforced, not assumed.</b> The suites share process-global state — most importantly the
    /// static <c>World.Instance</c> singleton, which several harnesses stub via reflection and restore on dispose.
    /// Because the suites run sequentially in one domain, a suite that fails to restore that singleton would make
    /// the <i>next</i> suite order-dependent (a green aggregate hiding a red individual run, or vice-versa) — the
    /// worst class of test bug. So around every suite this runner snapshots <c>World.Instance</c>, and if the suite
    /// left it mutated it force-restores the snapshot (protecting the next suite) and marks the offending suite
    /// failed and untrusted (making the leak a loud, attributed error instead of a silent heisenbug).
    /// </para>
    /// </summary>
    public static class ValidationSuiteAggregateRunner
    {
        /// <summary>Menu entry: runs all registered suites and logs the combined summary.</summary>
        [MenuItem("Minecraft Clone/Dev/Validate All", priority = 10)]
        public static void RunAll() => Run();

        /// <summary>
        /// Runs the given suites (default: every registered suite) and returns the combined result.
        /// </summary>
        /// <param name="logToConsole">When false, runs silently and only returns the result (for headless/CI use).</param>
        /// <param name="suites">The suites to run, in order. Null runs every suite in <see cref="ValidationSuiteRegistry.Suites"/>.</param>
        /// <returns>The combined, timed result across all suites.</returns>
        public static AggregateRunResult Run(bool logToConsole = true, IReadOnlyList<RegisteredSuite> suites = null)
        {
            suites ??= ValidationSuiteRegistry.Suites;

            if (logToConsole && suites.Count < ValidationSuiteRegistry.ExpectedSuiteCount
                             && ReferenceEquals(suites, ValidationSuiteRegistry.Suites))
                Debug.LogWarning($"<color=yellow>Validate All: registry has {suites.Count} suites, expected at least " +
                                 $"{ValidationSuiteRegistry.ExpectedSuiteCount} — a suite may have been dropped.</color>");

            if (logToConsole)
                Debug.Log($"=== Validate All: running {suites.Count} suite(s) ===");

            List<ValidationRunResult> results = new List<ValidationRunResult>(suites.Count);
            bool showProgress = logToConsole && !Application.isBatchMode;
            Stopwatch total = Stopwatch.StartNew();

            try
            {
                for (int i = 0; i < suites.Count; i++)
                {
                    RegisteredSuite suite = suites[i];

                    if (showProgress)
                        EditorUtility.DisplayProgressBar("Validate All",
                            $"Suite {i + 1}/{suites.Count}: {suite.DisplayName}", i / (float)suites.Count);

                    results.Add(RunOneIsolated(suite, logToConsole));
                }
            }
            finally
            {
                if (showProgress)
                    EditorUtility.ClearProgressBar();
                total.Stop();
            }

            AggregateRunResult aggregate = new AggregateRunResult
            {
                Suites = results,
                TotalMs = total.Elapsed.TotalMilliseconds,
            };

            if (logToConsole)
                LogCombinedSummary(aggregate);

            return aggregate;
        }

        /// <summary>
        /// Runs one suite with the global-state isolation guard around it (see the type remarks). The suite's own
        /// progress bar is suppressed (<c>showProgress: false</c>) so this runner's single combined bar is the only one.
        /// </summary>
        /// <param name="suite">The suite to run.</param>
        /// <param name="logToConsole">Whether the suite (and the guard) should log.</param>
        /// <returns>The suite's result, replaced by a failed result if it threw or leaked global state.</returns>
        private static ValidationRunResult RunOneIsolated(RegisteredSuite suite, bool logToConsole)
        {
            object worldBefore = World.Instance;

            ValidationRunResult result;
            try
            {
                result = suite.Run(logToConsole, false);
            }
            catch (Exception e)
            {
                if (logToConsole)
                    Debug.LogError($"<color=red>[SUITE ERROR] {suite.DisplayName} threw before returning a result:</color>\n{e}");
                result = SuiteLevelFailure(suite.DisplayName, "suite-level failure", e);
            }

            // Isolation guard: a suite must leave the shared World.Instance exactly as it found it.
            if (!ReferenceEquals(World.Instance, worldBefore))
            {
                ValidationReflection.SetStaticProperty(typeof(World), nameof(World.Instance), worldBefore);
                if (logToConsole)
                    Debug.LogError($"<color=red>ISOLATION VIOLATION: '{suite.DisplayName}' left World.Instance mutated. " +
                                   "Restored it to protect the next suite; this suite's run is UNTRUSTED and marked failed.</color>");
                result = AppendIsolationFailure(result, suite.DisplayName);
            }

            return result;
        }

        /// <summary>Builds a one-scenario failed result for a suite that threw before it could return one.</summary>
        /// <param name="suiteName">The suite's display name.</param>
        /// <param name="scenarioSuffix">The synthetic scenario's name suffix.</param>
        /// <param name="thrown">The exception, carried on the synthetic scenario.</param>
        /// <returns>A result whose single baseline scenario is a failure.</returns>
        private static ValidationRunResult SuiteLevelFailure(string suiteName, string scenarioSuffix, Exception thrown)
        {
            List<ScenarioResult> scenarios = new List<ScenarioResult>
            {
                new ScenarioResult { Name = $"{suiteName} ({scenarioSuffix})", Passed = false, Exception = thrown },
            };
            return new ValidationRunResult { SuiteName = suiteName, Scenarios = scenarios, BaselineFailed = 1 };
        }

        /// <summary>
        /// Returns a copy of <paramref name="original"/> with one extra failed "isolation guard" scenario and an
        /// incremented baseline-failure count, so a suite that leaked global state can never report success.
        /// </summary>
        /// <param name="original">The suite's own result (which may itself have passed).</param>
        /// <param name="suiteName">The suite's display name.</param>
        /// <returns>The result marked as an isolation failure.</returns>
        private static ValidationRunResult AppendIsolationFailure(ValidationRunResult original, string suiteName)
        {
            List<ScenarioResult> scenarios = original?.Scenarios != null
                ? new List<ScenarioResult>(original.Scenarios)
                : new List<ScenarioResult>();

            scenarios.Add(new ScenarioResult
            {
                Name = $"{suiteName} (isolation guard)",
                Passed = false,
                Exception = new Exception("Suite leaked global World.Instance state — order-dependent, run untrusted."),
            });

            return new ValidationRunResult
            {
                SuiteName = original?.SuiteName ?? suiteName,
                Scenarios = scenarios,
                BaselinePassed = original?.BaselinePassed ?? 0,
                BaselineFailed = (original?.BaselineFailed ?? 0) + 1,
                BugsReproduced = original?.BugsReproduced ?? 0,
                BugsFixCandidates = original?.BugsFixCandidates ?? 0,
                TotalMs = original?.TotalMs ?? 0.0,
            };
        }

        /// <summary>Prints the per-suite lines and the single combined verdict derived from the roll-up counts.</summary>
        /// <param name="aggregate">The completed aggregate result.</param>
        private static void LogCombinedSummary(AggregateRunResult aggregate)
        {
            StringBuilder sb = new StringBuilder("=== Validate All — combined summary ===");
            foreach (ValidationRunResult s in aggregate.Suites)
            {
                int baselineTotal = s.BaselinePassed + s.BaselineFailed;
                string icon = s.RanNothing ? "⚠️" : s.Success ? "✅" : "❌";
                sb.Append($"\n  {icon} {s.SuiteName}: {s.BaselinePassed}/{baselineTotal} baselines");
                if (s.BugsReproduced > 0) sb.Append($", {s.BugsReproduced} known-bug repro");
                if (s.BugsFixCandidates > 0) sb.Append($", {s.BugsFixCandidates} fix-candidate");
                if (s.RanNothing) sb.Append(" — RAN NOTHING");
                sb.Append($" ({ValidationSuiteRunner.FormatDuration(s.TotalMs)})");
            }

            Debug.Log(sb.ToString());

            int totalBaseline = aggregate.BaselinePassed + aggregate.BaselineFailed;
            string time = ValidationSuiteRunner.FormatDuration(aggregate.TotalMs);

            if (!aggregate.Success)
                Debug.LogError($"<color=red>VALIDATE ALL: REGRESSION — {aggregate.BaselineFailed} of {totalBaseline} baselines " +
                               $"failed across {aggregate.FailedSuiteCount} suite(s).</color> ({time} total)");
            else if (aggregate.AnySuiteRanNothing)
                Debug.LogWarning($"<color=yellow>VALIDATE ALL: no baseline failed, but a suite ran nothing — treat as suspicious.</color> ({time} total)");
            else
                Debug.Log($"<color=green>VALIDATE ALL: all {aggregate.BaselinePassed} baselines across {aggregate.SuiteCount} suites PASSED.</color> ({time} total)");

            if (aggregate.BugsReproduced > 0)
                Debug.Log($"{aggregate.BugsReproduced} known-bug scenario(s) still reproduce their documented bug/feature (expected).");
            if (aggregate.BugsFixCandidates > 0)
                Debug.Log($"<color=cyan>{aggregate.BugsFixCandidates} known-bug scenario(s) now pass — fix/implementation candidates!</color>");
        }
    }
}
