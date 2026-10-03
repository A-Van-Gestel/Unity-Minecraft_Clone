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
    }
}
