using System;
using Unity.Collections;

namespace Diagnostics
{
    /// <summary>
    /// Fixed-capacity history of <see cref="PerfFrame"/> rows, plus an optional block of per-slot columns
    /// (one <c>float</c> of milliseconds per <see cref="PerfSlot"/> per frame) with its per-counter columns (one
    /// <c>int</c> per <see cref="PerfCounter"/> per frame). All live in native memory, so the monitor's own storage
    /// never shows up in the managed-heap readings it takes.
    /// <para>
    /// The slot and counter columns are allocated and released together, independently of the frame rows, so a
    /// tier change costs only those blocks. Frames committed while the columns were absent carry no slot data, which is why
    /// <see cref="SlotFrameCount"/> can trail <see cref="Count"/>. Main thread only.
    /// </para>
    /// </summary>
    public sealed class PerfFrameRing : IDisposable
    {
        private const float BYTES_PER_KILOBYTE = 1024f;

        private readonly int _capacity;
        private readonly int _slotCount;
        private readonly int _counterCount;

        private NativeArray<PerfFrame> _frames;
        private NativeArray<float> _slotMs;
        private NativeArray<int> _counterValues;
        private bool _hasSlotColumns;
        private bool _isDisposed;

        /// <summary>Index the next commit writes to.</summary>
        private int _head;

        private int _count;
        private int _slotFrameCount;

        /// <summary>Allocates the frame rows; the slot and counter columns stay unallocated until <see cref="AllocateSlotColumns"/>.</summary>
        /// <param name="capacity">Frames retained; older frames are overwritten.</param>
        /// <param name="slotCount">Slot columns per frame.</param>
        /// <param name="counterCount">Counter columns per frame; 0 for none.</param>
        public PerfFrameRing(int capacity, int slotCount, int counterCount = 0)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            if (slotCount <= 0) throw new ArgumentOutOfRangeException(nameof(slotCount));
            if (counterCount < 0) throw new ArgumentOutOfRangeException(nameof(counterCount));

            _capacity = capacity;
            _slotCount = slotCount;
            _counterCount = counterCount;
            _frames = new NativeArray<PerfFrame>(capacity, Allocator.Persistent);
        }

        /// <summary>Frames the ring retains.</summary>
        public int Capacity => _capacity;

        /// <summary>Frames currently held, up to <see cref="Capacity"/>.</summary>
        public int Count => _count;

        /// <summary>Held frames that carry slot columns — those committed since the columns were last allocated.</summary>
        public int SlotFrameCount => _slotFrameCount;

        /// <summary>Whether the per-slot columns, and with them the per-counter columns, are allocated.</summary>
        public bool HasSlotColumns => _hasSlotColumns;

        /// <summary>Counter columns per frame.</summary>
        public int CounterCount => _counterCount;

        /// <summary>Whether <see cref="Dispose"/> has run.</summary>
        public bool IsDisposed => _isDisposed;

