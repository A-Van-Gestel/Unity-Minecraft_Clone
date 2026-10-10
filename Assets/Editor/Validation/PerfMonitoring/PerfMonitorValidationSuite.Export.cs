using System;
using System.Globalization;
using System.IO;
using Diagnostics;
using Editor.Validation.Framework;
using Debug = UnityEngine.Debug;
using Random = System.Random;

namespace Editor.Validation.PerfMonitoring
{
    /// <summary>
    /// The export half of the suite: <see cref="PerfPhaseRecorder"/>'s phase-wide statistics, fed by <see cref="PerfStore"/>
    /// with each row once it is final, and the tier floor benchmarks raise — driven through the store with synthetic readings.
    /// </summary>
    public static partial class PerfMonitorValidationSuite
    {
        /// <summary>Phase A is [5, 10), B is [10, 20) and C is [21, 30); frame 20 lies between phases.</summary>
        private const int PHASE_A_FIRST = 5;

        private const int PHASE_B_FIRST = 10;
        private const int PHASE_B_END = 20;
        private const int PHASE_C_FIRST = 21;
        private const int PHASE_C_END = 30;

        /// <summary>The first frame whose collection count is 1, so it records one collection.</summary>
        private const int COLLECTION_FRAME = 12;

        /// <summary>The frame whose late timing arrives when it is exactly the oldest row a timing can reach.</summary>
        private const int LATE_TIMING_FRAME = 23;

        private const float LATE_GPU_MS = 7.5f;
        private const float LATE_OTHER_MS = 1.5f;

        /// <summary>The close scenario's phase and the frames committed in it.</summary>
        private const int CLOSE_PHASE_FIRST = 100;

        private const int CLOSE_PHASE_FRAMES = 21;

        /// <summary>Frames in the long phase — more than twice the ring holds.</summary>
        private const int LONG_PHASE_FRAMES = 5000;

        private const int PERCENT_SCALE = 100;

        /// <summary>
        /// The age at which a row must be final: one past the oldest row a late timing reaches. Derived from the suite's
        /// own back-fill limit rather than <see cref="PerfStore.RowFinalAge"/>, so a wrong constant cannot move the test with it.
        /// </summary>
        private const int FINAL_ROW_AGE = BACKFILL_MAX_AGE + 1;

        /// <summary>Frames committed before the session opens, which it must not export.</summary>
        private const int PRE_SESSION_FRAMES = 5;

        private const int SESSION_FRAMES = 300;

        /// <summary>Exported values cycle through multiples of 1/8 ms, exact in a float and in three decimals.</summary>
        private const double EIGHTH_MS = 0.125;

        private const int EXPORT_VALUE_CYCLE = 40;
        private const int EXPORT_ALLOC_BYTES = 2048;
        private const int EXPORT_COLLECTION_FRAME = 50;
        private const int EXPORT_TIMING_FRAME = 100;
        private const float EXPORT_GPU_MS = 2.5f;
        private const float EXPORT_RENDER_THREAD_MS = 1.25f;
        private const float EXPORT_PRESENT_WAIT_MS = 0.5f;

        /// <summary>Normal frames before the hitch: more than a window's frames before it, so the record is full.</summary>
        private const int HITCH_LEAD_FRAMES = PerfHitchDetector.FramesBefore + 10;

        /// <summary>The hitch frame's <c>Tick</c> time, its costliest slot.</summary>
        private const double HITCH_TICK_MS = 80.0;

        /// <summary>Sizes that no test session reaches, and small ones that force parts and the cap.</summary>
        private const long LARGE_EXPORT_BYTES = 1L << 30;

        private const long SMALL_PART_BYTES = 4096;
        private const long SMALL_SESSION_BYTES = 20000;

        /// <summary>More than any row is long; a part or session may pass its size by one row.</summary>
        private const long MAX_ROW_BYTES = 2048;

        private const int LIMIT_FRAMES = 400;

