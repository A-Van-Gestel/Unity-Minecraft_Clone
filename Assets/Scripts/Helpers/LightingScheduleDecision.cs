/// <summary>
/// Pure decision function for the lighting job scheduling guard.
/// Both <c>WorldJobManager.ScheduleLightingUpdate</c> and the editor validation
/// <c>LightingFrameSimulator</c> call this to ensure identical guard logic — the
/// two can never silently disagree on when to accept or reject a scheduling attempt.
/// </summary>
public static class LightingScheduleDecision
{
    public enum Result : byte
    {
        /// <summary>All preconditions met — proceed with scheduling.</summary>
        Schedule,

        /// <summary>A lighting job is already in-flight for this chunk.</summary>
        AlreadyInFlight,

        /// <summary>One or more cardinal neighbors lack populated terrain data.</summary>
        NeighborsNotReady,
    }

    /// <summary>
    /// Evaluates whether a lighting job should be scheduled for a chunk.
    /// </summary>
    /// <param name="hasJobInFlight">True when a lighting job is already running for this chunk
    /// (production: <c>LightingJobs.ContainsKey</c>).</param>
    /// <param name="neighborsDataReady">True when all cardinal neighbors have populated terrain data
    /// (production: <c>World.AreNeighborsDataReady</c>).</param>
    /// <returns>The scheduling decision.</returns>
    public static Result Evaluate(bool hasJobInFlight, bool neighborsDataReady)
    {
        if (hasJobInFlight) return Result.AlreadyInFlight;
        if (!neighborsDataReady) return Result.NeighborsNotReady;
        return Result.Schedule;
    }
}