        /// <summary>Allocates the per-slot and per-counter columns; a no-op when already allocated.</summary>
        public void AllocateSlotColumns()
        {
            if (_isDisposed) throw new ObjectDisposedException(nameof(PerfFrameRing));
            if (_hasSlotColumns) return;

            _slotMs = new NativeArray<float>(_capacity * _slotCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            if (_counterCount > 0)
                _counterValues = new NativeArray<int>(_capacity * _counterCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _hasSlotColumns = true;
            _slotFrameCount = 0;
        }

        /// <summary>Frees the per-slot and per-counter columns; a no-op when not allocated.</summary>
        public void ReleaseSlotColumns()
        {
            if (!_hasSlotColumns) return;

            _slotMs.Dispose();
            _slotMs = default;
            if (_counterValues.IsCreated) _counterValues.Dispose();
            _counterValues = default;
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
        public void Commit(PerfFrame frame, long[] slotTicks, double tickToMs) => Commit(frame, slotTicks, tickToMs, null);

        /// <summary>
        /// Appends one frame as <see cref="Commit(PerfFrame, long[], double)"/> does, storing the counter values beside its
        /// slot times while the columns are allocated.
        /// </summary>
        /// <param name="frame">The frame row.</param>
        /// <param name="slotTicks">Per-slot stopwatch ticks for the frame; at least as long as the slot count.</param>
        /// <param name="tickToMs">Milliseconds per stopwatch tick.</param>
        /// <param name="counterValues">Per-counter values for the frame, at least as long as the counter count; null stores zeros.</param>
        public void Commit(PerfFrame frame, long[] slotTicks, double tickToMs, int[] counterValues)
        {
            _frames[_head] = frame;

            if (_hasSlotColumns)
            {
                int row = _head * _slotCount;
                for (int i = 0; i < _slotCount; i++)
                    _slotMs[row + i] = (float)(slotTicks[i] * tickToMs);

                int counterRow = _head * _counterCount;
                for (int i = 0; i < _counterCount; i++)
                    _counterValues[counterRow + i] = counterValues?[i] ?? 0;

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

        /// <summary>Reads one counter for a held frame by age.</summary>
        /// <param name="age">0 for the newest frame, up to <see cref="SlotFrameCount"/> − 1.</param>
        /// <param name="counter">The counter; below <see cref="CounterCount"/>.</param>
        /// <returns>The counter's value in that frame.</returns>
        public int GetCounter(int age, PerfCounter counter)
        {
            if ((uint)age >= (uint)_slotFrameCount) throw new ArgumentOutOfRangeException(nameof(age));
            if ((int)counter >= _counterCount) throw new ArgumentOutOfRangeException(nameof(counter));
            return _counterValues[IndexOfAge(age) * _counterCount + (int)counter];
        }

        /// <summary>Copies the held frames' wall milliseconds, newest first.</summary>
        /// <param name="destination">Receives up to its length of values.</param>
        /// <returns>The number of values copied.</returns>
        public int CopyWallMs(float[] destination) => CopyField(PerfFrameField.WallMs, destination);

        /// <summary>Copies the held frames' CPU milliseconds, newest first.</summary>
        /// <param name="destination">Receives up to its length of values.</param>
        /// <returns>The number of values copied.</returns>
        public int CopyCpuMs(float[] destination) => CopyField(PerfFrameField.CpuMs, destination);

        /// <summary>
        /// Copies one field of the held frames, newest first, skipping frames for which the field has no value:
        /// allocation outside <see cref="PerfGcState.Measured"/> frames, and frame timings that never arrived.
        /// </summary>
        /// <param name="field">The field.</param>
        /// <param name="destination">Receives up to its length of values.</param>
        /// <returns>The number of values copied.</returns>
        public int CopyField(PerfFrameField field, float[] destination)
        {
            int n = 0;
            for (int age = 0; age < _count && n < destination.Length; age++)
            {
                if (TryReadField(_frames[IndexOfAge(age)], field, out float value))
                    destination[n++] = value;
            }

            return n;
        }

        /// <summary>Total garbage collections recorded across the held frames.</summary>
        /// <returns>The sum of <see cref="PerfFrame.GcCollections"/>.</returns>
        public int SumGcCollections()
        {
            int sum = 0;
            for (int age = 0; age < _count; age++)
                sum += _frames[IndexOfAge(age)].GcCollections;
            return sum;
        }

        /// <summary>
        /// Writes a late-arriving frame timing into the held row it describes: the row whose interval
        /// [previous row's <see cref="PerfFrame.EndTimestamp"/>, own end) contains the frame's start. The oldest
        /// held row has no known start, so a timing that predates the second-oldest row's interval is not matched.
        /// </summary>
        /// <param name="frameStartTimestamp">The timing's <c>frameStartTimestamp</c>, on the <see cref="PerfFrame.EndTimestamp"/> clock.</param>
        /// <param name="gpuMs">GPU milliseconds.</param>
        /// <param name="renderThreadMs">Render-thread milliseconds.</param>
        /// <param name="presentWaitMs">Present-wait milliseconds.</param>
        /// <param name="maxAge">Oldest row age searched; timings arrive a few frames late, so the search stays short.</param>
        /// <returns>True when a row matched and was written.</returns>
        public bool TryBackfillFrameTiming(long frameStartTimestamp, float gpuMs, float renderThreadMs, float presentWaitMs,
            int maxAge)
        {
            int lastAge = Math.Min(maxAge, _count - 2);
            for (int age = 0; age <= lastAge; age++)
            {
                int index = IndexOfAge(age);
                PerfFrame row = _frames[index];
                if (frameStartTimestamp >= row.EndTimestamp) return false;

                long rowStart = _frames[IndexOfAge(age + 1)].EndTimestamp;
                if (frameStartTimestamp < rowStart) continue;

                row.GpuMs = gpuMs;
                row.RenderThreadMs = renderThreadMs;
                row.PresentWaitMs = presentWaitMs;
                _frames[index] = row;
                return true;
            }

            return false;
        }

        /// <summary>Copies the newest held frames into a buffer, oldest first.</summary>
        /// <param name="destination">The receiving buffer.</param>
        /// <param name="destinationStart">Index of the first row written.</param>
        /// <param name="count">Frames to copy; at most <see cref="Count"/>.</param>
        public void CopyNewestFrames(NativeArray<PerfFrame> destination, int destinationStart, int count)
        {
            if ((uint)count > (uint)_count) throw new ArgumentOutOfRangeException(nameof(count));

            for (int i = 0; i < count; i++)
                destination[destinationStart + i] = _frames[IndexOfAge(count - 1 - i)];
        }

        /// <summary>Copies the slot columns of the newest held frames into a buffer, oldest row first.</summary>
        /// <param name="destination">The receiving buffer, one row of slot-count floats per frame.</param>
        /// <param name="destinationStart">Index of the first float written.</param>
        /// <param name="count">Frames to copy; at most <see cref="SlotFrameCount"/>.</param>
        public void CopyNewestSlotRows(NativeArray<float> destination, int destinationStart, int count)
        {
            if ((uint)count > (uint)_slotFrameCount) throw new ArgumentOutOfRangeException(nameof(count));

            for (int i = 0; i < count; i++)
            {
                NativeArray<float>.Copy(_slotMs, IndexOfAge(count - 1 - i) * _slotCount,
                    destination, destinationStart + i * _slotCount, _slotCount);
            }
        }

        /// <summary>Copies the counter columns of the newest held frames into a buffer, oldest row first.</summary>
        /// <param name="destination">The receiving buffer, one row of counter-count ints per frame.</param>
        /// <param name="destinationStart">Index of the first int written.</param>
        /// <param name="count">Frames to copy; at most <see cref="SlotFrameCount"/>.</param>
        public void CopyNewestCounterRows(NativeArray<int> destination, int destinationStart, int count)
        {
            if ((uint)count > (uint)_slotFrameCount) throw new ArgumentOutOfRangeException(nameof(count));

            for (int i = 0; i < count; i++)
            {
                NativeArray<int>.Copy(_counterValues, IndexOfAge(count - 1 - i) * _counterCount,
                    destination, destinationStart + i * _counterCount, _counterCount);
            }
        }

        /// <summary>Copies one held frame's slot columns into a managed buffer.</summary>
        /// <param name="age">0 for the newest frame, up to <see cref="SlotFrameCount"/> − 1.</param>
        /// <param name="destination">The receiving buffer.</param>
        /// <param name="destinationStart">Index of the first float written; the row takes slot-count floats.</param>
        public void CopySlotRow(int age, float[] destination, int destinationStart)
        {
            if ((uint)age >= (uint)_slotFrameCount) throw new ArgumentOutOfRangeException(nameof(age));
            NativeArray<float>.Copy(_slotMs, IndexOfAge(age) * _slotCount, destination, destinationStart, _slotCount);
        }

        /// <summary>Copies one held frame's counter columns into a managed buffer.</summary>
        /// <param name="age">0 for the newest frame, up to <see cref="SlotFrameCount"/> − 1.</param>
        /// <param name="destination">The receiving buffer.</param>
        /// <param name="destinationStart">Index of the first int written; the row takes counter-count ints.</param>
        public void CopyCounterRow(int age, int[] destination, int destinationStart)
        {
            if ((uint)age >= (uint)_slotFrameCount) throw new ArgumentOutOfRangeException(nameof(age));
            if (_counterCount == 0) return;
            NativeArray<int>.Copy(_counterValues, IndexOfAge(age) * _counterCount, destination, destinationStart, _counterCount);
        }

        /// <summary>Copies one counter for the frames that carry counter columns, newest first, as floats for window statistics.</summary>
        /// <param name="counter">The counter.</param>
        /// <param name="destination">Receives up to its length of values.</param>
        /// <returns>The number of values copied (0 when the columns are not allocated or the counter is out of range).</returns>
        public int CopyCounter(PerfCounter counter, float[] destination)
        {
            if ((int)counter >= _counterCount) return 0;

            int n = Math.Min(_slotFrameCount, destination.Length);
            for (int age = 0; age < n; age++)
                destination[age] = _counterValues[IndexOfAge(age) * _counterCount + (int)counter];
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

        /// <summary>Reads one field of a frame, reporting whether the frame has a value for it (see <see cref="CopyField"/>).</summary>
        /// <param name="frame">The frame.</param>
        /// <param name="field">The field.</param>
        /// <param name="value">The value; meaningless when the method returns false.</param>
        /// <returns>True when the frame has a value for the field.</returns>
        public static bool TryReadField(in PerfFrame frame, PerfFrameField field, out float value)
        {
            switch (field)
            {
                case PerfFrameField.WallMs:
                    value = frame.WallMs;
                    return true;
                case PerfFrameField.CpuMs:
                    value = frame.CpuMs;
                    return true;
                case PerfFrameField.GcAllocKb:
                    value = frame.GcAllocBytes / BYTES_PER_KILOBYTE;
                    return frame.GcState == PerfGcState.Measured;
                case PerfFrameField.GpuMs:
                    value = frame.GpuMs;
                    return !float.IsNaN(value);
                case PerfFrameField.RenderThreadMs:
                    value = frame.RenderThreadMs;
                    return !float.IsNaN(value);
                case PerfFrameField.PresentWaitMs:
                    value = frame.PresentWaitMs;
                    return !float.IsNaN(value);
                default:
                    throw new ArgumentOutOfRangeException(nameof(field));
            }
        }
    }
}
