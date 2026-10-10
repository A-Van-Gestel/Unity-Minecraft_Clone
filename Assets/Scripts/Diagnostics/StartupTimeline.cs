using System;
using System.Globalization;
using System.Text;
using Data;

namespace Diagnostics
{
    /// <summary>The marks a launch's <see cref="StartupTimeline"/> passes, in order.</summary>
    public enum StartupStage : byte
    {
        /// <summary>The World component woke.</summary>
        Awake,

        /// <summary>The startup coroutine's initial loads finished.</summary>
        LoadsDone,

        /// <summary>The startup coroutine handed the world to <c>World.Update</c>.</summary>
        Handoff,

        /// <summary>The pipeline drained around the player (<c>PipelineDrainPredicate</c>).</summary>
        Drained,

        /// <summary>Frame time settled after the drain.</summary>
        Stable,
    }

    /// <summary>How a <see cref="StartupTimeline"/> ended.</summary>
    public enum StartupOutcome : byte
    {
        /// <summary>Still measuring.</summary>
        Pending,

        /// <summary>Every mark was reached.</summary>
        Stable,

        /// <summary>The player left the start chunk before frame time settled; later marks are not comparable.</summary>
        Interrupted,

        /// <summary>No stable frame time within <see cref="StartupTimeline.TimeoutSeconds"/> of the handoff.</summary>
        TimedOut,
    }

    /// <summary>
    /// The once-per-launch time-to-stable stamp (ES-0): milliseconds from the World scene load request to Awake, to the
    /// initial loads, to the handoff (<c>World.IsWorldLoaded</c>), to a drained pipeline, and to settled frame time.
    /// Splits the time to a playable world into the parts before and after the handoff.
    /// </summary>
    /// <remarks>
    /// Pure: the caller passes the clock, the frame time and the drain test, so every input can be injected.
    /// "Drained" holds for <see cref="DrainSettleFrames"/> consecutive frames and is stamped at the first of them, as P-4's
    /// fill measure counts it. "Stable" is the end of the first trailing <see cref="StableWindowSeconds"/> window after the
    /// drain whose worst frame is within <see cref="StableMedianFactor"/>× the window median or
    /// <see cref="StableSlackMs"/> above it, whichever is larger: the slack keeps ordinary scheduler jitter at
    /// uncapped ~1 ms frames from failing a 0.25 ms band, and from 8 ms frames up the factor alone decides. Moving off
    /// the start chunk ends the measurement, because streaming new terrain is not startup.
    /// </remarks>
    public sealed class StartupTimeline
    {
        /// <summary>Consecutive drained frames before the drain counts.</summary>
        public const int DrainSettleFrames = 30;

        /// <summary>Length of the trailing frame window that must be settled.</summary>
        public const double StableWindowSeconds = 2.0;

        /// <summary>Worst frame allowed in a settled window, as a multiple of its median.</summary>
        public const float StableMedianFactor = 1.25f;

        /// <summary>Worst frame allowed above the window median regardless of the factor, in milliseconds.</summary>
        public const float StableSlackMs = 2f;

        /// <summary>Time after the handoff by which the frame time must have settled.</summary>
        public const double TimeoutSeconds = 300.0;

        /// <summary>Frames the settle window can hold — over 2 s at 4 kHz, so a short window never caps it.</summary>
        public const int WindowCapacity = 8192;

        private const double MILLISECONDS_PER_SECOND = 1000.0;
        private const int STAGE_COUNT = (int)StartupStage.Stable + 1;

        private readonly double[] _stageAt = new double[STAGE_COUNT];
        private double _anchorAt = double.NaN;
        private ChunkCoord _handoffChunk;
        private int _settleFrames;
        private double _settleStartAt;

        private float[] _windowMs;
        private float[] _scratch;
        private int _windowStart;
        private int _windowCount;
        private double _windowSumMs;

        /// <summary>Creates a timeline with no marks.</summary>
        public StartupTimeline()
        {
            for (int i = 0; i < STAGE_COUNT; i++)
                _stageAt[i] = double.NaN;
        }

