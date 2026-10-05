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
    /// deserialize time, payload bytes), saves (serialize and write time, payload bytes), the time each background
    /// operation waited for a ThreadPool thread, and the operations in flight.
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
        private static long s_queueWaitTicks;

        [NoAutoStaticsCleanup] // reset in DomainReset
        private static long s_backgroundOps;

        [NoAutoStaticsCleanup] // reset in DomainReset
        private static long s_inFlight;

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

        /// <summary>Time background loads and saves waited between submission and a ThreadPool thread starting them, in microseconds.</summary>
        public static long QueueWaitMicros => PerfStore.TicksToMicros(Interlocked.Read(ref s_queueWaitTicks));

        /// <summary>Background loads and saves that started on a ThreadPool thread — the operations <see cref="QueueWaitMicros"/> covers.</summary>
        public static long BackgroundOps => Interlocked.Read(ref s_backgroundOps);

        /// <summary>
        /// Background loads and saves submitted and not yet resumed on their caller. Clamped at 0: an operation submitted
        /// before a domain reset that ends after it would otherwise read as −1.
        /// </summary>
        public static int InFlight => (int)Math.Max(Interlocked.Read(ref s_inFlight), 0L);

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
        public static void RecordQueueWait(long submitTimestamp)
        {
            if (submitTimestamp == 0L) return;

            Interlocked.Add(ref s_queueWaitTicks, Stopwatch.GetTimestamp() - submitTimestamp);
            Interlocked.Increment(ref s_backgroundOps);
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

            Interlocked.Add(ref s_readTicks, Stopwatch.GetTimestamp() - startTimestamp);
            if (!isHit)
            {
                Interlocked.Increment(ref s_loadMisses);
                return;
            }

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
        public static void RecordWrite(long startTimestamp, int payloadBytes)
        {
            if (startTimestamp == 0L) return;

            Interlocked.Add(ref s_writeTicks, Stopwatch.GetTimestamp() - startTimestamp);
            Interlocked.Increment(ref s_saves);
            Interlocked.Add(ref s_saveBytes, payloadBytes);
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
            Interlocked.Exchange(ref s_queueWaitTicks, 0L);
            Interlocked.Exchange(ref s_backgroundOps, 0L);
            Interlocked.Exchange(ref s_inFlight, 0L);
        }
    }
}
