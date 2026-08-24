namespace Helpers
{
    /// <summary>
    /// Pure per-neighbor readiness predicate: decides whether ONE neighbor blocks a given gate, and why.
    /// The gate-side member of the shared-guard family alongside <see cref="LightingScheduleDecision"/>,
    /// <see cref="LightingScanDecision"/> and <c>JobCompletionPass</c>.
    /// <para>
    /// This is the sole definition of each gate's terms. Re-testing them in a loop of its own is how a
    /// readiness bug becomes reachable only in a running game — the three gates are close enough to look
    /// interchangeable and are not: they disagree deliberately about an unpopulated neighbor and about
    /// lighting state. <see cref="Evaluate"/>'s remarks carry the per-gate matrix.
    /// </para>
    /// <para>
    /// <b>Caller contract.</b> The caller owns everything needing world context: skipping neighbors outside
    /// the world, probing the job dictionaries and the chunk map to build <see cref="NeighborFacts"/>, and
    /// short-circuiting its loop on the first blocking neighbor. This type never sees a coordinate, a
    /// <c>ChunkData</c>, or a dictionary, so it evaluates without a live world.
    /// </para>
    /// <para>
    /// See Documentation/Design/LIGHTING_PIPELINE_STATE_REFACTOR.md §4.2 (LP-2).
    /// </para>
    /// </summary>
    public static class NeighborReadinessDecision
    {
        /// <summary>Which of the three readiness gates is being evaluated.</summary>
        public enum Gate : byte
        {
            /// <summary>Terrain data only: every neighbor must exist and be populated. Gates lighting
            /// scheduling, so it must never pass while a neighbor could still be all-air placeholder data.</summary>
            DataReady,

            /// <summary>The strict lighting gate: neighbors must additionally be fully lit and settled, so a
            /// border edge comparison reads data that is not about to change. Gates the edge-check arm.</summary>
            ReadyAndLit,

            /// <summary>The deliberately relaxed meshing gate: neighbors need populated data and one completed
            /// initial lighting pass, but may have lighting in flight or pending. The relaxation is required, not
            /// an oversight — demanding settled light here deadlocks the generation wave-front, since two
            /// neighbors each waiting on the other's light never mesh.</summary>
            MeshReady,
        }

        /// <summary>
        /// Why one neighbor blocks its gate, or <see cref="BlockReason.None"/> when it does not. Members are
        /// ordered to match each gate's short-circuit order, so the reason names the term that actually fired
        /// first rather than an arbitrary one of several true terms.
        /// </summary>
        public enum BlockReason : byte
        {
            /// <summary>The neighbor does not block this gate.</summary>
            None,

            /// <summary>A terrain-generation job is still running — there is no valid voxel data to read.
            /// Blocks all three gates.</summary>
            GenerationInFlight,

            /// <summary>A lighting job is running, so the neighbor's border light is still moving. Blocks
            /// <see cref="Gate.ReadyAndLit"/> only; <see cref="Gate.MeshReady"/> tolerates it by design.</summary>
            LightingInFlight,

            /// <summary>The neighbor is absent or is an unpopulated placeholder. Blocks
            /// <see cref="Gate.DataReady"/> and <see cref="Gate.MeshReady"/>; for
            /// <see cref="Gate.ReadyAndLit"/> an unpopulated neighbor has no light to settle and is skipped.</summary>
            NotPopulated,

            /// <summary>The neighbor has pending light changes that have not even been scheduled yet.
            /// Blocks <see cref="Gate.ReadyAndLit"/> only.</summary>
            PendingLightWork,

            /// <summary>The neighbor has never completed a lighting pass, so its light data is all zeros.
            /// Blocks <see cref="Gate.ReadyAndLit"/>, and <see cref="Gate.MeshReady"/> when lighting is
            /// enabled (with lighting off the sunlight fill supplies brightness instead).</summary>
            NeedsInitialLighting,
        }

        /// <summary>
        /// Everything the gates know about ONE neighbor, assembled by the caller from the job dictionaries
        /// and the chunk map. Plain bools only — no references, no allocation, and nothing that needs a live
        /// world, so the facts can be synthesized directly.
        /// </summary>
        public readonly struct NeighborFacts
        {
            /// <summary>A terrain-generation job is in flight (<c>JobManager.GenerationJobs.ContainsKey</c>).</summary>
            public readonly bool GenerationInFlight;

            /// <summary>A lighting job is in flight (<c>JobManager.LightingJobs.ContainsKey</c>).</summary>
            public readonly bool LightingInFlight;

            /// <summary>The chunk resolves AND carries populated terrain data (<c>TryGetChunk</c> succeeded
            /// and <c>ChunkData.IsPopulated</c>). False covers both "absent" and "empty placeholder", which
            /// every gate treats identically.</summary>
            public readonly bool ExistsAndPopulated;

            /// <summary><c>ChunkData.NeedsInitialLighting</c>. Only meaningful when
            /// <see cref="ExistsAndPopulated"/>.</summary>
            public readonly bool NeedsInitialLighting;

            /// <summary><c>ChunkData.HasLightChangesToProcess</c>. Only meaningful when
            /// <see cref="ExistsAndPopulated"/>.</summary>
            public readonly bool HasLightChanges;

            /// <summary><c>Settings.enableLighting</c>. A world-level fact, carried per neighbor so the
            /// predicate stays a pure function of its argument.</summary>
            public readonly bool LightingEnabled;

            /// <summary>Assembles the facts for one neighbor.</summary>
            /// <param name="generationInFlight">See <see cref="GenerationInFlight"/>.</param>
            /// <param name="lightingInFlight">See <see cref="LightingInFlight"/>.</param>
            /// <param name="existsAndPopulated">See <see cref="ExistsAndPopulated"/>.</param>
            /// <param name="needsInitialLighting">See <see cref="NeedsInitialLighting"/>.</param>
            /// <param name="hasLightChanges">See <see cref="HasLightChanges"/>.</param>
            /// <param name="lightingEnabled">See <see cref="LightingEnabled"/>.</param>
            public NeighborFacts(
                bool generationInFlight,
                bool lightingInFlight,
                bool existsAndPopulated,
                bool needsInitialLighting,
                bool hasLightChanges,
                bool lightingEnabled)
            {
                GenerationInFlight = generationInFlight;
                LightingInFlight = lightingInFlight;
                ExistsAndPopulated = existsAndPopulated;
                NeedsInitialLighting = needsInitialLighting;
                HasLightChanges = hasLightChanges;
                LightingEnabled = lightingEnabled;
            }
        }

        /// <summary>
        /// Decides whether one neighbor blocks the given gate, and why.
        /// </summary>
        /// <remarks>
        /// The per-gate term matrix. The gates are NOT refinements of one another — read the differences:
        /// <list type="bullet">
        /// <item><see cref="Gate.DataReady"/> — generation in flight, or not populated.</item>
        /// <item><see cref="Gate.ReadyAndLit"/> — generation in flight, or lighting in flight, or (when
        /// populated) pending light changes / needs initial lighting. An unpopulated neighbor does
        /// <b>not</b> block: it has no light to settle.</item>
        /// <item><see cref="Gate.MeshReady"/> — generation in flight, or not populated, or (only when
        /// lighting is enabled) needs initial lighting. Lighting in flight and pending light changes are
        /// tolerated on purpose.</item>
        /// </list>
        /// </remarks>
        /// <param name="gate">Which gate's rules to apply.</param>
        /// <param name="facts">The neighbor's assembled facts.</param>
        /// <returns><see cref="BlockReason.None"/> when the neighbor satisfies the gate; otherwise the first
        /// term that blocked it.</returns>
        public static BlockReason Evaluate(Gate gate, in NeighborFacts facts)
        {
            // Generation is the one term every gate shares: without finished terrain there is nothing valid
            // to snapshot, whatever the caller intends to do with the neighborhood.
            if (facts.GenerationInFlight) return BlockReason.GenerationInFlight;

            switch (gate)
            {
                case Gate.DataReady:
                    return facts.ExistsAndPopulated ? BlockReason.None : BlockReason.NotPopulated;

                case Gate.ReadyAndLit:
                    if (facts.LightingInFlight) return BlockReason.LightingInFlight;

                    // Unpopulated placeholders are skipped rather than blocking: they hold no light, so
                    // there is nothing for the edge comparison to wait on.
                    if (!facts.ExistsAndPopulated) return BlockReason.None;

                    if (facts.HasLightChanges) return BlockReason.PendingLightWork;
                    if (facts.NeedsInitialLighting) return BlockReason.NeedsInitialLighting;

                    return BlockReason.None;

                case Gate.MeshReady:
                    if (!facts.ExistsAndPopulated) return BlockReason.NotPopulated;

                    // With lighting disabled the sunlight fill supplies brightness, so a never-lit neighbor
                    // still meshes correctly and the term must not fire.
                    return facts.LightingEnabled && facts.NeedsInitialLighting
                        ? BlockReason.NeedsInitialLighting
                        : BlockReason.None;

                default:
                    return BlockReason.None;
            }
        }
    }
}
