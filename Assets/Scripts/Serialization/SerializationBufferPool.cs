using System.Collections.Concurrent;
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

        public static byte[] Get()
        {
            if (s_pool.TryTake(out byte[] buffer)) return buffer;
            return new byte[BUFFER_SIZE];
        }

        public static void Return(byte[] buffer)
        {
            if (buffer.Length == BUFFER_SIZE) s_pool.Add(buffer);
        }

        /// <summary>
        /// Drops every pooled buffer on play-mode entry. The bag is unbounded, so without this the
        /// previous session's 256 KB buffers would be retained once domain reload stops clearing them.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void DomainReset()
        {
            while (s_pool.TryTake(out byte[] _)) { }
        }
    }
}
