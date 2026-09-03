using System.Collections.Generic;
using System.Text;
using Data;
using Editor.Validation.Lighting.Framework;
using Helpers;
using Scenario = Editor.Validation.Framework.Scenario;

namespace Editor.Validation.Lighting
{
    /// <summary>
    /// Baseline for LP-6's <b>lazy strict-gate evaluation</b>: the scan evaluates only the neighbor gate the
    /// chunk's arm actually reads, and that must not change which arm any chunk takes.
    /// <para>
    /// <b>Why this baseline is load-bearing rather than a nicety.</b> LP-6 is behavior-preserving by
    /// intent, so the universal gate cannot witness it — a correct lazy path and a broken one both leave
    /// every existing baseline green, exactly as LP-5 measured when sabotaging a call-site assignment left
    /// 573/573 passing. The only thing that can catch a mis-derived gate need is a direct comparison of the
    /// two evaluation strategies over the whole input space, which is what B121 does.
    /// </para>
    /// <para>
    /// <b>The check.</b> All 64 combinations of the six decision inputs are enumerated. For each, the
    /// pre-evaluated overload's action is compared against the lazy overload's, driven by a recording gate
    /// provider. Two properties are asserted per combination: the actions match, and neither gate is
    /// queried more than once. Gate-value <i>agreement</i> is deliberately not asserted — the provider is
    /// constructed from the same two values the eager call is handed, so agreement holds by construction
    /// and asserting it would witness nothing.
    /// </para>
    /// <para>
    /// <b>B122</b> pins the laziness itself: the gate-need rule derived from the arm precedence. Without it
    /// B121 would still pass if the lazy path simply queried both gates every time, which is the null
    /// refactor.
    /// </para>
    /// <para>
    /// <b>Prove-red, measured 2026-08-24 — and it refuted the prediction made when these were written.</b>
    /// Dropping the <c>DataReady</c> term from the regular arm reds <b>2 of 114</b>: B122 (naming the six
    /// input combinations that stopped querying the gate) and B67, which depends on that gate to keep a
    /// chunk parked. <b>B121 stayed green.</b> That is structural, not luck: both overloads reach one shared
    /// core, so a mutation inside the core moves the lazy and pre-evaluated answers together and they still
    /// agree. B121 therefore witnesses that the two strategies have not been re-implemented apart — it is a
    /// guard against re-duplicating the arm rule, not a mutation detector. <b>B122 is the one with teeth
    /// against the gate-need rule</b>, and the pair should be read that way rather than as two of a kind.
    /// </para>
    /// Self-registered via the <see cref="AddLazyGateEquivalenceBaselineScenarios"/> hook.
    /// </summary>
    public static partial class LightingValidationSuite
    {
        /// <summary>Registers the LP-6 lazy-gate baselines (called from <c>AddBaselineScenarios</c>).</summary>
        /// <param name="scenarios">The scenario list to append to.</param>
        static partial void AddLazyGateEquivalenceBaselineScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario(
                "B121: lazy and pre-evaluated gate evaluation agree on the scan arm for all 64 input combinations (LP-6)",
                Baseline_LazyGateEquivalence));

