using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Data;
using Diagnostics;
using Editor.Validation.Framework;
using Helpers;
using Jobs;
using Unity.Collections;
using UnityEngine;

namespace Editor.Validation.PerfMonitoring
{
    /// <summary>
    /// The startup half of the suite: <see cref="StartupTimeline"/> driven by injected clock, frame times and drain
    /// results — the drain run and its stamp, the settled-window rule with its slack, interruption, timeout, the anchor
    /// fallback and <see cref="WorldLaunchState"/>'s one-shot scene request — and the <see cref="StartupPhaseTotals"/> report.
    /// Every value is exact.
    /// </summary>
    public static partial class PerfMonitorValidationSuite
    {
        private const double STARTUP_REQUEST_AT = 9.0;
        private const double STARTUP_AWAKE_AT = 10.0;
        private const double STARTUP_LOADS_AT = 11.0;
        private const double STARTUP_HANDOFF_AT = 12.0;
        private const double STARTUP_FRAME_SECONDS = 0.001;
        private const float STARTUP_FRAME_MS = 1f;

        /// <summary>Frames of <see cref="STARTUP_FRAME_MS"/> that exactly fill the settle window.</summary>
        private const int STARTUP_WINDOW_FRAMES = 2000;

        /// <summary>Within the slack bound at a 1 ms median (1 + 2 ms), above the 1.25× factor bound.</summary>
        private const float STARTUP_TOLERATED_SPIKE_MS = 2.9f;

        /// <summary>Above the slack bound at a 1 ms median.</summary>
        private const float STARTUP_REJECTED_SPIKE_MS = 3.5f;

        private const float FACTOR_BOUND_MEDIAN_MS = 20f;
        private const float FACTOR_BOUND_MS = 25f;
        private const float CROSSOVER_MEDIAN_MS = 8f;
        private const float CROSSOVER_BOUND_MS = 10f;
        private const double TIMEOUT_MARGIN_SECONDS = 0.001;

        // One sweep's route fixture; the report prints sweep 1 as is and the 18-sweep total as 18 times it.
        private const int ROUTE_EMITTED = 20;
        private const int ROUTE_DROPS = 1;
        private const int ROUTE_PERSISTS = 2;
        private const int ROUTE_DEFERS = 7;
        private const int ROUTE_DIRECT = 10;
        private const int ROUTE_DRAINED = 7;
        private const int ROUTE_EFFECTIVE_DIRECT = 4;
        private const int ROUTE_EFFECTIVE_DRAINED = 3;
        private const int ROUTE_EFFECTIVE_PULL_BACK = 2;
        private const int ROUTE_SUPPORT_SCANS = 5;
        private const int ROUTE_SAME_TARGET_MODS = 5;
        private const int ROUTE_SAMPLES = 8;
        private const int ROUTE_CENSUS_MODS = 12;
        private const int ROUTE_CENSUS_REPEATS = 5;
        private const int ROUTE_CENSUS_REDUNDANT = 3;
        private const int ROUTE_CENSUS_REMOVALS = 2;

        private static readonly ChunkCoord s_startChunk = new ChunkCoord(3, -4);
        private static readonly ChunkCoord s_movedChunk = new ChunkCoord(4, -4);

        #region Scenarios

