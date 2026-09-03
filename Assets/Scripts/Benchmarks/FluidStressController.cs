using System.Collections;
using System.Collections.Generic;
using Data;
using Data.Enums;
using Helpers;
using UnityEngine;

namespace Benchmarks
{
    /// <summary>
    /// Drives the full-world fluid stress pass — the TG-4 §5 <b>attribution</b> gate. Where the isolated
    /// <c>FluidTickBenchmark</c> measures the tick alone, this seeds a deterministic ocean flood in a <i>real</i>
    /// loaded world and lets the <b>real throttled pipeline</b> (mesh rebuilds + lighting + cross-chunk spread)
    /// run, capturing the per-frame Tick / Apply / Mesh / Light split via <see cref="WorldFrameProfiler"/>. The
    /// question it answers: in the real ocean frame, is the behavior tick or the mesh-rebuild it triggers the
    /// dominant main-thread cost?
    /// <para>
    /// Parallel to <see cref="BenchmarkController"/> (attached to the player by <c>Player.Awake</c> in
    /// <see cref="RuntimeMode.FluidStress"/>; forces VSync off / FPS uncapped; reuses the pipeline-settle wait).
    /// Flow: settle the loaded region → stamp the flood <b>substrate</b> (floor + cleared sky band) and settle
    /// (unmeasured) → record a settled <b>Baseline</b> → stamp the water-cap <b>flood trigger</b> and record the
    /// cascade → write the attribution report.
    /// </para>
    /// </summary>
    public class FluidStressController : MonoBehaviour
    {
        /// <summary>
        /// Side length of the square flood region, in chunks. <b>5 → 25 chunks = render-distance-5 ocean scale</b>
        /// (the regime the historical stutter occurred in). 3 (9 chunks) is the lighter validated-safe fallback if a
        /// machine struggles. Larger regions flood more chunks simultaneously — heavier mesh/light load.
        /// </summary>
        private const int REGION_CHUNKS = 5;

        /// <summary>
        /// Render distance forced for the capture (chunks loaded around the player). Covers the flood region plus a
        /// neighbor margin so the flood's edges have real loaded neighbors (cross-chunk realism).
        /// </summary>
        private const int VIEW_DISTANCE = REGION_CHUNKS + 1;

        /// <summary>
        /// Water source mods enqueued per frame while releasing the flood. Throttling the stamp across frames lets the
        /// pipeline's per-frame mesh/light throttles engage instead of dirtying every region chunk in one drain — the
        /// single-frame avalanche that previously exhausted memory.
        /// </summary>
        private const int STAMP_BATCH = 1024;

        /// <summary>Seconds to record the settled, pre-flood baseline.</summary>
        private const float BASELINE_SECONDS = 4f;

        /// <summary>Seconds to record the flood — long enough to span the peak tick + the mesh/light cascade and settle (tick fires once per second).</summary>
        private const float FLOOD_SECONDS = 16f;

        /// <summary>Frames to wait after the job pipeline drains, letting metrics flush before a measured phase.</summary>
        private const int SETTLE_FRAMES = 60;

        /// <summary>
        /// Hard cap on how long <see cref="WaitForPipelineToSettle"/> waits for the job pipeline to go idle. If it is
        /// exceeded the run proceeds anyway (with a warning) rather than hanging forever — a safety net against a
        /// lingering lighting edge-check or other non-settling state.
        /// </summary>
        private const int SETTLE_TIMEOUT_FRAMES = 1200;

        private int _savedVSyncCount;
        private int _savedTargetFrameRate;
        private bool _frameRateOverridden;
        private bool _profilerEnabled;

