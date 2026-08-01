using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Data;
using Data.Enums;
using Helpers;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

namespace Benchmarks
{
    /// <summary>
    /// Controls the player during a benchmark profiling run.
    /// Drives the player through a two-pass waypoint system to stress-test the
    /// chunk generation, lighting, meshing, and disk loading pipelines.
    /// <para><b>Pass 1 — Generation:</b> A zigzag sweep whose geometry is <b>derived</b> by
    /// <see cref="BenchmarkRouteGeometry"/> from the configured speeds and
    /// <see cref="Settings.benchmarkPhaseSeconds"/> — rows sit <c>2 × LoadDistance</c> apart so each sweeps
    /// virgin terrain. Every speed phase runs exactly one phase duration, at every view distance, which is
    /// what makes two captures comparable (FP-9b).</para>
    /// <para><b>Ensure Generated:</b> the loading tour is flown once slowly so its terrain is on disk before
    /// the transition saves it. Not a measurement — it carries no regime verdict.</para>
    /// <para><b>Transition:</b> All active jobs are drained, world data is saved to disk,
    /// and chunks are force-unloaded from memory via <see cref="World.ForceUnloadAllChunks"/>.
    /// This ensures the loading pass exercises the deserialization pipeline.</para>
    /// <para><b>Pass 2 — Loading:</b> Diagonal cross-cuts through previously generated
    /// territory at escalating speeds, forcing chunks to be reloaded from disk. Each speed
    /// phase runs for a fixed duration; loading waypoints loop if exhausted before phases end.
    /// The benchmark ends when all loading phases complete.</para>
    /// </summary>
    public class BenchmarkController : MonoBehaviour
    {
        // ── Constants ────────────────────────────────────────────────────

        /// <summary>
        /// Fraction of <see cref="VoxelData.ChunkHeight"/> used as flight altitude.
        /// Keeps the player above terrain while auto-adapting to chunk height changes.
        /// </summary>
        private const float FLIGHT_HEIGHT_RATIO = 0.8f;

        /// <summary>
        /// Flight altitude in world units, derived from chunk height.
        /// </summary>
        private const float FLIGHT_HEIGHT = VoxelData.ChunkHeight * FLIGHT_HEIGHT_RATIO;

        /// <summary>
        /// Fallback duration of each speed phase in seconds, used when the configured
        /// <see cref="Settings.benchmarkPhaseSeconds"/> is out of range.
        /// </summary>
        private const float DEFAULT_TIME_PER_PHASE = 30f;

        /// <summary>
        /// Number of frames to wait after the chunk pipeline drains before starting
        /// a measured phase. Allows <see cref="PerformanceMonitor"/>'s moving averages
        /// (30–60 frame windows) to flush any spike data from the preceding teleport.
        /// </summary>
        private const int SETTLE_FRAMES = 60;

        // ── Phase Group Names ────────────────────────────────────────────

        private const string GROUP_GENERATION = "Generation Pass";
        private const string GROUP_ENSURE = "Ensure Generated";
        private const string GROUP_TRANSITION = "Transition";
        private const string GROUP_LOADING = "Loading Pass";

        // ── Fallback Phase Configuration ─────────────────────────────────

        private static readonly float[] s_defaultGenerationSpeeds = { 10f, 20f, 50f, 100f, 200f };
        private static readonly float[] s_defaultLoadingSpeeds = { 50f, 100f, 200f };

        // ── Runtime State ────────────────────────────────────────────────

        private readonly List<Vector3> _generationWaypoints = new List<Vector3>();
        private readonly List<Vector3> _loadingWaypoints = new List<Vector3>();
        private float[] _generationSpeeds;
        private float[] _loadingSpeeds;
        private int _activeWaypointIndex;
        private Transform _playerCamera;
        private BenchmarkMetricsCollector _metricsCollector;
        private float _phaseSeconds = DEFAULT_TIME_PER_PHASE;
        private BenchmarkRouteGeometry _routeGeometry;
        private Stopwatch _totalStopwatch;
        private Material _blurMaterial;

        /// <summary>
        /// Pipeline tuning captured at run start (FP-6) — the geometry input to the FP trace-capacity
        /// estimate, and the values the report must state so its stop-reason tallies are interpretable.
        /// Snapshotted rather than re-read at report time: settings are editable mid-session.
        /// </summary>
        private PipelineSettingsSnapshot _pipelineSettingsForCapture;

        // ── Frame Rate Overrides ─────────────────────────────────────────

        private int _savedVSyncCount;
        private int _savedTargetFrameRate;
        private bool _frameRateOverridden;

        // ── UI ───────────────────────────────────────────────────────────

