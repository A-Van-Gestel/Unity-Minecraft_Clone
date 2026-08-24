namespace Helpers
{
    /// <summary>
    /// Decides whether a lighting job being scheduled runs its border edge check.
    /// <para>
    /// The result has <b>two</b> consumers and both must receive the same value: the job's
    /// <c>PerformEdgeCheck</c> field, and LI-2's <see cref="LightingBandDecision.DeriveBandHeight"/>, where
    /// it admits the neighbor→center cross-seam term and can widen the Y-band. Deriving it once per
    /// schedule is what keeps the two from steering different behavior — the drift surface finding F4
    /// named. Re-reading <c>ChunkData.NeedsEdgeCheck</c> at either consumer re-opens it.
    /// </para>
    /// <para>
    /// Distinct from <see cref="EdgeCheckCascadeDecision"/>, which owns the opposite end of the lifecycle:
    /// whether a <i>completed</i> pass re-arms the flag. This type only reads it.
    /// </para>
    /// <para>
    /// <b>Coverage limit.</b> This closes the double-read, not the witness gap: production's
    /// <c>ScheduleLightingUpdate</c> has three callers, all in <c>World.cs</c>, and no validation harness
    /// reaches it — so the two lines that *consume* this decision in production stay unobserved by every
    /// baseline. Only a harness that drives the production scheduler can witness them (design doc LP-8).
    /// </para>
    /// <para>See Documentation/Design/LIGHTING_PIPELINE_STATE_REFACTOR.md §7 (LP-5, finding F4).</para>
    /// </summary>
    public static class ScheduledEdgeCheckDecision
    {
        /// <summary>
        /// Evaluates whether the job about to be scheduled performs its border edge check.
        /// </summary>
        /// <param name="needsEdgeCheck">The chunk's <c>ChunkData.NeedsEdgeCheck</c> flag, cleared by
        /// <c>ChunkData.OnLightingJobScheduled()</c> once the schedule succeeds.</param>
        /// <param name="explicitRequest">Forces the edge check independently of the flag, for a caller
        /// staging a border scenario on a flag-clear chunk. Pass <c>false</c> to ride the flag alone.</param>
        /// <returns>True when the scheduled job runs its edge check.</returns>
        public static bool Evaluate(bool needsEdgeCheck, bool explicitRequest)
        {
            return needsEdgeCheck || explicitRequest;
        }
    }
}
