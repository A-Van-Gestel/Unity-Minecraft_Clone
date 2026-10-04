using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Benchmarks;
using Diagnostics;
using Editor.Dev;
using Editor.Validation.Framework;
using UnityEditor;
using Debug = UnityEngine.Debug;
using Random = System.Random;

namespace Editor.Validation.PerfMonitoring
{
    /// <summary>
    /// Truth-table suite for the performance monitor's store: <see cref="PerfFrameRing"/>'s wrap-around and
    /// slot-column lifecycle, <see cref="PerfWindowStats"/>' exact percentiles against a sorted oracle,
    /// <see cref="PerfStore"/>'s tier gating, frame boundary and shutdown, and the
    /// <see cref="WorldFrameProfiler"/> facade's slot mapping and bit-identical published values. The Frame-tier
    /// scenarios (B11–B19: GC readings, frame-timing back-fill, hitch detection) live in the <c>.Frame</c> part.
    /// <para>
    /// Timing <i>values</i> are not asserted beyond "zero" versus "positive" — wall-clock durations are not
    /// deterministic. Every scenario that touches the static store starts and ends from
    /// <c>PerfStore.DomainReset</c> and restores the tier and force flag it found, so running the suite in
    /// play mode discards the history ring but leaves the session's settings in force.
    /// </para>
    /// </summary>
    public static partial class PerfMonitorValidationSuite
    {
        private const int SMALL_RING_CAPACITY = 4;
        private const int SMALL_RING_SLOTS = 3;
        private const int SMALL_RING_FRAMES = 6;
        private const float CPU_OFFSET_MS = 10f;
        private const double TEST_TICK_TO_MS = 0.5;
        private const long SLOT_TICK_STEP = 1000;

        /// <summary>Busy-wait per probed region — long enough that a recorded region can never read 0 ticks.</summary>
        private const double SPIN_MS = 0.2;

        private const int STATS_SEED = 12345;
        private const int STATS_TRIALS = 20;
        private const float STATS_MAX_MS = 50f;
        private const long TEN_MS_DIVISOR = 100;
        private const double MILLISECONDS_PER_SECOND = 1000.0;

        /// <summary>Sample shapes cycled across trials: random, constant, ascending, descending.</summary>
        private const int SAMPLE_PATTERN_COUNT = 4;

        /// <summary>Injected tick counts are distinct and non-round (a prime stride), so a float round-trip or a swapped slot cannot match by accident.</summary>
        private const long BASE_INJECTED_TICKS = 123_456_789L;

        private const long INJECTED_TICK_STRIDE = 7_919L;

        /// <summary>Runs every scenario and prints a categorized summary via the shared runner.</summary>
        [MenuItem("Minecraft Clone/Dev/Validate Performance Monitor", priority = DevMenuPriority.Validation)]
        public static void RunAll() => Execute();

