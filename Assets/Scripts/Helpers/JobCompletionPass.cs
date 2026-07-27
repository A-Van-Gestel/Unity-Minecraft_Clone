using System;
using System.Collections.Generic;

namespace Helpers
{
    /// <summary>
    /// Per-job operations for one job-completion pass, supplied by whoever drives
    /// <see cref="JobCompletionPass"/>. Production (<c>WorldJobManager.ProcessLightingJobs</c> and
    /// <c>WorldJobManager.ProcessMeshJobs</c>) and the editor validation <c>LightingFrameSimulator</c> each
    /// implement it, so they share the exact multi-job iteration, fault-isolation, and release-inside /
    /// remove-after ordering — the completion-pass half of the shared-guard pattern started by
    /// <see cref="LightingScheduleDecision"/> / <see cref="LightingScanDecision"/>. The skeleton owns the
    /// control flow (the <c>try/catch/finally</c> and loop structure); the driver owns the side effects.
    /// See Documentation/Design/LIGHTING_ASYNC_BUG_VALIDATION_ROADMAP.md §10 (HF-4 #2) and
    /// Documentation/Design/MESHING_PIPELINE_ORCHESTRATION_REFACTOR.md §MP-4 (the meshing reuse).
    /// </summary>
    /// <typeparam name="TKey">The per-job key (production: <c>ChunkCoord</c>; sim: <c>Vector2Int</c> chunk coord).</typeparam>
    public interface IJobCompletionDriver<in TKey>
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

        /// <summary>Stage 2 — merge the completed job into its chunk (lighting: request the chunk +
        /// <c>MergeCompletedLightingJob</c>; meshing: resolve the chunk + <c>ApplyMeshData</c>). May throw; the
        /// skeleton isolates it via <see cref="OnMergeFault"/> but still runs <see cref="ReleaseJob"/> and
        /// enrolls the job.</summary>
        void MergeJob(TKey key);

        /// <summary>Stage-2 fault handler: record the fault and leave the chunk re-schedulable (a merge that
        /// threw is in an unknown state — a corrective pass must run rather than silently dropping it).</summary>
        void OnMergeFault(TKey key, Exception e);

        /// <summary>Unconditional per-job cleanup (lighting: clear <c>IsAwaitingMainThreadProcess</c> +
        /// release the job's containers; meshing: the MR-6 output return + the pooled input release). Runs in
        /// the merge <c>finally</c> even on a stage-2 fault, so a faulted job never lingers in the registry with
        /// disposed containers (the fidelity-B7 cascade).</summary>
        void ReleaseJob(TKey key);

