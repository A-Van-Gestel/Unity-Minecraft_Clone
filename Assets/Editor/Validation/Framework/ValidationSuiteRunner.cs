using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEngine;
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
        /// <param name="showProgress">When false, suppresses this suite's own <see cref="EditorUtility"/> progress
        /// bar. The aggregate runner (VS-2 "Validate All") sets this false so it can drive a single combined bar
        /// instead of each suite's inner bar clobbering it (only one modal progress dialog exists).</param>
        /// <returns>The categorized, timed result of the run.</returns>
        public static ValidationRunResult Execute(
            string suiteName,
            IReadOnlyList<Scenario> scenarios,
            KnownBugChannel channel = KnownBugChannel.Bug,
            bool logToConsole = true,
            bool showProgress = true)
        {
            // Stale-code preamble (VS-3): warn loudly if the loaded assemblies may be out of date vs source on disk.
            // Diagnostic only — never throws, never fails a baseline. Suppressed while the aggregate runner's scope is
            // open (it checks once around its whole loop, not once per inner suite).
            StaleAssemblyGuard.WarnIfStale();

            // suiteName is passed plain; per-suite header annotations (e.g. "(MT-1)"/"(MT-2)") were dropped in
            // the VS-1 migration — future: carry them as a structured Scenario/suite tag rather than baking them
            // into the display name, so the summary can surface them without hard-coding per suite.
            if (logToConsole)
                Debug.Log($"--- Starting {suiteName} Validation ---");

            List<ScenarioResult> results = new List<ScenarioResult>(scenarios.Count);
            int baselinePassed = 0, baselineFailed = 0, bugsReproduced = 0, bugsFixCandidates = 0;
            double totalMs = 0.0;

            // Live progress bar for interactive runs: the suites run synchronously on the main thread (the console
            // can't repaint until they finish), but EditorUtility force-repaints its progress dialog on each call,
            // so this is the only "what's running now" signal during a long run. Suppressed for headless/batch runs
            // (no GUI) and when the caller drives its own bar (showProgress == false, e.g. the aggregate runner).
            // Cleared in the finally so an exception can't leave it stuck on screen.
            bool showProgressBar = showProgress && logToConsole && !Application.isBatchMode;

            try
            {
                for (int i = 0; i < scenarios.Count; i++)
                {
                    Scenario scenario = scenarios[i];

                    if (showProgressBar)
                        EditorUtility.DisplayProgressBar(
                            $"Validating {suiteName}",
                            $"Scenario {i + 1}/{scenarios.Count}: {scenario.Name}",
                            i / (float)scenarios.Count);

                    if (logToConsole)
                        Debug.Log($"--- Scenario {i + 1}/{scenarios.Count}: {scenario.Name} ---");

                    bool passed;
                    Exception thrown = null;

                    // A Stopwatch object per scenario is fine on this editor-only path; if a zero-alloc run is ever
                    // wanted, Stopwatch.GetTimestamp() deltas measure the same interval without the allocation.
                    Stopwatch stopwatch = Stopwatch.StartNew();
                    // B8: watch for an engine fail-safe (tagged console error) fired during THIS scenario's body.
                    FailSafeErrorScope failSafe = new FailSafeErrorScope();
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
                        failSafe.Dispose();
                    }

                    // B8: an engine fail-safe (e.g. the lighting BFS work-cap, Bug 16) reports via a tagged
                    // console error but cannot flip a scenario's own return value — promote it to a hard failure
                    // here, so a runaway that arms on ANY scenario's geometry reds the suite instead of hiding
                    // under a green summary. Generalizes the per-baseline WorkCapAbortListener into a suite-wide
                    // invariant shared by all suites. Only force-fails a scenario that otherwise passed (a
                    // scenario that already failed keeps its own diagnosis).
                    if (passed && failSafe.Tripped)
                    {
                        passed = false;
                        if (logToConsole)
                            Debug.LogError($"[FAIL-SAFE] {scenario.Name}: an engine fail-safe fired during this scenario — {failSafe.FirstMessage}");
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
            }
            finally
            {
                if (showProgressBar)
                    EditorUtility.ClearProgressBar();
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

        private const int SLOWEST_SCENARIO_COUNT = 3; // how many slowest scenarios to surface in the summary

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

            LogFailedBaselines(result);

            if (result.BugsReproduced > 0)
                Debug.Log($"{result.BugsReproduced} known-bug scenario(s) still reproduce their documented bug/feature (expected).");

            if (result.BugsFixCandidates > 0)
                Debug.Log($"<color=cyan>{result.BugsFixCandidates} known-bug scenario(s) now pass — fix/implementation candidates!</color>");

            LogSlowestScenarios(result);
        }

        /// <summary>
        /// Lists the failed baseline scenarios by name (with timing) directly under the red summary line, so a
        /// regression run does not require scrolling back through the per-scenario log to find what broke.
        /// </summary>
        /// <param name="result">The completed run result.</param>
        private static void LogFailedBaselines(ValidationRunResult result)
        {
            if (result.BaselineFailed == 0)
                return;

            StringBuilder sb = new StringBuilder($"<color=red>Failed baselines ({result.BaselineFailed}):</color>");
            foreach (ScenarioResult scenario in result.Scenarios)
            {
                if (scenario.IsKnownBug || scenario.Passed)
                    continue;
                sb.Append($"\n  • {scenario.Name} ({FormatDuration(scenario.ElapsedMs)})");
                if (scenario.Exception != null)
                    sb.Append(" — threw");
            }

            Debug.LogError(sb.ToString());
        }

        /// <summary>
        /// Surfaces the slowest scenarios so the per-scenario timing is actionable — a scenario that has drifted
        /// pathologically slow stands out at the bottom of the run instead of hiding in the per-scenario log.
        /// </summary>
        /// <param name="result">The completed run result.</param>
        private static void LogSlowestScenarios(ValidationRunResult result)
        {
            int show = Math.Min(SLOWEST_SCENARIO_COUNT, result.Scenarios.Count);
            if (show <= 0)
                return;

            List<ScenarioResult> sorted = new List<ScenarioResult>(result.Scenarios);
            sorted.Sort((a, b) => b.ElapsedMs.CompareTo(a.ElapsedMs));

            StringBuilder sb = new StringBuilder($"Slowest {show} scenario(s):");
            for (int i = 0; i < show; i++)
                sb.Append($"\n  {i + 1}. {sorted[i].Name} ({FormatDuration(sorted[i].ElapsedMs)})");

            Debug.Log(sb.ToString());
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
        internal static string FormatDuration(double ms)
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

        /// <summary>
        /// Console-error markers that signal an engine fail-safe fired (B8). A scenario during whose body a
        /// <see cref="LogType.Error"/> carrying one of these is logged is force-failed by <see cref="Execute"/>
        /// — a suite-wide termination/abort invariant. A future engine fail-safe that reports via a tagged
        /// console error joins by adding its marker here. NOT every error: the fault-isolation baselines
        /// deliberately log errors and must still pass, so the match is restricted to these explicit tags.
        /// </summary>
        private static readonly string[] s_failSafeErrorMarkers =
        {
            "[LightingJob DIAG]", // NeighborhoodLightingJob.MAX_BFS_NODES_PER_PASS runaway abort (Bug 16 permanent fail-safe)
        };

        /// <summary>
        /// True when a console message is an engine fail-safe report — a <see cref="LogType.Error"/> carrying a
        /// <see cref="s_failSafeErrorMarkers"/> tag. Pure, so the self-test can pin it directly without
        /// logging (a real log would bubble through the global <see cref="Application.logMessageReceived"/> into
        /// the enclosing self-test scenario's own scope and cross-fail it).
        /// </summary>
        /// <param name="condition">The log message text.</param>
        /// <param name="type">The log severity.</param>
        /// <returns>True when the message is a fail-safe report.</returns>
        internal static bool IsFailSafeError(string condition, LogType type)
        {
            if (type != LogType.Error || condition == null)
                return false;
            foreach (string marker in s_failSafeErrorMarkers)
                if (condition.Contains(marker))
                    return true;
            return false;
        }

        /// <summary>
        /// Listens for engine fail-safe console errors (see <see cref="IsFailSafeError"/>) for the duration of a
        /// single scenario body (B8). Subscribes on construction, unsubscribes on <see cref="Dispose"/>; the
        /// runner force-fails the scenario when <see cref="Tripped"/>. Generalizes the per-baseline
        /// <c>WorkCapAbortListener</c> (Bug 16) into a suite-wide invariant.
        /// <para>
        /// <b>Precondition — synchronous main-thread delivery.</b> Detection assumes the engine fail-safe's
        /// <c>Debug.LogError</c> is raised on the main thread <i>inside</i> the scenario body, where
        /// <see cref="Application.logMessageReceived"/> fires inline (before <see cref="Dispose"/>). This holds
        /// for every current suite: they run their jobs via <c>job.Run()</c> on the main thread, so a work-cap
        /// abort logs synchronously within the scope. A future suite that SCHEDULES a job off the main thread
        /// would have its abort marshaled to a later main-thread pump (<c>logMessageReceived</c> queues
        /// non-main-thread logs) and delivered AFTER this scope disposes — escaping the trip. Such a suite must
        /// also subscribe to <c>Application.logMessageReceivedThreaded</c> and guard
        /// <see cref="Tripped"/>/<see cref="FirstMessage"/> for concurrent writes.
        /// </para>
        /// </summary>
        internal sealed class FailSafeErrorScope : IDisposable
        {
            /// <summary>True once a fail-safe error was seen while this scope was open.</summary>
            public bool Tripped { get; private set; }

            /// <summary>The first fail-safe error message seen (for the runner's force-fail log).</summary>
            public string FirstMessage { get; private set; }

            public FailSafeErrorScope()
            {
                // UDR0004 false positive: this listener deregisters in Dispose and every use site is a scoped
                // new/Dispose pair, not the OnDisable pattern the analyzer recognizes.
#pragma warning disable UDR0004
                Application.logMessageReceived += OnLog;
#pragma warning restore UDR0004
            }

            public void Dispose() => Application.logMessageReceived -= OnLog;

            /// <summary>
            /// Test seam: feeds a message straight to the detection logic, bypassing the global
            /// <see cref="Application.logMessageReceived"/> event (so a self-test can prove the trip without a
            /// real log that would bubble into the enclosing scenario's scope).
            /// </summary>
            /// <param name="condition">The log message text.</param>
            /// <param name="type">The log severity.</param>
            internal void Feed(string condition, LogType type) => OnLog(condition, null, type);

            private void OnLog(string condition, string stackTrace, LogType type)
            {
                if (Tripped || !IsFailSafeError(condition, type))
                    return;
                Tripped = true;
                FirstMessage = condition;
            }
        }
    }
}
