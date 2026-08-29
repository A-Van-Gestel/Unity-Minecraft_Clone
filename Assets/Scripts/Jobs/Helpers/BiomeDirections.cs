using Unity.Mathematics;

namespace Jobs.Helpers
{
    /// <summary>
    /// Where each contributing biome lies from a column — the bearing companion to <see cref="BiomeWeights"/>,
    /// index-aligned with it (SOUND_ENGINE_DESIGN.md §10).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Offsets are in <b>voxel-space blocks</b> on the XZ plane, pointing from the sampled column toward the
    /// biome's <i>nearest cell center</i> — not its centroid. A biome's cells are scattered, so at a
    /// shoreline this reads correctly while standing deep inside a biome puts the nearest cell close by and
    /// makes its bearing swing as the listener walks. Consumers that place something at this bearing need
    /// smoothing; consumers that only want a direction can normalize and ignore the magnitude.
    /// </para>
    /// <para>
    /// Carried in <see cref="float4"/> pairs rather than an array of <see cref="float2"/> for the same reason
    /// <see cref="BiomeWeights"/> uses <see cref="int4"/>/<see cref="float4"/>: produced by Burst-compatible
    /// code, consumed on the main thread, blittable and allocation-free on both sides.
    /// </para>
    /// </remarks>
    public struct BiomeDirections
    {
        /// <summary>X of each contributor's offset, in blocks, index-aligned with <see cref="BiomeWeights.Indices"/>.</summary>
        public float4 OffsetsX;

        /// <inheritdoc cref="OffsetsX"/>
        public float4 OffsetsZ;

        /// <summary>
        /// One contributor's offset.
        /// </summary>
        /// <param name="slot">Contributor index, below <see cref="BiomeWeights.Count"/>.</param>
        /// <returns>The offset in blocks, XZ. Zero means no bearing is known for that slot.</returns>
        public float2 Offset(int slot) => new float2(OffsetsX[slot], OffsetsZ[slot]);
    }
}
