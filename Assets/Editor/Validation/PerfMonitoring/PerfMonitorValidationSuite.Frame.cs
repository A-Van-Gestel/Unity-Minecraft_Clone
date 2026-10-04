using System;
using System.Diagnostics;
using Diagnostics;
using Editor.Validation.Framework;

namespace Editor.Validation.PerfMonitoring
{
    /// <summary>
    /// The Frame-tier half of the suite: per-frame GC readings and their statistics, frame-timing back-fill,
    /// the Frame-tier resource lifecycle, and the hitch detector's threshold, window, merge, retention and
    /// attribution rules — all driven through <see cref="PerfStore"/> with synthetic readings.
    /// </summary>
    public static partial class PerfMonitorValidationSuite
    {
        private const double NORMAL_FRAME_MS = 5.0;
        private const double HITCH_FRAME_MS = 100.0;
        private const double BASELINE_FRAME_MS = 20.0;
        private const double LOW_BASELINE_FRAME_MS = 4.0;

        /// <summary>Enough frames to wrap the store's 2 048-frame ring before a hitch window is copied out.</summary>
        private const int RING_WRAP_FRAMES = PerfStore.RingCapacity + 52;

        /// <summary>A margin either side of the 33 ms floor that survives the tick round-trip.</summary>
        private const double FLOOR_MARGIN_MS = 0.01;

        /// <summary>Over the 33 ms floor but under 2.5 × the 20 ms baseline, and the reverse.</summary>
        private const double UNDER_FACTOR_MS = 45.0;

        private const double OVER_FACTOR_MS = 55.0;

        /// <summary>Valid thresholds unlike the defaults, and invalid ones that must fall back to them.</summary>
        private const float CUSTOM_MIN_MS = 10f;

        private const float CUSTOM_MEDIAN_FACTOR = 1.5f;
        private const float INVALID_MIN_MS = -1f;
        private const float INVALID_MEDIAN_FACTOR = 0.5f;

        /// <summary>A hitch slower than <see cref="HITCH_FRAME_MS"/>, joining its window <see cref="JOIN_OFFSET_FRAMES"/> frames in.</summary>
        private const double WORST_HITCH_FRAME_MS = 150.0;

        private const int JOIN_OFFSET_FRAMES = 6;

        /// <summary>Normal frames before a scenario's first hitch.</summary>
        private const int LEAD_IN_FRAMES = 10;

        /// <summary>Lead-in short of a full window, so the record holds only what the ring has.</summary>
        private const int SHORT_LEAD_IN_FRAMES = 9;

        /// <summary>Slot times of the hitch frame, ranked MeshProcess, LightMerge, Tick, Apply, GenerationProcess.</summary>
        private const double MESH_PROCESS_MS = 9.0;

        private const double LIGHT_MERGE_MS = 7.0;
        private const double TICK_MS = 5.0;
        private const double APPLY_MS = 1.0;
        private const double GENERATION_PROCESS_MS = 0.5;

        /// <summary>Second record's Tick time, above <see cref="APPLY_MS"/>.</summary>
        private const double SECOND_TICK_MS = 3.0;

        /// <summary>Rows end at multiples of this; a start half a step past a row's predecessor lies inside that row.</summary>
        private const long TIMESTAMP_STEP = 100;

        private const long HALF_STEP = TIMESTAMP_STEP / 2;

        /// <summary>Rows committed for the back-fill scenario's first checks, then the total after the age-bound extension.</summary>
        private const int BACKFILL_ROWS = 4;

        private const int BACKFILL_ROWS_EXTENDED = 24;

        /// <summary>Oldest row age the store searches for a late timing (its private <c>FRAME_TIMING_MAX_AGE</c>).</summary>
        private const int BACKFILL_MAX_AGE = 16;

        #region Scenarios

