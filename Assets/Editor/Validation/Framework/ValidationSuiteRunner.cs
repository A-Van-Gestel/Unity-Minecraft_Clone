using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Debug = UnityEngine.Debug;

namespace Editor.Validation.Framework
{
    /// <summary>
    /// Shared runner for the editor validation suites. Executes a list of <see cref="Scenario"/> values,
    /// categorizes each as a baseline (regression) or known-bug result, times each one, and returns a
    /// machine-readable <see cref="ValidationRunResult"/>.
    /// <para>
    /// This replaces the ~90-line <c>Scenario</c> struct + <c>RunAll</c> loop that every suite previously
    /// copy-pasted (the VS-1 finding). Each suite now shrinks to its menu item, display name, and scenario
    /// registration; the run loop, categorized counting, colorized summary, and per-scenario timing live here
    /// once. Returning the result object (rather than logging into the void) is what lets VS-2's aggregate/CI
    /// entry points and VS-3's stale-assembly preamble attach in this one place.
    /// </para>
    /// </summary>
    public static class ValidationSuiteRunner
    {
        /// <summary>
        /// Runs every scenario and returns the categorized result. This is the headless/CI entry point — call it
        /// directly (and inspect <see cref="ValidationRunResult.Success"/>) from a batch-mode runner; the
        /// per-suite <c>[MenuItem] RunAll()</c> wrappers call it with console logging on.
        /// </summary>
        /// <param name="suiteName">The suite's display name (e.g. "Lighting Engine"), used in log headers/summary.</param>
        /// <param name="scenarios">The scenarios to run, in registration order.</param>
        /// <param name="channel">The vocabulary for known-bug state changes (fix/archive vs implement/promote).</param>
        /// <param name="logToConsole">When false, runs silently and only returns the result (for headless/CI use).</param>
        /// <returns>The categorized, timed result of the run.</returns>
        public static ValidationRunResult Execute(
            string suiteName,
            IReadOnlyList<Scenario> scenarios,
            KnownBugChannel channel = KnownBugChannel.Bug,
            bool logToConsole = true)
        {
            // suiteName is passed plain; per-suite header annotations (e.g. "(MT-1)"/"(MT-2)") were dropped in
            // the VS-1 migration — future: carry them as a structured Scenario/suite tag rather than baking them
            // into the display name, so the summary can surface them without hard-coding per suite.
            if (logToConsole)
                Debug.Log($"--- Starting {suiteName} Validation ---");

            List<ScenarioResult> results = new List<ScenarioResult>(scenarios.Count);
            int baselinePassed = 0, baselineFailed = 0, bugsReproduced = 0, bugsFixCandidates = 0;
            double totalMs = 0.0;

            foreach (Scenario scenario in scenarios)
            {
                if (logToConsole)
                    Debug.Log($"--- Scenario: {scenario.Name} ---");

                bool passed;
                Exception thrown = null;

                // A Stopwatch object per scenario is fine on this editor-only path; if a zero-alloc run is ever
                // wanted, Stopwatch.GetTimestamp() deltas measure the same interval without the allocation.
                Stopwatch stopwatch = Stopwatch.StartNew();
                try
                {
                    passed = scenario.Run();
                }
                catch (Exception e)
                {
                    thrown = e;
                    passed = false;
                }
                finally
                {
                    stopwatch.Stop();
                }

                double elapsedMs = stopwatch.Elapsed.TotalMilliseconds;
                totalMs += elapsedMs;

                results.Add(new ScenarioResult
                {
                    Name = scenario.Name,
                    KnownBugId = scenario.KnownBugId,
                    Passed = passed,
                    ElapsedMs = elapsedMs,
                    Exception = thrown,
                });

                CategorizeAndLog(scenario, passed, thrown, elapsedMs, channel, logToConsole,
                    ref baselinePassed, ref baselineFailed, ref bugsReproduced, ref bugsFixCandidates);
            }

            ValidationRunResult result = new ValidationRunResult
            {
                SuiteName = suiteName,
                Scenarios = results,
                BaselinePassed = baselinePassed,
                BaselineFailed = baselineFailed,
                BugsReproduced = bugsReproduced,
                BugsFixCandidates = bugsFixCandidates,
                TotalMs = totalMs,
            };

            if (logToConsole)
                LogSummary(result);

            return result;
        }

