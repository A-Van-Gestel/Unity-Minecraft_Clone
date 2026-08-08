using System.Runtime.CompilerServices;
using Data;
using Unity.Burst;
using Unity.Mathematics;

namespace Jobs.BurstData
{
    /// <summary>
    /// Burst-safe block-shape queries: rotates a block's authored collision AABB into its placed
    /// orientation, and reports how much of a given cell face that rotated volume covers.
    /// <para>
    /// This is the shared rotation-to-AABB core. <see cref="Helpers.BlockCollisionBoundsUtility"/>
    /// calls it for the managed (physics / placement / interaction-ray) side, so there is exactly one
    /// implementation of "where does this block's volume actually sit" in the engine — see
    /// <c>Documentation/Design/VOXEL_OCCLUSION_REFACTOR.md</c> §4 D1 for why occlusion is derived from
    /// the collision bounds rather than from a second shape descriptor.
    /// </para>
    /// <para>
    /// <b>Coverage is geometry, not a lighting decision.</b> <see cref="GetFaceCoverage"/> answers
    /// "what fraction of this cell face does the block's volume span", nothing more. Callers combine it
    /// with the block's <see cref="BlockTypeJobData.Opacity"/> to reach a light cost; a block whose
    /// volume fills a face is not automatically a light blocker (glass covers every face and blocks
    /// nothing). Consequently, a full-block type returns 1 on all six faces regardless of whether it is
    /// solid or even air — the opacity is what distinguishes them.
    /// </para>
    /// <para>
    /// <b>Single AABB only.</b> Compound shapes (stairs, L-shapes) are out of scope, inherited from the
    /// collision model this reads — see <c>SUB_VOXEL_COLLISION_SYSTEM.md</c> §7 and <c>VQ-4</c>.
    /// </para>
    /// </summary>
    [BurstCompile]
    public static class BurstOcclusionUtility
    {
        /// <summary>
        /// Tolerance for "the rotated box reaches this cell face". Rotations are exact 90° multiples of
        /// authored values, so the slack only absorbs float round-off from the matrix multiply.
        /// </summary>
        private const float FACE_TOUCH_EPSILON = 1e-4f;

        /// <summary>
        /// Rotates block-local bounds about the cell center and returns the enclosing axis-aligned volume,
        /// still in block-local <c>[0,1]³</c>.
        /// </summary>
        /// <param name="min">Authored minimum corner, block-local.</param>
        /// <param name="max">Authored maximum corner, block-local.</param>
        /// <param name="rotationMatrix">The block's metadata rotation.</param>
        /// <param name="rotatedMin">The enclosing volume's minimum corner, block-local.</param>
        /// <param name="rotatedMax">The enclosing volume's maximum corner, block-local.</param>
        public static void RotateLocalBounds(float3 min, float3 max, in float3x3 rotationMatrix,
            out float3 rotatedMin, out float3 rotatedMax)
        {
            float3 center = BurstVoxelData.BlockCenter;

            // Shift to sit around the origin so the rotation pivots on the cell center.
            float3 localCenter = (min + max) * 0.5f - center;
            float3 e = (max - min) * 0.5f;

            // 8 corners, computed inline — this runs per voxel face in the lighting/meshing hot paths.
            float3 c0 = math.mul(rotationMatrix, localCenter + new float3(e.x, e.y, e.z));
            float3 c1 = math.mul(rotationMatrix, localCenter + new float3(e.x, e.y, -e.z));
            float3 c2 = math.mul(rotationMatrix, localCenter + new float3(e.x, -e.y, e.z));
            float3 c3 = math.mul(rotationMatrix, localCenter + new float3(e.x, -e.y, -e.z));
            float3 c4 = math.mul(rotationMatrix, localCenter + new float3(-e.x, e.y, e.z));
            float3 c5 = math.mul(rotationMatrix, localCenter + new float3(-e.x, e.y, -e.z));
            float3 c6 = math.mul(rotationMatrix, localCenter + new float3(-e.x, -e.y, e.z));
            float3 c7 = math.mul(rotationMatrix, localCenter + new float3(-e.x, -e.y, -e.z));

            float3 lo = math.min(c0, math.min(c1, math.min(c2, math.min(c3, math.min(c4, math.min(c5, math.min(c6, c7)))))));
            float3 hi = math.max(c0, math.max(c1, math.max(c2, math.max(c3, math.max(c4, math.max(c5, math.max(c6, c7)))))));

            rotatedMin = lo + center;
            rotatedMax = hi + center;
        }