        private static bool RunB11GcReadings() => WithFreshStore(() =>
        {
            // A truth table of heap bytes and cumulative collection counts: each delta reads against the line before.
            CommitGc(1, 1000, 5);
            PerfFrameRing ring = GetStoreRing();
            bool ok = Check("First frame has no baseline",
                ring.GetFrame(0).GcState == PerfGcState.NoBaseline && ring.GetFrame(0).GcCollections == 0);

            CommitGc(2, 1500, 5);
            ok &= Check("Growth without a collection is measured exactly",
                ring.GetFrame(0).GcState == PerfGcState.Measured && ring.GetFrame(0).GcAllocBytes == 500);

            CommitGc(3, 800, 6);
            ok &= Check("A collection flags the frame and counts it",
                ring.GetFrame(0).GcState == PerfGcState.Collected && ring.GetFrame(0).GcCollections == 1);

            CommitGc(4, 700, 6);
            ok &= Check("A shrink without a collection is flagged and counted",
                ring.GetFrame(0).GcState == PerfGcState.Shrank && PerfStore.GcShrinkFrames == 1);

            CommitGc(5, 700L + int.MaxValue + 10L, 6);
            ok &= Check("Growth beyond the int range saturates",
                ring.GetFrame(0).GcState == PerfGcState.Measured && ring.GetFrame(0).GcAllocBytes == int.MaxValue);

            CommitGc(6, 0, 6 + 300);
            ok &= Check("Collections saturate at 255",
                ring.GetFrame(0).GcState == PerfGcState.Collected && ring.GetFrame(0).GcCollections == byte.MaxValue);

            PerfStore.ResetGcBaseline();
            CommitGc(7, 5000, 400);
            ok &= Check("After a baseline reset the next frame has no baseline, not a delta spanning the gap",
                ring.GetFrame(0).GcState == PerfGcState.NoBaseline && ring.GetFrame(0).GcCollections == 0);
            CommitGc(8, 5100, 400);
            ok &= Check("The frame after that is measured from the new baseline",
                ring.GetFrame(0).GcState == PerfGcState.Measured && ring.GetFrame(0).GcAllocBytes == 100);
            return ok;
        });

        private static bool RunB12GcStatistics() => WithFreshStore(() =>
        {
            CommitGc(1, 0, 0);
            CommitGc(2, 2048, 0);
            CommitGc(3, 1000, 1);
            CommitGc(4, 1000 + 4096, 1);
            CommitGc(5, 0, 1);

            PerfWindowSummary alloc = PerfStore.Summarize(PerfFrameField.GcAllocKb);
            bool ok = Check("Only the two measured frames enter the statistics", alloc.Count == 2);
            ok &= Check("Their kilobytes are exact (worst 4, mean 3, median 2)",
                ExactValue.Equal(alloc.Max, 4f) && ExactValue.Equal(alloc.Mean, 3f) && ExactValue.Equal(alloc.P50, 2f));
            ok &= Check("Collections across the held frames are summed", PerfStore.GcCollectionsHeld == 1);
            return ok;
        });

