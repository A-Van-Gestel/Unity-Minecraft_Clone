using System.Runtime.CompilerServices;
using Data;
using Unity.Burst;

namespace Jobs.BurstData
{
    /// <summary>
    /// Burst-safe meshing helpers for blocks declared with <see cref="MetadataSchema.Facing6"/>.
    /// </summary>
    /// <remarks>
    /// <para>For a Facing6 block (directional blocks, observers, dispensers, etc.), the cube's
    /// vertex geometry is unchanged — what differs per facing is which texture face is shown on
    /// each world face. This utility encodes the frozen per-facing face-remap rule so the
    /// meshing job can do an O(1) lookup instead of per-voxel quaternion rotation.</para>
    /// <para><b>Convention</b>: the "front" of the block (face 1, +Z/North) rotates to point in
    /// the direction indicated by the Facing6 value.
    /// <list type="bullet">
    ///   <item><description>0 = South (-Z): 180° Y rotation.</description></item>
    ///   <item><description>1 = North (+Z): identity (default).</description></item>
    ///   <item><description>2 = Top (+Y): -90° X pitch (front→up).</description></item>
    ///   <item><description>3 = Bottom (-Y): +90° X pitch (front→down).</description></item>
    ///   <item><description>4 = West (-X): 90° CW Y rotation.</description></item>
    ///   <item><description>5 = East (+X): 270° CW Y rotation.</description></item>
    /// </list></para>
    /// <para>This convention is <b>frozen</b> once shipped — any change requires a new save
    /// version + migration step per <c>PER_BLOCK_METADATA_SCHEMAS.md §9.6</c>.</para>
    /// </remarks>
    [BurstCompile]
    public static class BurstFacing6MeshUtility
    {
        /// <summary>
        /// Frozen face-remap LUT, flat-indexed by <c>facing * 6 + worldFace</c>. Each entry tells
        /// the meshing job which <i>block</i> face index to read the texture from when emitting a
        /// given <i>world</i> face for a Facing6 block.
        /// </summary>
        /// <remarks>
        /// World face indices follow the standard convention:
        /// <c>0=Back(-Z), 1=Front(+Z), 2=Top(+Y), 3=Bottom(-Y), 4=Left(-X), 5=Right(+X)</c>.
        /// </remarks>
        private static readonly byte[] s_faceRemap =
        {
            // facing 0 (South): player looks -Z, block front faces +Z (toward the player).
            //   Identity — the default block front already faces +Z.
            0, 1, 2, 3, 4, 5,

            // facing 1 (North): player looks +Z, block front faces -Z (toward the player).
            //   180° Y rotation: Front↔Back swap, Left↔Right swap. Top/Bottom unchanged.
            1, 0, 2, 3, 5, 4,

            // facing 2 (Top): player looks +Y, block front faces -Y (toward the player).
            //   +90° X pitch (front rotates downward).
            //   World Back  ← Original Bottom, World Front ← Original Top,
            //   World Top   ← Original Back,   World Bottom ← Original Front.
            //   Left/Right unchanged.
            3, 2, 0, 1, 4, 5,

            // facing 3 (Bottom): player looks -Y, block front faces +Y (toward the player).
            //   -90° X pitch (front rotates upward).
            //   World Back  ← Original Top,    World Front ← Original Bottom,
            //   World Top   ← Original Front,  World Bottom ← Original Back.
            //   Left/Right unchanged.
            2, 3, 1, 0, 4, 5,

            // facing 4 (West): 90° CW Y rotation.
            //   World Back  ← Original Right,  World Front ← Original Left,
            //   World Left  ← Original Back,   World Right ← Original Front.
            //   Top/Bottom unchanged.
            5, 4, 2, 3, 0, 1,

            // facing 5 (East): 270° CW Y rotation.
            //   World Back  ← Original Left,   World Front ← Original Right,
            //   World Left  ← Original Front,  World Right ← Original Back.
            //   Top/Bottom unchanged.
            4, 5, 2, 3, 1, 0,
        };

        /// <summary>
        /// Frozen per-facing UV rotation LUT, flat-indexed by <c>facing * 6 + worldFace</c>.
        /// Each entry stores clockwise quarter-turns (0-3) applied to the canonical face UVs.
        /// </summary>
        private static readonly byte[] s_uvQuarterTurnsCW =
        {
            // facing 0 (South): identity
            0, 0, 0, 0, 0, 0,
            // facing 1 (North): 180° Y
            0, 0, 2, 2, 0, 0,
            // facing 2 (Top): +90° X pitch
            0, 0, 2, 2, 0, 0,
            // facing 3 (Bottom): -90° X pitch
            0, 0, 0, 0, 0, 0,
            // facing 4 (West): 90° CW Y
            0, 0, 1, 3, 0, 0,
            // facing 5 (East): 270° CW Y
            0, 0, 3, 1, 0, 0,
        };

        /// <summary>
        /// Looks up the effective block face index for a given (facing, world face) pair.
        /// </summary>
        /// <param name="facing">The Facing6 value: 0=South, 1=North, 2=Top, 3=Bottom, 4=West, 5=East.</param>
        /// <param name="worldFace">The world face index 0-5 the meshing job is currently emitting.</param>
        /// <returns>The block face index 0-5 whose texture should be sampled for the world face.</returns>
        /// <remarks>
        /// Burst-safe O(1) array lookup. Hot-path callable. Inputs are not validated — callers
        /// must supply <paramref name="facing"/> in 0-5 and <paramref name="worldFace"/> in 0-5.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte GetEffectiveFace(byte facing, int worldFace)
        {
            return s_faceRemap[facing * 6 + worldFace];
        }

        /// <summary>
        /// Returns clockwise quarter-turns (0-3) applied to the canonical face UVs for the given
        /// Facing6 direction and world face.
        /// </summary>
        /// <param name="facing">The Facing6 value: 0-5.</param>
        /// <param name="worldFace">The world face index 0-5.</param>
        /// <returns>Clockwise quarter-turn count in the range 0-3.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte GetUvQuarterTurnsCW(byte facing, int worldFace)
        {
            return s_uvQuarterTurnsCW[facing * 6 + worldFace];
        }
    }
}