        /// <summary>How the measurement ended, or <see cref="StartupOutcome.Pending"/> while it runs.</summary>
        public StartupOutcome Outcome { get; private set; }

        /// <summary>True until the timeline has an outcome.</summary>
        public bool IsPending => Outcome == StartupOutcome.Pending;

        /// <summary>True when the anchor is the scene load request; false when it is Awake (Play pressed in the World scene).</summary>
        public bool AnchoredOnSceneRequest { get; private set; }

        /// <summary>True while the caller should evaluate the drain test for <see cref="Tick"/>: handed off, not yet drained.</summary>
        public bool NeedsDrainCheck => IsPending && Reached(StartupStage.Handoff) && !Reached(StartupStage.Drained);

        /// <summary>The settled window's median frame time, or NaN before <see cref="StartupStage.Stable"/>.</summary>
        public float StableWindowMedianMs { get; private set; } = float.NaN;

        /// <summary>The settled window's worst frame time, or NaN before <see cref="StartupStage.Stable"/>.</summary>
        public float StableWindowWorstMs { get; private set; } = float.NaN;

        /// <summary>
        /// The outcome in words. A timeout names the stage it stalled in, because a pipeline that never drained and frame
        /// time that never settled have different owners.
        /// </summary>
        public string OutcomeDescription => Outcome switch
        {
            StartupOutcome.Stable => "stable",
            StartupOutcome.Interrupted => "interrupted (the player left the start chunk)",
            StartupOutcome.TimedOut when !Reached(StartupStage.Drained) => "timed out before the drain (the pipeline never drained)",
            StartupOutcome.TimedOut => "timed out (frame time did not settle)",
            _ => "pending",
        };

        /// <summary>The last mark reached, or null before Awake.</summary>
        public StartupStage? LastStage
        {
            get
            {
                for (int i = STAGE_COUNT - 1; i >= 0; i--)
                {
                    if (!double.IsNaN(_stageAt[i])) return (StartupStage)i;
                }

                return null;
            }
        }

        /// <summary>Whether a mark was reached.</summary>
        /// <param name="stage">The mark.</param>
        /// <returns>True when it has a time.</returns>
        public bool Reached(StartupStage stage) => !double.IsNaN(_stageAt[(int)stage]);

        /// <summary>Milliseconds from the anchor to a mark.</summary>
        /// <param name="stage">The mark.</param>
        /// <returns>The elapsed time, or NaN when the mark was not reached.</returns>
        public double MillisecondsTo(StartupStage stage) => (_stageAt[(int)stage] - _anchorAt) * MILLISECONDS_PER_SECOND;

        /// <summary>Stamps Awake and fixes the anchor.</summary>
        /// <param name="now">The clock, in seconds.</param>
        /// <param name="sceneRequestedAt">The scene load request time, or <see cref="WorldLaunchState.NoSceneRequest"/>.</param>
        public void MarkAwake(double now, double sceneRequestedAt)
        {
            AnchoredOnSceneRequest = sceneRequestedAt >= 0.0 && sceneRequestedAt <= now;
            _anchorAt = AnchoredOnSceneRequest ? sceneRequestedAt : now;
            _stageAt[(int)StartupStage.Awake] = now;
        }

        /// <summary>Stamps the end of the initial loads.</summary>
        /// <param name="now">The clock, in seconds.</param>
        public void MarkLoadsDone(double now) => _stageAt[(int)StartupStage.LoadsDone] = now;

        /// <summary>Stamps the handoff and remembers the chunk the player starts in.</summary>
        /// <param name="now">The clock, in seconds.</param>
        /// <param name="playerChunk">The player's chunk at the handoff.</param>
        public void MarkHandoff(double now, ChunkCoord playerChunk)
        {
            _stageAt[(int)StartupStage.Handoff] = now;
            _handoffChunk = playerChunk;
        }

