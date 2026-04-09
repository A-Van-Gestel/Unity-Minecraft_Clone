using System;
using System.Collections;
using System.Diagnostics;
using Helpers;
using UnityEngine;

/// <summary>
/// Accurately measures CPU frame timings across different Unity lifecycle phases
/// using high-resolution <see cref="Stopwatch"/> ticks.
/// <para>
/// Executes before all other scripts (<c>[DefaultExecutionOrder(int.MinValue)]</c>) to
/// accurately capture the start of each frame phase. A companion
/// <see cref="PerformanceMonitorLateHook"/> script executes after all other scripts to
/// bookend the LateUpdate phase.
/// </para>
/// <para>
/// This replaces the previous <c>ProfilerRecorder</c>-based approach, which returned
/// invalid data in non-Development Release builds.
/// </para>
/// </summary>
/// <remarks>
/// Lifecycle: Scene-scoped. Metrics reset on scene reload / Play Mode re-entry.
/// </remarks>
[DefaultExecutionOrder(int.MinValue)]
public class PerformanceMonitor : MonoBehaviour
{
    // --- Constants ---
    private const int PHASE_WINDOW_SIZE = 30;
    private const int GC_WINDOW_SIZE = 60;

    // --- Singleton ---

    /// <summary>
    /// The singleton instance of the PerformanceMonitor. Null if not yet initialized.
    /// </summary>
    public static PerformanceMonitor Instance { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void DomainReset()
    {
        Instance = null;
    }

    /// <summary>
    /// A chronological snapshot of all tracked performance metrics at a specific point in time.
    /// </summary>
    public struct FrameMetricSnapshot
    {
        /// <summary>The total time the CPU spent executing engine code during the frame.</summary>
        public float CpuTimeMs;

        /// <summary>The real-world wall clock time for the frame, including VSync and GPU idle time.</summary>
        public float WallTimeMs;

        /// <summary>The amount of managed GC memory allocated during the frame in Kilobytes.</summary>
        public float GcAllocKb;
    }

    [Header("History Tracking")]
    [Tooltip("How many seconds of history to keep in memory (Rolling Window).")]
    [SerializeField]
    private float _historyTimeframeSeconds = 10f;

    [Tooltip("How often (in seconds) to capture a sample for the history graphs.")]
    [SerializeField]
    private float _historyPollRate = 0.05f;

    public float HistoryPollRate => _historyPollRate;
    public int HistorySize { get; private set; }
    public int HistoryHeadIndex { get; private set; }

    /// <summary>
    /// The ring buffer containing the historical snapshots of performance metrics.
    /// </summary>
    public FrameMetricSnapshot[] MetricsHistory { get; private set; }

    /// <summary>
    /// Fired every time a new history snapshot is recorded.
    /// </summary>
    public event Action<FrameMetricSnapshot> OnMetricsSampled;

    private float _historyTimer;
    private static readonly double s_tickToMs = 1000.0 / Stopwatch.Frequency;

    // --- Phase Timings (Moving Averages of Stopwatch Ticks) ---

    /// <summary>Average time spent in the FixedUpdate phase (all iterations this frame).</summary>
    public MovingAverage FixedUpdateTime { get; } = new MovingAverage(PHASE_WINDOW_SIZE);

    /// <summary>
    /// Average time spent in the Update phase.
    /// Includes all scripts' Update() calls and coroutines that yield null.
    /// </summary>
    public MovingAverage UpdatePhaseTime { get; } = new MovingAverage(PHASE_WINDOW_SIZE);

    /// <summary>
    /// Average time between the coroutine yield-null resume and the start of LateUpdate.
    /// This is where Unity processes animations and internal coroutine scheduling.
    /// </summary>
    public MovingAverage CoroutinePhaseTime { get; } = new MovingAverage(PHASE_WINDOW_SIZE);

    /// <summary>Average time spent in the LateUpdate phase (all scripts).</summary>
    public MovingAverage LateUpdateTime { get; } = new MovingAverage(PHASE_WINDOW_SIZE);

    /// <summary>
    /// Average time from end-of-LateUpdate through scene rendering and GUI passes
    /// to WaitForEndOfFrame. Includes IMGUI if present.
    /// </summary>
    public MovingAverage RenderTime { get; } = new MovingAverage(PHASE_WINDOW_SIZE);

    /// <summary>Average total active CPU ticks per frame (sum of all measured phases).</summary>
    public MovingAverage CpuFrameTime { get; } = new MovingAverage(PHASE_WINDOW_SIZE);

    /// <summary>Average wall-clock ticks per frame (includes VSync idle, GPU waits).</summary>
    public MovingAverage WallFrameTime { get; } = new MovingAverage(PHASE_WINDOW_SIZE);

    /// <summary>Average managed GC bytes allocated per frame.</summary>
    public MovingAverage GcAllocationPerFrame { get; } = new MovingAverage(GC_WINDOW_SIZE);

    // --- State ---
    private Stopwatch _phaseStopwatch;
    private Stopwatch _frameStopwatch;
    private long _currentFrameCpuTicks;
    private long _lastGcMemory;

    /// <summary>
    /// Baseline GC collection counts captured at startup, used for session-relative display.
    /// Indexed by GC generation (0, 1, 2).
    /// </summary>
    public int[] BaselineGcCounts { get; private set; }

    // --- Derived Metrics ---

    /// <summary>
    /// Frames per second based on actual CPU work time (excludes VSync/GPU idle).
    /// Will be higher than <see cref="WallFPS"/> when the CPU has headroom.
    /// </summary>
    public float CpuFPS => CpuFrameTime.GetAverage() == 0 ? 0 : Stopwatch.Frequency / (float)CpuFrameTime.GetAverage();

    /// <summary>
    /// Frames per second based on wall-clock time (what the user actually sees).
    /// Capped by VSync / target frame rate.
    /// </summary>
    public float WallFPS => WallFrameTime.GetAverage() == 0 ? 0 : Stopwatch.Frequency / (float)WallFrameTime.GetAverage();

    /// <summary>
    /// Idle/Other time in milliseconds: the gap between wall-clock frame time and measured
    /// CPU time. Includes VSync waits, GPU stalls, and any Unity internals not captured
    /// by the phase hooks.
    /// </summary>
    public double IdleTimeMs
    {
        get
        {
            double tickToMs = 1000.0 / Stopwatch.Frequency;
            double wall = WallFrameTime.GetAverage() * tickToMs;
            double cpu = CpuFrameTime.GetAverage() * tickToMs;
            return Math.Max(0, wall - cpu);
        }
    }

    #region Unity Lifecycle

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }

