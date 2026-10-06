using System;
using Unity.Collections;

namespace Diagnostics
{
    /// <summary>
    /// Flags hitch frames and keeps the frames around each one. A frame is a hitch when its wall time exceeds
    /// <c>max(minimum, factor × median)</c>, the median taken over the last <see cref="BaselineFrames"/> frames and
    /// refreshed every <see cref="BaselineRefreshFrames"/>; until the first refresh only the minimum applies.
    /// <para>
    /// <b>Windows.</b> A hitch opens a window that closes <see cref="FramesAfter"/> frames later, when the
    /// <see cref="FramesBefore"/> frames before it, the hitch and the frames after are copied out of the ring
    /// into a record. Hitches inside an open window join it rather than opening their own, and do not extend it.
    /// The newest <see cref="MaxRecords"/> records are held; a new one overwrites the oldest. Closing late lets
    /// frame timings, which arrive a few frames after their frame, reach the hitch rows before they are copied.
    /// </para>
    /// <para>
    /// Frame rows live in native memory, allocated with the detector; the per-slot block, and with it the
    /// per-counter block, only between <see cref="AllocateSlotBlock"/> and <see cref="ReleaseSlotBlock"/>. A record
    /// carries counter rows exactly when it carries slot times. Main thread only.
    /// </para>
    /// </summary>
    public sealed class PerfHitchDetector : IDisposable
    {
        /// <summary>Default minimum hitch threshold, in milliseconds.</summary>
        public const float DefaultMinMs = 33f;

        /// <summary>Default multiple of the recent median frame time that makes a hitch.</summary>
        public const float DefaultMedianFactor = 2.5f;

        /// <summary>Frames kept from before the hitch that opens a window.</summary>
        public const int FramesBefore = 120;

        /// <summary>Frames recorded after it before the window closes.</summary>
        public const int FramesAfter = 30;

        /// <summary>Most frames one record holds.</summary>
        public const int WindowFrames = FramesBefore + 1 + FramesAfter;

        /// <summary>Records held.</summary>
        public const int MaxRecords = 16;

        /// <summary>Frames the median baseline is taken over.</summary>
        public const int BaselineFrames = 240;

        /// <summary>Frames between baseline refreshes.</summary>
        public const int BaselineRefreshFrames = 60;

        private const int TOP_SLOTS = 3;

        private readonly int _slotCount;
        private readonly int _counterCount;
        private readonly float[] _baselineScratch = new float[BaselineFrames];
        private readonly PerfHitchRecord[] _records = new PerfHitchRecord[MaxRecords];

        private NativeArray<PerfFrame> _recordFrames;
        private NativeArray<float> _recordSlots;
        private NativeArray<int> _recordCounters;
        private bool _hasSlotBlock;
        private bool _isDisposed;

        /// <summary>Index the next closed record is written to.</summary>
        private int _recordHead;

        private int _recordCount;
        private float _minMs;
        private float _medianFactor;
        private float _baselineMedianMs;
        private int _framesSinceRefresh;

        private bool _isWindowOpen;
        private PerfHitchRecord _open;
        private int _framesSinceHitch;

        /// <summary>Frames between the opening hitch and the slowest one in the open window.</summary>
        private int _worstOffset;

        /// <summary>Allocates the record frame rows; the slot block stays unallocated until <see cref="AllocateSlotBlock"/>.</summary>
        /// <param name="slotCount">Slot columns per frame, as in the ring.</param>
        /// <param name="minMs">Minimum hitch threshold; see <see cref="SetThresholds"/>.</param>
        /// <param name="medianFactor">Multiple of the median; see <see cref="SetThresholds"/>.</param>
        /// <param name="counterCount">Counter columns per frame, as in the ring; 0 for none.</param>
        public PerfHitchDetector(int slotCount, float minMs, float medianFactor, int counterCount = 0)
        {
            if (slotCount <= 0) throw new ArgumentOutOfRangeException(nameof(slotCount));
            if (counterCount < 0) throw new ArgumentOutOfRangeException(nameof(counterCount));

            _slotCount = slotCount;
            _counterCount = counterCount;
            _recordFrames = new NativeArray<PerfFrame>(MaxRecords * WindowFrames, Allocator.Persistent);
            SetThresholds(minMs, medianFactor);
        }

        /// <summary>The threshold the next frame is tested against, in milliseconds.</summary>
        public float ThresholdMs => Math.Max(_minMs, _medianFactor * _baselineMedianMs);

        /// <summary>The median frame time from the last baseline refresh; 0 before the first.</summary>
        public float BaselineMedianMs => _baselineMedianMs;

        /// <summary>Records held, up to <see cref="MaxRecords"/>.</summary>
        public int RecordCount => _recordCount;

        /// <summary>Records closed since the detector was created, held or overwritten.</summary>
        public int RecordsClosed { get; private set; }

        /// <summary>Hitch frames seen since the detector was created.</summary>
        public int HitchFrames { get; private set; }

        /// <summary>Whether a window is open, waiting for its frames after the hitch.</summary>
        public bool IsWindowOpen => _isWindowOpen;