        /// <summary>Builds and runs the scenarios, returning the categorized result (the headless/CI entry point).</summary>
        /// <param name="logToConsole">When false, runs silently and only returns the result.</param>
        /// <param name="showProgress">When false, suppresses this suite's own progress bar.</param>
        /// <returns>The categorized, timed result of the run.</returns>
        public static ValidationRunResult Execute(bool logToConsole = true, bool showProgress = true)
        {
            List<Scenario> scenarios = new List<Scenario>
            {
                new Scenario("B1 Ring wrap-around: newest-first ages, oldest overwritten, bounds enforced", RunB1RingWrap),
                new Scenario("B2 Slot columns: allocate/release lifecycle; frames without columns carry no slot data", RunB2SlotColumns),
                new Scenario("B3 Window stats: nearest-rank index table + exact match against a sorted oracle", RunB3WindowStats),
                new Scenario("B4 Tier gating: slots record at Systems+ or when forced; clearing force keeps the tier", RunB4TierGating),
                new Scenario("B5 Below-tier and zero-start probes write nothing; an active region records time", RunB5ProbeNoOps),
                new Scenario("B6 Frame boundary: a frame without probes commits 0, not the previous frame", RunB6FrameBoundary),
                new Scenario("B7 Shutdown frees the ring and blocks reallocation; DomainReset restores a fresh store", RunB7ShutdownAndReset),
                new Scenario("B8 Facade mapping: WorldFrameProfiler.Phase matches PerfSlot by name and value", RunB8FacadeMapping),
                new Scenario("B9 Facade values: LastFrameMs is bit-identical to ticks × (1000 / Frequency)", RunB9FacadeBitIdentity),
                new Scenario("B10 Facade Enabled is the force flag: on at Basic records, off never disables Systems", RunB10FacadeEnabled),
                new Scenario("B11 GC readings: no baseline, measured growth, collection, unexplained shrink, saturation", RunB11GcReadings),
                new Scenario("B12 GC statistics take only measured frames; collections are summed", RunB12GcStatistics),
                new Scenario("B13 Frame timings back-fill the row whose interval holds their start; misses stay NaN", RunB13FrameTimingBackfill),
                new Scenario("B14 Frame-tier resources: timing reader and detector exist only at Frame+, slot block only at Systems+", RunB14FrameTierResources),
                new Scenario("B15 Hitch threshold: absolute floor, median factor after the first refresh, setting fallbacks", RunB15HitchThreshold),
                new Scenario("B16 Hitch window: closes after the frames after it, rows oldest first across the ring wrap", RunB16HitchWindow),
                new Scenario("B17 Hitches in an open window join it; the newest records are kept", RunB17HitchMergeAndRetention),
                new Scenario("B18 Hitch attribution: GC-correlated flag, top three slots at Systems, none at Frame", RunB18HitchAttribution),
                new Scenario("B19 Leaving Frame discards the detector and its open window; shutdown frees Frame resources", RunB19TierDropAndShutdown),
            };
            return ValidationSuiteRunner.Execute("Performance Monitor", scenarios, KnownBugChannel.Unimplemented, logToConsole, showProgress);
        }

        #region Scenarios

        private static bool RunB1RingWrap()
        {
            using PerfFrameRing ring = new PerfFrameRing(SMALL_RING_CAPACITY, SMALL_RING_SLOTS);
            long[] ticks = new long[SMALL_RING_SLOTS];

            for (int i = 0; i < SMALL_RING_FRAMES; i++)
                ring.Commit(new PerfFrame { FrameIndex = i, WallMs = i, CpuMs = CPU_OFFSET_MS + i }, ticks, TEST_TICK_TO_MS);

            bool ok = Check("Count capped at capacity", ring.Count == SMALL_RING_CAPACITY);
            ok &= Check("Age 0 is the newest frame", ring.GetFrame(0).FrameIndex == SMALL_RING_FRAMES - 1);
            ok &= Check("Oldest held frame is the first not overwritten",
                ring.GetFrame(SMALL_RING_CAPACITY - 1).FrameIndex == SMALL_RING_FRAMES - SMALL_RING_CAPACITY);

            float[] wall = new float[SMALL_RING_CAPACITY + 1];
            float[] cpu = new float[SMALL_RING_CAPACITY + 1];
            int wallCount = ring.CopyWallMs(wall);
            int cpuCount = ring.CopyCpuMs(cpu);
            bool orderOk = wallCount == SMALL_RING_CAPACITY && cpuCount == SMALL_RING_CAPACITY;
            for (int age = 0; orderOk && age < SMALL_RING_CAPACITY; age++)
            {
                int frame = SMALL_RING_FRAMES - 1 - age;
                orderOk = ExactValue.Equal(wall[age], frame) && ExactValue.Equal(cpu[age], CPU_OFFSET_MS + frame);
            }

            ok &= Check("Copies return newest-first values, count = Count", orderOk);
            ok &= Check("Reading past Count throws", Throws(() => ring.GetFrame(SMALL_RING_CAPACITY)));
            ok &= Check("No slot data without slot columns", ring.SlotFrameCount == 0 && ring.CopySlotMs(PerfSlot.Apply, wall) == 0);
            return ok;
        }

