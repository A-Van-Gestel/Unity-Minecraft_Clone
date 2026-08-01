using System.Text;
using Helpers;

namespace Benchmarks
{
    /// <summary>
    /// The pipeline tuning values a benchmark run actually used, captured once at run start (FP-6).
    /// <para>
    /// Every knob here determines one of the stop reasons the FP verdict is computed from, so a capture that
    /// omits them is not interpretable: a phase reporting <c>Quota</c> on 99 % of frames says nothing without
    /// the quota, and the §7.1 rule turns exactly those tallies into a regime. The FP-4 sweep made the cost
    /// concrete — the view distance had to be supplied by the operator out of band, and reading the wrong
    /// row of a sizing table produced a finding that was later retracted.
    /// </para>
    /// <para>
    /// Captured at run <i>start</i> rather than read at report time, for the same reason the trace-capacity
    /// estimate is: settings are user-editable mid-session, and the report must state what the run used.
    /// </para>
    /// </summary>
    public readonly struct PipelineSettingsSnapshot
    {
        #region Geometry

        /// <summary>Rendered chunk radius (`Settings.viewDistance`).</summary>
        public readonly int ViewDistance;

        /// <summary>Data-resident chunk radius — view distance plus the data buffer.</summary>
        public readonly int LoadDistance;

        #endregion

        #region Per-frame quotas — drive the Quota stop reason

        /// <summary>Lighting jobs schedulable per reference frame.</summary>
        public readonly int MaxLightJobsPerFrame;

        /// <summary>Mesh rebuilds schedulable per reference frame.</summary>
        public readonly int MaxMeshRebuildsPerFrame;

        /// <summary>Structure modifications applied per frame inside the generation pass.</summary>
        public readonly int MaxStructureModsPerFrame;

        #endregion

        #region In-flight caps — drive the InFlightCap stop reason

        /// <summary>Concurrent generation jobs allowed in flight.</summary>
        public readonly int MaxInFlightGenerationJobs;

        /// <summary>Concurrent lighting jobs allowed in flight.</summary>
        public readonly int MaxInFlightLightingJobs;

        /// <summary>Concurrent mesh jobs allowed in flight.</summary>
        public readonly int MaxInFlightMeshJobs;

        #endregion

        #region Time ceilings — drive the Ceiling stop reason

        /// <summary>Whether the P-4 §3.4 time budgets are active at all.</summary>
        public readonly bool TimeBudgetsEnabled;

        /// <summary>Whether ceilings scale proportionally with a voluntary FPS cap.</summary>
        public readonly bool ScaleCeilingsWithFpsCap;

        /// <summary>Generation-completion pass ceiling, milliseconds (≤ 0 disables it).</summary>
        public readonly float GenProcessBudgetMs;

        /// <summary>Lighting schedule pass ceiling, milliseconds (≤ 0 disables it).</summary>
        public readonly float LightScheduleBudgetMs;

        /// <summary>Mesh schedule pass ceiling, milliseconds (≤ 0 disables it).</summary>
        public readonly float MeshScheduleBudgetMs;

        /// <summary>Mesh apply/completion pass ceiling, milliseconds (≤ 0 disables it).</summary>
        public readonly float MeshApplyBudgetMs;

        #endregion

        #region Admission gate — withholds admissions entirely, outside the four instrumented passes

        /// <summary>Whether the P-4 §3.5 generation panic gate is active.</summary>
        public readonly bool PanicGateEnabled;

        /// <summary>Configured close threshold, as persisted — stated at the reference view distance.</summary>
        public readonly int PanicGateCloseThreshold;

        /// <summary>Configured reopen threshold, as persisted — stated at the reference view distance.</summary>
        public readonly int PanicGateReopenThreshold;

        /// <summary>Whether P-8's residency scaling is active for this run.</summary>
        public readonly bool ScalePanicGateWithResidency;

        /// <summary>
        /// The close threshold the gate is actually evaluated against, after P-8 scaling and sanitization.
        /// Equals <see cref="PanicGateCloseThreshold"/> at the reference view distance and with scaling off.
        /// </summary>
        public readonly int EffectiveCloseThreshold;

        /// <summary>The reopen threshold the gate is actually evaluated against, likewise derived.</summary>
        public readonly int EffectiveReopenThreshold;

        /// <summary>
        /// Whether the lighting engine is on. Load-bearing for the gate: its backlog signal is the lighting
        /// ready count, so with lighting off the gate never closes regardless of its thresholds.
        /// </summary>
        public readonly bool LightingEnabled;

        #endregion

        /// <summary>
        /// Side length of the resident load square in chunks, captured from <see cref="Settings.ResidentWidth"/>
        /// — which is also what the panic gate scales its thresholds by (P-8), so the report and the gate
        /// cannot disagree about how big the resident world is.
        /// </summary>
        public readonly int ResidentWidth;

        /// <summary>Chunks in the resident load square — <see cref="ResidentWidth"/> squared.</summary>
        public int ResidentChunks => ResidentWidth * ResidentWidth;

        /// <summary>
        /// The gate's <i>configured</i> close threshold as a percentage of the resident square. The FP-4
        /// sweep found this ratio — not the threshold itself — predicts whether the gate ever closes, because
        /// the threshold was absolute while the population it guards grows with the square of view distance.
        /// Kept on the configured value so it stays the same quantity FP-8 and FP-10 reasoned from; the
        /// post-P-8 figure is <see cref="EffectiveCloseThresholdPercentOfResident"/>.
        /// </summary>
        public double PanicGateCloseThresholdPercentOfResident =>
            ResidentChunks > 0 ? 100.0 * PanicGateCloseThreshold / ResidentChunks : 0.0;

        /// <summary>
        /// The effective close threshold as a percentage of the resident square — the ratio that actually
        /// governs this run. With P-8 scaling on it falls as <c>1 / width</c> rather than <c>1 / width²</c>,
        /// which is the whole of the change stated as one number.
        /// </summary>
        public double EffectiveCloseThresholdPercentOfResident =>
            ResidentChunks > 0 ? 100.0 * EffectiveCloseThreshold / ResidentChunks : 0.0;

        /// <summary>Initializes a snapshot from the values a run is about to use.</summary>
        /// <param name="settings">The settings instance the run reads.</param>
        public PipelineSettingsSnapshot(Settings settings)
        {
            ViewDistance = settings.viewDistance;
            LoadDistance = settings.LoadDistance;
            ResidentWidth = settings.ResidentWidth;

            MaxLightJobsPerFrame = settings.maxLightJobsPerFrame;
            MaxMeshRebuildsPerFrame = settings.maxMeshRebuildsPerFrame;
            MaxStructureModsPerFrame = settings.maxStructureModsPerFrame;

            MaxInFlightGenerationJobs = settings.maxInFlightGenerationJobs;
            MaxInFlightLightingJobs = settings.maxInFlightLightingJobs;
            MaxInFlightMeshJobs = settings.maxInFlightMeshJobs;

            TimeBudgetsEnabled = settings.enablePipelineTimeBudgets;
            ScaleCeilingsWithFpsCap = settings.scaleBudgetCeilingsWithFpsCap;
            GenProcessBudgetMs = settings.genProcessBudgetMs;
            LightScheduleBudgetMs = settings.lightScheduleBudgetMs;
            MeshScheduleBudgetMs = settings.meshScheduleBudgetMs;
            MeshApplyBudgetMs = settings.meshApplyBudgetMs;

            PanicGateEnabled = settings.enableGenerationPanicGate;
            PanicGateCloseThreshold = settings.panicGateCloseThreshold;
            PanicGateReopenThreshold = settings.panicGateReopenThreshold;
            ScalePanicGateWithResidency = settings.scalePanicGateThresholdsWithResidency;
            LightingEnabled = settings.enableLighting;

            // Through the same helper the gate itself calls, never a re-derivation here: a report that
            // computed its own version of the thresholds could disagree with the run it describes.
            GenerationPanicGate.DeriveThresholds(
                settings.ResidentWidth,
                settings.panicGateCloseThreshold,
                settings.panicGateReopenThreshold,
                settings.scalePanicGateThresholdsWithResidency,
                out EffectiveCloseThreshold,
                out EffectiveReopenThreshold);
        }

        /// <summary>
        /// Renders the report section, grouped by the stop reason each knob produces. The mapping is the
        /// point: the FP section's tallies say which limit bound the pipeline, and this says what that limit
        /// was set to — neither is actionable without the other.
        /// <para>Lives on the snapshot rather than in the report generator so a field added above cannot be
        /// captured but silently left unprinted.</para>
        /// </summary>
        /// <param name="sb">The report builder.</param>
        public void AppendTo(StringBuilder sb)
        {
            sb.AppendLine("<b>=== Pipeline Settings (as used by this run) ===</b>");
            sb.AppendLine("Every value below determines one of the stop reasons the FP verdict is derived from.");
            sb.AppendLine();

            sb.AppendLine($"View distance:       {ViewDistance} chunks");
            sb.AppendLine($"Load distance:       {LoadDistance} chunks  " +
                          $"({ResidentWidth}x{ResidentWidth} = {ResidentChunks:N0} resident)");
            sb.AppendLine($"Lighting engine:     {(LightingEnabled ? "ON" : "OFF")}");
            sb.AppendLine();

            sb.AppendLine("  Per-frame quotas -> 'Quota' stop reason (rate = cap x frame duration x 60):");
            sb.AppendLine($"    Light jobs:        {MaxLightJobsPerFrame}");
            sb.AppendLine($"    Mesh rebuilds:     {MaxMeshRebuildsPerFrame}");
            sb.AppendLine($"    Structure mods:    {MaxStructureModsPerFrame:N0}");
            sb.AppendLine();

            sb.AppendLine("  In-flight caps -> 'InFlightCap' stop reason:");
            sb.AppendLine($"    Generation jobs:   {MaxInFlightGenerationJobs}");
            sb.AppendLine($"    Lighting jobs:     {MaxInFlightLightingJobs}");
            sb.AppendLine($"    Mesh jobs:         {MaxInFlightMeshJobs}");
            sb.AppendLine();

            sb.AppendLine($"  Time ceilings -> 'Ceiling' stop reason  [budgets {(TimeBudgetsEnabled ? "ON" : "OFF")}, " +
                          $"FPS-cap scaling {(ScaleCeilingsWithFpsCap ? "ON" : "OFF")}]:");
            sb.AppendLine($"    Generation process:{FormatCeiling(GenProcessBudgetMs)}");
            sb.AppendLine($"    Light schedule:    {FormatCeiling(LightScheduleBudgetMs)}");
            sb.AppendLine($"    Mesh schedule:     {FormatCeiling(MeshScheduleBudgetMs)}");
            sb.AppendLine($"    Mesh apply:        {FormatCeiling(MeshApplyBudgetMs)}");
            sb.AppendLine();

            // The gate withholds admissions at DrainGenerationRequests, which is NOT one of the four
            // instrumented passes — so it never appears as a stop reason and is visible only as the
            // gate-closed % per phase. The ratio is printed because the threshold is absolute while the
            // backlog it guards scales with the resident square.
            sb.AppendLine("  Admission gate -> no stop reason; see 'Panic gate closed' per phase  " +
                          $"[gate {(PanicGateEnabled ? "ON" : "OFF")}, residency scaling " +
                          $"{(ScalePanicGateWithResidency ? "ON" : "OFF")}]:");
            sb.AppendLine($"    Configured:        {PanicGateCloseThreshold} / {PanicGateReopenThreshold} " +
                          $"lighting-backlog chunks (stated at resident width " +
                          $"{GenerationPanicGate.ReferenceResidentWidth})");
            sb.AppendLine($"    Effective:         {EffectiveCloseThreshold} / {EffectiveReopenThreshold} " +
                          $"at this run's resident width {ResidentWidth}");
            sb.AppendLine($"    Close threshold:   {EffectiveCloseThresholdPercentOfResident:F1}% of the resident " +
                          "square (a LOW % means the gate closes readily)");
            sb.AppendLine($"                       {PanicGateCloseThresholdPercentOfResident:F1}% unscaled — the " +
                          "pre-P-8 figure FP-8/FP-10 reasoned from");
            sb.AppendLine();
        }

        /// <summary>Formats a ceiling, naming the disabled case rather than printing a bare "0".</summary>
        /// <param name="ms">The configured ceiling in milliseconds.</param>
        /// <returns>A right-aligned label.</returns>
        private static string FormatCeiling(float ms) =>
            ms > 0f ? $"{ms,7:F1} ms" : "    off (quota only)";
    }
}