        private static bool RunB13FrameTimingBackfill() => WithFreshStore(() =>
        {
            bool ok = Check("Without a ring a timing is neither matched nor counted",
                !PerfStore.SubmitFrameTiming(1, 1f, 1f, 1f) && PerfStore.FrameTimingsReceived == 0);

            // Row r (1-based) ends at r × step; marker values 1–9 only tell the writes apart.
            for (int row = 1; row <= BACKFILL_ROWS; row++)
                CommitEnd(row, RowEnd(row));

            PerfFrameRing ring = GetStoreRing();
            ok &= Check("A start inside a row's interval writes that row",
                PerfStore.SubmitFrameTiming(InsideRow(3), 1f, 2f, 3f)
                && ExactValue.Equal(ring.GetFrame(1).GpuMs, 1f)
                && ExactValue.Equal(ring.GetFrame(1).RenderThreadMs, 2f)
                && ExactValue.Equal(ring.GetFrame(1).PresentWaitMs, 3f));
            ok &= Check("Rows without a timing stay NaN",
                float.IsNaN(ring.GetFrame(0).GpuMs) && float.IsNaN(ring.GetFrame(2).GpuMs) && float.IsNaN(ring.GetFrame(3).GpuMs));

            ok &= Check("A start equal to the previous row's end belongs to the next row",
                PerfStore.SubmitFrameTiming(RowEnd(1), 4f, 4f, 4f) && ExactValue.Equal(ring.GetFrame(2).GpuMs, 4f));
            ok &= Check("A start inside the oldest row is not matched (its start is unknown)",
                !PerfStore.SubmitFrameTiming(RowEnd(1) - 1, 9f, 9f, 9f) && float.IsNaN(ring.GetFrame(3).GpuMs));
            ok &= Check("A start at or after the newest row's end is not matched",
                !PerfStore.SubmitFrameTiming(RowEnd(BACKFILL_ROWS), 9f, 9f, 9f));
            ok &= Check("The newest row matches its own interval",
                PerfStore.SubmitFrameTiming(InsideRow(BACKFILL_ROWS), 5f, 5f, 5f) && ExactValue.Equal(ring.GetFrame(0).GpuMs, 5f));
            ok &= Check("Received and matched are counted", PerfStore.FrameTimingsReceived == 5 && PerfStore.FrameTimingsMatched == 3);
            ok &= Check("GPU statistics take only rows with a timing", PerfStore.Summarize(PerfFrameField.GpuMs).Count == 3);

            for (int row = BACKFILL_ROWS + 1; row <= BACKFILL_ROWS_EXTENDED; row++)
                CommitEnd(row, RowEnd(row));

            ok &= Check("A row 16 frames old is still matched",
                PerfStore.SubmitFrameTiming(InsideRow(BACKFILL_ROWS_EXTENDED - BACKFILL_MAX_AGE), 6f, 6f, 6f));
            ok &= Check("A row 17 frames old is not searched",
                !PerfStore.SubmitFrameTiming(InsideRow(BACKFILL_ROWS_EXTENDED - BACKFILL_MAX_AGE - 1), 6f, 6f, 6f));

            ok &= Check("A GPU time of 0 is stored as none; render-thread and present-wait times are kept",
                PerfStore.SubmitFrameTiming(InsideRow(BACKFILL_ROWS_EXTENDED), 0f, 2f, 0f)
                && float.IsNaN(ring.GetFrame(0).GpuMs) && ExactValue.Equal(ring.GetFrame(0).RenderThreadMs, 2f)
                && ExactValue.IsZero(ring.GetFrame(0).PresentWaitMs));
            ok &= Check("A re-read overwrites the row without being counted again",
                PerfStore.SubmitFrameTiming(InsideRow(BACKFILL_ROWS_EXTENDED), 7f, 7f, 7f, false)
                && ExactValue.Equal(ring.GetFrame(0).GpuMs, 7f)
                && PerfStore.FrameTimingsReceived == 8 && PerfStore.FrameTimingsMatched == 5);
            return ok;
        });

        private static bool RunB14FrameTierResources() => WithFreshStore(() =>
        {
            bool ok = Check("Basic: no timing reader, no detector", GetStoreField("s_frameTiming") == null && PerfStore.Hitches == null);
            PerfStore.SampleFrameTiming();
            ok &= Check("Basic: sampling submits nothing", PerfStore.FrameTimingsReceived == 0);

            PerfStore.SetTier(PerfTier.Frame);
            PerfFrameTimingSource source = (PerfFrameTimingSource)GetStoreField("s_frameTiming");
            PerfHitchDetector detector = PerfStore.Hitches;
            ok &= Check("Frame: reader and detector exist, no slot block", source != null && detector != null && !detector.HasSlotBlock);
            ok &= Check("Frame: sampling the live FrameTimingManager does not throw", !Throws(PerfStore.SampleFrameTiming));

            PerfStore.SetTier(PerfTier.Systems);
            ok &= Check("Systems: same detector, slot block allocated", PerfStore.Hitches == detector && detector.HasSlotBlock);

            PerfStore.SetTier(PerfTier.Frame);
            ok &= Check("Back to Frame: slot block freed, same detector", PerfStore.Hitches == detector && !detector.HasSlotBlock);

            PerfStore.SetTier(PerfTier.Basic);
            ok &= Check("Back to Basic: reader and detector disposed and dropped",
                source != null && source.IsDisposed && detector != null && detector.IsDisposed
                && GetStoreField("s_frameTiming") == null && PerfStore.Hitches == null);
            return ok;
        });