        private BenchmarkHUD _hud;
        private BenchmarkResultsScreen _resultsScreen;

        // ── Public HUD State (read by BenchmarkHUD on its own timer) ─────

        /// <summary>Current pass group name for HUD display.</summary>
        public string CurrentGroupName { get; private set; }

        /// <summary>Current speed phase name for HUD display.</summary>
        public string CurrentPhaseName { get; private set; }

        /// <summary>Progress within current pass (0-1). Negative means indeterminate (settling/transition).</summary>
        public float Progress { get; private set; }

        /// <summary>Overall benchmark progress (0-1) across all phases. Negative means indeterminate.</summary>
        public float OverallProgress { get; private set; }

        /// <summary>Total wall-clock seconds since benchmark measurement started.</summary>
        public float ElapsedSeconds => _totalStopwatch != null ? (float)_totalStopwatch.Elapsed.TotalSeconds : 0f;

        /// <summary>Whether the benchmark is currently running measured phases.</summary>
        public bool IsRunning { get; private set; }

        /// <summary>Total waypoints in the currently active pass.</summary>
        public int TotalWaypointsInActivePass { get; private set; }

        /// <summary>Total number of speed phases across all passes (generation + transition + loading).</summary>
        private int _totalPhaseCount;

        /// <summary>Index of the current phase across all passes (0-based).</summary>
        private int _currentOverallPhaseIndex;

        // ── Lifecycle ────────────────────────────────────────────────────

        /// <summary>
        /// Coroutine entry point. Waits for the world to fully load, parses
        /// speed configuration, then runs the complete benchmark:
        /// generation pass → transition → loading pass → results screen.
        /// </summary>
        public IEnumerator Start()
        {
            if (WorldLaunchState.CurrentMode != RuntimeMode.Benchmark)
            {
                Destroy(this);
                yield break;
            }

            if (Camera.main != null)
                _playerCamera = Camera.main.transform;

            // Wait for world to be fully loaded
            while (World.Instance == null || !World.Instance.IsWorldLoaded)
            {
                yield return null;
            }

            Settings settings = SettingsManager.LoadSettings();
            _generationSpeeds = ParseSpeedString(settings.benchmarkGenerationSpeeds, s_defaultGenerationSpeeds, "Generation");
            _loadingSpeeds = ParseSpeedString(settings.benchmarkLoadingSpeeds, s_defaultLoadingSpeeds, "Loading");
            _phaseSeconds = settings.benchmarkPhaseSeconds > 0f
                ? settings.benchmarkPhaseSeconds
                : DEFAULT_TIME_PER_PHASE;

            // Force VSync off and uncap framerate for accurate throughput measurement
            _savedVSyncCount = QualitySettings.vSyncCount;
            _savedTargetFrameRate = Application.targetFrameRate;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            _frameRateOverridden = true;

            BuildWaypoints(settings);

            if (_generationWaypoints.Count < 2)
            {
                Debug.LogError("[Benchmark] Insufficient waypoints generated. Ending benchmark.");
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
                ReturnToMainMenu();
                yield break;
            }

            // Create UI overlays
            Shader blurShader = Shader.Find("Custom/MaskedUIBlur");
            if (blurShader != null)
                _blurMaterial = new Material(blurShader);

            _hud = BenchmarkUIBuilder.CreateHUD(this, _blurMaterial);
            _resultsScreen = BenchmarkUIBuilder.CreateResultsScreen("Benchmark Complete", _blurMaterial);

            Debug.Log("[Benchmark] Waiting for initial chunk pipeline to settle...");
            Progress = -1f;
            CurrentGroupName = "Initializing";
            yield return WaitForChunkPipelineToSettle();

            // Generation speeds + ensure-generated + transition + loading speeds (FP-9b added the first of
            // those two singles; progress would otherwise never reach 100 %).
            _totalPhaseCount = _generationSpeeds.Length + 2 + _loadingSpeeds.Length;
            _currentOverallPhaseIndex = 0;

            _metricsCollector = new BenchmarkMetricsCollector(_totalPhaseCount);
            _metricsCollector.StartRecording();

            // FP: pipeline-internal telemetry rides the same phase boundaries as the frame-health collector,
            // so the two report the same phases. Enabled only for the duration of this run and cleared in
            // OnDestroy — the WorldFrameProfiler/FluidStressController pattern.
            _pipelineSettingsForCapture = new PipelineSettingsSnapshot(settings);

            // Pairs with the freshly-constructed collector above: both recorders must start a run empty, or
            // a second run in one process reports the first run's phases as its own (FP-5).
            PipelineTelemetry.BeginRun();
            PipelineTelemetry.Enabled = true;

            // FP-11a: start crediting tour coverage HERE rather than at the ensure pass, so terrain the
            // generation phases already produced counts — it is on disk by the time the loading pass asks
            // for it, which is the only property the ensure sweep exists to guarantee.
            BenchmarkTourCoverage.Arm(_routeGeometry, settings.LoadDistance);

            _totalStopwatch = Stopwatch.StartNew();
            IsRunning = true;

            Debug.Log($"[Benchmark] Started profiling run. " +
                      $"{_generationWaypoints.Count} generation waypoints, " +
                      $"{_loadingWaypoints.Count} loading waypoints. " +
                      $"Generation speeds: [{string.Join(", ", _generationSpeeds)}] m/s, " +
                      $"Loading speeds: [{string.Join(", ", _loadingSpeeds)}] m/s.");

            // === Pass 1: Generation ===
            yield return RunGenerationPass();

            // === Ensure Generated: guarantee the loading tour's terrain exists on disk ===
            yield return RunEnsureGeneratedPass();

            // === Transition: Drain Jobs → Save → Force Unload ===
            yield return TransitionToLoadingPass();

            // FP-11a: the tour's terrain is now all on disk and nothing is resident, so this is the instant
            // the coverage question is actually asked — will the loading pass load, or generate? Freezing
            // any later would let that pass credit itself.
            BenchmarkTourCoverage.Freeze();
            ReportTourCoverage();

            // === Pass 2: Loading ===
            yield return RunLoadingPass();

            _totalStopwatch.Stop();
            _metricsCollector.StopRecording();

            // Close the final telemetry phase BEFORE the report reads CompletedPhases — an open phase is
            // never in that list, so the last (fastest, most interesting) speed tier would be missing.
            PipelineTelemetry.EndPhase();
            IsRunning = false;

            BenchmarkReportResult reportResult = BenchmarkReportGenerator.GenerateAndWriteReport(
                _metricsCollector,
                _generationSpeeds,
                _loadingSpeeds,
                _phaseSeconds,
                _routeGeometry,
                _generationWaypoints.Count,
                _loadingWaypoints.Count,
                _totalStopwatch.Elapsed,
                _savedVSyncCount,
                _savedTargetFrameRate,
                _pipelineSettingsForCapture);

            ShowResults(reportResult);
        }

