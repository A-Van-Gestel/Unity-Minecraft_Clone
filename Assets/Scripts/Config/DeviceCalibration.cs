using System;
using System.Text;
using Benchmarks;
using Data;
using Data.JobData;
using Data.NativeData;
using Helpers;
using UnityEngine;

namespace Config
{
    /// <summary>
    /// The resolved device-scaled budgets (OM-1). Memory caps are derived from system RAM; throughput
    /// budgets are derived from the <see cref="StartupCalibrationProbe"/> micro-benchmark. Written into
    /// the settings file once on first launch and fully user-editable thereafter.
    /// </summary>
    public readonly struct CalibrationResult
    {
        /// <summary>Max buffers retained per type in <c>ChunkJobArrayPool</c> (native memory ceiling).</summary>
        public readonly int JobArrayPoolRetention;

        /// <summary>Max concurrently in-flight mesh jobs before scheduling pauses.</summary>
        public readonly int MaxInFlightMeshJobs;

        /// <summary>Per-frame lighting-job budget (maps to <c>Settings.maxLightJobsPerFrame</c>).</summary>
        public readonly int MaxLightJobsPerFrame;

        /// <summary>Per-frame mesh-rebuild budget (maps to <c>Settings.maxMeshRebuildsPerFrame</c>).</summary>
        public readonly int MaxMeshRebuildsPerFrame;

        /// <summary>Initializes a resolved budget set.</summary>
        public CalibrationResult(int jobArrayPoolRetention, int maxInFlightMeshJobs, int maxLightJobsPerFrame, int maxMeshRebuildsPerFrame)
        {
            JobArrayPoolRetention = jobArrayPoolRetention;
            MaxInFlightMeshJobs = maxInFlightMeshJobs;
            MaxLightJobsPerFrame = maxLightJobsPerFrame;
            MaxMeshRebuildsPerFrame = maxMeshRebuildsPerFrame;
        }

        /// <inheritdoc/>
        public override string ToString() =>
            $"retention={JobArrayPoolRetention}, inFlightMesh={MaxInFlightMeshJobs}, " +
            $"lightJobs/frame={MaxLightJobsPerFrame}, meshRebuilds/frame={MaxMeshRebuildsPerFrame}";
    }

    /// <summary>
    /// Resolves OM-1's device-scaled budgets once, from two signals that must not be conflated:
    /// <list type="bullet">
    /// <item><b>Memory caps</b> (pool retention, in-flight job caps) — a continuous function of
    /// <see cref="SystemInfo.systemMemorySize"/>. RAM is the direct OOM signal; it is never benchmarked.</item>
    /// <item><b>Throughput budgets</b> (per-frame mesh/light job counts) — derived from the
    /// <see cref="StartupCalibrationProbe"/>, which times how fast this device actually meshes/lights.</item>
    /// </list>
    /// On a high-RAM desktop the memory caps reproduce today's constants exactly (retention 512,
    /// in-flight 20), so desktop behavior is unchanged. No budget caps a user-facing range maximum — the
    /// values seed the initial settings only. See <c>Documentation/Design/OM1_DEVICE_CALIBRATION.md</c>.
    /// </summary>
    public static class DeviceCalibration
    {
        /// <summary>
        /// Bumped when the calibration formula changes; a persisted settings file with an older version
        /// is re-calibrated on next launch (without discarding unrelated user edits).
        /// </summary>
        public const int CalibrationVersion = 1;

        // --- Memory tuning: retention = clamp(systemMemoryMb / MB_PER_RETAINED_BUFFER, floor, ceiling). ---
        // 16 GB -> 512 (today's constant), 8 GB -> 256, 4 GB -> 128, <3 GB -> floor.
        private const int POOL_RETENTION_CEILING = 512; // desktop ceiling == today's MAX_RETAINED_PER_TYPE
        private const int POOL_RETENTION_FLOOR = 96;
        private const int MB_PER_RETAINED_BUFFER = 32;

        // --- In-flight mesh cap: scales linearly with retention (today: 20 at retention 512). ---
        private const int INFLIGHT_MESH_CEILING = 20; // today's hardcoded literal in World.Update
        private const int INFLIGHT_MESH_FLOOR = 4;

