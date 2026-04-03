using System.Collections.Generic;
using Data;
using Data.JobData;
using Data.WorldTypes;
using Jobs.Data;
using UnityEngine;

namespace Jobs.Generators
{
    /// <summary>
    /// Abstraction for chunk generation strategies. Each world type provides its own implementation.
    /// The active generator is owned by <see cref="WorldJobManager"/> and delegates all scheduling,
    /// synchronous voxel queries, and flora expansion through this interface.
    /// </summary>
    public interface IChunkGenerator
    {
        /// <summary>
        /// Injects explicit dependencies required for generation.
        /// Called once during WorldJobManager construction.
        /// </summary>
        /// <param name="seed">The deterministic world seed.</param>
        /// <param name="worldType">The ScriptableObject containing biome configuration.</param>
        /// <param name="globalJobData">World-type-agnostic data (Blocks, Meshes, etc.).</param>
        void Initialize(int seed, WorldTypeDefinition worldType, JobDataManager globalJobData);

        /// <summary>
        /// Schedules the generation job and returns a populated GenerationJobData struct.
        /// </summary>
        /// <param name="coord">The chunk coordinate to generate.</param>
        /// <returns>A <see cref="GenerationJobData"/> containing the job handle and output containers.</returns>
        GenerationJobData ScheduleGeneration(ChunkCoord coord);

        /// <summary>
        /// Synchronous main-thread voxel query. Used by World.GetHighestVoxel and spawn-point logic.
        /// </summary>
        /// <param name="globalPos">The global voxel position to query.</param>
        /// <returns>The block ID at the given position.</returns>
        byte GetVoxel(Vector3Int globalPos);

        /// <summary>
        /// Expands a flora root point (queued by the generation job) into a full
        /// set of VoxelMods (trunk + leaves, cactus body, etc.).
        /// Called on the main thread during WorldJobManager.ProcessGenerationJobs().
        /// Each generator owns its own noise/random strategy for trunk height determination,
        /// ensuring legacy worlds use Mathf.PerlinNoise and standard worlds use Unity.Mathematics.Random.
        /// </summary>
        /// <param name="rootMod">The flora root VoxelMod as queued by the generation job.
        /// The ID field encodes the flora type index (0 = tree, 1 = cactus, etc.).</param>
        /// <returns>An enumerable of VoxelMods representing the full flora structure.</returns>
        IEnumerable<VoxelMod> ExpandFlora(VoxelMod rootMod);

        /// <summary>
        /// Disposes of any internal NativeArrays allocated during Initialize.
        /// </summary>
        void Dispose();
    }
}