        private static bool RunB2SlotColumns()
        {
            PerfFrameRing ring = new PerfFrameRing(SMALL_RING_CAPACITY, SMALL_RING_SLOTS);
            long[] ticks = new long[SMALL_RING_SLOTS];
            bool ok;
            try
            {
                ring.Commit(new PerfFrame { FrameIndex = 0 }, ticks, TEST_TICK_TO_MS);
                ring.AllocateSlotColumns();
                ok = Check("Columns allocated, no slot frames yet", ring.HasSlotColumns && ring.SlotFrameCount == 0);

                for (int i = 0; i <= SMALL_RING_CAPACITY; i++)
                {
                    ticks[(int)PerfSlot.Apply] = SLOT_TICK_STEP * i;
                    ring.Commit(new PerfFrame { FrameIndex = i + 1 }, ticks, TEST_TICK_TO_MS);
                }

                ok &= Check("SlotFrameCount capped at capacity", ring.SlotFrameCount == SMALL_RING_CAPACITY);
                ok &= Check("Newest slot value = ticks × tickToMs",
                    ExactValue.Equal(ring.GetSlotMs(0, PerfSlot.Apply), (float)(SLOT_TICK_STEP * SMALL_RING_CAPACITY * TEST_TICK_TO_MS)));
                ok &= Check("Untouched slot reads 0", ExactValue.IsZero(ring.GetSlotMs(0, PerfSlot.Tick)));

                ring.ReleaseSlotColumns();
                ok &= Check("Release drops the columns but keeps the frames",
                    !ring.HasSlotColumns && ring.SlotFrameCount == 0 && ring.Count == SMALL_RING_CAPACITY);
                ok &= Check("Reading a slot after release throws", Throws(() => ring.GetSlotMs(0, PerfSlot.Apply)));
            }
            finally
            {
                ring.Dispose();
            }

            ok &= Check("Dispose marks disposed and empties", ring.IsDisposed && ring.Count == 0);
            ring.Dispose();
            ok &= Check("A second Dispose is safe", ring.IsDisposed);
            ok &= Check("Allocating after Dispose throws", Throws(ring.AllocateSlotColumns));
            return ok;
        }

        private static bool RunB3WindowStats()
        {
            // Hand-computed ⌈p·n/100⌉ − 1, so the oracle below does not rest on the formula under test.
            int[,] rankTable =
            {
                // count, p50 index, p99 index
                { 1, 0, 0 }, { 2, 0, 1 }, { 3, 1, 2 }, { 100, 49, 98 }, { 101, 50, 99 }, { 2048, 1023, 2027 },
            };

            bool ok = true;
            for (int row = 0; row < rankTable.GetLength(0); row++)
            {
                int n = rankTable[row, 0];
                ok &= Check($"Nearest-rank indices for n={n}",
                    PerfWindowStats.NearestRankIndex(n, PerfWindowStats.MedianPercentile) == rankTable[row, 1] &&
                    PerfWindowStats.NearestRankIndex(n, PerfWindowStats.HighPercentile) == rankTable[row, 2]);
            }

            Random random = new Random(STATS_SEED);
            int[] sizes = { 1, 2, 3, 99, 100, 101, PerfStore.RingCapacity };
            bool oracleOk = true;
            for (int trial = 0; trial < STATS_TRIALS; trial++)
            {
                foreach (int size in sizes)
                {
                    float[] samples = new float[size];
                    FillSamples(samples, random, trial);
                    oracleOk &= MatchesOracle(samples, $"trial {trial}, n={size}");
                }
            }

            ok &= Check("Summaries match the sorted oracle (random, constant, ascending, descending)", oracleOk);
            ok &= Check("An empty window summarizes to all zero", PerfWindowStats.Summarize(new float[1], 0).Count == 0);
            return ok;
        }

