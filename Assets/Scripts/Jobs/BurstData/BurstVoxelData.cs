using Unity.Burst;
using Unity.Collections;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Jobs.BurstData
{
    /// <summary>
    /// Serves as a globally accessible NativeArray storage container for standard voxel mesh generation primitive data.
    /// Used directly inside Burst-compiled Job contexts to prevent expensive data layout transitions from managed memory to unmanaged memory.
    /// Automatically manages its own <see cref="UnityEngine.Application.quitting"/> and <see cref="UnityEditor.AssemblyReloadEvents"/> lifecycles.
    /// </summary>
    [BurstCompile]
    public class BurstVoxelData
    {
        // These SharedStatic fields will hold our data in a way that Burst can access.
        public static readonly SharedStatic<NativeArray<Vector3>> VoxelVerts = SharedStatic<NativeArray<Vector3>>.GetOrCreate<BurstVoxelData, VoxelVertsKey>();
        public static readonly SharedStatic<NativeArray<int>> VoxelTris = SharedStatic<NativeArray<int>>.GetOrCreate<BurstVoxelData, VoxelTrisKey>();
        public static readonly SharedStatic<NativeArray<Vector2>> VoxelUvs = SharedStatic<NativeArray<Vector2>>.GetOrCreate<BurstVoxelData, VoxelUvsKey>();
        public static readonly SharedStatic<NativeArray<Vector3Int>> FaceChecks = SharedStatic<NativeArray<Vector3Int>>.GetOrCreate<BurstVoxelData, FaceChecksKey>();

        // These empty structs are just unique keys for the SharedStatic fields.
        private struct VoxelVertsKey
        {
        }

        private struct VoxelTrisKey
        {
        }

        private struct VoxelUvsKey
        {
        }

        private struct FaceChecksKey
        {
        }

        // This method is called automatically when the game loads.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        public static void Initialize()
        {
            Dispose(); // Clean up any old data before initializing

            // Allocate persistent memory for our arrays, as they will exist for the lifetime of the app.
            var voxelVerts = new NativeArray<Vector3>(VoxelData.VoxelVerts, Allocator.Persistent);
            var voxelTris = new NativeArray<int>(VoxelData.VoxelTris, Allocator.Persistent);
            var voxelUvs = new NativeArray<Vector2>(VoxelData.VoxelUvs, Allocator.Persistent);
            var faceChecks = new NativeArray<Vector3Int>(VoxelData.FaceChecks, Allocator.Persistent);

            // Assign the created NativeArrays to our SharedStatic fields.
            VoxelVerts.Data = voxelVerts;
            VoxelTris.Data = voxelTris;
            VoxelUvs.Data = voxelUvs;
            FaceChecks.Data = faceChecks;

            // Subscribe our Dispose method to the Application.quitting event.
            // This ensures our native memory is cleaned up when the game closes.
            // We unsubscribe first to prevent duplicate subscriptions during editor domain reloads.
            Application.quitting -= Dispose;
            // Hook into the editor's assembly reload event to dispose data correctly.
#if UNITY_EDITOR
            AssemblyReloadEvents.beforeAssemblyReload += Dispose;
#endif
        }

        // It's crucial to dispose of NativeArrays to prevent memory leaks.
        // This method is called when the application closes or the editor recompiles.
        private static void Dispose()
        {
            // Unsubscribe from events to prevent memory leaks in the editor
            Application.quitting -= Dispose;
#if UNITY_EDITOR
            AssemblyReloadEvents.beforeAssemblyReload -= Dispose;
#endif


            // Check if the data has been created before trying to dispose it.
            if (VoxelVerts.Data.IsCreated) VoxelVerts.Data.Dispose();
            if (VoxelTris.Data.IsCreated) VoxelTris.Data.Dispose();
            if (VoxelUvs.Data.IsCreated) VoxelUvs.Data.Dispose();
            if (FaceChecks.Data.IsCreated) FaceChecks.Data.Dispose();
        }
    }
}
