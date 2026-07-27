using System;
using Benchmarks;
using Data;
using Jobs.Data;
using UnityEngine;

namespace Helpers
{
    /// <summary>
    /// The mesh pass's <see cref="IJobCompletionDriver{TKey}"/>: the per-job side effects the shared
    /// <see cref="JobCompletionPass"/> skeleton sequences for <c>WorldJobManager.ProcessMeshJobs</c>.
    /// A separate object rather than another interface on <c>WorldJobManager</c> itself, because one class
    /// cannot implement <c>IJobCompletionDriver&lt;ChunkCoord&gt;</c> twice — the lighting pass already
    /// holds that slot there. Instantiated once, so the per-frame pass allocates nothing.
    /// <para>Every collaborator it touches comes through <see cref="IMeshCompletionHost"/> (§8.1), so the
    /// validation suite can drive this exact class with a recording fake host — B27 replays the skeleton,
    /// B31–B33 replay <i>this</i>.</para>
    /// </summary>
    /// <remarks><c>_curJob</c> caches the job across a single candidate's hooks; the pass is single-threaded
    /// and non-reentrant, so a plain field is safe (the lighting driver's <c>_curLightJob</c> precedent).</remarks>
    public sealed class MeshCompletionDriver : IJobCompletionDriver<ChunkCoord>
    {
        private readonly IMeshCompletionHost _host;
        private MeshingJobData _curJob;

        /// <summary>Initializes the driver against the host supplying its collaborators.</summary>
        /// <param name="host">The owner of the job registry, chunk map, and pools (production: <c>WorldJobManager</c>).</param>
        public MeshCompletionDriver(IMeshCompletionHost host) => _host = host;

        /// <inheritdoc />
        public bool IsComplete(ChunkCoord key) => _host.IsJobComplete(key);

        /// <inheritdoc />
        public void CompleteJob(ChunkCoord key) => _curJob = _host.CompleteJob(key);

        /// <inheritdoc />
        public void OnCompleteFault(ChunkCoord key, Exception e)
        {
            Debug.LogError($"[MESHING] Handle.Complete() for chunk {key} faulted — job left enrolled for retry. {e}");
        }

        /// <inheritdoc />
        public void MergeJob(ChunkCoord key)
        {
            // A gone chunk discards the result (out-of-range work has already left the chunk map) and, with
            // it, the animation: MP-6 pairs the load animation to the apply that earned it, so a discarded
            // result can never animate an empty slot.
            if (_host.TryApplyMesh(key, in _curJob))
            {
                _host.TriggerLoadAnimation(key);

                // FP-1 terminal stage stamp. Post-MP-6 the apply IS the moment the chunk becomes visible —
                // the animation fires on this same line — so there is no later "visible" hop to stamp.
                // Inside the success branch: a discarded result must not be recorded as an arrival.
                PipelineTelemetry.StampMeshApplied(key);
            }
        }

        /// <inheritdoc />
        public void OnMergeFault(ChunkCoord key, Exception e)
        {
            // Deliberately does not claim which half failed: the upload may have completed and only the
            // animation thrown, so "previous mesh kept" would point an investigator at the wrong stage.
            Debug.LogError($"[MESHING] Merging the completed mesh for chunk {key} (upload + load animation) faulted — buffers released, chunk left with whatever mesh it had. {e}");
        }

        /// <inheritdoc />
        public void ReleaseJob(ChunkCoord key)
        {
            // MR-6: single release site for both branches (applied and discarded), symmetric with the input
            // release. The upload above is done, so the pooled buffers can be cleared and reused.
            _host.ReleaseJobData(in _curJob);

            // Drop the now-recycled handles: the scratch is only valid inside one Complete → Merge → Release
            // sequence, and holding released buffers is the fidelity-B7 stranded-container shape. Any future
            // hook reading it out of sequence then fails loudly instead of silently operating on buffers
            // another job already owns.
            _curJob = default;
        }

        /// <inheritdoc />
        public void RemoveAndPromote(ChunkCoord key) => _host.RemoveJob(key);
    }
}
