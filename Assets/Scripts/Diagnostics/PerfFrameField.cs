namespace Diagnostics
{
    /// <summary>A per-frame value of <see cref="PerfFrame"/> that window statistics can be taken over.</summary>
    public enum PerfFrameField : byte
    {
        /// <summary><see cref="PerfFrame.WallMs"/>; every frame.</summary>
        WallMs,

        /// <summary><see cref="PerfFrame.CpuMs"/>; every frame.</summary>
        CpuMs,

        /// <summary><see cref="PerfFrame.GcAllocBytes"/> in kilobytes; only <see cref="PerfGcState.Measured"/> frames.</summary>
        GcAllocKb,

        /// <summary><see cref="PerfFrame.GpuMs"/>; only frames whose timing arrived.</summary>
        GpuMs,

        /// <summary><see cref="PerfFrame.RenderThreadMs"/>; only frames whose timing arrived.</summary>
        RenderThreadMs,

        /// <summary><see cref="PerfFrame.PresentWaitMs"/>; only frames whose timing arrived.</summary>
        PresentWaitMs,
    }
}