        /// <summary>Whether the per-slot block is allocated.</summary>
        public bool HasSlotBlock => _hasSlotBlock;

        /// <summary>Whether <see cref="Dispose"/> has run.</summary>
        public bool IsDisposed => _isDisposed;

        /// <summary>Sets the thresholds; a non-positive or non-finite minimum, or a factor below 1, falls back to its default.</summary>
        /// <param name="minMs">Wall milliseconds above which a frame is always a hitch.</param>
        /// <param name="medianFactor">Multiple of the recent median above which a frame is a hitch.</param>
        public void SetThresholds(float minMs, float medianFactor)
        {
            _minMs = ResolveMinMs(minMs);
            _medianFactor = ResolveMedianFactor(medianFactor);
        }

        /// <summary>The minimum threshold a detector applies for a requested one: the value when positive and finite, else <see cref="DefaultMinMs"/>.</summary>
        /// <param name="minMs">The requested minimum, in milliseconds.</param>
        /// <returns>The minimum applied.</returns>
        public static float ResolveMinMs(float minMs) => minMs > 0f && !float.IsInfinity(minMs) ? minMs : DefaultMinMs;

        /// <summary>The median factor a detector applies for a requested one: the value when at least 1 and finite, else <see cref="DefaultMedianFactor"/>.</summary>
        /// <param name="medianFactor">The requested multiple of the median.</param>
        /// <returns>The factor applied.</returns>
        public static float ResolveMedianFactor(float medianFactor) =>
            medianFactor >= 1f && !float.IsInfinity(medianFactor) ? medianFactor : DefaultMedianFactor;

