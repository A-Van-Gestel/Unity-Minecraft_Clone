using Data;
using Diagnostics;
using Editor.Validation.Framework;

namespace Editor.Validation.PerfMonitoring
{
    /// <summary>
    /// The startup half of the suite: <see cref="StartupTimeline"/> driven by injected clock, frame times and drain
    /// results — the drain run and its stamp, the settled-window rule with its slack, interruption, timeout, the anchor
    /// fallback and <see cref="WorldLaunchState"/>'s one-shot scene request. Every value is exact.
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

        #endregion

        #region Helpers

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
