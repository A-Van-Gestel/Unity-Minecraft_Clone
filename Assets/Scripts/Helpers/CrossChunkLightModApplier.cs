using System;
using Data;
using Jobs;
using Jobs.BurstData;
using UnityEngine;

namespace Helpers
{
    /// <summary>
    /// Pure decision logic for applying a cross-chunk <see cref="LightModification"/> (emitted by a
    /// <see cref="NeighborhoodLightingJob"/>) to the live light data of a neighboring chunk.
    /// Centralized so the main-thread orchestrator (<c>WorldJobManager.ProcessLightingJobs</c>) and the
    /// editor lighting validation suite share the exact same stale-snapshot guards and BFS wake-up
    /// node semantics. Stateless and side-effect free: callers perform the actual light store and
    /// queue enqueue based on the returned <see cref="ApplyDecision"/>.
    /// </summary>
    public static class CrossChunkLightModApplier
    {
        /// <summary>
        /// The outcome of evaluating a cross-chunk light modification against a voxel's current light value.
        /// </summary>
        public readonly struct ApplyDecision
        {
            /// <summary>True when the modification must be written and a BFS wake-up node enqueued.</summary>
            public readonly bool ShouldApply;

            /// <summary>The new packed ushort light value to store. Only meaningful when <see cref="ShouldApply"/> is true.</summary>
            public readonly ushort NewLight;

            /// <summary>The voxel's pre-apply scalar light level for the wake-up node (sky level for sunlight mods, max RGB channel for blocklight mods).</summary>
            public readonly byte OldLevel;

            /// <summary>The voxel's pre-apply red blocklight channel for the wake-up node (always 0 for sunlight mods).</summary>
            public readonly byte OldR;

            /// <summary>The voxel's pre-apply green blocklight channel for the wake-up node (always 0 for sunlight mods).</summary>
            public readonly byte OldG;

            /// <summary>The voxel's pre-apply blue blocklight channel for the wake-up node (always 0 for sunlight mods).</summary>
            public readonly byte OldB;

            /// <summary>A decision that applies nothing (the modification is skipped).</summary>
            public static ApplyDecision Skip => default;

            /// <summary>
            /// Initializes an apply decision with <see cref="ShouldApply"/> set to true.
            /// </summary>
            /// <param name="newLight">The new packed ushort light value to store.</param>
            /// <param name="oldLevel">The pre-apply scalar light level for the wake-up node.</param>
            /// <param name="oldR">The pre-apply red blocklight channel.</param>
            /// <param name="oldG">The pre-apply green blocklight channel.</param>
            /// <param name="oldB">The pre-apply blue blocklight channel.</param>
            public ApplyDecision(ushort newLight, byte oldLevel, byte oldR, byte oldG, byte oldB)
            {
                ShouldApply = true;
                NewLight = newLight;
                OldLevel = oldLevel;
                OldR = oldR;
                OldG = oldG;
                OldB = oldB;
            }
        }

        /// <summary>
        /// Evaluates a cross-chunk light modification against the target voxel's current packed light
        /// value, dispatching to the channel-specific rules.
        /// </summary>
        /// <param name="currentLight">The voxel's current packed ushort light value.</param>
        /// <param name="mod">The cross-chunk modification emitted by the lighting job.</param>
        /// <returns>The apply decision, including the new light value and wake-up node old values.</returns>
        public static ApplyDecision Compute(ushort currentLight, in LightModification mod, byte independentSunlightSupport = 0,
            byte independentBlockR = 0, byte independentBlockG = 0, byte independentBlockB = 0)
        {
            return mod.Channel == LightChannel.Sun
                ? ComputeSunlight(currentLight, mod.LightLevel, independentSunlightSupport)
                : ComputeBlocklight(currentLight, mod.BlockR, mod.BlockG, mod.BlockB, mod.IsRemoval,
                    independentBlockR, independentBlockG, independentBlockB);
        }

