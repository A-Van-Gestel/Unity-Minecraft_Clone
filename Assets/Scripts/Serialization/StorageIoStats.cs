using System;
using System.Diagnostics;
using System.Threading;
using Diagnostics;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace Serialization
{
    /// <summary>
    /// Running totals of chunk disk I/O for the performance monitor's counters: loads (region hits and misses, read and
    /// deserialize time, payload bytes), saves (serialize and write time, payload bytes, and the time their snapshot
    /// took returning to the data pool), the time each background operation waited for a ThreadPool thread, and the
    /// operations in flight.
    /// <para>
    /// Updated from ThreadPool threads and the main thread, so every total is <see cref="Interlocked"/>. An operation is
    /// measured only when it starts while <see cref="PerfStore.SlotsActive"/>: <see cref="Stamp"/> and
    /// <see cref="BeginQueued"/> then return a non-zero timestamp, and every <c>Record</c> call given 0 does nothing, so
    /// the monitor's lower tiers cost one static read per operation.
    /// </para>
    /// </summary>
    public static class StorageIoStats
    {
        [NoAutoStaticsCleanup] // reset in DomainReset
        private static long s_loadHits;

        [NoAutoStaticsCleanup] // reset in DomainReset
        private static long s_loadMisses;

        [NoAutoStaticsCleanup] // reset in DomainReset
        private static long s_readTicks;

        [NoAutoStaticsCleanup] // reset in DomainReset
        private static long s_deserializeTicks;

        [NoAutoStaticsCleanup] // reset in DomainReset
        private static long s_loadBytes;

        [NoAutoStaticsCleanup] // reset in DomainReset
        private static long s_saves;

        [NoAutoStaticsCleanup] // reset in DomainReset
        private static long s_serializeTicks;

        [NoAutoStaticsCleanup] // reset in DomainReset
        private static long s_writeTicks;

        [NoAutoStaticsCleanup] // reset in DomainReset
        private static long s_saveBytes;

        [NoAutoStaticsCleanup] // reset in DomainReset
        private static long s_saveRawBytes;

        [NoAutoStaticsCleanup] // reset in DomainReset
        private static long s_queueWaitTicks;

        [NoAutoStaticsCleanup] // reset in DomainReset
        private static long s_backgroundOps;

        [NoAutoStaticsCleanup] // reset in DomainReset
        private static long s_inFlight;

        [NoAutoStaticsCleanup] // reset in DomainReset
        private static long s_readHitTicks;

        [NoAutoStaticsCleanup] // reset in DomainReset
        private static long s_loadLockWaitTicks;

        [NoAutoStaticsCleanup] // reset in DomainReset
        private static long s_loadLockWaits;

        [NoAutoStaticsCleanup] // reset in DomainReset
        private static long s_saveLockWaitTicks;

        [NoAutoStaticsCleanup] // reset in DomainReset
        private static long s_saveLockWaits;

        [NoAutoStaticsCleanup] // reset in DomainReset
        private static long s_regionOpens;

        [NoAutoStaticsCleanup] // reset in DomainReset
        private static long s_regionOpenTicks;

        [NoAutoStaticsCleanup] // reset in DomainReset
        private static long s_loadQueueWaitTicks;

        [NoAutoStaticsCleanup] // reset in DomainReset
        private static long s_loadOps;

        [NoAutoStaticsCleanup] // reset in DomainReset
        private static long s_loadLatencyTicks;

        [NoAutoStaticsCleanup] // reset in DomainReset
        private static long s_loadLatencyOps;

        [NoAutoStaticsCleanup] // reset in DomainReset
        private static long s_snapshotReturnTicks;

        [NoAutoStaticsCleanup] // reset in DomainReset
        private static long s_snapshotReturns;

        /// <summary>Loads that found the chunk in its region file.</summary>
        public static long LoadHits => Interlocked.Read(ref s_loadHits);

        /// <summary>Loads that found no chunk on disk.</summary>
        public static long LoadMisses => Interlocked.Read(ref s_loadMisses);

        /// <summary>Time spent opening regions and reading payloads, hits and misses alike, in microseconds.</summary>
        public static long ReadMicros => PerfStore.TicksToMicros(Interlocked.Read(ref s_readTicks));

        /// <summary>Time spent decompressing and deserializing loaded payloads, in microseconds.</summary>
        public static long DeserializeMicros => PerfStore.TicksToMicros(Interlocked.Read(ref s_deserializeTicks));

        /// <summary>Compressed payload bytes read.</summary>
        public static long LoadBytes => Interlocked.Read(ref s_loadBytes);

        /// <summary>Chunk payloads written to a region file, from every save path.</summary>
        public static long Saves => Interlocked.Read(ref s_saves);

        /// <summary>Time spent serializing and compressing chunks for saving, in microseconds.</summary>
        public static long SerializeMicros => PerfStore.TicksToMicros(Interlocked.Read(ref s_serializeTicks));

        /// <summary>Time spent writing payloads into region files, in microseconds.</summary>
        public static long WriteMicros => PerfStore.TicksToMicros(Interlocked.Read(ref s_writeTicks));

        /// <summary>Compressed payload bytes written.</summary>
        public static long SaveBytes => Interlocked.Read(ref s_saveBytes);

        /// <summary>The same payloads' bytes before compression; with <see cref="SaveBytes"/>, the compression ratio.</summary>
        public static long SaveRawBytes => Interlocked.Read(ref s_saveRawBytes);

        /// <summary>Time background loads and saves waited between submission and a ThreadPool thread starting them, in microseconds.</summary>
        public static long QueueWaitMicros => PerfStore.TicksToMicros(Interlocked.Read(ref s_queueWaitTicks));

        /// <summary>Background loads and saves that started on a ThreadPool thread — the operations <see cref="QueueWaitMicros"/> covers.</summary>
        public static long BackgroundOps => Interlocked.Read(ref s_backgroundOps);

        /// <summary>
        /// Background loads and saves submitted and not yet resumed on their caller. Clamped at 0: an operation submitted
        /// before a domain reset that ends after it would otherwise read as −1.
        /// </summary>
        public static int InFlight => (int)Math.Max(Interlocked.Read(ref s_inFlight), 0L);

        /// <summary>Time spent reading payloads of loads that found their chunk, in microseconds; the misses' share is <see cref="ReadMicros"/> minus this.</summary>
        public static long ReadHitMicros => PerfStore.TicksToMicros(Interlocked.Read(ref s_readHitTicks));

        /// <summary>Time loads waited for a region file's lock, in microseconds.</summary>
        public static long LoadLockWaitMicros => PerfStore.TicksToMicros(Interlocked.Read(ref s_loadLockWaitTicks));

        /// <summary>Loads whose lock wait was recorded.</summary>
        public static long LoadLockWaits => Interlocked.Read(ref s_loadLockWaits);

        /// <summary>Time saves waited for a region file's lock, in microseconds.</summary>
        public static long SaveLockWaitMicros => PerfStore.TicksToMicros(Interlocked.Read(ref s_saveLockWaitTicks));

        /// <summary>Saves whose lock wait was recorded.</summary>
        public static long SaveLockWaits => Interlocked.Read(ref s_saveLockWaits);

        /// <summary>Region files opened.</summary>
        public static long RegionOpens => Interlocked.Read(ref s_regionOpens);

        /// <summary>Time spent opening region files, in microseconds.</summary>
        public static long RegionOpenMicros => PerfStore.TicksToMicros(Interlocked.Read(ref s_regionOpenTicks));

        /// <summary>The share of <see cref="QueueWaitMicros"/> that belongs to loads, in microseconds.</summary>
        public static long LoadQueueWaitMicros => PerfStore.TicksToMicros(Interlocked.Read(ref s_loadQueueWaitTicks));

        /// <summary>The loads among <see cref="BackgroundOps"/>.</summary>
        public static long LoadOps => Interlocked.Read(ref s_loadOps);

        /// <summary>Time loads spent from submission until their caller resumed, in microseconds.</summary>
        public static long LoadLatencyMicros => PerfStore.TicksToMicros(Interlocked.Read(ref s_loadLatencyTicks));

        /// <summary>Loads that resumed their caller: the divisor of <see cref="LoadLatencyMicros"/>.</summary>
        public static long LoadLatencyOps => Interlocked.Read(ref s_loadLatencyOps);

        /// <summary>Time a save's snapshot spent returning to the data pool, in microseconds.</summary>
        public static long SnapshotReturnMicros => PerfStore.TicksToMicros(Interlocked.Read(ref s_snapshotReturnTicks));

        /// <summary>Save snapshots returned to the data pool: the divisor of <see cref="SnapshotReturnMicros"/>.</summary>
        public static long SnapshotReturns => Interlocked.Read(ref s_snapshotReturns);

        /// <summary>Starts timing an operation.</summary>
        /// <returns>The start timestamp, or 0 when the monitor is not recording.</returns>
        public static long Stamp() => PerfStore.SlotsActive ? Stopwatch.GetTimestamp() : 0L;

        /// <summary>Counts a background operation as submitted; close it with <see cref="EndQueued"/> once its caller resumes.</summary>
        /// <returns>The submission timestamp, or 0 when the monitor is not recording.</returns>
        public static long BeginQueued()
        {
            if (!PerfStore.SlotsActive) return 0L;

            Interlocked.Increment(ref s_inFlight);
            return Stopwatch.GetTimestamp();
        }

        /// <summary>Adds the wait between submission and a ThreadPool thread starting the operation. Call first thing on that thread.</summary>
        /// <param name="submitTimestamp">The value <see cref="BeginQueued"/> returned; 0 is a no-op.</param>
        /// <param name="isLoad">Whether the operation is a load, which also counts into the load-only totals.</param>
        public static void RecordQueueWait(long submitTimestamp, bool isLoad)
        {
            if (submitTimestamp == 0L) return;

            long waited = Stopwatch.GetTimestamp() - submitTimestamp;
            Interlocked.Add(ref s_queueWaitTicks, waited);
            Interlocked.Increment(ref s_backgroundOps);
            if (!isLoad) return;

            Interlocked.Add(ref s_loadQueueWaitTicks, waited);
            Interlocked.Increment(ref s_loadOps);
        }

        /// <summary>Records a load's time from submission to its caller resuming. Call before <see cref="EndQueued"/>.</summary>
        /// <param name="submitTimestamp">The value <see cref="BeginQueued"/> returned; 0 is a no-op.</param>
        public static void RecordLoadLatency(long submitTimestamp)
        {
            if (submitTimestamp == 0L) return;

            Interlocked.Add(ref s_loadLatencyTicks, Stopwatch.GetTimestamp() - submitTimestamp);
            Interlocked.Increment(ref s_loadLatencyOps);
        }

        /// <summary>Records a load's wait for a region file's lock. Call first thing inside the lock.</summary>
        /// <param name="startTimestamp">The value <see cref="Stamp"/> returned before taking the lock; 0 is a no-op.</param>
        public static void RecordLoadLockWait(long startTimestamp)
        {
            if (startTimestamp == 0L) return;

            Interlocked.Add(ref s_loadLockWaitTicks, Stopwatch.GetTimestamp() - startTimestamp);
            Interlocked.Increment(ref s_loadLockWaits);
        }

        /// <summary>Records a save's wait for a region file's lock. Call first thing inside the lock.</summary>
        /// <param name="startTimestamp">The value <see cref="Stamp"/> returned before taking the lock; 0 is a no-op.</param>
        public static void RecordSaveLockWait(long startTimestamp)
        {
            if (startTimestamp == 0L) return;

            Interlocked.Add(ref s_saveLockWaitTicks, Stopwatch.GetTimestamp() - startTimestamp);
            Interlocked.Increment(ref s_saveLockWaits);
        }

        /// <summary>Records the save's snapshot being handed back to the data pool. Call after the return.</summary>
        /// <param name="startTimestamp">The value <see cref="Stamp"/> returned before the return; 0 is a no-op.</param>
        public static void RecordSnapshotReturn(long startTimestamp)
        {
            if (startTimestamp == 0L) return;

            Interlocked.Add(ref s_snapshotReturnTicks, Stopwatch.GetTimestamp() - startTimestamp);
            Interlocked.Increment(ref s_snapshotReturns);
        }

        /// <summary>Records a region file opened.</summary>
        /// <param name="startTimestamp">The value <see cref="Stamp"/> returned before opening; 0 is a no-op.</param>
        public static void RecordRegionOpen(long startTimestamp)
        {
            if (startTimestamp == 0L) return;

            Interlocked.Add(ref s_regionOpenTicks, Stopwatch.GetTimestamp() - startTimestamp);
            Interlocked.Increment(ref s_regionOpens);
        }

        /// <summary>Counts a background operation as finished.</summary>
        /// <param name="submitTimestamp">The value <see cref="BeginQueued"/> returned; 0 is a no-op.</param>
        public static void EndQueued(long submitTimestamp)
        {
            if (submitTimestamp == 0L) return;

            Interlocked.Decrement(ref s_inFlight);
        }

        /// <summary>Records a region read.</summary>
        /// <param name="startTimestamp">The value <see cref="Stamp"/> returned before the read; 0 is a no-op.</param>
        /// <param name="payloadBytes">The compressed payload's length, or 0 for a miss.</param>
        /// <param name="isHit">Whether the chunk was on disk.</param>
        public static void RecordRead(long startTimestamp, int payloadBytes, bool isHit)
        {
            if (startTimestamp == 0L) return;

            long elapsed = Stopwatch.GetTimestamp() - startTimestamp;
            Interlocked.Add(ref s_readTicks, elapsed);
            if (!isHit)
            {
                Interlocked.Increment(ref s_loadMisses);
                return;
            }

            Interlocked.Add(ref s_readHitTicks, elapsed);
            Interlocked.Increment(ref s_loadHits);
            Interlocked.Add(ref s_loadBytes, payloadBytes);
        }

        /// <summary>Records a deserialization, failed or not.</summary>
        /// <param name="startTimestamp">The value <see cref="Stamp"/> returned before it; 0 is a no-op.</param>
        public static void RecordDeserialize(long startTimestamp)
        {
            if (startTimestamp == 0L) return;

            Interlocked.Add(ref s_deserializeTicks, Stopwatch.GetTimestamp() - startTimestamp);
        }

        /// <summary>Records a serialization for saving.</summary>
        /// <param name="startTimestamp">The value <see cref="Stamp"/> returned before it; 0 is a no-op.</param>
        public static void RecordSerialize(long startTimestamp)
        {
            if (startTimestamp == 0L) return;

            Interlocked.Add(ref s_serializeTicks, Stopwatch.GetTimestamp() - startTimestamp);
        }

        /// <summary>Records a completed region write.</summary>
        /// <param name="startTimestamp">The value <see cref="Stamp"/> returned before it; 0 is a no-op.</param>
        /// <param name="payloadBytes">The payload's length.</param>
        /// <param name="uncompressedBytes">The payload's length before compression.</param>
        public static void RecordWrite(long startTimestamp, int payloadBytes, int uncompressedBytes)
        {
            if (startTimestamp == 0L) return;

            Interlocked.Add(ref s_writeTicks, Stopwatch.GetTimestamp() - startTimestamp);
            Interlocked.Increment(ref s_saves);
            Interlocked.Add(ref s_saveBytes, payloadBytes);
            Interlocked.Add(ref s_saveRawBytes, uncompressedBytes);
        }

        /// <summary>Samples every running total into the performance monitor's per-frame disk counters.</summary>
        public static void SamplePerfCounters()
        {
            PerfStore.SampleTotal(PerfCounter.DiskLoadHits, LoadHits);
            PerfStore.SampleTotal(PerfCounter.DiskLoadMisses, LoadMisses);
            PerfStore.SampleTotal(PerfCounter.DiskReadUs, ReadMicros);
            PerfStore.SampleTotal(PerfCounter.DeserializeUs, DeserializeMicros);
            PerfStore.SampleTotal(PerfCounter.DiskLoadBytes, LoadBytes);
            PerfStore.SampleTotal(PerfCounter.DiskSaves, Saves);
            PerfStore.SampleTotal(PerfCounter.SerializeUs, SerializeMicros);
            PerfStore.SampleTotal(PerfCounter.DiskWriteUs, WriteMicros);
            PerfStore.SampleTotal(PerfCounter.DiskSaveBytes, SaveBytes);
            PerfStore.SampleTotal(PerfCounter.DiskSaveRawBytes, SaveRawBytes);
            PerfStore.SampleTotal(PerfCounter.IoQueueWaitUs, QueueWaitMicros);
            PerfStore.SampleTotal(PerfCounter.IoBackgroundOps, BackgroundOps);
            PerfStore.SampleTotal(PerfCounter.DiskReadHitUs, ReadHitMicros);
            PerfStore.SampleTotal(PerfCounter.LoadLockWaitUs, LoadLockWaitMicros);
            PerfStore.SampleTotal(PerfCounter.SaveLockWaitUs, SaveLockWaitMicros);
            PerfStore.SampleTotal(PerfCounter.RegionOpens, RegionOpens);
            PerfStore.SampleTotal(PerfCounter.RegionOpenUs, RegionOpenMicros);
            PerfStore.SampleTotal(PerfCounter.IoLoadQueueWaitUs, LoadQueueWaitMicros);
            PerfStore.SampleTotal(PerfCounter.IoLoadOps, LoadOps);
            PerfStore.SampleTotal(PerfCounter.DiskLoadLatencyUs, LoadLatencyMicros);
            PerfStore.SampleTotal(PerfCounter.DiskLoadLatencyOps, LoadLatencyOps);
            PerfStore.SampleTotal(PerfCounter.SaveReturnUs, SnapshotReturnMicros);
            PerfStore.SampleTotal(PerfCounter.SaveReturns, SnapshotReturns);
        }

        /// <summary>Zeroes every total on play-mode entry.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void DomainReset()
        {
            Interlocked.Exchange(ref s_loadHits, 0L);
            Interlocked.Exchange(ref s_loadMisses, 0L);
            Interlocked.Exchange(ref s_readTicks, 0L);
            Interlocked.Exchange(ref s_deserializeTicks, 0L);
            Interlocked.Exchange(ref s_loadBytes, 0L);
            Interlocked.Exchange(ref s_saves, 0L);
            Interlocked.Exchange(ref s_serializeTicks, 0L);
            Interlocked.Exchange(ref s_writeTicks, 0L);
            Interlocked.Exchange(ref s_saveBytes, 0L);
            Interlocked.Exchange(ref s_saveRawBytes, 0L);
            Interlocked.Exchange(ref s_queueWaitTicks, 0L);
            Interlocked.Exchange(ref s_backgroundOps, 0L);
            Interlocked.Exchange(ref s_inFlight, 0L);
            Interlocked.Exchange(ref s_readHitTicks, 0L);
            Interlocked.Exchange(ref s_loadLockWaitTicks, 0L);
            Interlocked.Exchange(ref s_loadLockWaits, 0L);
            Interlocked.Exchange(ref s_saveLockWaitTicks, 0L);
            Interlocked.Exchange(ref s_saveLockWaits, 0L);
            Interlocked.Exchange(ref s_regionOpens, 0L);
            Interlocked.Exchange(ref s_regionOpenTicks, 0L);
            Interlocked.Exchange(ref s_loadQueueWaitTicks, 0L);
            Interlocked.Exchange(ref s_loadOps, 0L);
            Interlocked.Exchange(ref s_loadLatencyTicks, 0L);
            Interlocked.Exchange(ref s_loadLatencyOps, 0L);
            Interlocked.Exchange(ref s_snapshotReturnTicks, 0L);
            Interlocked.Exchange(ref s_snapshotReturns, 0L);
        }
    }
}
