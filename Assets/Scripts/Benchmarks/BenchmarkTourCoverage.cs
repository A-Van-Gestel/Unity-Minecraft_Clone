using System.Collections.Generic;
using Data;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace Benchmarks
{
    /// <summary>
    /// Measures how much of the loading tour exists before the loading pass starts (FP-11a).
    /// <para>
    /// The ensure pass exists so the loading pass flies over terrain that already exists. Its pre-generation
    /// (CMD-6) reports its own success, but this count is independent of it: it credits the chunks the pipeline
    /// actually populated, so a loading pass whose terrain is short reads as short whatever made it. Without it,
    /// the loading numbers rest on the ensure pass <i>having run</i> rather than on it having worked.
    /// </para>
    /// <para>
    /// Marking is driven from <see cref="PipelineTelemetry.StampPopulated"/> and spans the whole run up to the
    /// moment the loading pass starts: terrain the generation pass already produced counts, since it is on
    /// disk by then (<c>ChunkData.Populate</c> marks the chunk modified, and both the unload path and the
    /// transition's synchronous save write modified chunks). Terrain that existed before arming is credited once,
    /// at arming, through <see cref="CreditExisting"/>.
    /// </para>
    /// <para>
    /// <b>Two figures, taken at two instants.</b> <see cref="SnapshotEnsurePass"/> records what the ensure
    /// pass itself achieved; <see cref="Freeze"/> fixes the final figure <i>after</i> the transition. They
    /// can differ because <c>World.ForceUnloadAllChunks</c> waits out the in-flight jobs before saving, and
    /// <c>World.Update</c> keeps admitting the queued generation backlog throughout that wait — so terrain still
    /// in flight when the ensure pass ended can land on disk, and is then genuinely <i>loaded</i> by the loading
    /// pass. Freezing at the end of the ensure pass would count it as missing. The gap between the two figures is
    /// how much of the tour the ensure pass left to the transition.
    /// </para>
    /// <para>
    /// Freezing before the loading pass is still essential in the other direction — that pass populates the
    /// same chunks by definition and would drive coverage to 100 % regardless of what preceded it.
    /// </para>
    /// </summary>
    public static class BenchmarkTourCoverage
    {
        /// <summary>
        /// Fraction of the tour footprint that must be generated for the loading pass to be measuring
        /// loading. Below this it generates part of its own terrain — the confound the ensure pass exists to
        /// remove, and one no other figure in the report reveals.
        /// </summary>
        public const float MinimumCoverage = 0.99f;

        // Only chunks the loading pass will actually visit are retained, so this never grows past the
        // footprint (~19 k chunks at vd 32) no matter how much terrain the run generates.
        [NoAutoStaticsCleanup] // contents cleared in DomainReset
        private static readonly HashSet<ChunkCoord> s_required = new HashSet<ChunkCoord>();
        [NoAutoStaticsCleanup] // contents cleared in DomainReset
        private static readonly HashSet<ChunkCoord> s_covered = new HashSet<ChunkCoord>();

        [NoAutoStaticsCleanup] // reset in DomainReset
        private static bool s_armed;
        [NoAutoStaticsCleanup] // reset in DomainReset
        private static bool s_frozen;
        [NoAutoStaticsCleanup] // reset in DomainReset
        private static bool s_ensurePassSnapshotTaken;
        [NoAutoStaticsCleanup] // reset in DomainReset
        private static int s_ensurePassCovered;

        /// <summary>Whether marking is currently accruing.</summary>
        public static bool Armed => s_armed;

        /// <summary>Chunks the loading pass will make resident — the coverage denominator.</summary>
        public static int RequiredChunks => s_required.Count;

        /// <summary>Required chunks that were populated before the measurement was frozen.</summary>
        public static int CoveredChunks => s_covered.Count;

        /// <summary>
        /// Whether a coverage figure exists and means something. False when the tracker was never armed or the
        /// footprint came out empty — both of which must read as <i>not measured</i> rather than as a vacuous
        /// 100 %, which is exactly the false green this instrument exists to prevent.
        /// </summary>
        public static bool HasMeasurement => s_frozen && s_required.Count > 0;

        /// <summary>Fraction of the tour footprint that was generated, in <c>[0, 1]</c>; 0 when unmeasured.</summary>
        public static float CoverageFraction =>
            s_required.Count > 0 ? (float)s_covered.Count / s_required.Count : 0f;

        /// <summary>
        /// Whether coverage was both measured and sufficient. False for an unmeasurable run, so a missing
        /// footprint can never read as a clean pass.
        /// </summary>
        public static bool IsSufficient => HasMeasurement && CoverageFraction >= MinimumCoverage;

        /// <summary>Whether the ensure-pass figure was taken.</summary>
        public static bool HasEnsurePassSnapshot => s_ensurePassSnapshotTaken && s_required.Count > 0;

        /// <summary>Footprint chunks generated by the end of the ensure pass itself.</summary>
        public static int EnsurePassCoveredChunks => s_ensurePassCovered;

        /// <summary>
        /// Fraction of the footprint generated by the end of the ensure pass. Informational, not an admissibility
        /// gate: the transition drain legitimately finishes work still in flight when the pass ended.
        /// </summary>
        public static float EnsurePassCoverageFraction =>
            s_required.Count > 0 ? (float)s_ensurePassCovered / s_required.Count : 0f;

        /// <summary>
        /// Computes the tour footprint and starts accruing coverage. Called at the start of the generation
        /// pass, not the ensure pass, so terrain the generation phases already produced is credited.
        /// </summary>
        /// <param name="geometry">The run's route geometry.</param>
        /// <param name="loadDistance">Active <c>Settings.LoadDistance</c>, in chunks.</param>
        public static void Arm(in BenchmarkRouteGeometry geometry, int loadDistance)
        {
            s_covered.Clear();
            geometry.BuildTourChunkSet(loadDistance, s_required);
            s_ensurePassCovered = 0;
            s_ensurePassSnapshotTaken = false;
            s_frozen = false;
            s_armed = true;
        }

        /// <summary>
        /// Credits footprint chunks that already exist when the tracker arms — the world load's spawn area, generated
        /// before the run. Nothing need populate them again: pre-generation skips a station with nothing missing, so
        /// without this they would read as missing although the loading pass loads them.
        /// </summary>
        /// <param name="existing">Chunks resident and populated, or saved on disk, at arming time.</param>
        public static void CreditExisting(HashSet<ChunkCoord> existing)
        {
            foreach (ChunkCoord coord in existing) MarkPopulated(coord);
        }

        /// <summary>
        /// Records what the ensure pass alone achieved, without stopping accrual. Called at the end of that
        /// pass; the transition then continues to contribute to the final figure.
        /// </summary>
        public static void SnapshotEnsurePass()
        {
            s_ensurePassCovered = s_covered.Count;
            s_ensurePassSnapshotTaken = true;
        }

        /// <summary>
        /// Stops accruing and fixes the final measurement. Called once the transition has drained, saved and
        /// unloaded — i.e. at the instant the loading pass is about to begin.
        /// </summary>
        public static void Freeze()
        {
            s_armed = false;
            s_frozen = true;
        }

        /// <summary>
        /// Discards all state and disarms. Used when a run ends without reaching the freeze — an abort back to
        /// the main menu, or an early exit from the ensure pass — so an armed tracker cannot outlive the
        /// benchmark and make every later world session pay a lookup per populated chunk while accruing into
        /// a footprint nothing will ever report. Leaves no measurement behind, so a stale render says so.
        /// </summary>
        public static void Reset()
        {
            s_required.Clear();
            s_covered.Clear();
            s_ensurePassCovered = 0;
            s_ensurePassSnapshotTaken = false;
            s_armed = false;
            s_frozen = false;
        }

        /// <summary>
        /// Records a chunk becoming populated. A no-op unless armed and the chunk is part of the tour
        /// footprint, so the cost on the populate path is one bool read in every non-benchmark session.
        /// </summary>
        /// <param name="coord">The populated chunk.</param>
        public static void MarkPopulated(ChunkCoord coord)
        {
            if (!s_armed) return;
            if (s_required.Contains(coord)) s_covered.Add(coord);
        }

        /// <summary>
        /// Clears all state on play-mode entry, so a previous capture's footprint cannot be reported as this
        /// one's when domain reload is disabled (the FP-5 defect class).
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void DomainReset()
        {
            // Assigned lexically here rather than by delegating to Reset(): UDR0002 requires every mutable
            // static to be written inside the [RuntimeInitializeOnLoadMethod] itself.
            s_required.Clear();
            s_covered.Clear();
            s_ensurePassCovered = 0;
            s_ensurePassSnapshotTaken = false;
            s_armed = false;
            s_frozen = false;
        }
    }
}