        private static bool RunB32PhaseRouting() => WithFreshStore(() =>
        {
            PerfPhaseRecorder recorder = new PerfPhaseRecorder(3);
            PerfPhaseRecorder closing = new PerfPhaseRecorder(1);
            try
            {
                PerfStore.PhaseRecorder = recorder;
                recorder.BeginPhase(PHASE_A_FIRST);
                recorder.BeginPhase(PHASE_B_FIRST);
                recorder.EndPhase(PHASE_B_END);
                recorder.BeginPhase(PHASE_C_FIRST);
                recorder.EndPhase(PHASE_C_END);
                bool ok = Check("A phase cannot begin before the previous one ended", Throws(() => recorder.BeginPhase(PHASE_C_END - 1)));

                int frame = CommitPhaseFrames(1, PHASE_B_FIRST + FINAL_ROW_AGE - 1);
                ok &= Check("No phase completes before the row past its end is final", recorder.CompletedCount == 0);
                frame = CommitPhaseFrames(frame, 1);
                ok &= Check("A phase completes on the commit that makes the row past its end final", recorder.CompletedCount == 1);

                // The late frame is now as old as the oldest row a timing still reaches (B13 pins that age).
                frame = CommitPhaseFrames(frame, LATE_TIMING_FRAME + BACKFILL_MAX_AGE - (frame - 1));
                ok &= Check("A timing reaches the row one frame short of final",
                    PerfStore.SubmitFrameTiming(InsideRow(LATE_TIMING_FRAME), LATE_GPU_MS, LATE_OTHER_MS, LATE_OTHER_MS));
                CommitPhaseFrames(frame, PHASE_C_END + FINAL_ROW_AGE + 1 - frame);
                ok &= Check("Every phase completes once the row past its end is final", recorder.CompletedCount == 3);

                PerfPhaseSummary a = recorder.GetCompleted(0);
                PerfPhaseSummary b = recorder.GetCompleted(1);
                PerfPhaseSummary c = recorder.GetCompleted(2);
                ok &= Check("Each phase holds its own frames; those before, between and after phases are ignored",
                    a.FrameCount == PHASE_B_FIRST - PHASE_A_FIRST && b.FrameCount == PHASE_B_END - PHASE_B_FIRST
                    && c.FrameCount == PHASE_C_END - PHASE_C_FIRST);
                ok &= Check("Each summary carries its phase's frame range, an end set by the next phase's begin included",
                    a.FirstFrame == PHASE_A_FIRST && a.EndFrame == PHASE_B_FIRST
                    && b.FirstFrame == PHASE_B_FIRST && b.EndFrame == PHASE_B_END
                    && c.FirstFrame == PHASE_C_FIRST && c.EndFrame == PHASE_C_END);
                ok &= Check("Phase statistics come from the phase's own rows",
                    ExactValue.Equal(a.Wall.Max, PhaseFrameWallMs(PHASE_B_FIRST - 1))
                    && ExactValue.Equal(a.Wall.P50, PhaseFrameWallMs(PHASE_A_FIRST + 2))
                    && ExactValue.Equal(c.Wall.Max, PhaseFrameWallMs(PHASE_C_END - 1)));
                ok &= Check("Collections are summed per phase and a collection frame has no allocation figure",
                    a.GcCollections == 0 && b.GcCollections == 1 && a.GcAllocKb.Count == a.FrameCount
                    && b.GcAllocKb.Count == b.FrameCount - 1);
                ok &= Check("The phase holds the late timing; rows without one add no GPU sample",
                    c.Gpu.Count == 1 && ExactValue.Equal(c.Gpu.Max, LATE_GPU_MS) && c.RenderThread.Count == 1
                    && a.Gpu.Count == 0 && b.Gpu.Count == 0);

                PerfStore.PhaseRecorder = closing;
                closing.BeginPhase(CLOSE_PHASE_FIRST);
                CommitPhaseFrames(CLOSE_PHASE_FIRST, CLOSE_PHASE_FRAMES);
                closing.Close();
                int finalRows = CLOSE_PHASE_FRAMES - FINAL_ROW_AGE;
                ok &= Check("Close summarizes an open phase with the rows that are final",
                    closing.IsClosed && closing.CompletedCount == 1 && closing.GetCompleted(0).FrameCount == finalRows);
                ok &= Check($"Close ends an open phase one past the last row it received (end {closing.GetCompleted(0).EndFrame}, expected {CLOSE_PHASE_FIRST + finalRows})",
                    closing.GetCompleted(0).EndFrame == CLOSE_PHASE_FIRST + finalRows);
                CommitPhaseFrames(CLOSE_PHASE_FIRST + CLOSE_PHASE_FRAMES, PerfStore.RowFinalAge);
                closing.BeginPhase(CLOSE_PHASE_FIRST + CLOSE_PHASE_FRAMES);
                ok &= Check("A closed recorder ignores later rows and phases",
                    closing.PhaseCount == 1 && closing.GetCompleted(0).FrameCount == finalRows);

                InvokeStorePrivate("DomainReset");
                ok &= Check("DomainReset detaches the recorder", PerfStore.PhaseRecorder == null);
                return ok;
            }
            finally
            {
                PerfStore.PhaseRecorder = null;
                recorder.Dispose();
                closing.Dispose();
            }
        });