        /// <summary>
        /// Returns the fraction of one cell face that a rotated block volume spans, in <c>[0, 1]</c>:
        /// 0 when the volume does not reach that face at all, 1 when it covers the face completely.
        /// </summary>
        /// <param name="rotatedMin">Rotated minimum corner, block-local (from <see cref="RotateLocalBounds"/>).</param>
        /// <param name="rotatedMax">Rotated maximum corner, block-local.</param>
        /// <param name="faceIndex">Face direction, in <c>VoxelData.FaceChecks</c> order:
        /// 0 = Back (−Z), 1 = Front (+Z), 2 = Top (+Y), 3 = Bottom (−Y), 4 = Left (−X), 5 = Right (+X).</param>
        /// <returns>The covered fraction of that face.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float GetFaceCoverage(float3 rotatedMin, float3 rotatedMax, int faceIndex)
        {
            FaceAxis(faceIndex, out int axis, out bool positive);

            // A volume that stops short of the face plane occludes nothing on that face.
            bool touches = positive
                ? rotatedMax[axis] >= 1f - FACE_TOUCH_EPSILON
                : rotatedMin[axis] <= FACE_TOUCH_EPSILON;
            if (!touches)
                return 0f;

            // Coverage is the cross-section on the two axes perpendicular to the face normal.
            int a = axis == 0 ? 1 : 0;
            int b = axis == 2 ? 1 : 2;
            float extentA = math.saturate(rotatedMax[a] - rotatedMin[a]);
            float extentB = math.saturate(rotatedMax[b] - rotatedMin[b]);
            return extentA * extentB;
        }

        /// <summary>
        /// Resolves a placed block's coverage of one of its cell faces — the entry point callers use.
        /// Full-block types short-circuit to 1 without touching the rotation path.
        /// </summary>
        /// <param name="block">The placed block's job data, supplying its bounds and metadata schema.</param>
        /// <param name="meta">The placed voxel's raw metadata byte, which selects the rotation.</param>
        /// <param name="faceIndex">Face direction, in <c>VoxelData.FaceChecks</c> order.</param>
        /// <returns>The covered fraction of that face, in <c>[0, 1]</c>.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float GetBlockFaceCoverage(in BlockTypeJobData block, byte meta, int faceIndex)
        {
            if (!block.HasCustomBounds)
                return 1f;

            float3x3 rotationMatrix = BurstCustomMeshRotationUtility.GetRotationMatrix(
                block.MetadataSchema, meta, block.DefaultMetadata);
            RotateLocalBounds(block.BoundsMin, block.BoundsMax, in rotationMatrix,
                out float3 rotatedMin, out float3 rotatedMax);
            return GetFaceCoverage(rotatedMin, rotatedMax, faceIndex);
        }

