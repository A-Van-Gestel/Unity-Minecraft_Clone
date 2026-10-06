using System;

namespace Diagnostics
{
    /// <summary>Exact statistics of every frame of one phase, from <see cref="PerfPhaseRecorder"/>.</summary>
    public struct PerfPhaseSummary
    {
        /// <summary>Frames the phase recorded.</summary>
        public int FrameCount;

        /// <summary>Wall milliseconds.</summary>
        public PerfWindowSummary Wall;

        /// <summary>CPU milliseconds.</summary>
        public PerfWindowSummary Cpu;

        /// <summary>Managed allocation in kilobytes, over the frames whose allocation was measured.</summary>
        public PerfWindowSummary GcAllocKb;

        /// <summary>GPU milliseconds, over the frames whose timing arrived with a GPU time.</summary>
        public PerfWindowSummary Gpu;

        /// <summary>Render-thread milliseconds, over the frames whose timing arrived.</summary>
        public PerfWindowSummary RenderThread;

        /// <summary>Present-wait milliseconds, over the frames whose timing arrived.</summary>
        public PerfWindowSummary PresentWait;

        /// <summary>Garbage collections completed during the phase.</summary>
        public int GcCollections;

        /// <summary>The summary of one field.</summary>
        /// <param name="field">The field.</param>
        /// <returns>Its statistics.</returns>
        public readonly PerfWindowSummary Get(PerfFrameField field) => field switch
        {
            PerfFrameField.WallMs => Wall,
            PerfFrameField.CpuMs => Cpu,
            PerfFrameField.GcAllocKb => GcAllocKb,
            PerfFrameField.GpuMs => Gpu,
            PerfFrameField.RenderThreadMs => RenderThread,
            PerfFrameField.PresentWaitMs => PresentWait,
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

        /// <summary>Stores the summary of one field; the counterpart of <see cref="Get"/>.</summary>
        /// <param name="field">The field.</param>
        /// <param name="summary">Its statistics.</param>
        public void Set(PerfFrameField field, PerfWindowSummary summary)
        {
            switch (field)
            {
                case PerfFrameField.WallMs:
                    Wall = summary;
                    return;
                case PerfFrameField.CpuMs:
                    Cpu = summary;
                    return;
                case PerfFrameField.GcAllocKb:
                    GcAllocKb = summary;
                    return;
                case PerfFrameField.GpuMs:
                    Gpu = summary;
                    return;
                case PerfFrameField.RenderThreadMs:
                    RenderThread = summary;
                    return;
                case PerfFrameField.PresentWaitMs:
                    PresentWait = summary;
                    return;
                default:
                    throw new ArgumentOutOfRangeException(nameof(field));
            }
        }
    }
}
