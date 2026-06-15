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
        public static ApplyDecision Compute(ushort currentLight, in LightModification mod, byte inChunkSunlightSupport = 0)
        {
            return mod.Channel == LightChannel.Sun
                ? ComputeSunlight(currentLight, mod.LightLevel, inChunkSunlightSupport)
                : ComputeBlocklight(currentLight, mod.BlockR, mod.BlockG, mod.BlockB, mod.IsRemoval);
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
        /// Attenuation matches <c>NeighborhoodLightingJob.AttenuateLight</c> (and the cross-chunk
        /// <c>CheckEdgeVoxel</c> guard): light is charged the <b>destination</b> voxel's opacity on entry,
        /// <c>max(1, targetOpacity)</c> per step. Passing the flat air cost (opacity ≤ 1) would
        /// over-estimate support into semi-transparent media and wrongly veto a legitimate removal,
        /// leaving stale over-bright light until a full relight.
        /// </para>
        /// </summary>
        /// <param name="chunk">The chunk receiving the cross-chunk modification.</param>
        /// <param name="localPos">The local voxel position the modification targets.</param>
        /// <param name="targetOpacity">The opacity of the voxel at <paramref name="localPos"/> (the light
        /// enters this voxel, so it pays this voxel's opacity — minimum 1).</param>
        /// <returns>The maximum attenuated sky a same-chunk neighbor supports (0 if none).</returns>
        public static byte InChunkSunlightSupport(ChunkData chunk, Vector3Int localPos, byte targetOpacity)
        {
            byte best = 0;
            int cost = Mathf.Max(1, targetOpacity); // mirrors AttenuateLight: max(1, opacity)
            for (int i = 0; i < 6; i++)
            {
                Vector3Int n = localPos + VoxelData.FaceChecks[i];
                if (n.x < 0 || n.x >= VoxelData.ChunkWidth ||
                    n.z < 0 || n.z >= VoxelData.ChunkWidth ||
                    n.y < 0 || n.y >= VoxelData.ChunkHeight)
                    continue; // cross-chunk (untrusted) or out of vertical range

                byte s = LightBitMapping.GetSkyLight(chunk.GetLightData(n.x, n.y, n.z));
                if (s > cost && s - cost > best)
                    best = (byte)(s - cost);
            }

            return best;
        }

        /// <summary>
        /// Evaluates a cross-chunk sunlight modification.
        /// </summary>
        /// <param name="currentLight">The voxel's current packed ushort light value.</param>
        /// <param name="modLightLevel">The sunlight level the modification wants to set (0-15).</param>
        /// <returns>The apply decision, including the new light value and wake-up node old values.</returns>
        public static ApplyDecision ComputeSunlight(ushort currentLight, byte modLightLevel, byte inChunkSunlightSupport = 0)
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

            // Bug 11 guard: a cross-chunk sunlight removal (level 0) must not clobber a voxel that a
            // neighbor INSIDE the receiving chunk independently supports. The emitting chunk computed
            // this removal against a stale snapshot of the receiver; when two adjacent chunks remove
            // each other's shared seam column in the same wave (e.g. both reloaded mid-darkness-wave),
            // forcing the receiver's freshly re-lit, independently-supported value back to 0 re-arms the
            // cycle forever (the sunlight removal/re-placement oscillation that stalls reloaded worlds).
            // An in-chunk source still supplying >= the current value means the value is NOT dependent on
            // the removed cross-chunk light, so the removal is spurious and is skipped; a genuinely
            // dependent voxel (no in-chunk support) still clears, preserving legitimate cross-chunk darkness.
            if (modLightLevel == 0 && currentSunlight > 0 && inChunkSunlightSupport >= currentSunlight)
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
        /// <returns>The apply decision, including the new light value and wake-up node old values.</returns>
        public static ApplyDecision ComputeBlocklight(ushort currentLight, byte modR, byte modG, byte modB, bool isRemoval)
        {
            byte oldR = LightBitMapping.GetBlocklightR(currentLight);
            byte oldG = LightBitMapping.GetBlocklightG(currentLight);
            byte oldB = LightBitMapping.GetBlocklightB(currentLight);

            // Per-channel apply rule:
            // - Placement mods (BFS uplift, edge checks): channels only ever RAISE the live value.
            //   A zero channel means the emitting job had no light to contribute there — possibly
            //   a stale snapshot that never saw an independent source — never "remove"
            //   (Bug 07 secondary contributor).
            // - Removal mods (darkness waves): a zero channel is a genuine removal and passes
            //   through; non-zero channels still MAX-merge so a stale snapshot cannot lower
            //   values owned by independent light sources.
            byte applyR = isRemoval && modR == 0 ? (byte)0 : Max(oldR, modR);
            byte applyG = isRemoval && modG == 0 ? (byte)0 : Max(oldG, modG);
            byte applyB = isRemoval && modB == 0 ? (byte)0 : Max(oldB, modB);

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

        private static byte Max(byte a, byte b)
        {
            return a > b ? a : b;
        }
    }
}
