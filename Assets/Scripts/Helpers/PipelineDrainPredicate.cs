using Data;

namespace Helpers
{
    /// <summary>
    /// P-4's tail-inclusive drain test: the chunk pipeline has finished everything it can finish around the player.
    /// The single definition of "drained", so every measurement that waits for it measures the same thing.
    /// </summary>
    /// <remarks>
    /// In-flight mesh jobs are the whole mesh tail since MP-6 (the apply and the load animation happen when the job
    /// merges), so they are included. The mesh <i>build</i> queue and the lighting <i>waiting</i> set are excluded on
    /// purpose: their steady state is the load square's perimeter ring, which can never be served because its outer
    /// neighbors are not loaded.
    /// </remarks>
    public static class PipelineDrainPredicate
    {
        /// <summary>Whether the generation queue, generation jobs, lighting ready-set, lighting jobs and mesh jobs are all empty.</summary>
        /// <param name="world">The live world.</param>
        /// <returns>True when no stage holds work.</returns>
        public static bool AreStagesDrained(World world) =>
            world.GenerationRequestQueueCount == 0
            && world.JobManager.GenerationJobs.Count == 0
            && world.LightWorkReadyCount == 0
            && world.JobManager.LightingJobs.Count == 0
            && world.JobManager.MeshJobs.Count == 0;

        /// <summary>Whether every chunk of the load square around the player is populated.</summary>
        /// <param name="world">The live world.</param>
        /// <returns>True when the square is fully populated.</returns>
        public static bool IsLoadSquarePopulated(World world)
        {
            ChunkCoord center = world.PlayerChunkCoord;
            int loadDist = world.settings.LoadDistance;

            for (int dx = -loadDist; dx <= loadDist; dx++)
            {
                for (int dz = -loadDist; dz <= loadDist; dz++)
                {
                    if (!world.worldData.TryGetChunk(center.Neighbor(dx, dz).ToVoxelOrigin(), out ChunkData data)
                        || !data.IsPopulated)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>Whether the pipeline is drained and the load square populated; the square is walked only once the stages are empty.</summary>
        /// <param name="world">The live world.</param>
        /// <returns>True when nothing is left to finish around the player.</returns>
        public static bool IsDrained(World world) => AreStagesDrained(world) && IsLoadSquarePopulated(world);
    }
}