        private static bool RunB4TierGating() => WithFreshStore(() =>
        {
            bool ok = Check("Basic: slots inactive", !PerfStore.SlotsActive && PerfStore.Begin() == 0 && PerfStore.Begin(PerfSlot.Tick) == 0);

            PerfStore.ForceSlots = true;
            ok &= Check("Basic + forced: slots active", PerfStore.SlotsActive);
            PerfStore.ForceSlots = false;

            PerfStore.SetTier(PerfTier.Frame);
            ok &= Check("Frame: slots inactive", !PerfStore.SlotsActive);

            PerfStore.SetTier(PerfTier.Systems);
            ok &= Check("Systems: slots active", PerfStore.SlotsActive);

            PerfStore.SetTier(PerfTier.Capture);
            ok &= Check("Capture: slots active", PerfStore.SlotsActive);

            PerfStore.ForceSlots = true;
            PerfStore.ForceSlots = false;
            ok &= Check("Clearing force at Capture keeps slots active", PerfStore.SlotsActive);
            return ok;
        });

        private static bool RunB5ProbeNoOps() => WithFreshStore(() =>
        {
            long slotless = PerfStore.Begin();
            Spin();
            PerfStore.Accumulate(PerfSlot.Apply, slotless);
            long slotted = PerfStore.Begin(PerfSlot.Tick);
            Spin();
            PerfStore.End(PerfSlot.Tick, slotted);
            PerfStore.PublishSlots(0, PerfStore.SlotCount);
            bool ok = Check("Below-tier probes record nothing", AllPublishedZero());

            PerfStore.SetTier(PerfTier.Systems);
            PerfStore.End(PerfSlot.Tick, 0L);
            PerfStore.Accumulate(PerfSlot.Apply, 0L);
            PerfStore.PublishSlots(0, PerfStore.SlotCount);
            ok &= Check("A zero start is a no-op even while active", AllPublishedZero());

            long active = PerfStore.Begin(PerfSlot.Tick);
            Spin();
            PerfStore.End(PerfSlot.Tick, active);
            PerfStore.PublishSlots(0, PerfStore.SlotCount);
            ok &= Check("Positive control: an active region records time", PerfStore.PublishedMs(PerfSlot.Tick) > 0.0);
            return ok;
        });

        private static bool RunB6FrameBoundary() => WithFreshStore(() =>
        {
            PerfStore.SetTier(PerfTier.Systems);
            long wallTicks = Stopwatch.Frequency / TEN_MS_DIVISOR;

            long start = PerfStore.Begin(PerfSlot.Tick);
            Spin();
            PerfStore.End(PerfSlot.Tick, start);
            PerfStore.CommitFrame(Readings(wallTicks, 1));
            PerfStore.CommitFrame(Readings(wallTicks, 2));

            PerfFrameRing ring = GetStoreRing();
            bool ok = Check("Two frames held, both with slot columns",
                ring != null && PerfStore.FramesRecorded == 2 && PerfStore.SlotFramesRecorded == 2);
            if (ring == null) return false;

            ok &= Check("Probed frame recorded time", ring.GetSlotMs(1, PerfSlot.Tick) > 0f);
            ok &= Check("Frame without probes recorded 0", ExactValue.IsZero(ring.GetSlotMs(0, PerfSlot.Tick)));
            ok &= Check("Wall time converted from ticks",
                ExactValue.Equal(ring.GetFrame(0).WallMs, (float)(wallTicks * (MILLISECONDS_PER_SECOND / Stopwatch.Frequency))));
            ok &= Check("Wall summary covers both frames", PerfStore.SummarizeWallMs().Count == 2);

            PerfStore.SetTier(PerfTier.Basic);
            ok &= Check("Dropping below Systems frees the slot columns, keeps the frames",
                !ring.HasSlotColumns && PerfStore.SlotFramesRecorded == 0 && PerfStore.FramesRecorded == 2);
            ok &= Check("No slot summary without slot columns", PerfStore.SummarizeSlotMs(PerfSlot.Tick).Count == 0);
            return ok;
        });