        private static bool RunB38StartupDrainAndStable()
        {
            StartupTimeline early = new StartupTimeline();
            early.MarkAwake(STARTUP_AWAKE_AT, STARTUP_REQUEST_AT);
            bool ok = Check("No tick before the handoff", !early.Tick(STARTUP_AWAKE_AT, STARTUP_FRAME_MS, true, s_startChunk)
                                                       && early.IsPending && !early.NeedsDrainCheck);

            StartupTimeline timeline = HandedOffTimeline();
            ok &= Check("Marks are measured from the scene request",
                timeline.AnchoredOnSceneRequest
                && ExactValue.Equal(timeline.MillisecondsTo(StartupStage.Awake), 1000.0)
                && ExactValue.Equal(timeline.MillisecondsTo(StartupStage.LoadsDone), 2000.0)
                && ExactValue.Equal(timeline.MillisecondsTo(StartupStage.Handoff), 3000.0));
            ok &= Check("Handed off and waiting for the drain", timeline.NeedsDrainCheck
                                                                && timeline.LastStage == StartupStage.Handoff);

            // A run one frame short of the settle count, broken by an undrained frame, does not count.
            double now = STARTUP_HANDOFF_AT;
            for (int i = 0; i < StartupTimeline.DrainSettleFrames - 1; i++)
                timeline.Tick(now += STARTUP_FRAME_SECONDS, STARTUP_FRAME_MS, true, s_startChunk);
            timeline.Tick(now += STARTUP_FRAME_SECONDS, STARTUP_FRAME_MS, false, s_startChunk);
            ok &= Check("A broken drain run does not stamp the drain", !timeline.Reached(StartupStage.Drained));

            double runStart = now + STARTUP_FRAME_SECONDS;
            for (int i = 0; i < StartupTimeline.DrainSettleFrames; i++)
                timeline.Tick(now += STARTUP_FRAME_SECONDS, STARTUP_FRAME_MS, true, s_startChunk);
            ok &= Check("The drain is stamped at the first frame of the settled run",
                timeline.Reached(StartupStage.Drained) && !timeline.NeedsDrainCheck
                && ExactValue.Equal(timeline.MillisecondsTo(StartupStage.Drained), (runStart - STARTUP_REQUEST_AT) * 1000.0));

            // A spike inside the slack bound settles once the window spans its length — one frame early, because the
            // spike's extra 1.9 ms already covers part of the last frame.
            int finishedAt = FeedUntilFinished(timeline, ref now, STARTUP_TOLERATED_SPIKE_MS);
            ok &= Check("A tolerated spike settles on the frame that fills the window", finishedAt == STARTUP_WINDOW_FRAMES - 1);
            ok &= Check("Stable outcome with the window's median and worst frame",
                timeline.Outcome == StartupOutcome.Stable && timeline.LastStage == StartupStage.Stable
                && ExactValue.Equal(timeline.StableWindowMedianMs, STARTUP_FRAME_MS)
                && ExactValue.Equal(timeline.StableWindowWorstMs, STARTUP_TOLERATED_SPIKE_MS)
                && ExactValue.Equal(timeline.MillisecondsTo(StartupStage.Stable), (now - STARTUP_REQUEST_AT) * 1000.0));
            ok &= Check("A finished timeline ignores later frames",
                !timeline.Tick(now + STARTUP_FRAME_SECONDS, STARTUP_FRAME_MS, true, s_movedChunk)
                && timeline.Outcome == StartupOutcome.Stable);

            // A spike above the bound holds the window back until it slides out.
            StartupTimeline spiked = DrainedTimeline(out double spikedNow);
            int spikedAt = FeedUntilFinished(spiked, ref spikedNow, STARTUP_REJECTED_SPIKE_MS);
            ok &= Check("A rejected spike delays the settle until the window excludes it",
                spikedAt == STARTUP_WINDOW_FRAMES + 1 && ExactValue.Equal(spiked.StableWindowWorstMs, STARTUP_FRAME_MS));

            ok &= Check("Bound: factor above 8 ms, slack below it, equal at it",
                ExactValue.Equal(StartupTimeline.StableBoundMs(FACTOR_BOUND_MEDIAN_MS), FACTOR_BOUND_MS)
                && ExactValue.Equal(StartupTimeline.StableBoundMs(STARTUP_FRAME_MS), STARTUP_FRAME_MS + StartupTimeline.StableSlackMs)
                && ExactValue.Equal(StartupTimeline.StableBoundMs(CROSSOVER_MEDIAN_MS), CROSSOVER_BOUND_MS));
            return ok;
        }

