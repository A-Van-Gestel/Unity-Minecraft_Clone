namespace Diagnostics
{
    /// <summary>
    /// How much the performance monitor records. Each tier includes everything below it, so tiers compare
    /// with <c>&gt;=</c>. A value is persisted as its integer and doubles as its option index, so the values stay
    /// contiguous from zero and are never reordered.
    /// </summary>
    public enum PerfTier
    {
        /// <summary>Raw per-frame wall and CPU time, managed allocation and collections, for worst-frame and percentile readouts. Always on.</summary>
        Basic = 0,

        /// <summary>Adds per-frame GPU, render-thread and present-wait time, and hitch detection with the frames around each hitch.</summary>
        Frame = 1,

        /// <summary>Adds a per-frame time for every <see cref="PerfSlot"/>.</summary>
        Systems = 2,

        /// <summary>Adds a session file of every frame and a file per hitch, written to disk in the background.</summary>
        Capture = 3,
    }
}
