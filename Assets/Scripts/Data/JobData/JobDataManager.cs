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
        /// Whether <see cref="Dispose"/> has run. <b>The only reliable "are these arrays still usable" test
        /// this type offers</b> — see the remarks on <see cref="Dispose"/>.
        /// </summary>
        public bool IsDisposed { get; private set; }

        /// <summary>
        /// A helper to dispose all the containers at once. Safe to call more than once.
        /// </summary>
        /// <remarks>
        /// <b><see cref="NativeArray{T}.IsCreated"/> cannot tell whether these arrays are alive.</b> The
        /// fields are <c>readonly</c>, so the hoisting below disposes <i>copies</i>: the memory is freed, but
        /// each field keeps its pointer and reports <c>IsCreated == true</c> forever. Guard on
        /// <see cref="IsDisposed"/> instead — reading a disposed array throws in the editor and is undefined
        /// under IL2CPP.
        /// </remarks>
        public void Dispose()
        {
            if (IsDisposed) return;

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

            IsDisposed = true;
        }
    }
}
