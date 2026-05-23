using System.Collections.Generic;
using Data;
using Data.JobData;
using Data.NativeData;
using Helpers;
using Unity.Collections;

namespace Editor.WorldTools.Libraries
{
    /// <summary>
    /// Builds <see cref="JobDataManager"/> and <see cref="FluidVertexTemplatesNativeData"/> from a
    /// <see cref="BlockDatabase"/> asset without requiring a <c>World</c> instance.
    /// Mirrors the flattening logic in <c>World.InitializeJobData()</c>.
    /// </summary>
    public static class EditorJobDataManagerFactory
    {
        /// <summary>
        /// Creates persistent native job data from the given block database.
        /// The caller is responsible for disposing both returned objects.
        /// </summary>
        /// <param name="blockDatabase">The block database asset to flatten.</param>
        /// <returns>A tuple of the job data manager and fluid vertex template data.</returns>
        public static (JobDataManager jobDataManager, FluidVertexTemplatesNativeData fluidTemplates) Create(
            BlockDatabase blockDatabase)
        {
            // --- Step 1: Collect all unique custom mesh assets ---
            List<VoxelMeshData> uniqueCustomMeshes = new List<VoxelMeshData>();
            foreach (BlockType blockType in blockDatabase.blockTypes)
            {
                if (blockType.meshData != null && !uniqueCustomMeshes.Contains(blockType.meshData))
                {
                    uniqueCustomMeshes.Add(blockType.meshData);
                }
            }

            // --- Step 2: Flatten custom mesh data into temporary lists ---
            List<CustomMeshData> customMeshesList = new List<CustomMeshData>();
            List<CustomFaceData> customFacesList = new List<CustomFaceData>();
            List<CustomVertData> customVertsList = new List<CustomVertData>();
            List<int> customTrisList = new List<int>();

            foreach (VoxelMeshData meshAsset in uniqueCustomMeshes)
            {
                customMeshesList.Add(new CustomMeshData
                {
                    FaceStartIndex = customFacesList.Count,
                    FaceCount = meshAsset.faces.Length,
                });

                foreach (FaceMeshData faceAsset in meshAsset.faces)
                {
                    customFacesList.Add(new CustomFaceData
                    {
                        VertStartIndex = customVertsList.Count,
                        VertCount = faceAsset.vertData.Length,
                        TriStartIndex = customTrisList.Count,
                        TriCount = faceAsset.triangles.Length,
                    });

                    foreach (VertData vertAsset in faceAsset.vertData)
                    {
                        customVertsList.Add(new CustomVertData { Position = vertAsset.position, UV = vertAsset.uv });
                    }

                    customTrisList.AddRange(faceAsset.triangles);
                }
            }

            // --- Step 3: Convert lists to persistent NativeArrays ---
            NativeArray<CustomMeshData> customMeshesJobData =
                new NativeArray<CustomMeshData>(customMeshesList.ToArray(), Allocator.Persistent);
            NativeArray<CustomFaceData> customFacesJobData =
                new NativeArray<CustomFaceData>(customFacesList.ToArray(), Allocator.Persistent);
            NativeArray<CustomVertData> customVertsJobData =
                new NativeArray<CustomVertData>(customVertsList.ToArray(), Allocator.Persistent);
            NativeArray<int> customTrisJobData =
                new NativeArray<int>(customTrisList.ToArray(), Allocator.Persistent);

            // --- Step 4: Populate blockTypesJobData, including the custom mesh index ---
            NativeArray<BlockTypeJobData> blockTypesJobData =
                new NativeArray<BlockTypeJobData>(blockDatabase.blockTypes.Length, Allocator.Persistent);
            for (int i = 0; i < blockDatabase.blockTypes.Length; i++)
            {
                int customMeshIndex = -1;
                if (blockDatabase.blockTypes[i].meshData != null)
                {
                    customMeshIndex = uniqueCustomMeshes.IndexOf(blockDatabase.blockTypes[i].meshData);
                }

                blockTypesJobData[i] = new BlockTypeJobData(blockDatabase.blockTypes[i], customMeshIndex);
            }

            // --- Step 5: Create the final JobDataManager ---
            JobDataManager jobDataManager = new JobDataManager(
                blockTypesJobData,
                customMeshesJobData,
                customFacesJobData,
                customVertsJobData,
                customTrisJobData
            );

            // --- Step 6: Prepare Fluid Vertex Templates ---
            FluidTemplates fluidTemplates = ResourceLoader.LoadFluidTemplates();
            FluidVertexTemplatesNativeData fluidVertexTemplates = new FluidVertexTemplatesNativeData(fluidTemplates);

            return (jobDataManager, fluidVertexTemplates);
        }
    }
}