        /// <summary>Updates the running counts for a scenario and logs its per-scenario outcome line.</summary>
        private static void CategorizeAndLog(
            Scenario scenario, bool passed, Exception thrown, double elapsedMs, KnownBugChannel channel,
            bool logToConsole, ref int baselinePassed, ref int baselineFailed,
            ref int bugsReproduced, ref int bugsFixCandidates)
        {
            string elapsed = FormatDuration(elapsedMs);

            if (scenario.KnownBugId == null)
            {
                if (passed)
                {
                    baselinePassed++;
                    if (logToConsole) Debug.Log($"[PASS] {scenario.Name} ({elapsed})");
                }
                else
                {
                    baselineFailed++;
                    if (logToConsole)
                    {
                        string detail = thrown != null ? $"\nScenario threw: {thrown}" : "";
                        Debug.LogError($"[FAIL] {scenario.Name} ({elapsed}){detail}");
                    }
                }
            }
            else if (passed)
            {
                bugsFixCandidates++;
                if (logToConsole) Debug.Log($"<color=cyan>✅ {scenario.Name} ({elapsed}): known-bug scenario PASSES — {KnownBugPassMessage(scenario.KnownBugId, channel)}</color>");
            }
            else
            {
                bugsReproduced++;
                if (logToConsole) Debug.LogWarning($"⚠️ {scenario.Name} ({elapsed}): {KnownBugReproMessage(scenario.KnownBugId, channel)}");
            }
        }

        /// <summary>The "it now passes" message for a known-bug scenario, per <paramref name="channel"/>.</summary>
        private static string KnownBugPassMessage(string knownBugId, KnownBugChannel channel) =>
            channel == KnownBugChannel.Unimplemented
                ? $"{knownBugId} may be implemented. Verify, then promote it to a baseline."
                : $"{knownBugId} may be fixed. Verify in-game, then archive via the archive-fixed-bug workflow.";

        /// <summary>The "still reproduces" message for a known-bug scenario, per <paramref name="channel"/>.</summary>
        private static string KnownBugReproMessage(string knownBugId, KnownBugChannel channel) =>
            channel == KnownBugChannel.Unimplemented
                ? $"reproduces {knownBugId} (expected failure until implemented)."
                : $"reproduces {knownBugId} (expected failure until the bug is fixed).";

        /// <summary>Prints the categorized, PHPUnit-style summary derived entirely from the result counts.</summary>
        private static void LogSummary(ValidationRunResult result)
        {
            if (result.RanNothing)
            {
                Debug.LogWarning($"<color=yellow>{result.SuiteName.ToUpperInvariant()}: NO SCENARIOS RAN — nothing was validated.</color>");
                return;
            }

            string upper = result.SuiteName.ToUpperInvariant();
            int baselineTotal = result.BaselinePassed + result.BaselineFailed;

            if (result.BaselineFailed == 0)
                Debug.Log($"<color=green>ALL {result.BaselinePassed} {upper} BASELINE TESTS PASSED.</color> ({FormatDuration(result.TotalMs)} total)");
            else
                Debug.LogError($"<color=red>{result.BaselineFailed} OF {baselineTotal} {upper} BASELINE TESTS FAILED — REGRESSION.</color> ({FormatDuration(result.TotalMs)} total)");

            if (result.BugsReproduced > 0)
                Debug.Log($"{result.BugsReproduced} known-bug scenario(s) still reproduce their documented bug/feature (expected).");

            if (result.BugsFixCandidates > 0)
                Debug.Log($"<color=cyan>{result.BugsFixCandidates} known-bug scenario(s) now pass — fix/implementation candidates!</color>");
        }

        private const double HUMAN_BREAKDOWN_THRESHOLD_MS = 1000.0; // below this, a precise "N.N ms" reads better than a breakdown
        private const long MS_PER_SECOND = 1000;
        private const long MS_PER_MINUTE = 60 * MS_PER_SECOND;
        private const long MS_PER_HOUR = 60 * MS_PER_MINUTE;

        /// <summary>
        /// Formats a millisecond duration for log output. Sub-second durations stay as a precise, culture-invariant
        /// <c>"N.N ms"</c>; anything longer is broken into human-readable <c>h/min/s/ms</c> components so a
        /// multi-second or multi-minute total does not read as an unwieldy raw millisecond count.
        /// </summary>
        /// <param name="ms">The duration in milliseconds.</param>
        /// <returns>A human-readable duration string.</returns>
        private static string FormatDuration(double ms)
        {
            if (ms < HUMAN_BREAKDOWN_THRESHOLD_MS)
                return $"{ms.ToString("F1", CultureInfo.InvariantCulture)} ms";

            long totalMs = (long)Math.Round(ms);
            long hours = totalMs / MS_PER_HOUR;
            long minutes = (totalMs % MS_PER_HOUR) / MS_PER_MINUTE;
            long seconds = (totalMs % MS_PER_MINUTE) / MS_PER_SECOND;
            long millis = totalMs % MS_PER_SECOND;

            string result = $"{seconds} s {millis} ms";
            if (hours > 0 || minutes > 0) result = $"{minutes} min {result}";
            if (hours > 0) result = $"{hours} h {result}";
            return result;
        }
    }
}
