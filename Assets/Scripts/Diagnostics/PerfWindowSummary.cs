namespace Diagnostics
{
    /// <summary>
    /// Exact statistics over a window of per-frame milliseconds. Percentiles are nearest-rank: each is a sample
    /// that actually occurred, never an interpolation or a bucket bound. All zero when <see cref="Count"/> is 0.
    /// </summary>
    public struct PerfWindowSummary
    {
        /// <summary>Samples in the window.</summary>
        public int Count;

        /// <summary>The largest sample — the worst frame.</summary>
        public float Max;

        /// <summary>The arithmetic mean.</summary>
        public float Mean;

        /// <summary>The nearest-rank median.</summary>
        public float P50;

        /// <summary>The nearest-rank 99th percentile.</summary>
        public float P99;
    }
}
