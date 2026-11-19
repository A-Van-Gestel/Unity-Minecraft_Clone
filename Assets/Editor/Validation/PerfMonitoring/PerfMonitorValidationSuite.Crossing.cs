using System.Reflection;
using Diagnostics;
using Editor.Validation.Framework;
using Serialization;
using UnityEngine;

namespace Editor.Validation.PerfMonitoring
{
    /// <summary>
    /// The view-distance crossing half of the suite: <see cref="PerfCrossingTotals"/> sampled into the <c>Xing*</c>
    /// and <c>Unload*</c> counters by <c>World.SampleCrossingCounters</c>, the shared
    /// <see cref="PerfTickTotals.Lap(ref long, long)"/> contract that an untimed start charges nothing, and the
    /// <see cref="StorageIoStats"/> totals behind a save snapshot's return to the data pool. World-free: the passes
    /// themselves need a live world, so the probes are the sampler, the lap and the recorder, not
    /// <c>World.CheckViewDistance</c> or <c>World.UnloadChunks</c>.
    /// <para>
    /// The two storage totals the scenario moves are read back and restored around it; a session's own disk counters
    /// keep their values.
    /// </para>
    /// </summary>
    public static partial class PerfMonitorValidationSuite
    {
        /// <summary>The step between the values the mapping check gives the fields, so no two expected figures collide.</summary>
        private const long CROSSING_FIELD_VALUE_STEP = 100_000L;

        /// <summary>Each crossing totals field, the counter it must reach, and whether its ticks become microseconds.</summary>
        private static readonly (string Field, PerfCounter Counter, bool Ticks)[] s_crossingCounterMap =
        {
            (nameof(PerfCrossingTotals.CloudsTicks), PerfCounter.XingCloudsUs, true),
            (nameof(PerfCrossingTotals.SpiralTicks), PerfCounter.XingSpiralUs, true),
            (nameof(PerfCrossingTotals.DeactScanTicks), PerfCounter.XingDeactScanUs, true),
            (nameof(PerfCrossingTotals.DeactivateTicks), PerfCounter.XingDeactivateUs, true),
            (nameof(PerfCrossingTotals.ActivateTicks), PerfCounter.XingActivateUs, true),
            (nameof(PerfCrossingTotals.ActiveSetTicks), PerfCounter.XingActiveSetUs, true),
            (nameof(PerfCrossingTotals.UnloadScanTicks), PerfCounter.UnloadScanUs, true),
            (nameof(PerfCrossingTotals.UnloadDecideTicks), PerfCounter.UnloadDecideUs, true),
            (nameof(PerfCrossingTotals.UnloadBookkeepTicks), PerfCounter.UnloadBookkeepUs, true),
            (nameof(PerfCrossingTotals.UnloadSaveTicks), PerfCounter.UnloadSaveUs, true),
            (nameof(PerfCrossingTotals.UnloadVisualTicks), PerfCounter.UnloadVisualUs, true),
            (nameof(PerfCrossingTotals.UnloadDataReturnTicks), PerfCounter.UnloadDataReturnUs, true),
            (nameof(PerfCrossingTotals.Crossings), PerfCounter.Crossings, false),
            (nameof(PerfCrossingTotals.GenEnqueued), PerfCounter.XingGenEnqueued, false),
            (nameof(PerfCrossingTotals.Activations), PerfCounter.XingActivations, false),
            (nameof(PerfCrossingTotals.Deactivations), PerfCounter.XingDeactivations, false),
            (nameof(PerfCrossingTotals.UnloadCandidates), PerfCounter.UnloadCandidates, false),
            (nameof(PerfCrossingTotals.UnloadedChunks), PerfCounter.UnloadedChunks, false),
            (nameof(PerfCrossingTotals.UnloadSaved), PerfCounter.UnloadSaved, false),
            (nameof(PerfCrossingTotals.UnloadSavedSections), PerfCounter.UnloadSavedSections, false),
            (nameof(PerfCrossingTotals.UnloadDeferJob), PerfCounter.UnloadDeferJob, false),
            (nameof(PerfCrossingTotals.UnloadDeferStrand), PerfCounter.UnloadDeferStrand, false),
        };

        #region Scenarios

