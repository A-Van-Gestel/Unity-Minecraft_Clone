using System;
using System.Collections;
using System.Text;
using Data;
using Data.Enums;
using Diagnostics;
using Launch;
using Serialization;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Benchmarks
{
    /// <summary>
    /// Drives a <see cref="RuntimeMode.StartupProbe"/> run (<c>-mc-run startup-new</c> / <c>startup-existing</c>): waits
    /// while the player stands at spawn until the world's <see cref="StartupTimeline"/> has its outcome, then writes a
    /// <c>StartupRun</c> report and ends the run through <see cref="LaunchSession.TryQuitAfterRun"/>. The run succeeds only
    /// when every mark was reached.
    /// </summary>
    /// <remarks>
    /// Raises the monitor to the Systems tier for the run, so the disk counters can say how many chunks the launch
    /// loaded and how many it generated, and the hitch records name their slots. The tier is measured as costing
    /// nothing attributable at frame level (PM-3/PM-4 Master A/Bs).
    /// </remarks>
    public class StartupProbeController : MonoBehaviour
    {
        /// <summary>
        /// Bound on the whole run: the startup coroutine's own limits plus the timeline's
        /// <see cref="StartupTimeline.TimeoutSeconds"/>, with margin.
        /// </summary>
        private const float RUN_TIMEOUT_SECONDS = 600f;

        private const string REPORT_PREFIX = "StartupRun";
        private const string MAIN_MENU_SCENE = "Scenes/MainMenu";

        private long _loadHitsAtStart;
        private long _loadMissesAtStart;
        private bool _finished;

        // Owned by this run rather than inferred from the mode: the no-quit path switches the mode back before the
        // scene unloads, and the floor must still be cleared then.
        private bool _raisedTierFloor;

        private void Awake()
        {
            if (WorldLaunchState.CurrentMode != RuntimeMode.StartupProbe)
            {
                Destroy(this);
                return;
            }

            PerfStore.TierFloor = PerfTier.Systems;
            _raisedTierFloor = true;
            _loadHitsAtStart = StorageIoStats.LoadHits;
            _loadMissesAtStart = StorageIoStats.LoadMisses;
        }

        private IEnumerator Start()
        {
            float start = Time.realtimeSinceStartup;
            while (World.Instance == null || World.Instance.StartupTimeline.IsPending)
            {
                if (Time.realtimeSinceStartup - start > RUN_TIMEOUT_SECONDS)
                {
                    Debug.LogError($"[StartupProbe] No startup outcome within {RUN_TIMEOUT_SECONDS} s.");
                    Finish(World.Instance);
                    yield break;
                }

                yield return null;
            }

            Finish(World.Instance);
        }

        private void OnDestroy()
        {
            if (!_raisedTierFloor) return;

            PerfStore.TierFloor = PerfTier.Basic;
            _raisedTierFloor = false;
        }

        /// <summary>Writes the report and ends the run; with no <c>-mc-quit</c>, saves the world and returns to the main menu.</summary>
        private void Finish(World world)
        {
            if (_finished) return;
            _finished = true;

            StartupTimeline timeline = world != null ? world.StartupTimeline : null;
            string report = BuildReport(world, timeline);
            string path = BenchmarkEnvironment.WriteReportToDisk(report, REPORT_PREFIX);
            Debug.Log(report);

            bool succeeded = path != null && timeline != null && timeline.Outcome == StartupOutcome.Stable;
            if (LaunchSession.TryQuitAfterRun(succeeded ? LaunchSession.ExitSuccess : LaunchSession.ExitRunFailed, path))
                return;

            // The pause menu's save-and-leave sequence, so a startup-existing run finds this world. Saved while the mode
            // still selects the probe's isolated save folder.
            if (world != null) world.SaveWorldData();
            WorldLaunchState.CurrentMode = RuntimeMode.Default;
            SceneManager.LoadScene(MAIN_MENU_SCENE, LoadSceneMode.Single);
        }

        private string BuildReport(World world, StartupTimeline timeline)
        {
            StringBuilder sb = new StringBuilder(capacity: 2048);
            sb.AppendLine("=== Startup Run ===");
            sb.AppendLine($"World:            {WorldLaunchState.WorldName} ({(WorldLaunchState.IsNewGame ? "new" : "existing")}), seed {WorldLaunchState.Seed}");
            sb.AppendLine($"Outcome:          {(timeline != null ? timeline.OutcomeDescription : "no world")}");
            if (timeline != null)
            {
                sb.AppendLine($"Anchor:           {(timeline.AnchoredOnSceneRequest ? "scene load request" : "Awake")}");
                for (int i = 0; i <= (int)StartupStage.Stable; i++)
                {
                    StartupStage stage = (StartupStage)i;
                    double ms = timeline.MillisecondsTo(stage);
                    sb.AppendLine($"  {stage,-10} {(double.IsNaN(ms) ? "not reached" : $"{ms:F0} ms")}");
                }

                // Invariant, so the report reads the same on every machine's locale.
                sb.Append(FormattableString.Invariant(
                    $"Settled window:   median {timeline.StableWindowMedianMs:F2} ms, worst {timeline.StableWindowWorstMs:F2} ms "));
                sb.Append(FormattableString.Invariant(
                    $"(rule: worst ≤ max({StartupTimeline.StableMedianFactor}× median, median + {StartupTimeline.StableSlackMs} ms) "));
                sb.AppendLine(FormattableString.Invariant(
                    $"over {StartupTimeline.StableWindowSeconds} s after {StartupTimeline.DrainSettleFrames} drained frames)"));
            }

            sb.AppendLine($"Chunks loaded:    {StorageIoStats.LoadHits - _loadHitsAtStart} from disk, " +
                          $"{StorageIoStats.LoadMisses - _loadMissesAtStart} not on disk (generated)");
            sb.AppendLine();

            sb.AppendLine("=== Configuration ===");
            if (world != null && world.settings != null)
            {
                Settings settings = world.settings;
                sb.AppendLine($"View distance:    {settings.viewDistance} (load distance {settings.LoadDistance}, initial load radius cap {settings.maxInitialLoadRadius})");
                sb.AppendLine($"Jobs per frame:   light {settings.maxLightJobsPerFrame}, mesh {settings.maxMeshRebuildsPerFrame}");
            }

            sb.AppendLine($"VSync count:      {QualitySettings.vSyncCount}, target frame rate {Application.targetFrameRate}");
            sb.AppendLine($"Monitor detail:   {PerfStore.Tier}");
            sb.AppendLine();
            sb.Append(BenchmarkEnvironment.DescribeSystem());
            return sb.ToString();
        }
    }
}