        /// <summary>
        /// The strongest sky light an <b>in-chunk</b> neighbor of <paramref name="localPos"/> could still
        /// supply it, attenuated by the cost of entering the target voxel. Used to veto a spurious
        /// cross-chunk sunlight removal: a voxel a neighbor inside the receiving chunk independently
        /// supports must not be cleared to 0 by a darkness wave the emitting chunk computed against a
        /// stale snapshot — that is the simultaneous mutual cross-seam removal/re-placement oscillation
        /// (Bug 11). Only neighbors inside the chunk's own X/Z columns are consulted; the cross-chunk
        /// neighbors are exactly the stale side the removal mod itself came from, so trusting them would
        /// defeat the guard.
        /// <para>
        /// Attenuation uses the shared <see cref="LightAttenuation.Attenuate"/> (the same definition the
        /// BFS and the validation oracle use): light is charged the <b>destination</b> voxel's opacity on
        /// entry, <c>max(1, targetOpacity)</c> per step. Passing the flat air cost (opacity ≤ 1) would
        /// over-estimate support into semi-transparent media and wrongly veto a legitimate removal,
        /// leaving stale over-bright light until a full relight.
        /// </para>
        /// <para>
        /// Fully-opaque neighbors are skipped: a fully-opaque block cannot propagate sunlight to its
        /// neighbors (mirror of <c>NeighborhoodLightingJob.PropagateLight</c>'s <c>IsOpaque</c> source
        /// guard), yet it can still hold a high stored sky value (e.g. a sky-exposed roof block stores
        /// sky-top 15). Counting that as support would over-estimate it and again veto a legitimate
        /// removal. Semi-transparent neighbors (glass/leaves/water) DO propagate and are kept.
        /// </para>
        /// </summary>
        /// <param name="chunk">The chunk receiving the cross-chunk modification.</param>
        /// <param name="localPos">The local voxel position the modification targets.</param>
        /// <param name="targetOpacity">The opacity of the voxel at <paramref name="localPos"/> (the light
        /// enters this voxel, so it pays this voxel's opacity — minimum 1).</param>
        /// <param name="isBlockFullyOpaque">Predicate returning true when a block id is fully opaque
        /// (opacity ≥ 15) and therefore cannot propagate sunlight. Supplied by the caller so this helper
        /// stays free of any block-database dependency.</param>
        /// <returns>The maximum attenuated sky a same-chunk neighbor supports (0 if none).</returns>
        public static byte InChunkSunlightSupport(ChunkData chunk, Vector3Int localPos, byte targetOpacity, Func<ushort, bool> isBlockFullyOpaque)
        {
            byte best = 0;
            for (int i = 0; i < 6; i++)
            {
                Vector3Int n = localPos + VoxelData.FaceChecks[i];
                if (n.x < 0 || n.x >= VoxelData.ChunkWidth ||
                    n.z < 0 || n.z >= VoxelData.ChunkWidth ||
                    n.y < 0 || n.y >= VoxelData.ChunkHeight)
                    continue; // cross-chunk (untrusted) or out of vertical range

                byte s = LightBitMapping.GetSkyLight(chunk.GetLightData(n.x, n.y, n.z));
                byte support = LightAttenuation.Attenuate(s, targetOpacity);
                if (support <= best)
                    continue; // can't improve the best support — skip the voxel read + opacity check

                // A fully-opaque neighbor cannot propagate sunlight (mirror of PropagateLight's IsOpaque
                // source guard), so its stored sky — possibly a high sky-top value — is not real support.
                ushort neighborId = BurstVoxelDataBitMapping.GetId(chunk.GetVoxel(n.x, n.y, n.z));
                if (isBlockFullyOpaque(neighborId))
                    continue;

                best = support;
            }

            return best;
        }

