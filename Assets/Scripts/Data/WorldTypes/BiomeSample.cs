namespace Data.WorldTypes
{
    /// <summary>
    /// The answer to "which biome is at this column", resolved on the main thread. Carries the
    /// authored biome asset alongside its index so consumers (ambience beds, weather, the debug
    /// readout) read biome data directly instead of each re-deriving a lookup from an index.
    /// </summary>
    public readonly struct BiomeSample
    {
        /// <summary>
        /// The primary Voronoi cell's biome index — what terrain height, caves and features are
        /// generated from, and what "the biome you are in" means to a player.
        /// </summary>
        public readonly int Index;

        /// <summary>
        /// The dithered index that chose the surface block actually underfoot. Equal to
        /// <see cref="Index"/> except within the few blocks either side of a biome boundary, where
        /// the surface pass jitters its sample. Consumers wanting to match what the player is
        /// standing on read this; consumers picking an ambience bed or a weather type read
        /// <see cref="Index"/>, which does not flicker along a boundary.
        /// </summary>
        /// <remarks>
        /// <b>Approximate, unlike <see cref="Index"/>.</b> The jitter rides
        /// <c>Unity.Mathematics.noise.snoise</c>, whose Burst codegen is not bit-identical to the managed
        /// one this query runs under, so on roughly 0.4% of columns — those whose jittered sample lands
        /// near a Voronoi cell edge — this reports a different biome than the generator actually placed.
        /// Fine for a readout or an ambience hint; do not use it to decide what block is really there
        /// (read the voxel), and do not feed it into anything that must agree with generation.
        /// The Biome Selection suite's B11 baseline pins the divergence rate.
        /// </remarks>
        public readonly int SurfaceIndex;

        /// <summary>The primary biome's authored display name.</summary>
        public readonly string Name;

        /// <summary>
        /// The primary biome's authored asset. Cast to the world type's concrete biome type to read
        /// generation or presentation fields.
        /// </summary>
        public readonly BiomeBase Attributes;

        /// <summary>Builds a sample. See the field docs for the two indices.</summary>
        /// <param name="index">Primary Voronoi biome index.</param>
        /// <param name="surfaceIndex">Dithered surface biome index.</param>
        /// <param name="attributes">The primary biome's authored asset.</param>
        public BiomeSample(int index, int surfaceIndex, BiomeBase attributes)
        {
            Index = index;
            SurfaceIndex = surfaceIndex;
            Attributes = attributes;
            Name = attributes != null ? attributes.biomeName : null;
        }
    }
}
