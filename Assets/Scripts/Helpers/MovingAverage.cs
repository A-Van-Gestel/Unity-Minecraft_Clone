namespace Helpers
{
    /// <summary>
    /// A zero-allocation ring buffer for calculating moving averages over a fixed window of samples.
    /// Uses a pre-allocated array instead of a Queue to avoid GC pressure.
    /// </summary>
    public sealed class MovingAverage
    {
        private readonly long[] _samples;
        private int _currentIndex;
        private int _count;
        private long _accumulator;

        /// <summary>
        /// Creates a new MovingAverage with the specified window size.
        /// </summary>
        /// <param name="windowSize">The number of samples to average over.</param>
        public MovingAverage(int windowSize)
        {
            _samples = new long[windowSize];
        }

        /// <summary>
        /// Adds a new sample to the ring buffer, evicting the oldest if full.
        /// </summary>
        /// <param name="newSample">The new sample value (typically Stopwatch ticks or bytes).</param>
        public void Sample(long newSample)
        {
            if (_count == _samples.Length)
            {
                _accumulator -= _samples[_currentIndex];
            }
            else
            {
                _count++;
            }

            _samples[_currentIndex] = newSample;
            _accumulator += newSample;

            _currentIndex = (_currentIndex + 1) % _samples.Length;
        }

        /// <summary>
        /// Returns the integer average of all samples in the buffer.
        /// Returns 0 if no samples have been added.
        /// </summary>
        public long GetAverage() => _count == 0 ? 0 : _accumulator / _count;

        /// <summary>
        /// Resets the ring buffer, clearing all samples and the accumulator.
        /// </summary>
        public void Reset()
        {
            _currentIndex = 0;
            _count = 0;
            _accumulator = 0;

            // Zero out the backing array to prevent stale data if reused.
            for (int i = 0; i < _samples.Length; i++)
            {
                _samples[i] = 0;
            }
        }
    }
}