        private void OnDestroy()
        {
            _metricsCollector?.StopRecording();

            // Close any phase still open (an aborted run) and switch the layer off. The domain reset covers
            // a play-mode restart, but not returning to the main menu within one session.
            PipelineTelemetry.EndPhase();
            PipelineTelemetry.Enabled = false;

            // Same reasoning for the coverage tracker: a run aborted before the freeze would otherwise stay
            // armed for the rest of the session, charging every ordinary world session a lookup per populated
            // chunk and accruing into a footprint no report will read.
            BenchmarkTourCoverage.Reset();

            if (_frameRateOverridden)
            {
                QualitySettings.vSyncCount = _savedVSyncCount;
                Application.targetFrameRate = _savedTargetFrameRate;
                _frameRateOverridden = false;
            }

            if (WorldLaunchState.CurrentMode == RuntimeMode.Benchmark)
                WorldLaunchState.CurrentMode = RuntimeMode.Default;

            if (_blurMaterial != null)
                Destroy(_blurMaterial);
        }

        /// <summary>
        /// Opens a phase on <b>both</b> recorders at once. Routed through one helper so the frame-health
        /// collector and the FP pipeline telemetry can never disagree about which phase a sample belongs
        /// to — the report prints them side by side, and a one-sided <c>BeginPhase</c> would silently
        /// attribute pipeline data to the wrong speed tier.
        /// </summary>
        /// <param name="phaseName">Display name (e.g. "200 m/s").</param>
        /// <param name="groupName">Logical group (e.g. "Generation Pass").</param>
        /// <param name="speedMetersPerSecond">The phase's flight speed, sizing the trace table (§8 Q1).</param>
        /// <param name="regimeBearing">
        /// <c>false</c> for the transition, which drains and unloads by design rather than measuring a
        /// pipeline state (FP-9a). Defaults true so the generation and loading phases are unaffected.
        /// </param>
        private void BeginPhaseBoth(string phaseName, string groupName, float speedMetersPerSecond,
            bool regimeBearing = true, float phaseSecondsOverride = 0f)
        {
            float seconds = phaseSecondsOverride > 0f ? phaseSecondsOverride : _phaseSeconds;
            _metricsCollector.BeginPhase(phaseName, groupName);
            PipelineTelemetry.BeginPhase(phaseName, groupName,
                PipelineTelemetry.EstimateTraceCapacity(_pipelineSettingsForCapture.LoadDistance,
                    speedMetersPerSecond, seconds),
                regimeBearing);
        }

