using System.Collections.Generic;
using Data;
using Editor.Validation.Lighting.Framework;
using Helpers;
using Jobs.BurstData;
using Unity.Collections;
using UnityEngine;
using Scenario = Editor.Validation.Framework.Scenario;

namespace Editor.Validation.Lighting
{
    /// <summary>
    /// Baseline scenarios for P9-2's outcome-conditional edge-check cascade (design doc
    /// <c>CHUNK_PIPELINE_SCHEDULE_QUOTA_THROUGHPUT.md</c> §6, Option B1). Two layers are guarded, both
    /// below the level the world harness can reach:
    /// <list type="bullet">
    /// <item><see cref="EdgeCheckCascadeDecision.ShouldRearm"/> — the pure predicate, including its
    /// flag-off reduction to the legacy stability-only rule.</item>
    /// <item><c>ChunkData.ApplyJobLightMap</c>'s change signal — the input that predicate is only as good
    /// as, exercised across the uniform-sky compaction boundary where a naive
    /// <c>section.LightData</c> comparison silently reads the wrong pre-merge value.</item>
    /// </list>
    /// Self-registered via the <see cref="AddP92CascadeBaselineScenarios"/> hook.
    /// </summary>
    public static partial class LightingValidationSuite
    {

        /// <summary>Registers the P9-2 cascade baselines (called from <c>AddBaselineScenarios</c>).</summary>
        /// <param name="scenarios">The scenario list to append to.</param>
        static partial void AddP92CascadeBaselineScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario(
                "B97: EdgeCheckCascadeDecision — flag OFF reproduces the legacy stability-only rule for every input (P9-2 rollback guard)",
                Baseline_CascadeDecisionFlagOffIsLegacy));
            scenarios.Add(new Scenario(
                "B98: EdgeCheckCascadeDecision — flag ON re-arms only on effect (changed light or pending work), and never past an exhausted budget (P9-2)",
                Baseline_CascadeDecisionFlagOnRequiresEffect));
            scenarios.Add(new Scenario(
                "B99: ChunkData.ApplyJobLightMap reports change vs no-change, including across the uniform-sky compaction boundary (P9-2 cascade signal)",
                Baseline_ApplyJobLightMapReportsChange));
            scenarios.Add(new Scenario(
                "B100: the cascade signal is per-MERGE, not per-border-state — a main-thread write between two merges is invisible to it (P9-2 known limitation)",
                Baseline_CascadeSignalIsPerMergeNotPerBorderState));
            scenarios.Add(new Scenario(
                "B119: Evaluate's outcome maps to the right cascade EFFECTS across the whole input matrix — the three outcomes stay three (P9-2 F11 seam, LP-4)",
                Baseline_CascadeOutcomeAppliesCorrectEffects));
        }

        /// <summary>
        /// B97: with the P9-2 flag off, <see cref="EdgeCheckCascadeDecision.Evaluate"/> must depend on the
        /// round budget ALONE and must never yield <c>SpendOnly</c> — exactly the legacy
        /// <c>RemainingEdgeCheckRounds &gt; 0</c> test the merge ran before this decision existed. This is
        /// the rollback leg's guard: a regression that leaks the effect condition into the flag-off path
        /// would silently change shipped lighting behavior while every capture still reported the flag as
        /// disabled.
        /// </summary>
        private static bool Baseline_CascadeDecisionFlagOffIsLegacy()
        {
            bool passed = true;

            foreach (int rounds in new[] { -1, 0, 1, 2 })
            foreach (bool changed in new[] { false, true })
            foreach (bool pending in new[] { false, true })
            {
                EdgeCheckCascadeDecision.CascadeOutcome actual =
                    EdgeCheckCascadeDecision.Evaluate(false, rounds, changed, pending);
                EdgeCheckCascadeDecision.CascadeOutcome expected = rounds > 0
                    ? EdgeCheckCascadeDecision.CascadeOutcome.SpendAndRearm
                    : EdgeCheckCascadeDecision.CascadeOutcome.None;

                passed &= LightingAssert.IsTrue(actual == expected,
                    $"B97: flag OFF, rounds={rounds.ToString()} changed={changed} pending={pending} → legacy budget-only result",
                    $"expected {expected.ToString()}, got {actual.ToString()}");
            }

            return passed;
        }

        /// <summary>
        /// B98: with the flag on, the cascade PROPAGATES only when the completed pass actually moved light
        /// (<c>lightChanged</c>) or left the chunk flagged for another pass (<c>hasPendingLightWork</c> —
        /// the deferred cross-chunk drain and the pull-back verification write through that flag).
        /// <para>
        /// The load-bearing assertion is that a no-effect pass returns <c>SpendOnly</c> and not
        /// <c>None</c>: the round must still be spent. Only the flags buy lighting schedules, so declining
        /// the round saves nothing — while letting a converged chunk hoard budget for its whole residency
        /// would break the premise <c>ChunkData.ModifyVoxel</c>'s Bug-05 top-up rests on (post-generation
        /// the rounds are already spent) and arm cascades on ordinary edits legacy never armed.
        /// </para>
        /// An exhausted budget still returns <c>None</c>, so the flag can never manufacture rounds the
        /// legacy rule would not have allowed.
        /// </summary>
        private static bool Baseline_CascadeDecisionFlagOnRequiresEffect()
        {
            bool passed = true;

            passed &= LightingAssert.IsTrue(
                EdgeCheckCascadeDecision.Evaluate(true, 2, false, false)
                == EdgeCheckCascadeDecision.CascadeOutcome.SpendOnly,
                "B98: a no-effect pass with budget left SPENDS its round but does not propagate (the redundant schedules P9-2 removes)");
            passed &= LightingAssert.IsTrue(
                EdgeCheckCascadeDecision.Evaluate(true, 2, true, false)
                == EdgeCheckCascadeDecision.CascadeOutcome.SpendAndRearm,
                "B98: a pass that changed light re-arms");
            passed &= LightingAssert.IsTrue(
                EdgeCheckCascadeDecision.Evaluate(true, 2, false, true)
                == EdgeCheckCascadeDecision.CascadeOutcome.SpendAndRearm,
                "B98: a no-effect merge whose post-merge writers left pending work still re-arms");
            passed &= LightingAssert.IsTrue(
                EdgeCheckCascadeDecision.Evaluate(true, 0, true, true)
                == EdgeCheckCascadeDecision.CascadeOutcome.None,
                "B98: an exhausted budget refuses regardless of effect");

            return passed;
        }

        /// <summary>
        /// B99: the signal the whole decision rests on. Re-applying an identical light map must report NO
        /// change; a single differing voxel must report one. The third case is the trap the naive
        /// implementation falls into: a section compacted to a uniform sky level reads as that level via
        /// <c>GetLightData</c> whether or not the section object survived compaction, so comparing against
        /// <c>ChunkSection.LightData</c> would compare against a stale or pooled buffer and report
        /// "unchanged" for a merge that genuinely changed the chunk — the false-negative that would drop a
        /// real cascade.
        /// </summary>
        private static bool Baseline_ApplyJobLightMapReportsChange()
        {
            bool passed = true;

            const int length = VoxelData.ChunkWidth * VoxelData.ChunkHeight * VoxelData.ChunkWidth;
            ChunkData chunk = new ChunkData(Vector2Int.zero);

            NativeArray<uint> voxels = new NativeArray<uint>(length, Allocator.Temp);
            NativeArray<ushort> light = new NativeArray<ushort>(length, Allocator.Temp);
            try
            {
                ushort sky8 = LightBitMapping.PackLightData(8, 0, 0, 0);
                for (int i = 0; i < length; i++) light[i] = sky8;

                // First merge onto an empty chunk: every voxel goes 0 → 8, so this must report a change.
                passed &= LightingAssert.IsTrue(chunk.ApplyJobLightMap(voxels, light, null),
                    "B99: the first merge of a lit map onto an unlit chunk reports a change");

                // Identical re-merge. Every section is now compacted to uniform sky 8 (no blocks, so the
                // section objects were returned to the pool) — the compaction case.
                passed &= LightingAssert.IsTrue(!chunk.ApplyJobLightMap(voxels, light, null),
                    "B99: re-merging the identical map reports NO change (the redundant pass P9-2 detects)");

                // One voxel differs, in a compacted section: must be seen despite there being no
                // ChunkSection.LightData to compare against.
                light[0] = LightBitMapping.PackLightData(7, 0, 0, 0);
                passed &= LightingAssert.IsTrue(chunk.ApplyJobLightMap(voxels, light, null),
                    "B99: a single differing voxel in a COMPACTED section reports a change");

                // And settling again reports no change.
                passed &= LightingAssert.IsTrue(!chunk.ApplyJobLightMap(voxels, light, null),
                    "B99: the merge after that change settles back to NO change");

                // Light vanishing entirely (sections drop out) is a change, and its repeat is not.
                for (int i = 0; i < length; i++) light[i] = 0;
                passed &= LightingAssert.IsTrue(chunk.ApplyJobLightMap(voxels, light, null),
                    "B99: light vanishing from every section reports a change");
                passed &= LightingAssert.IsTrue(!chunk.ApplyJobLightMap(voxels, light, null),
                    "B99: re-merging the now-dark map reports NO change");
            }
            finally
            {
                voxels.Dispose();
                light.Dispose();
            }

            return passed;
        }

        /// <summary>
        /// B100: a <b>characterization</b> baseline, not a safety proof. It pins the exact semantics of
        /// P9-2's cascade signal so the limitation is discoverable rather than folklore:
        /// <c>ApplyJobLightMap</c> answers <i>"did THIS merge change light?"</i>, not <i>"has this chunk's
        /// border changed since its neighbors last read it?"</i> A main-thread write that lands between two
        /// merges — the shape <c>WorldJobManager.ApplyCrossChunkLightMod</c> produces, which writes through
        /// <c>SetLightData</c> and queues a wake-up node — is already present in the next job's snapshot, so
        /// the merge that reproduces it reports NO change and the cascade declines a propagation the legacy
        /// rule would have made.
        /// <para>
        /// <b>Whether that is reachable end-to-end is an OPEN question, deliberately not asserted here.</b>
        /// The argument that it is not: the same wake-up node makes the job re-spread from that voxel, and
        /// <c>PropagateLight</c> emits a cross-chunk mod toward the far neighbor under the same condition
        /// that neighbor's own add-only edge check would have gained light — which makes the job unstable,
        /// sets <c>HasLightChangesToProcess</c>, and re-arms the cascade anyway. If that symmetry is ever
        /// broken, this baseline is where the consequence becomes visible.
        /// </para>
        /// <para>
        /// <b>Prove-red status — honestly, non-exclusive.</b> It reddens when the write/read path breaks
        /// (verified: a <c>SetLightData</c> that skips <c>PromoteCompactSection</c> fails its second and
        /// third assertions) — but that mutation also reddens B1, so it does not isolate this guard. No
        /// cheap mutation does: the only change that flips B100 alone is widening the signal to notice
        /// external writes, i.e. implementing the fix this limitation describes. That is precisely the
        /// intended trigger — a characterization baseline's prove-red is by nature the future change it
        /// exists to catch.
        /// </para>
        /// </summary>
        private static bool Baseline_CascadeSignalIsPerMergeNotPerBorderState()
        {
            bool passed = true;

            const int length = VoxelData.ChunkWidth * VoxelData.ChunkHeight * VoxelData.ChunkWidth;
            ChunkData chunk = new ChunkData(Vector2Int.zero);

            NativeArray<uint> voxels = new NativeArray<uint>(length, Allocator.Temp);
            NativeArray<ushort> light = new NativeArray<ushort>(length, Allocator.Temp);
            try
            {
                ushort sky8 = LightBitMapping.PackLightData(8, 0, 0, 0);
                for (int i = 0; i < length; i++) light[i] = sky8;

                // Settle the chunk, then confirm it is quiescent — the state a converged chunk is in when
                // a neighbor's merge routes a cross-chunk mod into it.
                chunk.ApplyJobLightMap(voxels, light, null);
                passed &= LightingAssert.IsTrue(!chunk.ApplyJobLightMap(voxels, light, null),
                    "B100: the chunk is quiescent before the simulated cross-chunk write");

                // The main-thread write ApplyCrossChunkLightMod performs: a brighter value on a border
                // voxel (local x = 0), landing while no job of this chunk's own is in flight.
                const int borderY = 4;
                ushort sky12 = LightBitMapping.PackLightData(12, 0, 0, 0);
                chunk.SetLightData(0, borderY, 0, sky12);
                passed &= LightingAssert.IsTrue(
                    chunk.GetLightData(0, borderY, 0) == sky12,
                    "B100: the simulated cross-chunk write landed on the border voxel");

                // The chunk's next job snapshots that value and reproduces it. Build the map the job would
                // return: the settled field plus the write already in it.
                int borderIndex = ChunkMath.GetFlattenedIndexInChunk(0, borderY, 0);
                light[borderIndex] = sky12;

                // THE POINT: the border differs from what neighbors last read, yet the merge reports no
                // change, because nothing changed across THIS merge.
                passed &= LightingAssert.IsTrue(!chunk.ApplyJobLightMap(voxels, light, null),
                    "B100: the merge reproducing an already-applied cross-chunk write reports NO change — the signal is per-merge, so this pass would not propagate");

                // And the value is genuinely live, i.e. the "no change" is not because the write was lost.
                passed &= LightingAssert.IsTrue(
                    chunk.GetLightData(0, borderY, 0) == sky12,
                    "B100: the border voxel still holds the written value after the merge (the write was preserved, not reverted)");
            }
            finally
            {
                voxels.Dispose();
                light.Dispose();
            }

            return passed;
        }

        /// <summary>
        /// B119: guards the seam between the pure decision and its effects. Before LP-4 those effects were
        /// three loose lines inside <c>WorldJobManager.MergeCompletedLightingJob</c> — a method reachable
        /// only from <c>World.Update</c>, so NO validation harness could execute it. A measured prove-red
        /// confirmed the hole: forcing <c>SpendOnly</c> to re-arm left all 110 lighting, 7 chunk-pipeline
        /// and 22 backpressure baselines GREEN, because an over-eager cascade converges *better* and every
        /// end-state oracle still passes. Pairing the effects with the decision (<c>Apply</c>) makes the
        /// mapping reachable; this sweeps the full input matrix over it.
        /// <para>
        /// <b>Still not covered:</b> that the merge passes the outcome it actually computed. That is one
        /// line in an unreachable method; only an in-game session or a harness that drives production's
        /// merge can witness it.
        /// </para>
        /// </summary>
        private static bool Baseline_CascadeOutcomeAppliesCorrectEffects()
        {
            List<string> failures = new List<string>();

            foreach (bool flagEnabled in new[] { false, true })
            foreach (int rounds in new[] { 0, 1, 2 })
            foreach (bool changed in new[] { false, true })
            foreach (bool pending in new[] { false, true })
            {
                EdgeCheckCascadeDecision.CascadeOutcome outcome =
                    EdgeCheckCascadeDecision.Evaluate(flagEnabled, rounds, changed, pending);

                LightingWork startWork = pending ? LightingWork.LightChanges : LightingWork.None;
                ChunkData subject = MakeChunkWithWork(startWork);

                // Drive the counter to the case under test (Reset leaves it at the default budget).
                while (subject.RemainingEdgeCheckRounds > rounds) subject.SpendEdgeCheckRound(rearm: false);
                subject.ClearAllLightingWork();
                if (pending) subject.FlagLightWork();

                int roundsBefore = subject.RemainingEdgeCheckRounds;
                EdgeCheckCascadeDecision.Apply(outcome, subject);

                bool spent = outcome != EdgeCheckCascadeDecision.CascadeOutcome.None;
                int wantRounds = spent ? roundsBefore - 1 : roundsBefore;
                if (subject.RemainingEdgeCheckRounds != wantRounds)
                    failures.Add($"{outcome} (flag={flagEnabled} rounds={rounds} changed={changed} pending={pending}): "
                                 + $"rounds {roundsBefore} -> {subject.RemainingEdgeCheckRounds} (expected {wantRounds})");

                LightingWork wantWork =
                    outcome == EdgeCheckCascadeDecision.CascadeOutcome.SpendAndRearm
                        ? startWork | LightingWork.EdgeCheck | LightingWork.LightChanges
                        : startWork;
                if (subject.Work != wantWork)
                    failures.Add($"{outcome} (flag={flagEnabled} rounds={rounds} changed={changed} pending={pending}): "
                                 + $"work {startWork} -> {subject.Work} (expected {wantWork})");
            }

            return LightingAssert.IsTrue(failures.Count == 0,
                "B119: every cascade outcome applies its census effects",
                failures.Count == 0 ? null : string.Join("\n", failures));
        }
    }
}
