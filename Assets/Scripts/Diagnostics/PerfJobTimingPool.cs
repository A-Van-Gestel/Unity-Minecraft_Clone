using System;
using Jobs.Data;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Diagnostics
{
    /// <summary>
    /// Busy-time records for timed jobs: one native block of <see cref="JobBusyTimer.RecordLength"/>-long records,
    /// handed out by handle. A record is rented when a job is scheduled and returned once the main thread has completed
    /// the job and read the record, so a running job always writes into live memory.
    /// <para>
    /// Main thread only. The block is allocated on the first rent and stays allocated until <see cref="Dispose"/>, which
    /// the owner calls only after completing every job that holds a record — a tier change never frees it.
    /// </para>
    /// </summary>
    public sealed class PerfJobTimingPool : IDisposable
    {
        /// <summary>
        /// Records the pool holds by default. A job scheduled while every record is rented runs untimed and is counted in
        /// <see cref="UntimedTotal"/>.
        /// </summary>
        public const int DefaultCapacity = 2048;

        private readonly int _capacity;
        private readonly int[] _free;
        private int _freeCount;
        private NativeArray<long> _records;
        private long _untimedTotal;

        /// <summary>Creates an empty pool; no native memory is allocated until the first rent.</summary>
        /// <param name="capacity">The number of records.</param>
        public PerfJobTimingPool(int capacity = DefaultCapacity)
        {
            _capacity = capacity;
            _free = new int[capacity];
        }

        /// <summary>Records currently rented.</summary>
        public int RentedCount => _records.IsCreated ? _capacity - _freeCount : 0;

        /// <summary>Running total of rents refused because every record was rented; those jobs ran untimed.</summary>
        public long UntimedTotal => _untimedTotal;

        /// <summary>Rents a zeroed record.</summary>
        /// <param name="timer">A timer writing into the record, or an untimed default when every record is rented.</param>
        /// <returns>The record's handle, or 0 when none was free.</returns>
        public unsafe int Rent(out JobBusyTimer timer)
        {
            EnsureRecords();
            if (_freeCount == 0)
            {
                _untimedTotal++;
                timer = default;
                return 0;
            }

            int index = _free[--_freeCount];
            timer = new JobBusyTimer((long*)_records.GetUnsafePtr() + index * JobBusyTimer.RecordLength);
            return index + 1;
        }

        /// <summary>Reads a record. Call only after the jobs given its timer have completed.</summary>
        /// <param name="handle">A handle from <see cref="Rent"/>, not 0.</param>
        /// <param name="busyTicks">Accumulated execute time, in <c>ProfilerUnsafeUtility.Timestamp</c> ticks.</param>
        /// <param name="links">Timed executions.</param>
        public void Read(int handle, out long busyTicks, out long links)
        {
            int start = (handle - 1) * JobBusyTimer.RecordLength;
            busyTicks = _records[start + JobBusyTimer.BusyIndex];
            links = _records[start + JobBusyTimer.LinkIndex];
        }

        /// <summary>Zeroes a record and makes it available again. Call only after the jobs given its timer have completed.</summary>
        /// <param name="handle">A handle from <see cref="Rent"/>; 0 is a no-op.</param>
        public void Return(int handle)
        {
            if (handle == 0 || !_records.IsCreated) return;

            int start = (handle - 1) * JobBusyTimer.RecordLength;
            for (int i = 0; i < JobBusyTimer.RecordLength; i++)
                _records[start + i] = 0;

            _free[_freeCount++] = handle - 1;
        }

        /// <summary>Frees the native block. Every job holding a record must have completed.</summary>
        public void Dispose()
        {
            if (_records.IsCreated) _records.Dispose();
            _freeCount = 0;
        }

        private void EnsureRecords()
        {
            if (_records.IsCreated) return;

            _records = new NativeArray<long>(_capacity * JobBusyTimer.RecordLength, Allocator.Persistent);
            for (int i = 0; i < _capacity; i++)
                _free[i] = _capacity - 1 - i;
            _freeCount = _capacity;
        }
    }
}
