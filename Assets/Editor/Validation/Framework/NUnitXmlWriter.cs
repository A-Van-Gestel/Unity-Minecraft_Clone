using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;

namespace Editor.Validation.Framework
{
    /// <summary>
    /// Emits an <see cref="AggregateRunResult"/> as an NUnit3 <c>test-run</c> XML document — the format CI and
    /// external tooling consume the same way they would Unity Test Framework output (the VS-2 design intent).
    /// <para>Scenario → <c>test-case</c> mapping:
    /// <list type="bullet">
    /// <item>baseline pass / known-bug now-passing → <c>result="Passed"</c> (the fix candidate carries a property).</item>
    /// <item>baseline fail (incl. a thrown or isolation-failed suite) → <c>result="Failed"</c> + a <c>&lt;failure&gt;</c>.</item>
    /// <item>known-bug still reproducing → <c>result="Inconclusive"</c> + a <c>&lt;reason&gt;</c> (an expected non-failure).</item>
    /// </list>
    /// Roll-up counts (<c>total/passed/failed/inconclusive</c>) are computed at the suite and run level so the file
    /// is internally consistent; the framework self-test round-trips this writer to guard against that drifting.</para>
    /// </summary>
    public sealed class NUnitXmlWriter : IValidationResultWriter
    {
        /// <inheritdoc/>
        public string FileExtension => "xml";

        /// <inheritdoc/>
        public void Write(AggregateRunResult result, TextWriter output)
        {
            IReadOnlyList<ValidationRunResult> suites = result?.Suites ?? Array.Empty<ValidationRunResult>();

            int total = 0, passed = 0, failed = 0, inconclusive = 0;
            foreach (ValidationRunResult s in suites)
            {
                total += s.Scenarios?.Count ?? 0;
                passed += s.BaselinePassed + s.BugsFixCandidates;
                failed += s.BaselineFailed;
                inconclusive += s.BugsReproduced;
            }

            DateTime end = DateTime.UtcNow;
            DateTime start = end.AddMilliseconds(-(result?.TotalMs ?? 0.0));

            XmlWriterSettings settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) };
            using XmlWriter w = XmlWriter.Create(output, settings);

            w.WriteStartDocument();
            w.WriteStartElement("test-run");
            w.WriteAttributeString("id", "0");
            w.WriteAttributeString("testcasecount", Int(total));
            w.WriteAttributeString("result", failed > 0 ? "Failed" : "Passed");
            w.WriteAttributeString("total", Int(total));
            w.WriteAttributeString("passed", Int(passed));
            w.WriteAttributeString("failed", Int(failed));
            w.WriteAttributeString("inconclusive", Int(inconclusive));
            w.WriteAttributeString("skipped", "0");
            w.WriteAttributeString("warnings", "0");
            w.WriteAttributeString("start-time", Iso(start));
            w.WriteAttributeString("end-time", Iso(end));
            w.WriteAttributeString("duration", Seconds(result?.TotalMs ?? 0.0));

            int suiteIndex = 0;
            foreach (ValidationRunResult s in suites)
                WriteSuite(w, s, ++suiteIndex);