        private static bool RunB7ShutdownAndReset() => WithFreshStore(() =>
        {
            PerfStore.SetTier(PerfTier.Systems);
            PerfStore.CommitFrame(Readings(1, 1));
            PerfFrameRing ring = GetStoreRing();

            InvokeStorePrivate("ShutDown");
            bool ok = Check("Shutdown disposes the ring", ring != null && ring.IsDisposed && GetStoreRing() == null);

            PerfStore.CommitFrame(Readings(1, 2));
            ok &= Check("A commit after shutdown does not reallocate", GetStoreRing() == null && PerfStore.FramesRecorded == 0);

            PerfStore.ForceSlots = true;
            InvokeStorePrivate("DomainReset");
            ok &= Check("DomainReset restores Basic, unforced, inactive",
                PerfStore.Tier == PerfTier.Basic && !PerfStore.ForceSlots && !PerfStore.SlotsActive);

            PerfStore.CommitFrame(Readings(1, 3));
            ok &= Check("Commits work again after DomainReset", PerfStore.FramesRecorded == 1);
            return ok;
        });

        private static bool RunB8FacadeMapping()
        {
            Array phases = Enum.GetValues(typeof(WorldFrameProfiler.Phase));
            bool ok = Check("PhaseCount matches the Phase enum", phases.Length == WorldFrameProfiler.PhaseCount);
            ok &= Check("PerfSlot holds every phase", PerfStore.SlotCount >= WorldFrameProfiler.PhaseCount);

            bool mapped = true;
            foreach (WorldFrameProfiler.Phase phase in phases)
            {
                int index = (int)phase;
                mapped &= index < PerfStore.SlotCount && phase.ToString() == ((PerfSlot)index).ToString();
            }

            ok &= Check("Every Phase has the PerfSlot of the same value and name", mapped);
            return ok;
        }

        private static bool RunB9FacadeBitIdentity() => WithFreshStore(() =>
        {
            long[] slotTicks = GetStoreSlotTicks();
            WorldFrameProfiler.Enabled = true;
            WorldFrameProfiler.BeginFrame();

            for (int i = 0; i < WorldFrameProfiler.PhaseCount; i++)
                slotTicks[i] = BASE_INJECTED_TICKS + INJECTED_TICK_STRIDE * i;

            WorldFrameProfiler.EndFrame();

            double tickToMs = MILLISECONDS_PER_SECOND / Stopwatch.Frequency;
            bool exact = true;
            for (int i = 0; i < WorldFrameProfiler.PhaseCount; i++)
                exact &= ExactValue.Equal(WorldFrameProfiler.LastFrameMs((WorldFrameProfiler.Phase)i), slotTicks[i] * tickToMs);

            bool ok = Check("Every phase publishes ticks × (1000.0 / Frequency) exactly", exact);
            ok &= Check("LastFrameMeshMs is the sum of its two slots",
                ExactValue.Equal(WorldFrameProfiler.LastFrameMeshMs,
                    WorldFrameProfiler.LastFrameMeshProcessMs + WorldFrameProfiler.LastFrameMeshScheduleMs));

            double frozen = WorldFrameProfiler.LastFrameTickMs;
            WorldFrameProfiler.Enabled = false;
            WorldFrameProfiler.BeginFrame();
            slotTicks[(int)WorldFrameProfiler.Phase.Tick] = 1;
            WorldFrameProfiler.EndFrame();
            ok &= Check("Disabled BeginFrame/EndFrame leave the published values alone",
                ExactValue.Equal(WorldFrameProfiler.LastFrameTickMs, frozen));
            return ok;
        });

        private static bool RunB10FacadeEnabled() => WithFreshStore(() =>
        {
            WorldFrameProfiler.Enabled = true;
            bool ok = Check("Enabled at Basic activates slots and reads back", PerfStore.SlotsActive && WorldFrameProfiler.Enabled);

            PerfStore.SetTier(PerfTier.Systems);
            WorldFrameProfiler.Enabled = false;
            ok &= Check("Disabling at Systems keeps slots active", PerfStore.SlotsActive && !WorldFrameProfiler.Enabled);

            PerfStore.SetTier(PerfTier.Basic);
            ok &= Check("Basic and not enabled: slots inactive", !PerfStore.SlotsActive);
            return ok;
        });

