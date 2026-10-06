using System;
using System.Collections.Generic;
using Unity.Collections;

namespace Diagnostics
{
    /// <summary>
    /// Exact statistics of the frame fields over whole phases, which outlast <see cref="PerfStore"/>'s ring. A phase is
    /// a frame-index range [first, end). While the recorder is <see cref="PerfStore.PhaseRecorder"/>, the store passes it
    /// each row once the row is final (<see cref="PerfStore.RowFinalAge"/> frames old, so its late frame timing is in).
    /// <para>
    /// Rows therefore arrive in frame order, well after their phase may have ended: a phase is summarized when the first
    /// row past its end arrives, or by <see cref="Close"/>. Only one phase gathers samples at a time, into native lists that
    /// grow without managed allocation and are summarized in place. Main thread only.
    /// </para>
    /// </summary>
    public sealed class PerfPhaseRecorder : IDisposable
    {
        private const int FIELD_COUNT = (int)PerfFrameField.Count;
        private const int INITIAL_SAMPLE_CAPACITY = 16384;

        /// <summary>End of a phase that has not ended yet.</summary>
        private const int OPEN_END = int.MaxValue;

        private readonly List<int> _firstFrames;
        private readonly List<int> _endFrames;
        private readonly List<PerfPhaseSummary> _completed;
        private readonly NativeList<float>[] _samples = new NativeList<float>[FIELD_COUNT];

        private int _gcCollections;
        private bool _isClosed;
        private bool _isDisposed;

        /// <summary>Allocates the sample lists.</summary>
        /// <param name="expectedPhaseCount">Phases expected, to size the bookkeeping lists.</param>
        public PerfPhaseRecorder(int expectedPhaseCount)
        {
            _firstFrames = new List<int>(expectedPhaseCount);
            _endFrames = new List<int>(expectedPhaseCount);
            _completed = new List<PerfPhaseSummary>(expectedPhaseCount);
            for (int i = 0; i < FIELD_COUNT; i++)
                _samples[i] = new NativeList<float>(INITIAL_SAMPLE_CAPACITY, Allocator.Persistent);
        }

        /// <summary>Phases begun.</summary>
        public int PhaseCount => _firstFrames.Count;

        /// <summary>Phases summarized; their ordinals are 0 to this − 1, in the order they began.</summary>
        public int CompletedCount => _completed.Count;

        /// <summary>Whether the newest phase has begun and not ended.</summary>
        public bool IsPhaseOpen => PhaseCount > 0 && _endFrames[PhaseCount - 1] == OPEN_END;

        /// <summary>Whether <see cref="Close"/> has run; later phases and rows are ignored.</summary>
        public bool IsClosed => _isClosed;

        /// <summary>Whether <see cref="Dispose"/> has run.</summary>
        public bool IsDisposed => _isDisposed;

        /// <summary>Begins a phase at a frame, ending an open phase there.</summary>
        /// <param name="firstFrameIndex">The phase's first frame (<see cref="PerfFrame.FrameIndex"/>); not before the previous phase's end.</param>
        public void BeginPhase(int firstFrameIndex)
        {
            if (_isClosed) return;

            EndPhase(firstFrameIndex);
            if (PhaseCount > 0 && firstFrameIndex < _endFrames[PhaseCount - 1])
                throw new ArgumentOutOfRangeException(nameof(firstFrameIndex), "A phase cannot begin before the previous one ended.");

            _firstFrames.Add(firstFrameIndex);
            _endFrames.Add(OPEN_END);
        }

        /// <summary>Ends the open phase before a frame; a no-op when none is open.</summary>
        /// <param name="endFrameIndex">The first frame after the phase; an end before its first frame leaves it empty.</param>
        public void EndPhase(int endFrameIndex)
        {
            if (!IsPhaseOpen) return;

            int last = PhaseCount - 1;
            _endFrames[last] = Math.Max(endFrameIndex, _firstFrames[last]);
        }

        /// <summary>
        /// Adds one final row to the phase holding its frame, first summarizing every earlier phase the row is past. Rows
        /// before the first phase, between phases or after the last are ignored.
        /// </summary>
        /// <param name="frame">The row; rows must arrive in frame order.</param>
        public void Add(in PerfFrame frame)
        {
            if (_isClosed) return;

            int frameIndex = frame.FrameIndex;
            while (CompletedCount < PhaseCount && frameIndex >= _endFrames[CompletedCount])
                CompleteOldest();

            if (CompletedCount >= PhaseCount || frameIndex < _firstFrames[CompletedCount]) return;

            _gcCollections += frame.GcCollections;
            for (int field = 0; field < FIELD_COUNT; field++)
            {
                if (PerfFrameRing.TryReadField(frame, (PerfFrameField)field, out float value))
                    _samples[field].Add(value);
            }
        }

        /// <summary>Summarizes every phase not yet summarized with the rows that arrived, an open one included, and stops recording.</summary>
        public void Close()
        {
            if (_isClosed || _isDisposed) return;

            EndPhase(OPEN_END - 1);
            while (CompletedCount < PhaseCount)
                CompleteOldest();
            _isClosed = true;
        }

        /// <summary>Reads a summarized phase.</summary>
        /// <param name="ordinal">0 for the first phase begun, up to <see cref="CompletedCount"/> − 1.</param>
        /// <returns>Its statistics.</returns>
        public PerfPhaseSummary GetCompleted(int ordinal) => _completed[ordinal];

        /// <summary>Frees the sample lists. Safe to call more than once.</summary>
        public void Dispose()
        {
            if (_isDisposed) return;

            for (int i = 0; i < FIELD_COUNT; i++)
                _samples[i].Dispose();
            _isClosed = true;
            _isDisposed = true;
        }

        /// <summary>Summarizes the phase gathering samples and clears the lists for the next.</summary>
        private void CompleteOldest()
        {
            PerfPhaseSummary summary = new PerfPhaseSummary { GcCollections = _gcCollections };
            for (int field = 0; field < FIELD_COUNT; field++)
            {
                NativeList<float> samples = _samples[field];
                summary.Set((PerfFrameField)field, PerfWindowStats.Summarize(samples.AsArray(), samples.Length));
                samples.Clear();
            }

            summary.FrameCount = summary.Wall.Count;
            _completed.Add(summary);
            _gcCollections = 0;
        }
    }
}
