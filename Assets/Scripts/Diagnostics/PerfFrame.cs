namespace Diagnostics
{
    /// <summary>One frame of the performance monitor's history ring: raw, unsmoothed timings.</summary>
    public struct PerfFrame
    {
        /// <summary>The frame's <c>Time.frameCount</c>.</summary>
        public int FrameIndex;

        /// <summary>Wall-clock milliseconds from the previous frame's end to this frame's end, including VSync and GPU waits.</summary>
        public float WallMs;

        /// <summary>Milliseconds of measured main-thread CPU work in this frame (the sum of the Unity lifecycle phases).</summary>
        public float CpuMs;

        /// <summary>Managed-heap growth over the frame, in bytes; meaningful only when <see cref="GcState"/> is <see cref="PerfGcState.Measured"/>.</summary>
        public int GcAllocBytes;

        /// <summary>How <see cref="GcAllocBytes"/> was obtained, and whether it can be trusted.</summary>
        public PerfGcState GcState;

        /// <summary>Garbage collections completed during the frame, saturating at 255.</summary>
        public byte GcCollections;

        /// <summary>GPU milliseconds for the frame (<c>FrameTiming.gpuFrameTime</c>); NaN until a timing for this frame arrives.</summary>
        public float GpuMs;

        /// <summary>Render-thread milliseconds (<c>FrameTiming.cpuRenderThreadFrameTime</c>); NaN until a timing arrives.</summary>
        public float RenderThreadMs;

        /// <summary>Main-thread milliseconds spent waiting for Present (<c>FrameTiming.cpuMainThreadPresentWaitTime</c>); NaN until a timing arrives.</summary>
        public float PresentWaitMs;

        /// <summary>
        /// When the frame was committed, on the <c>FrameTiming</c> clock (<c>ProfilerUnsafeUtility.Timestamp</c>) —
        /// the bound that matches a late-arriving frame timing to its row. 0 when not recorded.
        /// </summary>
        public long EndTimestamp;
    }
}
