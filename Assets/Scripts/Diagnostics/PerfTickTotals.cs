using System.Diagnostics;
using Helpers;

namespace Diagnostics
{
    /// <summary>
    /// Running totals of the behavior tick's main-thread parts — the active-chunk list, the per-chunk fluid prepare,
    /// scheduling, the wait for the fluid jobs, the grass drain and the fluid replay — with the work counts behind them.
    /// The owner samples them into the per-frame <c>Tick*</c> <see cref="PerfCounter"/>s.
    /// <para>
    /// A region is timed only when its start came from <see cref="PerfStore.Begin()"/> while slots were active; a start
    /// of 0 adds nothing, so below <see cref="PerfTier.Systems"/> each probe costs one static read. Main thread only.
    /// </para>
    /// </summary>
    public struct PerfTickTotals
    {
        /// <summary>Kilobytes in one full-chunk voxel map copied for a fluid job.</summary>
        public const int SnapshotMapKb = ChunkMath.CHUNK_VOLUME * sizeof(uint) / BYTES_PER_KB;

        private const int BYTES_PER_KB = 1024;

        /// <summary>Building the active-chunk list, in <see cref="Stopwatch"/> ticks.</summary>
        public long ListTicks;

        /// <summary>Preparing fluid chunks — bucket partition and voxel-map copies, plus first-use allocation — in ticks.</summary>
        public long PrepareTicks;

        /// <summary>Renting tickers and timing records, scheduling the jobs and flushing the batch, in ticks.</summary>
        public long ScheduleTicks;

        /// <summary>Waiting for the fluid jobs to complete, in ticks.</summary>
        public long WaitTicks;

        /// <summary>The managed grass tick, in ticks.</summary>
        public long GrassTicks;

        /// <summary>Replaying the fluid jobs' modifications and inactive voxels, in ticks.</summary>
        public long ReplayTicks;

        /// <summary>Fluid chunks prepared for a job.</summary>
        public long FluidChunks;

        /// <summary>Full-chunk voxel maps copied by those prepares; <see cref="SnapshotMapKb"/> each.</summary>
        public long SnapshotMaps;

        /// <summary>Active grass voxels ticked.</summary>
        public long GrassVoxels;

        /// <summary>
        /// Adds the time since <paramref name="start"/> to <paramref name="total"/> and returns the end, so consecutive
        /// regions share one timestamp per boundary. A start of 0 adds nothing and returns 0.
        /// </summary>
        /// <param name="total">The part's running total, in <see cref="Stopwatch"/> ticks.</param>
        /// <param name="start">The region's start from <see cref="PerfStore.Begin()"/> or an earlier lap.</param>
        /// <returns>The region's end timestamp, or 0 when it was not timed.</returns>
        public static long Lap(ref long total, long start)
        {
            if (start == 0L) return 0L;

            long now = Stopwatch.GetTimestamp();
            total += now - start;
            return now;
        }
    }
}
