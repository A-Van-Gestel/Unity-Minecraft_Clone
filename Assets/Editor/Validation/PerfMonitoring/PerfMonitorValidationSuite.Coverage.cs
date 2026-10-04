using System;
using Diagnostics;
using Editor.Validation.Framework;

namespace Editor.Validation.PerfMonitoring
{
    /// <summary>
    /// The coverage half of the suite: the <c>World.Update</c> unattributed remainder, derived by
    /// <see cref="PerfStore.BeginWorldFrame"/> / <see cref="PerfStore.EndWorldFrameAt"/> from injected slot ticks, and the
    /// counters — gauge and per-frame semantics, their columns and statistics, and their rows in hitch records. Every
    /// expectation is exact.
    /// </summary>
    public static partial class PerfMonitorValidationSuite
    {
        /// <summary>Distinct, non-round tick counts: slot time before the bracket, inside it, and the bracket itself.</summary>
        private const long BEFORE_BRACKET_TICKS = 50_021L;

        private const long IN_BRACKET_APPLY_TICKS = 30_011L;
        private const long IN_BRACKET_DISK_TICKS = 7_013L;
        private const long AFTER_BRACKET_TICKS = 9_001L;
        private const long BRACKET_TICKS = 100_003L;

        /// <summary>A gauge level, and running totals whose growth is distinct per step.</summary>
        private const int GAUGE_LEVEL = 37;

        private const long FIRST_TOTAL = 1_000L;
        private const int FIRST_GROWTH = 3;
        private const int SECOND_GROWTH = 7;

        /// <summary>A total below the previous one, as from a fresh pool; and one sampled while the columns were absent.</summary>
        private const long RESTARTED_TOTAL = 4L;

        private const long GAP_TOTAL = 5_000L;

        /// <summary>Frames of descending gauge levels for the statistics check.</summary>
        private const int STATS_COUNTER_FRAMES = 10;

        #region Scenarios

        private static bool RunB20UnattributedRemainder() => WithFreshStore(() =>
        {
            long[] slotTicks = GetStoreSlotTicks();
            int remainderSlot = (int)PerfSlot.WorldUnattributed;

            bool ok = Check("Below Systems the bracket does not open", PerfStore.BeginWorldFrame() == 0L);
            PerfStore.EndWorldFrameAt(0L, BRACKET_TICKS);
            ok &= Check("A zero start writes nothing", slotTicks[remainderSlot] == 0L);

            ok &= Check("Below Systems the bracket never reads as open", !PerfStore.IsWorldFrameOpen);

            PerfStore.SetTier(PerfTier.Systems);
            slotTicks[(int)PerfSlot.Tick] = BEFORE_BRACKET_TICKS;
            long start = PerfStore.BeginWorldFrame();
            ok &= Check("The bracket reads as open between its begin and end", PerfStore.IsWorldFrameOpen);
            slotTicks[(int)PerfSlot.Apply] += IN_BRACKET_APPLY_TICKS;
            slotTicks[(int)PerfSlot.DiskLoadApply] += IN_BRACKET_DISK_TICKS;
            PerfStore.EndWorldFrameAt(start, start + BRACKET_TICKS);
            ok &= Check("Ending the bracket closes it", !PerfStore.IsWorldFrameOpen);

            long expected = BRACKET_TICKS - IN_BRACKET_APPLY_TICKS - IN_BRACKET_DISK_TICKS;
            ok &= Check("Remainder = bracket − slot time recorded inside it (time before it is ignored)",
                start != 0L && slotTicks[remainderSlot] == expected);

            slotTicks[(int)PerfSlot.Tick] += AFTER_BRACKET_TICKS;
            ok &= Check("Slot time after the bracket leaves the remainder alone", slotTicks[remainderSlot] == expected);

            start = PerfStore.BeginWorldFrame();
            PerfStore.EndWorldFrameAt(start, start + BRACKET_TICKS);
            ok &= Check("A second bracket in the frame adds its own remainder, excluding the first's",
                slotTicks[remainderSlot] == expected + BRACKET_TICKS);
            ok &= Check("No negative remainder so far", PerfStore.NegativeRemainderFrames == 0);

            PerfStore.BeginWorldFrame();
            PerfStore.CommitFrame(Readings(BRACKET_TICKS, 1));
            ok &= Check("A bracket left open (an exception in World.Update) is closed by the commit", !PerfStore.IsWorldFrameOpen);
            PerfFrameRing ring = GetStoreRing();
            ok &= Check("The commit stores the remainder as milliseconds and zeroes it",
                ring != null && ExactValue.Equal(ring.GetSlotMs(0, PerfSlot.WorldUnattributed), SlotMs(expected + BRACKET_TICKS))
                             && slotTicks[remainderSlot] == 0L);

            start = PerfStore.BeginWorldFrame();
            slotTicks[(int)PerfSlot.Unload] += BRACKET_TICKS + IN_BRACKET_DISK_TICKS;
            PerfStore.EndWorldFrameAt(start, start + BRACKET_TICKS);
            ok &= Check("Overlapping slots give a negative remainder, kept as measured and counted",
                slotTicks[remainderSlot] == -IN_BRACKET_DISK_TICKS && PerfStore.NegativeRemainderFrames == 1);

            InvokeStorePrivate("DomainReset");
            ok &= Check("DomainReset clears the negative count", PerfStore.NegativeRemainderFrames == 0);
            return ok;
        });