        #endregion

        #region Helpers

        /// <summary>Runs a scenario against a freshly reset store, then resets again and restores the tier, force flag and hitch thresholds.</summary>
        private static bool WithFreshStore(Func<bool> scenario)
        {
            PerfTier tier = PerfStore.Tier;
            bool forced = PerfStore.ForceSlots;
            float hitchMinMs = PerfStore.HitchMinMs;
            float hitchMedianFactor = PerfStore.HitchMedianFactor;
            try
            {
                InvokeStorePrivate("DomainReset");
                return scenario();
            }
            finally
            {
                InvokeStorePrivate("DomainReset");
                PerfStore.SetHitchThresholds(hitchMinMs, hitchMedianFactor);
                PerfStore.SetTier(tier);
                PerfStore.ForceSlots = forced;
            }
        }

        private static void FillSamples(float[] samples, Random random, int trial)
        {
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] = (trial % SAMPLE_PATTERN_COUNT) switch
                {
                    0 => (float)(random.NextDouble() * STATS_MAX_MS),
                    1 => STATS_MAX_MS,
                    2 => i,
                    _ => samples.Length - i,
                };
            }
        }

        private static bool MatchesOracle(float[] samples, string label)
        {
            int n = samples.Length;
            float[] sorted = (float[])samples.Clone();
            Array.Sort(sorted);

            double sum = 0;
            foreach (float value in samples) sum += value;

            PerfWindowSummary summary = PerfWindowStats.Summarize(samples, n);
            bool match = summary.Count == n
                         && ExactValue.Equal(summary.Max, sorted[n - 1])
                         && ExactValue.Equal(summary.Mean, (float)(sum / n))
                         && ExactValue.Equal(summary.P50, sorted[PerfWindowStats.NearestRankIndex(n, PerfWindowStats.MedianPercentile)])
                         && ExactValue.Equal(summary.P99, sorted[PerfWindowStats.NearestRankIndex(n, PerfWindowStats.HighPercentile)]);

            if (!match)
                Debug.LogError($"  [FAIL] Oracle mismatch ({label}): got max {summary.Max} p50 {summary.P50} p99 {summary.P99}");
            return match;
        }

        private static bool AllPublishedZero()
        {
            for (int i = 0; i < PerfStore.SlotCount; i++)
            {
                if (!ExactValue.IsZero(PerfStore.PublishedMs((PerfSlot)i))) return false;
            }

            return true;
        }

        private static void Spin()
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed.TotalMilliseconds < SPIN_MS)
            {
            }
        }

        private static bool Throws(Action action)
        {
            try
            {
                action();
                return false;
            }
            catch (Exception)
            {
                return true;
            }
        }

        private static void InvokeStorePrivate(string methodName)
        {
            MethodInfo method = typeof(PerfStore).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)
                                ?? throw new MissingMethodException(nameof(PerfStore), methodName);
            method.Invoke(null, null);
        }

        /// <summary>Readings with equal wall and CPU ticks and no heap, collection or timestamp data.</summary>
        private static PerfFrameReadings Readings(long wallTicks, int frameIndex) =>
            new PerfFrameReadings { WallTicks = wallTicks, CpuTicks = wallTicks, FrameIndex = frameIndex };

        private static PerfFrameRing GetStoreRing() => (PerfFrameRing)GetStoreField("s_ring");

        private static long[] GetStoreSlotTicks() => (long[])GetStoreField("s_slotTicks");

        private static object GetStoreField(string fieldName)
        {
            FieldInfo field = typeof(PerfStore).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static)
                              ?? throw new MissingFieldException(nameof(PerfStore), fieldName);
            return field.GetValue(null);
        }

        /// <summary>Logs a single assertion as PASS/FAIL and returns its result for AND-chaining.</summary>
        private static bool Check(string label, bool condition)
        {
            if (condition) Debug.Log($"  [PASS] {label}");
            else Debug.LogError($"  [FAIL] {label}");
            return condition;
        }

        #endregion
    }
}