        private static bool RunB33PhaseStatsBeyondRing() => WithFreshStore(() =>
        {
            PerfPhaseRecorder recorder = new PerfPhaseRecorder(1);
            try
            {
                PerfStore.PhaseRecorder = recorder;
                recorder.BeginPhase(1);

                Random random = new Random(STATS_SEED);
                float[] expected = new float[LONG_PHASE_FRAMES];
                for (int i = 0; i < LONG_PHASE_FRAMES; i++)
                {
                    long ticks = TicksForMs(random.NextDouble() * STATS_MAX_MS);
                    expected[i] = SlotMs(ticks);
                    PerfStore.CommitFrame(Readings(ticks, i + 1));
                }

                recorder.EndPhase(LONG_PHASE_FRAMES + 1);
                for (int i = 0; i <= PerfStore.RowFinalAge; i++)
                    PerfStore.CommitFrame(Readings(1, LONG_PHASE_FRAMES + 1 + i));

                bool ok = Check("The phase completes after its last row is final", recorder.CompletedCount == 1);
                if (!ok) return false;

                double sum = 0;
                foreach (float value in expected) sum += value;
                float[] sorted = (float[])expected.Clone();
                Array.Sort(sorted);

                PerfWindowSummary wall = recorder.GetCompleted(0).Wall;
                ok &= Check($"Every one of the {LONG_PHASE_FRAMES} frames counts, more than the ring's {PerfStore.RingCapacity}",
                    wall.Count == LONG_PHASE_FRAMES && LONG_PHASE_FRAMES > PerfStore.RingCapacity);
                ok &= Check("Worst, mean, p50 and p99 match a sorted copy of every frame",
                    ExactValue.Equal(wall.Max, sorted[LONG_PHASE_FRAMES - 1])
                    && ExactValue.Equal(wall.Mean, (float)(sum / LONG_PHASE_FRAMES))
                    && ExactValue.Equal(wall.P50, sorted[OracleRankIndex(LONG_PHASE_FRAMES, PerfWindowStats.MedianPercentile)])
                    && ExactValue.Equal(wall.P99, sorted[OracleRankIndex(LONG_PHASE_FRAMES, PerfWindowStats.HighPercentile)]));
                return ok;
            }
            finally
            {
                PerfStore.PhaseRecorder = null;
                recorder.Dispose();
            }
        });