        /// <summary>
        /// The strongest sky light a <b>live cross-chunk</b> neighbor of the target voxel — in a chunk
        /// OTHER than the one that emitted the removal — could still supply it, attenuated by the entry
        /// cost. Completes the Bug 11 veto for voxels whose genuine support crosses a <i>different</i>
        /// seam (the Bug 13 live-lock): a border voxel fed by a sky-lit chunk on its far side (the
        /// perimeter gradient under a multi-chunk opaque slab) has no in-chunk support ≥ its value, so
        /// the in-chunk veto alone let the Bug 12 cross-seam removal initiator clear it every pass — the
        /// seam pull-back then re-lit it, and the pair oscillated forever. Live main-thread data is
        /// trustworthy (unlike the emitter's schedule-time snapshot). The <b>emitting</b> chunk itself is
        /// excluded: it is exactly the possibly-stale mutual-loop side the removal is trying to collapse,
        /// and crediting it would re-arm Bug 12.
        /// </summary>
        /// <param name="targetChunkOriginXZ">The receiving chunk's voxel origin (world XZ).</param>
        /// <param name="localPos">The local voxel position the modification targets.</param>
        /// <param name="targetOpacity">The opacity of the target voxel (entry cost, minimum 1).</param>
        /// <param name="emitterChunkOriginXZ">The voxel origin of the chunk whose job emitted the mod.</param>
        /// <param name="getLoadedChunk">Lookup from a chunk voxel origin (world XZ) to its live,
        /// populated <see cref="ChunkData"/>, or null when absent/unloaded. Supplied by the caller
        /// (world store vs. harness grid); cache the delegate to avoid per-mod closures.</param>
        /// <param name="isBlockFullyOpaque">Predicate returning true when a block id is fully opaque
        /// (opacity ≥ 15) and therefore cannot propagate sunlight.</param>
        /// <returns>The maximum attenuated sky a live third-party cross-chunk neighbor supports (0 if none).</returns>
        public static byte CrossChunkSunlightSupport(Vector2Int targetChunkOriginXZ, Vector3Int localPos,
            byte targetOpacity, Vector2Int emitterChunkOriginXZ,
            Func<Vector2Int, ChunkData> getLoadedChunk, Func<ushort, bool> isBlockFullyOpaque)
        {
            byte best = 0;
            for (int i = 0; i < 6; i++)
            {
                Vector3Int dir = VoxelData.FaceChecks[i];
                if (dir.y != 0) continue; // vertical neighbors never cross a chunk boundary

                Vector3Int n = localPos + dir;
                if (n.x >= 0 && n.x < VoxelData.ChunkWidth &&
                    n.z >= 0 && n.z < VoxelData.ChunkWidth)
                    continue; // in-chunk neighbors are InChunkSunlightSupport's job

                Vector2Int ownerOrigin = targetChunkOriginXZ + new Vector2Int(dir.x, dir.z) * VoxelData.ChunkWidth;
                if (ownerOrigin == emitterChunkOriginXZ) continue; // the emitter is the untrusted side

                ChunkData owner = getLoadedChunk(ownerOrigin);
                if (owner == null) continue;

                // Wrap the stepped position into the owning chunk's local space (only the stepped axis moved).
                int lx = n.x - dir.x * VoxelData.ChunkWidth;
                int lz = n.z - dir.z * VoxelData.ChunkWidth;

                byte s = LightBitMapping.GetSkyLight(owner.GetLightData(lx, n.y, lz));
                byte support = LightAttenuation.Attenuate(s, targetOpacity);
                if (support <= best)
                    continue; // can't improve the best support — skip the voxel read + opacity check

                // A fully-opaque neighbor cannot propagate sunlight — its stored sky is not real support.
                ushort neighborId = BurstVoxelDataBitMapping.GetId(owner.GetVoxel(lx, n.y, lz));
                if (isBlockFullyOpaque(neighborId))
                    continue;

                best = support;
            }

            return best;
        }

        /// <summary>
        /// The opacity at and above which a block is fully opaque (mirrors
        /// <c>BlockTypeJobData.IsOpaque</c>'s <c>Opacity >= 15</c>).
        /// </summary>
        private const byte FULLY_OPAQUE_OPACITY = 15;

