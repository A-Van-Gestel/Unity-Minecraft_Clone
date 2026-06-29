using System.Collections.Generic;
using Data;
using Data.JobData;
using Data.WorldTypes;
using Jobs.Data;
using UnityEngine;

namespace Jobs.Generators
{
    /// <summary>
    /// Render modes for the in-game terrain generation debug minimap.
    /// </summary>
    public enum TerrainDebugRenderMode
    {
        BiomeVoronoi,
        BiomeBorderFade,
        BlendedHeightmap,
        CombinedDensitySlice,
    }

    /// <summary>
    /// Diagnostic data returned by <see cref="IChunkGenerator.GetTerrainDebugInfo"/>.
    /// Main-thread only — not Burst-safe (contains managed string).
    /// </summary>
    public struct TerrainDebugInfo
    {
        public bool IsValid;
        public int BiomeIndex;
        public string BiomeName;
        public float BlendedTerrainHeight;
        public float BorderFade;
        public float DensityAmplitude;
        public float EffectiveDensityAmplitude;
        public bool Enable3DDensity;
        public float BlendRadius;
        public float BlendWeight;
    }

    /// <summary>
    /// Abstraction for chunk generation strategies. Each world type provides its own implementation.
    /// The active generator is owned by <see cref="WorldJobManager"/> and delegates all scheduling,
    /// synchronous voxel queries, and flora expansion through this interface.
    /// </summary>
    public interface IChunkGenerator
    {
        /// <summary>
        /// Controls which optional generation passes (caves, lodes, water) are executed.
        /// Defaults to <see cref="GenerationFeatureFlags.Default"/> (all passes enabled).
        /// Set before calling <see cref="ScheduleGeneration"/> to take effect.
        /// </summary>
        GenerationFeatureFlags FeatureFlags { get; set; }

        /// <summary>
        /// Injects explicit dependencies required for generation.
        /// Called once during WorldJobManager construction.
        /// </summary>
        /// <param name="seed">The deterministic world seed.</param>
        /// <param name="worldType">The ScriptableObject containing biome configuration.</param>
        /// <param name="globalJobData">World-type-agnostic data (Blocks, Meshes, etc.).</param>
        /// <param name="isSingleBiomeMode">Whether to override Voronoi selection with a single biome.</param>
        /// <param name="selectedBiome">The single biome to use (when isSingleBiomeMode is true).</param>
        void Initialize(int seed, WorldTypeDefinition worldType, JobDataManager globalJobData, bool isSingleBiomeMode = false, StandardBiomeAttributes selectedBiome = null);

        /// <summary>
        /// Schedules the generation job and returns a populated GenerationJobData struct.
        /// </summary>
        /// <param name="coord">The chunk coordinate to generate.</param>
        /// <param name="activeVoxelPool">Optional pool for the per-chunk active-voxel
        /// <see cref="GenerationJobData.ActiveVoxels"/> list (TG-6). When supplied, the generator rents from
        /// it and marks <see cref="GenerationJobData.ActiveVoxelsFromPool"/> so the caller returns the list to
        /// the pool instead of disposing it. When null (editor / preview / benchmark paths) the list is
        /// freshly allocated and freed by <see cref="GenerationJobData.Dispose"/>. Generators that do not run
        /// the active-voxel scan (e.g. the legacy generator) ignore it.</param>
        /// <returns>A <see cref="GenerationJobData"/> containing the job handle and output containers.</returns>
        GenerationJobData ScheduleGeneration(ChunkCoord coord, global::Helpers.ActiveVoxelListPool activeVoxelPool = null);

        /// <summary>
        /// Synchronous main-thread voxel query. Used by World.GetHighestVoxel and spawn-point logic.
        /// </summary>
        /// <param name="globalPos">The global voxel position to query.</param>
        /// <returns>The block ID at the given position.</returns>
        byte GetVoxel(Vector3Int globalPos);

        /// <summary>
        /// Expands a structure spawn marker (queued by the generation job) into a full
        /// set of VoxelMods (trunk + leaves, cactus body, etc.).
        /// Called on the main thread during WorldJobManager.ProcessGenerationJobs().
        /// Each generator owns its own structure templates and random strategy.
        /// </summary>
        /// <param name="marker">The spawn marker as queued by the generation job.
        /// Contains the global position and the pool entry index.</param>
        /// <returns>An enumerable of VoxelMods representing the full structure.</returns>
        IEnumerable<VoxelMod> ExpandStructure(StructureSpawnMarker marker);

        /// <summary>
        /// Returns terrain generation diagnostic data at the given column.
        /// Main-thread only, used by <see cref="DebugScreen"/> for runtime inspection.
        /// </summary>
        /// <param name="globalX">Global X coordinate.</param>
        /// <param name="globalZ">Global Z coordinate.</param>
        /// <returns>Diagnostic data for the terrain at this column.</returns>
        TerrainDebugInfo GetTerrainDebugInfo(int globalX, int globalZ);

        /// <summary>
        /// Evaluates a batch of pixels for the terrain debug minimap.
        /// Writes RGBA32 color data for each coordinate into the output array.
        /// </summary>
        /// <param name="startIndex">First pixel index to evaluate this frame.</param>
        /// <param name="count">Number of pixels to evaluate.</param>
        /// <param name="textureSize">Width/height of the square texture.</param>
        /// <param name="originX">World X coordinate of the texture's bottom-left corner.</param>
        /// <param name="originZ">World Z coordinate of the texture's bottom-left corner.</param>
        /// <param name="scale">World blocks per pixel.</param>
        /// <param name="mode">Which data channel to render.</param>
        /// <param name="biomeCount">Total number of biomes (for color mapping).</param>
        /// <param name="sliceY">Y level for density slice mode.</param>
        /// <param name="outputPixels">RGBA32 byte array (length = textureSize * textureSize * 4).</param>
        void EvaluateTerrainDebugPixels(int startIndex, int count, int textureSize,
            int originX, int originZ, int scale, TerrainDebugRenderMode mode,
            int biomeCount, int sliceY, byte[] outputPixels);

        /// <summary>
        /// Disposes of any internal NativeArrays allocated during Initialize.
        /// </summary>
        void Dispose();
    }
}