        private static bool RunB34TierFloor() => WithFreshStore(() =>
        {
            PerfStore.TierFloor = PerfTier.Frame;
            bool ok = Check("A floor raises a lower tier and creates its resources",
                PerfStore.Tier == PerfTier.Frame && PerfStore.Hitches != null);

            PerfStore.SetTier(PerfTier.Systems);
            ok &= Check("A tier above the floor is kept", PerfStore.Tier == PerfTier.Systems && PerfStore.SlotsActive);
            PerfStore.SetTier(PerfTier.Basic);
            ok &= Check("A tier set below the floor is raised to it", PerfStore.Tier == PerfTier.Frame && !PerfStore.SlotsActive);

            PerfStore.TierFloor = PerfTier.Basic;
            ok &= Check("Clearing the floor restores the tier last set and frees what it no longer needs",
                PerfStore.Tier == PerfTier.Basic && PerfStore.Hitches == null);

            PerfStore.SetTier(PerfTier.Systems);
            PerfStore.TierFloor = PerfTier.Frame;
            PerfStore.TierFloor = PerfTier.Basic;
            ok &= Check("Raising and clearing a floor under a higher tier leaves that tier", PerfStore.Tier == PerfTier.Systems);

            PerfStore.TierFloor = PerfTier.Frame;
            InvokeStorePrivate("DomainReset");
            ok &= Check("DomainReset clears the floor", PerfStore.TierFloor == PerfTier.Basic && PerfStore.Tier == PerfTier.Basic);
            return ok;
        });

        private static bool RunB35SessionCsv() => WithFreshStore(() =>
        {
            string directory = NewExportDirectory();
            try
            {
                PerfStore.SetTier(PerfTier.Capture);
                CommitExportFrames(1, PRE_SESSION_FRAMES);
                bool ok = Check("No session opens without a folder", PerfStore.Exporter == null);

                PerfStore.ConfigureExport(directory, LARGE_EXPORT_BYTES, LARGE_EXPORT_BYTES);
                PerfSessionExporter exporter = PerfStore.Exporter;
                ok &= Check("Setting a folder at Capture opens a session", exporter != null);
                if (exporter == null) return false;

                int first = PRE_SESSION_FRAMES + 1;
                int end = CommitExportFrames(first, SESSION_FRAMES);
                PerfStore.SetTier(PerfTier.Systems);
                ok &= Check("Leaving Capture closes the session after its newest rows",
                    PerfStore.Exporter == null && exporter.IsClosed && exporter.RowsWritten == SESSION_FRAMES
                    && exporter.RowsDropped == 0);

                string[] lines = File.ReadAllLines(exporter.CurrentPath);
                string[] header = lines[0].Split(',');
                int columns = PerfSessionExporter.FrameColumnCount + PerfStore.SlotCount + PerfStore.CounterCount;
                ok &= Check($"The header has the {columns} frame, slot and counter columns",
                    header.Length == columns && header[0] == "frame"
                    && header[PerfSessionExporter.FrameColumnCount + (int)PerfSlot.Tick] == nameof(PerfSlot.Tick) + "_ms"
                    && header[PerfSessionExporter.FrameColumnCount + PerfStore.SlotCount + (int)PerfCounter.GenerationQueue]
                    == nameof(PerfCounter.GenerationQueue));
                ok &= Check("One row per frame since the session opened, none from before",
                    lines.Length == SESSION_FRAMES + 1 && lines[1].StartsWith(first + ","));

                bool rowsMatch = true;
                for (int frame = first; rowsMatch && frame < end; frame++)
                {
                    string[] cells = lines[frame - first + 1].Split(',');
                    rowsMatch = cells.Length == columns && RowMatches(cells, frame);
                    if (!rowsMatch) Debug.LogError($"  [FAIL] Row of frame {frame}: {lines[frame - first + 1]}");
                }

                ok &= Check("Every cell holds its frame's value; a frame without a value has an empty cell", rowsMatch);
                return ok;
            }
            finally
            {
                PerfStore.ConfigureExport(null, 0, 0);
                DeleteExportDirectory(directory);
            }
        });

