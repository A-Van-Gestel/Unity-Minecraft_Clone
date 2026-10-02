using System;
using System.Text;
using Benchmarks;
using Data;
using Data.JobData;
using Data.NativeData;
using Helpers;
using Unity.Scripting.LifecycleManagement;
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

        /// <summary>Max concurrently in-flight generation jobs before scheduling pauses (P-4 §3.1).</summary>
        public readonly int MaxInFlightGenerationJobs;

        /// <summary>Per-frame lighting-job budget (maps to <c>Settings.maxLightJobsPerFrame</c>).</summary>
        public readonly int MaxLightJobsPerFrame;

        /// <summary>Per-frame mesh-rebuild budget (maps to <c>Settings.maxMeshRebuildsPerFrame</c>).</summary>
        public readonly int MaxMeshRebuildsPerFrame;

        /// <summary>Initializes a resolved budget set.</summary>
        public CalibrationResult(int jobArrayPoolRetention, int maxInFlightMeshJobs, int maxInFlightGenerationJobs, int maxLightJobsPerFrame, int maxMeshRebuildsPerFrame)
        {
            JobArrayPoolRetention = jobArrayPoolRetention;
            MaxInFlightMeshJobs = maxInFlightMeshJobs;
            MaxInFlightGenerationJobs = maxInFlightGenerationJobs;
            MaxLightJobsPerFrame = maxLightJobsPerFrame;
            MaxMeshRebuildsPerFrame = maxMeshRebuildsPerFrame;
        }

        /// <inheritdoc/>
        public override string ToString() =>
            $"retention={JobArrayPoolRetention}, inFlightMesh={MaxInFlightMeshJobs}, " +
            $"inFlightGen={MaxInFlightGenerationJobs}, " +
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
        public const int CalibrationVersion = 2;

        // --- Memory tuning: retention = clamp(systemMemoryMb / MB_PER_RETAINED_BUFFER, floor, ceiling). ---
        // 16 GB -> 512 (today's constant), 8 GB -> 256, 4 GB -> 128, <3 GB -> floor.
        private const int POOL_RETENTION_CEILING = 512; // desktop ceiling == today's MAX_RETAINED_PER_TYPE
        private const int POOL_RETENTION_FLOOR = 96;
        private const int MB_PER_RETAINED_BUFFER = 32;

        // --- In-flight mesh cap: scales linearly with retention (today: 20 at retention 512). ---
        private const int INFLIGHT_MESH_CEILING = 20; // today's hardcoded literal in World.Update
        private const int INFLIGHT_MESH_FLOOR = 4;

        // --- In-flight generation cap (P-4 §3.1): scales linearly with retention, same memory-cap
        // taxonomy as the mesh cap. No historical literal to reproduce — generation was previously
        // uncapped — so the desktop ceiling is chosen to comfortably keep every job worker fed on a
        // typical desktop (~2× worker count) while RAM-scaling down on constrained devices. Generation
        // job buffers (~230 KB: Map + HeightMap + queues, WG-1) are lighter than mesh/light buffers,
        // so this ceiling costs well under the mesh cap's memory at the same count. ---
        private const int INFLIGHT_GEN_CEILING = 32;
        private const int INFLIGHT_GEN_FLOOR = 8;

        // --- Throughput tuning: reference-anchored scaling. ---
        // budget = clamp(round(DefaultBudget * ReferenceMs / anchorMs), floor, ceiling): a device at the reference
        // anchor gets the default exactly; slower devices scale down, faster ones up to the field's own [Range]
        // max (never a new restriction).

        /// <summary>The mesh budget a device at the reference anchor resolves to (today's
        /// <c>Settings.maxMeshRebuildsPerFrame</c> default).</summary>
        public const int DefaultMeshBudget = 10;

        /// <summary>The light budget a device at the reference anchor resolves to (today's
        /// <c>Settings.maxLightJobsPerFrame</c> default).</summary>
        public const int DefaultLightBudget = 32;

        /// <summary>
        /// The reference machine's mesh-leg anchor (ms) — the model's one hand-tuned knob. Measured in an IL2CPP
        /// Master player on an i9-9900K (16 logical cores) / 64 GB / RTX 4070 Ti: the median of 75
        /// <c>BASELINE_CALIBRATION</c> anchors over 5 cold launches (2026-10-02). Valid only for the probe statistic
        /// and job code it was measured with: a job that gets heavier on every device lowers every budget through
        /// a stale reference, so re-anchor after any change to either job's cost.
        /// </summary>
        public const double ReferenceMeshMs = 0.855;

        /// <summary>The reference machine's lighting-leg anchor (ms); same capture and validity as
        /// <see cref="ReferenceMeshMs"/>.</summary>
        public const double ReferenceLightMs = 1.310;

        private const int MESH_BUDGET_FLOOR = 2;
        private const int MESH_BUDGET_CEILING = 50; // Settings.maxMeshRebuildsPerFrame [Range(1,50)]
        private const int LIGHT_BUDGET_FLOOR = 4;
        private const int LIGHT_BUDGET_CEILING = 128; // Settings.maxLightJobsPerFrame [Range(1,128)]

        [NoAutoStaticsCleanup] // reset in ResetStatics
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
            int inFlightGen = ResolveInFlightGeneration(retention);

            StartupCalibrationProbe.ProbeResult probe = StartupCalibrationProbe.Measure(jobData, fluidTemplates);
            double meshMs = probe.MeshMs;
            double lightMs = probe.LightMs;

            // Probe anchors — the input to the reference-anchored model, beside the references they are
            // compared against (player-build anchored; see OM1_DEVICE_CALIBRATION.md §3.2).
            Debug.Log($"[DeviceCalibration] Probe anchors: mesh={meshMs:F3} ms, light={lightMs:F3} ms " +
                      $"(reference: mesh={ReferenceMeshMs:F3} ms, light={ReferenceLightMs:F3} ms).");

            int meshBudget = MapMeshBudget(meshMs);
            int lightBudget = MapLightBudget(lightMs);

            CalibrationResult result = new CalibrationResult(retention, inFlightMesh, inFlightGen, lightBudget, meshBudget);

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

        private static int ResolveInFlightGeneration(int retention) =>
            Mathf.Clamp(
                Mathf.RoundToInt(INFLIGHT_GEN_CEILING * (retention / (float)POOL_RETENTION_CEILING)),
                INFLIGHT_GEN_FLOOR, INFLIGHT_GEN_CEILING);

        /// <summary>Maps a mesh-leg anchor to the per-frame mesh-rebuild budget.</summary>
        /// <param name="anchorMs">The probe's mesh-leg anchor in milliseconds.</param>
        /// <returns>The budget, clamped to the mesh floor and the field's range maximum.</returns>
        public static int MapMeshBudget(double anchorMs) =>
            MapThroughputBudget(anchorMs, DefaultMeshBudget, ReferenceMeshMs, MESH_BUDGET_FLOOR, MESH_BUDGET_CEILING);

        /// <summary>Maps a lighting-leg anchor to the per-frame lighting-job budget.</summary>
        /// <param name="anchorMs">The probe's lighting-leg anchor in milliseconds.</param>
        /// <returns>The budget, clamped to the light floor and the field's range maximum.</returns>
        public static int MapLightBudget(double anchorMs) =>
            MapThroughputBudget(anchorMs, DefaultLightBudget, ReferenceLightMs, LIGHT_BUDGET_FLOOR, LIGHT_BUDGET_CEILING);

        /// <summary>
        /// The reference-anchored throughput model: <c>clamp(round(defaultBudget × referenceMs / anchorMs))</c>,
        /// with a non-positive anchor (immeasurably fast) mapped to <paramref name="ceiling"/>.
        /// </summary>
        /// <param name="anchorMs">The device's probe anchor in milliseconds.</param>
        /// <param name="defaultBudget">The budget a device at the reference resolves to.</param>
        /// <param name="referenceMs">The reference machine's anchor in milliseconds.</param>
        /// <param name="floor">The lowest budget returned.</param>
        /// <param name="ceiling">The highest budget returned.</param>
        /// <returns>The clamped budget.</returns>
        public static int MapThroughputBudget(double anchorMs, int defaultBudget, double referenceMs, int floor, int ceiling)
        {
            if (anchorMs <= 0) return ceiling; // immeasurably fast — give it the field's max

            // Clamp before the int cast: a tiny anchor's ratio exceeds int range, and the cast would wrap it
            // negative and land it on the floor instead of the ceiling.
            double budget = Math.Round(defaultBudget * (referenceMs / anchorMs));
            return (int)Math.Min(Math.Max(budget, floor), ceiling);
        }

        /// <summary>
        /// Combines a re-probe with the budgets already stored. On a calibration-version upgrade of a file
        /// that was calibrated before (<paramref name="storedVersion"/> ≥ 1), each field keeps the higher of
        /// its stored and probed value: a stored value above the probe may be a hand-tuned budget, which the
        /// re-probe cannot tell apart from an earlier calibration. A fresh or never-calibrated file
        /// (version 0) and an explicit recalibration at the current version take the probe as-is.
        /// </summary>
        /// <param name="storedVersion">The settings file's <c>calibrationVersion</c> before this probe.</param>
        /// <param name="currentVersion">The formula version being applied (normally <see cref="CalibrationVersion"/>).</param>
        /// <param name="stored">The budgets currently in the settings file.</param>
        /// <param name="probed">The budgets this probe resolved.</param>
        /// <returns>The budgets to write.</returns>
        public static CalibrationResult MergeForRecalibration(int storedVersion, int currentVersion,
            CalibrationResult stored, CalibrationResult probed)
        {
            bool upgradeOfCalibratedFile = storedVersion >= 1 && storedVersion < currentVersion;
            if (!upgradeOfCalibratedFile) return probed;

            return new CalibrationResult(
                Math.Max(stored.JobArrayPoolRetention, probed.JobArrayPoolRetention),
                Math.Max(stored.MaxInFlightMeshJobs, probed.MaxInFlightMeshJobs),
                Math.Max(stored.MaxInFlightGenerationJobs, probed.MaxInFlightGenerationJobs),
                Math.Max(stored.MaxLightJobsPerFrame, probed.MaxLightJobsPerFrame),
                Math.Max(stored.MaxMeshRebuildsPerFrame, probed.MaxMeshRebuildsPerFrame));
        }

        /// <summary>
        /// Writes a self-contained baseline-capture record (device specs, per-leg timing distributions,
        /// reference constants, and the resolved budgets) to a timestamped file under
        /// <c>persistentDataPath/Benchmarks/</c>. Used only in precision-capture mode to harvest a
        /// re-anchor / multi-baseline data point off a device — notably Android, where the player log is
        /// awkward to read; the file is reachable via <c>adb pull</c> with no storage permissions. The
        /// anchor ms here, paired with a playtested known-good budget, is one OM1 §3.3 baseline row.
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
            // Shared system/build description — crucially records Backend (IL2CPP/Mono) and Editor-vs-Player,
            // the config that determines whether a capture is comparable to the Reference*Ms anchors (§3.2).
            sb.Append(BenchmarkEnvironment.DescribeSystem());
            sb.AppendLine();
            sb.AppendLine("=== Device (extra) ===");
            sb.AppendLine($"Model:          {SystemInfo.deviceModel}");
            sb.AppendLine($"GPU:            {SystemInfo.graphicsDeviceName}");
            sb.AppendLine();
            sb.AppendLine("-- Probe anchors (median of the per-repetition min-of-batch-medians) --");
            sb.AppendLine($"Mesh anchor:   {probe.MeshMs:F3} ms over {probe.MeshRepetitions.Length} repetitions");
            sb.AppendLine($"Light anchor:  {probe.LightMs:F3} ms over {probe.LightRepetitions.Length} repetitions");
            sb.AppendLine();
            sb.AppendLine("-- Per-repetition distributions (repetition 1 is what a shipping launch measures) --");
            for (int r = 0; r < probe.MeshRepetitions.Length; r++)
            {
                sb.AppendLine($"#{r + 1,-2} Mesh:  {probe.MeshRepetitions[r]}");
                sb.AppendLine($"    Light: {probe.LightRepetitions[r]}");
            }

            sb.AppendLine();
            sb.AppendLine("-- Reference constants used (player-build anchored; see OM1 §3.2) --");
            sb.AppendLine($"ReferenceMeshMs:  {ReferenceMeshMs:F3} ms  (default budget {DefaultMeshBudget})");
            sb.AppendLine($"ReferenceLightMs: {ReferenceLightMs:F3} ms  (default budget {DefaultLightBudget})");
            sb.AppendLine();
            sb.AppendLine("-- Resolved budgets (this device) --");
            sb.AppendLine($"  {result}");
            sb.AppendLine();
            sb.AppendLine("To re-anchor Reference*Ms: use the mesh/light anchors above (same statistic as shipping).");
            sb.AppendLine("To add an OM1 §3.3 baseline row: pair those anchors with the known-good budget");
            sb.AppendLine("found by playtesting on this device.");

            // WriteReportToDisk routes to public Downloads on Android (MediaStore) and the Benchmarks
            // folder on desktop/editor, so the capture is retrievable on every platform — see OM1 §5.1.
            string location = BenchmarkEnvironment.WriteReportToDisk(sb.ToString(), "CalibrationBaseline");
            if (!string.IsNullOrEmpty(location))
                Debug.Log($"[DeviceCalibration] Baseline capture written to: {location}");
        }
    }
}
