using System;
using System.Collections.Generic;

namespace Helpers
{
    /// <summary>
    /// Per-job operations for one lighting-completion pass, supplied by whoever drives
    /// <see cref="LightingCompletionPass"/>. Production (<c>WorldJobManager.ProcessLightingJobs</c>) and the
    /// editor validation <c>LightingFrameSimulator</c> each implement it, so the two share the exact
    /// multi-job iteration, fault-isolation, and release-inside / remove-after ordering — the completion-pass
    /// half of the shared-guard pattern started by <see cref="LightingScheduleDecision"/> /
    /// <see cref="LightingScanDecision"/>. The skeleton owns the control flow (the <c>try/catch/finally</c>
    /// and loop structure); the driver owns the side effects.
    /// See Documentation/Design/LIGHTING_ASYNC_BUG_VALIDATION_ROADMAP.md §10 (HF-4 #2).
    /// </summary>
    /// <typeparam name="TKey">The per-job key (production: <c>ChunkCoord</c>; sim: <c>Vector2Int</c> chunk coord).</typeparam>
    public interface ILightingCompletionDriver<in TKey>
    {
        /// <summary>Is this candidate's job finished and eligible to process this pass? (production:
        /// <c>Handle.IsCompleted</c>; sim: the age/completion predicate). A <c>false</c> leaves the job
        /// enrolled nowhere — it is carried over to a later pass.</summary>
        bool IsComplete(TKey key);

        /// <summary>Stage 1 — complete the job (production: <c>Handle.Complete()</c>). May throw; the skeleton
        /// isolates it via <see cref="OnCompleteFault"/> and carries the job over (not enrolled, containers
        /// left owned).</summary>
        void CompleteJob(TKey key);

        /// <summary>Stage-1 fault handler: record the fault. The job stays enrolled in the registry for a
        /// later retry (the skeleton skips its merge/release this pass).</summary>
        void OnCompleteFault(TKey key, Exception e);

        /// <summary>Stage 2 — merge the completed job into its chunk (production: request the chunk +
        /// <c>MergeCompletedLightingJob</c>). May throw; the skeleton isolates it via
        /// <see cref="OnMergeFault"/> but still runs <see cref="ReleaseJob"/> and enrolls the job.</summary>
        void MergeJob(TKey key);

        /// <summary>Stage-2 fault handler: record the fault and leave the chunk re-schedulable (a merge that
        /// threw is in an unknown state — a corrective pass must run rather than silently dropping it).</summary>
        void OnMergeFault(TKey key, Exception e);

        /// <summary>Unconditional per-job cleanup (production: clear <c>IsAwaitingMainThreadProcess</c> +
        /// release the job's containers). Runs in the merge <c>finally</c> even on a stage-2 fault, so a
        /// faulted job never lingers in the registry with disposed containers (the fidelity-B7 cascade).</summary>
        void ReleaseJob(TKey key);

        /// <summary>After the whole merge loop: remove the job from the registry and promote its neighborhood
        /// (production: <c>LightingJobs.Remove</c> + <c>PromoteLightWorkNeighborhood</c>). Strictly after every
        /// merge, so a completion promoting a neighbor sees the fully-merged pass (MT-2).</summary>
        void RemoveAndPromote(TKey key);
    }

    /// <summary>
    /// The production lighting-completion loop structure, extracted so both <c>WorldJobManager</c> and the
    /// editor frame simulator drive one pass skeleton (HF-4 #2). The two structural guarantees it owns — and
    /// the harness could not replay before — are:
    /// <list type="bullet">
    /// <item><b>Fault isolation:</b> a stage-1 fault carries the job over; a stage-2 fault still releases the
    /// job and continues the pass (no aborted pass, no stranded-container cascade).</item>
    /// <item><b>Release-inside / remove-after ordering:</b> each job's merge + release happen inside the loop
    /// (so a later job's merge sees earlier jobs already enrolled), while registry removal + promotion happen
    /// only after every job has merged.</item>
    /// </list>
    /// The caller performs any production-specific work (dropped-update batching, mesh rebuilds) between the
    /// two calls; the sim runs them back-to-back.
    /// </summary>
    public static class LightingCompletionPass
    {
        /// <summary>
        /// Runs the fault-isolated merge loop over <paramref name="candidates"/> in the given order, clearing
        /// then repopulating <paramref name="enrolled"/> with the jobs that completed this pass. Mirrors the
        /// per-job body of <c>WorldJobManager.ProcessLightingJobs</c> exactly: <c>IsComplete</c> gate →
        /// <c>try CompleteJob / catch OnCompleteFault + skip</c> → <c>try MergeJob / catch OnMergeFault /
        /// finally ReleaseJob + enroll</c>.
        /// </summary>
        /// <typeparam name="TKey">The per-job key type.</typeparam>
        /// <param name="candidates">The jobs to consider this pass, in processing order (production snapshots
        /// <c>LightingJobs.Keys</c>; the sim applies its completion-order strategy first).</param>
        /// <param name="driver">The side-effect provider.</param>
        /// <param name="enrolled">Reused buffer; cleared here, then filled with every job that reached the
        /// merge <c>finally</c> (completed or stage-2-faulted). Also read by the driver's merge (production's
        /// <c>_completedLightJobs.Contains</c> cross-chunk check), so enrollment happens progressively.</param>
        public static void RunMergeLoop<TKey>(
            IReadOnlyList<TKey> candidates,
            ILightingCompletionDriver<TKey> driver,
            List<TKey> enrolled)
        {
            enrolled.Clear();

            foreach (TKey candidate in candidates)
            {
                if (!driver.IsComplete(candidate)) continue;

                // Stage 1 (fault isolation): if completion throws, the job may still own its containers — do
                // NOT release or enroll; leave it in the registry for the next pass to retry under isolation.
                try
                {
                    driver.CompleteJob(candidate);
                }
                catch (Exception e)
                {
                    driver.OnCompleteFault(candidate, e);
                    continue;
                }

                // Stage 2 (fault isolation): one merge throwing must not abort the pass or skip cleanup —
                // ReleaseJob + enrollment run unconditionally so a faulted job is never stranded with disposed
                // containers (the ObjectDisposedException cascade, fidelity B7).
                try
                {
                    driver.MergeJob(candidate);
                }
                catch (Exception e)
                {
                    driver.OnMergeFault(candidate, e);
                }
                finally
                {
                    driver.ReleaseJob(candidate);
                    enrolled.Add(candidate);
                }
            }
        }

        /// <summary>
        /// Removes + promotes every job the pass enrolled, strictly after the whole merge loop (and after any
        /// production-specific between-loop work the caller ran). Mirrors <c>ProcessLightingJobs</c>'
        /// end-of-method <c>foreach (_completedLightJobs) { LightingJobs.Remove; PromoteLightWorkNeighborhood }</c>.
        /// </summary>
        /// <typeparam name="TKey">The per-job key type.</typeparam>
        /// <param name="enrolled">The jobs enrolled by <see cref="RunMergeLoop{TKey}"/> this pass.</param>
        /// <param name="driver">The side-effect provider.</param>
        public static void RunRemoveAndPromote<TKey>(
            List<TKey> enrolled,
            ILightingCompletionDriver<TKey> driver)
        {
            foreach (TKey key in enrolled)
            {
                driver.RemoveAndPromote(key);
            }
        }
    }
}
