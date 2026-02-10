using System.Collections.Concurrent;

namespace Serialization
{
    public static class SerializationBufferPool
    {
        // Pool for serialization buffers (e.g., 256KB should fit any compressed chunk)
        private const int BUFFER_SIZE = 256 * 1024; 
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
    }
}