        private static bool RunB39StartupInterruptAndTimeout()
        {
            StartupTimeline interrupted = DrainedTimeline(out double now);
            bool finished = interrupted.Tick(now + STARTUP_FRAME_SECONDS, STARTUP_FRAME_MS, true, s_movedChunk);
            bool ok = Check("Leaving the start chunk interrupts at the last mark",
                finished && interrupted.Outcome == StartupOutcome.Interrupted
                && interrupted.LastStage == StartupStage.Drained && !interrupted.Reached(StartupStage.Stable));
            ok &= Check("An interrupted timeline stays interrupted",
                !interrupted.Tick(now + 2 * STARTUP_FRAME_SECONDS, STARTUP_FRAME_MS, true, s_startChunk)
                && interrupted.Outcome == StartupOutcome.Interrupted);

            StartupTimeline stalled = HandedOffTimeline();
            double limit = STARTUP_HANDOFF_AT + StartupTimeline.TimeoutSeconds;
            ok &= Check("Not timed out at the limit", !stalled.Tick(limit, STARTUP_FRAME_MS, false, s_startChunk) && stalled.IsPending);
            ok &= Check("Timed out past the limit, at the handoff",
                stalled.Tick(limit + TIMEOUT_MARGIN_SECONDS, STARTUP_FRAME_MS, false, s_startChunk)
                && stalled.Outcome == StartupOutcome.TimedOut && stalled.LastStage == StartupStage.Handoff);

            // A drained timeline whose frame time never settles times out on the same clock, under the other label.
            StartupTimeline unsettled = DrainedTimeline(out _);
            unsettled.Tick(limit + TIMEOUT_MARGIN_SECONDS, STARTUP_REJECTED_SPIKE_MS, true, s_startChunk);
            ok &= Check("A timeout names the stage it stalled in: before the drain, or after it",
                stalled.OutcomeDescription.Contains("before the drain")
                && unsettled.Outcome == StartupOutcome.TimedOut
                && !unsettled.OutcomeDescription.Contains("before the drain")
                && unsettled.OutcomeDescription.Contains("did not settle"));

            StartupTimeline direct = new StartupTimeline();
            direct.MarkAwake(STARTUP_AWAKE_AT, WorldLaunchState.NoSceneRequest);
            StartupTimeline future = new StartupTimeline();
            future.MarkAwake(STARTUP_AWAKE_AT, STARTUP_AWAKE_AT + 1.0);
            ok &= Check("Without a usable scene request the anchor is Awake",
                !direct.AnchoredOnSceneRequest && ExactValue.Equal(direct.MillisecondsTo(StartupStage.Awake), 0.0)
                && !future.AnchoredOnSceneRequest && ExactValue.Equal(future.MillisecondsTo(StartupStage.Awake), 0.0));
            ok &= Check("Unreached marks read NaN", double.IsNaN(direct.MillisecondsTo(StartupStage.Drained)));

            double saved = WorldLaunchState.WorldSceneRequestedAt;
            try
            {
                WorldLaunchState.MarkWorldSceneRequested();
                double first = WorldLaunchState.TakeWorldSceneRequest();
                double second = WorldLaunchState.TakeWorldSceneRequest();
                ok &= Check("The scene request is taken once",
                    first >= 0.0 && ExactValue.Equal(second, WorldLaunchState.NoSceneRequest));
            }
            finally
            {
                WorldLaunchState.WorldSceneRequestedAt = saved;
            }

            return ok;
        }