        private static bool RunB15HitchThreshold() => WithFreshStore(() =>
        {
            PerfStore.SetTier(PerfTier.Frame);
            PerfHitchDetector detector = PerfStore.Hitches;
            CommitMs(PerfHitchDetector.DefaultMinMs - FLOOR_MARGIN_MS, 1);
            bool ok = Check("Below the floor with no baseline: no hitch", detector.HitchFrames == 0 && !detector.IsWindowOpen);
            ok &= Check("No baseline before the first refresh", ExactValue.IsZero(detector.BaselineMedianMs));
            CommitMs(PerfHitchDetector.DefaultMinMs + FLOOR_MARGIN_MS, 2);
            ok &= Check("Above the floor: a hitch opens a window", detector.HitchFrames == 1 && detector.IsWindowOpen);

            detector = FreshDetector(PerfTier.Frame);
            int frame = CommitRun(1, PerfHitchDetector.BaselineFrames, BASELINE_FRAME_MS);
            float median = GetStoreRing().GetFrame(0).WallMs;
            ok &= Check("The baseline is the median of the recent frames", ExactValue.Equal(detector.BaselineMedianMs, median));
            ok &= Check("Above the floor the threshold is factor × median",
                ExactValue.Equal(detector.ThresholdMs, PerfHitchDetector.DefaultMedianFactor * median));

            CommitMs(UNDER_FACTOR_MS, frame++);
            ok &= Check("A frame under factor × median is no hitch, though over the floor", detector.HitchFrames == 0);
            CommitMs(OVER_FACTOR_MS, frame);
            ok &= Check("A frame over factor × median is a hitch", detector.HitchFrames == 1);

            PerfStore.SetHitchThresholds(CUSTOM_MIN_MS, CUSTOM_MEDIAN_FACTOR);
            ok &= Check("New thresholds reach the live detector", ExactValue.Equal(detector.ThresholdMs, CUSTOM_MEDIAN_FACTOR * median));
            PerfStore.SetHitchThresholds(INVALID_MIN_MS, INVALID_MEDIAN_FACTOR);
            ok &= Check("Invalid thresholds fall back to the defaults",
                ExactValue.Equal(detector.ThresholdMs, Math.Max(PerfHitchDetector.DefaultMinMs, PerfHitchDetector.DefaultMedianFactor * median)));

            detector = FreshDetector(PerfTier.Frame);
            CommitRun(1, PerfHitchDetector.BaselineRefreshFrames, LOW_BASELINE_FRAME_MS);
            ok &= Check("Under a low median the floor rules", ExactValue.Equal(detector.ThresholdMs, PerfHitchDetector.DefaultMinMs));

            InvokeStorePrivate("DomainReset");
            PerfStore.SetHitchThresholds(CUSTOM_MIN_MS, CUSTOM_MEDIAN_FACTOR);
            PerfStore.SetTier(PerfTier.Frame);
            ok &= Check("Thresholds set before the tier carry into the new detector", ExactValue.Equal(PerfStore.Hitches.ThresholdMs, CUSTOM_MIN_MS));
            return ok;
        });