        /// <summary>Closes the current phase on both recorders (both are no-ops when none is open).</summary>
        private void EndPhaseBoth()
        {
            _metricsCollector.EndPhase();
            PipelineTelemetry.EndPhase();
        }

        // ── Pass Execution ───────────────────────────────────────────────

        /// <summary>
        /// Runs the generation pass: every configured speed phase for exactly
        /// <see cref="_phaseSeconds"/> seconds, at every view distance (FP-9b).
        /// <para>
        /// The pass ends on <b>time</b>, not on waypoint exhaustion — the route is sized with headroom over
        /// the distance the phases travel, so it deliberately stops partway along. Covering the loading
        /// tour's terrain is <see cref="RunEnsureGeneratedPass"/>'s job, not this one's.
        /// </para>
        /// </summary>
        private IEnumerator RunGenerationPass()
        {
            transform.position = WorldOrigin.VoxelToUnity(_generationWaypoints[0]);
            _activeWaypointIndex = 1;
            FaceWaypoint(WorldOrigin.VoxelToUnity(_generationWaypoints[1]));

            CurrentGroupName = GROUP_GENERATION;
            TotalWaypointsInActivePass = _generationWaypoints.Count;
            Progress = -1f;

            yield return WaitForChunkPipelineToSettle();

            int speedIndex = 0;
            float phaseTimer = 0f;

            CurrentPhaseName = $"{_generationSpeeds[0]} m/s";
            BeginPhaseBoth(CurrentPhaseName, GROUP_GENERATION, _generationSpeeds[0]);
            Debug.Log($"[Benchmark] Generation Pass — Phase 0: {_generationSpeeds[0]}m/s");

            // TIME-bounded, not waypoint-bounded (FP-9b). Ending on waypoint exhaustion made every phase's
            // duration a function of route length, so at vd >= 10 the highest generation speed never ran at
            // all and the rest were cut short — durations of 19.7 / 3.2 / 19.8 / 2.2 / 0.7 s across the FP-8
            // sweep. Every phase now runs exactly _phaseSeconds at every view distance, which is what makes
            // two captures comparable. Coverage is no longer this loop's job: the route is sized so the
            // timed distance covers the loading tour, and the ensure-generated pass closes the remainder.
            while (speedIndex < _generationSpeeds.Length)
            {
                phaseTimer += Time.deltaTime;
                if (phaseTimer >= _phaseSeconds)
                {
                    phaseTimer = 0f;
                    speedIndex++;
                    if (speedIndex >= _generationSpeeds.Length) break;

                    _currentOverallPhaseIndex++;
                    CurrentPhaseName = $"{_generationSpeeds[speedIndex]} m/s";
                    BeginPhaseBoth(CurrentPhaseName, GROUP_GENERATION, _generationSpeeds[speedIndex]);
                    Debug.Log($"[Benchmark] Generation Pass — Phase {speedIndex}: " +
                              $"{_generationSpeeds[speedIndex]}m/s");
                }

                Progress = Mathf.Clamp01(phaseTimer / _phaseSeconds);
                OverallProgress = Mathf.Clamp01((_currentOverallPhaseIndex + Progress) / _totalPhaseCount);

                // Loop as a safety net: the route is sized with headroom over the timed distance, so this
                // should never wrap — but a wrap is survivable, whereas standing still at the last waypoint
                // would silently turn the remaining phases into a stationary hover.
                StepTowardWaypoint(_generationWaypoints, _generationSpeeds[speedIndex], loop: true);
                yield return null;
            }

            Progress = 1f;
            _currentOverallPhaseIndex = _generationSpeeds.Length;
            EndPhaseBoth();
            Debug.Log("[Benchmark] === Generation Pass Complete ===");
        }