        /// <summary>Coroutine entry point. No-op (self-destruct) outside <see cref="RuntimeMode.FluidStress"/>.</summary>
        public IEnumerator Start()
        {
            if (WorldLaunchState.CurrentMode != RuntimeMode.FluidStress)
            {
                Destroy(this);
                yield break;
            }

            while (World.Instance == null || !World.Instance.IsWorldLoaded)
                yield return null;

            // Deterministic capture conditions: render-distance-5 ocean scale, lighting on (so the displacement
            // work is measured), VSync off / FPS uncapped (clean CPU timing).
            if (World.Instance.settings != null)
            {
                World.Instance.settings.viewDistance = VIEW_DISTANCE;
                World.Instance.settings.enableLighting = true;
            }

            _savedVSyncCount = QualitySettings.vSyncCount;
            _savedTargetFrameRate = Application.targetFrameRate;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            _frameRateOverridden = true;

            WorldFrameProfiler.Enabled = true;
            _profilerEnabled = true;

            // Teleport to the default-spawn region at sky height so its columns load around us and the suspended
            // ocean is in view. The flood region is the center REGION_CHUNKS×REGION_CHUNKS chunks.
            const int centerChunk = VoxelData.DefaultSpawnPosition / VoxelData.ChunkWidth;
            ChunkCoord regionMin = new ChunkCoord(centerChunk - REGION_CHUNKS / 2, centerChunk - REGION_CHUNKS / 2);
            transform.position = WorldOrigin.VoxelToUnity(new Vector3(
                VoxelData.DefaultSpawnPosition, FluidBenchmarkScenarios.SkyWaterTopY + 16f, VoxelData.DefaultSpawnPosition));
            transform.rotation = Quaternion.Euler(45f, 0f, 0f); // look down toward the flood

            Debug.Log($"[FluidStress] World loaded. Region min chunk {regionMin.X},{regionMin.Z} " +
                      $"({REGION_CHUNKS}×{REGION_CHUNKS}={REGION_CHUNKS * REGION_CHUNKS} chunks). Settling...");
            yield return WaitForPipelineToSettle();

            // --- Substrate (UNMEASURED setup): stamp the deterministic basin — a solid floor + an air-clear of the
            //     band above it — THROTTLED across frames, then settle. Throttling is what keeps the resulting
            //     remesh/relight from avalanching the allocators (the earlier single-frame drain → OOM). The floor's
            //     one-time skylight recalc is absorbed here, before the baseline, so it never enters the attribution. ---
            const int regionVox = REGION_CHUNKS * VoxelData.ChunkWidth;
            List<VoxelMod> mods = new List<VoxelMod>(regionVox * regionVox * (FluidBenchmarkScenarios.SkyWaterTopY - FluidBenchmarkScenarios.SkyFloorY + 1));
            FluidBenchmarkScenarios.EmitFloodSubstrate(mods, regionMin, REGION_CHUNKS);
            Debug.Log($"[FluidStress] Stamping substrate ({mods.Count} mods, {STAMP_BATCH}/frame) and settling...");
            yield return EnqueueThrottled(mods, null);
            yield return WaitForPipelineToSettle();

            FluidStressMetricsCollector collector = new FluidStressMetricsCollector();

            // --- Baseline: settled basin, before the water cap is released. ---
            Debug.Log("[FluidStress] Recording baseline...");
            collector.BeginPhase("Baseline (settled)");
            yield return RecordFor(collector, BASELINE_SECONDS);
            collector.EndPhase();

            // --- Flood: release the suspended water cap (THROTTLED) and record the cross-chunk cascade. ---
            mods.Clear();
            FluidBenchmarkScenarios.EmitFloodTrigger(mods, regionMin, REGION_CHUNKS);
            Debug.Log($"[FluidStress] Releasing flood ({mods.Count} water mods, {STAMP_BATCH}/frame) and recording...");

            collector.BeginPhase($"Flood ({REGION_CHUNKS * REGION_CHUNKS}-chunk cascade)");
            yield return EnqueueThrottled(mods, collector);
            yield return RecordFor(collector, FLOOD_SECONDS);
            collector.EndPhase();

            // --- Report + results screen ---
            WorldFrameProfiler.Enabled = false;
            _profilerEnabled = false;

            BenchmarkReportResult result = FluidStressReportGenerator.GenerateAndWrite(collector, REGION_CHUNKS);
            Debug.Log($"[FluidStress] Complete. Report written to: {result.LogFilePath ?? "(disk write failed — see console log above)"}");

            // Show the shared post-run results overlay (report + Open Log Folder / Return to Main Menu), and free the
            // cursor so its buttons are clickable. The Return button reverts the runtime mode and loads the menu scene.
            BenchmarkResultsScreen resultsScreen = BenchmarkUIBuilder.CreateResultsScreen("Fluid Stress Pass Complete");
            resultsScreen.Show(result.ReportRichText, result.LogFilePath);
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        /// <summary>Records one phase for <paramref name="seconds"/>, sampling every frame after World.Update has run.</summary>
        private static IEnumerator RecordFor(FluidStressMetricsCollector collector, float seconds)
        {
            float elapsed = 0f;
            while (elapsed < seconds)
            {
                // Yield FIRST so the continuation runs after this frame's World.Update (and its WorldFrameProfiler
                // publish), then sample the now-current per-frame values.
                yield return null;
                collector.SampleFrame();
                elapsed += Time.unscaledDeltaTime;
            }
        }

        /// <summary>
        /// Enqueues <paramref name="mods"/> in <see cref="STAMP_BATCH"/>-sized batches, one batch per frame.
        /// Throttling the stamp is the core safety mechanism: it lets the pipeline's per-frame mesh/light throttles
        /// absorb the remesh/relight cascade instead of dirtying every region chunk in a single drain (which
        /// avalanches the allocators → OOM). When <paramref name="collector"/> is non-null each batch frame is also
        /// sampled (the measured flood); pass <c>null</c> for the unmeasured substrate stamp.
        /// </summary>
        private static IEnumerator EnqueueThrottled(List<VoxelMod> mods, FluidStressMetricsCollector collector)
        {
            for (int i = 0; i < mods.Count; i += STAMP_BATCH)
            {
                int end = Mathf.Min(i + STAMP_BATCH, mods.Count);
                for (int j = i; j < end; j++)
                    World.Instance.EnqueueVoxelModification(mods[j]);

                yield return null;
                collector?.SampleFrame();
            }
        }

        /// <summary>Waits for all generation/lighting/mesh jobs to drain, then a fixed flush window.</summary>
        private static IEnumerator WaitForPipelineToSettle()
        {
            // Give the enqueued mods a frame to be applied + scheduled before polling.
            yield return null;

            // Wait for jobs to drain, but cap the wait: if the pipeline never goes fully idle (e.g. a lingering
            // lighting edge-check), proceed anyway rather than hang the run forever.
            int guard = 0;
            while (World.Instance.JobManager.HasActiveJobs && guard < SETTLE_TIMEOUT_FRAMES)
            {
                guard++;
                yield return null;
            }

            if (guard >= SETTLE_TIMEOUT_FRAMES)
                Debug.LogWarning($"[FluidStress] Pipeline did not fully settle within {SETTLE_TIMEOUT_FRAMES} frames; proceeding anyway.");

            for (int i = 0; i < SETTLE_FRAMES; i++)
                yield return null;
        }

        private void OnDestroy()
        {
            if (_profilerEnabled)
            {
                WorldFrameProfiler.Enabled = false;
                _profilerEnabled = false;
            }

            if (_frameRateOverridden)
            {
                QualitySettings.vSyncCount = _savedVSyncCount;
                Application.targetFrameRate = _savedTargetFrameRate;
                _frameRateOverridden = false;
            }
        }
    }
}