        private static bool RunB16HitchWindow() => WithFreshStore(() =>
        {
            PerfHitchDetector detector = FreshDetector(PerfTier.Frame);
            int frame = CommitRun(1, RING_WRAP_FRAMES, NORMAL_FRAME_MS);
            int hitchFrame = frame;
            CommitMs(HITCH_FRAME_MS, frame++);
            frame = CommitRun(frame, PerfHitchDetector.FramesAfter - 1, NORMAL_FRAME_MS);
            bool ok = Check("The window stays open until its frames after are in", detector.IsWindowOpen && detector.RecordCount == 0);

            CommitMs(NORMAL_FRAME_MS, frame);
            ok &= Check("The window closes after FramesAfter frames", !detector.IsWindowOpen && detector.RecordCount == 1);

            PerfHitchRecord record = detector.GetRecord(0);
            ok &= Check("A full window holds FramesBefore + 1 + FramesAfter rows, the hitch at FramesBefore",
                record.RowCount == PerfHitchDetector.WindowFrames && record.HitchRow == PerfHitchDetector.FramesBefore
                && record.WorstRow == record.HitchRow && record.HitchFrameIndex == hitchFrame);

            bool consecutive = true;
            for (int row = 0; row < record.RowCount; row++)
                consecutive &= detector.GetRecordFrame(0, row).FrameIndex == hitchFrame - PerfHitchDetector.FramesBefore + row;
            ok &= Check("Rows are consecutive frames, oldest first, across the ring wrap", consecutive);
            ok &= Check("The record's worst time is the hitch row's",
                ExactValue.Equal(record.WorstWallMs, detector.GetRecordFrame(0, record.HitchRow).WallMs));

            detector = FreshDetector(PerfTier.Frame);
            frame = CommitRun(1, SHORT_LEAD_IN_FRAMES, NORMAL_FRAME_MS);
            CommitMs(HITCH_FRAME_MS, frame++);
            CommitRun(frame, PerfHitchDetector.FramesAfter, NORMAL_FRAME_MS);
            record = detector.GetRecord(0);
            ok &= Check("Early in a session the window holds what the ring has",
                record.RowCount == SHORT_LEAD_IN_FRAMES + 1 + PerfHitchDetector.FramesAfter && record.HitchRow == SHORT_LEAD_IN_FRAMES
                && detector.GetRecordFrame(0, 0).FrameIndex == 1);
            return ok;
        });

        private static bool RunB17HitchMergeAndRetention() => WithFreshStore(() =>
        {
            PerfHitchDetector detector = FreshDetector(PerfTier.Frame);
            int frame = CommitRun(1, LEAD_IN_FRAMES, NORMAL_FRAME_MS);
            CommitMs(HITCH_FRAME_MS, frame++);
            frame = CommitRun(frame, JOIN_OFFSET_FRAMES - 1, NORMAL_FRAME_MS);
            int worstFrame = frame;
            CommitMs(WORST_HITCH_FRAME_MS, frame++);
            frame = CommitRun(frame, PerfHitchDetector.FramesAfter - JOIN_OFFSET_FRAMES, NORMAL_FRAME_MS);

            PerfHitchRecord record = detector.GetRecord(0);
            bool ok = Check("A hitch inside an open window joins it without extending it",
                detector.RecordCount == 1 && !detector.IsWindowOpen && record.HitchFrameCount == 2);
            ok &= Check("The slowest joined hitch is the worst row",
                record.WorstRow == record.HitchRow + JOIN_OFFSET_FRAMES && detector.GetRecordFrame(0, record.WorstRow).FrameIndex == worstFrame
                && ExactValue.Equal(record.WorstWallMs, detector.GetRecordFrame(0, record.WorstRow).WallMs));

            CommitMs(HITCH_FRAME_MS, frame++);
            frame = CommitRun(frame, PerfHitchDetector.FramesAfter - 1, NORMAL_FRAME_MS);
            CommitMs(HITCH_FRAME_MS, frame++);
            ok &= Check("A hitch on the closing frame joins the window as its last row",
                detector.RecordCount == 2 && !detector.IsWindowOpen && detector.GetRecord(0).HitchFrameCount == 2);
            CommitMs(NORMAL_FRAME_MS, frame++);
            ok &= Check("The frame after a close opens nothing", !detector.IsWindowOpen);

            int firstKeptHitch = frame;
            int lastHitch = frame;
            for (int i = 0; i < PerfHitchDetector.MaxRecords; i++)
            {
                lastHitch = frame;
                CommitMs(HITCH_FRAME_MS, frame++);
                frame = CommitRun(frame, PerfHitchDetector.FramesAfter, NORMAL_FRAME_MS);
            }

            ok &= Check("Only the newest MaxRecords records are held",
                detector.RecordCount == PerfHitchDetector.MaxRecords && detector.RecordsClosed == PerfHitchDetector.MaxRecords + 2);
            ok &= Check("Newest first: age 0 is the last hitch, the oldest held is the first of the run",
                detector.GetRecord(0).HitchFrameIndex == lastHitch
                && detector.GetRecord(PerfHitchDetector.MaxRecords - 1).HitchFrameIndex == firstKeptHitch);
            ok &= Check("Every hitch frame is counted", detector.HitchFrames == 4 + PerfHitchDetector.MaxRecords);
            return ok;
        });

