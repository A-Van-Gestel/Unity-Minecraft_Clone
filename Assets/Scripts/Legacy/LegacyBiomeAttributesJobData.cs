namespace Legacy
{
    /// <summary>
    /// A job-safe, blittable representation of <see cref="LegacyBiomeAttributes"/>.
    /// Frozen — do not modify. Extracted from the shared Data/JobData.cs to isolate
    /// the legacy generation path. Fields are semantically tied to <c>Mathf.PerlinNoise</c>.
    /// </summary>
    public struct LegacyBiomeAttributesJobData
    {
        public readonly int Offset;
        public readonly float Scale;
        public readonly int TerrainHeight;
        public readonly float TerrainScale;
        public readonly byte SurfaceBlock;
        public readonly byte SubSurfaceBlock;
        public readonly bool PlaceMajorFlora;
        public readonly int MajorFloraIndex;
        public readonly float MajorFloraZoneScale;
        public readonly float MajorFloraZoneThreshold;
        public readonly float MajorFloraPlacementScale;
        public readonly float MajorFloraPlacementThreshold;
        public int MaxHeight;
        public int MinHeight;

        public readonly int LodeStartIndex;
        public readonly int LodeCount;

        /// <summary>
        /// Constructor that creates LegacyBiomeAttributesJobData from a LegacyBiomeAttributes ScriptableObject.
        /// </summary>
        /// <param name="biomeAttributes">The LegacyBiomeAttributes to copy properties from.</param>
        /// <param name="currentLodeIndex">The <see cref="LegacyLodeJobData"/> index of the first lode in the biome.</param>
        public LegacyBiomeAttributesJobData(LegacyBiomeAttributes biomeAttributes, int currentLodeIndex)
        {
            Offset = biomeAttributes.offset;
            Scale = biomeAttributes.scale;
            TerrainHeight = biomeAttributes.terrainHeight;
            TerrainScale = biomeAttributes.terrainScale;
            SurfaceBlock = biomeAttributes.surfaceBlock;
            SubSurfaceBlock = biomeAttributes.subSurfaceBlock;
            PlaceMajorFlora = biomeAttributes.placeMajorFlora;
            MajorFloraIndex = biomeAttributes.majorFloraIndex;
            MajorFloraZoneScale = biomeAttributes.majorFloraZoneScale;
            MajorFloraZoneThreshold = biomeAttributes.majorFloraZoneThreshold;
            MajorFloraPlacementScale = biomeAttributes.majorFloraPlacementScale;
            MajorFloraPlacementThreshold = biomeAttributes.majorFloraPlacementThreshold;
            MaxHeight = biomeAttributes.maxHeight;
            MinHeight = biomeAttributes.minHeight;
            LodeStartIndex = currentLodeIndex;
            LodeCount = biomeAttributes.lodes.Length;
        }
    }
}
