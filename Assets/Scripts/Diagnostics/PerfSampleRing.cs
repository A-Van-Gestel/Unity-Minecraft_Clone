using System;

namespace Diagnostics
{
    /// <summary>
    /// A fixed-capacity ring of the newest samples, one per event rather than per frame, for exact statistics with
    /// <see cref="PerfWindowStats"/>. Main thread only.
    /// </summary>
    public sealed class PerfSampleRing
    {
        private readonly float[] _values;
        private int _next;
        private int _count;

        /// <summary>Creates an empty ring.</summary>
        /// <param name="capacity">The newest samples retained.</param>
        public PerfSampleRing(int capacity)
        {
            _values = new float[capacity];
        }

        /// <summary>Samples held, at most the capacity.</summary>
        public int Count => _count;

        /// <summary>Adds a sample, overwriting the oldest once the ring is full.</summary>
        /// <param name="value">The sample.</param>
        public void Add(float value)
        {
            _values[_next] = value;
            _next = (_next + 1) % _values.Length;
            if (_count < _values.Length) _count++;
        }

        /// <summary>Copies the held samples, in no particular order, into the start of <paramref name="destination"/>.</summary>
        /// <param name="destination">A buffer at least <see cref="Count"/> long.</param>
        /// <returns>The number of samples copied.</returns>
        public int CopyTo(float[] destination)
        {
            Array.Copy(_values, destination, _count);
            return _count;
        }

        /// <summary>Forgets every sample.</summary>
        public void Clear()
        {
            _next = 0;
            _count = 0;
        }
    }
}
