using System;
using System.IO;
using System.Text;
using System.Threading;
using Helpers.UI;
using UnityEngine;

namespace Diagnostics
{
    /// <summary>
    /// Streams a recording session to disk: one CSV row per frame (frame fields, every slot, every counter) and one CSV
    /// file per hitch record, written by a dedicated background thread.
    /// <para>
    /// <b>Main thread.</b> <see cref="OnFrameCommitted"/> copies the row that just became final
    /// (<see cref="PerfStore.RowFinalAge"/>) and any newly closed hitch record into pooled blocks, allocating nothing; when
    /// every block is waiting to be written, the rows or the hitch window are dropped and counted rather than allocating more.
    /// </para>
    /// <para>
    /// <b>Writer thread.</b> Not a ThreadPool thread, so it never delays the chunk loads and saves queued there. It formats
    /// rows without allocating, because the store's allocation figure is the process-wide heap growth and garbage from this
    /// thread would land in the frames it records; only opening a file (a new part or a hitch file) allocates. A session is
    /// split into parts of at most the part size and stops writing at the session size; a write that fails stops the
    /// session's export and is reported once.
    /// </para>
    /// <para>Files are plain ASCII with invariant number formatting; an empty cell means the frame has no value.</para>
    /// </summary>
    public sealed class PerfSessionExporter : IDisposable
    {
        /// <summary>The folder under <c>Application.persistentDataPath</c> that sessions are written into.</summary>
        public const string FolderName = "PerfLogs";

        /// <summary>Prefix of every file a session writes.</summary>
        public const string FilePrefix = "PerfSession_";

        /// <summary>Extension of every file a session writes.</summary>
        public const string FileExtension = ".csv";

        /// <summary>The frame columns, before one column per slot and one per counter.</summary>
        public const string FrameColumns =
            "frame,wall_ms,cpu_ms,gc_alloc_bytes,gc_state,gc_collections,gpu_ms,render_thread_ms,present_wait_ms";

        /// <summary>Number of <see cref="FrameColumns"/>.</summary>
        public const int FrameColumnCount = 9;

        /// <summary>Starts the summary line above a hitch file's header.</summary>
        public const string HitchSummaryPrefix = "# hitch";

        /// <summary>The hitch file's extra last column: <see cref="HitchMark"/> and/or <see cref="WorstMark"/>.</summary>
        public const string MarkColumn = "mark";

        /// <summary>Marks the row of the frame that opened a hitch window.</summary>
        public const string HitchMark = "hitch";

        /// <summary>Marks the row of the window's slowest hitch frame.</summary>
        public const string WorstMark = "worst";

        /// <summary>Default megabytes after which a session continues in a new part file.</summary>
        public const int DefaultPartMb = 64;

        /// <summary>Default megabytes across a session's files after which it writes nothing more.</summary>
        public const int DefaultSessionMb = 1024;

        /// <summary>Bytes per megabyte, for the size settings.</summary>
        public const long BytesPerMegabyte = 1024L * 1024L;

        /// <summary>Blocks in the pool; when all are waiting for the writer, new rows and hitch windows are dropped.</summary>
        public const int BlockCount = 16;

        /// <summary>How long <see cref="Close"/> waits for the writer to finish.</summary>
        public const int CloseTimeoutMs = 2000;

        private const int MS_DECIMALS = 3;
        private const int LINE_CAPACITY = 8192;
        private const int WRITE_BUFFER_BYTES = 65536;
        private const char SEPARATOR = ',';
        private const char MARK_SEPARATOR = ' ';
        private const string PART_SUFFIX = "_part";
        private const string HITCH_SUFFIX = "_hitch";
        private const string FRAME_SUFFIX = "_frame";
        private const string SLOT_COLUMN_SUFFIX = "_ms";
        private const string LOG_TAG = "[PerfExport] ";

        private readonly string _directory;
        private readonly string _sessionName;
        private readonly long _partMaxBytes;
        private readonly long _sessionMaxBytes;
        private readonly int _slotCount;
        private readonly int _counterCount;
        private readonly string _header;
        private readonly string _hitchHeader;
        private readonly string[] _gcStateNames;
        private readonly string[] _slotNames;

        private readonly object _lock = new object();
        private readonly PerfExportBlock[] _free = new PerfExportBlock[BlockCount];
        private readonly PerfExportBlock[] _pending = new PerfExportBlock[BlockCount];
        private readonly AutoResetEvent _signal = new AutoResetEvent(false);
        private readonly Thread _thread;