        // --- Throughput tuning: reference-anchored scaling. ---
        // budget = clamp(round(DEFAULT_BUDGET * REFERENCE_MS / medianJobMs), floor, ceiling).
        // A device whose per-chunk job time equals the reference reproduces today's hand-tuned default
        // budget exactly; slower devices scale down, faster devices up to the field's own [Range] max
        // (never a new restriction). REFERENCE_*_MS are the one intentional hand-tuned knob — anchored on
        // a player build (IL2CPP) on an i9-9900K (16 logical cores) / 64 GB / RTX 4070 Ti, 99-sample median
        // (BASELINE_CALIBRATION capture, std ≈ 0.03 ms). On that box this reproduces today's 10 / 32.
        private const int DEFAULT_MESH_BUDGET = 10; // today's Settings.maxMeshRebuildsPerFrame
        private const int DEFAULT_LIGHT_BUDGET = 32; // today's Settings.maxLightJobsPerFrame
        private const double REFERENCE_MESH_MS = 0.952;
        private const double REFERENCE_LIGHT_MS = 0.604;
        private const int MESH_BUDGET_FLOOR = 2;
        private const int MESH_BUDGET_CEILING = 50; // Settings.maxMeshRebuildsPerFrame [Range(1,50)]
        private const int LIGHT_BUDGET_FLOOR = 4;
        private const int LIGHT_BUDGET_CEILING = 128; // Settings.maxLightJobsPerFrame [Range(1,128)]

        private static CalibrationResult? s_override;

        /// <summary>Forces a fixed result for testing (e.g. simulating a low-spec device). Cleared on domain reload.</summary>
        /// <param name="result">The result every <c>Resolve</c> call returns until cleared.</param>
        public static void OverrideForTesting(CalibrationResult result) => s_override = result;

        /// <summary>Clears any testing override so <c>Resolve</c> measures the real device again.</summary>
        public static void ClearOverride() => s_override = null;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => s_override = null;

        /// <summary>
        /// Resolves the budgets using injected job data (caller owns it). Use this overload when job data
        /// already exists (e.g. a live <c>World</c>); otherwise use the parameterless overload.
        /// </summary>
        /// <param name="jobData">Block-type / custom-mesh native job data the probe schedules against.</param>
        /// <param name="fluidTemplates">Water/lava vertex templates the mesh probe needs.</param>
        /// <returns>The resolved device budgets.</returns>
        public static CalibrationResult Resolve(JobDataManager jobData, FluidVertexTemplatesNativeData fluidTemplates)
        {
            if (s_override.HasValue) return s_override.Value;

            int retention = ResolvePoolRetention(SystemInfo.systemMemorySize);
            int inFlightMesh = ResolveInFlightMesh(retention);

            StartupCalibrationProbe.ProbeResult probe = StartupCalibrationProbe.Measure(jobData, fluidTemplates);
            double meshMs = probe.MeshMs;
            double lightMs = probe.LightMs;

            // Raw probe times — the input to the reference-anchored model. Logged so the REFERENCE_*_MS
            // constants can be re-anchored from a real player-build capture (they are currently editor-
            // measured; the player build runs faster). See OM1_DEVICE_CALIBRATION.md §3.2.
            Debug.Log($"[DeviceCalibration] Probe raw times: mesh={meshMs:F3} ms, light={lightMs:F3} ms " +
                      $"(reference: mesh={REFERENCE_MESH_MS:F3} ms, light={REFERENCE_LIGHT_MS:F3} ms).");

            int meshBudget = MapThroughputBudget(meshMs, DEFAULT_MESH_BUDGET, REFERENCE_MESH_MS, MESH_BUDGET_FLOOR, MESH_BUDGET_CEILING);
            int lightBudget = MapThroughputBudget(lightMs, DEFAULT_LIGHT_BUDGET, REFERENCE_LIGHT_MS, LIGHT_BUDGET_FLOOR, LIGHT_BUDGET_CEILING);

            CalibrationResult result = new CalibrationResult(retention, inFlightMesh, lightBudget, meshBudget);

            // In precision-capture mode, persist a self-contained baseline record to disk so it can be
            // harvested off devices whose logs are awkward to read (e.g. Android). See OM1 §3.3 / §5.
            if (StartupCalibrationProbe.BaselineCalibrationEnabled)
                WriteBaselineReport(probe, result);

            return result;
        }

