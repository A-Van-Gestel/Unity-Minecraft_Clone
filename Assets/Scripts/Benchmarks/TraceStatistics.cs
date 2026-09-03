using System;
using System.Collections.Generic;

namespace Benchmarks
{
    /// <summary>
    /// Pure summary statistics over a latency sample series (FP-3). Split out of the report generator so the
    /// ChunkMath validation suite can pin the percentile selection from edit mode with no <c>World</c> —
    /// a wrong percentile silently mis-ranks every future capture, which is exactly the class of error a
    /// report cannot reveal on its own.
    /// <para>
    /// <b>Nearest-rank percentiles</b> (no interpolation): the p-th percentile is the sample at
    /// <c>ceil(p/100 × n)</c>, 1-based. Chosen over linear interpolation because every reported value is then
    /// a real observed latency rather than a synthetic average of two, which matters when a reader is asked
    /// to reconcile the percentile table against the raw histogram beside it (§7.2).
    /// </para>
    /// </summary>
    public static class TraceStatistics
    {
        /// <summary>Upper bucket edges in milliseconds for the §7.2 raw histogram (last bucket is unbounded).</summary>
        public static readonly double[] HistogramEdgesMs = { 1, 2, 5, 10, 20, 50, 100, 200, 500, 1000, 2000, 5000 };

        /// <summary>
        /// Selects the nearest-rank percentile from an <b>already-sorted ascending</b> series.
        /// </summary>
        /// <param name="sortedAscending">The sorted samples. Must not be null.</param>
        /// <param name="percentile">The percentile in [0, 100].</param>
        /// <returns>The selected sample, or 0 when the series is empty.</returns>
        public static long Percentile(IReadOnlyList<long> sortedAscending, double percentile)
        {
            int n = sortedAscending.Count;
            if (n == 0) return 0;

            if (percentile <= 0) return sortedAscending[0];
            if (percentile >= 100) return sortedAscending[n - 1];

            // Nearest-rank: rank = ceil(p/100 * n), clamped into [1, n], then 0-based.
            int rank = (int)Math.Ceiling(percentile / 100.0 * n);
            if (rank < 1) rank = 1;
            else if (rank > n) rank = n;

            return sortedAscending[rank - 1];
        }

        /// <summary>
        /// Buckets samples by <see cref="HistogramEdgesMs"/> for the §7.2 raw block. The returned array has
        /// one slot per edge plus a final overflow slot, so no sample is ever dropped from the histogram —
        /// a reader recomputing a statistic must never silently lose the tail.
        /// </summary>
        /// <param name="samplesMs">Sample latencies in milliseconds (order irrelevant).</param>
        /// <returns>Counts per bucket; length is <c>HistogramEdgesMs.Length + 1</c>.</returns>
        public static int[] Histogram(IReadOnlyList<double> samplesMs)
        {
            int[] buckets = new int[HistogramEdgesMs.Length + 1];

            foreach (double ms in samplesMs)
            {
                int slot = HistogramEdgesMs.Length; // Overflow until an edge claims it.
                for (int i = 0; i < HistogramEdgesMs.Length; i++)
                {
                    if (ms <= HistogramEdgesMs[i])
                    {
                        slot = i;
                        break;
                    }
                }

                buckets[slot]++;
            }

            return buckets;
        }

        /// <summary>Human-readable label for a histogram bucket (the last one is the unbounded overflow).</summary>
        /// <param name="index">Bucket index, in <c>[0, HistogramEdgesMs.Length]</c>.</param>
        /// <returns>A label such as "≤5ms" or "&gt;5000ms".</returns>
        public static string BucketLabel(int index)
        {
            return index >= HistogramEdgesMs.Length
                ? $">{HistogramEdgesMs[^1]:F0}ms"
                : $"<={HistogramEdgesMs[index]:F0}ms";
        }
    }
}