        // Writer-thread state.
        private readonly StringBuilder _line = new StringBuilder(LINE_CAPACITY);
        private char[] _chars = new char[LINE_CAPACITY];
        private StreamWriter _writer;
        private long _partBytes;
        private int _partNumber;
        private bool _hasFailed;
        private bool _capLogged;

        /// <summary>Rows of the block being written that reached the file, so a failure drops only the rest.</summary>
        private int _blockRowsWritten;

        // Main-thread state.
        private PerfExportBlock _current;
        private int _lastFrameIndex;
        private PerfHitchDetector _hitches;
        private int _hitchRecordsSeen;
        private int _hitchNumber;
        private bool _isClosed;

        private int _freeCount;
        private int _pendingHead;
        private int _pendingCount;
        private volatile bool _stopRequested;
        private volatile string _currentPath;
        private volatile string _failureMessage;
        private volatile bool _capReached;
        private long _rowsWritten;
        private long _rowsDropped;
        private long _bytesWritten;
        private int _hitchFilesWritten;
        private int _hitchesDropped;

        /// <summary>Allocates the block pool and starts the writer thread; no file exists until the first block is written.</summary>
        /// <param name="directory">The folder the session's files go into; created when first written.</param>
        /// <param name="sessionName">The files' common name, without extension.</param>
        /// <param name="partMaxBytes">Bytes after which the session continues in a new part file.</param>
        /// <param name="sessionMaxBytes">Bytes across every file of the session after which nothing more is written.</param>
        /// <param name="slotCount">Slot columns per row.</param>
        /// <param name="counterCount">Counter columns per row.</param>
        /// <param name="lastFrameIndex">The newest frame already in the store; it and older frames are not exported.</param>
        /// <param name="hitches">The store's hitch detector, whose records closed from now on are exported; null for none yet.</param>
        public PerfSessionExporter(string directory, string sessionName, long partMaxBytes, long sessionMaxBytes, int slotCount,
            int counterCount, int lastFrameIndex, PerfHitchDetector hitches)
        {
            if (partMaxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(partMaxBytes));
            if (sessionMaxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(sessionMaxBytes));

            _directory = directory ?? throw new ArgumentNullException(nameof(directory));
            _sessionName = sessionName ?? throw new ArgumentNullException(nameof(sessionName));
            _partMaxBytes = partMaxBytes;
            _sessionMaxBytes = sessionMaxBytes;
            _slotCount = slotCount;
            _counterCount = counterCount;
            _lastFrameIndex = lastFrameIndex;
            _hitches = hitches;
            _hitchRecordsSeen = hitches?.RecordsClosed ?? 0;

            _gcStateNames = Enum.GetNames(typeof(PerfGcState));
            _slotNames = new string[slotCount];
            for (int i = 0; i < slotCount; i++)
                _slotNames[i] = ((PerfSlot)i).ToString();

            _header = BuildHeader(slotCount, counterCount);
            _hitchHeader = _header + SEPARATOR + MarkColumn;

            for (int i = 0; i < BlockCount; i++)
                _free[i] = new PerfExportBlock(slotCount, counterCount);
            _freeCount = BlockCount;

            _thread = new Thread(WriterLoop)
            {
                IsBackground = true,
                Priority = System.Threading.ThreadPriority.BelowNormal,
                Name = "PerfSessionExporter",
            };
            _thread.Start();
        }

        /// <summary>The session's common file name, without extension.</summary>
        public string SessionName => _sessionName;

        /// <summary>The folder the session writes into.</summary>
        public string Directory => _directory;

        /// <summary>The session file being written, or null before the first row is written.</summary>
        public string CurrentPath => _currentPath;

        /// <summary>Session rows written to disk.</summary>
        public long RowsWritten => Interlocked.Read(ref _rowsWritten);

        /// <summary>Session rows not written: dropped while every block waited for the writer, after the size cap, or after a failure.</summary>
        public long RowsDropped => Interlocked.Read(ref _rowsDropped);

        /// <summary>Bytes written across every file of the session.</summary>
        public long BytesWritten => Interlocked.Read(ref _bytesWritten);

        /// <summary>Hitch files written.</summary>
        public int HitchFilesWritten => Volatile.Read(ref _hitchFilesWritten);

        /// <summary>Hitch records not written: dropped while every block waited for the writer, after the size cap, or after a failure.</summary>
        public int HitchesDropped => Volatile.Read(ref _hitchesDropped);

        /// <summary>Whether the session reached its size cap and stopped writing.</summary>
        public bool CapReached => _capReached;

        /// <summary>Why writing stopped after a failure, or null.</summary>
        public string FailureMessage => _failureMessage;

