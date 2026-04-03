namespace Legacy
{
    /// <summary>
    /// A job-safe, blittable representation of <see cref="LegacyLode"/>.
    /// Frozen — do not modify. Fields are semantically tied to <c>Mathf.PerlinNoise</c>.
    /// </summary>
    public struct LegacyLodeJobData
    {
        public readonly byte BlockID;
        public readonly int MinHeight;
        public readonly int MaxHeight;
        public readonly float Scale;
        public readonly float Threshold;
        public readonly float NoiseOffset;

        /// <summary>
        /// Constructor that creates LegacyLodeJobData from a <see cref="LegacyLode"/>.
        /// </summary>
        /// <param name="lode">The LegacyLode to copy properties from.</param>
        public LegacyLodeJobData(LegacyLode lode)
        {
            BlockID = lode.blockID;
            MinHeight = lode.minHeight;
            MaxHeight = lode.maxHeight;
            Scale = lode.scale;
            Threshold = lode.threshold;
            NoiseOffset = lode.noiseOffset;
        }
    }
}
