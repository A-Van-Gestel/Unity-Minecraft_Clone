using Data;

namespace Helpers
{
    /// <summary>
    /// Owns what a completed lighting pass does to the post-generation edge-check cascade: <see cref="Evaluate"/>
    /// decides the outcome and <see cref="Apply"/> performs the budget spend and the self re-arm.
    /// <c>WorldJobManager.MergeCompletedLightingJob</c> keeps only the 4 cardinal neighbor triggers and the
    /// telemetry counter, which need the chunk coord.
    /// <para>
    /// The legacy rule arms on <b>stability</b>, which <see cref="Jobs.NeighborhoodLightingJob"/> reports as
    /// "no work left pending" — a condition a pass that wrote <i>nothing</i> also satisfies. P9-2 measured
    /// that case as the dominant one (design doc §6, Option B1), so this decision adds the
    /// <b>effect</b> condition behind a rollback flag.
    /// </para>
    /// <para>
    /// <b>Spending the round and re-arming are separate outcomes on purpose.</b> The quota units P9-2 exists
    /// to remove are bought by the <i>flags</i> — the self edge check and the 4 neighbor triggers each cost a
    /// lighting schedule — never by the counter. Declining to spend the round as well would leave chunks
    /// holding budget for their whole residency, which breaks the premise <c>ChunkData.ModifyVoxel</c>'s
    /// Bug-05 top-up is built on ("after generation both edge-check rounds are already spent") and would arm
    /// cascades on ordinary post-generation edits that legacy never armed. So a no-effect pass spends its
    /// round exactly as legacy does, and simply does not propagate.
    /// </para>
    /// <para>
    /// Same shared-guard pattern as <see cref="LightingScanDecision"/> and
    /// <see cref="LightingScheduleDecision"/>, with one deliberate difference: the decision's <i>effects</i>
    /// live here too. They previously sat in the merge as loose lines that no validation harness could reach
    /// (production's merge runs only from <c>World.Update</c>), so a mis-application went unwitnessed —
    /// baseline B119 now guards the outcome-to-effect mapping in one call.
    /// </para>
    /// </summary>
    public static class EdgeCheckCascadeDecision
    {
        /// <summary>What a completed, stable lighting pass does to the edge-check cascade.</summary>
        public enum CascadeOutcome : byte
        {
            /// <summary>No budget left — touch nothing (the legacy <c>RemainingEdgeCheckRounds &gt; 0</c> refusal).</summary>
            None,

            /// <summary>Spend a round, but do not propagate: the pass changed nothing, so there is nothing
            /// for the self round or the neighbors to reconcile against (P9-2, flag-gated).</summary>
            SpendOnly,

            /// <summary>Spend a round, flag the self edge check, and trigger the 4 cardinal neighbors —
            /// the legacy behavior, and what an effective pass still does with the flag on.</summary>
            SpendAndRearm,
        }

        /// <summary>
        /// Decides what the completed pass does to the cascade.
        /// </summary>
        /// <param name="convergentCascadeEnabled">The P9-2 rollback flag
        /// (<c>Settings.enableConvergentEdgeCheckCascade</c>). When false this never returns
        /// <see cref="CascadeOutcome.SpendOnly"/>, reducing exactly to the legacy budget-only rule.</param>
        /// <param name="remainingRounds">The chunk's <c>RemainingEdgeCheckRounds</c> budget.</param>
        /// <param name="lightChanged">Whether the merge changed any voxel's effective light value
        /// (<c>ChunkData.ApplyJobLightMap</c>'s return).</param>
        /// <param name="hasPendingLightWork">Whether the chunk is left flagged for another lighting pass.
        /// Covers the post-merge writers — the deferred cross-chunk drain and the pull-back verification —
        /// which set the flag whenever they change this chunk, so a merge whose light moved only there
        /// still re-arms.</param>
        /// <returns>The outcome the caller should apply.</returns>
        public static CascadeOutcome Evaluate(
            bool convergentCascadeEnabled,
            int remainingRounds,
            bool lightChanged,
            bool hasPendingLightWork)
        {
            if (remainingRounds <= 0) return CascadeOutcome.None;
            if (!convergentCascadeEnabled) return CascadeOutcome.SpendAndRearm;

            return lightChanged || hasPendingLightWork
                ? CascadeOutcome.SpendAndRearm
                : CascadeOutcome.SpendOnly;
        }

        /// <summary>
        /// Applies an outcome of <see cref="Evaluate"/> to the chunk. Paired with the decision on purpose:
        /// the two used to meet in the merge as three loose lines that no validation harness could reach
        /// (production's <c>MergeCompletedLightingJob</c> is callable only from <c>World.Update</c>), so a
        /// mis-application flattened the three outcomes back to two with every baseline still green.
        /// Keeping them together makes the mapping testable in one call.
        /// </summary>
        /// <param name="outcome">The outcome returned by <see cref="Evaluate"/>.</param>
        /// <param name="chunkData">The chunk whose cascade state the outcome applies to.</param>
        public static void Apply(CascadeOutcome outcome, ChunkData chunkData)
        {
            if (outcome == CascadeOutcome.None) return;

            // The round is spent whether or not the pass propagates. Only the re-arm flags buy lighting
            // schedules; the counter buys none — and letting a converged chunk hoard budget would break the
            // premise ModifyVoxel's Bug-05 top-up rests on (post-generation the rounds are spent) and arm
            // cascades on ordinary edits that legacy never armed.
            chunkData.SpendEdgeCheckRound(outcome == CascadeOutcome.SpendAndRearm);
        }
    }
}