        private static bool RunB41StartupPhaseReport()
        {
            StartupPhaseTotals unfinished = new StartupPhaseTotals();
            StringBuilder unfinishedReport = new StringBuilder();
            unfinished.AppendReport(unfinishedReport);
            bool ok = Check("An unfinished coroutine reports that it was not recorded",
                unfinishedReport.ToString().Contains("not recorded (the coroutine did not reach its report)"));

            StartupPhaseTotals totals = new StartupPhaseTotals
            {
                Recorded = true,
                GenerationWallTicks = StartupPhaseTotals.MsToTicks(1400.0),
                GenerationIterations = 83,
                GenerationQuotaStops = 80,
                GenerationPassTicks = StartupPhaseTotals.MsToTicks(500.0),
                GenerationApplyTicks = StartupPhaseTotals.MsToTicks(100.0),
                GenerationJobs = 529,
                GenerationStructureMods = 400000,
                GenerationCollections = 27,
                TailReportTicks = StartupPhaseTotals.MsToTicks(10.0),
                TailSpawnTicks = StartupPhaseTotals.MsToTicks(20.0),
                TailCloudsTicks = StartupPhaseTotals.MsToTicks(30.0),
                TailBorderWallTicks = StartupPhaseTotals.MsToTicks(40.0),
                TailCollections = 14,
                TailClouds = new StartupPhaseTotals.CloudsSplit
                {
                    PatternTicks = StartupPhaseTotals.MsToTicks(2.0),
                    ThresholdTicks = StartupPhaseTotals.MsToTicks(3.0),
                    MeshTicks = StartupPhaseTotals.MsToTicks(15.0),
                    TileTicks = StartupPhaseTotals.MsToTicks(6.0),
                    MeshesBuilt = 128,
                    TilesCreated = 242,
                    PatternCollections = 1,
                    ThresholdCollections = 2,
                    MeshCollections = 5,
                    TileCollections = 3,
                    Collections = 12,
                },
            };

            // Two sweeps past the cap, so the overflow row folds exactly sweeps 17 and 18.
            for (int i = 0; i < StartupPhaseTotals.MaxRecordedSweeps + 2; i++)
            {
                totals.AddSweep(new StartupPhaseTotals.Sweep
                {
                    RecalcTicks = StartupPhaseTotals.MsToTicks(i + 1),
                    ScheduleTicks = StartupPhaseTotals.MsToTicks(10.0),
                    AcquireTicks = StartupPhaseTotals.MsToTicks(5.0),
                    CompleteTicks = StartupPhaseTotals.MsToTicks(20.0),
                    MergeTicks = StartupPhaseTotals.MsToTicks(30.0),
                    MergeApplyTicks = StartupPhaseTotals.MsToTicks(1.0),
                    MergeDrainTicks = StartupPhaseTotals.MsToTicks(2.0),
                    MergeRouteTicks = StartupPhaseTotals.MsToTicks(3.0),
                    MergeCascadeTicks = StartupPhaseTotals.MsToTicks(4.0),
                    Recalcs = 2,
                    Jobs = i + 1,
                    CrossRouted = 3,
                    CrossEffective = 1,
                    Collections = 1,
                    SharedSnapshots = 7,
                    Route = RouteFixture(),
                });
            }

            // A comma-decimal culture: the report must still read the same on every machine.
            CultureInfo savedCulture = CultureInfo.CurrentCulture;
            string report;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
                StringBuilder sb = new StringBuilder();
                totals.AppendReport(sb);
                report = sb.ToString();
            }
            finally
            {
                CultureInfo.CurrentCulture = savedCulture;
            }

