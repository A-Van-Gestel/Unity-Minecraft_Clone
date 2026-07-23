using System;
using System.Collections;
using System.Text;
using Commands;
using Data;
using Helpers;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Benchmarks
{
    /// <summary>
    /// TEMPORARY capture harness for the P-4 §3.4/§3.5 A/B confirm (delete after the IL2CPP capture
    /// lands in <c>Documentation/Performance/</c>). Runs the full six-leg flag × FPS-condition matrix
    /// from the 2026-07-23 editor screening session in ONE unattended run — per leg: toggle
    /// <c>enablePipelineTimeBudgets</c>/<c>enableGenerationPanicGate</c>, impose the FPS condition,
    /// teleport to fresh never-generated terrain, and measure wall-clock fill time plus frame-health
    /// metrics — then writes a single results log via
    /// <see cref="BenchmarkEnvironment.WriteReportToDisk"/> and restores every touched setting.
    /// <para><b>Usage (Development Build or editor):</b> load any world, then press <b>F10</b>
    /// (gameplay-gated via <see cref="InputManager.DebugKeyPressed"/>). The run takes several
    /// minutes and teleports the player; progress is logged as <c>[P4Bench]</c> lines.</para>
    /// </summary>
    public class P4BackpressureBenchmark : MonoBehaviour
    {
        private const Key TRIGGER_KEY = Key.F10;
        private const float LEG_TIMEOUT_SECONDS = 300f;
        private const int SETTLE_FRAMES = 30;
        private const float HITCH_THRESHOLD_SECONDS = 0.050f;
        private const float TELEPORT_Y = 120f;

        // Destination ring: far enough that any reasonable session sits on virgin terrain, salted per
        // run so repeated captures (chunks persist to the world save!) never revisit generated ground.
        private const int BASE_DISTANCE_VOXELS = 30000;

        /// <summary>One measured leg's configuration and results.</summary>
        private struct LegResult
        {
            public string Name;
            public bool BudgetsOn;
            public int TargetFps; // -1 = uncapped
            public float FillSeconds;
            public int Frames;
            public float MaxFrameMs;
            public int Hitches;
            public long GateCloses;
            public bool TimedOut;
        }

        private bool _running;
        private bool _abortRun;
        private CommandEngine _engine;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        /// <summary>Spawns the harness once per play session (zero scene edits, dev builds only).</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            GameObject host = new GameObject("P4BackpressureBenchmark [TEMP]");
            DontDestroyOnLoad(host);
            host.AddComponent<P4BackpressureBenchmark>();
            Debug.Log("[P4Bench] Temp A/B harness armed — load a world and press F10 to run the 6-leg capture.");
        }
#endif

        private void Update()
        {
            if (_running) return;

            InputManager input = InputManager.Instance;
            if (input != null && input.DebugKeyPressed(TRIGGER_KEY))
                RequestRun();
        }

        /// <summary>
        /// Starts the capture unless one is already running — the SINGLE entry for both the F10 key
        /// and programmatic drivers (a raw <c>StartCoroutine("RunMatrix", …)</c> bypasses the
        /// <c>_running</c> guard; two concurrent matrices teleport-fight over the player and
        /// invalidate every leg — the smoke-run-2 failure mode).
        /// </summary>
        public void RequestRun()
        {
            if (_running)
            {
                Debug.LogWarning("[P4Bench] A capture is already running — start request ignored.");
                return;
            }

            World world = World.Instance;
            if (world == null || !world.IsWorldLoaded)
            {
                Debug.LogWarning("[P4Bench] No loaded world — load a world first.");
                return;
            }

            _running = true;
            StartCoroutine(RunMatrix(world));
        }

        /// <summary>Runs all six legs, writes the single report, and restores the session settings.</summary>
        /// <param name="world">The live world.</param>
        private IEnumerator RunMatrix(World world)
        {
            // The engine gets its own registry so the temp harness never touches the installer's
            // pinned command count. The context must be explicitly attached to the live world —
            // a default context has no world and every world-touching command fails with
            // "No world is loaded" (the WorldUIManager.Start wiring, replicated here).
            _engine = new CommandEngine();
            ConsoleCommandInstaller.RegisterAll(_engine.Registry);
            _engine.Context.AttachWorld(world, world.player);
            _abortRun = false;

            // Capture everything the legs mutate.
            bool origBudgets = world.settings.enablePipelineTimeBudgets;
            bool origGate = world.settings.enableGenerationPanicGate;
            int origTargetFps = Application.targetFrameRate;
            int origVsync = QualitySettings.vSyncCount;

            // Return point captured in absolute voxel space (origin-shift-safe): the center column of
            // the player's current chunk. Y is the harness teleport height — close enough for a
            // convenience return hop.
            Vector2Int origChunkVoxel = world.PlayerChunkCoord.ToVoxelOrigin();
            int origX = origChunkVoxel.x + ChunkMath.CHUNK_WIDTH / 2;
            int origZ = origChunkVoxel.y + ChunkMath.CHUNK_WIDTH / 2;

            QualitySettings.vSyncCount = 0;

            // Per-run salt keeps repeat captures on virgin terrain (generated chunks persist).
            int salt = (int)(DateTime.Now.Ticks % 997) * 64;
            LegResult[] legs =
            {
                new LegResult { Name = "L1 budgets=ON  fps=uncapped", BudgetsOn = true, TargetFps = -1 },
                new LegResult { Name = "L2 budgets=ON  fps=15cap", BudgetsOn = true, TargetFps = 15 },
                new LegResult { Name = "L3 budgets=OFF fps=uncapped", BudgetsOn = false, TargetFps = -1 },
                new LegResult { Name = "L4 budgets=OFF fps=15cap", BudgetsOn = false, TargetFps = 15 },
                new LegResult { Name = "L5 budgets=ON  fps=5cap", BudgetsOn = true, TargetFps = 5 },
                new LegResult { Name = "L6 budgets=OFF fps=5cap", BudgetsOn = false, TargetFps = 5 },
            };

            Debug.Log("[P4Bench] Starting 6-leg A/B capture — do not touch the player until the report path is logged.");

            for (int i = 0; i < legs.Length; i++)
            {
                world.settings.enablePipelineTimeBudgets = legs[i].BudgetsOn;
                world.settings.enableGenerationPanicGate = legs[i].BudgetsOn;
                Application.targetFrameRate = legs[i].TargetFps;

                // Unique far destination per leg, spread on a ring so legs never overlap each other.
                double angle = i * (2.0 * Math.PI / legs.Length) + 0.35;
                int destX = (int)Math.Round((BASE_DISTANCE_VOXELS + salt) * Math.Cos(angle));
                int destZ = (int)Math.Round((BASE_DISTANCE_VOXELS + salt) * Math.Sin(angle));

                yield return MeasureLeg(world, legs, i, destX, destZ);

                if (_abortRun)
                {
                    Debug.LogError("[P4Bench] Run ABORTED — see the error above. Settings restored, no report written.");
                    break;
                }
            }

            // Restore the session exactly as found (settings object is live — no save is triggered).
            world.settings.enablePipelineTimeBudgets = origBudgets;
            world.settings.enableGenerationPanicGate = origGate;
            Application.targetFrameRate = origTargetFps;
            QualitySettings.vSyncCount = origVsync;
            ExecuteLogged($"/teleport {origX.ToString()} {Mathf.RoundToInt(TELEPORT_Y).ToString()} {origZ.ToString()}");

            if (!_abortRun) WriteReport(world, legs);
            _running = false;
        }

        /// <summary>Teleports to the leg's destination and measures fill + frame health.</summary>
        /// <param name="world">The live world.</param>
        /// <param name="legs">The leg array (result written back to <paramref name="index"/>).</param>
        /// <param name="index">The leg being measured.</param>
        /// <param name="destX">Destination voxel X.</param>
        /// <param name="destZ">Destination voxel Z.</param>
        private IEnumerator MeasureLeg(World world, LegResult[] legs, int index, int destX, int destZ)
        {
            long closesBefore = world.GenerationGateCloseCount;
            if (!ExecuteLogged($"/teleport {destX.ToString()} {Mathf.RoundToInt(TELEPORT_Y).ToString()} {destZ.ToString()}"))
            {
                Debug.LogError($"[P4Bench] {legs[index].Name}: teleport command FAILED — aborting.");
                _abortRun = true;
                yield break;
            }

            // Arrival is asynchronous (CMD-2 teleport hold): the fill predicate must not be evaluated
            // against the OLD, fully-loaded square — that ends the leg after the bare settle window
            // having measured nothing (the smoke-run-1 failure mode). ALL metrics (timer, frames,
            // hitches) arm at arrival too, so the teleport-hold hitch of leaving the old area — which
            // is identical across legs — cannot contaminate the A/B frame-health comparison.
            int destChunkX = ChunkMath.VoxelToChunk(destX);
            int destChunkZ = ChunkMath.VoxelToChunk(destZ);

            float legStart = Time.realtimeSinceStartup;
            float t0 = 0f;
            int f0 = 0;
            float maxDt = 0f;
            int hitches = 0;
            int settleFrames = 0;
            bool departed = false;
            bool timedOut = true;

            while (Time.realtimeSinceStartup - legStart < LEG_TIMEOUT_SECONDS)
            {
                yield return null;

                if (!departed)
                {
                    ChunkCoord playerChunk = world.PlayerChunkCoord;
                    departed = Mathf.Abs(playerChunk.X - destChunkX) <= 2 && Mathf.Abs(playerChunk.Z - destChunkZ) <= 2;
                    if (!departed) continue;

                    // Arrived — arm every metric from this frame.
                    t0 = Time.realtimeSinceStartup;
                    f0 = Time.frameCount;
                    continue;
                }

                float dt = Time.unscaledDeltaTime;
                if (dt > maxDt) maxDt = dt;
                if (dt > HITCH_THRESHOLD_SECONDS) hitches++;

                // Fill = populated terrain AND every measured pipeline stage drained — including the
                // mesh/draw tail the budgets deliberately defer (omitting it would bias the A/B toward
                // budgets-ON, which pushes exactly that work past the old predicate). The mesh BUILD
                // queue and the lighting waiting set are excluded on purpose: their steady state is
                // the load-square perimeter ring, which can never be served (missing outer neighbors).
                bool drained = world.GenerationRequestQueueCount == 0
                               && world.JobManager.GenerationJobs.Count == 0
                               && world.LightWorkReadyCount == 0
                               && world.JobManager.LightingJobs.Count == 0
                               && world.JobManager.MeshJobs.Count == 0
                               && world.ChunksToDraw.Count == 0;

                if (drained && IsLoadSquarePopulated(world))
                {
                    // Sustained settle so a transient lull cannot end the leg early.
                    settleFrames++;
                    if (settleFrames < SETTLE_FRAMES) continue;
                    timedOut = false;
                    break;
                }

                settleFrames = 0;
            }

            if (!departed)
            {
                Debug.LogError($"[P4Bench] {legs[index].Name}: player never arrived at the destination — aborting.");
                _abortRun = true;
                yield break;
            }

            legs[index].FillSeconds = Time.realtimeSinceStartup - t0;
            legs[index].Frames = Time.frameCount - f0;
            legs[index].MaxFrameMs = maxDt * 1000f;
            legs[index].Hitches = hitches;
            legs[index].GateCloses = world.GenerationGateCloseCount - closesBefore;
            legs[index].TimedOut = timedOut;

            Debug.Log($"[P4Bench] {legs[index].Name}: fill={legs[index].FillSeconds:F2}s frames={legs[index].Frames.ToString()} " +
                      $"avgFps={legs[index].Frames / legs[index].FillSeconds:F1} maxFrame={legs[index].MaxFrameMs:F1}ms " +
                      $"hitches50={hitches.ToString()} gateCloses={legs[index].GateCloses.ToString()}" +
                      (timedOut ? " TIMED OUT" : string.Empty));
        }

        /// <summary>Whether every chunk of the load square around the player is populated.</summary>
        /// <param name="world">The live world.</param>
        /// <returns>True when the square is fully populated.</returns>
        private static bool IsLoadSquarePopulated(World world)
        {
            ChunkCoord center = world.PlayerChunkCoord;
            int loadDist = world.settings.LoadDistance;

            for (int dx = -loadDist; dx <= loadDist; dx++)
            {
                for (int dz = -loadDist; dz <= loadDist; dz++)
                {
                    if (!world.worldData.TryGetChunk(center.Neighbor(dx, dz).ToVoxelOrigin(), out ChunkData data)
                        || !data.IsPopulated)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>Formats every leg plus the derived A/B ratios into one report file.</summary>
        /// <param name="world">The live world (settings context for the header).</param>
        /// <param name="legs">The measured legs.</param>
        private static void WriteReport(World world, LegResult[] legs)
        {
            int side = world.settings.LoadDistance * 2 + 1;
            int squareChunks = side * side;

            StringBuilder sb = new StringBuilder(capacity: 4096);
            sb.AppendLine("=== P-4 §3.4/§3.5 Backpressure A/B — single-run capture (TEMP harness) ===");
            sb.AppendLine($"Timestamp:      {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"World:          '{world.worldData.worldName}' (LoadDistance {world.settings.LoadDistance.ToString()} → {squareChunks.ToString()}-chunk square)");
            sb.AppendLine($"Caps (quota anchors): light/frame {world.settings.maxLightJobsPerFrame.ToString()}, mesh/frame {world.settings.maxMeshRebuildsPerFrame.ToString()}");
            sb.AppendLine($"Ceilings ms:    light {world.settings.lightScheduleBudgetMs:F1}, meshSched {world.settings.meshScheduleBudgetMs:F1}, " +
                          $"genProc {world.settings.genProcessBudgetMs:F1}, meshApply {world.settings.meshApplyBudgetMs:F1}, draw {world.settings.drawApplyBudgetMs:F1}");
            sb.AppendLine($"Panic gate:     close {world.settings.panicGateCloseThreshold.ToString()} / reopen {world.settings.panicGateReopenThreshold.ToString()}, " +
                          $"in-flight light cap {world.settings.maxInFlightLightingJobs.ToString()}");
            sb.AppendLine();
            sb.Append(BenchmarkEnvironment.DescribeSystem());
            sb.AppendLine();
            sb.AppendLine("-- Methodology --");
            sb.AppendLine("One leg = teleport to fresh terrain; ALL metrics arm at arrival (teleport-hold excluded).");
            sb.AppendLine("Fill = wall-clock until the load square is fully populated AND gen queue + in-flight gen +");
            sb.AppendLine($"lighting ready-set/jobs + mesh jobs + draw queue are drained for {SETTLE_FRAMES.ToString()} frames");
            sb.AppendLine("(mesh BUILD queue + lighting waiting set excluded: perimeter-ring steady state).");
            sb.AppendLine("hitches50 = frames > 50 ms (cap-implied at the 15/5 FPS conditions — compare uncapped legs only).");
            sb.AppendLine("Editor screening baseline: Documentation/Performance/CHUNK_PIPELINE_P4_BACKPRESSURE_2026-07-23_BENCHMARK.md");
            sb.AppendLine();
            sb.AppendLine("-- Results --");
            sb.AppendLine("Leg                          | fill s  | frames | avgFps | maxFrame ms | hitches50 | chunks/s | gateCloses");

            foreach (LegResult leg in legs)
            {
                float avgFps = leg.Frames / Mathf.Max(leg.FillSeconds, 0.001f);
                float rate = squareChunks / Mathf.Max(leg.FillSeconds, 0.001f);
                sb.AppendLine($"{leg.Name,-28} | {leg.FillSeconds,7:F2} | {leg.Frames,6} | {avgFps,6:F1} | {leg.MaxFrameMs,11:F1} | " +
                              $"{leg.Hitches,9} | {rate,8:F1} | {leg.GateCloses,10}" + (leg.TimedOut ? "  ** TIMED OUT **" : string.Empty));
            }

            sb.AppendLine();
            sb.AppendLine("-- Derived (the §3.4 claims) --");
            sb.AppendLine($"Uncapped fill cost   ON/OFF: x{Ratio(legs[0].FillSeconds, legs[2].FillSeconds):F2}   (editor screening: x1.69)");
            sb.AppendLine($"Uncapped hitch rate  ON:  {100f * legs[0].Hitches / Mathf.Max(legs[0].Frames, 1):F0}%   OFF: {100f * legs[2].Hitches / Mathf.Max(legs[2].Frames, 1):F0}%   (editor: 11% vs 67%)");
            sb.AppendLine($"Mid-band constancy   ON fill 15cap/uncapped: x{Ratio(legs[1].FillSeconds, legs[0].FillSeconds):F2}   OFF: x{Ratio(legs[3].FillSeconds, legs[2].FillSeconds):F2}");
            sb.AppendLine($"Deep-cap scaling     ON fill 5cap/15cap: x{Ratio(legs[4].FillSeconds, legs[1].FillSeconds):F2}   OFF: x{Ratio(legs[5].FillSeconds, legs[3].FillSeconds):F2}   (ceilings bind deep — expected ~x3 ON)");
            sb.AppendLine();
            sb.AppendLine("Verdict: fill by hand into the Documentation/Performance report (GO expectation: uncapped");
            sb.AppendLine("hitch-rate and max-frame clearly better ON at a bounded fill-latency cost).");

            string report = sb.ToString();
            string location = BenchmarkEnvironment.WriteReportToDisk(report, "P4BackpressureAB");
            Debug.Log(location != null
                ? $"[P4Bench] Capture complete — report written to: {location}"
                : "[P4Bench] Capture complete — report write FAILED; results above in the log.");
            Debug.Log(report);
        }

        /// <summary>Safe ratio for the derived rows.</summary>
        /// <param name="a">Numerator.</param>
        /// <param name="b">Denominator.</param>
        /// <returns><c>a/b</c>, or 0 when the denominator is ~0.</returns>
        private static float Ratio(float a, float b) => b > 0.001f ? a / b : 0f;

        /// <summary>Executes a console command, logs its output, and reports whether it succeeded.</summary>
        /// <param name="command">The slash-prefixed command line.</param>
        /// <returns>False when any output line carries the error severity.</returns>
        private bool ExecuteLogged(string command)
        {
            bool ok = true;
            foreach (ConsoleLine line in _engine.Execute(command).Lines)
            {
                Debug.Log($"[P4Bench]   {command} -> [{line.Severity.ToString()}] {line.Text}");
                if (line.Severity == ConsoleLineSeverity.Error) ok = false;
            }

            return ok;
        }
    }
}
