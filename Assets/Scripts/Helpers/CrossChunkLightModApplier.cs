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
        /// The cost of entering the voxel a cross-chunk modification targets, per face.
        /// <para>
        /// Two forms, because the support scans have two kinds of caller. <see cref="ForBlock"/> is what
        /// production uses: since <c>VO-3</c> a partial block's entry cost depends on which face the light
        /// arrives through, so a scalar cannot express it — a vertical slab charges its full opacity on the
        /// covered face and nothing on the open one. <see cref="Flat"/> charges one opacity in every
        /// direction, which is exactly the pre-VO-4 behaviour and remains correct for every full cube; it
        /// also lets a baseline ask "what would the support be if this voxel had opacity N" without
        /// synthesizing a block type.
        /// </para>
        /// </summary>
        public readonly struct TargetEntryCost
        {
            private readonly BlockTypeJobData _block;
            private readonly byte _meta;
            private readonly byte _flatOpacity;
            private readonly bool _directional;

            private TargetEntryCost(in BlockTypeJobData block, byte meta, byte flatOpacity, bool directional)
            {
                _block = block;
                _meta = meta;
                _flatOpacity = flatOpacity;
                _directional = directional;
            }

            /// <summary>
            /// A single opacity charged regardless of direction — the whole-block form.
            /// </summary>
            /// <param name="opacity">The entry cost to charge on every face.</param>
            /// <returns>A direction-independent entry cost.</returns>
            public static TargetEntryCost Flat(byte opacity)
            {
                return new TargetEntryCost(default, 0, opacity, directional: false);
            }

            /// <summary>
            /// The entry cost derived from the target block's own shape and orientation.
            /// </summary>
            /// <param name="block">The block the light is entering.</param>
            /// <param name="meta">That voxel's raw metadata byte (selects the volume's rotation).</param>
            /// <returns>A per-face entry cost that reduces to the block's opacity for full cubes.</returns>
            public static TargetEntryCost ForBlock(in BlockTypeJobData block, byte meta)
            {
                return new TargetEntryCost(in block, meta, block.Opacity, directional: true);
            }

            /// <summary>
            /// The opacity charged for light arriving through the given face of the target voxel.
            /// </summary>
            /// <param name="entryFace">The target's entry face, in <c>VoxelData.FaceChecks</c> order.</param>
            /// <returns>The entry cost (minimum 1 is applied later by <see cref="LightAttenuation.Attenuate"/>).</returns>
            public byte OpacityOnEntryThrough(int entryFace)
            {
                return _directional
                    ? LightAttenuation.EntryOpacity(in _block, _meta, entryFace)
                    : _flatOpacity;
            }
        }

        /// <summary>
        /// Whether a neighbor can deliver sky light to the voxel it is being scanned for — the veto's
        /// mirror of <c>NeighborhoodLightingJob.PropagateLight</c>'s two source guards, and the invariant
        /// <c>VO-4</c> exists to restore. A fully-opaque <i>cell</i> holds only non-propagable surface light
        /// (the §3.7 data-model gotcha); a partial block DOES re-propagate, but only through the faces its
        /// volume leaves open. Getting this looser than the BFS over-estimates support and vetoes legitimate
        /// removals (stable over-bright, the Bug 12 shape); getting it stricter under-estimates support and
        /// lets the removal initiator clear what the BFS immediately re-lights (the Bug 13 live-lock).
        /// </summary>
        /// <param name="neighborVoxel">The neighbor's packed voxel data.</param>
        /// <param name="exitFace">The neighbor's face pointing at the target, in <c>VoxelData.FaceChecks</c> order.</param>
        /// <param name="getBlockData">Lookup from a block id to its job data.</param>
        /// <returns>True when the neighbor is a valid propagation source through that face.</returns>
        public static bool NeighborCanDeliver(uint neighborVoxel, int exitFace, Func<ushort, BlockTypeJobData> getBlockData)
        {
            BlockTypeJobData block = getBlockData(BurstVoxelDataBitMapping.GetId(neighborVoxel));
            if (block.IsFullyOpaqueCell)
                return false;

            return !LightAttenuation.ExitBlocked(in block, BurstVoxelDataBitMapping.GetMeta(neighborVoxel), exitFace);
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
        /// Neighbors that cannot deliver are skipped — see <see cref="NeighborCanDeliver"/>. A fully-opaque
        /// block cannot propagate sunlight at all, yet it can still hold a high stored sky value (e.g. a
        /// sky-exposed roof block stores sky-top 15); counting that as support would over-estimate it and
        /// veto a legitimate removal. Semi-transparent neighbors (glass/leaves/water) DO propagate and are
        /// kept, and since VO-4 so does a partial block through the faces its volume leaves open.
        /// </para>
        /// </summary>
        /// <param name="chunk">The chunk receiving the cross-chunk modification.</param>
        /// <param name="localPos">The local voxel position the modification targets.</param>
        /// <param name="entryCost">The cost of entering the voxel at <paramref name="localPos"/> (the light
        /// enters this voxel, so it pays this voxel's opacity — minimum 1), per face.</param>
        /// <param name="getBlockData">Lookup from a block id to its job data. Supplied by the caller so this
        /// helper stays free of any block-database dependency; cache the delegate to avoid per-mod closures.</param>
        /// <returns>The maximum attenuated sky a same-chunk neighbor supports (0 if none).</returns>
        public static byte InChunkSunlightSupport(ChunkData chunk, Vector3Int localPos, TargetEntryCost entryCost,
            Func<ushort, BlockTypeJobData> getBlockData)
        {
            byte best = 0;
            for (int i = 0; i < 6; i++)
            {
                Vector3Int n = localPos + VoxelData.FaceChecks[i];
                if (n.x < 0 || n.x >= VoxelData.ChunkWidth ||
                    n.z < 0 || n.z >= VoxelData.ChunkWidth ||
                    n.y < 0 || n.y >= VoxelData.ChunkHeight)
                    continue; // cross-chunk (untrusted) or out of vertical range

                // The target's face toward this neighbor is i, so the neighbor's face toward the target —
                // the one the light leaves through — is the opposite one.
                byte s = LightBitMapping.GetSkyLight(chunk.GetLightData(n.x, n.y, n.z));
                byte support = LightAttenuation.Attenuate(s, entryCost.OpacityOnEntryThrough(i));
                if (support <= best)
                    continue; // can't improve the best support — skip the voxel read + source check

                if (!NeighborCanDeliver(chunk.GetVoxel(n.x, n.y, n.z), VoxelData.RevFaceChecksIndices[i], getBlockData))
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
        /// <param name="entryCost">The cost of entering the target voxel (minimum 1), per face.</param>
        /// <param name="emitterChunkOriginXZ">The voxel origin of the chunk whose job emitted the mod.</param>
        /// <param name="getLoadedChunk">Lookup from a chunk voxel origin (world XZ) to its live,
        /// populated <see cref="ChunkData"/>, or null when absent/unloaded. Supplied by the caller
        /// (world store vs. harness grid); cache the delegate to avoid per-mod closures.</param>
        /// <param name="getBlockData">Lookup from a block id to its job data.</param>
        /// <returns>The maximum attenuated sky a live third-party cross-chunk neighbor supports (0 if none).</returns>
        public static byte CrossChunkSunlightSupport(Vector2Int targetChunkOriginXZ, Vector3Int localPos,
            TargetEntryCost entryCost, Vector2Int emitterChunkOriginXZ,
            Func<Vector2Int, ChunkData> getLoadedChunk, Func<ushort, BlockTypeJobData> getBlockData)
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
                byte support = LightAttenuation.Attenuate(s, entryCost.OpacityOnEntryThrough(i));
                if (support <= best)
                    continue; // can't improve the best support — skip the voxel read + source check

                if (!NeighborCanDeliver(owner.GetVoxel(lx, n.y, lz), VoxelData.RevFaceChecksIndices[i], getBlockData))
                    continue;

                best = support;
            }

            return best;
        }

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
        /// <param name="neighborCanDeliver">Whether the live neighbor is a valid propagation source through
        /// the face it faces the center with (false for a fully-opaque cell, and since VO-4 also false for a
        /// partial block whose volume seals that face).</param>
        /// <param name="centerReceivesOnly">Whether the center holds a surface stamp rather than propagated
        /// light — a fully-opaque <i>cell</i>. A partial block re-propagates, so it takes the attenuated arm.</param>
        /// <param name="centerEntryOpacity">The center's entry cost through the face the light arrives on
        /// (minimum 1). Ignored when <paramref name="centerReceivesOnly"/> is set.</param>
        /// <param name="writtenSky">The sky level the pull-back wrote from the snapshot.</param>
        /// <returns>True when the live neighbor still supports the written level.</returns>
        public static bool PullBackClaimStillSupported(byte liveNeighborSky, bool neighborCanDeliver,
            bool centerReceivesOnly, byte centerEntryOpacity, byte writtenSky)
        {
            if (!neighborCanDeliver)
                return false;

            int support = centerReceivesOnly
                ? liveNeighborSky - 1
                : LightAttenuation.Attenuate(liveNeighborSky, centerEntryOpacity);
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
        /// <param name="entryCost">The target voxel's entry cost (minimum 1), per face.</param>
        /// <param name="getBlockData">Lookup from a block id to its job data.</param>
        /// <param name="suppR">Out: strongest attenuated red an in-chunk neighbor supplies.</param>
        /// <param name="suppG">Out: strongest attenuated green.</param>
        /// <param name="suppB">Out: strongest attenuated blue.</param>
        public static void InChunkBlocklightSupport(ChunkData chunk, Vector3Int localPos, TargetEntryCost entryCost,
            Func<ushort, BlockTypeJobData> getBlockData,
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
                    VoxelData.RevFaceChecksIndices[i], getBlockData, out byte nR, out byte nG, out byte nB);

                byte entryOpacity = entryCost.OpacityOnEntryThrough(i);
                byte sR = LightAttenuation.Attenuate(nR, entryOpacity);
                byte sG = LightAttenuation.Attenuate(nG, entryOpacity);
                byte sB = LightAttenuation.Attenuate(nB, entryOpacity);
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
        /// <param name="entryCost">The target voxel's entry cost (minimum 1), per face.</param>
        /// <param name="emitterChunkOriginXZ">The voxel origin of the chunk whose job emitted the mod.</param>
        /// <param name="getLoadedChunk">Lookup from a chunk voxel origin to its live loaded chunk, or null.</param>
        /// <param name="getBlockData">Lookup from a block id to its job data.</param>
        /// <param name="suppR">Out: strongest attenuated red a live third-party cross-chunk neighbor supplies.</param>
        /// <param name="suppG">Out: strongest attenuated green.</param>
        /// <param name="suppB">Out: strongest attenuated blue.</param>
        public static void CrossChunkBlocklightSupport(Vector2Int targetChunkOriginXZ, Vector3Int localPos,
            TargetEntryCost entryCost, Vector2Int emitterChunkOriginXZ,
            Func<Vector2Int, ChunkData> getLoadedChunk, Func<ushort, BlockTypeJobData> getBlockData,
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
                    VoxelData.RevFaceChecksIndices[i], getBlockData, out byte nR, out byte nG, out byte nB);

                byte entryOpacity = entryCost.OpacityOnEntryThrough(i);
                byte sR = LightAttenuation.Attenuate(nR, entryOpacity);
                byte sG = LightAttenuation.Attenuate(nG, entryOpacity);
                byte sB = LightAttenuation.Attenuate(nB, entryOpacity);
                if (sR > suppR) suppR = sR;
                if (sG > suppG) suppG = sG;
                if (sB > suppB) suppB = sB;
            }
        }

        /// <summary>
        /// Resolves a neighbor voxel's propagable blocklight per channel for the RGB removal veto: a
        /// fully-opaque cell propagates only its own emission (mirror of <c>PropagateLightRGB</c>'s
        /// opaque-source arm), a transparent/semi/partial block propagates its stored blocklight. A face
        /// the neighbor's own volume seals delivers nothing at all (VO-4).
        /// </summary>
        /// <param name="neighborVoxel">The neighbor's packed voxel data.</param>
        /// <param name="neighborLight">The neighbor's packed light value.</param>
        /// <param name="exitFace">The neighbor's face pointing at the target.</param>
        /// <param name="getBlockData">Lookup from a block id to its job data.</param>
        /// <param name="nR">Out: propagable red.</param>
        /// <param name="nG">Out: propagable green.</param>
        /// <param name="nB">Out: propagable blue.</param>
        private static void ResolveNeighborBlocklight(uint neighborVoxel, ushort neighborLight, int exitFace,
            Func<ushort, BlockTypeJobData> getBlockData, out byte nR, out byte nG, out byte nB)
        {
            BlockTypeJobData block = getBlockData(BurstVoxelDataBitMapping.GetId(neighborVoxel));
            byte meta = BurstVoxelDataBitMapping.GetMeta(neighborVoxel);

            if (block.IsFullyOpaqueCell)
            {
                // An opaque lamp still radiates its own emission across the seam; an opaque non-emissive
                // block contributes nothing, since its stored blocklight is received surface light.
                nR = block.EmissionR;
                nG = block.EmissionG;
                nB = block.EmissionB;
            }
            else if (LightAttenuation.ExitBlocked(in block, meta, exitFace))
            {
                nR = 0;
                nG = 0;
                nB = 0;
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
