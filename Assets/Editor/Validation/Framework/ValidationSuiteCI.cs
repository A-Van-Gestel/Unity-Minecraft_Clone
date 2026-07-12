using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Editor.Validation.Framework
{
    /// <summary>
    /// Headless entry point for the aggregate validation run (VS-2). <see cref="RunHeadless"/> is the
    /// <c>-executeMethod</c> target for a batch/CI invocation: it runs the selected suites, writes an NUnit3 XML
    /// results file, and exits the editor with a non-zero code on any baseline failure (or a suite that ran nothing).
    /// <para>
    /// Near-term the primary consumer is an AI agent driving the editor, which calls <see cref="RunSelected"/> (no
    /// process exit) via <c>Unity_RunCommand</c> and inspects the returned <see cref="AggregateRunResult"/>. The
    /// batch <see cref="RunHeadless"/> path is built for the same set + a real exit code whenever CI actually lands.
    /// </para>
    /// <para>Command line: <c>Unity -batchmode -projectPath &lt;p&gt; -executeMethod
    /// Editor.Validation.Framework.ValidationSuiteCI.RunHeadless [-validationSuites "Lighting Engine,Meshing"]
    /// [-nunitXml &lt;path&gt;]</c>. Do <b>not</b> pass <c>-quit</c> — <see cref="RunHeadless"/> exits itself.</para>
    /// </summary>
    public static class ValidationSuiteCI
    {
        private const string SUITES_ARG = "-validationSuites";
        private const string XML_ARG = "-nunitXml";
        private const string DEFAULT_XML_PATH = "TestResults/validation-results.xml";

        /// <summary>
        /// Batch/CI entry point. Runs the selected suites (all by default; a subset via <c>-validationSuites</c>),
        /// writes the NUnit3 XML results file (<c>-nunitXml</c>, default <c>TestResults/validation-results.xml</c>),
        /// and calls <see cref="EditorApplication.Exit"/> — 0 only when every baseline passed and no suite ran
        /// nothing, else 1. Never propagates an exception out: any crash logs and exits 1 so a harness can't hang.
        /// </summary>
        public static void RunHeadless()
        {
            try
            {
                string[] args = Environment.GetCommandLineArgs();
                string xmlPath = GetArgValue(args, XML_ARG) ?? DEFAULT_XML_PATH;
                string suitesCsv = GetArgValue(args, SUITES_ARG);

                if (!TryResolveSuites(suitesCsv, out IReadOnlyList<RegisteredSuite> suites, out string error))
                {
                    Debug.LogError($"<color=red>Validate All (headless): {error}</color>");
                    EditorApplication.Exit(1);
                    return;
                }

                AggregateRunResult aggregate = ValidationSuiteAggregateRunner.Run(logToConsole: true, suites);
                WriteResults(aggregate, xmlPath);

                // A registry below its expected floor means a suite was dropped from the standard set — a silent
                // coverage regression. In the interactive runner that is only a warning (no exit code); here it must
                // fail the run, else CI stays green while validating fewer suites than it thinks. It fails a subset
                // run too: a broken registry is a real defect regardless of which subset was requested.
                bool registryComplete = ValidationSuiteRegistry.Suites.Count >= ValidationSuiteRegistry.ExpectedSuiteCount;
                if (!registryComplete)
                    Debug.LogError($"<color=red>Validate All (headless): registry has {ValidationSuiteRegistry.Suites.Count} " +
                                   $"suites, expected at least {ValidationSuiteRegistry.ExpectedSuiteCount} — a suite was dropped. Failing.</color>");

                bool ok = registryComplete && aggregate.Success && !aggregate.AnySuiteRanNothing && !aggregate.RanNothing;
                Debug.Log($"Validate All (headless): exiting {(ok ? 0 : 1)} — Success={aggregate.Success}, " +
                          $"AnySuiteRanNothing={aggregate.AnySuiteRanNothing}, RanNothing={aggregate.RanNothing}, RegistryComplete={registryComplete}");
                EditorApplication.Exit(ok ? 0 : 1);
            }
            catch (Exception e)
            {
                Debug.LogError($"<color=red>Validate All (headless) crashed before it could report — exiting 1:</color>\n{e}");
                EditorApplication.Exit(1);
            }
        }

        /// <summary>
        /// Agent/in-editor entry: resolves a comma-separated suite subset by display name and runs it, returning the
        /// result <b>without</b> exiting the editor. Returns null (and logs) when a requested name is unknown.
        /// </summary>
        /// <param name="suitesCsv">Comma-separated suite display names (case-insensitive), or null/empty for all.</param>
        /// <param name="logToConsole">Whether the run logs its per-suite and combined summary.</param>
        /// <returns>The aggregate result, or null when the subset could not be resolved.</returns>
        public static AggregateRunResult RunSelected(string suitesCsv = null, bool logToConsole = true)
        {
            if (!TryResolveSuites(suitesCsv, out IReadOnlyList<RegisteredSuite> suites, out string error))
            {
                Debug.LogError($"<color=red>Validate All: {error}</color>");
                return null;
            }

            return ValidationSuiteAggregateRunner.Run(logToConsole, suites);
        }

        /// <summary>
        /// Resolves a comma-separated suite subset (case-insensitive display names) to registered suites in registry
        /// order. Null/empty selects every suite. An unknown name fails resolution rather than silently running a
        /// smaller set — a typo must not launder a partial run into a green result.
        /// </summary>
        /// <param name="suitesCsv">The requested names, or null/empty for all.</param>
        /// <param name="resolved">The resolved suites in registry order (null on failure).</param>
        /// <param name="error">A human-readable reason on failure (null on success).</param>
        /// <returns>True when the subset resolved; false when a name was unknown or nothing was selected.</returns>
        internal static bool TryResolveSuites(string suitesCsv, out IReadOnlyList<RegisteredSuite> resolved, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(suitesCsv))
            {
                resolved = ValidationSuiteRegistry.Suites;
                return true;
            }

            HashSet<string> known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (RegisteredSuite s in ValidationSuiteRegistry.Suites)
                known.Add(s.DisplayName);

            HashSet<string> chosen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> unknown = new List<string>();
            foreach (string raw in suitesCsv.Split(','))
            {
                string name = raw.Trim();
                if (name.Length == 0)
                    continue;
                if (known.Contains(name))
                    chosen.Add(name);
                else
                    unknown.Add(name);
            }

            if (unknown.Count > 0)
            {
                resolved = null;
                error = $"unknown suite name(s): {string.Join(", ", unknown)}. Known: {string.Join(", ", known)}";
                return false;
            }

            if (chosen.Count == 0)
            {
                resolved = null;
                error = $"no suites selected from '{suitesCsv}'. Known: {string.Join(", ", known)}";
                return false;
            }

            List<RegisteredSuite> ordered = new List<RegisteredSuite>(chosen.Count);
            foreach (RegisteredSuite s in ValidationSuiteRegistry.Suites)
                if (chosen.Contains(s.DisplayName))
                    ordered.Add(s);
            resolved = ordered;
            return true;
        }

        /// <summary>Writes the NUnit3 XML results file, logging (but not failing the run) if the write itself fails.</summary>
        /// <param name="aggregate">The completed aggregate result.</param>
        /// <param name="xmlPath">The destination file path.</param>
        private static void WriteResults(AggregateRunResult aggregate, string xmlPath)
        {
            try
            {
                new NUnitXmlWriter().WriteToFile(aggregate, xmlPath);
                Debug.Log($"Validate All (headless): wrote NUnit XML → {Path.GetFullPath(xmlPath)}");
            }
            catch (Exception e)
            {
                Debug.LogError($"Validate All (headless): failed to write results to '{xmlPath}' (verdict stands via exit code):\n{e}");
            }
        }

        /// <summary>Returns the value following <paramref name="flag"/> in <paramref name="args"/>, or null when absent.</summary>
        /// <param name="args">The command-line arguments.</param>
        /// <param name="flag">The flag to look for (case-insensitive).</param>
        /// <returns>The next argument after the flag, or null.</returns>
        private static string GetArgValue(string[] args, string flag)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return null;
        }
    }
}
