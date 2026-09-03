namespace Helpers
{
    /// <summary>
    /// Pure decision function for the mesh job scheduling guard — the meshing sibling of
    /// <see cref="LightingScheduleDecision"/>. Both <c>WorldJobManager.ScheduleMeshing</c> and the
    /// editor meshing validation suite call this so the two can never silently disagree on when a
    /// mesh job may be scheduled. Mirrors the gate order of <c>ScheduleMeshing</c> exactly:
    /// in-flight → center light-readiness → neighbor mesh-readiness.
    /// </summary>
    public static class MeshingScheduleDecision
    {
        /// <summary>The outcome of the mesh-scheduling gates, in precedence order — consumed by
        /// <see cref="DequeuesChunk"/> to decide whether the caller builds the job or leaves the chunk queued.</summary>
        public enum Result : byte
        {
            /// <summary>All gates pass — build the job.</summary>
            Schedule,

            /// <summary>A mesh job is already running for this chunk. The caller leaves the chunk queued
            /// (MP-3: <see cref="DequeuesChunk"/> returns false), so the rebuild reschedules the frame after
            /// the flight completes instead of being dropped against the stale schedule-time snapshot.</summary>
            AlreadyInFlight,

            /// <summary>The center chunk has unscheduled light work (gate skipped when lighting is disabled).</summary>
            CenterNotLightReady,

            /// <summary><c>AreNeighborsMeshReady</c> failed — a neighbor lacks generated/lit data.</summary>
            NeighborsNotReady,
        }

        /// <summary>
        /// Evaluates whether a mesh job should be scheduled for a chunk.
        /// </summary>
        /// <param name="jobInFlight">True when a mesh job is already running for this chunk
        /// (production: <c>MeshJobs.ContainsKey</c>).</param>
        /// <param name="lightingEnabled">True when the lighting system is active
        /// (production: <c>settings.enableLighting</c>); when false the center light gate is bypassed.</param>
        /// <param name="centerHasLightWork">True when the center chunk has unprocessed light changes
        /// (production: <c>ChunkData.HasLightChangesToProcess</c>).</param>
        /// <param name="centerNeedsInitialLighting">True when the center chunk has never completed an
        /// initial lighting pass (production: <c>ChunkData.NeedsInitialLighting</c>).</param>
        /// <param name="neighborsMeshReady">True when every neighbor has the generated/lit data the mesh
        /// job needs (production: <c>World.AreNeighborsMeshReady</c>).</param>
        /// <returns>The scheduling decision.</returns>
        public static Result Evaluate(
            bool jobInFlight, bool lightingEnabled,
            bool centerHasLightWork, bool centerNeedsInitialLighting,
            bool neighborsMeshReady)
        {
            if (jobInFlight) return Result.AlreadyInFlight;
            if (lightingEnabled && (centerHasLightWork || centerNeedsInitialLighting))
                return Result.CenterNotLightReady;
            if (!neighborsMeshReady) return Result.NeighborsNotReady;
            return Result.Schedule;
        }

        /// <summary>
        /// Maps a decision to the boolean <c>ScheduleMeshing</c> returns to the drain: true means "handled —
        /// dequeue the chunk", false means "leave it queued to retry next frame". Only <see cref="Result.Schedule"/>
        /// dequeues (the caller builds the job, then returns true); every other result leaves the chunk queued.
        /// This is the single definition <c>ScheduleMeshing</c> and the B26 baseline share, so the MP-3 in-flight
        /// policy can never diverge between production and its test. MP-3 fix: <see cref="Result.AlreadyInFlight"/>
        /// leaves the chunk queued — before MP-3 it dequeued, dropping a rebuild requested during the flight
        /// against the job's stale schedule-time snapshot (the F1 lost update).
        /// </summary>
        /// <param name="result">The scheduling decision from <see cref="Evaluate"/>.</param>
        /// <returns>True to dequeue the chunk (only <see cref="Result.Schedule"/>); false to leave it queued.</returns>
        public static bool DequeuesChunk(Result result) => result == Result.Schedule;
    }
}