        /// <summary>After the whole merge loop: remove the job from the registry and promote its neighborhood
        /// (lighting: <c>LightingJobs.Remove</c> + <c>PromoteLightWorkNeighborhood</c>; meshing:
        /// <c>MeshJobs.Remove</c> only — the mesh pipeline has no promotion concept, the queue retries).
        /// Strictly after every merge, so a completion promoting a neighbor sees the fully-merged pass (MT-2).</summary>
        void RemoveAndPromote(TKey key);
    }

    /// <summary>
    /// The production job-completion loop structure, extracted so <c>WorldJobManager</c>'s lighting pass, its
    /// mesh pass, and the editor frame simulator all drive one pass skeleton (HF-4 #2; MP-4 added meshing).
    /// The two structural guarantees it owns — and the harness could not replay before — are:
    /// <list type="bullet">
    /// <item><b>Fault isolation:</b> a stage-1 fault carries the job over; a stage-2 fault still releases the
    /// job and continues the pass (no aborted pass, no stranded-container cascade).</item>
    /// <item><b>Release-inside / remove-after ordering:</b> each job's merge + release happen inside the loop
    /// (so a later job's merge sees earlier jobs already enrolled), while registry removal + promotion happen
    /// only after every job has merged.</item>
    /// </list>
    /// The caller performs any production-specific work (dropped-update batching, mesh rebuilds) between the
    /// two calls; the sim runs them back-to-back.
    /// <para><b>P-4 §3.4 (the MP-4 reconcile).</b> The budgeted passes additionally need a time ceiling and a
    /// rotating visit start; both are optional parameters on <see cref="RunMergeLoop{TKey}"/> so the unbudgeted
    /// callers stay byte-identical. The <i>cursor</i> itself stays owned by the caller — advancing it is per-pass
    /// policy (production gates the advance on <c>window.HasBudget</c> so the flag-off legs keep legacy order),
    /// not a property of the skeleton.</para>
    /// </summary>
    public static class JobCompletionPass
    {
        /// <summary>
        /// Runs the fault-isolated merge loop over <paramref name="candidates"/>, clearing then repopulating
        /// <paramref name="enrolled"/> with the jobs that completed this pass. Mirrors the per-job body of
        /// <c>WorldJobManager.ProcessLightingJobs</c> / <c>ProcessMeshJobs</c> exactly: <c>IsComplete</c> gate →
        /// <c>try CompleteJob / catch OnCompleteFault + skip</c> → <c>try MergeJob / catch OnMergeFault /
        /// finally ReleaseJob + enroll</c>.
        /// </summary>
        /// <typeparam name="TKey">The per-job key type.</typeparam>
        /// <param name="candidates">The jobs to consider this pass, in processing order (production snapshots
        /// the job registry's keys; the sim applies its completion-order strategy first).</param>
        /// <param name="driver">The side-effect provider.</param>
        /// <param name="enrolled">Reused buffer; cleared here, then filled with every job that reached the
        /// merge <c>finally</c> (completed or stage-2-faulted). Also read by the driver's merge (production's
        /// <c>_completedLightJobs.Contains</c> cross-chunk check), so enrollment happens progressively.</param>
        /// <param name="window">Optional P-4 §3.4 time ceiling, checked <i>between</i> jobs (never mid-job — one
        /// oversized merge can overshoot it once). On expiry the pass breaks and the un-visited remainder is
        /// simply not enrolled, so it stays in the registry and is retried next pass (its containers are held one
        /// more frame, bounded by the caller's in-flight cap). <c>default</c> never expires.</param>
        /// <param name="startIndex">Optional rotating visit start (P-4 §3.4 fairness): candidates are visited
        /// from this index and wrap, so a budget break cannot systematically starve the same high-index job every
        /// frame. <c>0</c> is plain front-to-back order.</param>
        /// <returns>
        /// <c>true</c> when the loop stopped because the <paramref name="window"/> expired, <c>false</c> when
        /// it walked every candidate (FP-2 stop-reason attribution). Returned rather than re-derived by the
        /// caller: re-reading <c>window.Expired</c> after the call would report a ceiling stop for a pass
        /// that finished all its work and only then ran out of window. Callers that do not report a stop
        /// reason simply ignore it.
        /// </returns>
        public static bool RunMergeLoop<TKey>(
            IReadOnlyList<TKey> candidates,
            IJobCompletionDriver<TKey> driver,
            List<TKey> enrolled,
            PipelinePassBudget.Window window = default,
            int startIndex = 0)
        {
            enrolled.Clear();

            int count = candidates.Count;
            if (count == 0) return false; // Also guards the modulo below against a divide-by-zero.

            for (int i = 0; i < count; i++)
            {
                // §3.4 time ceiling — between jobs only, so the budget never leaves a job half-merged.
                if (window.Expired) return true;

                TKey candidate = candidates[(startIndex + i) % count];

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

            return false; // Walked every candidate — no ceiling break.
        }

        /// <summary>
        /// Removes + promotes every job the pass enrolled, strictly after the whole merge loop (and after any
        /// production-specific between-loop work the caller ran). Mirrors <c>ProcessLightingJobs</c>'
        /// end-of-method <c>foreach (_completedLightJobs) { LightingJobs.Remove; PromoteLightWorkNeighborhood }</c>
        /// and <c>ProcessMeshJobs</c>' <c>foreach (_completedMeshJobs) { MeshJobs.Remove }</c>.
        /// </summary>
        /// <typeparam name="TKey">The per-job key type.</typeparam>
        /// <param name="enrolled">The jobs enrolled by <see cref="RunMergeLoop{TKey}"/> this pass.</param>
        /// <param name="driver">The side-effect provider.</param>
        public static void RunRemoveAndPromote<TKey>(
            List<TKey> enrolled,
            IJobCompletionDriver<TKey> driver)
        {
            foreach (TKey key in enrolled)
            {
                driver.RemoveAndPromote(key);
            }
        }
    }
}