        private static bool RunB21CounterSemantics() => WithFreshStore(() =>
        {
            PerfStore.SetTier(PerfTier.Systems);
            int frame = 1;
            PerfStore.SetGauge(PerfCounter.MeshQueue, GAUGE_LEVEL);
            PerfStore.SampleTotal(PerfCounter.SectionPoolMisses, FIRST_TOTAL);
            CommitMs(NORMAL_FRAME_MS, frame++);
            PerfFrameRing ring = GetStoreRing();
            bool ok = Check("A gauge is stored as set; a first running total only sets the baseline",
                ring != null && ring.GetCounter(0, PerfCounter.MeshQueue) == GAUGE_LEVEL
                             && ring.GetCounter(0, PerfCounter.SectionPoolMisses) == 0);
            if (ring == null) return false;

            PerfStore.SampleTotal(PerfCounter.SectionPoolMisses, FIRST_TOTAL + FIRST_GROWTH);
            PerfStore.SampleTotal(PerfCounter.SectionPoolMisses, FIRST_TOTAL + FIRST_GROWTH + SECOND_GROWTH);
            CommitMs(NORMAL_FRAME_MS, frame++);
            ok &= Check("Growth since the previous sample is added; two samples in a frame sum",
                ring.GetCounter(0, PerfCounter.SectionPoolMisses) == FIRST_GROWTH + SECOND_GROWTH);
            ok &= Check("A gauge holds its level across commits", ring.GetCounter(0, PerfCounter.MeshQueue) == GAUGE_LEVEL);

            CommitMs(NORMAL_FRAME_MS, frame++);
            ok &= Check("A per-frame count is zeroed by the commit", ring.GetCounter(0, PerfCounter.SectionPoolMisses) == 0);

            PerfStore.SampleTotal(PerfCounter.SectionPoolMisses, RESTARTED_TOTAL);
            PerfStore.SampleTotal(PerfCounter.SectionPoolMisses, RESTARTED_TOTAL + FIRST_GROWTH);
            CommitMs(NORMAL_FRAME_MS, frame++);
            ok &= Check("A total that went down re-baselines instead of adding a negative",
                ring.GetCounter(0, PerfCounter.SectionPoolMisses) == FIRST_GROWTH);

            PerfStore.SetTier(PerfTier.Frame);
            PerfStore.SampleTotal(PerfCounter.SectionPoolMisses, GAP_TOTAL);
            CommitMs(NORMAL_FRAME_MS, frame++);
            PerfStore.SetTier(PerfTier.Systems);
            PerfStore.SampleTotal(PerfCounter.SectionPoolMisses, GAP_TOTAL + SECOND_GROWTH);
            CommitMs(NORMAL_FRAME_MS, frame++);
            ok &= Check("Re-entering Systems starts the running totals over",
                ring.GetCounter(0, PerfCounter.SectionPoolMisses) == 0);

            PerfStore.ResetCounters();
            CommitMs(NORMAL_FRAME_MS, frame);
            ok &= Check("ResetCounters zeroes the held gauges", ring.GetCounter(0, PerfCounter.MeshQueue) == 0);
            return ok;
        });