        /// <summary>Whether <see cref="Close"/> has run.</summary>
        public bool IsClosed => _isClosed;

        /// <summary>
        /// Queues the row that just became final and, when the detector closed a record since the last call, that record.
        /// Call once per commit, after the hitch detector has seen the frame. Main thread only.
        /// </summary>
        /// <param name="ring">The store's ring, just committed to.</param>
        /// <param name="hitches">The store's hitch detector, or null.</param>
        public void OnFrameCommitted(PerfFrameRing ring, PerfHitchDetector hitches)
        {
            if (_isClosed) return;

            if (ring.Count > PerfStore.RowFinalAge) AddRow(ring, PerfStore.RowFinalAge);
            if (hitches == null) return;

            // A replaced detector counts from 0 again; only records it closes from now on are new.
            if (hitches != _hitches)
            {
                _hitches = hitches;
                _hitchRecordsSeen = hitches.RecordsClosed;
                return;
            }

            if (hitches.RecordsClosed == _hitchRecordsSeen) return;

            _hitchRecordsSeen = hitches.RecordsClosed;
            QueueHitch(hitches);
        }

        /// <summary>
        /// Queues the rows not yet final — the newest frames, whose late timings may never arrive — then lets the writer
        /// finish and waits for it up to <see cref="CloseTimeoutMs"/>. Main thread only; later calls do nothing.
        /// </summary>
        /// <param name="ring">The store's ring, for the newest rows; null to skip them.</param>
        public void Close(PerfFrameRing ring)
        {
            if (_isClosed) return;

            if (ring != null && !ring.IsDisposed)
            {
                for (int age = Math.Min(PerfStore.RowFinalAge, ring.Count) - 1; age >= 0; age--)
                    AddRow(ring, age);
            }

            if (_current != null)
            {
                Submit(_current);
                _current = null;
            }

            _isClosed = true;
            _stopRequested = true;
            _signal.Set();
            if (_thread.Join(CloseTimeoutMs)) _signal.Close();
            else Debug.LogWarning($"{LOG_TAG}The writer did not finish within {CloseTimeoutMs} ms; the session file may be incomplete.");
        }

        /// <summary>Closes the session without the rows not yet final.</summary>
        public void Dispose() => Close(null);

        #region Main thread

        private void AddRow(PerfFrameRing ring, int age)
        {
            PerfFrame frame = ring.GetFrame(age);
            if (frame.FrameIndex <= _lastFrameIndex) return;

            _lastFrameIndex = frame.FrameIndex;
            if (_current == null && !TryRent(out _current))
            {
                Interlocked.Increment(ref _rowsDropped);
                return;
            }

            int row = _current.RowCount++;
            _current.Frames[row] = frame;
            bool hasColumns = age < ring.SlotFrameCount && ring.CounterCount == _counterCount;
            _current.HasColumns[row] = hasColumns;
            if (hasColumns)
            {
                ring.CopySlotRow(age, _current.SlotMs, row * _slotCount);
                ring.CopyCounterRow(age, _current.Counters, row * _counterCount);
            }

            if (_current.RowCount < PerfExportBlock.Capacity) return;

            Submit(_current);
            _current = null;
        }

        private void QueueHitch(PerfHitchDetector hitches)
        {
            _hitchNumber++;
            if (!TryRent(out PerfExportBlock block))
            {
                Interlocked.Increment(ref _hitchesDropped);
                return;
            }

            PerfHitchRecord record = hitches.GetRecord(0);
            block.IsHitch = true;
            block.Hitch = record;
            block.HitchNumber = _hitchNumber;
            block.RowCount = record.RowCount;
            for (int row = 0; row < record.RowCount; row++)
            {
                block.Frames[row] = hitches.GetRecordFrame(0, row);
                block.HasColumns[row] = record.HasSlots;
                if (!record.HasSlots) continue;

                for (int slot = 0; slot < _slotCount; slot++)
                    block.SlotMs[row * _slotCount + slot] = hitches.GetRecordSlotMs(0, row, (PerfSlot)slot);
                for (int counter = 0; counter < _counterCount; counter++)
                    block.Counters[row * _counterCount + counter] = hitches.GetRecordCounter(0, row, (PerfCounter)counter);
            }

            Submit(block);
        }

        private bool TryRent(out PerfExportBlock block)
        {
            lock (_lock)
            {
                if (_freeCount == 0)
                {
                    block = null;
                    return false;
                }

                block = _free[--_freeCount];
            }

            block.RowCount = 0;
            block.IsHitch = false;
            return true;
        }

