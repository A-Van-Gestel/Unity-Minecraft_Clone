using Unity.Collections;

namespace Data.JobData
{
    /// <summary>
    /// Manages native arrays of world-type-agnostic game data required by the job system.
    /// Block types, custom meshes, and related rendering data live here.
    /// Biome and lode data are owned by each <see cref="Jobs.Generators.IChunkGenerator"/> implementation.
    /// </summary>
    public class JobDataManager
    {
        // --- Public Readonly Fields ---
        public readonly NativeArray<BlockTypeJobData> BlockTypesJobData;
        public readonly NativeArray<CustomMeshData> CustomMeshesJobData;
        public readonly NativeArray<CustomFaceData> CustomFacesJobData;
        public readonly NativeArray<CustomVertData> CustomVertsJobData;
        public readonly NativeArray<int> CustomTrisJobData;

        // --- Constructor ---

        /// <summary>
        /// Initializes a new instance of the <see cref="JobDataManager"/> class.
        /// </summary>
        /// <param name="blockTypesJobData">Native array of block type properties.</param>
        /// <param name="customMeshesJobData">Native array of custom mesh structures.</param>
        /// <param name="customFacesJobData">Native array tracking custom faces.</param>
        /// <param name="customVertsJobData">Native array of custom vertices.</param>
        /// <param name="customTrisJobData">Native array of custom triangles.</param>
        public JobDataManager(
            NativeArray<BlockTypeJobData> blockTypesJobData,
            NativeArray<CustomMeshData> customMeshesJobData,
            NativeArray<CustomFaceData> customFacesJobData,
            NativeArray<CustomVertData> customVertsJobData,
            NativeArray<int> customTrisJobData
        )
        {
            BlockTypesJobData = blockTypesJobData;
            CustomMeshesJobData = customMeshesJobData;
            CustomFacesJobData = customFacesJobData;
            CustomVertsJobData = customVertsJobData;
            CustomTrisJobData = customTrisJobData;
        }

        // --- Methods ---

        /// <summary>
        /// A helper to dispose all the containers at once.
        /// </summary>
        public void Dispose()
        {
            // Hoisted off the readonly fields so Dispose() runs without hidden defensive copies.
            NativeArray<BlockTypeJobData> blockTypes = BlockTypesJobData;
            NativeArray<CustomMeshData> customMeshes = CustomMeshesJobData;
            NativeArray<CustomFaceData> customFaces = CustomFacesJobData;
            NativeArray<CustomVertData> customVerts = CustomVertsJobData;
            NativeArray<int> customTris = CustomTrisJobData;

            if (blockTypes.IsCreated) blockTypes.Dispose();
            if (customMeshes.IsCreated) customMeshes.Dispose();
            if (customFaces.IsCreated) customFaces.Dispose();
            if (customVerts.IsCreated) customVerts.Dispose();
            if (customTris.IsCreated) customTris.Dispose();
        }
    }
}
