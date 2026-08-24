namespace Helpers
{
    /// <summary>
    /// Decides whether a lighting job being scheduled runs its border edge check. Both schedulers derive
    /// the value once, here, and hand the same result to its two consumers: the job's
    /// <c>PerformEdgeCheck</c> field and LI-2's <see cref="LightingBandDecision.DeriveBandHeight"/> (where
    /// it admits the neighbor→center cross-seam term and can widen the Y-band). One derivation for both
    /// keeps the two from steering different behavior — the drift surface finding F4 named.
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
        /// <remarks>
        /// <paramref name="explicitRequest"/> is why this is shared code rather than a local: the harness
        /// forces edge checks on flag-clear chunks for scenario setup, production never does. Both terms
        /// live in one documented function so that difference stays visible.
        /// </remarks>
        /// <param name="needsEdgeCheck">The chunk's <c>ChunkData.NeedsEdgeCheck</c> flag. Set by the disk-load
        /// arm, the post-stabilization cascade, and neighbor propagation; consumed here and cleared by
        /// <c>ChunkData.OnLightingJobScheduled()</c> once the schedule succeeds.</param>
        /// <param name="explicitRequest">A caller-forced edge check independent of the flag. Always
        /// <c>false</c> in production; the editor harness passes <c>true</c> to stage a border scenario.</param>
        /// <returns>True when the scheduled job runs its edge check.</returns>
        public static bool Evaluate(bool needsEdgeCheck, bool explicitRequest)
        {
            return needsEdgeCheck || explicitRequest;
        }
    }
}
