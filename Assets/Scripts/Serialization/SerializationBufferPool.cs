using System.Collections.Concurrent;
using System.Threading;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace Serialization
{
    public static class SerializationBufferPool
    {
        // Pool for serialization buffers (e.g., 256KB should fit any compressed chunk)
        private const int BUFFER_SIZE = 256 * 1024; 
        [NoAutoStaticsCleanup] // contents cleared in DomainReset
        private static readonly ConcurrentBag<byte[]> s_pool = new ConcurrentBag<byte[]>();

        // Interlocked: saves rent buffers on ThreadPool threads.
        [NoAutoStaticsCleanup] // reset in DomainReset
        private static long s_totalCreated;

        /// <summary>Cumulative count of <see cref="Get"/> calls that found the pool empty and allocated a new buffer (pool misses).</summary>
        public static long TotalCreated => Interlocked.Read(ref s_totalCreated);

        public static byte[] Get()
        {
            if (s_pool.TryTake(out byte[] buffer)) return buffer;
            Interlocked.Increment(ref s_totalCreated);
            return new byte[BUFFER_SIZE];
        }

        public static void Return(byte[] buffer)
        {
            if (buffer.Length == BUFFER_SIZE) s_pool.Add(buffer);
        }

        /// <summary>
        /// Drops every pooled buffer and zeroes the miss counter on play-mode entry. The bag is unbounded,
        /// so without this the previous session's 256 KB buffers would be retained once domain reload stops
        /// clearing them.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void DomainReset()
        {
            while (s_pool.TryTake(out byte[] _)) { }
            Interlocked.Exchange(ref s_totalCreated, 0);
        }
    }
}
