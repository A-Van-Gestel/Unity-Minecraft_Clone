using System.Runtime.CompilerServices;

namespace Helpers
{
    /// <summary>
    /// Pure decision logic for the main-thread lighting-job processing pass — the orchestration step
    /// that runs after a <see cref="Jobs.NeighborhoodLightingJob"/> completes and its emitted
    /// cross-chunk <see cref="Jobs.LightModification"/>s must be delivered to neighboring chunks.
    /// <para>
    /// Centralized so the production orchestrator (<c>WorldJobManager.ProcessLightingJobs</c>) and the
    /// editor lighting validation harness (<c>LightingTestWorld.CompleteLightingJob</c>) share the exact
    /// same routing rule (drop / persist / defer / apply) and stability override — the two can never
    /// silently disagree on when a mod is deferred vs. applied, nor on when a chunk counts as stable.
    /// This mirrors the existing <see cref="CrossChunkLightModApplier"/> and
    /// <see cref="LightingScheduleDecision"/> seams. Stateless and side-effect free: callers perform
    /// the actual apply/defer/persist action and own the deferred-mod store.
    /// </para>
    /// </summary>
    public static class LightingJobProcessor
    {
        /// <summary>
        /// How a single emitted cross-chunk light modification must be handled, given the state of the
        /// chunk it targets at the moment the emitting job's result is processed.
        /// </summary>
        public enum CrossChunkModRoute : byte
        {
            /// <summary>The target chunk lies outside world boundaries — the mod can never be consumed
            /// and is dropped without affecting stability (production's out-of-world skip).</summary>
            DropOutOfWorld,

            /// <summary>The target chunk is in-world but not currently loaded/populated — the mod is
            /// saved to the persisted pending store for replay when the chunk loads.</summary>
            PersistUndeliverable,

            /// <summary>The target chunk has its own lighting job in flight this pass, snapshotted
            /// before this mod existed. Applying now would be overwritten by that job's full-LightMap
            /// merge (Bug 08 path 2) — defer it; the caller drains it right after the target's merge.</summary>
            Defer,

            /// <summary>The target chunk is loaded and has no in-flight job (or its job already merged
            /// this pass), so the mod is safe to apply to live data immediately.</summary>
            ApplyDirect,
        }

        /// <summary>
        /// Decides how to route one cross-chunk light modification toward its target chunk.
        /// </summary>
        /// <param name="targetInWorld">True when the target chunk coordinate lies inside world
        /// boundaries (production: <c>World.IsChunkInWorld</c>).</param>
        /// <param name="targetLoaded">True when the target chunk currently exists with populated data
        /// (production: a non-null, populated <c>RequestChunk</c> result). The fixed editor test grid
        /// passes the same value as <paramref name="targetInWorld"/> — every in-world chunk is loaded.</param>
        /// <param name="targetJobInFlightThisPass">True when the target chunk has a lighting job that is
        /// in flight and has NOT yet merged during the current processing pass (production:
        /// <c>LightingJobs.ContainsKey(target) &amp;&amp; !_completedLightJobs.Contains(target)</c>).</param>
        /// <returns>The route the caller must take for this modification.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static CrossChunkModRoute RouteCrossChunkMod(
            bool targetInWorld, bool targetLoaded, bool targetJobInFlightThisPass)
        {
            if (!targetInWorld) return CrossChunkModRoute.DropOutOfWorld;
            if (!targetLoaded) return CrossChunkModRoute.PersistUndeliverable;
            if (targetJobInFlightThisPass) return CrossChunkModRoute.Defer;
            return CrossChunkModRoute.ApplyDirect;
        }

        /// <summary>
        /// Whether a routed modification counts as a "real" cross-chunk mod for the stability override:
        /// every route except <see cref="CrossChunkModRoute.DropOutOfWorld"/> contributes, because only
        /// out-of-world mods can never be consumed. A chunk left not-stable solely by out-of-world mods
        /// would otherwise reschedule lighting indefinitely.
        /// </summary>
        /// <param name="route">The route returned by <see cref="RouteCrossChunkMod"/>.</param>
        /// <returns>True when the mod should keep the emitting chunk from being treated as stable.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool CountsAsRealCrossChunkMod(CrossChunkModRoute route)
        {
            return route != CrossChunkModRoute.DropOutOfWorld;
        }

        /// <summary>
        /// Applies the production stability override: a chunk whose Burst job reported not-stable solely
        /// because it emitted cross-chunk mods that all target out-of-world positions (which can never be
        /// consumed) is treated as effectively stable.
        /// </summary>
        /// <param name="jobReportedStable">The stability flag the Burst job wrote.</param>
        /// <param name="hasRealCrossChunkMods">True when at least one emitted mod was routed somewhere
        /// other than <see cref="CrossChunkModRoute.DropOutOfWorld"/> (see
        /// <see cref="CountsAsRealCrossChunkMod"/>).</param>
        /// <returns>The effective stability after the override.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsEffectivelyStable(bool jobReportedStable, bool hasRealCrossChunkMods)
        {
            return jobReportedStable || !hasRealCrossChunkMods;
        }
    }
}