            w.WriteEndElement();
            w.WriteEndDocument();
        }

        /// <summary>Writes one suite as a <c>test-suite</c> element with its scenarios as nested test-cases.</summary>
        private static void WriteSuite(XmlWriter w, ValidationRunResult s, int suiteIndex)
        {
            int count = s.Scenarios?.Count ?? 0;
            int suitePassed = s.BaselinePassed + s.BugsFixCandidates;

            w.WriteStartElement("test-suite");
            w.WriteAttributeString("type", "TestFixture");
            w.WriteAttributeString("id", Int(suiteIndex));
            w.WriteAttributeString("name", s.SuiteName ?? "");
            w.WriteAttributeString("fullname", s.SuiteName ?? "");
            w.WriteAttributeString("testcasecount", Int(count));
            w.WriteAttributeString("result", s.BaselineFailed > 0 ? "Failed" : "Passed");
            w.WriteAttributeString("total", Int(count));
            w.WriteAttributeString("passed", Int(suitePassed));
            w.WriteAttributeString("failed", Int(s.BaselineFailed));
            w.WriteAttributeString("inconclusive", Int(s.BugsReproduced));
            w.WriteAttributeString("skipped", "0");
            w.WriteAttributeString("warnings", "0");
            w.WriteAttributeString("duration", Seconds(s.TotalMs));

            int caseIndex = 0;
            if (s.Scenarios != null)
                foreach (ScenarioResult sc in s.Scenarios)
                    WriteCase(w, s.SuiteName ?? "", sc, suiteIndex, ++caseIndex);

            w.WriteEndElement();
        }

        /// <summary>Writes one scenario as a <c>test-case</c> element with its result classification and detail child.</summary>
        private static void WriteCase(XmlWriter w, string suiteName, ScenarioResult sc, int suiteIndex, int caseIndex)
        {
            // Classify: known-bug scenarios are expected failures (Inconclusive) until they pass; baselines are pass/fail.
            string result = sc.IsKnownBug ? (sc.Passed ? "Passed" : "Inconclusive") : (sc.Passed ? "Passed" : "Failed");

            w.WriteStartElement("test-case");
            w.WriteAttributeString("id", $"{suiteIndex}-{caseIndex}");
            w.WriteAttributeString("name", sc.Name ?? "");
            w.WriteAttributeString("fullname", $"{suiteName}.{sc.Name}");
            w.WriteAttributeString("result", result);
            if (sc.IsKnownBug && sc.Passed)
                w.WriteAttributeString("label", "FixCandidate");
            w.WriteAttributeString("duration", Seconds(sc.ElapsedMs));
            w.WriteAttributeString("asserts", "0");

            // Child element order per the NUnit3 schema: properties, then failure/reason.
            if (sc.IsKnownBug)
            {
                w.WriteStartElement("properties");
                w.WriteStartElement("property");
                w.WriteAttributeString("name", "known-bug");
                w.WriteAttributeString("value", sc.KnownBugId ?? "");
                w.WriteEndElement();
                w.WriteEndElement();
            }

            if (result == "Failed")
            {
                w.WriteStartElement("failure");
                w.WriteStartElement("message");
                WriteCData(w, sc.Exception != null ? sc.Exception.Message : "Baseline scenario returned false.");
                w.WriteEndElement();
                if (sc.Exception != null)
                {
                    w.WriteStartElement("stack-trace");
                    WriteCData(w, sc.Exception.ToString());
                    w.WriteEndElement();
                }

                w.WriteEndElement();
            }
            else if (result == "Inconclusive")
            {
                w.WriteStartElement("reason");
                w.WriteStartElement("message");
                WriteCData(w, $"Reproduces {sc.KnownBugId} (expected until fixed/implemented).");
                w.WriteEndElement();
                w.WriteEndElement();
            }

            w.WriteEndElement();
        }

        /// <summary>
        /// Writes <paramref name="text"/> as CDATA, split across sections at every <c>]]&gt;</c> so no single section
        /// contains the terminator — <see cref="XmlWriter.WriteCData"/> throws on an embedded <c>]]&gt;</c>, and
        /// scenario exception text (message + stack trace) is arbitrary, developer-uncontrolled content.
        /// </summary>
        /// <param name="w">The XML writer.</param>
        /// <param name="text">The (possibly null) text to emit as CDATA.</param>
        private static void WriteCData(XmlWriter w, string text)
        {
            text ??= "";
            int start = 0;
            int idx;
            while ((idx = text.IndexOf("]]>", start, StringComparison.Ordinal)) >= 0)
            {
                // Include the "]]" in this section; the ">" begins the next, so neither section holds the full "]]>".
                w.WriteCData(text.Substring(start, idx - start + 2));
                start = idx + 2;
            }
            w.WriteCData(text.Substring(start));
        }

        /// <summary>Formats an integer culture-invariantly.</summary>
        private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);

        /// <summary>Formats a millisecond duration as culture-invariant seconds (NUnit's <c>duration</c> unit).</summary>
        private static string Seconds(double ms) => (ms / 1000.0).ToString("0.000", CultureInfo.InvariantCulture);

        /// <summary>Formats a timestamp as a round-trippable ISO-8601 string.</summary>
        private static string Iso(DateTime utc) => utc.ToString("o", CultureInfo.InvariantCulture);
    }
}
