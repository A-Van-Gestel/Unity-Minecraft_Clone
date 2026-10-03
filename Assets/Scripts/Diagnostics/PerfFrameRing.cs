using System;
using Unity.Collections;

namespace Diagnostics
{
    /// <summary>
    /// Fixed-capacity history of <see cref="PerfFrame"/> rows, plus an optional block of per-slot columns
    /// (one <c>float</c> of milliseconds per <see cref="PerfSlot"/> per frame). Both live in native memory, so
    /// the monitor's own storage never shows up in the managed-heap readings it takes.
    /// <para>
    /// The slot columns are allocated and released independently of the frame rows, so a tier change costs
    /// only the slot block. Frames committed while the columns were absent carry no slot data, which is why
    /// <see cref="SlotFrameCount"/> can trail <see cref="Count"/>. Main thread only.
    /// </para>
    /// </summary>
    public sealed class PerfFrameRing : IDisposable
    {
        private readonly int _capacity;
        private readonly int _slotCount;

        private NativeArray<PerfFrame> _frames;
        private NativeArray<float> _slotMs;
        private bool _hasSlotColumns;
        private bool _isDisposed;

        /// <summary>Index the next commit writes to.</summary>
        private int _head;

        private int _count;
        private int _slotFrameCount;

        /// <summary>Allocates the frame rows; the slot columns stay unallocated until <see cref="AllocateSlotColumns"/>.</summary>
        /// <param name="capacity">Frames retained; older frames are overwritten.</param>
        /// <param name="slotCount">Slot columns per frame.</param>
        public PerfFrameRing(int capacity, int slotCount)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            if (slotCount <= 0) throw new ArgumentOutOfRangeException(nameof(slotCount));

            _capacity = capacity;
            _slotCount = slotCount;
            _frames = new NativeArray<PerfFrame>(capacity, Allocator.Persistent);
        }

        /// <summary>Frames the ring retains.</summary>
        public int Capacity => _capacity;

        /// <summary>Frames currently held, up to <see cref="Capacity"/>.</summary>
        public int Count => _count;

        /// <summary>Held frames that carry slot columns — those committed since the columns were last allocated.</summary>
        public int SlotFrameCount => _slotFrameCount;

        /// <summary>Whether the per-slot columns are allocated.</summary>
        public bool HasSlotColumns => _hasSlotColumns;

        /// <summary>Whether <see cref="Dispose"/> has run.</summary>
        public bool IsDisposed => _isDisposed;

        /// <summary>Allocates the per-slot columns; a no-op when already allocated.</summary>
        public void AllocateSlotColumns()
        {
            if (_isDisposed) throw new ObjectDisposedException(nameof(PerfFrameRing));
            if (_hasSlotColumns) return;

            _slotMs = new NativeArray<float>(_capacity * _slotCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _hasSlotColumns = true;
            _slotFrameCount = 0;
        }

        /// <summary>Frees the per-slot columns; a no-op when not allocated.</summary>
        public void ReleaseSlotColumns()
        {
            if (!_hasSlotColumns) return;

            _slotMs.Dispose();
            _slotMs = default;
            _hasSlotColumns = false;
            _slotFrameCount = 0;
        }

        /// <summary>
        /// Appends one frame, overwriting the oldest when full. When the slot columns are allocated, each slot's
        /// accumulated ticks are converted to milliseconds and stored with it.
        /// </summary>
        /// <param name="frame">The frame row.</param>
        /// <param name="slotTicks">Per-slot stopwatch ticks for the frame; at least as long as the slot count.</param>
        /// <param name="tickToMs">Milliseconds per stopwatch tick.</param>
        public void Commit(PerfFrame frame, long[] slotTicks, double tickToMs)
        {
            _frames[_head] = frame;

            if (_hasSlotColumns)
            {
                int row = _head * _slotCount;
                for (int i = 0; i < _slotCount; i++)
                    _slotMs[row + i] = (float)(slotTicks[i] * tickToMs);

                if (_slotFrameCount < _capacity) _slotFrameCount++;
            }

            _head++;
            if (_head == _capacity) _head = 0;
            if (_count < _capacity) _count++;
        }

        /// <summary>Reads a held frame by age.</summary>
        /// <param name="age">0 for the newest frame, up to <see cref="Count"/> − 1 for the oldest.</param>
        /// <returns>The frame row.</returns>
        public PerfFrame GetFrame(int age)
        {
            if ((uint)age >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(age));
            return _frames[IndexOfAge(age)];
        }

        /// <summary>Reads one slot's milliseconds for a held frame by age.</summary>
        /// <param name="age">0 for the newest frame, up to <see cref="SlotFrameCount"/> − 1.</param>
        /// <param name="slot">The slot.</param>
        /// <returns>The slot's milliseconds in that frame.</returns>
        public float GetSlotMs(int age, PerfSlot slot)
        {
            if ((uint)age >= (uint)_slotFrameCount) throw new ArgumentOutOfRangeException(nameof(age));
            return _slotMs[IndexOfAge(age) * _slotCount + (int)slot];
        }

        /// <summary>Copies the held frames' wall milliseconds, newest first.</summary>
        /// <param name="destination">Receives up to its length of values.</param>
        /// <returns>The number of values copied.</returns>
        public int CopyWallMs(float[] destination)
        {
            int n = Math.Min(_count, destination.Length);
            for (int age = 0; age < n; age++)
                destination[age] = _frames[IndexOfAge(age)].WallMs;
            return n;
        }

        /// <summary>Copies the held frames' CPU milliseconds, newest first.</summary>
        /// <param name="destination">Receives up to its length of values.</param>
        /// <returns>The number of values copied.</returns>
        public int CopyCpuMs(float[] destination)
        {
            int n = Math.Min(_count, destination.Length);
            for (int age = 0; age < n; age++)
                destination[age] = _frames[IndexOfAge(age)].CpuMs;
            return n;
        }

        /// <summary>Copies one slot's milliseconds for the frames that carry slot columns, newest first.</summary>
        /// <param name="slot">The slot.</param>
        /// <param name="destination">Receives up to its length of values.</param>
        /// <returns>The number of values copied (0 when the columns are not allocated).</returns>
        public int CopySlotMs(PerfSlot slot, float[] destination)
        {
            int n = Math.Min(_slotFrameCount, destination.Length);
            for (int age = 0; age < n; age++)
                destination[age] = _slotMs[IndexOfAge(age) * _slotCount + (int)slot];
            return n;
        }

        /// <summary>Frees both native blocks. Safe to call more than once.</summary>
        public void Dispose()
        {
            if (_isDisposed) return;

            ReleaseSlotColumns();
            _frames.Dispose();
            _frames = default;
            _count = 0;
            _head = 0;
            _isDisposed = true;
        }

        private int IndexOfAge(int age)
        {
            int index = _head - 1 - age;
            return index < 0 ? index + _capacity : index;
        }
    }
}