        private static bool RunB22CounterColumnsAndStats() => WithFreshStore(() =>
        {
            bool ok = Check("A ring without counter columns ignores counter values", !Throws(() =>
            {
                using PerfFrameRing bare = new PerfFrameRing(SMALL_RING_CAPACITY, SMALL_RING_SLOTS);
                bare.AllocateSlotColumns();
                bare.Commit(new PerfFrame(), new long[SMALL_RING_SLOTS], TEST_TICK_TO_MS, new int[PerfStore.CounterCount]);
            }));

            PerfStore.SetTier(PerfTier.Frame);
            PerfStore.SetGauge(PerfCounter.LightReady, GAUGE_LEVEL);
            CommitMs(NORMAL_FRAME_MS, 0);
            ok &= Check("Below Systems no counter is stored", PerfStore.SummarizeCounter(PerfCounter.LightReady).Count == 0);

            PerfStore.SetTier(PerfTier.Systems);
            float[] levels = new float[STATS_COUNTER_FRAMES];
            double sum = 0;
            for (int i = 0; i < STATS_COUNTER_FRAMES; i++)
            {
                int level = STATS_COUNTER_FRAMES - i;
                levels[i] = level;
                sum += level;
                PerfStore.SetGauge(PerfCounter.LightReady, level);
                CommitMs(NORMAL_FRAME_MS, i + 1);
            }

            Array.Sort(levels);
            PerfWindowSummary summary = PerfStore.SummarizeCounter(PerfCounter.LightReady);
            ok &= Check("Counter statistics cover the frames with counter columns, exactly",
                summary.Count == STATS_COUNTER_FRAMES
                && ExactValue.Equal(summary.Max, levels[STATS_COUNTER_FRAMES - 1])
                && ExactValue.Equal(summary.Mean, (float)(sum / STATS_COUNTER_FRAMES))
                && ExactValue.Equal(summary.P50, levels[PerfWindowStats.NearestRankIndex(STATS_COUNTER_FRAMES, PerfWindowStats.MedianPercentile)]));

            PerfStore.SetTier(PerfTier.Basic);
            ok &= Check("Dropping below Systems frees the counter columns with the slot columns",
                PerfStore.SummarizeCounter(PerfCounter.LightReady).Count == 0);
            ok &= Check("A ring without counters reads none", Throws(() =>
            {
                using PerfFrameRing bare = new PerfFrameRing(SMALL_RING_CAPACITY, SMALL_RING_SLOTS);
                bare.AllocateSlotColumns();
                bare.Commit(new PerfFrame(), new long[SMALL_RING_SLOTS], TEST_TICK_TO_MS);
                bare.GetCounter(0, PerfCounter.LightReady);
            }));
            return ok;
        });

        private static bool RunB23HitchRecordCounters() => WithFreshStore(() =>
        {
            PerfHitchDetector detector = FreshDetector(PerfTier.Systems);
            int frame = 1;
            for (int i = 0; i < RING_WRAP_FRAMES; i++, frame++)
            {
                PerfStore.SetGauge(PerfCounter.MeshQueue, frame);
                CommitMs(NORMAL_FRAME_MS, frame);
            }

            int hitchFrame = frame;
            PerfStore.SetGauge(PerfCounter.MeshQueue, frame);
            PerfStore.SampleTotal(PerfCounter.SectionPoolMisses, FIRST_TOTAL);
            PerfStore.SampleTotal(PerfCounter.SectionPoolMisses, FIRST_TOTAL + FIRST_GROWTH);
            CommitMs(HITCH_FRAME_MS, frame++);
            for (int i = 0; i < PerfHitchDetector.FramesAfter; i++, frame++)
            {
                PerfStore.SetGauge(PerfCounter.MeshQueue, frame);
                CommitMs(NORMAL_FRAME_MS, frame);
            }

            PerfHitchRecord record = detector.GetRecord(0);
            bool rowsMatch = record.HasSlots && record.RowCount == PerfHitchDetector.WindowFrames;
            for (int row = 0; rowsMatch && row < record.RowCount; row++)
                rowsMatch = detector.GetRecordCounter(0, row, PerfCounter.MeshQueue) == detector.GetRecordFrame(0, row).FrameIndex;
            bool ok = Check("Every record row carries its own frame's counters, oldest first, across the ring wrap", rowsMatch);
            ok &= Check("A per-frame count lands in its frame's row only",
                record.HasSlots && record.HitchFrameIndex == hitchFrame
                && detector.GetRecordCounter(0, record.HitchRow, PerfCounter.SectionPoolMisses) == FIRST_GROWTH
                && detector.GetRecordCounter(0, record.HitchRow + 1, PerfCounter.SectionPoolMisses) == 0);

            PerfStore.SetTier(PerfTier.Frame);
            ok &= Check("Dropping to Frame releases the record counters with the slot times",
                Throws(() => detector.GetRecordCounter(0, 0, PerfCounter.MeshQueue)));
            return ok;
        });

        #endregion
    }
}
