using System;
using System.Collections.Generic;
using Data;
using Editor.Validation.Lighting.Framework;
using UnityEngine;
using Scenario = Editor.Validation.Framework.Scenario;

namespace Editor.Validation.Lighting
{
    /// <summary>
    /// Baseline scenarios for LP-4's <see cref="LightingWork"/> transition API — the mutation layer that
    /// replaced ~30 scattered raw flag writes. Every scenario is oracle-free (the B34/B47 style): it drives
    /// <see cref="ChunkData"/> directly and asserts bits, the rounds counter, and callback fire counts.
    /// <para>
    /// <b>What this family does and does not guard.</b> It pins each transition method to the bit mask the
    /// design doc's §2.3 census assigns it, so a method wired to the wrong mask — or one that clears a bit
    /// it should leave alone — reds here. It cannot tell you the census itself is right: these assertions
    /// and the methods share that origin. The census-vs-production question is what the world-level
    /// baselines and the in-game session answer.
    /// </para>
    /// Self-registered via the <see cref="AddLightingWorkTransitionBaselineScenarios"/> hook.
    /// </summary>
    public static partial class LightingValidationSuite
    {
        private const int DEFAULT_EDGE_CHECK_ROUNDS = 2;

        /// <summary>Registers the LP-4 transition-API baselines (called from <c>AddBaselineScenarios</c>).</summary>
        /// <param name="scenarios">The scenario list to append to.</param>
        static partial void AddLightingWorkTransitionBaselineScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario(
                "B115: every LightingWork transition method maps its census bit mask, from all 8 starting work sets (LP-4)",
                Baseline_TransitionBitEffects));
            scenarios.Add(new Scenario(
                "B116: SpendEdgeCheckRound spends the round on both outcomes and re-arms EdgeCheck+LightChanges together only when asked (LP-4 / P9-2 split)",
                Baseline_SpendEdgeCheckRoundOutcomes));
            scenarios.Add(new Scenario(
                "B117: OnLightWorkFlagged fires once per 0-to-1 bit transition — never on a clear, a no-op, or the second bit of a combined arm (LP-4 callback delta)",
                Baseline_CallbackFiresOncePerRisingTransition));
            scenarios.Add(new Scenario(
                "B118: the COMBINED arming transitions set EdgeCheck and LightChanges together, never one alone (LP-4)",
                Baseline_EdgeCheckNeverArmedAlone));
        }

        /// <summary>
        /// B115: drives every transition method from each of the 8 reachable I/C/E combinations and asserts
        /// the resulting work set equals the census-specified mask arithmetic. Catches a method wired to the
        /// wrong bit, and — the historical defect class — one that clears more than its share.
        /// </summary>
        private static bool Baseline_TransitionBitEffects()
        {
            using (new SuppressedWorkCallback())
            {
                return TransitionBitEffectsBody();
            }
        }

        private static bool TransitionBitEffectsBody()
        {
            const LightingWork initial = LightingWork.InitialLighting;
            const LightingWork changes = LightingWork.LightChanges;
            const LightingWork edge = LightingWork.EdgeCheck;

            (string Name, Action<ChunkData> Apply, Func<LightingWork, LightingWork> Expected)[] transitions =
            {
                ("FlagInitialLighting", d => d.FlagInitialLighting(), w => w | initial),
                ("FlagLightWork", d => d.FlagLightWork(), w => w | changes),
                ("FlagEdgeCheck", d => d.FlagEdgeCheck(), w => w | edge),
                ("FlagNeighborEdgeCheck", d => d.FlagNeighborEdgeCheck(), w => w | edge | changes),
                ("ClearInitialLighting", d => d.ClearInitialLighting(), w => w & ~initial),
                ("OnLightingJobScheduled", d => d.OnLightingJobScheduled(), w => w & ~(changes | edge)),
                ("ClearAllLightingWork", d => d.ClearAllLightingWork(), _ => LightingWork.None),
            };

            List<string> failures = new List<string>();

            foreach ((string name, Action<ChunkData> apply, Func<LightingWork, LightingWork> expected) in transitions)
            {
                for (int mask = 0; mask < 8; mask++)
                {
                    LightingWork start = (LightingWork)mask;
                    ChunkData subject = MakeChunkWithWork(start);

                    apply(subject);

                    LightingWork want = expected(start);
                    if (subject.Work != want)
                        failures.Add($"{name}: {start} -> {subject.Work} (expected {want})");
                }
            }

            return LightingAssert.IsTrue(failures.Count == 0,
                "B115: transition methods map their census bit masks",
                failures.Count == 0 ? null : string.Join("\n", failures));
        }

        /// <summary>
        /// B116: the P9-2 cascade has three outcomes and the mutation layer must keep them three. Asserts
        /// the round is spent on the no-propagate outcome as well as the re-arm one (a converged chunk must
        /// not hoard budget — <c>ModifyVoxel</c>'s Bug-05 top-up rests on rounds being spent
        /// post-generation), and that only the re-arm form touches the flags.
        /// </summary>
        private static bool Baseline_SpendEdgeCheckRoundOutcomes()
        {
            using (new SuppressedWorkCallback())
            {
                return SpendEdgeCheckRoundOutcomesBody();
            }
        }

        private static bool SpendEdgeCheckRoundOutcomesBody()
        {
            bool passed = true;

            ChunkData spendOnly = MakeChunkWithWork(LightingWork.None);
            int before = spendOnly.RemainingEdgeCheckRounds;
            spendOnly.SpendEdgeCheckRound(rearm: false);

            passed &= LightingAssert.IsTrue(spendOnly.RemainingEdgeCheckRounds == before - 1,
                "B116: a spent-only round still decrements the budget",
                $"rounds {before} -> {spendOnly.RemainingEdgeCheckRounds}");
            passed &= LightingAssert.IsTrue(spendOnly.Work == LightingWork.None,
                "B116: a spent-only round leaves the work set untouched",
                $"work = {spendOnly.Work}");

            ChunkData rearm = MakeChunkWithWork(LightingWork.None);
            before = rearm.RemainingEdgeCheckRounds;
            rearm.SpendEdgeCheckRound(rearm: true);

            passed &= LightingAssert.IsTrue(rearm.RemainingEdgeCheckRounds == before - 1,
                "B116: a re-arming round decrements the budget",
                $"rounds {before} -> {rearm.RemainingEdgeCheckRounds}");
            passed &= LightingAssert.IsTrue(
                rearm.Work == (LightingWork.EdgeCheck | LightingWork.LightChanges),
                "B116: a re-arming round arms EdgeCheck and LightChanges together",
                $"work = {rearm.Work}");

            // The Bug-05 re-grant is a max, not a set: it must never lower a larger surviving budget.
            ChunkData regrant = MakeChunkWithWork(LightingWork.None);
            regrant.RegrantBorderEditEdgeRound();
            passed &= LightingAssert.IsTrue(regrant.RemainingEdgeCheckRounds == DEFAULT_EDGE_CHECK_ROUNDS,
                "B116: the border re-grant does not lower a larger surviving budget (Bug 05)",
                $"rounds = {regrant.RemainingEdgeCheckRounds} (expected {DEFAULT_EDGE_CHECK_ROUNDS})");

            ChunkData exhausted = MakeChunkWithWork(LightingWork.None);
            exhausted.SpendEdgeCheckRound(rearm: false);
            exhausted.SpendEdgeCheckRound(rearm: false);
            exhausted.RegrantBorderEditEdgeRound();
            passed &= LightingAssert.IsTrue(exhausted.RemainingEdgeCheckRounds == 1,
                "B116: the border re-grant tops an exhausted budget back up to one round (Bug 05)",
                $"rounds = {exhausted.RemainingEdgeCheckRounds} (expected 1)");

            return passed;
        }

        /// <summary>
        /// B117: pins the scheduler-facing contract of the LP-4 write funnel. The callback drives
        /// <c>LightWorkScheduler</c>'s staging queue, so an extra fire is wasted work and a missing one is a
        /// stalled chunk. Also pins the one accepted behavioral delta of the refactor: a combined arm fires
        /// ONCE where two property writes used to fire twice (staging dedupes, so this is observationally
        /// equivalent — but it is a real change, asserted here rather than reasoned about).
        /// </summary>
        private static bool Baseline_CallbackFiresOncePerRisingTransition()
        {
            Action<Vector2Int> saved = ChunkData.OnLightWorkFlagged;
            bool passed = true;

            try
            {
                FireCounter fires = new FireCounter();
                ChunkData subject = MakeChunkWithWork(LightingWork.None);
                ChunkData.OnLightWorkFlagged = _ => fires.Count++;

                fires.Reset();
                subject.FlagLightWork();
                passed &= LightingAssert.IsTrue(fires.Count == 1,
                    "B117: a rising bit fires once", $"fires = {fires.Count}");

                fires.Reset();
                subject.FlagLightWork();
                passed &= LightingAssert.IsTrue(fires.Count == 0,
                    "B117: re-flagging an already-set bit does not fire", $"fires = {fires.Count}");

                fires.Reset();
                subject.OnLightingJobScheduled();
                passed &= LightingAssert.IsTrue(fires.Count == 0,
                    "B117: clearing bits does not fire", $"fires = {fires.Count}");

                fires.Reset();
                subject.ClearAllLightingWork();
                passed &= LightingAssert.IsTrue(fires.Count == 0,
                    "B117: a no-op write does not fire", $"fires = {fires.Count}");

                // The accepted delta: both bits rise in one call, one notification.
                fires.Reset();
                subject.FlagNeighborEdgeCheck();
                passed &= LightingAssert.IsTrue(fires.Count == 1,
                    "B117: a combined two-bit arm fires ONCE, not twice", $"fires = {fires.Count}");

                ChunkData cascade = MakeChunkWithWork(LightingWork.None);
                fires.Reset();
                cascade.SpendEdgeCheckRound(rearm: true);
                passed &= LightingAssert.IsTrue(fires.Count == 1,
                    "B117: the cascade re-arm fires ONCE for its two bits", $"fires = {fires.Count}");

                ChunkData spendOnly = MakeChunkWithWork(LightingWork.None);
                fires.Reset();
                spendOnly.SpendEdgeCheckRound(rearm: false);
                passed &= LightingAssert.IsTrue(fires.Count == 0,
                    "B117: a spent-but-not-re-armed round never notifies the scheduler", $"fires = {fires.Count}");

                // A partial rise still notifies: EdgeCheck already set, LightChanges rising.
                ChunkData partial = MakeChunkWithWork(LightingWork.EdgeCheck);
                fires.Reset();
                partial.FlagNeighborEdgeCheck();
                passed &= LightingAssert.IsTrue(fires.Count == 1,
                    "B117: a combined arm still fires when only one of its bits is rising", $"fires = {fires.Count}");

                return passed;
            }
            finally
            {
                ChunkData.OnLightWorkFlagged = saved;
            }
        }

        /// <summary>
        /// B118: the transitions that arm an edge check <i>as part of a cascade or neighbor trigger</i> must
        /// set <c>LightChanges</c> in the same call, so the chunk can reschedule under either scan arm — the
        /// strict edge arm or the relaxed regular one. Splitting them would narrow the chunk to the strict
        /// arm alone and change post-merge reconciliation timing, which is what the two separate writes this
        /// API replaced never did.
        /// <para>
        /// <b>Deliberately NOT swept: <c>FlagEdgeCheck</c>.</b> Arming an edge check alone is a legal,
        /// production-used state — <c>World</c>'s disk-load-stable arm sets one, and the design doc's
        /// reachable-combination table lists <c>0 0 1</c> ("waits on the strict edge gate"). Such a chunk is
        /// not stranded: <see cref="Helpers.LightingScanDecision"/>'s <c>ScheduleEdge</c> arm takes it on
        /// <c>needsEdgeCheck &amp;&amp; neighborsReadyAndLit</c> alone and pre-sets the companion before
        /// scheduling. B115 covers <c>FlagEdgeCheck</c>'s own bit effect.
        /// </para>
        /// </summary>
        private static bool Baseline_EdgeCheckNeverArmedAlone()
        {
            using (new SuppressedWorkCallback())
            {
                return EdgeCheckNeverArmedAloneBody();
            }
        }

        private static bool EdgeCheckNeverArmedAloneBody()
        {
            (string Name, Action<ChunkData> Apply)[] armingTransitions =
            {
                ("FlagNeighborEdgeCheck", d => d.FlagNeighborEdgeCheck()),
                ("SpendEdgeCheckRound(rearm: true)", d => d.SpendEdgeCheckRound(rearm: true)),
            };

            List<string> failures = new List<string>();

            foreach ((string name, Action<ChunkData> apply) in armingTransitions)
            {
                for (int mask = 0; mask < 8; mask++)
                {
                    LightingWork start = (LightingWork)mask;

                    // A start state that already carries a lone edge check is not this transition's doing.
                    bool startArmedAlone = (start & LightingWork.EdgeCheck) != 0
                                           && (start & LightingWork.LightChanges) == 0;
                    if (startArmedAlone) continue;

                    ChunkData subject = MakeChunkWithWork(start);
                    apply(subject);

                    bool armedAlone = (subject.Work & LightingWork.EdgeCheck) != 0
                                      && (subject.Work & LightingWork.LightChanges) == 0;
                    if (armedAlone)
                        failures.Add($"{name}: {start} -> {subject.Work} (set EdgeCheck without its LightChanges companion)");
                }
            }

            return LightingAssert.IsTrue(failures.Count == 0,
                "B118: the combined arming transitions never set EdgeCheck alone",
                failures.Count == 0 ? null : string.Join("\n", failures));
        }

        /// <summary>
        /// Detaches the static <c>ChunkData.OnLightWorkFlagged</c> for a scenario's lifetime and restores it
        /// on dispose. These baselines drive bare <c>ChunkData</c> instances outside any test world, so
        /// without this their transitions would reach a live <c>LightWorkScheduler</c> if the suite is run
        /// during play mode — injecting a synthetic chunk coord into the running world's staging queue, and
        /// exposing the scenario to exceptions thrown by a stale subscriber. Same save/null/restore guard
        /// <c>LightingAssert</c>, <c>LightingTestWorld</c> and <c>ChunkPipelineFixture</c> use.
        /// </summary>
        private sealed class SuppressedWorkCallback : IDisposable
        {
            private readonly Action<Vector2Int> _saved;

            /// <summary>Detaches the callback, remembering the previous subscriber.</summary>
            public SuppressedWorkCallback()
            {
                _saved = ChunkData.OnLightWorkFlagged;
                ChunkData.OnLightWorkFlagged = null;
            }

            /// <summary>Restores the previous subscriber.</summary>
            public void Dispose() => ChunkData.OnLightWorkFlagged = _saved;
        }

        /// <summary>
        /// Mutable fire-count holder for B117. The sink lambda captures the holder, never a local int, so
        /// resetting between assertions does not reassign a captured variable.
        /// </summary>
        private sealed class FireCounter
        {
            /// <summary>Number of <c>OnLightWorkFlagged</c> invocations since the last reset.</summary>
            public int Count;

            /// <summary>Zeroes the counter between assertions.</summary>
            public void Reset() => Count = 0;
        }

        /// <summary>
        /// Builds a chunk whose work set is <paramref name="work"/>, using only the transition API (the bits
        /// have no setter). Those arming calls fire <c>OnLightWorkFlagged</c> like any other, so a caller
        /// counting fires must zero its counter AFTER this returns, not before.
        /// </summary>
        /// <param name="work">The starting work set.</param>
        /// <returns>A fresh chunk carrying exactly that work set.</returns>
        private static ChunkData MakeChunkWithWork(LightingWork work)
        {
            ChunkData data = new ChunkData(new Vector2Int(64, 64));

            if ((work & LightingWork.InitialLighting) != 0) data.FlagInitialLighting();
            if ((work & LightingWork.LightChanges) != 0) data.FlagLightWork();
            if ((work & LightingWork.EdgeCheck) != 0) data.FlagEdgeCheck();

            return data;
        }
    }
}
