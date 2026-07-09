using System;
using System.Collections.Generic;

namespace Editor.Validation.Framework
{
    /// <summary>
    /// The combined result of running several validation suites through
    /// <see cref="ValidationSuiteAggregateRunner"/> (the "Validate All" menu item and the headless/CI entry point).
    /// <para>
    /// A plain data carrier over the per-suite <see cref="ValidationRunResult"/> objects: the aggregate menu runner
    /// formats its combined summary from these roll-ups, and the CI entry point derives its exit code from
    /// <see cref="Success"/> / <see cref="AnySuiteRanNothing"/> and emits NUnit-XML from <see cref="Suites"/>.
    /// The roll-up counts are computed from <see cref="Suites"/> so they can never drift out of sync with it.
    /// </para>
    /// </summary>
    public sealed class AggregateRunResult
    {
        /// <summary>Per-suite results in run order. A suite that threw or leaked global state is recorded here as a failed result.</summary>
        public IReadOnlyList<ValidationRunResult> Suites;

        /// <summary>Total wall-clock time for the whole aggregate run, in milliseconds (includes per-suite construction overhead).</summary>
        public double TotalMs;

        /// <summary>Number of suites in the run.</summary>
        public int SuiteCount => Suites?.Count ?? 0;

        /// <summary>Sum of baseline passes across all suites.</summary>
        public int BaselinePassed => Sum(static s => s.BaselinePassed);

        /// <summary>Sum of baseline failures across all suites — the aggregate regression signal.</summary>
        public int BaselineFailed => Sum(static s => s.BaselineFailed);

        /// <summary>Sum of known-bug scenarios still reproducing their documented bug/feature (expected).</summary>
        public int BugsReproduced => Sum(static s => s.BugsReproduced);

        /// <summary>Sum of known-bug scenarios that now pass (fix/implementation candidates — informational).</summary>
        public int BugsFixCandidates => Sum(static s => s.BugsFixCandidates);

        /// <summary>Number of suites that had at least one baseline failure.</summary>
        public int FailedSuiteCount
        {
            get
            {
                int n = 0;
                if (Suites != null)
                    foreach (ValidationRunResult s in Suites)
                        if (!s.Success)
                            n++;
                return n;
            }
        }

        /// <summary>True when no suite had a baseline failure. Empty run is vacuously true (mirrors <see cref="ValidationRunResult.Success"/>); pair with <see cref="RanNothing"/>/<see cref="AnySuiteRanNothing"/> for CI.</summary>
        public bool Success
        {
            get
            {
                if (Suites == null) return true;
                foreach (ValidationRunResult s in Suites)
                    if (!s.Success)
                        return false;
                return true;
            }
        }

        /// <summary>
        /// True when <b>any</b> individual suite registered zero scenarios. The CI gate treats this as a failure
        /// even when <see cref="Success"/> is vacuously true: a suite that silently ran nothing (a dropped
        /// registration, an unbuilt partial) is exactly the "green on nothing" regression to catch.
        /// </summary>
        public bool AnySuiteRanNothing
        {
            get
            {
                if (Suites == null) return false;
                foreach (ValidationRunResult s in Suites)
                    if (s.RanNothing)
                        return true;
                return false;
            }
        }

        /// <summary>True when the aggregate ran no suites at all.</summary>
        public bool RanNothing => Suites == null || Suites.Count == 0;

        /// <summary>Sums a per-suite integer selector across <see cref="Suites"/>, null-safe.</summary>
        private int Sum(Func<ValidationRunResult, int> selector)
        {
            int n = 0;
            if (Suites != null)
                foreach (ValidationRunResult s in Suites)
                    n += selector(s);
            return n;
        }
    }
}
