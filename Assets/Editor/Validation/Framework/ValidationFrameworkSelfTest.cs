using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using UnityEditor;
using UnityEngine;

namespace Editor.Validation.Framework
{
    /// <summary>
    /// Self-tests the VS-2 framework layer that has no domain suite of its own: the <see cref="NUnitXmlWriter"/>
    /// (round-tripped in-memory against a synthetic result) and the aggregate runner's isolation guard (proven to
    /// trip on a leak via a mock guard, so the guarantee is verified rather than assumed). Registered in
    /// <see cref="ValidationSuiteRegistry"/> so "Validate All" re-checks the reporting/guard layer on every run;
    /// it touches no process-global state, so it is itself isolation-clean.
    /// </summary>
    public static class ValidationFrameworkSelfTest
    {
        /// <summary>Menu entry — runs the framework self-tests and logs the categorized summary.</summary>
        [MenuItem("Minecraft Clone/Dev/Validate Validation Framework")]
        public static void RunAll() => Execute();

        /// <summary>Builds and runs the framework self-test scenarios (all baselines).</summary>
        /// <param name="logToConsole">When false, runs silently and only returns the result (for headless/CI use).</param>
        /// <param name="showProgress">When false, suppresses this suite's own progress bar (the aggregate runner drives one).</param>
        /// <returns>The categorized, timed result of the run.</returns>
        public static ValidationRunResult Execute(bool logToConsole = true, bool showProgress = true)
        {
            List<Scenario> scenarios = new List<Scenario>
            {
                new Scenario("NUnit XML: well-formed + run-level rollups", XmlRunRollups),
                new Scenario("NUnit XML: per-suite rollups + suite count", XmlSuiteRollups),
                new Scenario("NUnit XML: failed baseline carries <failure>", XmlFailureDetail),
                new Scenario("NUnit XML: known-bug repro is Inconclusive + reason + property", XmlInconclusiveDetail),
                new Scenario("NUnit XML: known-bug now-passing is Passed + FixCandidate", XmlFixCandidate),
                new Scenario("NUnit XML: round-trip case counts preserved", XmlRoundTripCounts),
                new Scenario("Isolation guard: trips on leak, restores, marks failed", GuardTripsOnLeak),
                new Scenario("Isolation guard: no false-positive when clean", GuardCleanNoFalsePositive),
            };
            return ValidationSuiteRunner.Execute("Validation Framework", scenarios, KnownBugChannel.Bug, logToConsole, showProgress);
        }

        // --- Synthetic fixture: two suites covering every branch the writer must map. ---

        /// <summary>
        /// Builds a synthetic aggregate result: suite "Alpha" (2 baseline pass, 1 baseline fail-with-exception,
        /// 1 known-bug repro, 1 known-bug now-passing) and suite "Beta" (empty / ran-nothing). Rollups: 5 cases,
        /// 3 passed, 1 failed, 1 inconclusive.
        /// </summary>
        /// <returns>The synthetic aggregate result.</returns>
        private static AggregateRunResult BuildSynthetic()
        {
            List<ScenarioResult> alpha = new List<ScenarioResult>
            {
                new ScenarioResult { Name = "A pass1", Passed = true, ElapsedMs = 1.0 },
                new ScenarioResult { Name = "A pass2", Passed = true, ElapsedMs = 2.0 },
                new ScenarioResult { Name = "A fail", Passed = false, ElapsedMs = 3.0, Exception = new Exception("boom") },
                new ScenarioResult { Name = "A repro", KnownBugId = "Bug 99", Passed = false, ElapsedMs = 4.0 },
                new ScenarioResult { Name = "A fixcand", KnownBugId = "Bug 42", Passed = true, ElapsedMs = 5.0 },
            };

            ValidationRunResult a = new ValidationRunResult
            {
                SuiteName = "Alpha", Scenarios = alpha,
                BaselinePassed = 2, BaselineFailed = 1, BugsReproduced = 1, BugsFixCandidates = 1, TotalMs = 15.0,
            };
            ValidationRunResult b = new ValidationRunResult
            {
                SuiteName = "Beta", Scenarios = new List<ScenarioResult>(), TotalMs = 0.0,
            };

            return new AggregateRunResult { Suites = new List<ValidationRunResult> { a, b }, TotalMs = 15.0 };
        }

        /// <summary>Serializes the synthetic result to an in-memory NUnit3 XML string (no disk I/O).</summary>
        private static string WriteXml(AggregateRunResult result)
        {
            using StringWriter sw = new StringWriter();
            new NUnitXmlWriter().Write(result, sw);
            return sw.ToString();
        }

        /// <summary>Parses XML text into a document (throwing on malformed XML, which fails the scenario).</summary>
        private static XmlDocument Parse(string xml)
        {
            XmlDocument doc = new XmlDocument();
            doc.LoadXml(xml);
            return doc;
        }

        /// <summary>Reads an attribute off a node, or null.</summary>
        private static string Attr(XmlNode node, string name) => node?.Attributes?[name]?.Value;

        /// <summary>Logs and returns false when <paramref name="condition"/> fails; returns true otherwise.</summary>
        private static bool Check(bool condition, string message)
        {
            if (!condition)
                Debug.LogError($"[Validation Framework] {message}");
            return condition;
        }

        // --- NUnit XML scenarios ---