        private static bool RunB46CrossingBreakdown() => WithFreshStore(() =>
        {
            FieldInfo returnTicks = IoStatsField("s_snapshotReturnTicks");
            FieldInfo returnOps = IoStatsField("s_snapshotReturns");
            long ticksBefore = (long)returnTicks.GetValue(null);
            long opsBefore = (long)returnOps.GetValue(null);
            try
            {
                PerfStore.SetTier(PerfTier.Systems);
                bool ok = Check("Every crossing totals field has exactly one entry in the counter map, naming a real field",
                    CrossingMapFieldsAreExact());
                ok &= Check("Every map counter is distinct, and none is a gauge", CrossingMapCountersAreDistinct());
                ok &= Check("Each crossing totals field reaches its own counter, ticks converted to microseconds",
                    CrossingCountersMatch());

                ok &= Check("An untimed lap leaves its part's total alone and passes the 0 on to the next part",
                    LapFromUntimedStartIsNoOp());

                StorageIoStats.SamplePerfCounters();
                StorageIoStats.RecordSnapshotReturn(0L);
                ok &= Check("A zero start records neither the return's time nor the return itself",
                    ExactValue.Equal((long)returnTicks.GetValue(null), ticksBefore)
                    && ExactValue.Equal((long)returnOps.GetValue(null), opsBefore));

                long stamp = StorageIoStats.Stamp();
                Spin();
                StorageIoStats.RecordSnapshotReturn(stamp);
                long ticksDelta = (long)returnTicks.GetValue(null) - ticksBefore;
                long opsDelta = (long)returnOps.GetValue(null) - opsBefore;
                StorageIoStats.SamplePerfCounters();
                PerfStore.CommitFrame(Readings(1, 1));
                ok &= Check("A timed return records exactly one return and its time",
                    ExactValue.Equal(opsDelta, 1L) && ticksDelta > 0);
                // The counter differences the converted totals, and earlier scenarios' saves leave a nonzero start, so
                // the expected figure rounds the same way rather than converting the delta alone.
                long expectedMicros = PerfStore.TicksToMicros(ticksBefore + ticksDelta) - PerfStore.TicksToMicros(ticksBefore);
                ok &= Check("Both save-return counters read the frame's growth of their totals",
                    ExactValue.Equal(PerfStore.SumCounter(PerfCounter.SaveReturns), opsDelta)
                    && ExactValue.Equal(PerfStore.SumCounter(PerfCounter.SaveReturnUs), expectedMicros));

                PerfStore.SetTier(PerfTier.Basic);
                long belowStamp = StorageIoStats.Stamp();
                StorageIoStats.RecordSnapshotReturn(belowStamp);
                ok &= Check("Below Systems the return is not stamped, so nothing is recorded",
                    ExactValue.Equal(belowStamp, 0L)
                    && ExactValue.Equal((long)returnTicks.GetValue(null), ticksBefore + ticksDelta)
                    && ExactValue.Equal((long)returnOps.GetValue(null), opsBefore + 1L));
                return ok;
            }
            finally
            {
                returnTicks.SetValue(null, ticksBefore);
                returnOps.SetValue(null, opsBefore);
            }
        });

        #endregion

        #region Helpers

        /// <summary>
        /// Reads the totals struct's own fields and requires each to appear exactly once in the counter map, and every
        /// map entry to name a field that exists — so a renamed or dropped field or counter cannot pass silently.
        /// </summary>
        private static bool CrossingMapFieldsAreExact()
        {
            FieldInfo[] fields = typeof(PerfCrossingTotals).GetFields(BindingFlags.Public | BindingFlags.Instance);
            bool ok = Check($"The map has one entry per totals field ({fields.Length} fields, " +
                            $"{s_crossingCounterMap.Length} entries)", fields.Length == s_crossingCounterMap.Length);

            foreach (FieldInfo field in fields)
            {
                int entries = 0;
                for (int i = 0; i < s_crossingCounterMap.Length; i++)
                    if (s_crossingCounterMap[i].Field == field.Name) entries++;

                if (entries == 1) continue;

                Debug.LogError($"  [FAIL] {field.Name} appears {entries} times in the crossing counter map");
                ok = false;
            }

            for (int i = 0; i < s_crossingCounterMap.Length; i++)
            {
                if (typeof(PerfCrossingTotals).GetField(s_crossingCounterMap[i].Field) != null) continue;

                Debug.LogError($"  [FAIL] Map entry {s_crossingCounterMap[i].Field} names no PerfCrossingTotals field");
                ok = false;
            }

            return ok;
        }

