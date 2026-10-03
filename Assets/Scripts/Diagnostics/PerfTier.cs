namespace Diagnostics
{
    /// <summary>
    /// How much the performance monitor records. Each tier includes everything below it, so tiers compare
    /// with <c>&gt;=</c>. A value is persisted as its integer and doubles as its option index, so the values stay
    /// contiguous from zero and are never reordered.
    /// </summary>
    public enum PerfTier
    {
        /// <summary>Raw per-frame wall and CPU time, for worst-frame and percentile readouts. Always on.</summary>
        Basic = 0,

        /// <summary>Records the same as <see cref="Basic"/>.</summary>
        Frame = 1,

        /// <summary>Adds a per-frame time for every <see cref="PerfSlot"/>.</summary>
        Systems = 2,

        /// <summary>Records the same as <see cref="Systems"/>.</summary>
        Capture = 3,
    }
}