            scenarios.Add(new Scenario(
                "B122: the lazy path queries a neighbor gate only when the arm precedence can read it (LP-6)",
                Baseline_LazyGateNecessity));
        }

        /// <summary>
        /// Records which gates were asked for, and answers with values fixed per combination. A class so it
        /// reaches the decision as an interface reference without boxing, matching how production passes
        /// <c>World</c>.
        /// </summary>
        private sealed class RecordingGates : INeighborGates
        {
            private readonly bool _dataReady;
            private readonly bool _readyAndLit;

            /// <summary>Number of <see cref="INeighborGates.DataReady"/> queries since construction.</summary>
            public int DataReadyQueries;

            /// <summary>Number of <see cref="INeighborGates.ReadyAndLit"/> queries since construction.</summary>
            public int ReadyAndLitQueries;

            /// <summary>Fixes the values this provider will answer with.</summary>
            /// <param name="dataReady">The value to answer for the data-ready gate.</param>
            /// <param name="readyAndLit">The value to answer for the strict gate.</param>
            public RecordingGates(bool dataReady, bool readyAndLit)
            {
                _dataReady = dataReady;
                _readyAndLit = readyAndLit;
            }

            /// <summary><see cref="INeighborGates.DataReady"/>: records the query and answers.</summary>
            /// <param name="coord">Ignored — this provider is not world-backed.</param>
            /// <returns>The fixed data-ready value.</returns>
            bool INeighborGates.DataReady(ChunkCoord coord)
            {
                DataReadyQueries++;
                return _dataReady;
            }

            /// <summary><see cref="INeighborGates.ReadyAndLit"/>: records the query and answers.</summary>
            /// <param name="coord">Ignored — this provider is not world-backed.</param>
            /// <returns>The fixed strict-gate value.</returns>
            bool INeighborGates.ReadyAndLit(ChunkCoord coord)
            {
                ReadyAndLitQueries++;
                return _readyAndLit;
            }
        }

        /// <summary>
        /// B121: enumerates all 64 input combinations and asserts the lazy overload returns the identical
        /// <c>ScanAction</c> as the pre-evaluated one, and never queries a gate more than once.
        /// </summary>
        /// <returns>True when all 64 combinations agree.</returns>
        private static bool Baseline_LazyGateEquivalence()
        {
            bool passed = true;
            StringBuilder mismatches = new StringBuilder();

            for (int bits = 0; bits < 64; bits++)
            {
                bool jobInFlight = (bits & 1) != 0;
                bool needsInitial = (bits & 2) != 0;
                bool needsEdge = (bits & 4) != 0;
                bool hasChanges = (bits & 8) != 0;
                bool dataReady = (bits & 16) != 0;
                bool readyAndLit = (bits & 32) != 0;

                LightingScanDecision.ScanAction eager = LightingScanDecision.EvaluateReadyChunk(
                    jobInFlight, needsInitial, needsEdge, hasChanges, dataReady, readyAndLit);

                RecordingGates gates = new RecordingGates(dataReady, readyAndLit);
                LightingScanDecision.ScanAction lazy = LightingScanDecision.EvaluateReadyChunk(
                    jobInFlight, needsInitial, needsEdge, hasChanges, gates, default);

                if (eager != lazy)
                {
                    passed = false;
                    mismatches.Append($"\n  inFlight={jobInFlight} initial={needsInitial} edge={needsEdge} " +
                                      $"changes={hasChanges} dataReady={dataReady} readyAndLit={readyAndLit}: " +
                                      $"eager={eager} lazy={lazy}");
                }

                // A gate queried twice would mean the decision re-walks 8 neighbors for a value it already
                // holds — the opposite of this phase's point, and invisible to the action comparison.
                if (gates.DataReadyQueries <= 1 && gates.ReadyAndLitQueries <= 1) continue;

                passed = false;
                mismatches.Append($"\n  bits={bits}: gate queried more than once " +
                                  $"(dataReady x{gates.DataReadyQueries}, readyAndLit x{gates.ReadyAndLitQueries})");
            }

            return LightingAssert.IsTrue(passed,
                "B121: lazy evaluation agrees with pre-evaluated on every input combination",
                $"Divergences:{mismatches}");
        }

        /// <summary>
        /// B122: asserts the lazy path queries each gate exactly when the arm precedence can read it —
        /// <c>ReadyAndLit</c> only for a not-in-flight, non-initial chunk with an edge check pending, and
        /// <c>DataReady</c> only for a not-in-flight chunk that wants initial or regular lighting.
        /// </summary>
        /// <remarks>
        /// This is the assertion that fails if the "lazy" path is not actually lazy. B121 alone would stay
        /// green against an implementation that evaluated both gates unconditionally, since that is exactly
        /// what the pre-evaluated overload does.
        /// </remarks>
        /// <returns>True when every combination queries only the reachable gates.</returns>
        private static bool Baseline_LazyGateNecessity()
        {
            bool passed = true;
            StringBuilder wrong = new StringBuilder();
            int lazyDataReadySkips = 0;
            int lazyReadyAndLitSkips = 0;

            for (int bits = 0; bits < 64; bits++)
            {
                bool jobInFlight = (bits & 1) != 0;
                bool needsInitial = (bits & 2) != 0;
                bool needsEdge = (bits & 4) != 0;
                bool hasChanges = (bits & 8) != 0;
                bool dataReady = (bits & 16) != 0;
                bool readyAndLit = (bits & 32) != 0;

                RecordingGates gates = new RecordingGates(dataReady, readyAndLit);
                LightingScanDecision.EvaluateReadyChunk(
                    jobInFlight, needsInitial, needsEdge, hasChanges, gates, default);

                // The strict gate is reachable only from the edge arm, which sits after the in-flight park
                // and the initial-lighting arm.
                bool readyAndLitReachable = !jobInFlight && !needsInitial && needsEdge;

                // The data-ready gate is read by the initial arm, and by the regular arm — the latter only
                // when the edge arm did not already schedule.
                bool edgeArmTook = readyAndLitReachable && readyAndLit;
                bool dataReadyReachable = !jobInFlight && (needsInitial || (hasChanges && !edgeArmTook));

                // Counted from what the provider was ACTUALLY asked, never from the oracle above: a guard
                // derived from `bits` is a property of this loop's arithmetic and would hold against an
                // implementation that queried both gates every time — the null refactor B122 exists to catch.
                if (gates.ReadyAndLitQueries == 0) lazyReadyAndLitSkips++;
                if (gates.DataReadyQueries == 0) lazyDataReadySkips++;

                if (gates.ReadyAndLitQueries != (readyAndLitReachable ? 1 : 0))
                {
                    passed = false;
                    wrong.Append($"\n  bits={bits}: ReadyAndLit queried x{gates.ReadyAndLitQueries}, " +
                                 $"expected {(readyAndLitReachable ? 1 : 0)}");
                }

                if (gates.DataReadyQueries == (dataReadyReachable ? 1 : 0)) continue;

                passed = false;
                wrong.Append($"\n  bits={bits}: DataReady queried x{gates.DataReadyQueries}, " +
                             $"expected {(dataReadyReachable ? 1 : 0)}");
            }

            // A run where nothing is ever skipped would mean the laziness is inert; name it rather than
            // letting the per-combination checks pass vacuously.
            passed &= LightingAssert.IsTrue(lazyDataReadySkips > 0 && lazyReadyAndLitSkips > 0,
                "B122: the lazy path actually skips gates (the phase would be a no-op otherwise)",
                $"Expected skips on both gates; got dataReady={lazyDataReadySkips}, readyAndLit={lazyReadyAndLitSkips}");

            return LightingAssert.IsTrue(passed,
                "B122: each gate is queried exactly when the arm precedence can read it",
                $"Divergences:{wrong}");
        }
    }
}