        /// <summary>Every counter the map names once, and at or after the suite's first per-frame count.</summary>
        private static bool CrossingMapCountersAreDistinct()
        {
            bool ok = true;
            for (int i = 0; i < s_crossingCounterMap.Length; i++)
            {
                if (s_crossingCounterMap[i].Counter < PerfStore.FirstPerFrameCount)
                {
                    Debug.LogError($"  [FAIL] {s_crossingCounterMap[i].Counter} is a gauge, not a per-frame count");
                    ok = false;
                    continue;
                }

                for (int later = i + 1; later < s_crossingCounterMap.Length; later++)
                {
                    if (s_crossingCounterMap[i].Counter != s_crossingCounterMap[later].Counter) continue;

                    Debug.LogError($"  [FAIL] {s_crossingCounterMap[i].Field} and " +
                                   $"{s_crossingCounterMap[later].Field} share counter {s_crossingCounterMap[i].Counter}");
                    ok = false;
                }
            }

            return ok;
        }

        /// <summary>
        /// Samples zeroed totals (the baseline), then totals carrying a distinct value per field, commits the frame and
        /// compares every counter with the value its own field was given.
        /// </summary>
        private static bool CrossingCountersMatch()
        {
            MethodInfo sample = typeof(World).GetMethod("SampleCrossingCounters", BindingFlags.NonPublic | BindingFlags.Static)
                                ?? throw new System.MissingMethodException(nameof(World), "SampleCrossingCounters");
            sample.Invoke(null, new object[] { default(PerfCrossingTotals) });

            object boxed = default(PerfCrossingTotals);
            for (int i = 0; i < s_crossingCounterMap.Length; i++)
                typeof(PerfCrossingTotals).GetField(s_crossingCounterMap[i].Field)
                    .SetValue(boxed, (i + 1) * CROSSING_FIELD_VALUE_STEP);
            sample.Invoke(null, new[] { boxed });
            PerfStore.CommitFrame(Readings(1, 1));

            bool match = true;
            for (int i = 0; i < s_crossingCounterMap.Length; i++)
            {
                long value = (i + 1) * CROSSING_FIELD_VALUE_STEP;
                long expected = s_crossingCounterMap[i].Ticks ? PerfStore.TicksToMicros(value) : value;
                long got = PerfStore.SumCounter(s_crossingCounterMap[i].Counter);
                if (ExactValue.Equal(got, expected)) continue;

                Debug.LogError($"  [FAIL] {s_crossingCounterMap[i].Field} -> {s_crossingCounterMap[i].Counter}: expected " +
                               $"{expected}, got {got}");
                match = false;
            }

            return match;
        }

        /// <summary>
        /// Calls the shared lap with a start of 0, as a pass below <see cref="PerfTier.Systems"/> hands every part:
        /// the part's total must not move, and the lap must pass that 0 on to the next part rather than invent a start.
        /// </summary>
        private static bool LapFromUntimedStartIsNoOp()
        {
            PerfCrossingTotals totals = default;
            totals.CloudsTicks = 1L;
            totals.UnloadSaveTicks = 1L;

            long end = PerfTickTotals.Lap(ref totals.CloudsTicks, 0L);
            bool ok = ExactValue.Equal(end, 0L) && ExactValue.Equal(totals.CloudsTicks, 1L);
            end = PerfTickTotals.Lap(ref totals.UnloadSaveTicks, end);
            ok &= ExactValue.Equal(end, 0L) && ExactValue.Equal(totals.UnloadSaveTicks, 1L);

            // One timed start on each side proves the other side's 0 start is not what froze it.
            long first = PerfStore.Begin();
            long timed = PerfTickTotals.Lap(ref totals.CloudsTicks, first);
            ok &= timed != 0L && totals.CloudsTicks > 1L;
            return ok;
        }

        private static FieldInfo IoStatsField(string fieldName) =>
            typeof(StorageIoStats).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new System.MissingFieldException(nameof(StorageIoStats), fieldName);

        #endregion
    }
}
