using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Data;
using Data.Enums;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

namespace Benchmarks
{
    /// <summary>
    /// Controls the player during a benchmark profiling run.
    /// Drives the player through a two-pass waypoint system to stress-test the
    /// chunk generation, lighting, meshing, and disk loading pipelines.
    /// <para><b>Pass 1 — Generation:</b> A zigzag sweep across a configurable
    /// benchmark region (see <see cref="Settings.benchmarkRegionSize"/>) that
    /// maximizes unique chunk coverage. Row spacing and margins are derived
    /// from the current <see cref="Settings.LoadDistance"/> to ensure optimal chunk throughput.
    /// The pass completes when all generation waypoints have been visited, with speed
    /// escalating on a timer within the pass.</para>
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
        /// Duration of each speed phase in seconds.
        /// </summary>
        private const float TIME_PER_PHASE = 30f;

        /// <summary>
        /// Number of frames to wait after the chunk pipeline drains before starting
        /// a measured phase. Allows <see cref="PerformanceMonitor"/>'s moving averages
        /// (30–60 frame windows) to flush any spike data from the preceding teleport.
        /// </summary>
        private const int SETTLE_FRAMES = 60;

        // ── Phase Group Names ────────────────────────────────────────────

        private const string GROUP_GENERATION = "Generation Pass";
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
        private int _regionChunks;
        private int _configuredRegionChunks;

        // ── Lifecycle ────────────────────────────────────────────────────

        /// <summary>
        /// Coroutine entry point. Waits for the world to fully load, parses
        /// speed configuration, then runs the complete benchmark:
        /// generation pass → transition → loading pass.
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

            BuildWaypoints(settings);

            if (_generationWaypoints.Count < 2)
            {
                Debug.LogError("[Benchmark] Insufficient waypoints generated. Ending benchmark.");
                EndBenchmark();
                yield break;
            }

            Debug.Log("[Benchmark] Waiting for initial chunk pipeline to settle...");
            yield return WaitForChunkPipelineToSettle();

            _metricsCollector = new BenchmarkMetricsCollector(_generationSpeeds.Length + _loadingSpeeds.Length + 1);
            _metricsCollector.StartRecording();
            Stopwatch totalStopwatch = Stopwatch.StartNew();

            Debug.Log($"[Benchmark] Started profiling run. " +
                      $"{_generationWaypoints.Count} generation waypoints, " +
                      $"{_loadingWaypoints.Count} loading waypoints. " +
                      $"Generation speeds: [{string.Join(", ", _generationSpeeds)}] m/s, " +
                      $"Loading speeds: [{string.Join(", ", _loadingSpeeds)}] m/s.");

            // === Pass 1: Generation ===
            yield return RunGenerationPass();

            // === Transition: Drain Jobs → Save → Force Unload ===
            yield return TransitionToLoadingPass();

            // === Pass 2: Loading ===
            yield return RunLoadingPass();

            totalStopwatch.Stop();
            _metricsCollector.StopRecording();

            BenchmarkReportGenerator.GenerateAndWriteReport(
                _metricsCollector,
                _generationSpeeds,
                _loadingSpeeds,
                TIME_PER_PHASE,
                _regionChunks,
                _configuredRegionChunks,
                _generationWaypoints.Count,
                _loadingWaypoints.Count,
                totalStopwatch.Elapsed);