        /// <summary>
        /// Flies the loading tour once at <see cref="BenchmarkRouteGeometry.EnsureGeneratedSpeed"/> so every
        /// chunk the loading pass will visit exists on disk before the transition saves and unloads.
        /// </summary>
        /// <returns>The coroutine enumerator.</returns>
        /// <remarks>
        /// <b>Not a measurement</b> — begun with <c>regimeBearing: false</c> (FP-9a) so a deliberately slow
        /// sweep cannot contribute a regime verdict. Its purpose is correctness, not data: without it any
        /// chunk the fast generation phases failed to populate would be <i>generated</i> by the loading pass,
        /// which would then be measuring generation while labeled loading.
        /// <para>
        /// It flies the tour <b>legs</b> rather than the whole swept region deliberately: the loading pass
        /// only ever visits those legs, so this is the smallest sufficient sweep — and its cost stays
        /// constant (~211 s) no matter how many speed phases are configured, whereas a full-region sweep
        /// would grow with them (~2 684 s once 300/500 m/s are added).
        /// </para>
        /// </remarks>
        private IEnumerator RunEnsureGeneratedPass()
        {
            if (_loadingWaypoints.Count < 2) yield break;

            CurrentGroupName = GROUP_ENSURE;
            CurrentPhaseName = "Ensure Generated";
            TotalWaypointsInActivePass = _loadingWaypoints.Count;
            Progress = -1f;

            transform.position = WorldOrigin.VoxelToUnity(_loadingWaypoints[0]);
            _activeWaypointIndex = 1;
            FaceWaypoint(WorldOrigin.VoxelToUnity(_loadingWaypoints[1]));

            yield return WaitForChunkPipelineToSettle();

            // Sized from THIS pass's duration, not _phaseSeconds: the sweep runs ~7x a speed phase, and a
            // capacity hint short by that factor makes the trace table rehash mid-run — the incremental
            // growth design §6 pre-sizes to avoid, landing inside the benchmark process.
            BeginPhaseBoth(CurrentPhaseName, GROUP_ENSURE, BenchmarkRouteGeometry.EnsureGeneratedSpeed,
                regimeBearing: false, phaseSecondsOverride: _routeGeometry.EnsureGeneratedSeconds);
            Debug.Log("[Benchmark] === Ensure Generated: covering the loading tour at " +
                      $"{BenchmarkRouteGeometry.EnsureGeneratedSpeed} m/s ===");

            // Leg-bounded, unlike the timed passes: this one exists to COVER the tour, so it walks the whole
            // circuit exactly once. Looping beyond that would repeat work; stopping early would defeat the
            // purpose — and stopping at the LAST WAYPOINT is stopping early, because the loading pass loops
            // its waypoints and therefore also flies the return leg back to the first. That leg went
            // ungenerated until FP-11a, and at low view distance the load radius does not reach across it.
            int legsToFly = _loadingWaypoints.Count;
            int legsFlown = 0;
            int previousWaypointIndex = _activeWaypointIndex;

            while (legsFlown < legsToFly)
            {
                Progress = (float)legsFlown / legsToFly;
                OverallProgress = Mathf.Clamp01((_currentOverallPhaseIndex + Progress) / _totalPhaseCount);
                StepTowardWaypoint(_loadingWaypoints, BenchmarkRouteGeometry.EnsureGeneratedSpeed, loop: true);

                // An arrival ALWAYS increments the index by exactly one; the wrap back to 0 happens on a
                // later call and completes no leg, so testing for the increment cannot double-count it.
                if (_activeWaypointIndex == previousWaypointIndex + 1) legsFlown++;
                previousWaypointIndex = _activeWaypointIndex;

                yield return null;
            }

            EndPhaseBoth();

            // Snapshot only — accrual continues through the transition, which drains and saves the backlog
            // the gate deferred out of this sweep. Freezing here would count that terrain as missing even
            // though the loading pass genuinely loads it.
            BenchmarkTourCoverage.SnapshotEnsurePass();

            _currentOverallPhaseIndex++;
            Progress = 1f;
            Debug.Log("[Benchmark] === Ensure Generated Complete ===");
        }

        /// <summary>
        /// Logs how much of the loading tour exists on disk as the loading pass begins (FP-11a), alongside
        /// what the ensure sweep alone achieved.
        /// </summary>
        /// <remarks>
        /// Only the final figure gates admissibility, and it is loud below
        /// <see cref="BenchmarkTourCoverage.MinimumCoverage"/> on the same grounds as the shrunken-tour
        /// banner: the loading pass is then partly generating terrain while labeled loading, and its numbers
        /// look entirely plausible anyway. The ensure figure is informational — a sweep the gate throttled is
        /// not a problem if the transition drain finished the job. An unmeasurable result is reported as such
        /// and never as 100 %.
        /// </remarks>
        private static void ReportTourCoverage()
        {
            if (!BenchmarkTourCoverage.HasMeasurement)
            {
                Debug.LogError("[Benchmark] Loading-tour coverage NOT MEASURED — the footprint came out " +
                               "empty. The loading pass's numbers cannot be attributed to loading.");
                return;
            }

            int required = BenchmarkTourCoverage.RequiredChunks;

            string message = $"[Benchmark] Loading-tour coverage: {BenchmarkTourCoverage.CoveredChunks.ToString()} / " +
                             $"{required.ToString()} chunks on disk when the loading pass starts " +
                             $"({BenchmarkTourCoverage.CoverageFraction * 100f:F1} %); the ensure sweep alone " +
                             $"reached {BenchmarkTourCoverage.EnsurePassCoveredChunks.ToString()} " +
                             $"({BenchmarkTourCoverage.EnsurePassCoverageFraction * 100f:F1} %).";

            if (BenchmarkTourCoverage.IsSufficient) Debug.Log(message);
            else
                Debug.LogError(message + " Below " +
                               (BenchmarkTourCoverage.MinimumCoverage * 100f).ToString("F0") +
                               " % — the panic gate throttled the sweep and the transition did not finish the " +
                               "job, so the loading pass will GENERATE part of its terrain rather than load " +
                               "it. Treat its numbers as inadmissible at this view distance.");
        }

