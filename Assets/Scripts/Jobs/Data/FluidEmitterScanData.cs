using System.Runtime.CompilerServices;
using Unity.Mathematics;

namespace Jobs.Data
{
    /// <summary>
    /// One cell of the fluid-emitter scan's accumulation grid: how many flowing voxels of one kind fell in
    /// this bin, and the sum of their voxel positions.
    /// </summary>
    /// <remarks>
    /// A sum plus a count rather than a running average: integer accumulation is exact and, crucially,
    /// order-independent, so the same world always produces the same centroid no matter which order the
    /// scan visited the sections in. The average is taken once, on the main thread, when the bin becomes an
    /// emitter.
    /// </remarks>
    public struct FluidEmitterBin
    {
        /// <summary>How many flowing voxels of this bin's kind were found in it.</summary>
        public int Weight;

        /// <summary>Component-wise sum of those voxels' positions, in voxel world space.</summary>
        public int3 SumPos;

        /// <summary>The bin's centroid in voxel world space. Only meaningful when <see cref="Weight"/> &gt; 0.</summary>
        public float3 Centroid => new float3(SumPos.x, SumPos.y, SumPos.z) / math.max(1, Weight);
    }

    /// <summary>
    /// Geometry of the fluid-emitter scan — the volume searched around the listener and the bin grid the
    /// candidates accumulate into (SOUND_ENGINE_DESIGN.md §5.2).
    /// </summary>
    /// <remarks>
    /// The grid is anchored to <b>world</b> coordinates snapped to <see cref="BinSize"/>, not to the
    /// listener: a listener-relative grid would shift its cell boundaries every time the player moved, and
    /// voxels crossing a boundary would jump the centroid they contribute to. Snapping to world space keeps
    /// a given river's bins identical from scan to scan, so an emitter only moves when the water does.
    /// </remarks>
    public static class FluidEmitterScanGeometry
    {
        /// <summary>
        /// Number of <see cref="Data.Enums.FluidEmitterKind"/> values — the grid's innermost stride.
        /// </summary>
        /// <remarks>
        /// Duplicated from the enum rather than derived, because <c>Enum.GetValues</c> is managed and this is
        /// read inside a Burst job. The duplication is guarded by a validation baseline: appending a kind
        /// without bumping this would make the job index one past the end of the bin grid on the far cell.
        /// </remarks>
        public const int KindCount = 4;

        /// <summary>Horizontal search radius around the listener, in voxels (~2 chunks).</summary>
        public const int RadiusXZ = 32;

        /// <summary>Vertical search radius around the listener, in voxels.</summary>
        public const int RadiusY = 32;

        /// <summary>Bin edge length in voxels. A power of two so the bin index is a shift, not a divide.</summary>
        public const int BinSize = 8;

        /// <summary>log2(<see cref="BinSize"/>).</summary>
        public const int BinShift = 3;

        /// <summary>Bins spanning the horizontal search width. One extra so a snapped origin still covers the far edge.</summary>
        public const int BinsXZ = 2 * RadiusXZ / BinSize + 1;

        /// <summary>Bins spanning the vertical search height.</summary>
        public const int BinsY = 2 * RadiusY / BinSize + 1;

        /// <summary>Total length of the bin array — the grid times one slot per kind.</summary>
        public const int BinCount = BinsXZ * BinsY * BinsXZ * KindCount;

        /// <summary>
        /// The bin grid's origin for a listener: the low corner of the search box, snapped down to a bin
        /// boundary so the grid lands on the same world cells regardless of where the listener stands.
        /// </summary>
        /// <param name="listenerVoxel">The listener's voxel cell.</param>
        /// <returns>The voxel-space position of bin (0, 0, 0)'s low corner.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int3 BinOrigin(int3 listenerVoxel)
        {
            // Arithmetic shift, not a divide: it floors for negative coordinates too, which is what keeps
            // the grid aligned in the negative quadrants (COORDINATE_SPACES_GUIDE.md / WS-1).
            int3 low = listenerVoxel - new int3(RadiusXZ, RadiusY, RadiusXZ);
            return new int3(low.x >> BinShift, low.y >> BinShift, low.z >> BinShift) << BinShift;
        }

        /// <summary>
        /// Flattens a bin coordinate and kind into an index of the bin array.
        /// </summary>
        /// <param name="bin">Bin coordinate within the grid.</param>
        /// <param name="kind">The emitter kind slot.</param>
        /// <returns>The flat index, or -1 when the bin coordinate or kind falls outside the grid.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int BinIndex(int3 bin, int kind)
        {
            if ((uint)bin.x >= BinsXZ || (uint)bin.y >= BinsY || (uint)bin.z >= BinsXZ) return -1;

            // Bounded too: the kind is the innermost stride, so an out-of-range one walks straight off the
            // end of the array rather than landing in a wrong-but-valid bin.
            if ((uint)kind >= KindCount) return -1;

            return ((bin.y * BinsXZ + bin.z) * BinsXZ + bin.x) * KindCount + kind;
        }
    }
}