        /// <summary>Allocates the per-slot and per-counter blocks; a no-op when already allocated.</summary>
        public void AllocateSlotBlock()
        {
            if (_isDisposed) throw new ObjectDisposedException(nameof(PerfHitchDetector));
            if (_hasSlotBlock) return;

            _recordSlots = new NativeArray<float>(MaxRecords * WindowFrames * _slotCount, Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            if (_counterCount > 0)
            {
                _recordCounters = new NativeArray<int>(MaxRecords * WindowFrames * _counterCount, Allocator.Persistent,
                    NativeArrayOptions.UninitializedMemory);
            }

            _hasSlotBlock = true;
        }

        /// <summary>Frees the per-slot and per-counter blocks; held records keep their top slots but lose their per-row slot times and counters.</summary>
        public void ReleaseSlotBlock()
        {
            if (!_hasSlotBlock) return;

            _recordSlots.Dispose();
            _recordSlots = default;
            if (_recordCounters.IsCreated) _recordCounters.Dispose();
            _recordCounters = default;
            _hasSlotBlock = false;
            for (int i = 0; i < MaxRecords; i++)
                _records[i].HasSlots = false;
        }

        /// <summary>Tests the ring's newest frame, advances the open window, and refreshes the baseline when due.</summary>
        /// <param name="ring">The ring the frame was just committed to.</param>
        public void OnFrameCommitted(PerfFrameRing ring)
        {
            PerfFrame frame = ring.GetFrame(0);
            float threshold = ThresholdMs;
            bool isHitch = frame.WallMs > threshold;
            if (isHitch) HitchFrames++;

            if (_isWindowOpen)
            {
                _framesSinceHitch++;
                if (isHitch) JoinWindow(frame);
                if (_framesSinceHitch >= FramesAfter) CloseWindow(ring);
            }
            else if (isHitch)
            {
                OpenWindow(frame, threshold);
            }

            if (++_framesSinceRefresh >= BaselineRefreshFrames)
            {
                _framesSinceRefresh = 0;
                _baselineMedianMs = PerfWindowStats.Summarize(_baselineScratch, ring.CopyWallMs(_baselineScratch)).P50;
            }
        }

        /// <summary>Reads a held record by age.</summary>
        /// <param name="age">0 for the newest record, up to <see cref="RecordCount"/> − 1.</param>
        /// <returns>The record.</returns>
        public PerfHitchRecord GetRecord(int age) => _records[IndexOfAge(age)];

        /// <summary>Reads one frame of a held record.</summary>
        /// <param name="age">The record's age, as for <see cref="GetRecord"/>.</param>
        /// <param name="row">0 for the window's oldest frame, up to its <see cref="PerfHitchRecord.RowCount"/> − 1.</param>
        /// <returns>The frame.</returns>
        public PerfFrame GetRecordFrame(int age, int row)
        {
            int index = IndexOfAge(age);
            if ((uint)row >= (uint)_records[index].RowCount) throw new ArgumentOutOfRangeException(nameof(row));
            return _recordFrames[index * WindowFrames + row];
        }

        /// <summary>Reads one slot's milliseconds for a frame of a held record that has slot times.</summary>
        /// <param name="age">The record's age, as for <see cref="GetRecord"/>.</param>
        /// <param name="row">The frame's row, as for <see cref="GetRecordFrame"/>.</param>
        /// <param name="slot">The slot.</param>
        /// <returns>The slot's milliseconds in that frame.</returns>
        public float GetRecordSlotMs(int age, int row, PerfSlot slot)
        {
            int index = IndexOfAge(age);
            PerfHitchRecord record = _records[index];
            if (!record.HasSlots) throw new InvalidOperationException("The record holds no slot times.");
            if ((uint)row >= (uint)record.RowCount) throw new ArgumentOutOfRangeException(nameof(row));
            return _recordSlots[(index * WindowFrames + row) * _slotCount + (int)slot];
        }

        /// <summary>Reads one counter for a frame of a held record that has slot times.</summary>
        /// <param name="age">The record's age, as for <see cref="GetRecord"/>.</param>
        /// <param name="row">The frame's row, as for <see cref="GetRecordFrame"/>.</param>
        /// <param name="counter">The counter.</param>
        /// <returns>The counter's value in that frame.</returns>
        public int GetRecordCounter(int age, int row, PerfCounter counter)
        {
            int index = IndexOfAge(age);
            PerfHitchRecord record = _records[index];
            if (!record.HasSlots || _counterCount == 0) throw new InvalidOperationException("The record holds no counters.");
            if ((uint)row >= (uint)record.RowCount) throw new ArgumentOutOfRangeException(nameof(row));
            if ((int)counter >= _counterCount) throw new ArgumentOutOfRangeException(nameof(counter));
            return _recordCounters[(index * WindowFrames + row) * _counterCount + (int)counter];
        }

        /// <summary>Frees the native blocks and discards an open window. Safe to call more than once.</summary>
        public void Dispose()
        {
            if (_isDisposed) return;

            ReleaseSlotBlock();
            _recordFrames.Dispose();
            _recordFrames = default;
            _recordCount = 0;
            _isWindowOpen = false;
            _isDisposed = true;
        }

        private void OpenWindow(in PerfFrame frame, float threshold)
        {
            _isWindowOpen = true;
            _framesSinceHitch = 0;
            _worstOffset = 0;
            _open = new PerfHitchRecord
            {
                HitchFrameIndex = frame.FrameIndex,
                ThresholdMs = threshold,
                WorstWallMs = frame.WallMs,
                HitchFrameCount = 1,
                GcCorrelated = frame.GcCollections > 0,
            };
        }

        private void JoinWindow(in PerfFrame frame)
        {
            _open.HitchFrameCount++;
            _open.GcCorrelated |= frame.GcCollections > 0;
            if (frame.WallMs <= _open.WorstWallMs) return;

            _open.WorstWallMs = frame.WallMs;
            _worstOffset = _framesSinceHitch;
        }

        private void CloseWindow(PerfFrameRing ring)
        {
            int rows = Math.Min(ring.Count, WindowFrames);
            int frameStart = _recordHead * WindowFrames;
            ring.CopyNewestFrames(_recordFrames, frameStart, rows);

            // The ring keeps slot columns only for frames committed since they were last allocated.
            // A record's counter rows come with its slot times, so a ring with other counters gives neither.
            bool hasSlots = _hasSlotBlock && ring.HasSlotColumns && ring.SlotFrameCount >= rows
                            && ring.CounterCount == _counterCount;
            if (hasSlots)
            {
                ring.CopyNewestSlotRows(_recordSlots, frameStart * _slotCount, rows);
                if (_counterCount > 0) ring.CopyNewestCounterRows(_recordCounters, frameStart * _counterCount, rows);
            }

            _open.RowCount = rows;
            _open.HitchRow = rows - 1 - _framesSinceHitch;
            _open.WorstRow = _open.HitchRow + _worstOffset;
            _open.HasSlots = hasSlots;
            if (hasSlots) RankTopSlots(ref _open, (frameStart + _open.WorstRow) * _slotCount);

            _records[_recordHead] = _open;
            _recordHead = (_recordHead + 1) % MaxRecords;
            if (_recordCount < MaxRecords) _recordCount++;
            RecordsClosed++;
            _isWindowOpen = false;
        }

        /// <summary>Keeps the costliest non-zero slots of one record row, by insertion into a three-entry ranking.</summary>
        private void RankTopSlots(ref PerfHitchRecord record, int rowStart)
        {
            record.TopSlotCount = 0;
            for (int slot = 0; slot < _slotCount; slot++)
            {
                float ms = _recordSlots[rowStart + slot];
                if (ms <= 0f) continue;

                int rank = record.TopSlotCount;
                while (rank > 0)
                {
                    record.GetTopSlot(rank - 1, out PerfSlot above, out float aboveMs);
                    if (aboveMs >= ms) break;
                    if (rank < TOP_SLOTS) record.SetTopSlot(rank, above, aboveMs);
                    rank--;
                }

                if (rank >= TOP_SLOTS) continue;

                record.SetTopSlot(rank, (PerfSlot)slot, ms);
                if (record.TopSlotCount < TOP_SLOTS) record.TopSlotCount++;
            }
        }

        private int IndexOfAge(int age)
        {
            if ((uint)age >= (uint)_recordCount) throw new ArgumentOutOfRangeException(nameof(age));

            int index = _recordHead - 1 - age;
            return index < 0 ? index + MaxRecords : index;
        }
    }
}
