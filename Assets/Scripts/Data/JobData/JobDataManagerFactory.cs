using System.Collections.Generic;
using Data.NativeData;
using Helpers;
using Unity.Collections;
using UnityEngine;

namespace Data.JobData
{
    /// <summary>
    /// Flattens a <see cref="BlockDatabase"/> asset into the native job-data structures the meshing and
    /// lighting jobs read — without requiring a live <c>World</c> instance.
    /// <para>This is the single source of truth for that flatten logic, shared by the runtime
    /// (<c>World.PrepareGlobalJobData</c>), editor tools (<c>EditorJobDataManagerFactory</c>), and the
    /// OM-1 startup calibrator. It lives in the runtime assembly so the editor assembly can depend on it
    /// (the reverse is not allowed).</para>
    /// </summary>
    public static class JobDataManagerFactory
    {
        /// <summary>
        /// Builds the native job data and the flat active-voxel lookup from the given block database.
        /// The caller owns the returned native containers and must dispose them
        /// (<see cref="JobDataManager.Dispose"/> + <see cref="FluidVertexTemplatesNativeData.Dispose"/>).
        /// </summary>
        /// <param name="blockDatabase">The block database asset to flatten.</param>
        /// <returns>The assembled <see cref="GlobalJobData"/> (job data, fluid templates, active lookup).</returns>
        public static GlobalJobData Create(BlockDatabase blockDatabase)
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

                if (meshAsset.faces.Length > 6)
                    Debug.LogWarning($"VoxelMeshData asset '{meshAsset.name}' has more than 6 faces. Only the first 6 will be used.");

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
            NativeArray<CustomMeshData> customMeshesJobData = NativeArrayHelper.ToNativeArray(customMeshesList);
            NativeArray<CustomFaceData> customFacesJobData = NativeArrayHelper.ToNativeArray(customFacesList);
            NativeArray<CustomVertData> customVertsJobData = NativeArrayHelper.ToNativeArray(customVertsList);
            NativeArray<int> customTrisJobData = NativeArrayHelper.ToNativeArray(customTrisList);

            // --- Step 4: Populate blockTypesJobData, including the custom mesh index ---
            NativeArray<BlockTypeJobData> blockTypesJobData =
                new NativeArray<BlockTypeJobData>(blockDatabase.blockTypes.Length, Allocator.Persistent);

            // Precomputed flat isActive lookup for the fallback active-voxel scan (load / pool-replay paths).
            // Co-built in the loop below from the same BlockType.isActive source as blockTypesJobData[i].IsActive,
            // so the two active-voxel scan paths (Chunk.OnDataPopulated vs Jobs.ActiveVoxelScanJob) cannot
            // disagree on the active criterion — keep them built together.
            bool[] isActiveById = new bool[blockDatabase.blockTypes.Length];

            for (int i = 0; i < blockDatabase.blockTypes.Length; i++)
            {
                int customMeshIndex = -1;
                if (blockDatabase.blockTypes[i].meshData != null)
                {
                    customMeshIndex = uniqueCustomMeshes.IndexOf(blockDatabase.blockTypes[i].meshData);
                }

                blockTypesJobData[i] = new BlockTypeJobData(blockDatabase.blockTypes[i], customMeshIndex);
                isActiveById[i] = blockDatabase.blockTypes[i].isActive;
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

            return new GlobalJobData(jobDataManager, fluidVertexTemplates, isActiveById);
        }
    }

    /// <summary>
    /// The world-agnostic job data assembled by <see cref="JobDataManagerFactory.Create"/>:
    /// the native <see cref="JobData.JobDataManager"/>, the fluid vertex templates, and the flat
    /// <see cref="IsActiveById"/> active-voxel lookup. The caller owns and disposes the native containers.
    /// </summary>
    public readonly struct GlobalJobData
    {
        /// <summary>Native block-type / custom-mesh job data.</summary>
        public readonly JobDataManager JobDataManager;

        /// <summary>Native water/lava vertex templates.</summary>
        public readonly FluidVertexTemplatesNativeData FluidVertexTemplates;

        /// <summary>Flat <c>blockId → isActive</c> lookup for the fallback active-voxel scan.</summary>
        public readonly bool[] IsActiveById;

        /// <summary>Initializes the assembled job-data bundle.</summary>
        /// <param name="jobDataManager">Native block/custom-mesh job data.</param>
        /// <param name="fluidVertexTemplates">Native fluid vertex templates.</param>
        /// <param name="isActiveById">Flat active-voxel lookup keyed by block id.</param>
        public GlobalJobData(
            JobDataManager jobDataManager,
            FluidVertexTemplatesNativeData fluidVertexTemplates,
            bool[] isActiveById)
        {
            JobDataManager = jobDataManager;
            FluidVertexTemplates = fluidVertexTemplates;
            IsActiveById = isActiveById;
        }
    }
}
