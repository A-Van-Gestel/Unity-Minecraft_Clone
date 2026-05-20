using System.Collections;
using System.Collections.Generic;
using Data;
using Data.Enums;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Benchmarks
{
    /// <summary>
    /// Controls the player during a benchmark profiling run.
    /// Drives the player through a two-pass waypoint system to stress-test the
    /// chunk generation, lighting, meshing, and disk loading pipelines.
    /// <para><b>Pass 1 — Generation (Phases 0–1):</b> A zigzag sweep across the world that
    /// maximizes unique chunk coverage. Row spacing and world-edge margins are derived
    /// from the current <see cref="Settings.LoadDistance"/> to ensure optimal chunk throughput.</para>
    /// <para><b>Pass 2 — Loading (Phases 2–3):</b> Diagonal cross-cuts through previously
    /// generated territory at escalating speeds, forcing chunks to be reloaded from disk.</para>
    /// <para>The benchmark runs through four timed speed phases (30 s each). Phases 0–1
    /// navigate generation waypoints; phases 2–3 switch to loading waypoints. Each pass
    /// loops its waypoints if exhausted before phases end. The benchmark ends when all
    /// phases complete.</para>
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
        /// Number of speed phases dedicated to the generation pass.
        /// Phases [0, GENERATION_PHASE_COUNT) use generation waypoints;
        /// phases [GENERATION_PHASE_COUNT, total) use loading waypoints.
        /// </summary>
        private const int GENERATION_PHASE_COUNT = 2;

        // ── Phase Configuration ──────────────────────────────────────────

        /// <summary>
        /// Movement speeds for each benchmark phase, in meters/second.
        /// Phases 0–1 are generation-oriented (slower), phases 2–3 are loading-oriented (faster).
        /// </summary>
        private static readonly float[] s_phaseSpeeds = { 10f, 20f, 50f, 100f, 200f };

        /// <summary>
        /// Duration of each speed phase in seconds.
        /// </summary>
        private const float TIME_PER_PHASE = 30f;

        // ── Runtime State ────────────────────────────────────────────────

        private readonly List<Vector3> _generationWaypoints = new List<Vector3>();
        private readonly List<Vector3> _loadingWaypoints = new List<Vector3>();
        private int _activeWaypointIndex;
        private bool _inLoadingPass;
        private int _currentPhase;
        private float _phaseTimer;
        private bool _isInitialized;
        private Transform _playerCamera;

        // ── Lifecycle ────────────────────────────────────────────────────

        /// <summary>
        /// Coroutine entry point. Waits for the world to fully load, then builds
        /// the waypoint path and begins the benchmark run.
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

            BuildWaypoints();

            if (_generationWaypoints.Count < 2)
            {
                Debug.LogError("[Benchmark] Insufficient waypoints generated. Ending benchmark.");
                EndBenchmark();
                yield break;
            }

            // Start on generation pass
            _inLoadingPass = false;
            _activeWaypointIndex = 1;

            // Teleport to first generation waypoint and face the second
            transform.position = _generationWaypoints[0];
            FaceWaypoint(_generationWaypoints[1]);

            _isInitialized = true;
            Debug.Log($"[Benchmark] Started profiling run. " +
                      $"{_generationWaypoints.Count} generation waypoints, " +
                      $"{_loadingWaypoints.Count} loading waypoints. " +
                      $"Phase 0: {s_phaseSpeeds[0]}m/s (Generation Pass)");
        }

        /// <summary>
        /// Advances the phase timer, handles pass switching, and moves the player
        /// toward the current waypoint.
        /// </summary>
        public void Update()
        {
            if (!_isInitialized) return;

            // ── Phase timer ──
            _phaseTimer += Time.deltaTime;
            if (_phaseTimer >= TIME_PER_PHASE)
            {
                _phaseTimer = 0f;
                _currentPhase++;

                if (_currentPhase >= s_phaseSpeeds.Length)
                {
                    EndBenchmark();
                    return;
                }

                // Switch to loading pass when entering the loading phase range
                if (_currentPhase >= GENERATION_PHASE_COUNT && !_inLoadingPass)
                {
                    SwitchToLoadingPass();
                }

                string passLabel = _inLoadingPass ? "Loading Pass" : "Generation Pass";
                Debug.Log($"[Benchmark] Entering Phase {_currentPhase}: " +
                          $"{s_phaseSpeeds[_currentPhase]}m/s ({passLabel})");
            }

            MoveTowardWaypoint();
        }

        // ── Pass Switching ───────────────────────────────────────────────

        /// <summary>
        /// Transitions from the generation pass to the loading pass.
        /// Resets the waypoint index and teleports the player to the first loading waypoint.
        /// </summary>
        private void SwitchToLoadingPass()
        {
            _inLoadingPass = true;
            _activeWaypointIndex = 0;

            if (_loadingWaypoints.Count > 0)
            {
                transform.position = _loadingWaypoints[0];
                _activeWaypointIndex = 1;

                if (_loadingWaypoints.Count > 1)
                    FaceWaypoint(_loadingWaypoints[1]);
            }

            Debug.Log("[Benchmark] === Switching to Loading Pass ===");
        }

        // ── Movement ─────────────────────────────────────────────────────

        /// <summary>
        /// Moves the player toward the current waypoint at the active phase speed.
        /// Uses the generation or loading waypoint list based on the current pass.
        /// Loops the active list's waypoints if exhausted before phases end.
        /// </summary>
        private void MoveTowardWaypoint()
        {
            List<Vector3> activeList = _inLoadingPass ? _loadingWaypoints : _generationWaypoints;

            if (activeList.Count == 0)
            {
                EndBenchmark();
                return;
            }

            // Loop waypoints when exhausted
            if (_activeWaypointIndex >= activeList.Count)
            {
                _activeWaypointIndex = 0;
                Debug.Log($"[Benchmark] Looping {(_inLoadingPass ? "loading" : "generation")} waypoints.");
            }

            Vector3 target = activeList[_activeWaypointIndex];
            float speed = s_phaseSpeeds[_currentPhase];
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
                int nextIndex = _activeWaypointIndex < activeList.Count
                    ? _activeWaypointIndex
                    : 0;
                FaceWaypoint(activeList[nextIndex]);
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

        // ── Waypoint Building ────────────────────────────────────────────

        /// <summary>
        /// Builds the complete waypoint sequences for both passes.
        /// Row spacing and world-edge margins are derived from the current
        /// <see cref="Settings.LoadDistance"/> to ensure chunk generation throughput
        /// is optimal for the configured view distance.
        /// </summary>
        private void BuildWaypoints()
        {
            _generationWaypoints.Clear();
            _loadingWaypoints.Clear();

            Settings settings = SettingsManager.LoadSettings();
            int loadDistance = settings.LoadDistance;

            const int chunkWidth = VoxelData.ChunkWidth;
            const int worldChunks = VoxelData.WorldSizeInChunks;

            // Margin from world edges: equal to load distance so edge chunks
            // have their full neighborhood available for lighting and meshing.
            int marginChunks = loadDistance;

            // Row stride: 2× load distance ensures each row shift generates
            // a full band of previously unseen chunks with zero overlap.
            int rowStrideChunks = loadDistance * 2;

            // World-space bounds (in blocks), inset by margin
            float minEdge = marginChunks * chunkWidth;
            float maxEdge = (worldChunks - marginChunks) * chunkWidth;
            float rowStride = rowStrideChunks * chunkWidth;

            BuildGenerationWaypoints(minEdge, maxEdge, rowStride);
            BuildLoadingWaypoints(minEdge, maxEdge);

            Debug.Log($"[Benchmark] Built {_generationWaypoints.Count + _loadingWaypoints.Count} waypoints " +
                      $"({_generationWaypoints.Count} generation, {_loadingWaypoints.Count} loading). " +
                      $"LoadDistance={loadDistance}, Margin={marginChunks}, RowStride={rowStrideChunks}");
        }

        /// <summary>
        /// Generates zigzag sweep waypoints across the world.
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
            _isInitialized = false;

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