        /// <summary>
        /// Re-verifies one <see cref="Jobs.PullBackClaim"/> against the claimed neighbor's LIVE data (the
        /// Bug 14 stale-ghost guard): the darkness-wave pull-back trusted a schedule-time snapshot to
        /// re-light a border voxel, and the claim holds only if the live neighbor still supplies at least
        /// the written level after entering the center voxel. A fully-opaque live neighbor supplies
        /// nothing (surface light is non-propagable — the Bug 10 rule); a fully-opaque CENTER holds a
        /// surface stamp (source − 1, the receive-only rule) rather than attenuated propagation (Bug 15).
        /// Mirrors <c>NeighborhoodLightingJob.CheckEdgeVoxel</c>'s write condition exactly, so a fresh
        /// snapshot always verifies; only genuinely stale trust fails and is routed to the removal veto
        /// by the caller. Centralized so production and the validation harness cannot drift on the rule.
        /// </summary>
        /// <param name="liveNeighborSky">The claimed neighbor voxel's live sky level (0-15).</param>
        /// <param name="neighborFullyOpaque">Whether the live neighbor block is fully opaque.</param>
        /// <param name="centerOpacity">The re-lit center voxel's opacity (entry cost, minimum 1).</param>
        /// <param name="writtenSky">The sky level the pull-back wrote from the snapshot.</param>
        /// <returns>True when the live neighbor still supports the written level.</returns>
        public static bool PullBackClaimStillSupported(byte liveNeighborSky, bool neighborFullyOpaque,
            byte centerOpacity, byte writtenSky)
        {
            if (neighborFullyOpaque)
                return false;

            int support = centerOpacity >= FULLY_OPAQUE_OPACITY
                ? liveNeighborSky - 1
                : LightAttenuation.Attenuate(liveNeighborSky, centerOpacity);
            return support >= writtenSky;
        }

        /// <summary>
        /// Per-channel RGB analog of <see cref="InChunkSunlightSupport"/> (the Bug 11 blocklight mirror):
        /// the strongest blocklight an in-chunk neighbor could still supply the target voxel, per channel,
        /// attenuated by the target's entry opacity. Vetoes a spurious cross-chunk RGB removal so a channel
        /// an independent in-chunk source still backs is not cleared to 0 by a stale-snapshot removal. A
        /// fully-opaque neighbor contributes only its OWN emission (it propagates emission but never
        /// received surface light — mirror of <c>PropagateLightRGB</c>'s opaque-source arm); a
        /// transparent/semi neighbor contributes its stored blocklight.
        /// </summary>
        /// <param name="chunk">The chunk receiving the cross-chunk modification.</param>
        /// <param name="localPos">The local voxel position the modification targets.</param>
        /// <param name="targetOpacity">The target voxel's opacity (entry cost, minimum 1).</param>
        /// <param name="isBlockFullyOpaque">Predicate: is a block id fully opaque?</param>
        /// <param name="blockEmission">Lookup: a block id's own RGB emission.</param>
        /// <param name="suppR">Out: strongest attenuated red an in-chunk neighbor supplies.</param>
        /// <param name="suppG">Out: strongest attenuated green.</param>
        /// <param name="suppB">Out: strongest attenuated blue.</param>
        public static void InChunkBlocklightSupport(ChunkData chunk, Vector3Int localPos, byte targetOpacity,
            Func<ushort, bool> isBlockFullyOpaque, Func<ushort, (byte r, byte g, byte b)> blockEmission,
            out byte suppR, out byte suppG, out byte suppB)
        {
            suppR = 0;
            suppG = 0;
            suppB = 0;
            for (int i = 0; i < 6; i++)
            {
                Vector3Int n = localPos + VoxelData.FaceChecks[i];
                if (n.x < 0 || n.x >= VoxelData.ChunkWidth ||
                    n.z < 0 || n.z >= VoxelData.ChunkWidth ||
                    n.y < 0 || n.y >= VoxelData.ChunkHeight)
                    continue; // cross-chunk (untrusted) or out of vertical range

                ResolveNeighborBlocklight(chunk.GetVoxel(n.x, n.y, n.z), chunk.GetLightData(n.x, n.y, n.z),
                    isBlockFullyOpaque, blockEmission, out byte nR, out byte nG, out byte nB);

                byte sR = LightAttenuation.Attenuate(nR, targetOpacity);
                byte sG = LightAttenuation.Attenuate(nG, targetOpacity);
                byte sB = LightAttenuation.Attenuate(nB, targetOpacity);
                if (sR > suppR) suppR = sR;
                if (sG > suppG) suppG = sG;
                if (sB > suppB) suppB = sB;
            }
        }