        /// <summary>
        /// Resolves the budgets without a live <c>World</c> — loads the shared block database and builds
        /// temporary job data (disposed before returning). Intended for the first-launch calibration at
        /// the Main Menu.
        /// </summary>
        /// <returns>The resolved device budgets.</returns>
        public static CalibrationResult Resolve()
        {
            if (s_override.HasValue) return s_override.Value;

            BlockDatabase database = ResourceLoader.LoadBlockDatabase();
            if (!database)
            {
                // Fail loudly but cleanly rather than NRE deep inside the factory. The caller treats this
                // as "calibration could not run" and retries next launch (see SettingsManager.ApplyCalibration).
                throw new InvalidOperationException(
                    "OM-1 calibration cannot run: BlockDatabase failed to load from Resources.");
            }

            GlobalJobData jobData = JobDataManagerFactory.Create(database);
            try
            {
                return Resolve(jobData.JobDataManager, jobData.FluidVertexTemplates);
            }
            finally
            {
                jobData.JobDataManager.Dispose();
                jobData.FluidVertexTemplates.Dispose();
            }
        }

        private static int ResolvePoolRetention(int systemMemoryMb) =>
            Mathf.Clamp(systemMemoryMb / MB_PER_RETAINED_BUFFER, POOL_RETENTION_FLOOR, POOL_RETENTION_CEILING);

        private static int ResolveInFlightMesh(int retention) =>
            Mathf.Clamp(
                Mathf.RoundToInt(INFLIGHT_MESH_CEILING * (retention / (float)POOL_RETENTION_CEILING)),
                INFLIGHT_MESH_FLOOR, INFLIGHT_MESH_CEILING);

        private static int MapThroughputBudget(double medianMs, int defaultBudget, double referenceMs, int floor, int ceiling)
        {
            if (medianMs <= 0) return ceiling; // immeasurably fast — give it the field's max
            int budget = (int)Math.Round(defaultBudget * (referenceMs / medianMs));
            return Mathf.Clamp(budget, floor, ceiling);
        }

        /// <summary>
        /// Writes a self-contained baseline-capture record (device specs, per-leg timing distributions,
        /// reference constants, and the resolved budgets) to a timestamped file under
        /// <c>persistentDataPath/Benchmarks/</c>. Used only in precision-capture mode to harvest a
        /// re-anchor / multi-baseline data point off a device — notably Android, where the player log is
        /// awkward to read; the file is reachable via <c>adb pull</c> with no storage permissions. The
        /// median ms here, paired with a playtested known-good budget, is one OM1 §3.3 baseline row.
        /// </summary>
        /// <param name="probe">The probe's per-leg timing distributions.</param>
        /// <param name="result">The budgets this device resolved to.</param>
        private static void WriteBaselineReport(StartupCalibrationProbe.ProbeResult probe, CalibrationResult result)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("=== OM-1 Device Calibration — Baseline Capture ===");
            sb.AppendLine($"Timestamp:        {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"CalibrationVer:   {CalibrationVersion}");
            sb.AppendLine();
            sb.AppendLine("-- Device --");
            sb.AppendLine($"Model:            {SystemInfo.deviceModel}");
            sb.AppendLine($"OS:               {SystemInfo.operatingSystem}");
            sb.AppendLine($"CPU:              {SystemInfo.processorType} ({SystemInfo.processorCount} logical cores)");
            sb.AppendLine($"System RAM:       {SystemInfo.systemMemorySize} MB");
            sb.AppendLine($"GPU:              {SystemInfo.graphicsDeviceName}");
            sb.AppendLine();
            sb.AppendLine("-- Probe distribution (median is the throughput anchor) --");
            sb.AppendLine($"Mesh:   {probe.Mesh}");
            sb.AppendLine($"Light:  {probe.Light}");
            sb.AppendLine();
            sb.AppendLine("-- Reference constants used (editor-anchored; see OM1 §3.2) --");
            sb.AppendLine($"REFERENCE_MESH_MS:  {REFERENCE_MESH_MS:F3} ms  (default budget {DEFAULT_MESH_BUDGET})");
            sb.AppendLine($"REFERENCE_LIGHT_MS: {REFERENCE_LIGHT_MS:F3} ms  (default budget {DEFAULT_LIGHT_BUDGET})");
            sb.AppendLine();
            sb.AppendLine("-- Resolved budgets (this device) --");
            sb.AppendLine($"  {result}");
            sb.AppendLine();
            sb.AppendLine("To re-anchor REFERENCE_*_MS: use the mesh/light medians above.");
            sb.AppendLine("To add an OM1 §3.3 baseline row: pair those medians with the known-good budget");
            sb.AppendLine("found by playtesting on this device.");

            string report = sb.ToString();
            string fileName = $"CalibrationBaseline_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log";

            // On Android, persistentDataPath is app-private (invisible to file managers, and even
            // file:// opens trip a permission error), so target the public Downloads collection instead
            // — retrievable from the Files / Downloads app. Desktop keeps the canonical Benchmarks path.
            string location = null;
            if (Application.platform == RuntimePlatform.Android)
                location = TryWriteToAndroidDownloads(fileName, report);

            // Desktop, editor, or Android-MediaStore-failure fallback (still adb-pullable).
            if (string.IsNullOrEmpty(location))
                location = BenchmarkEnvironment.WriteReportToDisk(report, "CalibrationBaseline");

            if (!string.IsNullOrEmpty(location))
                Debug.Log($"[DeviceCalibration] Baseline capture written to: {location}");
        }

