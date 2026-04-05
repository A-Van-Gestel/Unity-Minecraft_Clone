using Data.WorldTypes;

namespace Jobs.Data
{
    /// <summary>
    /// Blittable, job-safe representation of a Standard lode (ore vein).
    /// Uses <see cref="FastNoiseConfig"/> instead of the legacy scale/threshold/noiseOffset triple,
    /// enabling full FastNoiseLite noise types (Cellular veins, fractal ridged, etc.).
    /// Free to evolve independently of the frozen LegacyLodeJobData.
    /// </summary>
    public struct StandardLodeJobData
    {
        public readonly byte BlockID;
        public readonly int MinHeight;
        public readonly int MaxHeight;
        public FastNoiseConfig NoiseConfig;

        /// <summary>
        /// Constructs a StandardLodeJobData from its authoring <see cref="Data.WorldTypes.StandardLode"/>.
        /// </summary>
        /// <param name="lode">The authoring lode data.</param>
        public StandardLodeJobData(StandardLode lode)
        {
            BlockID = (byte)lode.blockID;
            MinHeight = lode.minHeight;
            MaxHeight = lode.maxHeight;
            NoiseConfig = lode.noiseConfig;
        }
    }
}