        /// <summary>
        /// Per-channel RGB analog of <see cref="CrossChunkSunlightSupport"/> (the Bug 13 blocklight
        /// mirror): the strongest blocklight a LIVE cross-chunk neighbor in a chunk OTHER than the emitter
        /// could still supply the target, per channel. Completes the RGB removal veto for a border voxel
        /// whose genuine support crosses a different seam. Live main-thread data is trustworthy; the
        /// emitting chunk is excluded (it is the possibly-stale side the removal is collapsing).
        /// </summary>
        /// <param name="targetChunkOriginXZ">The receiving chunk's voxel origin (world XZ).</param>
        /// <param name="localPos">The local voxel position the modification targets.</param>
        /// <param name="targetOpacity">The target voxel's opacity (entry cost, minimum 1).</param>
        /// <param name="emitterChunkOriginXZ">The voxel origin of the chunk whose job emitted the mod.</param>
        /// <param name="getLoadedChunk">Lookup from a chunk voxel origin to its live loaded chunk, or null.</param>
        /// <param name="isBlockFullyOpaque">Predicate: is a block id fully opaque?</param>
        /// <param name="blockEmission">Lookup: a block id's own RGB emission.</param>
        /// <param name="suppR">Out: strongest attenuated red a live third-party cross-chunk neighbor supplies.</param>
        /// <param name="suppG">Out: strongest attenuated green.</param>
        /// <param name="suppB">Out: strongest attenuated blue.</param>
        public static void CrossChunkBlocklightSupport(Vector2Int targetChunkOriginXZ, Vector3Int localPos,
            byte targetOpacity, Vector2Int emitterChunkOriginXZ,
            Func<Vector2Int, ChunkData> getLoadedChunk, Func<ushort, bool> isBlockFullyOpaque,
            Func<ushort, (byte r, byte g, byte b)> blockEmission,
            out byte suppR, out byte suppG, out byte suppB)
        {
            suppR = 0;
            suppG = 0;
            suppB = 0;
            for (int i = 0; i < 6; i++)
            {
                Vector3Int dir = VoxelData.FaceChecks[i];
                if (dir.y != 0) continue; // vertical neighbors never cross a chunk boundary

                Vector3Int n = localPos + dir;
                if (n.x >= 0 && n.x < VoxelData.ChunkWidth &&
                    n.z >= 0 && n.z < VoxelData.ChunkWidth)
                    continue; // in-chunk neighbors are InChunkBlocklightSupport's job

                Vector2Int ownerOrigin = targetChunkOriginXZ + new Vector2Int(dir.x, dir.z) * VoxelData.ChunkWidth;
                if (ownerOrigin == emitterChunkOriginXZ) continue; // the emitter is the untrusted side

                ChunkData owner = getLoadedChunk(ownerOrigin);
                if (owner == null) continue;

                int lx = n.x - dir.x * VoxelData.ChunkWidth;
                int lz = n.z - dir.z * VoxelData.ChunkWidth;

                ResolveNeighborBlocklight(owner.GetVoxel(lx, n.y, lz), owner.GetLightData(lx, n.y, lz),
                    isBlockFullyOpaque, blockEmission, out byte nR, out byte nG, out byte nB);

                byte sR = LightAttenuation.Attenuate(nR, targetOpacity);
                byte sG = LightAttenuation.Attenuate(nG, targetOpacity);
                byte sB = LightAttenuation.Attenuate(nB, targetOpacity);
                if (sR > suppR) suppR = sR;
                if (sG > suppG) suppG = sG;
                if (sB > suppB) suppB = sB;
            }
        }

        /// <summary>
        /// Resolves a neighbor voxel's propagable blocklight per channel for the RGB removal veto: an
        /// opaque block propagates only its own emission (mirror of <c>PropagateLightRGB</c>'s
        /// opaque-source arm), a transparent/semi block propagates its stored blocklight.
        /// </summary>
        private static void ResolveNeighborBlocklight(uint neighborVoxel, ushort neighborLight,
            Func<ushort, bool> isBlockFullyOpaque, Func<ushort, (byte r, byte g, byte b)> blockEmission,
            out byte nR, out byte nG, out byte nB)
        {
            ushort neighborId = BurstVoxelDataBitMapping.GetId(neighborVoxel);
            if (isBlockFullyOpaque(neighborId))
            {
                (nR, nG, nB) = blockEmission(neighborId);
            }
            else
            {
                nR = LightBitMapping.GetBlocklightR(neighborLight);
                nG = LightBitMapping.GetBlocklightG(neighborLight);
                nB = LightBitMapping.GetBlocklightB(neighborLight);
            }
        }