        /// <summary>Advances the measurement by one frame after the handoff.</summary>
        /// <param name="now">The clock, in seconds.</param>
        /// <param name="frameMs">The last frame's duration.</param>
        /// <param name="drained">This frame's drain test; read only while <see cref="NeedsDrainCheck"/>.</param>
        /// <param name="playerChunk">The player's chunk this frame.</param>
        /// <returns>True on the one call that gives the timeline its outcome.</returns>
        public bool Tick(double now, float frameMs, bool drained, ChunkCoord playerChunk)
        {
            if (!IsPending || !Reached(StartupStage.Handoff)) return false;

            if (!playerChunk.Equals(_handoffChunk))
            {
                Outcome = StartupOutcome.Interrupted;
                return true;
            }

            if (now - _stageAt[(int)StartupStage.Handoff] > TimeoutSeconds)
            {
                Outcome = StartupOutcome.TimedOut;
                return true;
            }

            if (!Reached(StartupStage.Drained))
            {
                if (!drained)
                {
                    _settleFrames = 0;
                    return false;
                }

                if (_settleFrames == 0) _settleStartAt = now;
                if (++_settleFrames >= DrainSettleFrames) _stageAt[(int)StartupStage.Drained] = _settleStartAt;
                return false;
            }

            if (!PushFrame(frameMs)) return false;

            _stageAt[(int)StartupStage.Stable] = now;
            Outcome = StartupOutcome.Stable;
            return true;
        }

        /// <summary>One line naming every mark reached and the outcome, for the log and the run reports.</summary>
        /// <returns>The description.</returns>
        public string Describe()
        {
            StringBuilder sb = new StringBuilder(capacity: 256);
            sb.Append(OutcomeDescription);
            sb.Append(AnchoredOnSceneRequest ? " — ms after the scene load request: " : " — ms after Awake (no scene load request): ");

            for (int i = 0; i < STAGE_COUNT; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(((StartupStage)i).ToString()).Append(' ');
                double ms = MillisecondsTo((StartupStage)i);
                sb.Append(double.IsNaN(ms) ? "—" : ms.ToString("F0", CultureInfo.InvariantCulture));
            }

            if (Reached(StartupStage.Stable))
            {
                sb.Append(" (settled window median ").Append(StableWindowMedianMs.ToString("F2", CultureInfo.InvariantCulture))
                    .Append(" ms, worst ").Append(StableWindowWorstMs.ToString("F2", CultureInfo.InvariantCulture)).Append(" ms)");
            }

            return sb.ToString();
        }

        /// <summary>The worst frame a window with this median may hold and still count as settled.</summary>
        /// <param name="medianMs">The window median.</param>
        /// <returns>The larger of the factor bound and the slack bound.</returns>
        public static float StableBoundMs(float medianMs) => Math.Max(medianMs * StableMedianFactor, medianMs + StableSlackMs);

        /// <summary>Adds a frame to the trailing window and tests it once it spans the window length.</summary>
        private bool PushFrame(float frameMs)
        {
            if (_windowMs == null)
            {
                _windowMs = new float[WindowCapacity];
                _scratch = new float[WindowCapacity];
            }

            if (_windowCount == WindowCapacity) DropOldest();
            _windowMs[(_windowStart + _windowCount) % WindowCapacity] = frameMs;
            _windowCount++;
            _windowSumMs += frameMs;

            double windowMs = StableWindowSeconds * MILLISECONDS_PER_SECOND;
            while (_windowCount > 1 && _windowSumMs - _windowMs[_windowStart] >= windowMs)
                DropOldest();

            if (_windowSumMs < windowMs) return false;

            for (int i = 0; i < _windowCount; i++)
                _scratch[i] = _windowMs[(_windowStart + i) % WindowCapacity];

            PerfWindowSummary summary = PerfWindowStats.Summarize(_scratch, _windowCount);
            if (summary.Max > StableBoundMs(summary.P50)) return false;

            StableWindowMedianMs = summary.P50;
            StableWindowWorstMs = summary.Max;
            return true;
        }

        private void DropOldest()
        {
            _windowSumMs -= _windowMs[_windowStart];
            _windowStart = (_windowStart + 1) % WindowCapacity;
            _windowCount--;
        }
    }
}