        private void Submit(PerfExportBlock block)
        {
            lock (_lock)
            {
                _pending[(_pendingHead + _pendingCount) % BlockCount] = block;
                _pendingCount++;
            }

            _signal.Set();
        }

        #endregion

        #region Writer thread

        private void WriterLoop()
        {
            try
            {
                while (true)
                {
                    _signal.WaitOne();

                    // Read before draining: once set, every block was submitted before it, so this drain is the last.
                    bool isStopping = _stopRequested;
                    while (TryDequeue(out PerfExportBlock block))
                    {
                        Write(block);
                        Return(block);
                    }

                    if (isStopping) break;
                }
            }
            finally
            {
                CloseWriter();
            }
        }

        private bool TryDequeue(out PerfExportBlock block)
        {
            lock (_lock)
            {
                if (_pendingCount == 0)
                {
                    block = null;
                    return false;
                }

                block = _pending[_pendingHead];
                _pending[_pendingHead] = null;
                _pendingHead = (_pendingHead + 1) % BlockCount;
                _pendingCount--;
                return true;
            }
        }

        private void Return(PerfExportBlock block)
        {
            lock (_lock)
                _free[_freeCount++] = block;
        }

        private void Write(PerfExportBlock block)
        {
            if (_hasFailed || _capReached)
            {
                CountDropped(block, block.RowCount);
                return;
            }

            _blockRowsWritten = 0;
            try
            {
                if (block.IsHitch) WriteHitch(block);
                else WriteRows(block);
            }
            catch (Exception exception)
            {
                // Any failure ends the session's export rather than the thread, which the main thread keeps signaling.
                _hasFailed = true;
                _failureMessage = exception.Message;
                CloseWriter();
                CountDropped(block, block.RowCount - _blockRowsWritten);
                Debug.LogWarning($"{LOG_TAG}Writing stopped: {exception.Message}");
            }
        }

        /// <summary>Counts a block that will not be written: its rows for session rows, one record for a hitch window.</summary>
        private void CountDropped(PerfExportBlock block, int rows)
        {
            if (block.IsHitch) Interlocked.Increment(ref _hitchesDropped);
            else Interlocked.Add(ref _rowsDropped, rows);
        }

        private void WriteRows(PerfExportBlock block)
        {
            _blockRowsWritten = 0;
            for (int row = 0; row < block.RowCount; row++)
            {
                if (HasReachedCap(block.RowCount - row)) break;
                if (_writer == null || _partBytes >= _partMaxBytes) OpenNextPart();

                _line.Clear();
                AppendRow(block, row);
                _partBytes += WriteLine(_writer);
                _blockRowsWritten++;
                Interlocked.Increment(ref _rowsWritten);
            }

            _writer?.Flush();
        }

        private void WriteHitch(PerfExportBlock block)
        {
            if (HasReachedCap(0))
            {
                CountDropped(block, 0);
                return;
            }

            System.IO.Directory.CreateDirectory(_directory);
            PerfHitchRecord record = block.Hitch;
            string path = Path.Combine(_directory, _sessionName + HITCH_SUFFIX + block.HitchNumber.ToString("D3")
                                                  + FRAME_SUFFIX + record.HitchFrameIndex + FileExtension);
            using (StreamWriter writer = CreateWriter(path))
            {
                _line.Clear();
                AppendHitchSummary(record);
                WriteLine(writer);
                _line.Clear().Append(_hitchHeader);
                WriteLine(writer);

                for (int row = 0; row < block.RowCount; row++)
                {
                    _line.Clear();
                    AppendRow(block, row);
                    _line.Append(SEPARATOR);
                    if (row == record.HitchRow) _line.Append(HitchMark);
                    if (row == record.WorstRow)
                    {
                        if (row == record.HitchRow) _line.Append(MARK_SEPARATOR);
                        _line.Append(WorstMark);
                    }

                    WriteLine(writer);
                }
            }

            Interlocked.Increment(ref _hitchFilesWritten);
        }

        /// <summary>Whether the session is at its size cap, logging it once and counting the rows that will not be written.</summary>
        private bool HasReachedCap(int rowsAffected)
        {
            if (Interlocked.Read(ref _bytesWritten) < _sessionMaxBytes) return false;

            _capReached = true;
            Interlocked.Add(ref _rowsDropped, rowsAffected);
            if (!_capLogged)
            {
                _capLogged = true;
                Debug.LogWarning($"{LOG_TAG}Session reached its {_sessionMaxBytes / BytesPerMegabyte} MB cap; writing stopped.");
            }

            return true;
        }

