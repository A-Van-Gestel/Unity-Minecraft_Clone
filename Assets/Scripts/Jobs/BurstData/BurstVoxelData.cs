using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
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

        /// <summary>
        /// Precomputed neighbor offsets for smooth lighting corner averaging.
        /// Layout: [faceIndex * 12 + cornerIndex * 3 + offsetIndex] = 72 entries.
        /// offsetIndex 0 = SideA, 1 = SideB, 2 = Diagonal.
        /// All offsets are from the block position (include the face normal component).
        /// The direct neighbor (FaceChecks[face]) is NOT stored — it's the same for all 4 corners.
        /// </summary>
        public static readonly SharedStatic<NativeArray<int3>> CornerOffsets = SharedStatic<NativeArray<int3>>.GetOrCreate<BurstVoxelData, CornerOffsetsKey>();

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

        private struct CornerOffsetsKey
        {
        }

        // This method is called automatically when the game loads or when the editor reloads.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
#if UNITY_EDITOR
        [InitializeOnLoadMethod]
#endif
        public static void Initialize()
        {
            Dispose(); // Clean up any old data before initializing

            // Allocate persistent memory for our arrays, as they will exist for the lifetime of the app.
            NativeArray<Vector3> voxelVerts = new NativeArray<Vector3>(VoxelData.VoxelVerts, Allocator.Persistent);
            NativeArray<int> voxelTris = new NativeArray<int>(VoxelData.VoxelTris, Allocator.Persistent);
            NativeArray<Vector2> voxelUvs = new NativeArray<Vector2>(VoxelData.VoxelUvs, Allocator.Persistent);
            NativeArray<Vector3Int> faceChecks = new NativeArray<Vector3Int>(VoxelData.FaceChecks, Allocator.Persistent);

            // Build the smooth lighting corner offset LUT from VoxelVerts/VoxelTris/FaceChecks.
            NativeArray<int3> cornerOffsets = new NativeArray<int3>(72, Allocator.Persistent);
            BuildCornerOffsetLUT(cornerOffsets);

            // Assign the created NativeArrays to our SharedStatic fields.
            VoxelVerts.Data = voxelVerts;
            VoxelTris.Data = voxelTris;
            VoxelUvs.Data = voxelUvs;
            FaceChecks.Data = faceChecks;
            CornerOffsets.Data = cornerOffsets;

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
            if (CornerOffsets.Data.IsCreated) CornerOffsets.Data.Dispose();
        }

        /// <summary>
        /// Builds the 72-entry corner offset LUT for smooth lighting.
        /// For each face (6) and each corner vertex (4), computes the 3 neighbor offsets
        /// (SideA, SideB, Diagonal) relative to the block position.
        /// </summary>
        private static void BuildCornerOffsetLUT(NativeArray<int3> offsets)
        {
            for (int face = 0; face < 6; face++)
            {
                Vector3Int normal = VoxelData.FaceChecks[face];
                int3 n = new int3(normal.x, normal.y, normal.z);
                int3 absN = math.abs(n);

                // Determine which two axes are perpendicular to the face normal.
                // perpMask has 1 on perp axes, 0 on the normal axis.
                int3 perpMask = new int3(1, 1, 1) - absN;

                // Identify the two perpendicular axis indices for SideA/SideB split.
                // Convention: first perp axis = lower axis index, second = higher.
                int axisA = -1, axisB = -1;
                for (int a = 0; a < 3; a++)
                {
                    if (perpMask[a] == 1)
                    {
                        if (axisA < 0) axisA = a;
                        else axisB = a;
                    }
                }

                for (int corner = 0; corner < 4; corner++)
                {
                    // Get the vertex position for this face/corner from VoxelTris/VoxelVerts.
                    int vertIndex = VoxelData.VoxelTris[face * 4 + corner];
                    Vector3 vertPos = VoxelData.VoxelVerts[vertIndex];

                    // Determine the sign on each perpendicular axis:
                    // vertex component 0 → offset -1, vertex component 1 → offset +1
                    int3 cornerSign = new int3(
                        vertPos.x > 0.5f ? 1 : -1,
                        vertPos.y > 0.5f ? 1 : -1,
                        vertPos.z > 0.5f ? 1 : -1
                    );

                    // Build SideA: face normal + sign on first perp axis only
                    int3 sideA = n;
                    sideA[axisA] = cornerSign[axisA];

                    // Build SideB: face normal + sign on second perp axis only
                    int3 sideB = n;
                    sideB[axisB] = cornerSign[axisB];

                    // Build Diagonal: face normal + sign on both perp axes
                    int3 diag = n;
                    diag[axisA] = cornerSign[axisA];
                    diag[axisB] = cornerSign[axisB];

                    int baseIdx = face * 12 + corner * 3;
                    offsets[baseIdx + 0] = sideA;
                    offsets[baseIdx + 1] = sideB;
                    offsets[baseIdx + 2] = diag;
                }
            }
        }
    }
}
