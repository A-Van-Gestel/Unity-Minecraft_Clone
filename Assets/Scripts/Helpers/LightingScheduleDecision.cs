namespace Helpers
{
    /// <summary>
    /// Pure decision function for the lighting job scheduling guard: whether a chunk may start a lighting
    /// job now, and when it may not, which precondition blocked it.
    /// <para>
    /// This is the sole admission rule for a lighting schedule — it exists so that no path can accept or
    /// reject a scheduling attempt on terms of its own. A caller that re-tests these preconditions itself
    /// re-opens the drift it prevents. <see cref="LightingScanDecision"/> continues the pattern for the
    /// per-chunk arm; this type covers only the in-flight / neighbors-data-ready gate.
    /// </para>
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
        /// <param name="hasJobInFlight">True when a lighting job is already running for this chunk.</param>
        /// <param name="neighborsDataReady">True when all cardinal neighbors have populated terrain data.</param>
        /// <returns>The scheduling decision.</returns>
        public static Result Evaluate(bool hasJobInFlight, bool neighborsDataReady)
        {
            if (hasJobInFlight) return Result.AlreadyInFlight;
            if (!neighborsDataReady) return Result.NeighborsNotReady;
            return Result.Schedule;
        }
    }
}
