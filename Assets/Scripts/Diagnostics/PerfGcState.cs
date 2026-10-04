namespace Diagnostics
{
    /// <summary>
    /// How a frame's managed-allocation figure was obtained. Only <see cref="Measured"/> frames enter allocation
    /// statistics: the figure is a heap delta, which a collection makes meaningless rather than zero.
    /// </summary>
    public enum PerfGcState : byte
    {
        /// <summary>No previous heap reading to subtract (the first frame after a reset).</summary>
        NoBaseline = 0,

        /// <summary>No collection ran and the heap did not shrink: the delta is the frame's allocation.</summary>
        Measured = 1,

        /// <summary>A collection completed during the frame, so its allocation is unknown.</summary>
        Collected = 2,

        /// <summary>The heap shrank with no collection counted — unexpected; the allocation is unknown.</summary>
        Shrank = 3,
    }
}