            string[] lines = report.Replace("\r", "").Split('\n');
            ok &= Check("Header, phase 1, 16 sweep rows, the overflow row, the total, the tail, the clouds split, the route lines, in order",
                lines.Length >= 24 && lines[0] == "=== Startup Coroutine ==="
                && lines[1] == "Phase 1:          1400.0 ms wall over 83 iterations, 80 quota-stopped; main thread pass 500.0 ms + apply mods 100.0 ms; 529 jobs, 400000 structure mods; 27 collections"
                && lines[2] == "Sweep 1           61.0 ms: recalc 1.0 (2), schedule 10.0 (acquire 5.0; 1 jobs, 7 shared maps), wait 20.0, merge 30.0 (apply 1.0, drain 2.0, route 3.0, cascade 4.0); cross-chunk 3 routed / 1 effective; 1 collections"
                && lines[17] == "Sweep 16          76.0 ms: recalc 16.0 (2), schedule 10.0 (acquire 5.0; 16 jobs, 7 shared maps), wait 20.0, merge 30.0 (apply 1.0, drain 2.0, route 3.0, cascade 4.0); cross-chunk 3 routed / 1 effective; 1 collections"
                && lines[18] == "Sweeps 17+        155.0 ms: recalc 35.0 (4), schedule 20.0 (acquire 10.0; 35 jobs, 14 shared maps), wait 40.0, merge 60.0 (apply 2.0, drain 4.0, route 6.0, cascade 8.0); cross-chunk 6 routed / 2 effective; 2 collections"
                && lines[19] == "Sweeps total      1251.0 ms: recalc 171.0 (36), schedule 180.0 (acquire 90.0; 171 jobs, 126 shared maps), wait 360.0, merge 540.0 (apply 18.0, drain 36.0, route 54.0, cascade 72.0); cross-chunk 54 routed / 18 effective; 18 collections"
                && lines[20] == "Tail:             report 10.0 ms, spawn 20.0 ms, clouds 30.0 ms, border wall 40.0 ms; 14 collections"
                && lines[21] == "Clouds:           pattern 2.0 ms, threshold 3.0 ms, meshes 15.0 ms (128 built), tiles 6.0 ms (242 created), other 4.0 ms; 12 collections (pattern 1, threshold 2, meshes 5, tiles 3)"
                && lines[22] == "Route sweep 1     emitted 20 = drop 1 + persist 2 + defer 7 + direct 10 (OK); drained 7; effective direct 4 / drained 3 / pull-back 2; support 5 scans 6.0 ms (route 4.0, drain 2.0); same-target 25.0 %; resolve 25.0 % of 8 samples (est.); census 12 mods, 5 repeats, 3 redundant, 2 removals"
                && lines[23] == "Route total       emitted 360 = drop 18 + persist 36 + defer 126 + direct 180 (OK); drained 126; effective direct 72 / drained 54 / pull-back 36; support 90 scans 108.0 ms (route 72.0, drain 36.0); same-target 25.0 %; resolve 25.0 % of 144 samples (est.); census 216 mods, 90 repeats, 54 redundant, 36 removals");
            ok &= Check("Every sweep is counted, the overflow row is readable by index",
                totals.SweepCount == StartupPhaseTotals.MaxRecordedSweeps + 2
                && totals.GetSweep(StartupPhaseTotals.MaxRecordedSweeps).Jobs == 35);

            StartupPhaseTotals ordered = new StartupPhaseTotals { Recorded = true };
            ordered.AddSweep(new StartupPhaseTotals.Sweep { MergeOrderHash = 0xABUL, MergeOrderJobs = 3 });
            ordered.AddSweep(new StartupPhaseTotals.Sweep { MergeOrderHash = 0xDEADBEEF12345678UL, MergeOrderJobs = 12 });
            StringBuilder orderSb = new StringBuilder();
            ordered.AppendMergeOrder(orderSb);
            ok &= Check("The merge order prints each sweep hash as 16 hex digits and its merge count",
                orderSb.ToString() == "s1 00000000000000AB (3), s2 DEADBEEF12345678 (12)");

            StartupPhaseTotals broken = new StartupPhaseTotals { Recorded = true };
            PerfRouteTotals mismatched = RouteFixture();
            mismatched.Direct++;
            broken.AddSweep(new StartupPhaseTotals.Sweep { Route = mismatched });
            StringBuilder brokenSb = new StringBuilder();
            broken.AppendReport(brokenSb);
            string brokenReport = brokenSb.ToString();
            ok &= Check("Arms that do not sum to the emitted mods print MISMATCH on the sweep and the total",
                brokenReport.Contains("Route sweep 1     emitted 20 = drop 1 + persist 2 + defer 7 + direct 11 (MISMATCH)")
                && brokenReport.Contains("Route total       emitted 20 = drop 1 + persist 2 + defer 7 + direct 11 (MISMATCH)"));
            return ok;
        }

