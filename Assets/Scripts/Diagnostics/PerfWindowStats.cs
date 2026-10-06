using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Diagnostics
{
    /// <summary>
    /// Exact window statistics by in-place selection: O(n) per percentile, no sorting and no allocation. Exact
    /// rather than histogram-bucketed because the percentiles are compared across runs, where a bucket's width
    /// would add error on top of the run-to-run noise.
    /// </summary>
    public static class PerfWindowStats
    {
        /// <summary>Percentile reported as <see cref="PerfWindowSummary.P50"/>.</summary>
        public const int MedianPercentile = 50;

        /// <summary>Percentile reported as <see cref="PerfWindowSummary.P99"/>.</summary>
        public const int HighPercentile = 99;

        private const int PERCENT = 100;

        /// <summary>Summarizes the first <paramref name="count"/> samples. Reorders that part of the buffer.</summary>
        /// <param name="samples">The samples; the first <paramref name="count"/> are read and permuted.</param>
        /// <param name="count">How many samples to use.</param>
        /// <returns>The summary; all zero when <paramref name="count"/> is 0.</returns>
        public static unsafe PerfWindowSummary Summarize(float[] samples, int count)
        {
            if (count <= 0) return default;
            if (count > samples.Length) throw new ArgumentOutOfRangeException(nameof(count));

            fixed (float* values = samples)
                return Summarize(values, count);
        }

        /// <summary>Summarizes the first <paramref name="count"/> native samples. Reorders that part of the buffer.</summary>
        /// <param name="samples">The samples; the first <paramref name="count"/> are read and permuted.</param>
        /// <param name="count">How many samples to use.</param>
        /// <returns>The summary; all zero when <paramref name="count"/> is 0.</returns>
        public static unsafe PerfWindowSummary Summarize(NativeArray<float> samples, int count)
        {
            if (count <= 0) return default;
            if (count > samples.Length) throw new ArgumentOutOfRangeException(nameof(count));

            return Summarize((float*)samples.GetUnsafePtr(), count);
        }

        /// <summary>
        /// Zero-based index of the nearest-rank percentile in a sorted window: rank ⌈p·n/100⌉, in integer math so
        /// no floating-point rounding can move it.
        /// </summary>
        /// <param name="count">Samples in the window (at least 1).</param>
        /// <param name="percentile">The percentile, 1–100.</param>
        /// <returns>The index into the sorted window.</returns>
        public static int NearestRankIndex(int count, int percentile)
        {
            int rank = (percentile * count + PERCENT - 1) / PERCENT;
            return Math.Max(rank - 1, 0);
        }

        private static unsafe PerfWindowSummary Summarize(float* samples, int count)
        {
            double sum = 0;
            float max = samples[0];
            for (int i = 0; i < count; i++)
            {
                float value = samples[i];
                sum += value;
                if (value > max) max = value;
            }

            return new PerfWindowSummary
            {
                Count = count,
                Max = max,
                Mean = (float)(sum / count),
                P50 = Select(samples, count, NearestRankIndex(count, MedianPercentile)),
                P99 = Select(samples, count, NearestRankIndex(count, HighPercentile)),
            };
        }

        /// <summary>Hoare selection: returns the value that would sit at index <paramref name="k"/> if the window were sorted.</summary>
        private static unsafe float Select(float* values, int count, int k)
        {
            int left = 0;
            int right = count - 1;

            while (left < right)
            {
                float pivot = values[left + ((right - left) >> 1)];
                int i = left;
                int j = right;

                while (i <= j)
                {
                    while (values[i] < pivot) i++;
                    while (values[j] > pivot) j--;

                    if (i <= j)
                    {
                        float swap = values[i];
                        values[i] = values[j];
                        values[j] = swap;
                        i++;
                        j--;
                    }
                }

                // [left..j] <= pivot <= [i..right]; anything strictly between equals the pivot.
                if (k <= j) right = j;
                else if (k >= i) left = i;
                else return values[k];
            }

            return values[k];
        }
    }
}
