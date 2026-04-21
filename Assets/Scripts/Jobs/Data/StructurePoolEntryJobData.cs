namespace Jobs.Data
{
    /// <summary>
    /// Blittable, Burst-safe representation of a structure pool entry's placement parameters.
    /// Constructed by <c>StandardChunkGenerator.Initialize()</c> from the authoring
    /// <see cref="Data.Structures.StructurePoolEntry"/> structs on each biome.
    /// Each entry runs its own independent placement grid pass in the generation job.
    /// </summary>
    public struct StructurePoolEntryJobData
    {
        /// <summary>The minimum grid size for this structure type. Smaller = denser placement.</summary>
        public int Spacing;

        /// <summary>Minimum empty blocks from grid cell edges. -1 = automatic.</summary>
        public int Padding;

        /// <summary>Probability [0,1] that an elected grid cell actually spawns this structure.</summary>
        public float Chance;

        /// <summary>Structure will only spawn if the surface Y is at or above this value.</summary>
        public int MinPlacementHeight;

        /// <summary>Structure will only spawn if the surface Y is at or below this value.</summary>
        public int MaxPlacementHeight;

        /// <summary>If true, this entry only spawns inside the biome's flora zone noise region.</summary>
        public bool UseFloraZone;

        /// <summary>
        /// Index into the per-entry override flora zone noise array.
        /// -1 = use the biome's default flora zone noise instead.
        /// </summary>
        public int FloraZoneNoiseIndex;

        /// <summary>
        /// Per-entry override for the flora zone coverage threshold.
        /// Only used when <see cref="FloraZoneNoiseIndex"/> >= 0.
        /// </summary>
        public float FloraZoneCoverage;
    }
}
