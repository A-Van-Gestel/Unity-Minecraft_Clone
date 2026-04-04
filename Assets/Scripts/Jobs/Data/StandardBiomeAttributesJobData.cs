namespace Jobs.Data
{
    /// <summary>
    /// Blittable, job-safe representation of <see cref="Data.WorldTypes.StandardBiomeAttributes"/>.
    /// Constructed by <c>StandardChunkGenerator.Initialize()</c> from the ScriptableObject array.
    /// Lodes are flattened into a shared <c>NativeArray&lt;StandardLodeJobData&gt;</c>, referenced by index range.
    /// </summary>
    public struct StandardBiomeAttributesJobData
    {
        /// <summary>Noise configuration for terrain height evaluation.</summary>
        public FastNoiseConfig TerrainNoiseConfig;

        /// <summary>Noise configuration for biome weight / selection.</summary>
        public FastNoiseConfig BiomeWeightNoiseConfig;

        /// <summary>Base height added to noise output.</summary>
        public float BaseTerrainHeight;

        /// <summary>
        /// Vertical multiplier for terrain noise. FastNoiseLite returns -1..1;
        /// multiply by this for physical height in blocks.
        /// </summary>
        public float TerrainAmplitude;

        /// <summary>Block ID for the surface layer (e.g., Grass).</summary>
        public byte SurfaceBlockID;

        /// <summary>Block ID for the sub-surface layers (e.g., Dirt).</summary>
        public byte SubSurfaceBlockID;

        /// <summary>If true, flora like trees or cacti will be generated in this biome.</summary>
        public bool EnableMajorFlora;

        /// <summary>Threshold for flora placement. Higher = fewer trees.</summary>
        public float MajorFloraPlacementThreshold;

        /// <summary>Flora type index dispatched to ExpandFlora (0 = tree, 1 = cactus, etc.).</summary>
        public byte MajorFloraIndex;

        /// <summary>Index into the shared NativeArray&lt;StandardLodeJobData&gt; owned by StandardChunkGenerator.</summary>
        public int LodeStartIndex;

        /// <summary>Number of lodes for this biome in the shared array.</summary>
        public int LodeCount;
    }
}
