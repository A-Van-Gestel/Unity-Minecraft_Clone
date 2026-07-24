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
        public enum Result : byte
        {
            /// <summary>All gates pass — build the job.</summary>
            Schedule,

            /// <summary>A mesh job is already running for this chunk (today the caller returns true; MP-3
            /// changes it to leave the chunk queued).</summary>
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
    }
}