        private void OpenNextPart()
        {
            CloseWriter();
            System.IO.Directory.CreateDirectory(_directory);
            _partNumber++;
            string name = _partNumber == 1 ? _sessionName : _sessionName + PART_SUFFIX + _partNumber.ToString("D2");
            string path = Path.Combine(_directory, name + FileExtension);
            _writer = CreateWriter(path);
            _currentPath = path;
            _partBytes = 0;
            _line.Clear().Append(_header);
            _partBytes += WriteLine(_writer);
        }

        private static StreamWriter CreateWriter(string path) =>
            new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, WRITE_BUFFER_BYTES),
                new UTF8Encoding(false), WRITE_BUFFER_BYTES);

        private void CloseWriter()
        {
            try
            {
                _writer?.Dispose();
            }
            catch (IOException)
            {
                // The data is lost either way; the failure, if any, was already reported.
            }

            _writer = null;
        }

        /// <summary>Writes the line being built plus a newline through a reused buffer, and counts its bytes (the text is ASCII).</summary>
        /// <returns>The bytes written.</returns>
        private int WriteLine(StreamWriter writer)
        {
            _line.Append('\n');
            int length = _line.Length;
            if (_chars.Length < length) _chars = new char[length * 2];

            _line.CopyTo(0, _chars, 0, length);
            writer.Write(_chars, 0, length);
            Interlocked.Add(ref _bytesWritten, length);
            return length;
        }

        private void AppendRow(PerfExportBlock block, int row)
        {
            PerfFrame frame = block.Frames[row];
            _line.AppendInteger(frame.FrameIndex).Append(SEPARATOR);
            AppendMs(frame.WallMs).Append(SEPARATOR);
            AppendMs(frame.CpuMs).Append(SEPARATOR);
            if (frame.GcState == PerfGcState.Measured) _line.AppendInteger(frame.GcAllocBytes);
            _line.Append(SEPARATOR);
            _line.Append(_gcStateNames[(int)frame.GcState]).Append(SEPARATOR);
            _line.AppendInteger(frame.GcCollections).Append(SEPARATOR);
            AppendMs(frame.GpuMs).Append(SEPARATOR);
            AppendMs(frame.RenderThreadMs).Append(SEPARATOR);
            AppendMs(frame.PresentWaitMs);

            bool hasColumns = block.HasColumns[row];
            int slotStart = row * _slotCount;
            for (int slot = 0; slot < _slotCount; slot++)
            {
                _line.Append(SEPARATOR);
                if (hasColumns) AppendMs(block.SlotMs[slotStart + slot]);
            }

            int counterStart = row * _counterCount;
            for (int counter = 0; counter < _counterCount; counter++)
            {
                _line.Append(SEPARATOR);
                if (hasColumns) _line.AppendInteger(block.Counters[counterStart + counter]);
            }
        }

        private void AppendHitchSummary(PerfHitchRecord record)
        {
            _line.Append(HitchSummaryPrefix);
            _line.Append(" frame=").AppendInteger(record.HitchFrameIndex);
            _line.Append(" threshold_ms=");
            AppendMs(record.ThresholdMs);
            _line.Append(" worst_ms=");
            AppendMs(record.WorstWallMs);
            _line.Append(" hitch_frames=").AppendInteger(record.HitchFrameCount);
            _line.Append(" gc_correlated=").Append(record.GcCorrelated ? "true" : "false");
            _line.Append(" top=");
            for (int rank = 0; rank < record.TopSlotCount; rank++)
            {
                record.GetTopSlot(rank, out PerfSlot slot, out float ms);
                if (rank > 0) _line.Append(';');
                _line.Append(_slotNames[(int)slot]).Append(':');
                AppendMs(ms);
            }
        }

        /// <summary>Appends milliseconds to three decimals, or nothing for NaN — the empty cell of a frame without the value.</summary>
        private StringBuilder AppendMs(float ms) => float.IsNaN(ms) ? _line : _line.AppendFixed(ms, MS_DECIMALS);

        private static string BuildHeader(int slotCount, int counterCount)
        {
            StringBuilder header = new StringBuilder(FrameColumns);
            for (int i = 0; i < slotCount; i++)
                header.Append(SEPARATOR).Append(((PerfSlot)i).ToString()).Append(SLOT_COLUMN_SUFFIX);
            for (int i = 0; i < counterCount; i++)
                header.Append(SEPARATOR).Append(((PerfCounter)i).ToString());
            return header.ToString();
        }

        #endregion
    }
}