        /// <summary>
        /// Transitions from the generation pass to the loading pass by delegating
        /// to <see cref="World.ForceUnloadAllChunks"/>, which drains all active jobs,
        /// saves world data, and removes every chunk from memory in a single pass.
        /// </summary>
        private IEnumerator TransitionToLoadingPass()
        {
            CurrentGroupName = GROUP_TRANSITION;
            CurrentPhaseName = "Drain + Save + Unload";
            Progress = -1f;
            OverallProgress = Mathf.Clamp01((float)_currentOverallPhaseIndex / _totalPhaseCount);
            TotalWaypointsInActivePass = 0;

            BeginPhaseBoth(CurrentPhaseName, GROUP_TRANSITION, 0f, regimeBearing: false);
            Debug.Log("[Benchmark] === Transition: Force-unloading all chunks... ===");
            yield return World.Instance.ForceUnloadAllChunks();
            EndPhaseBoth();
            Debug.Log("[Benchmark] === Transition Complete ===");
        }

        /// <summary>
        /// Runs the loading pass: loops through loading waypoints for a fixed
        /// duration per speed phase. Chunks are loaded from disk since memory
        /// was cleared during the transition.
        /// </summary>
        private IEnumerator RunLoadingPass()
        {
            if (_loadingWaypoints.Count < 2)
            {
                Debug.LogWarning("[Benchmark] Insufficient loading waypoints. Skipping loading pass.");
                yield break;
            }

            transform.position = WorldOrigin.VoxelToUnity(_loadingWaypoints[0]);
            _activeWaypointIndex = 1;
            FaceWaypoint(WorldOrigin.VoxelToUnity(_loadingWaypoints[1]));

            CurrentGroupName = GROUP_LOADING;
            TotalWaypointsInActivePass = _loadingWaypoints.Count;
            Progress = -1f;

            yield return WaitForChunkPipelineToSettle();

            // +2, not +1: the ensure-generated sweep and the transition both precede the loading phases.
            int loadingPhaseBase = _generationSpeeds.Length + 2;
            for (int i = 0; i < _loadingSpeeds.Length; i++)
            {
                float phaseTimer = 0f;
                _currentOverallPhaseIndex = loadingPhaseBase + i;

                CurrentPhaseName = $"{_loadingSpeeds[i]} m/s";
                BeginPhaseBoth(CurrentPhaseName, GROUP_LOADING, _loadingSpeeds[i]);
                Debug.Log($"[Benchmark] Loading Pass — Phase {i}: {_loadingSpeeds[i]}m/s");

                while (phaseTimer < _phaseSeconds)
                {
                    phaseTimer += Time.deltaTime;
                    Progress = phaseTimer / _phaseSeconds;
                    OverallProgress = (_currentOverallPhaseIndex + Progress) / _totalPhaseCount;
                    StepTowardWaypoint(_loadingWaypoints, _loadingSpeeds[i], loop: true);
                    yield return null;
                }

                EndPhaseBoth();
            }

            Progress = 1f;
            OverallProgress = 1f;
            Debug.Log("[Benchmark] === Loading Pass Complete ===");
        }

        // ── Pipeline Settling ────────────────────────────────────────────

        /// <summary>
        /// Waits for all active generation, lighting, and meshing jobs to complete,
        /// then waits additional frames for <see cref="PerformanceMonitor"/>'s moving
        /// averages to flush spike data from the preceding teleport.
        /// </summary>
        private static IEnumerator WaitForChunkPipelineToSettle()
        {
            while (World.Instance.JobManager.HasActiveJobs)
            {
                yield return null;
            }

            for (int i = 0; i < SETTLE_FRAMES; i++)
                yield return null;
        }

        // ── Movement ─────────────────────────────────────────────────────