        private static bool RunB42CrossChunkModCensus()
        {
            Dictionary<CrossChunkModCensus.Key, CrossChunkModCensus.Slot> scratch =
                new Dictionary<CrossChunkModCensus.Key, CrossChunkModCensus.Slot>();
            Vector3Int voxel = new Vector3Int(5, 70, -3);

            PerfRouteTotals sky = Census(scratch, SkyMod(voxel, 10), SkyMod(voxel, 8), SkyMod(voxel, 12), SkyMod(voxel, 12));
            bool ok = Check("Sky 10, 8, 12, 12 at one voxel: 4 mods, 3 repeats, 2 redundant (the 8 and the second 12), no removal",
                sky.CensusMods == 4 && sky.CensusRepeats == 3 && sky.CensusRedundant == 2 && sky.CensusRemovals == 0);

            PerfRouteTotals channels = Census(scratch, SkyMod(voxel, 10), BlockMod(voxel, 10, 0, 0));
            ok &= Check("The same voxel on the sky and block channels is two keys: no repeat, no redundant",
                channels.CensusMods == 2 && channels.CensusRepeats == 0 && channels.CensusRedundant == 0);

            PerfRouteTotals barrier = Census(scratch, SkyMod(voxel, 10), SkyMod(voxel, 0), SkyMod(voxel, 9));
            ok &= Check("A removal is a barrier: sky 10, 0, 9 counts 2 repeats, 1 removal, and the 9 is not redundant",
                barrier.CensusMods == 3 && barrier.CensusRepeats == 2 && barrier.CensusRemovals == 1 && barrier.CensusRedundant == 0);

            PerfRouteTotals raisesG = Census(scratch, BlockMod(voxel, 5, 0, 0), BlockMod(voxel, 0, 5, 0));
            ok &= Check("A block placement that raises any channel is not redundant: (5,0,0) then (0,5,0)",
                raisesG.CensusRepeats == 1 && raisesG.CensusRedundant == 0);

            PerfRouteTotals covered = Census(scratch, BlockMod(voxel, 5, 0, 0), BlockMod(voxel, 0, 5, 0), BlockMod(voxel, 4, 4, 0));
            ok &= Check("A block placement under the per-channel maxima is redundant: (4,4,0) after (5,0,0) and (0,5,0)",
                covered.CensusMods == 3 && covered.CensusRepeats == 2 && covered.CensusRedundant == 1);

            Census(scratch, SkyMod(voxel, 10));
            PerfRouteTotals fresh = Census(scratch, SkyMod(voxel, 10));
            ok &= Check("The scratch map is cleared per call: the same key in a later call is not a repeat",
                fresh.CensusMods == 1 && fresh.CensusRepeats == 0 && fresh.CensusRedundant == 0);

            PerfRouteTotals zeroed = Census(scratch, BlockMod(voxel, 0, 0, 0));
            ok &= Check("A block placement with IsRemoval false and every channel 0 is a placement, not a removal",
                zeroed.CensusMods == 1 && zeroed.CensusRemovals == 0 && !CrossChunkModCensus.IsRemoval(BlockMod(voxel, 0, 0, 0)));
            return ok;
        }

        #endregion

        #region Helpers