        /// <summary>
        /// Evaluates a cross-chunk sunlight modification.
        /// </summary>
        /// <param name="currentLight">The voxel's current packed ushort light value.</param>
        /// <param name="modLightLevel">The sunlight level the modification wants to set (0-15).</param>
        /// <param name="independentSunlightSupport">The strongest attenuated sky an independent source
        /// still supplies the voxel — max of <see cref="InChunkSunlightSupport"/> (Bug 11) and
        /// <see cref="CrossChunkSunlightSupport"/> (Bug 13). Consulted only by removals (level 0).</param>
        /// <returns>The apply decision, including the new light value and wake-up node old values.</returns>
        public static ApplyDecision ComputeSunlight(ushort currentLight, byte modLightLevel, byte independentSunlightSupport = 0)
        {
            byte currentSunlight = LightBitMapping.GetSkyLight(currentLight);

            // Guard: Cross-chunk BFS mods are computed against a STALE snapshot of
            // the neighbor's data (taken before the neighbor's own lighting pass).
            // This means a mod might try to set sunlight to a value LOWER than what
            // the neighbor's own column recalculation has already computed.
            //
            // Rule: Non-zero cross-chunk sunlight mods may only INCREASE light.
            // - Uplift mods (from PropagateLight): must be >= current to apply.
            // - Darkness removal mods (level=0, from PropagateDarkness): apply so block
            //   removal/placement propagates across borders — but NOT when an independent
            //   in-chunk source still supports the current value (see the Bug 11 guard below).
            if (modLightLevel > 0 && modLightLevel < currentSunlight)
            {
                return ApplyDecision.Skip;
            }

            if (currentSunlight == modLightLevel)
            {
                return ApplyDecision.Skip;
            }

            // Bug 11 guard: a cross-chunk sunlight removal (level 0) must not clobber a voxel that an
            // INDEPENDENT source still supports. The emitting chunk computed this removal against a
            // stale snapshot of the receiver; when two adjacent chunks remove each other's shared seam
            // column in the same wave (e.g. both reloaded mid-darkness-wave), forcing the receiver's
            // freshly re-lit, independently-supported value back to 0 re-arms the cycle forever (the
            // sunlight removal/re-placement oscillation that stalls reloaded worlds). Independent support
            // is the max of (a) in-chunk neighbors (InChunkSunlightSupport — Bug 11) and (b) LIVE
            // cross-chunk neighbors in chunks other than the emitter (CrossChunkSunlightSupport — Bug 13:
            // a perimeter-fed border voxel under a multi-chunk slab has no in-chunk support, so without
            // (b) the Bug 12 removal initiator cleared it every pass and the seam live-locked). A source
            // still supplying >= the current value means the value is NOT dependent on the removed
            // cross-chunk light, so the removal is spurious and is skipped; a genuinely dependent voxel
            // (no independent support) still clears, preserving legitimate cross-chunk darkness.
            if (modLightLevel == 0 && currentSunlight > 0 && independentSunlightSupport >= currentSunlight)
            {
                return ApplyDecision.Skip;
            }

            return new ApplyDecision(
                LightBitMapping.SetSkyLight(currentLight, modLightLevel),
                currentSunlight, 0, 0, 0);
        }

