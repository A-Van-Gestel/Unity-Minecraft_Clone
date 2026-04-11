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

        /// <summary>How far this biome's height influence extends beyond its Voronoi edge.</summary>
        public float BlendRadius;

        /// <summary>Base height added to noise output.</summary>
        public float BaseTerrainHeight;

        /// <summary>
        /// Vertical multiplier for terrain noise. FastNoiseLite returns -1..1;
        /// multiply by this for physical height in blocks.
        /// </summary>
        public float TerrainAmplitude;

        /// <summary>Block ID for the surface layer (e.g., Grass).</summary>
        public byte SurfaceBlockID;

        /// <summary>Block ID to substitute the surface layer with if generating below sea level (e.g. Sand).</summary>
        public byte UnderwaterSurfaceBlockID;

        /// <summary>If true, flora like trees or cacti will be generated in this biome.</summary>
        public bool EnableMajorFlora;

        /// <summary>Percentage of the biome covered by flora zones. Larger = larger zones, 1.0 = entire biome is a zone.</summary>
        public float MajorFloraZoneCoverage;

        /// <summary>The minimum grid size for flora. Smaller = denser forest, Larger = sparser forest.</summary>
        public int MajorFloraPlacementSpacing;

        /// <summary>Minimum empty blocks to maintain between the tree and the grid cell edges.</summary>
        public int MajorFloraPlacementPadding;

        /// <summary>Probability that a valid spacing slot will actually spawn a tree.</summary>
        public float MajorFloraPlacementChance;

        /// <summary>The absolute lowest Y level a tree root is allowed to spawn on.</summary>
        public int MajorFloraPlacementMinHeight;

        /// <summary>The absolute highest Y level a tree root is allowed to spawn on.</summary>
        public int MajorFloraPlacementMaxHeight;

        /// <summary>The physical minimum size/height of the tree/cactus being generated.</summary>
        public int MajorFloraMinPhysicalHeight;

        /// <summary>The physical maximum size/height of the tree/cactus being generated.</summary>
        public int MajorFloraMaxPhysicalHeight;

        /// <summary>Flora type index dispatched to ExpandFlora (0 = tree, 1 = cactus, etc.).</summary>
        public byte MajorFloraIndex;

        /// <summary>Index into the shared NativeArray&lt;StandardTerrainLayerJobData&gt; owned by StandardChunkGenerator.</summary>
        public int TerrainLayerStartIndex;

        /// <summary>Number of terrain layers for this biome in the shared array.</summary>
        public int TerrainLayerCount;

        /// <summary>Index into the shared NativeArray&lt;StandardLodeJobData&gt; owned by StandardChunkGenerator.</summary>
        public int LodeStartIndex;

        /// <summary>Number of lodes for this biome in the shared array.</summary>
        public int LodeCount;

        /// <summary>Index into the shared NativeArray&lt;StandardCaveLayerJobData&gt; owned by StandardChunkGenerator.</summary>
        public int CaveLayerStartIndex;

        /// <summary>Number of cave layers for this biome in the shared array.</summary>
        public int CaveLayerCount;
    }
}