        Instance = this;
        _phaseStopwatch = new Stopwatch();
        _frameStopwatch = Stopwatch.StartNew();
        _lastGcMemory = GC.GetTotalMemory(false);

        // --- Initialize History Buffers ---
        _historyTimeframeSeconds = Mathf.Clamp(_historyTimeframeSeconds, 1f, 60f);
        _historyPollRate = Mathf.Clamp(_historyPollRate, 0.01f, 1f);
        HistorySize = Mathf.CeilToInt(_historyTimeframeSeconds / _historyPollRate);

        MetricsHistory = new FrameMetricSnapshot[HistorySize];

        // Capture baseline GC counts for session-relative display in the Editor.
        // GC.CollectionCount returns process-lifetime totals, so we subtract these
        // baselines to show clean per-session metrics.
        BaselineGcCounts = new int[GC.MaxGeneration + 1];
        for (int g = 0; g <= GC.MaxGeneration; g++)
        {
            BaselineGcCounts[g] = GC.CollectionCount(g);
        }

        // Attach the late hook on the same GameObject to cap the LateUpdate phase.
        gameObject.AddComponent<PerformanceMonitorLateHook>();
    }

    private void Start()
    {
        StartCoroutine(FramePhaseCoroutine());
    }

    private void OnDestroy()
    {
        if (Instance == this)
        {
            Instance = null;
        }
    }

    #endregion

    #region Phase Measurement

    /// <summary>
    /// Reads the elapsed ticks from the phase stopwatch, accumulates them into
    /// <see cref="_currentFrameCpuTicks"/>, restarts the stopwatch, and returns
    /// the sampled ticks.
    /// </summary>
    /// <returns>The number of high-resolution stopwatch ticks accumulated since the last sample.</returns>
    private long SampleAndRestart()
    {
        long ticks = _phaseStopwatch.ElapsedTicks;
        _currentFrameCpuTicks += ticks;
        _phaseStopwatch.Restart();
        return ticks;
    }

    private void FixedUpdate()
    {
        // Use Start() instead of Restart(). If FixedUpdate doesn't run this frame,
        // the stopwatch stays at 0, preventing idle time from bleeding into the
        // FixedUpdate measurement.
        _phaseStopwatch.Start();
    }

    private void Update()
    {
        // Sample the time since FixedUpdate (or since the previous frame's reset).
        // This captures all FixedUpdate iterations this frame.
        FixedUpdateTime.Sample(SampleAndRestart());
    }

    private void LateUpdate()
    {
        // Captures the time from the coroutine yield-null resume up to the START
        // of LateUpdate. This is where Unity processes animations, internal
        // coroutine scheduling, and other yield-null callbacks.
        CoroutinePhaseTime.Sample(SampleAndRestart());
    }

    /// <summary>
    /// Called by <see cref="PerformanceMonitorLateHook"/> at the END of the LateUpdate
    /// phase (execution order <c>int.MaxValue</c>) to capture the full duration of
    /// all LateUpdate callbacks.
    /// </summary>
    internal void SampleLateUpdate()
    {
        LateUpdateTime.Sample(SampleAndRestart());
    }

    private IEnumerator FramePhaseCoroutine()
    {
        WaitForEndOfFrame waitForEndOfFrame = new WaitForEndOfFrame();

        while (true)
        {
            // 1. Wait until right after all Update() callbacks and yield-null coroutines.
            //    This measurement captures the full Update phase including all other
            //    scripts' Update() methods (since we run at int.MinValue, they all
            //    run after us).
            yield return null;
            UpdatePhaseTime.Sample(SampleAndRestart());

            // 2. Wait until end of frame (after scene rendering and GUI passes).
            yield return waitForEndOfFrame;

            long renderTicks = _phaseStopwatch.ElapsedTicks;
            RenderTime.Sample(renderTicks);
            _currentFrameCpuTicks += renderTicks;

            // Stop the stopwatch so inter-frame idle time is NOT counted.
            _phaseStopwatch.Reset();

            // Store true active CPU time (sum of all measured phases).
            CpuFrameTime.Sample(_currentFrameCpuTicks);
            _currentFrameCpuTicks = 0;

            // Store actual wall-clock time (includes VSync/GPU waits).
            WallFrameTime.Sample(_frameStopwatch.ElapsedTicks);
            _frameStopwatch.Restart();

            // Track Managed GC Allocations per frame.
            long currentGcMemory = GC.GetTotalMemory(false);
            long delta = currentGcMemory - _lastGcMemory;

            // Always sample (even zero/negative) so the moving average correctly
            // decays to zero when allocations stop. Negative deltas indicate a
            // GC collection occurred — we clamp to 0 for the allocation metric.
            GcAllocationPerFrame.Sample(Math.Max(0, delta));
            _lastGcMemory = currentGcMemory;

            // --- HISTORY TRACKING ---
            _historyTimer += Time.unscaledDeltaTime;
            if (_historyTimer >= _historyPollRate)
            {
                _historyTimer = 0f;

                FrameMetricSnapshot snapshot = new FrameMetricSnapshot
                {
                    CpuTimeMs = (float)(CpuFrameTime.GetAverage() * s_tickToMs),
                    WallTimeMs = (float)(WallFrameTime.GetAverage() * s_tickToMs),
                    GcAllocKb = GcAllocationPerFrame.GetAverage() / 1024f,
                };

                MetricsHistory[HistoryHeadIndex] = snapshot;
                HistoryHeadIndex = (HistoryHeadIndex + 1) % HistorySize;

                // Fire event with the bundled struct
                OnMetricsSampled?.Invoke(snapshot);
            }
        }
        // ReSharper disable once IteratorNeverReturns
    }

    #endregion
}

/// <summary>
/// Captures the end of the LateUpdate phase by running at
/// <c>[DefaultExecutionOrder(int.MaxValue)]</c>.
/// <para>
/// This is dynamically added by <see cref="PerformanceMonitor.Awake()"/> to the same
/// GameObject. It calls back into the monitor to sample the LateUpdate duration,
/// cleanly separating it from the subsequent Render phase.
/// </para>
/// </summary>
[DefaultExecutionOrder(int.MaxValue)]
public class PerformanceMonitorLateHook : MonoBehaviour
{
    private PerformanceMonitor _monitor;

    private void Awake()
    {
        _monitor = GetComponent<PerformanceMonitor>();
    }

    private void LateUpdate()
    {
        _monitor.SampleLateUpdate();
    }
}