        /// <summary>
        /// Evaluates a cross-chunk blocklight (RGB) modification.
        /// </summary>
        /// <param name="currentLight">The voxel's current packed ushort light value.</param>
        /// <param name="modR">The red blocklight channel the modification wants to set (0-15).</param>
        /// <param name="modG">The green blocklight channel the modification wants to set (0-15).</param>
        /// <param name="modB">The blue blocklight channel the modification wants to set (0-15).</param>
        /// <param name="isRemoval">True when the modification was emitted by a darkness/removal pass
        /// (zero channels mean "remove"); false for placement/edge-check mods (zero channels mean
        /// "no contribution" and may never lower the live value).</param>
        /// <param name="independentR">Strongest attenuated red an independent source still supplies the
        /// voxel — max of <see cref="InChunkBlocklightSupport"/> (Bug 11 analog) and
        /// <see cref="CrossChunkBlocklightSupport"/> (Bug 13 analog). Consulted only by removals (Bug 17).</param>
        /// <param name="independentG">Strongest attenuated green an independent source still supplies.</param>
        /// <param name="independentB">Strongest attenuated blue an independent source still supplies.</param>
        /// <returns>The apply decision, including the new light value and wake-up node old values.</returns>
        public static ApplyDecision ComputeBlocklight(ushort currentLight, byte modR, byte modG, byte modB, bool isRemoval,
            byte independentR = 0, byte independentG = 0, byte independentB = 0)
        {
            byte oldR = LightBitMapping.GetBlocklightR(currentLight);
            byte oldG = LightBitMapping.GetBlocklightG(currentLight);
            byte oldB = LightBitMapping.GetBlocklightB(currentLight);

            // Per-channel apply rule:
            // - Placement mods (BFS uplift, edge checks): channels only ever RAISE the live value.
            //   A zero channel means the emitting job had no light to contribute there — possibly
            //   a stale snapshot that never saw an independent source — never "remove"
            //   (Bug 07 secondary contributor).
            // - Removal mods (darkness waves): a zero channel is a genuine removal — but is VETOED when
            //   an independent source still supports the channel (the blocklight Bug 11/13 analog, Bug 17):
            //   clearing an independently-fed channel to 0 against a stale snapshot re-arms the cross-seam
            //   removal/re-light oscillation. A non-zero removal channel still MAX-merges so a stale
            //   snapshot cannot lower values owned by independent sources.
            byte applyR = ApplyRemovalChannel(oldR, modR, isRemoval, independentR);
            byte applyG = ApplyRemovalChannel(oldG, modG, isRemoval, independentG);
            byte applyB = ApplyRemovalChannel(oldB, modB, isRemoval, independentB);

            if (applyR == oldR && applyG == oldG && applyB == oldB)
            {
                return ApplyDecision.Skip;
            }

            // Wake-up node semantics (Bug 07 defect 1): the new light value is written to the live
            // data before the receiving chunk's next lighting job runs, so the wake node reports
            // old = 0 for every channel that did NOT lose light — the job's seeding then sees a
            // pure increase (anyIncreased) and re-spreads the uplift, instead of re-interpreting
            // the apply as a block removal and force-clearing the voxel. Only channels that
            // genuinely lost light report their real old value, launching the darkness wave with
            // the correct strength.
            byte wakeR = applyR < oldR ? oldR : (byte)0;
            byte wakeG = applyG < oldG ? oldG : (byte)0;
            byte wakeB = applyB < oldB ? oldB : (byte)0;
            byte wakeLevel = Max(wakeR, Max(wakeG, wakeB));

            return new ApplyDecision(
                LightBitMapping.SetBlocklightRGB(currentLight, applyR, applyG, applyB),
                wakeLevel, wakeR, wakeG, wakeB);
        }

        /// <summary>
        /// Per-channel apply for a blocklight mod: a placement (or a non-zero removal channel) MAX-merges;
        /// a genuine removal channel (removal mod, zero value) clears to 0 UNLESS an independent source
        /// still supports the current value, in which case the value is kept (the Bug 17 removal veto).
        /// </summary>
        /// <param name="oldC">The voxel's current value on this channel.</param>
        /// <param name="modC">The modification's value on this channel.</param>
        /// <param name="isRemoval">Whether the mod came from a darkness/removal pass.</param>
        /// <param name="independentC">The strongest attenuated value an independent source still supplies.</param>
        /// <returns>The value to store on this channel.</returns>
        private static byte ApplyRemovalChannel(byte oldC, byte modC, bool isRemoval, byte independentC)
        {
            if (isRemoval && modC == 0)
                return independentC >= oldC ? oldC : (byte)0;
            return Max(oldC, modC);
        }

        private static byte Max(byte a, byte b)
        {
            return a > b ? a : b;
        }
    }
}