        private static bool RunB36HitchFile() => WithFreshStore(() =>
        {
            string directory = NewExportDirectory();
            try
            {
                PerfStore.SetTier(PerfTier.Capture);
                PerfStore.ConfigureExport(directory, LARGE_EXPORT_BYTES, LARGE_EXPORT_BYTES);
                PerfSessionExporter exporter = PerfStore.Exporter;

                int frame = CommitRun(1, HITCH_LEAD_FRAMES, NORMAL_FRAME_MS);
                int hitchFrame = frame;
                GetStoreSlotTicks()[(int)PerfSlot.Tick] = TicksForMs(HITCH_TICK_MS);
                CommitMs(HITCH_FRAME_MS, frame++);
                CommitRun(frame, PerfHitchDetector.FramesAfter, NORMAL_FRAME_MS);
                PerfStore.ConfigureExport(null, 0, 0);

                bool ok = Check("One hitch record makes one hitch file", exporter.HitchFilesWritten == 1);
                string expectedName = exporter.SessionName + "_hitch001_frame" + hitchFrame + PerfSessionExporter.FileExtension;
                string path = Path.Combine(directory, expectedName);
                ok &= Check($"The file is named for the session, the hitch's number and its frame ({expectedName})", File.Exists(path));
                if (!ok) return false;

                string[] lines = File.ReadAllLines(path);
                ok &= Check("A summary line names the hitch frame, its worst time and its costliest slot",
                    lines[0].StartsWith(PerfSessionExporter.HitchSummaryPrefix + " frame=" + hitchFrame + " ")
                    && lines[0].Contains(" worst_ms=" + MsText(SlotMs(TicksForMs(HITCH_FRAME_MS))))
                    && lines[0].Contains(" top=" + nameof(PerfSlot.Tick) + ":" + MsText(SlotMs(TicksForMs(HITCH_TICK_MS)))));
                ok &= Check("The header is the session header plus the mark column",
                    lines[1].EndsWith("," + PerfSessionExporter.MarkColumn)
                    && lines[1].StartsWith(PerfSessionExporter.FrameColumns + ","));
                ok &= Check($"The window's {PerfHitchDetector.WindowFrames} rows follow, oldest first",
                    lines.Length == PerfHitchDetector.WindowFrames + 2
                    && lines[2].StartsWith(hitchFrame - PerfHitchDetector.FramesBefore + ",")
                    && lines[lines.Length - 1].StartsWith(hitchFrame + PerfHitchDetector.FramesAfter + ","));

                bool marksOk = true;
                for (int row = 0; row < PerfHitchDetector.WindowFrames; row++)
                {
                    string[] cells = lines[row + 2].Split(',');
                    string mark = cells[cells.Length - 1];
                    bool isHitchRow = row == PerfHitchDetector.FramesBefore;
                    marksOk &= isHitchRow
                        ? mark == PerfSessionExporter.HitchMark + " " + PerfSessionExporter.WorstMark
                          && cells[0] == hitchFrame.ToString()
                          && cells[1] == MsText(SlotMs(TicksForMs(HITCH_FRAME_MS)))
                        : mark.Length == 0;
                }

                ok &= Check("Only the hitch row is marked, as both the hitch and the worst frame", marksOk);
                return ok;
            }
            finally
            {
                PerfStore.ConfigureExport(null, 0, 0);
                DeleteExportDirectory(directory);
            }
        });

