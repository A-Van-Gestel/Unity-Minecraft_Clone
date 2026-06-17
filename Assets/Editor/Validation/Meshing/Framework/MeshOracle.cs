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
        /// The within-cell UV corner each of a face's four vertices maps to, in the standard-cube
        /// mesher's emission order: vertex 0 = bottom-left, 1 = top-left, 2 = bottom-right, 3 = top-right.
        /// Hand-defined here (not read from the engine's <c>VoxelUvs</c> table) so a corruption of that
        /// table is caught rather than mirrored — the MH-4 analog of the MR-1 geometry oracle.
        /// </summary>
        private static readonly Vector2[] s_cornerUv =
        {
            new Vector2(0f, 0f), // vertex 0 — bottom-left
            new Vector2(0f, 1f), // vertex 1 — top-left
            new Vector2(1f, 0f), // vertex 2 — bottom-right
            new Vector2(1f, 1f), // vertex 3 — top-right
        };

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
        /// MH-4 — the geometry-face-index → texture-id convention the standard-cube mesher uses
        /// (face 0=Back, 1=Front, 2=Top, 3=Bottom, 4=Left, 5=Right), read from the block's per-face
        /// textures. Independently re-states the engine's <c>GetTextureID</c> selection spec so the UV
        /// oracle knows which atlas cell a given face must show; a divergent re-mapping in the engine is
        /// caught because this copy does not change with it.
        /// </summary>
        /// <param name="block">The block whose per-face texture ids to read.</param>
        /// <param name="faceIndex">Geometry face index (0-5).</param>
        /// <returns>The atlas texture id the given face must use.</returns>
        public static int ExpectedTextureIDForFace(in BlockTypeJobData block, int faceIndex) => faceIndex switch
        {
            0 => block.BackFaceTexture,
            1 => block.FrontFaceTexture,
            2 => block.TopFaceTexture,
            3 => block.BottomFaceTexture,
            4 => block.LeftFaceTexture,
            5 => block.RightFaceTexture,
            _ => 0,
        };

        /// <summary>
        /// MH-4 — the four atlas UV coordinates the standard-cube mesher must emit for a face textured
        /// with <paramref name="textureID"/>, in the mesher's per-vertex order (BL, TL, BR, TR). The
        /// atlas-cell placement — the math finding MR-2 may restructure — is re-derived here from the
        /// atlas dimensions rather than calling the engine's <c>AddTexture</c>, so this is an independent
        /// ground truth. The palette emits no UV quarter-turn rotation, so none is modelled (a rotated
        /// texture fixture would need its own oracle extension).
        /// </summary>
        /// <param name="textureID">Atlas texture index for the face.</param>
        /// <param name="expectedUVs">Output array (length 4) of expected UVs (xy = atlas coord, zw = 0).</param>
        public static void ExpectedFaceUVs(int textureID, Vector4[] expectedUVs)
        {
            int atlas = VoxelData.TextureAtlasSizeInBlocks;
            float norm = VoxelData.NormalizedBlockTextureSize;
            int cellX = textureID % atlas;
            int cellY = textureID / atlas;
            float uBase = cellX * norm;
            // The engine reads the atlas top-left-first, so cell row 0 sits at the TOP (v near 1).
            float vBase = 1f - cellY * norm - norm;

            for (int i = 0; i < 4; i++)
            {
                expectedUVs[i] = new Vector4(
                    uBase + norm * s_cornerUv[i].x,
                    vBase + norm * s_cornerUv[i].y,
                    0f, 0f);
            }
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
