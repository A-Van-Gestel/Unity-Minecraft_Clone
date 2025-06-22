using Unity.Burst;
using Unity.Collections;
using UnityEngine;

namespace Jobs
{
    [BurstCompile]
    public class BurstVoxelData
    {
        // These SharedStatic fields will hold our data in a way that Burst can access.
        public static readonly SharedStatic<NativeArray<Vector3>> VoxelVerts = SharedStatic<NativeArray<Vector3>>.GetOrCreate<BurstVoxelData, VoxelVertsKey>();
        public static readonly SharedStatic<NativeArray<int>> VoxelTris = SharedStatic<NativeArray<int>>.GetOrCreate<BurstVoxelData, VoxelTrisKey>();
        public static readonly SharedStatic<NativeArray<Vector2>> VoxelUvs = SharedStatic<NativeArray<Vector2>>.GetOrCreate<BurstVoxelData, VoxelUvsKey>();
        public static readonly SharedStatic<NativeArray<Vector3Int>> FaceChecks = SharedStatic<NativeArray<Vector3Int>>.GetOrCreate<BurstVoxelData, FaceChecksKey>();

        // These empty structs are just unique keys for the SharedStatic fields.
        private class VoxelVertsKey {}
        private class VoxelTrisKey {}
        private class VoxelUvsKey {}
        private class FaceChecksKey {}

        // This method is called automatically when the game loads.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        public static void Initialize()
        {
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
            Application.quitting += Dispose;
        }
    
        // It's crucial to dispose of NativeArrays to prevent memory leaks.
        // This method is called when the application closes or the editor recompiles.
        private static void Dispose()
        {
            // Check if the data has been created before trying to dispose it.
            if (VoxelVerts.Data.IsCreated) VoxelVerts.Data.Dispose();
            if (VoxelTris.Data.IsCreated) VoxelTris.Data.Dispose();
            if (VoxelUvs.Data.IsCreated) VoxelUvs.Data.Dispose();
            if (FaceChecks.Data.IsCreated) FaceChecks.Data.Dispose();
        
            // It's also good practice to unsubscribe after the event has fired, though not strictly necessary on quit.
            Application.quitting -= Dispose;
        }
    }
}