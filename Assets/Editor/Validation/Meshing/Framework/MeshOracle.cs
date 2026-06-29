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
            const int atlas = VoxelData.TextureAtlasSizeInBlocks;
            const float norm = VoxelData.NormalizedBlockTextureSize;
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
        /// MH-3 — the per-vertex smooth-light <see cref="Color32"/> the engine must emit for a face whose
        /// entire sampled neighborhood holds one uniform light level per channel (each 0-15). This is the
        /// <i>limiting case</i> of the engine's corner averaging: when all four samples a corner reads
        /// (direct + sideA + sideB + diagonal) are equal, the result is independent of <b>which</b>
        /// neighbors are sampled, so the oracle never references the engine's <c>CornerOffsets</c> LUT —
        /// deliberately, to avoid mirroring the engine's own sampling assumption (the A4 shared-assumption
        /// trap). The encoding is re-derived by hand: average of four equal values <c>V</c> is <c>V</c>,
        /// and the engine's UNorm8 map <c>(4V*17 + 2)/4</c> reduces to exactly <c>17V</c> (<c>68V</c> is
        /// always divisible by 4, so the <c>+2</c> rounding never carries). The output channel order
        /// matches the engine's <c>LightData</c>: <c>(sun, blockR, blockG, blockB)</c>.
        /// <para>
        /// <b>Scope:</b> only the uniform (all-corners-equal) case is modelled, which pins the smooth-light
        /// <i>encoding</i> MR-2 must preserve. Distinct-per-corner values and AO darkening (a corner whose
        /// diagonal is dropped because both its sides are opaque) are NOT modelled here — predicting which
        /// corner darkens requires re-deriving <c>CornerOffsets</c>, the A4 trap. A future extension should
        /// add a per-corner oracle (needed to fully guard MR-8's "merge only equal-corner-light faces"
        /// predicate); see the MH-3 entry in <c>MESHING_VALIDATION_HARNESS_FIDELITY.md</c>.
        /// </para>
        /// </summary>
        /// <param name="sky">Uniform sky-light level across the neighborhood (0-15).</param>
        /// <param name="blockR">Uniform red blocklight level (0-15).</param>
        /// <param name="blockG">Uniform green blocklight level (0-15).</param>
        /// <param name="blockB">Uniform blue blocklight level (0-15).</param>
        /// <returns>The <see cref="Color32"/> every emitted vertex must carry in <c>LightData</c>.</returns>
        public static Color32 ExpectedUniformCornerLight(byte sky, byte blockR, byte blockG, byte blockB)
        {
            return new Color32((byte)(17 * sky), (byte)(17 * blockR), (byte)(17 * blockG), (byte)(17 * blockB));
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
