using Unity.Collections;

namespace Data.JobData
{
    public class JobDataManager
    {
        // --- Public Readonly Fields ---
        public readonly NativeArray<BiomeAttributesJobData> BiomesJobData;
        public readonly NativeArray<LodeJobData> AllLodesJobData;
        public readonly NativeArray<BlockTypeJobData> BlockTypesJobData;
        public readonly NativeArray<CustomMeshData> CustomMeshesJobData;
        public readonly NativeArray<CustomFaceData> CustomFacesJobData;
        public readonly NativeArray<CustomVertData> CustomVertsJobData;
        public readonly NativeArray<int> CustomTrisJobData;


        // --- Constructor ---
        public JobDataManager(
            NativeArray<BiomeAttributesJobData> biomesJobData,
            NativeArray<LodeJobData> allLodesJobData,
            NativeArray<BlockTypeJobData> blockTypesJobData,
            NativeArray<CustomMeshData> customMeshesJobData,
            NativeArray<CustomFaceData> customFacesJobData,
            NativeArray<CustomVertData> customVertsJobData,
            NativeArray<int> customTrisJobData
        )
        {
            BiomesJobData = biomesJobData;
            AllLodesJobData = allLodesJobData;
            BlockTypesJobData = blockTypesJobData;
            CustomMeshesJobData = customMeshesJobData;
            CustomFacesJobData = customFacesJobData;
            CustomVertsJobData = customVertsJobData;
            CustomTrisJobData = customTrisJobData;
        }

        // --- Methods ---
        /// A helper to dispose all the containers at once
        public void Dispose()
        {
            if (BiomesJobData.IsCreated) BiomesJobData.Dispose();
            if (AllLodesJobData.IsCreated) AllLodesJobData.Dispose();
            if (BlockTypesJobData.IsCreated) BlockTypesJobData.Dispose();
            if (CustomMeshesJobData.IsCreated) CustomMeshesJobData.Dispose();
            if (CustomFacesJobData.IsCreated) CustomFacesJobData.Dispose();
            if (CustomVertsJobData.IsCreated) CustomVertsJobData.Dispose();
            if (CustomTrisJobData.IsCreated) CustomTrisJobData.Dispose();
        }
    }
}