using Jobs;
using Jobs.BurstData;

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
        public static ApplyDecision Compute(ushort currentLight, in LightModification mod)
        {
            return mod.Channel == LightChannel.Sun
                ? ComputeSunlight(currentLight, mod.LightLevel)
                : ComputeBlocklight(currentLight, mod.BlockR, mod.BlockG, mod.BlockB);
        }

        /// <summary>
        /// Evaluates a cross-chunk sunlight modification.
        /// </summary>
        /// <param name="currentLight">The voxel's current packed ushort light value.</param>
        /// <param name="modLightLevel">The sunlight level the modification wants to set (0-15).</param>
        /// <returns>The apply decision, including the new light value and wake-up node old values.</returns>
        public static ApplyDecision ComputeSunlight(ushort currentLight, byte modLightLevel)
        {
            byte currentSunlight = LightBitMapping.GetSkyLight(currentLight);

            // Guard: Cross-chunk BFS mods are computed against a STALE snapshot of
            // the neighbor's data (taken before the neighbor's own lighting pass).
            // This means a mod might try to set sunlight to a value LOWER than what
            // the neighbor's own column recalculation has already computed.
            //
            // Rule: Non-zero cross-chunk sunlight mods may only INCREASE light.
            // - Uplift mods (from PropagateLight): must be >= current to apply.
            // - Darkness removal mods (level=0, from PropagateDarkness): always apply
            //   so that block removal/placement propagates correctly across borders.
            if (modLightLevel > 0 && modLightLevel < currentSunlight)
            {
                return ApplyDecision.Skip;
            }

            if (currentSunlight == modLightLevel)
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
        /// <returns>The apply decision, including the new light value and wake-up node old values.</returns>
        public static ApplyDecision ComputeBlocklight(ushort currentLight, byte modR, byte modG, byte modB)
        {
            byte oldLevel = LightBitMapping.GetMaxBlocklight(currentLight);
            byte oldR = LightBitMapping.GetBlocklightR(currentLight);
            byte oldG = LightBitMapping.GetBlocklightG(currentLight);
            byte oldB = LightBitMapping.GetBlocklightB(currentLight);

            // Per-channel MAX guard (mirrors the sunlight guard above):
            // Non-zero mod channels use MAX to prevent stale-snapshot mods
            // from reducing values set by independent light sources.
            // Zero channels pass through for darkness removal.
            byte applyR = modR == 0 ? (byte)0 : Max(oldR, modR);
            byte applyG = modG == 0 ? (byte)0 : Max(oldG, modG);
            byte applyB = modB == 0 ? (byte)0 : Max(oldB, modB);

            if (applyR == oldR && applyG == oldG && applyB == oldB)
            {
                return ApplyDecision.Skip;
            }

            return new ApplyDecision(
                LightBitMapping.SetBlocklightRGB(currentLight, applyR, applyG, applyB),
                oldLevel, oldR, oldG, oldB);
        }

        private static byte Max(byte a, byte b)
        {
            return a > b ? a : b;
        }
    }
}
