using System.Runtime.CompilerServices;
using Data;
using Unity.Burst;

namespace Jobs.BurstData
{
    /// <summary>
    /// Burst-safe meshing helpers for blocks declared with <see cref="MetadataSchema.Facing6Roll2"/>.
    /// </summary>
    /// <remarks>
    /// <para>For a Facing6Roll2 block, the block can be oriented in any of 6 directions, and then
    /// rolled around its front-facing axis in 4 quarter-turn steps. This results in 24 possible
    /// orientations.</para>
    /// <para>This utility composes the <see cref="BurstFacing6MeshUtility"/> facing remap with a
    /// secondary roll remap.</para>
    /// </remarks>
    [BurstCompile]
    public static class BurstFacing6Roll2MeshUtility
    {
        /// <summary>
        /// Frozen effective-face LUT for Facing6Roll2, indexed by <c>facing * 24 + roll * 6 + worldFace</c>.
        /// </summary>
        private static readonly byte[] s_effectiveFaceLut =
        {
            0, 1, 2, 3, 4, 5, 0, 1, 4, 5, 3, 2, 0, 1, 3, 2, 5, 4, 0, 1, 5, 4, 2, 3,
            1, 0, 2, 3, 5, 4, 1, 0, 4, 5, 2, 3, 1, 0, 3, 2, 4, 5, 1, 0, 5, 4, 3, 2,
            3, 2, 0, 1, 4, 5, 5, 4, 0, 1, 3, 2, 2, 3, 0, 1, 5, 4, 4, 5, 0, 1, 2, 3,
            2, 3, 1, 0, 4, 5, 4, 5, 1, 0, 3, 2, 3, 2, 1, 0, 5, 4, 5, 4, 1, 0, 2, 3,
            5, 4, 2, 3, 0, 1, 2, 3, 4, 5, 0, 1, 4, 5, 3, 2, 0, 1, 3, 2, 5, 4, 0, 1,
            4, 5, 2, 3, 1, 0, 3, 2, 4, 5, 1, 0, 5, 4, 3, 2, 1, 0, 2, 3, 5, 4, 1, 0,
        };

        /// <summary>
        /// Frozen UV rotation LUT for Facing6Roll2, indexed by <c>facing * 24 + roll * 6 + worldFace</c>.
        /// </summary>
        private static readonly byte[] s_uvTurnsLut =
        {
            0, 0, 0, 0, 0, 0, 1, 3, 1, 1, 1, 1, 2, 2, 2, 2, 2, 2, 3, 1, 3, 3, 3, 3,
            0, 0, 2, 2, 0, 0, 3, 1, 3, 3, 1, 1, 2, 2, 0, 0, 2, 2, 1, 3, 1, 1, 3, 3,
            0, 0, 2, 2, 0, 0, 0, 0, 3, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 3, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 3, 1, 0, 0, 0, 0, 2, 2, 0, 0, 0, 0, 1, 3, 0, 0,
            0, 0, 1, 3, 0, 0, 1, 1, 2, 0, 1, 3, 2, 2, 3, 1, 2, 2, 3, 3, 0, 2, 3, 1,
            0, 0, 3, 1, 0, 0, 1, 1, 0, 2, 3, 1, 2, 2, 1, 3, 2, 2, 3, 3, 2, 0, 1, 3,
        };

        /// <summary>
        /// Looks up the effective block face index for a given (facing, roll, world face) combination.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte GetEffectiveFace(byte facing, byte roll, int worldFace)
        {
            return s_effectiveFaceLut[facing * 24 + roll * 6 + worldFace];
        }

        /// <summary>
        /// Returns clockwise quarter-turns (0-3) applied to the canonical face UVs for the given
        /// Facing6Roll2 direction and world face.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte GetUvQuarterTurnsCW(byte facing, byte roll, int worldFace)
        {
            return s_uvTurnsLut[facing * 24 + roll * 6 + worldFace];
        }
    }
}