        /// <summary>
        /// Advances the player one frame toward the current waypoint in the given list.
        /// </summary>
        /// <param name="waypoints">The active waypoint list.</param>
        /// <param name="speed">Movement speed in meters/second.</param>
        /// <param name="loop">If true, loops back to the first waypoint when exhausted.
        /// If false, stops advancing once all waypoints have been visited.</param>
        private void StepTowardWaypoint(List<Vector3> waypoints, float speed, bool loop)
        {
            if (waypoints.Count == 0) return;

            if (_activeWaypointIndex >= waypoints.Count)
            {
                if (!loop) return;
                _activeWaypointIndex = 0;
                Debug.Log("[Benchmark] Looping loading waypoints.");
            }

            // Waypoints are authored in voxel space; from here down everything is Unity space (the transform).
            Vector3 target = WorldOrigin.VoxelToUnity(waypoints[_activeWaypointIndex]);
            float step = speed * Time.deltaTime;

            Vector3 currentPos = transform.position;
            Vector3 toTarget = target - currentPos;
            float distance = toTarget.magnitude;

            if (distance <= step)
            {
                transform.position = target;
                _activeWaypointIndex++;

                int nextIndex = _activeWaypointIndex < waypoints.Count
                    ? _activeWaypointIndex
                    : (loop ? 0 : _activeWaypointIndex - 1);
                FaceWaypoint(WorldOrigin.VoxelToUnity(waypoints[nextIndex]));
            }
            else
            {
                Vector3 direction = toTarget / distance;
                transform.position = currentPos + direction * step;
                FaceWaypoint(target);
            }
        }

        /// <summary>
        /// Rotates the player transform to face the given world position.
        /// Also zeroes the camera's local rotation to prevent inherited pitch/roll.
        /// </summary>
        /// <param name="target">The world-space position to look at.</param>
        private void FaceWaypoint(Vector3 target)
        {
            Vector3 direction = target - transform.position;
            if (direction.sqrMagnitude < 0.001f) return;

            transform.rotation = Quaternion.LookRotation(direction);
            if (_playerCamera != null)
            {
                _playerCamera.localEulerAngles = Vector3.zero;
            }
        }

        // ── Configuration Parsing ────────────────────────────────────────

        /// <summary>
        /// Parses a semicolon-separated string of speeds into a float array.
        /// Falls back to the provided defaults if the string is empty or malformed.
        /// </summary>
        /// <param name="input">Semicolon-separated speed values (e.g., "10; 20; 50").</param>
        /// <param name="fallback">Default speeds used when parsing fails.</param>
        /// <param name="label">Label for log messages (e.g., "Generation").</param>
        /// <returns>Parsed speed array, or the fallback on failure.</returns>
        private static float[] ParseSpeedString(string input, float[] fallback, string label)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                Debug.LogWarning($"[Benchmark] {label} speeds string is empty. Using defaults.");
                return fallback;
            }

            string[] parts = input.Split(';');
            List<float> speeds = new List<float>(parts.Length);