        /// <summary>
        /// VO-8: returns the fraction of one <b>octant</b> of a cell that a rotated block volume fills,
        /// in <c>[0, 1]</c>. An octant is a <c>0.5³</c> corner sub-box, selected per axis by
        /// <paramref name="lowHalf"/>.
        /// <para>
        /// This is the per-corner counterpart to <see cref="GetFaceCoverage"/>. Ambient occlusion asks
        /// its question at a <i>vertex</i> shared by eight cells, so "how much of the corner is blocked"
        /// is answered by the volume sitting in the octant nearest that vertex — which is what lets a
        /// vertical slab darken the two corners on its solid side and leave the other two open.
        /// A full-cell volume fills every octant, so this returns 1 for any full cube.
        /// </para>
        /// </summary>
        /// <param name="rotatedMin">Rotated minimum corner, block-local (from <see cref="RotateLocalBounds"/>).</param>
        /// <param name="rotatedMax">Rotated maximum corner, block-local.</param>
        /// <param name="lowHalf">Per axis: true selects <c>[0, 0.5]</c>, false selects <c>[0.5, 1]</c>.</param>
        /// <returns>The filled fraction of that octant.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float GetOctantCoverage(float3 rotatedMin, float3 rotatedMax, bool3 lowHalf)
        {
            float3 octantMin = math.select(new float3(0.5f), float3.zero, lowHalf);
            float3 octantMax = math.select(new float3(1f), new float3(0.5f), lowHalf);

            return GetRegionCoverage(rotatedMin, rotatedMax, octantMin, octantMax);
        }

        /// <summary>
        /// VO-9: returns the fraction of an <b>arbitrary</b> block-local box that a rotated block volume
        /// fills, in <c>[0, 1]</c>. This is the general form of <see cref="GetOctantCoverage"/>, which is
        /// now the special case where the box is one of the eight <c>0.5³</c> corner sub-boxes.
        /// <para>
        /// Ambient occlusion needs it because a shading sample taken somewhere other than a cell corner
        /// asks about a box centred on that sample point, not about an octant. Generalizing the region
        /// rather than adding per-shape cases is what keeps the query shape-agnostic: the primitive is
        /// still an AABB-versus-AABB fill fraction, so any single-box custom mesh works unchanged.
        /// </para>
        /// <para>
        /// The result is normalized by the <i>region's own</i> volume, so a sliver of a cell reports the
        /// fraction of that sliver which is filled. A region of zero volume returns 0.
        /// </para>
        /// </summary>
        /// <param name="rotatedMin">Rotated minimum corner, block-local (from <see cref="RotateLocalBounds"/>).</param>
        /// <param name="rotatedMax">Rotated maximum corner, block-local.</param>
        /// <param name="regionMin">Minimum corner of the query box, block-local.</param>
        /// <param name="regionMax">Maximum corner of the query box, block-local.</param>
        /// <returns>The filled fraction of that region.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float GetRegionCoverage(float3 rotatedMin, float3 rotatedMax,
            float3 regionMin, float3 regionMax)
        {
            float3 extent = math.max(0f, regionMax - regionMin);
            float regionVolume = extent.x * extent.y * extent.z;
            if (regionVolume <= 0f) return 0f;

            float3 overlap = math.max(0f, math.min(rotatedMax, regionMax) - math.max(rotatedMin, regionMin));

            // An octant's volume is exactly 0.125, so for the octant case this division is by a power of
            // two — bit-identical to the multiply by 8 this replaced, which is what lets VO-9's general
            // form take over without moving a single existing corner value.
            return math.saturate(overlap.x * overlap.y * overlap.z / regionVolume);
        }

        /// <summary>
        /// Maps a face index to the axis it is perpendicular to and which end of that axis it sits on.
        /// </summary>
        /// <param name="faceIndex">Face direction, in <c>VoxelData.FaceChecks</c> order.</param>
        /// <param name="axis">0 = X, 1 = Y, 2 = Z.</param>
        /// <param name="positive">True for the +axis face, false for the −axis face.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void FaceAxis(int faceIndex, out int axis, out bool positive)
        {
            switch (faceIndex)
            {
                case 0:
                    axis = 2;
                    positive = false;
                    break; // Back  (-Z)
                case 1:
                    axis = 2;
                    positive = true;
                    break; // Front (+Z)
                case 2:
                    axis = 1;
                    positive = true;
                    break; // Top   (+Y)
                case 3:
                    axis = 1;
                    positive = false;
                    break; // Bottom(-Y)
                case 4:
                    axis = 0;
                    positive = false;
                    break; // Left  (-X)
                default:
                    axis = 0;
                    positive = true;
                    break; // Right (+X)
            }
        }
    }
}
