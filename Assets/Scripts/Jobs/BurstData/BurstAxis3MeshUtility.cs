using System.Runtime.CompilerServices;
using Data;
using Unity.Burst;

namespace Jobs.BurstData
{
    /// <summary>
    /// Burst-safe meshing helpers for blocks declared with <see cref="MetadataSchema.Axis3"/>.
    /// </summary>
    /// <remarks>
    /// <para>For an Axis3 block (logs, pillars, fallen trunks), the cube's vertex geometry is
    /// unchanged — what differs per axis is which texture face is shown on each world face.
    /// This utility encodes the frozen per-axis face-remap rule so the meshing job can do an
    /// O(1) lookup instead of running per-voxel quaternion rotation (per
    /// <c>PER_BLOCK_METADATA_SCHEMAS.md §8.1</c> meshing note).</para>
    /// <para><b>Convention</b>: the "top" of the log faces in the direction of its named axis.
    /// <list type="bullet">
    ///   <item><description><see cref="BurstVoxelMetadataUtility.AXIS_Y"/>: top of log → world +Y (face 2). Identity remap.</description></item>
    ///   <item><description><see cref="BurstVoxelMetadataUtility.AXIS_X"/>: top of log → world +X (face 5, "Right/East").</description></item>
    ///   <item><description><see cref="BurstVoxelMetadataUtility.AXIS_Z"/>: top of log → world +Z (face 1, "Front/North").</description></item>
    /// </list></para>
    /// <para>This convention is <b>frozen</b> — once shipped, the chunk migration that converts
    /// legacy orientation values to Axis3 axis values (Phase 2d, save version v6) depends on it.
    /// Any change to these mappings requires a new save version + migration step per
    /// <c>PER_BLOCK_METADATA_SCHEMAS.md §9.6</c>.</para>
    /// </remarks>
    [BurstCompile]
    public static class BurstAxis3MeshUtility
    {
        /// <summary>
        /// Frozen face-remap LUT, flat-indexed by <c>axis * 6 + worldFace</c>. Each entry tells the
        /// meshing job which <i>block</i> face index to read the texture from when emitting a given
        /// <i>world</i> face for an Axis3 block.
        /// </summary>
        /// <remarks>
        /// World face indices follow the standard convention used elsewhere in the meshing job:
        /// <c>0=Back(-Z), 1=Front(+Z), 2=Top(+Y), 3=Bottom(-Y), 4=Left(-X), 5=Right(+X)</c>.
        /// </remarks>
        private static readonly byte[] s_faceRemap =
        {
            // axis 0 (Y): identity. Top of log at +Y.
            //  worldBack worldFront worldTop worldBottom worldLeft worldRight
            0, 1, 2, 3, 4, 5,

            // axis 1 (X): top of log at +X (right). Block face 2 (top tex) shows on world face 5.
            //   World back/front (Z faces) show block back/front (sides — unchanged).
            //   World top/bottom (Y faces) now show block left/right (sides).
            //   World left/right (X faces) now show block bottom/top (the log's caps).
            0, 1, 4, 5, 3, 2,

            // axis 2 (Z): top of log at +Z (front). Block face 2 (top tex) shows on world face 1.
            //   World back/front (Z faces) show block bottom/top (the log's caps).
            //   World top/bottom (Y faces) now show block back/front (sides).
            //   World left/right (X faces) unchanged (sides).
            3, 2, 0, 1, 4, 5,
        };

        /// <summary>
        /// Frozen per-axis UV rotation LUT, flat-indexed by <c>axis * 6 + worldFace</c>.
        /// Each entry stores clockwise quarter-turns (0-3) applied to the canonical face UVs so
        /// bark grain follows the log's long axis on sideways logs.
        /// </summary>
        private static readonly byte[] s_uvQuarterTurnsCW =
        {
            // axis 0 (Y): identity.
            0, 0, 0, 0, 0, 0,

            // axis 1 (X): derived from rotating the canonical upright cube -90° around Z.
            3, 1, 3, 1, 1, 3,

            // axis 2 (Z): derived from rotating the canonical upright cube +90° around X.
            2, 2, 0, 0, 1, 3,
        };

        /// <summary>
        /// Looks up the effective block face index for a given (axis, world face) pair.
        /// </summary>
        /// <param name="axis">The Axis3 value: 0 = Y, 1 = X, 2 = Z (per <see cref="BurstVoxelMetadataUtility"/>).</param>
        /// <param name="worldFace">The world face index 0-5 the meshing job is currently emitting.</param>
        /// <returns>The block face index 0-5 whose texture should be sampled for the world face.</returns>
        /// <remarks>
        /// Burst-safe O(1) array lookup. Hot-path callable. Inputs are not validated — callers must
        /// supply <paramref name="axis"/> in 0-2 and <paramref name="worldFace"/> in 0-5.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte GetEffectiveFace(byte axis, int worldFace)
        {
            return s_faceRemap[axis * 6 + worldFace];
        }

        /// <summary>
        /// Returns clockwise quarter-turns (0-3) applied to the canonical face UVs for the given
        /// Axis3 axis and world face.
        /// </summary>
        /// <param name="axis">The Axis3 value: 0 = Y, 1 = X, 2 = Z.</param>
        /// <param name="worldFace">The world face index 0-5 the meshing job is currently emitting.</param>
        /// <returns>Clockwise quarter-turn count in the range 0-3.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte GetUvQuarterTurnsCW(byte axis, int worldFace)
        {
            return s_uvQuarterTurnsCW[axis * 6 + worldFace];
        }
    }
}