        /// <summary>
        /// Writes <paramref name="content"/> into the device's public <c>Downloads</c> collection via
        /// MediaStore (Android 10+ / API 29+, no storage permission required), so the capture is reachable
        /// from the Files / Downloads app — unlike <c>persistentDataPath</c>, which is app-private. The JNI
        /// body compiles only under the Android target (<c>#if UNITY_ANDROID</c>); on every other platform
        /// it is a no-op returning <c>null</c> so the caller falls back to <c>persistentDataPath</c>.
        /// </summary>
        /// <param name="fileName">Display name for the created file (e.g. <c>CalibrationBaseline_*.log</c>).</param>
        /// <param name="content">UTF-8 text to write.</param>
        /// <returns>The public relative path on success, or <c>null</c> on any failure (caller falls back).</returns>
        private static string TryWriteToAndroidDownloads(string fileName, string content)
        {
#if UNITY_ANDROID
            try
            {
                // Group captures under Downloads/<product> so they are easy to find and clean up.
                string relativeDir = "Download/" + Application.productName;

                using AndroidJavaClass player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using AndroidJavaObject activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                using AndroidJavaObject resolver = activity.Call<AndroidJavaObject>("getContentResolver");

                // Column-name string literals are the stable MediaStore.MediaColumns values, used directly
                // to avoid extra JNI static lookups: DISPLAY_NAME / MIME_TYPE / RELATIVE_PATH.
                using AndroidJavaObject values = new AndroidJavaObject("android.content.ContentValues");
                values.Call("put", "_display_name", fileName);
                values.Call("put", "mime_type", "text/plain");
                values.Call("put", "relative_path", relativeDir);

                using AndroidJavaClass downloads = new AndroidJavaClass("android.provider.MediaStore$Downloads");
                using AndroidJavaObject collection = downloads.GetStatic<AndroidJavaObject>("EXTERNAL_CONTENT_URI");

                using AndroidJavaObject itemUri = resolver.Call<AndroidJavaObject>("insert", collection, values);
                if (itemUri == null) return null;

                using AndroidJavaObject stream = resolver.Call<AndroidJavaObject>("openOutputStream", itemUri);
                if (stream == null) return null;

                byte[] bytes = Encoding.UTF8.GetBytes(content);
                stream.Call("write", bytes);
                stream.Call("flush");
                stream.Call("close");

                return relativeDir + "/" + fileName;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DeviceCalibration] MediaStore write to Downloads failed ({e.Message}); " +
                                 "falling back to persistentDataPath.");
                return null;
            }
#else
            // Non-Android targets never reach the Android branch in WriteBaselineReport; stub for compilation.
            _ = fileName;
            _ = content;
            return null;
#endif
        }
    }
}