        /// <summary>One sweep's route totals built from the <c>ROUTE_*</c> fixture; the time fields are whole milliseconds as ticks.</summary>
        private static PerfRouteTotals RouteFixture() => new PerfRouteTotals
        {
            Emitted = ROUTE_EMITTED,
            Drops = ROUTE_DROPS,
            Persists = ROUTE_PERSISTS,
            Defers = ROUTE_DEFERS,
            Direct = ROUTE_DIRECT,
            Drained = ROUTE_DRAINED,
            EffectiveDirect = ROUTE_EFFECTIVE_DIRECT,
            EffectiveDrained = ROUTE_EFFECTIVE_DRAINED,
            EffectivePullBack = ROUTE_EFFECTIVE_PULL_BACK,
            SupportScans = ROUTE_SUPPORT_SCANS,
            SupportTicks = StartupPhaseTotals.MsToTicks(6.0),
            SupportRouteTicks = StartupPhaseTotals.MsToTicks(4.0),
            SupportDrainTicks = StartupPhaseTotals.MsToTicks(2.0),
            SameTargetMods = ROUTE_SAME_TARGET_MODS,
            Samples = ROUTE_SAMPLES,
            SampleResolveTicks = StartupPhaseTotals.MsToTicks(1.0),
            SampleArmTicks = StartupPhaseTotals.MsToTicks(3.0),
            CensusMods = ROUTE_CENSUS_MODS,
            CensusRepeats = ROUTE_CENSUS_REPEATS,
            CensusRedundant = ROUTE_CENSUS_REDUNDANT,
            CensusRemovals = ROUTE_CENSUS_REMOVALS,
        };

        private static LightModification SkyMod(Vector3Int voxel, byte level) => new LightModification
        {
            GlobalPosition = voxel,
            LightLevel = level,
            Channel = LightChannel.Sky,
        };

        private static LightModification BlockMod(Vector3Int voxel, byte r, byte g, byte b) => new LightModification
        {
            GlobalPosition = voxel,
            BlockR = r,
            BlockG = g,
            BlockB = b,
            Channel = LightChannel.Block,
        };

        /// <summary>Runs the census over a hand-built mod list and returns the counts.</summary>
        private static PerfRouteTotals Census(Dictionary<CrossChunkModCensus.Key, CrossChunkModCensus.Slot> scratch, params LightModification[] mods)
        {
            NativeArray<LightModification> array = new NativeArray<LightModification>(mods, Allocator.Temp);
            try
            {
                PerfRouteTotals totals = default;
                CrossChunkModCensus.Accumulate(array, scratch, ref totals);
                return totals;
            }
            finally
            {
                array.Dispose();
            }
        }

        /// <summary>A timeline anchored on a scene request, with every mark up to the handoff at the start chunk.</summary>
        private static StartupTimeline HandedOffTimeline()
        {
            StartupTimeline timeline = new StartupTimeline();
            timeline.MarkAwake(STARTUP_AWAKE_AT, STARTUP_REQUEST_AT);
            timeline.MarkLoadsDone(STARTUP_LOADS_AT);
            timeline.MarkHandoff(STARTUP_HANDOFF_AT, s_startChunk);
            return timeline;
        }

        /// <summary>A handed-off timeline fed exactly the drain run, so its next frame enters the settle window.</summary>
        private static StartupTimeline DrainedTimeline(out double now)
        {
            StartupTimeline timeline = HandedOffTimeline();
            now = STARTUP_HANDOFF_AT;
            for (int i = 0; i < StartupTimeline.DrainSettleFrames; i++)
                timeline.Tick(now += STARTUP_FRAME_SECONDS, STARTUP_FRAME_MS, true, s_startChunk);
            return timeline;
        }

        /// <summary>Feeds one spike then 1 ms frames until the timeline finishes; returns how many frames it took (0 = never).</summary>
        private static int FeedUntilFinished(StartupTimeline timeline, ref double now, float spikeMs)
        {
            for (int frame = 1; frame <= 2 * STARTUP_WINDOW_FRAMES; frame++)
            {
                float frameMs = frame == 1 ? spikeMs : STARTUP_FRAME_MS;
                if (timeline.Tick(now += STARTUP_FRAME_SECONDS, frameMs, true, s_startChunk))
                    return frame;
            }

            return 0;
        }

        #endregion
    }
}