        private static bool RunB18HitchAttribution() => WithFreshStore(() =>
        {
            PerfHitchDetector detector = FreshDetector(PerfTier.Frame);
            int frame = 1;
            for (int i = 0; i < LEAD_IN_FRAMES; i++) CommitFull(NORMAL_FRAME_MS, frame++, 0);
            CommitFull(HITCH_FRAME_MS, frame++, 1);
            for (int i = 0; i < PerfHitchDetector.FramesAfter; i++) CommitFull(NORMAL_FRAME_MS, frame++, 1);
            bool ok = Check("A collection during a hitch frame marks the record GC-correlated", detector.GetRecord(0).GcCorrelated);

            CommitFull(NORMAL_FRAME_MS, frame++, 2);
            CommitFull(HITCH_FRAME_MS, frame++, 2);
            for (int i = 0; i < PerfHitchDetector.FramesAfter; i++) CommitFull(NORMAL_FRAME_MS, frame++, 2);
            ok &= Check("A collection outside the hitch frames does not", !detector.GetRecord(0).GcCorrelated);
            ok &= Check("At Frame detail a record holds no slot times", !detector.GetRecord(0).HasSlots && detector.GetRecord(0).TopSlotCount == 0);

            detector = FreshDetector(PerfTier.Systems);
            long[] slotTicks = GetStoreSlotTicks();
            frame = CommitRun(1, LEAD_IN_FRAMES, NORMAL_FRAME_MS);
            slotTicks[(int)PerfSlot.Tick] = TicksForMs(TICK_MS);
            slotTicks[(int)PerfSlot.Apply] = TicksForMs(APPLY_MS);
            slotTicks[(int)PerfSlot.MeshProcess] = TicksForMs(MESH_PROCESS_MS);
            slotTicks[(int)PerfSlot.LightMerge] = TicksForMs(LIGHT_MERGE_MS);
            slotTicks[(int)PerfSlot.GenerationProcess] = TicksForMs(GENERATION_PROCESS_MS);
            CommitMs(HITCH_FRAME_MS, frame++);
            frame = CommitRun(frame, PerfHitchDetector.FramesAfter, NORMAL_FRAME_MS);

            PerfHitchRecord record = detector.GetRecord(0);
            ok &= Check("At Systems the record holds slot times", record.HasSlots && record.TopSlotCount == 3);
            ok &= Check("Top three slots of the worst frame, costliest first",
                record.TopSlot0 == PerfSlot.MeshProcess && record.TopSlot1 == PerfSlot.LightMerge && record.TopSlot2 == PerfSlot.Tick
                && ExactValue.Equal(record.TopSlotMs0, SlotMs(TicksForMs(MESH_PROCESS_MS))));
            ok &= Check("Per-row slot times are the committed ones",
                ExactValue.Equal(detector.GetRecordSlotMs(0, record.WorstRow, PerfSlot.Apply), SlotMs(TicksForMs(APPLY_MS)))
                && ExactValue.IsZero(detector.GetRecordSlotMs(0, record.WorstRow - 1, PerfSlot.MeshProcess)));

            slotTicks[(int)PerfSlot.Apply] = TicksForMs(APPLY_MS);
            slotTicks[(int)PerfSlot.Tick] = TicksForMs(SECOND_TICK_MS);
            CommitMs(HITCH_FRAME_MS, frame++);
            CommitRun(frame, PerfHitchDetector.FramesAfter, NORMAL_FRAME_MS);
            record = detector.GetRecord(0);
            ok &= Check("Only non-zero slots are ranked",
                record.TopSlotCount == 2 && record.TopSlot0 == PerfSlot.Tick && record.TopSlot1 == PerfSlot.Apply);

            PerfStore.SetTier(PerfTier.Frame);
            record = detector.GetRecord(0);
            ok &= Check("Dropping to Frame drops the slot times but keeps the ranking", !record.HasSlots && record.TopSlotCount == 2);
            return ok;
        });