        private static bool XmlRunRollups()
        {
            XmlNode run = Parse(WriteXml(BuildSynthetic())).DocumentElement;
            return Check(run != null && run.Name == "test-run", "root is not <test-run>")
                   && Check(Attr(run, "testcasecount") == "5", "run testcasecount != 5")
                   && Check(Attr(run, "passed") == "3", "run passed != 3")
                   && Check(Attr(run, "failed") == "1", "run failed != 1")
                   && Check(Attr(run, "inconclusive") == "1", "run inconclusive != 1")
                   && Check(Attr(run, "result") == "Failed", "run result != Failed");
        }

        private static bool XmlSuiteRollups()
        {
            XmlDocument doc = Parse(WriteXml(BuildSynthetic()));
            XmlNodeList suites = doc.SelectNodes("//test-suite");
            XmlNode alpha = doc.SelectSingleNode("//test-suite[@name='Alpha']");
            XmlNode beta = doc.SelectSingleNode("//test-suite[@name='Beta']");
            return Check(suites != null && suites.Count == 2, "expected 2 test-suite elements")
                   && Check(Attr(alpha, "total") == "5" && Attr(alpha, "passed") == "3"
                                                        && Attr(alpha, "failed") == "1" && Attr(alpha, "inconclusive") == "1"
                                                        && Attr(alpha, "result") == "Failed", "Alpha rollups wrong")
                   && Check(Attr(beta, "total") == "0" && Attr(beta, "result") == "Passed", "Beta (empty) rollups wrong");
        }

        private static bool XmlFailureDetail()
        {
            XmlNode fail = Parse(WriteXml(BuildSynthetic())).SelectSingleNode("//test-case[@name='A fail']");
            XmlNode msg = fail?.SelectSingleNode("failure/message");
            return Check(Attr(fail, "result") == "Failed", "A fail result != Failed")
                   && Check(msg != null && msg.InnerText.Contains("boom"), "A fail missing <failure><message> with exception text");
        }

        private static bool XmlInconclusiveDetail()
        {
            XmlNode repro = Parse(WriteXml(BuildSynthetic())).SelectSingleNode("//test-case[@name='A repro']");
            XmlNode prop = repro?.SelectSingleNode("properties/property[@name='known-bug']");
            return Check(Attr(repro, "result") == "Inconclusive", "A repro result != Inconclusive")
                   && Check(repro?.SelectSingleNode("reason/message") != null, "A repro missing <reason>")
                   && Check(Attr(prop, "value") == "Bug 99", "A repro missing known-bug property Bug 99");
        }

        private static bool XmlFixCandidate()
        {
            XmlNode fc = Parse(WriteXml(BuildSynthetic())).SelectSingleNode("//test-case[@name='A fixcand']");
            return Check(Attr(fc, "result") == "Passed", "A fixcand result != Passed")
                   && Check(Attr(fc, "label") == "FixCandidate", "A fixcand missing FixCandidate label");
        }

        private static bool XmlRoundTripCounts()
        {
            XmlNodeList cases = Parse(WriteXml(BuildSynthetic())).SelectNodes("//test-case");
            int passed = 0, failed = 0, inconclusive = 0;
            foreach (XmlNode c in cases)
            {
                switch (Attr(c, "result"))
                {
                    case "Passed": passed++; break;
                    case "Failed": failed++; break;
                    case "Inconclusive": inconclusive++; break;
                }
            }

            return Check(cases.Count == 5, "expected 5 test-case elements")
                   && Check(passed == 3 && failed == 1 && inconclusive == 1, "round-trip case-result tally wrong");
        }

        // --- Isolation-guard scenarios (mock guard, no real World touched) ---

        private static bool GuardTripsOnLeak()
        {
            MockGuard guard = new MockGuard();
            RegisteredSuite leaking = new RegisteredSuite("Leaky", (log, prog) =>
            {
                guard.State = "leaked"; // simulate a suite that mutates global state and forgets to restore it
                return OnePassResult("Leaky");
            });

            ValidationRunResult r = ValidationSuiteAggregateRunner.RunOneIsolated(leaking, logToConsole: false, guard);

            return Check(guard.State == "clean", "guard did not force-restore leaked state")
                   && Check(r.BaselineFailed == 1, "leak was not marked as a baseline failure")
                   && Check(!r.Success, "leaked suite still reports Success")
                   && Check(r.BaselinePassed == 1, "original suite pass count was lost");
        }

        private static bool GuardCleanNoFalsePositive()
        {
            MockGuard guard = new MockGuard();
            RegisteredSuite clean = new RegisteredSuite("Clean", (log, prog) => OnePassResult("Clean"));

            ValidationRunResult r = ValidationSuiteAggregateRunner.RunOneIsolated(clean, logToConsole: false, guard);

            return Check(guard.State == "clean", "guard mutated state on a clean run")
                   && Check(r.BaselineFailed == 0 && r.Success, "guard false-tripped a clean suite")
                   && Check(r.BaselinePassed == 1, "clean suite pass count changed");
        }

        /// <summary>A one-scenario, all-pass suite result for the guard scenarios.</summary>
        private static ValidationRunResult OnePassResult(string suiteName) => new ValidationRunResult
        {
            SuiteName = suiteName,
            Scenarios = new List<ScenarioResult> { new ScenarioResult { Name = "ok", Passed = true } },
            BaselinePassed = 1,
        };

        /// <summary>An in-memory stand-in for a piece of process-global state, so the guard's trip path is provable without a real World.</summary>
        private sealed class MockGuard : ValidationSuiteAggregateRunner.IIsolationGuard
        {
            public string State = "clean";

            public string StateName => "MockGlobal";

            public object Capture() => State;

            public bool RestoreIfLeaked(object snapshot)
            {
                string snap = (string)snapshot;
                if (State == snap)
                    return false;
                State = snap;
                return true;
            }
        }
    }
}
