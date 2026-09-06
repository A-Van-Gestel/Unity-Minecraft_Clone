using Data;

namespace Helpers
{
    /// <summary>
    /// Owns what a completed lighting pass does to the post-generation edge-check cascade: <see cref="Evaluate"/>
    /// decides the outcome and <see cref="Apply"/> performs the budget spend and the self re-arm.
    /// The caller keeps only the 4 cardinal neighbor triggers and the telemetry counter, which need the
    /// chunk coord.
    /// <para>
    /// Re-arming requires the pass to have had an <b>effect</b>, not merely to be stable:
    /// <see cref="Jobs.NeighborhoodLightingJob"/> reports stability as "no work left pending", which a pass
    /// that wrote <i>nothing</i> also satisfies, and P9-2 measured that as the dominant case (design doc §6,
    /// Option B1).
    /// </para>
    /// <para>
    /// <b>Spending the round and re-arming are separate outcomes on purpose.</b> The quota units P9-2 exists
    /// to remove are bought by the <i>flags</i> — the self edge check and the 4 neighbor triggers each cost a
    /// lighting schedule — never by the counter. Declining to spend the round as well would leave chunks
    /// holding budget for their whole residency, which breaks the premise <c>ChunkData.ModifyVoxel</c>'s
    /// Bug-05 top-up is built on ("after generation both edge-check rounds are already spent") and would arm
    /// cascades on ordinary post-generation edits. A no-effect pass therefore spends its round and simply
    /// does not propagate.
    /// </para>
    /// <para>
    /// Same shared-guard pattern as <see cref="LightingScanDecision"/> and
    /// <see cref="LightingScheduleDecision"/>, with one deliberate difference: the decision's <i>effects</i>
    /// live here too, so the outcome-to-effect mapping is reachable from a harness and can be pinned in a
    /// single call rather than only through a merge that runs on the main-thread update path.
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
            /// for the self round or the neighbors to reconcile against (P9-2).</summary>
            SpendOnly,

            /// <summary>Spend a round, flag the self edge check, and trigger the 4 cardinal neighbors —
            /// what an effective pass does.</summary>
            SpendAndRearm,
        }

        /// <summary>
        /// Decides what the completed pass does to the cascade.
        /// </summary>
        /// <param name="remainingRounds">The chunk's <c>RemainingEdgeCheckRounds</c> budget.</param>
        /// <param name="lightChanged">Whether the merge changed any voxel's effective light value
        /// (<c>ChunkData.ApplyJobLightMap</c>'s return).</param>
        /// <param name="hasPendingLightWork">Whether the chunk is left flagged for another lighting pass.
        /// Covers the post-merge writers — the deferred cross-chunk drain and the pull-back verification —
        /// which set the flag whenever they change this chunk, so a merge whose light moved only there
        /// still re-arms.</param>
        /// <returns>The outcome the caller should apply.</returns>
        public static CascadeOutcome Evaluate(
            int remainingRounds,
            bool lightChanged,
            bool hasPendingLightWork)
        {
            if (remainingRounds <= 0) return CascadeOutcome.None;

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
            // cascades on ordinary edits.
            chunkData.SpendEdgeCheckRound(outcome == CascadeOutcome.SpendAndRearm);
        }
    }
}
