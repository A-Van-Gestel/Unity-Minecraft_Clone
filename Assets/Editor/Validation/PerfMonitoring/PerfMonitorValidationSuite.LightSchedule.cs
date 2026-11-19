using System;
using System.Reflection;
using Diagnostics;
using Editor.Validation.Framework;
using Helpers;
using UnityEngine;

namespace Editor.Validation.PerfMonitoring
{
    /// <summary>
    /// The light-schedule half of the suite: <see cref="PerfLightScheduleTotals"/> counted per scan arm and sampled into
    /// the <c>LightSched*</c> counters by <c>World.SampleLightScheduleCounters</c>. World-free: the scan and the disk-load
    /// schedule need a live world, so the probes are the arm mapping and the sampler.
    /// </summary>
    public static partial class PerfMonitorValidationSuite
    {
        /// <summary>The step between the values the mapping check gives the fields, so no two expected figures collide.</summary>
        private const long LIGHT_SCHEDULE_VALUE_STEP = 1_000L;

        /// <summary>Each light-schedule totals field and the counter it must reach.</summary>
        private static readonly (string Field, PerfCounter Counter)[] s_lightScheduleCounterMap =
        {
            (nameof(PerfLightScheduleTotals.Initial), PerfCounter.LightSchedInitial),
            (nameof(PerfLightScheduleTotals.Edge), PerfCounter.LightSchedEdge),
            (nameof(PerfLightScheduleTotals.Regular), PerfCounter.LightSchedRegular),
            (nameof(PerfLightScheduleTotals.LoadInitial), PerfCounter.LightSchedLoadInitial),
        };

        #region Scenarios

        private static bool RunB47LightScheduleSplit() => WithFreshStore(() =>
        {
            PerfStore.SetTier(PerfTier.Systems);
            bool ok = Check("Every light-schedule totals field has exactly one entry in the counter map, each a distinct per-frame count",
                LightScheduleMapIsExact());
            ok &= Check("Each scheduling scan arm counts into its own field, and every other action counts nothing",
                RecordScanCountsEachArm());
            ok &= Check("Each light-schedule totals field reaches its own counter", LightScheduleCountersMatch());
            return ok;
        });

        #endregion

        #region Helpers

        /// <summary>Requires each struct field once in the map, a real field per entry, and distinct per-frame counters.</summary>
        private static bool LightScheduleMapIsExact()
        {
            FieldInfo[] fields = typeof(PerfLightScheduleTotals).GetFields(BindingFlags.Public | BindingFlags.Instance);
            bool ok = fields.Length == s_lightScheduleCounterMap.Length;
            if (!ok) Debug.LogError($"  [FAIL] {fields.Length} light-schedule fields, {s_lightScheduleCounterMap.Length} map entries");

            foreach (FieldInfo field in fields)
            {
                int entries = 0;
                foreach ((string name, PerfCounter _) in s_lightScheduleCounterMap)
                    if (name == field.Name) entries++;

                if (entries == 1) continue;

                Debug.LogError($"  [FAIL] {field.Name} appears {entries} times in the light-schedule counter map");
                ok = false;
            }

            for (int i = 0; i < s_lightScheduleCounterMap.Length; i++)
            {
                if (s_lightScheduleCounterMap[i].Counter < PerfStore.FirstPerFrameCount)
                {
                    Debug.LogError($"  [FAIL] {s_lightScheduleCounterMap[i].Counter} is a gauge, not a per-frame count");
                    ok = false;
                }

                for (int later = i + 1; later < s_lightScheduleCounterMap.Length; later++)
                {
                    if (s_lightScheduleCounterMap[i].Counter != s_lightScheduleCounterMap[later].Counter) continue;

                    Debug.LogError($"  [FAIL] {s_lightScheduleCounterMap[i].Field} and {s_lightScheduleCounterMap[later].Field} share a counter");
                    ok = false;
                }
            }

            return ok;
        }

        /// <summary>Records every <see cref="LightingScanDecision.ScanAction"/> once and checks each field counted exactly its arm.</summary>
        private static bool RecordScanCountsEachArm()
        {
            PerfLightScheduleTotals totals = default;
            foreach (LightingScanDecision.ScanAction action in Enum.GetValues(typeof(LightingScanDecision.ScanAction)))
                totals.RecordScan(action);

            totals.RecordScan(LightingScanDecision.ScanAction.ScheduleEdge);
            totals.RecordScan(LightingScanDecision.ScanAction.ScheduleRegular);
            totals.RecordScan(LightingScanDecision.ScanAction.ScheduleRegular);

            // Distinct per arm (1 / 2 / 3), so a swapped arm cannot pass; nothing on the disk-load field.
            bool ok = ExactValue.Equal(totals.Initial, 1L) && ExactValue.Equal(totals.Edge, 2L)
                      && ExactValue.Equal(totals.Regular, 3L) && ExactValue.Equal(totals.LoadInitial, 0L);
            if (!ok)
                Debug.LogError($"  [FAIL] arm counts initial {totals.Initial}, edge {totals.Edge}, regular {totals.Regular}, " +
                               $"load {totals.LoadInitial}; expected 1 / 2 / 3 / 0");
            return ok;
        }

        /// <summary>Samples zeroed totals (the baseline), then a distinct value per field, and compares every counter.</summary>
        private static bool LightScheduleCountersMatch()
        {
            MethodInfo sample = typeof(World).GetMethod("SampleLightScheduleCounters", BindingFlags.NonPublic | BindingFlags.Static)
                                ?? throw new MissingMethodException(nameof(World), "SampleLightScheduleCounters");
            sample.Invoke(null, new object[] { default(PerfLightScheduleTotals) });

            object boxed = default(PerfLightScheduleTotals);
            for (int i = 0; i < s_lightScheduleCounterMap.Length; i++)
                typeof(PerfLightScheduleTotals).GetField(s_lightScheduleCounterMap[i].Field)
                    .SetValue(boxed, (i + 1) * LIGHT_SCHEDULE_VALUE_STEP);
            sample.Invoke(null, new[] { boxed });
            PerfStore.CommitFrame(Readings(1, 1));

            bool match = true;
            for (int i = 0; i < s_lightScheduleCounterMap.Length; i++)
            {
                long expected = (i + 1) * LIGHT_SCHEDULE_VALUE_STEP;
                long got = PerfStore.SumCounter(s_lightScheduleCounterMap[i].Counter);
                if (ExactValue.Equal(got, expected)) continue;

                Debug.LogError($"  [FAIL] {s_lightScheduleCounterMap[i].Field} -> {s_lightScheduleCounterMap[i].Counter}: " +
                               $"expected {expected}, got {got}");
                match = false;
            }

            return match;
        }

        #endregion
    }
}