        private static bool RunB19TierDropAndShutdown() => WithFreshStore(() =>
        {
            PerfHitchDetector detector = FreshDetector(PerfTier.Frame);
            int frame = CommitRun(1, LEAD_IN_FRAMES, NORMAL_FRAME_MS);
            CommitMs(HITCH_FRAME_MS, frame);
            bool ok = Check("A window is open", detector.IsWindowOpen);

            PerfStore.SetTier(PerfTier.Basic);
            ok &= Check("Leaving Frame disposes the detector, discarding the window", detector.IsDisposed && PerfStore.Hitches == null);

            PerfStore.SetTier(PerfTier.Frame);
            PerfHitchDetector fresh = PerfStore.Hitches;
            ok &= Check("Re-entering Frame starts an empty detector",
                fresh != null && fresh != detector && fresh.RecordCount == 0 && !fresh.IsWindowOpen);

            PerfFrameTimingSource source = (PerfFrameTimingSource)GetStoreField("s_frameTiming");
            InvokeStorePrivate("ShutDown");
            ok &= Check("Shutdown frees the detector and the timing reader",
                fresh != null && fresh.IsDisposed && source != null && source.IsDisposed
                && PerfStore.Hitches == null && GetStoreField("s_frameTiming") == null);

            PerfStore.SetTier(PerfTier.Frame);
            ok &= Check("After shutdown a tier change allocates nothing", PerfStore.Hitches == null && GetStoreField("s_frameTiming") == null);
            return ok;
        });

        #endregion

        #region Helpers

        /// <summary>Resets the store and enters a tier, returning its new detector.</summary>
        private static PerfHitchDetector FreshDetector(PerfTier tier)
        {
            InvokeStorePrivate("DomainReset");
            PerfStore.SetTier(tier);
            return PerfStore.Hitches;
        }

        private static long TicksForMs(double ms) => (long)Math.Round(ms * Stopwatch.Frequency / MILLISECONDS_PER_SECOND);

        /// <summary>What the ring stores for a slot's ticks.</summary>
        private static float SlotMs(long ticks) => (float)(ticks * (MILLISECONDS_PER_SECOND / Stopwatch.Frequency));

        private static void CommitMs(double wallMs, int frameIndex) => PerfStore.CommitFrame(Readings(TicksForMs(wallMs), frameIndex));

        /// <summary>Commits consecutive frames of one wall time.</summary>
        /// <returns>The frame index after the last one committed.</returns>
        private static int CommitRun(int firstFrame, int count, double wallMs)
        {
            for (int i = 0; i < count; i++)
                CommitMs(wallMs, firstFrame + i);
            return firstFrame + count;
        }

        private static void CommitGc(int frameIndex, long heapBytes, int collectionCount) =>
            PerfStore.CommitFrame(new PerfFrameReadings
            {
                WallTicks = 1, CpuTicks = 1, FrameIndex = frameIndex, HeapBytes = heapBytes, GcCollectionCount = collectionCount,
            });

        private static void CommitFull(double wallMs, int frameIndex, int collectionCount) =>
            PerfStore.CommitFrame(new PerfFrameReadings
            {
                WallTicks = TicksForMs(wallMs), CpuTicks = TicksForMs(wallMs), FrameIndex = frameIndex,
                GcCollectionCount = collectionCount,
            });

        /// <summary>The end timestamp of back-fill row <paramref name="row"/> (1-based).</summary>
        private static long RowEnd(int row) => row * TIMESTAMP_STEP;

        /// <summary>A frame start inside back-fill row <paramref name="row"/>'s interval [previous end, own end).</summary>
        private static long InsideRow(int row) => RowEnd(row - 1) + HALF_STEP;

        private static void CommitEnd(int frameIndex, long endTimestamp) =>
            PerfStore.CommitFrame(new PerfFrameReadings { WallTicks = 1, CpuTicks = 1, FrameIndex = frameIndex, EndTimestamp = endTimestamp });

        #endregion
    }
}