            for (int i = 0; i < parts.Length; i++)
            {
                string trimmed = parts[i].Trim();
                if (float.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out float speed) && speed > 0f)
                {
                    speeds.Add(speed);
                }
                else
                {
                    Debug.LogWarning($"[Benchmark] {label} speeds: ignoring invalid entry \"{trimmed}\" at index {i}.");
                }
            }

            if (speeds.Count == 0)
            {
                Debug.LogWarning($"[Benchmark] {label} speeds: no valid entries parsed. Using defaults.");
                return fallback;
            }

            return speeds.ToArray();
        }

        // ── Waypoint Building ────────────────────────────────────────────

        /// <summary>
        /// Builds the waypoint sequences for all three passes from <see cref="BenchmarkRouteGeometry"/>.
        /// <para>
        /// The region is <b>derived</b> from the distance the configured speed phases travel, not
        /// configured — inverting the pre-FP-9b relationship in which waypoint count fell out of a region
        /// the user guessed at, and silently collapsed to four waypoints at high view distances.
        /// </para>
        /// </summary>
        /// <param name="settings">The active settings instance.</param>
        private void BuildWaypoints(Settings settings)
        {
            _generationWaypoints.Clear();
            _loadingWaypoints.Clear();

            _routeGeometry = new BenchmarkRouteGeometry(settings.LoadDistance, _generationSpeeds,
                _phaseSeconds, settings.benchmarkGenerationWaypoints);

            BenchmarkRouteGeometry geometry = _routeGeometry;

            // A shrunken tour means the timed phases cannot cover the loading route, so the loading pass
            // would generate terrain instead of loading it — the confound FP-9b removes. Loud, because the
            // capture is not comparable to any other and the numbers would look plausible anyway.
            if (geometry.TourWasShrunk)
            {
                Debug.LogError("[Benchmark] Loading tour shrunk to " +
                               $"{geometry.TourChunks} chunks (wanted {BenchmarkRouteGeometry.LoadingTourChunks}) — " +
                               "the configured speeds travel too little distance for the generation phases to " +
                               "cover it. The loading pass will GENERATE terrain, not load it. Raise the " +
                               "generation speeds or the phase duration before trusting this capture.");
            }

            BuildGenerationWaypoints(geometry);
            BuildLoadingWaypoints(geometry);

            Debug.Log($"[Benchmark] Built {_generationWaypoints.Count} generation + " +
                      $"{_loadingWaypoints.Count} loading waypoints. " +
                      $"Region={geometry.RegionChunks} chunks (derived), rows={geometry.Rows}, " +
                      $"route={geometry.RouteLengthMeters:F0} m, timed={geometry.TimedTravelMeters:F0} m, " +
                      $"tour={geometry.TourChunks} chunks, LoadDistance={settings.LoadDistance}, " +
                      $"RowStride={geometry.RowStrideChunks}");
        }

        /// <summary>
        /// Generates zigzag sweep waypoints across the benchmark region — two per row, alternating
        /// direction, with rows <c>2 × LoadDistance</c> apart so each sweeps virgin terrain.
        /// </summary>
        /// <param name="geometry">The derived route geometry.</param>
        private void BuildGenerationWaypoints(BenchmarkRouteGeometry geometry)
        {
            float minEdge = geometry.MinEdge;
            float maxEdge = geometry.MaxEdge;
            float rowStride = geometry.RowStrideChunks * VoxelData.ChunkWidth;
            bool leftToRight = true;

            // Row count comes from the geometry rather than a float loop bound: accumulating rowStride to a
            // computed maxEdge made the count depend on float rounding at the last row.
            for (int row = 0; row < geometry.Rows; row++)
            {
                float z = geometry.MinEdgeZ + row * rowStride;

                if (leftToRight)
                {
                    _generationWaypoints.Add(new Vector3(minEdge, FLIGHT_HEIGHT, z));
                    _generationWaypoints.Add(new Vector3(maxEdge, FLIGHT_HEIGHT, z));
                }
                else
                {
                    _generationWaypoints.Add(new Vector3(maxEdge, FLIGHT_HEIGHT, z));
                    _generationWaypoints.Add(new Vector3(minEdge, FLIGHT_HEIGHT, z));
                }

                leftToRight = !leftToRight;
            }
        }

        /// <summary>
        /// Generates diagonal cross-cut waypoints through previously generated territory, over a
        /// <b>fixed-size</b> square centred on the swept rectangle.
        /// </summary>
        /// <param name="geometry">The derived route geometry.</param>
        /// <remarks>
        /// The 12-point shape is unchanged; only its extent is. It used to span the region minus a
        /// <c>LoadDistance</c> margin, so the tour shrank as view distance rose — 84 chunks at vd 5 down to
        /// 54 at vd 20 in FP-8 — meaning the "same" loading pass measured a 36 % smaller route at the high
        /// end. A fixed extent is what makes loading numbers comparable across a view-distance sweep.
        /// </remarks>
        private void BuildLoadingWaypoints(BenchmarkRouteGeometry geometry)
        {
            geometry.BuildTourPoints(FLIGHT_HEIGHT, _loadingWaypoints);
        }

        // ── Benchmark End ────────────────────────────────────────────────

        /// <summary>
        /// Saves world data, hides the HUD, unlocks the cursor, and shows the results screen.
        /// Does NOT transition to the main menu — the results screen's "Return" button does that.
        /// </summary>
        /// <param name="reportResult">The generated report text and file path.</param>
        private void ShowResults(BenchmarkReportResult reportResult)
        {
            _metricsCollector?.StopRecording();

            Debug.Log("[Benchmark] Benchmark Complete. Saving world data...");

            if (World.Instance != null)
                World.Instance.SaveWorldData();

            if (_hud != null)
                _hud.gameObject.SetActive(false);

            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            if (_resultsScreen != null)
                _resultsScreen.Show(reportResult.ReportRichText, reportResult.LogFilePath);
            else
                ReturnToMainMenu();
        }

        /// <summary>
        /// Reverts the runtime mode and transitions to the main menu scene.
        /// Called by the results screen's "Return to Main Menu" button.
        /// </summary>
        public void ReturnToMainMenu()
        {
            WorldLaunchState.CurrentMode = RuntimeMode.Default;
            SceneManager.LoadScene("Scenes/MainMenu", LoadSceneMode.Single);
        }
    }
}
