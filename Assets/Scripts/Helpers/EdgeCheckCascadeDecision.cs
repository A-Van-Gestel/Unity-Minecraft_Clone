namespace Helpers
{
    /// <summary>
    /// Pure decision for what a completed lighting pass does to the post-generation edge-check cascade —
    /// the <c>RemainingEdgeCheckRounds</c> budget, the self <c>NeedsEdgeCheck</c> round, and the 4 cardinal
    /// neighbor triggers that <c>WorldJobManager.MergeCompletedLightingJob</c> fires on stabilization.
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
    /// <see cref="LightingScheduleDecision"/>: the caller performs the side effects (decrement, flag, trigger
    /// neighbors); this is a pure map from the merge's outcome to what the cascade does next.
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
    }
}
