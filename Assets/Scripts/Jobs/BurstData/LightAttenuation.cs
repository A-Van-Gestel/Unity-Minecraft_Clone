using System.Runtime.CompilerServices;
using Unity.Mathematics;

namespace Jobs.BurstData
{
    /// <summary>
    /// The single definition of the engine's light attenuation rule, shared by every system that must
    /// agree on it: the BFS flood-fill (<c>NeighborhoodLightingJob</c>), the borderless validation oracle
    /// (<c>LightingOracle</c>), and the cross-chunk sunlight removal veto
    /// (<c>Helpers.CrossChunkLightModApplier.InChunkSunlightSupport</c>).
    /// <para>
    /// Burst-compatible (uses only <see cref="Unity.Mathematics"/>), so the job can call it directly.
    /// Keeping the formula in one place prevents the three call sites from silently diverging — a
    /// divergence would make the cross-chunk veto over- or under-estimate support relative to the BFS.
    /// </para>
    /// </summary>
    public static class LightAttenuation
    {
        /// <summary>
        /// The light level remaining after light travels from a source into a destination voxel, charged
        /// the destination's opacity on entry. Uses the Starlight/Moonrise formula
        /// <c>max(0, sourceLight - max(1, opacity))</c>: air (opacity 0) costs 1 level, semi-transparent
        /// blocks cost their opacity, and a fully-opaque destination (opacity ≥ 15) receives 0.
        /// </summary>
        /// <param name="sourceLight">The light level at the source (0-15).</param>
        /// <param name="opacity">The opacity of the voxel the light is entering (the entry cost, minimum 1).</param>
        /// <returns>The attenuated light level (0-15).</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte Attenuate(int sourceLight, byte opacity)
        {
            return (byte)math.max(0, sourceLight - math.max(1, opacity));
        }
    }
}
