using Data;
using Helpers;
using Jobs.BurstData;
using UnityEngine;

namespace Editor.Validation.Meshing.Framework
{
    /// <summary>
    /// Independent specification for the geometry of a standard cube face — the domain of finding
    /// MR-1 (the per-vertex <c>Quaternion.Euler</c> hoist in
    /// <see cref="VoxelMeshHelper.GenerateStandardCubeFace"/>).
    /// <para>
    /// The oracle deliberately computes the rotated vertex positions with
    /// <see cref="Quaternion.Euler(float,float,float)"/> + a vector multiply — the <i>current</i>
    /// (pre-MR-1) ground-truth formula whose output the optimization must preserve bit-for-bit.
    /// After MR-1 replaces that with a precomputed rotation, any divergence between the engine's
    /// emitted vertices and this oracle is a regression. The oracle reuses the
    /// <see cref="VoxelHelper"/> face-translation / rotation-angle tables because MR-1 does not
    /// change them — only the vertex rotation math.
    /// </para>
    /// </summary>
    public static class MeshOracle
    {
        /// <summary>The block center the standard cube rotates about, matching the engine.</summary>
        private static readonly Vector3 s_center = new Vector3(0.5f, 0.5f, 0.5f);

        /// <summary>
        /// Computes the four world-space vertices and the normal that
        /// <see cref="VoxelMeshHelper.GenerateStandardCubeFace"/> must emit for the given face,
        /// Y-rotation, and block position.
        /// </summary>
        /// <param name="faceIndex">Geometry face index (0-5) passed to the emitter.</param>
        /// <param name="rotation">Y-axis rotation in degrees (0/90/180/270).</param>
        /// <param name="pos">Block position in chunk-local space.</param>
        /// <param name="verts">Output array (length 4) of expected vertex positions.</param>
        /// <param name="normal">Output expected face normal (unrotated, per the engine).</param>
        public static void ExpectedStandardCubeFace(int faceIndex, float rotation, Vector3Int pos,
            Vector3[] verts, out Vector3 normal)
        {
            for (int i = 0; i < 4; i++)
            {
                int vertIndex = BurstVoxelData.VoxelTris.Data[faceIndex * 4 + i];
                Vector3 vertPos = BurstVoxelData.VoxelVerts.Data[vertIndex];
                Vector3 direction = Quaternion.Euler(0f, rotation, 0f) * (vertPos - s_center);
                verts[i] = pos + direction + s_center;
            }

            // The engine adds the un-rotated face normal (FaceChecks[faceIndex]); the rotation is
            // already baked into the face index for oriented cubes.
            Vector3Int n = BurstVoxelData.FaceChecks.Data[faceIndex];
            normal = new Vector3(n.x, n.y, n.z);
        }

        /// <summary>
        /// Maps a <see cref="MetadataSchema.HorizontalOnly"/> yaw (0=N,1=S,2=W,3=E) to the legacy
        /// orientation index the meshing job converts it to, mirroring
        /// <c>GenerateStandardCubeMesh_HorizontalOnly</c>.
        /// </summary>
        public static byte LegacyOrientationForYaw(byte yaw)
        {
            return yaw switch
            {
                0 => VoxelOrientation.North,
                1 => VoxelOrientation.South,
                2 => VoxelOrientation.West,
                3 => VoxelOrientation.East,
                _ => VoxelOrientation.North,
            };
        }

        /// <summary>The Y-rotation in degrees applied to a HorizontalOnly cube with the given yaw.</summary>
        public static float RotationForYaw(byte yaw) => VoxelHelper.GetRotationAngle(LegacyOrientationForYaw(yaw));

        /// <summary>The geometry face index the engine emits for a world face on an oriented cube.</summary>
        public static int TranslatedFace(int worldFace, byte legacyOrientation)
            => VoxelHelper.GetTranslatedFaceIndex(worldFace, legacyOrientation);
    }
}
