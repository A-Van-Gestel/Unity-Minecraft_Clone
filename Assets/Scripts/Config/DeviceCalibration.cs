using System;
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
        // a 16-core / 64 GB desktop in-editor (Burst on); re-anchor from a player-build capture when tuning.
        private const int DEFAULT_MESH_BUDGET = 10; // today's Settings.maxMeshRebuildsPerFrame
        private const int DEFAULT_LIGHT_BUDGET = 32; // today's Settings.maxLightJobsPerFrame
        private const double REFERENCE_MESH_MS = 1.233;
        private const double REFERENCE_LIGHT_MS = 1.110;
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

            (double meshMs, double lightMs) = StartupCalibrationProbe.Measure(jobData, fluidTemplates);
            int meshBudget = MapThroughputBudget(meshMs, DEFAULT_MESH_BUDGET, REFERENCE_MESH_MS, MESH_BUDGET_FLOOR, MESH_BUDGET_CEILING);
            int lightBudget = MapThroughputBudget(lightMs, DEFAULT_LIGHT_BUDGET, REFERENCE_LIGHT_MS, LIGHT_BUDGET_FLOOR, LIGHT_BUDGET_CEILING);

            return new CalibrationResult(retention, inFlightMesh, lightBudget, meshBudget);
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
    }
}