        private static bool RunB37ExportLimits() => WithFreshStore(() =>
        {
            string partsDirectory = NewExportDirectory();
            string capDirectory = NewExportDirectory();
            string blockingFile = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            try
            {
                PerfStore.SetTier(PerfTier.Capture);

                PerfStore.ConfigureExport(partsDirectory, SMALL_PART_BYTES, LARGE_EXPORT_BYTES);
                PerfSessionExporter parts = PerfStore.Exporter;
                int frame = CommitRun(1, LIMIT_FRAMES, NORMAL_FRAME_MS);
                PerfStore.ConfigureExport(null, 0, 0);

                string[] files = Directory.GetFiles(partsDirectory, "*" + PerfSessionExporter.FileExtension);
                Array.Sort(files, StringComparer.Ordinal);
                int dataRows = 0;
                bool partsOk = files.Length > 1;
                foreach (string file in files)
                {
                    string[] lines = File.ReadAllLines(file);
                    dataRows += lines.Length - 1;
                    partsOk &= lines[0].StartsWith(PerfSessionExporter.FrameColumns + ",")
                               && new FileInfo(file).Length <= SMALL_PART_BYTES + MAX_ROW_BYTES;
                }

                bool ok = Check($"A session over the part size continues in parts, each with a header and under the size ({files.Length} files)",
                    partsOk && Path.GetFileName(files[1]) == parts.SessionName + "_part02" + PerfSessionExporter.FileExtension);
                ok &= Check("The parts hold every row once", dataRows == LIMIT_FRAMES && parts.RowsWritten == LIMIT_FRAMES);

                PerfStore.ConfigureExport(capDirectory, LARGE_EXPORT_BYTES, SMALL_SESSION_BYTES);
                PerfSessionExporter capped = PerfStore.Exporter;
                frame = CommitRun(frame, LIMIT_FRAMES, NORMAL_FRAME_MS);
                PerfStore.ConfigureExport(null, 0, 0);
                ok &= Check("At the session size writing stops, every row written or counted as dropped",
                    capped.CapReached && capped.RowsWritten > 0 && capped.RowsWritten < LIMIT_FRAMES
                    && capped.RowsWritten + capped.RowsDropped == LIMIT_FRAMES
                    && capped.BytesWritten <= SMALL_SESSION_BYTES + MAX_ROW_BYTES);

                File.WriteAllText(blockingFile, "not a folder");
                PerfStore.ConfigureExport(blockingFile, LARGE_EXPORT_BYTES, LARGE_EXPORT_BYTES);
                PerfSessionExporter failing = PerfStore.Exporter;
                frame = CommitRun(frame, LIMIT_FRAMES, NORMAL_FRAME_MS);
                CommitMs(HITCH_FRAME_MS, frame++);
                CommitRun(frame, PerfHitchDetector.FramesAfter, NORMAL_FRAME_MS);
                PerfStore.ConfigureExport(null, 0, 0);
                int failedRows = LIMIT_FRAMES + 1 + PerfHitchDetector.FramesAfter;
                ok &= Check("A folder that cannot be written stops the export, reports why, and drops the rows without throwing",
                    failing.FailureMessage != null && failing.RowsWritten == 0 && failing.RowsDropped == failedRows);
                ok &= Check("A hitch record closed after the failure is counted as dropped, not written",
                    failing.HitchesDropped == 1 && failing.HitchFilesWritten == 0);
                return ok;
            }
            finally
            {
                PerfStore.ConfigureExport(null, 0, 0);
                DeleteExportDirectory(partsDirectory);
                DeleteExportDirectory(capDirectory);
                if (File.Exists(blockingFile)) File.Delete(blockingFile);
            }
        });

        #region Export Helpers

        /// <summary>
        /// Commits frames whose values are exact in three decimals: wall and CPU time, a <c>Tick</c> slot time, a
        /// generation-queue gauge equal to the frame, steady heap growth, one collection frame and one frame timing.
        /// </summary>
        /// <returns>The frame index after the last one committed.</returns>
        private static int CommitExportFrames(int firstFrame, int count)
        {
            for (int frame = firstFrame; frame < firstFrame + count; frame++)
            {
                GetStoreSlotTicks()[(int)PerfSlot.Tick] = ExportSlotTicks(frame);
                PerfStore.SetGauge(PerfCounter.GenerationQueue, frame);
                PerfStore.CommitFrame(new PerfFrameReadings
                {
                    WallTicks = ExportWallTicks(frame), CpuTicks = ExportCpuTicks(frame), FrameIndex = frame,
                    HeapBytes = (long)frame * EXPORT_ALLOC_BYTES, GcCollectionCount = frame >= EXPORT_COLLECTION_FRAME ? 1 : 0,
                    EndTimestamp = RowEnd(frame),
                });

                if (frame == EXPORT_TIMING_FRAME)
                    PerfStore.SubmitFrameTiming(InsideRow(frame), EXPORT_GPU_MS, EXPORT_RENDER_THREAD_MS, EXPORT_PRESENT_WAIT_MS);
            }

            return firstFrame + count;
        }

