using System.Threading;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Profiling.LowLevel.Unsafe;

namespace Jobs.Data
{
    /// <summary>
    /// Adds a job's execute time to a busy-time record owned by the main thread, for the performance monitor's worker
    /// counters. The default value is untimed: <see cref="Begin"/> returns 0 and <see cref="End"/> does nothing, so a
    /// job built without a timer runs exactly as before.
    /// <para>
    /// A record is <see cref="RecordLength"/> <c>long</c>s: <see cref="BusyIndex"/> accumulates
    /// <see cref="ProfilerUnsafeUtility.Timestamp"/> ticks and <see cref="LinkIndex"/> counts timed executions — one
    /// per <c>IJob</c>, one per <c>IJobFor</c> index — so a chain with an unwired link is detectable. Both are updated
    /// atomically, because a parallel job's indices and every link of a chain share one record.
    /// </para>
    /// </summary>
    public unsafe struct JobBusyTimer
    {
        /// <summary>Index in a record of the accumulated busy ticks.</summary>
        public const int BusyIndex = 0;

        /// <summary>Index in a record of the count of timed executions.</summary>
        public const int LinkIndex = 1;

        /// <summary>Number of <c>long</c>s in one record.</summary>
        public const int RecordLength = 2;

        [NativeDisableUnsafePtrRestriction]
        private readonly long* _record;

        /// <summary>Creates a timer that accumulates into <paramref name="record"/>.</summary>
        /// <param name="record">The record's first element; it must outlive every job the timer is given to.</param>
        public JobBusyTimer(long* record)
        {
            _record = record;
        }

        /// <summary>Whether executions are recorded.</summary>
        public bool IsTimed => _record != null;

        /// <summary>Opens a timed execution; close it with <see cref="End"/>.</summary>
        /// <returns>The start timestamp, or 0 when untimed.</returns>
        public long Begin() => _record != null ? ProfilerUnsafeUtility.Timestamp : 0L;

        /// <summary>Adds the time since <paramref name="startTimestamp"/> and one execution to the record. A no-op when untimed.</summary>
        /// <param name="startTimestamp">The value <see cref="Begin"/> returned.</param>
        public void End(long startTimestamp)
        {
            if (_record == null) return;

            Interlocked.Add(ref _record[BusyIndex], ProfilerUnsafeUtility.Timestamp - startTimestamp);
            Interlocked.Increment(ref _record[LinkIndex]);
        }
    }
}