            EndBenchmark();
        }

        private void OnDestroy()
        {
            _metricsCollector?.StopRecording();
        }

        // ── Pass Execution ───────────────────────────────────────────────

        /// <summary>
        /// Runs the generation pass: visits every generation waypoint exactly once.
        /// Speed escalates every <see cref="TIME_PER_PHASE"/> seconds, clamping
        /// at the highest generation speed if all phases are exhausted before
        /// all waypoints are visited.
        /// </summary>
        private IEnumerator RunGenerationPass()
        {
            transform.position = _generationWaypoints[0];
            _activeWaypointIndex = 1;
            FaceWaypoint(_generationWaypoints[1]);

            yield return WaitForChunkPipelineToSettle();

            int speedIndex = 0;
            float phaseTimer = 0f;

            _metricsCollector.BeginPhase($"{_generationSpeeds[0]} m/s", GROUP_GENERATION);
            Debug.Log($"[Benchmark] Generation Pass — Phase 0: {_generationSpeeds[0]}m/s");

            while (_activeWaypointIndex < _generationWaypoints.Count)
            {
                phaseTimer += Time.deltaTime;
                if (phaseTimer >= TIME_PER_PHASE && speedIndex < _generationSpeeds.Length - 1)
                {
                    phaseTimer = 0f;
                    speedIndex++;
                    _metricsCollector.BeginPhase($"{_generationSpeeds[speedIndex]} m/s", GROUP_GENERATION);
                    Debug.Log($"[Benchmark] Generation Pass — Phase {speedIndex}: " +
                              $"{_generationSpeeds[speedIndex]}m/s");
                }

                StepTowardWaypoint(_generationWaypoints, _generationSpeeds[speedIndex], loop: false);
                yield return null;
            }

            _metricsCollector.EndPhase();
            Debug.Log("[Benchmark] === Generation Pass Complete ===");
        }

        /// <summary>
        /// Transitions from the generation pass to the loading pass by delegating
        /// to <see cref="World.ForceUnloadAllChunks"/>, which drains all active jobs,
        /// saves world data, and removes every chunk from memory in a single pass.
        /// </summary>
        private IEnumerator TransitionToLoadingPass()
        {
            _metricsCollector.BeginPhase("Drain + Save + Unload", GROUP_TRANSITION);
            Debug.Log("[Benchmark] === Transition: Force-unloading all chunks... ===");
            yield return World.Instance.ForceUnloadAllChunks();
            _metricsCollector.EndPhase();
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

            transform.position = _loadingWaypoints[0];
            _activeWaypointIndex = 1;
            FaceWaypoint(_loadingWaypoints[1]);

            yield return WaitForChunkPipelineToSettle();

            for (int i = 0; i < _loadingSpeeds.Length; i++)
            {
                float phaseTimer = 0f;

                _metricsCollector.BeginPhase($"{_loadingSpeeds[i]} m/s", GROUP_LOADING);
                Debug.Log($"[Benchmark] Loading Pass — Phase {i}: {_loadingSpeeds[i]}m/s");

                while (phaseTimer < TIME_PER_PHASE)
                {
                    phaseTimer += Time.deltaTime;
                    StepTowardWaypoint(_loadingWaypoints, _loadingSpeeds[i], loop: true);
                    yield return null;
                }

                _metricsCollector.EndPhase();
            }

            Debug.Log("[Benchmark] === Loading Pass Complete ===");
        }

        // ── Pipeline Settling ────────────────────────────────────────────

        /// <summary>
        /// Waits for all active generation, lighting, and meshing jobs to complete.
        /// Called after teleporting to a new waypoint origin so the load-radius burst
        /// from the teleport is excluded from the timed measurement phases.
        /// </summary>
        private static IEnumerator WaitForChunkPipelineToSettle()
        {
            while (World.Instance.JobManager.HasActiveJobs)
            {
                yield return null;
            }

            // Wait additional frames for PerformanceMonitor's moving averages (30–60 frame
            // windows) to naturally flush any spike data from the teleport/job burst.
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

            Vector3 target = waypoints[_activeWaypointIndex];
            float step = speed * Time.deltaTime;

            Vector3 currentPos = transform.position;
            Vector3 toTarget = target - currentPos;
            float distance = toTarget.magnitude;

            if (distance <= step)
            {
                // Arrived at waypoint — snap and advance
                transform.position = target;
                _activeWaypointIndex++;

                // Face next waypoint (with loop-aware index)
                int nextIndex = _activeWaypointIndex < waypoints.Count
                    ? _activeWaypointIndex
                    : (loop ? 0 : _activeWaypointIndex - 1);
                FaceWaypoint(waypoints[nextIndex]);
            }
            else
            {
                // Move toward waypoint
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
        /// Builds the complete waypoint sequences for both passes.
        /// The benchmark region is derived from <see cref="Settings.benchmarkRegionSize"/>,
        /// auto-scaled upward if needed to sustain all generation speed phases, and
        /// clamped to the actual world size. Row spacing and margins are derived from
        /// the current <see cref="Settings.LoadDistance"/> to ensure chunk generation
        /// throughput is optimal for the configured view distance.
        /// </summary>
        /// <param name="settings">The active settings instance.</param>
        private void BuildWaypoints(Settings settings)
        {
            _generationWaypoints.Clear();
            _loadingWaypoints.Clear();

            int loadDistance = settings.LoadDistance;

            const int chunkWidth = VoxelData.ChunkWidth;
            const int worldChunks = VoxelData.WorldSizeInChunks;

            _configuredRegionChunks = Mathf.Min(settings.benchmarkRegionSize, worldChunks);
            int configuredRegion = _configuredRegionChunks;

            // Margin from region edges: equal to load distance so edge chunks
            // have their full neighborhood available for lighting and meshing.
            int marginChunks = loadDistance;

            // Row stride: 2× load distance ensures each row shift generates
            // a full band of previously unseen chunks with zero overlap.
            int rowStrideChunks = loadDistance * 2;
            float rowStride = rowStrideChunks * chunkWidth;

            // Auto-scale region if too small for the configured generation speeds
            int minimumRegion = CalculateMinimumRegionChunks(_generationSpeeds, marginChunks, rowStride, chunkWidth);
            int regionChunks = configuredRegion;

            if (regionChunks < minimumRegion)
            {
                int scaledRegion = Mathf.Min(minimumRegion, worldChunks);
                Debug.LogWarning($"[Benchmark] Configured region ({configuredRegion} chunks) is too small " +
                                 $"for the generation speed phases. Auto-increasing to {scaledRegion} chunks " +
                                 $"(minimum required: {minimumRegion}).");
                regionChunks = scaledRegion;
            }

            _regionChunks = regionChunks;

            // Center the benchmark region within the world
            int regionStartChunk = (worldChunks - regionChunks) / 2;

            // World-space bounds (in blocks), inset by margin
            float minEdge = (regionStartChunk + marginChunks) * chunkWidth;
            float maxEdge = (regionStartChunk + regionChunks - marginChunks) * chunkWidth;

            if (maxEdge <= minEdge)
            {
                Debug.LogError($"[Benchmark] Region too small for margin. " +
                               $"RegionChunks={regionChunks}, Margin={marginChunks}. " +
                               $"Increase benchmarkRegionSize or decrease viewDistance.");
                return;
            }

            BuildGenerationWaypoints(minEdge, maxEdge, rowStride);
            BuildLoadingWaypoints(minEdge, maxEdge);

            Debug.Log($"[Benchmark] Built {_generationWaypoints.Count + _loadingWaypoints.Count} waypoints " +
                      $"({_generationWaypoints.Count} generation, {_loadingWaypoints.Count} loading). " +
                      $"Region={regionChunks} chunks{(regionChunks != configuredRegion ? $" (configured: {configuredRegion})" : "")}, " +
                      $"LoadDistance={loadDistance}, Margin={marginChunks}, RowStride={rowStrideChunks}");
        }

        /// <summary>
        /// Calculates the minimum benchmark region size (in chunks) needed to produce
        /// enough zigzag waypoint distance to sustain all generation speed phases.
        /// <para>The zigzag pattern produces <c>numRows × sweepWidth</c> total distance.
        /// Each speed phase consumes <c>speed × TIME_PER_PHASE</c> distance. The region
        /// must be large enough that the total waypoint distance exceeds the sum of all
        /// phase distances.</para>
        /// </summary>
        /// <param name="generationSpeeds">The generation speed phases.</param>
        /// <param name="marginChunks">Edge margin in chunks (equal to load distance).</param>
        /// <param name="rowStride">Distance between zigzag rows in blocks.</param>
        /// <param name="chunkWidth">Width of a single chunk in blocks.</param>
        /// <returns>The minimum region size in chunks, including margins.</returns>
        private static int CalculateMinimumRegionChunks(float[] generationSpeeds, int marginChunks, float rowStride, int chunkWidth)
        {
            float totalTravelDistance = 0f;
            foreach (float generationSpeed in generationSpeeds)
            {
                totalTravelDistance += generationSpeed * TIME_PER_PHASE;
            }

            // Zigzag distance ≈ sweepWidth² / rowStride (for large regions).
            // Solve: sweepWidth² / rowStride >= totalTravelDistance
            // sweepWidth >= sqrt(totalTravelDistance * rowStride)
            float minSweepWidth = Mathf.Sqrt(totalTravelDistance * rowStride);
            int minUsableChunks = Mathf.CeilToInt(minSweepWidth / chunkWidth);

            return minUsableChunks + 2 * marginChunks;
        }

        /// <summary>
        /// Generates zigzag sweep waypoints across the benchmark region.
        /// Each row alternates direction (left→right, right→left) to minimize
        /// dead travel and maximize unique chunk generation.
        /// <code>
        /// ┌──────────────────────────┐
        /// │ →→→→→→→→→→→→→→→→→→→→→→→→│
        /// │                          │
        /// │ ←←←←←←←←←←←←←←←←←←←←←←←│
        /// │                          │
        /// │ →→→→→→→→→→→→→→→→→→→→→→→→│
        /// └──────────────────────────┘
        /// </code>
        /// </summary>
        /// <param name="minEdge">Minimum world-space coordinate (blocks).</param>
        /// <param name="maxEdge">Maximum world-space coordinate (blocks).</param>
        /// <param name="rowStride">Distance between rows (blocks).</param>
        private void BuildGenerationWaypoints(float minEdge, float maxEdge, float rowStride)
        {
            bool leftToRight = true;

            for (float z = minEdge; z <= maxEdge; z += rowStride)
            {
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
        /// Generates diagonal cross-cut waypoints through previously generated territory.
        /// These force the chunk system to reload data from disk at high speeds,
        /// stress-testing deserialization, re-lighting, and re-meshing.
        /// <code>
        /// ┌──────────────────────────┐
        /// │ ╲          ╱      ─────  │
        /// │   ╲      ╱     ╱         │
        /// │     ╲  ╱     │           │
        /// │      ╳       │           │
        /// │     ╱  ╲     │           │
        /// │   ╱      ╲     ╲         │
        /// │ ╱          ╲      ─────  │
        /// └──────────────────────────┘
        /// </code>
        /// </summary>
        /// <param name="minEdge">Minimum world-space coordinate (blocks).</param>
        /// <param name="maxEdge">Maximum world-space coordinate (blocks).</param>
        private void BuildLoadingWaypoints(float minEdge, float maxEdge)
        {
            float midX = (minEdge + maxEdge) * 0.5f;
            float midZ = (minEdge + maxEdge) * 0.5f;

            // Diagonal: SW → NE corner
            _loadingWaypoints.Add(new Vector3(minEdge, FLIGHT_HEIGHT, minEdge));
            _loadingWaypoints.Add(new Vector3(maxEdge, FLIGHT_HEIGHT, maxEdge));

            // Reverse diagonal: SE → NW corner
            _loadingWaypoints.Add(new Vector3(maxEdge, FLIGHT_HEIGHT, minEdge));
            _loadingWaypoints.Add(new Vector3(minEdge, FLIGHT_HEIGHT, maxEdge));

            // Horizontal cross through center: W → E
            _loadingWaypoints.Add(new Vector3(minEdge, FLIGHT_HEIGHT, midZ));
            _loadingWaypoints.Add(new Vector3(maxEdge, FLIGHT_HEIGHT, midZ));

            // Vertical cross through center: N → S
            _loadingWaypoints.Add(new Vector3(midX, FLIGHT_HEIGHT, maxEdge));
            _loadingWaypoints.Add(new Vector3(midX, FLIGHT_HEIGHT, minEdge));

            // Diamond pattern through center
            _loadingWaypoints.Add(new Vector3(midX, FLIGHT_HEIGHT, minEdge));
            _loadingWaypoints.Add(new Vector3(maxEdge, FLIGHT_HEIGHT, midZ));
            _loadingWaypoints.Add(new Vector3(midX, FLIGHT_HEIGHT, maxEdge));
            _loadingWaypoints.Add(new Vector3(minEdge, FLIGHT_HEIGHT, midZ));
        }

        // ── Benchmark End ────────────────────────────────────────────────

        /// <summary>
        /// Saves world data, resets the runtime mode, and returns to the main menu.
        /// </summary>
        private void EndBenchmark()
        {
            _metricsCollector?.StopRecording();

            Debug.Log("[Benchmark] Benchmark Complete. Saving world data...");

            // Save world data so region files and level.dat are fully persisted
            if (World.Instance != null)
                World.Instance.SaveWorldData();

            // Revert mode so the main menu acts normally
            WorldLaunchState.CurrentMode = RuntimeMode.Default;

            // Ensure cursor is unlocked
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            SceneManager.LoadScene("Scenes/MainMenu", LoadSceneMode.Single);
        }
    }
}
