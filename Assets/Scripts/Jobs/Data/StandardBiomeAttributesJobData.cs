using System.Runtime.InteropServices;
using Data.WorldTypes;

namespace Jobs.Data
{
    /// <summary>
    /// Blittable, job-safe representation of <see cref="StandardBiomeAttributes"/>.
    /// Constructed by <c>StandardChunkGenerator.Initialize()</c> from the ScriptableObject array.
    /// Lodes are flattened into a shared <c>NativeArray&lt;StandardLodeJobData&gt;</c>, referenced by index range.
    /// </summary>
    public struct StandardBiomeAttributesJobData
    {
        /// <summary>Width of the transition zone at Voronoi boundaries. Larger = wider, more gradual blending.</summary>
        public float BlendRadius;

        /// <summary>
        /// Multiplier controlling how strongly this biome's height bleeds into neighbors during blending.
        /// 1.0 = full influence (default). Lower values suppress outward height contribution.
        /// </summary>
        public float BlendWeight;

        /// <summary>Interpolation curve shape applied to this biome's weight at Voronoi boundaries.</summary>
        public BlendCurve BlendCurve;

        /// <summary>Width of the transition zone for surface blocks at Voronoi boundaries. Larger = wider, more gradual blending.</summary>
        public float SurfaceBlockDitheringWidth;

        /// <summary>Base height added to noise output.</summary>
        public float BaseTerrainHeight;

        /// <summary>Block ID for the surface layer (e.g., Grass).</summary>
        public byte SurfaceBlockID;

        /// <summary>Block ID to substitute the surface layer with if generating below sea level (e.g. Sand).</summary>
        public byte UnderwaterSurfaceBlockID;

        /// <summary>Percentage of the biome covered by flora zones. Larger = larger zones, 1.0 = entire biome is a zone.</summary>
        public float FloraZoneCoverage;

        /// <summary>Index into the shared NativeArray&lt;StructurePoolEntryJobData&gt; for this biome's major flora pool.</summary>
        public int MajorFloraPoolStartIndex;

        /// <summary>Number of major flora pool entries for this biome.</summary>
        public int MajorFloraPoolCount;

        /// <summary>Index into the shared NativeArray&lt;StructurePoolEntryJobData&gt; for this biome's minor flora pool.</summary>
        public int MinorFloraPoolStartIndex;

        /// <summary>Number of minor flora pool entries for this biome.</summary>
        public int MinorFloraPoolCount;

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

        /// <summary>Whether to evaluate 3D density noise for volumetric terrain.</summary>
        [MarshalAs(UnmanagedType.U1)]
        public bool Enable3DDensity;

        /// <summary>Max height variation of 3D noise. Defines the Dynamic Density Band bounds.</summary>
        public float DensityAmplitude;

        /// <summary>Whether to apply domain warping to density noise coordinates.</summary>
        [MarshalAs(UnmanagedType.U1)]
        public bool EnableDensityWarp;
    }
}