        /// <summary>Checks one exported row against what <see cref="CommitExportFrames"/> committed for its frame.</summary>
        private static bool RowMatches(string[] cells, int frame)
        {
            bool isCollection = frame == EXPORT_COLLECTION_FRAME;
            bool hasTiming = frame == EXPORT_TIMING_FRAME;
            int slotColumn = PerfSessionExporter.FrameColumnCount;
            int counterColumn = slotColumn + PerfStore.SlotCount;

            bool slotsOk = true;
            for (int slot = 0; slot < PerfStore.SlotCount; slot++)
            {
                string expected = slot == (int)PerfSlot.Tick ? MsText(SlotMs(ExportSlotTicks(frame))) : MsText(0f);
                slotsOk &= cells[slotColumn + slot] == expected;
            }

            bool countersOk = true;
            for (int counter = 0; counter < PerfStore.CounterCount; counter++)
            {
                int expected = counter == (int)PerfCounter.GenerationQueue ? frame : 0;
                countersOk &= cells[counterColumn + counter] == expected.ToString(CultureInfo.InvariantCulture);
            }

            return cells[0] == frame.ToString(CultureInfo.InvariantCulture)
                   && cells[1] == MsText(SlotMs(ExportWallTicks(frame)))
                   && cells[2] == MsText(SlotMs(ExportCpuTicks(frame)))
                   && cells[3] == (isCollection ? "" : EXPORT_ALLOC_BYTES.ToString(CultureInfo.InvariantCulture))
                   && cells[4] == (isCollection ? nameof(PerfGcState.Collected) : nameof(PerfGcState.Measured))
                   && cells[5] == (isCollection ? "1" : "0")
                   && cells[6] == (hasTiming ? MsText(EXPORT_GPU_MS) : "")
                   && cells[7] == (hasTiming ? MsText(EXPORT_RENDER_THREAD_MS) : "")
                   && cells[8] == (hasTiming ? MsText(EXPORT_PRESENT_WAIT_MS) : "")
                   && slotsOk && countersOk;
        }

        /// <summary>Wall time a multiple of 1/8 ms, so it is exact in a float and in three decimals.</summary>
        private static long ExportWallTicks(int frame) => TicksForMs(EIGHTH_MS * (frame % EXPORT_VALUE_CYCLE + 1));

        private static long ExportCpuTicks(int frame) => TicksForMs(EIGHTH_MS * (frame % EXPORT_VALUE_CYCLE + 2));

        private static long ExportSlotTicks(int frame) => TicksForMs(EIGHTH_MS * (frame % EXPORT_VALUE_CYCLE + 3));

        /// <summary>A value as the export should write it, formatted apart from the code under test.</summary>
        private static string MsText(float ms) => ((double)ms).ToString("F3", CultureInfo.InvariantCulture);

        private static string NewExportDirectory() =>
            Path.Combine(Path.GetTempPath(), "PerfExportValidation_" + Guid.NewGuid().ToString("N"));

        private static void DeleteExportDirectory(string directory)
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }

        /// <summary>
        /// Commits consecutive frames whose wall time in milliseconds is their frame index, whose rows end at
        /// <see cref="RowEnd"/>, and whose collection count steps to 1 at <see cref="COLLECTION_FRAME"/>.
        /// </summary>
        /// <returns>The frame index after the last one committed.</returns>
        private static int CommitPhaseFrames(int firstFrame, int count)
        {
            for (int frame = firstFrame; frame < firstFrame + count; frame++)
            {
                PerfStore.CommitFrame(new PerfFrameReadings
                {
                    WallTicks = TicksForMs(frame), CpuTicks = TicksForMs(frame), FrameIndex = frame,
                    GcCollectionCount = frame >= COLLECTION_FRAME ? 1 : 0, EndTimestamp = RowEnd(frame),
                });
            }

            return firstFrame + count;
        }

        /// <summary>What the ring stores as the wall time of a frame committed by <see cref="CommitPhaseFrames"/>.</summary>
        private static float PhaseFrameWallMs(int frame) => SlotMs(TicksForMs(frame));

        /// <summary>The sorted index of the nearest-rank percentile, ⌈p·n/100⌉ − 1, computed apart from the code under test.</summary>
        private static int OracleRankIndex(int count, int percentile) =>
            (int)Math.Ceiling((double)percentile * count / PERCENT_SCALE) - 1;

        #endregion
    }
}
