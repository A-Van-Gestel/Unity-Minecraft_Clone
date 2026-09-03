using Data;
using Jobs.Data;

namespace Helpers
{
    /// <summary>
    /// The collaborators <see cref="MeshCompletionDriver"/> needs from its owner (production:
    /// <c>WorldJobManager</c>) — the <see cref="IMeshDrainHost"/> sibling on the completion side of the
    /// meshing pipeline. Kept as a cached interface rather than per-job delegates so the per-frame pass
    /// allocates nothing.
    /// <para>Its reason to exist is testability: every collaborator the driver used to reach through its
    /// owner needed a functioning <c>World</c> (a live chunk map, real <c>JobHandle</c>s, real
    /// <c>SectionRenderer</c>s uploading to the GPU), which the validation runner's <c>World.Instance</c>
    /// isolation guard forbids. Behind this seam a recording fake host drives the <b>real</b> driver
    /// world-free — see the meshing suite's B31–B33 and
    /// Documentation/Design/MESHING_PIPELINE_ORCHESTRATION_REFACTOR.md §8.1.</para>
    /// </summary>
    /// <remarks>Deliberately <b>not</b> a home for the MP-1/MP-4 diagnostic counters: an interface member
    /// cannot be <c>[Conditional]</c>, so routing the probes through here would resurrect their machinery
    /// in release players. They stay inside the production <see cref="TryApplyMesh"/> body, where the
    /// <c>[Conditional]</c> gate still elides them.</remarks>
    public interface IMeshCompletionHost
    {
        /// <summary>Has this key's mesh job finished? (production: <c>MeshJobs[key].Handle.IsCompleted</c>).</summary>
        /// <param name="key">The chunk coordinate the job is keyed on.</param>
        /// <returns>True when the job may be completed and merged this pass.</returns>
        bool IsJobComplete(ChunkCoord key);

        /// <summary>Completes the job's handle and hands back its data for the rest of the sequence
        /// (production: <c>Handle.Complete()</c>). May throw — the skeleton's stage-1 fault isolation
        /// then carries the job over with its containers still owned.</summary>
        /// <param name="key">The chunk coordinate the job is keyed on.</param>
        /// <returns>The completed job's data, cached by the driver for the merge + release hooks.</returns>
        MeshingJobData CompleteJob(ChunkCoord key);

        /// <summary>Resolves the job's chunk and uploads <paramref name="job"/>'s output to it
        /// (production: <c>GetChunkFromChunkCoord</c> + <c>Chunk.ApplyMeshData</c>, plus the MP-1/MP-4
        /// merge probes). May throw — the skeleton's stage-2 isolation still releases the buffers.</summary>
        /// <param name="key">The chunk coordinate the job is keyed on.</param>
        /// <param name="job">The completed job whose output is being applied.</param>
        /// <returns>True when a chunk received the mesh; false when the chunk is gone and the result is discarded.</returns>
        bool TryApplyMesh(ChunkCoord key, in MeshingJobData job);

        /// <summary>Starts the chunk's one-shot load animation, now that it has geometry (MP-6; production:
        /// <c>Chunk.TriggerLoadAnimation</c>). Called only after a successful <see cref="TryApplyMesh"/>, so
        /// a discarded result never animates.</summary>
        /// <param name="key">The chunk coordinate whose mesh was just applied.</param>
        void TriggerLoadAnimation(ChunkCoord key);

        /// <summary>Releases a completed job's buffers: the MR-6 central output return plus the pooled input
        /// release. The single release site for both the applied and the discarded branch.</summary>
        /// <param name="job">The completed job whose buffers are being reclaimed.</param>
        void ReleaseJobData(in MeshingJobData job);

        /// <summary>Drops the job from the registry after the whole merge loop (production:
        /// <c>MeshJobs.Remove</c> + the probe untrack). The mesh pipeline has no promotion concept — a chunk
        /// still needing a rebuild retries through the mesh build queue.</summary>
        /// <param name="key">The chunk coordinate the job was keyed on.</param>
        void RemoveJob(ChunkCoord key);
    }
}
