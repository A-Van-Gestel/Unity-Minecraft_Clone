namespace Diagnostics
{
    /// <summary>
    /// One frame's raw end-of-frame readings — cumulative values, not deltas — from which a <see cref="PerfFrame"/>
    /// row is derived.
    /// </summary>
    public struct PerfFrameReadings
    {
        /// <summary>The frame's wall-clock stopwatch ticks.</summary>
        public long WallTicks;

        /// <summary>The frame's measured main-thread CPU stopwatch ticks.</summary>
        public long CpuTicks;

        /// <summary>The frame's <c>Time.frameCount</c>.</summary>
        public int FrameIndex;

        /// <summary>The managed heap size at the end of the frame (<c>GC.GetTotalMemory(false)</c>).</summary>
        public long HeapBytes;

        /// <summary>The cumulative collection count at the end of the frame (<c>GC.CollectionCount(0)</c>).</summary>
        public int GcCollectionCount;

        /// <summary>The end of the frame on the <c>FrameTiming</c> clock (<c>ProfilerUnsafeUtility.Timestamp</c>).</summary>
        public long EndTimestamp;
    }
}
