using System.Runtime.CompilerServices;
using Data;
using Unity.Mathematics;

namespace Jobs.BurstData
{
    /// <summary>
    /// The single definition of the engine's light attenuation rule, shared by every system that must
    /// agree on it: the BFS flood-fill (<c>NeighborhoodLightingJob</c>), the borderless validation oracle
    /// (<c>LightingOracle</c>), and the cross-chunk sunlight removal veto
    /// (<c>Helpers.CrossChunkLightModApplier.InChunkSunlightSupport</c>).
    /// <para>
    /// Burst-compatible (uses only <see cref="Unity.Mathematics"/>), so the job can call it directly.
    /// Keeping the formula in one place prevents the three call sites from silently diverging — a
    /// divergence would make the cross-chunk veto over- or under-estimate support relative to the BFS.
    /// </para>
    /// </summary>
    public static class LightAttenuation
    {
        /// <summary>
        /// The light level remaining after light travels from a source into a destination voxel, charged
        /// the destination's opacity on entry. Uses the Starlight/Moonrise formula
        /// <c>max(0, sourceLight - max(1, opacity))</c>: air (opacity 0) costs 1 level, semi-transparent
        /// blocks cost their opacity, and a fully-opaque destination (opacity ≥ 15) receives 0.
        /// </summary>
        /// <param name="sourceLight">The light level at the source (0-15).</param>
        /// <param name="opacity">The opacity of the voxel the light is entering (the entry cost, minimum 1).</param>
        /// <returns>The attenuated light level (0-15).</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte Attenuate(int sourceLight, byte opacity)
        {
            return (byte)math.max(0, sourceLight - math.max(1, opacity));
        }

        // ===== Directional occlusion (VO-3) =====
        // A block that does not fill its cell blocks light only through the faces its volume actually
        // covers. These three predicates are the whole of that model, and live here — beside Attenuate —
        // so the BFS job, the borderless validation oracle, and the cross-chunk veto cannot drift apart.
        // See Documentation/Design/VOXEL_OCCLUSION_REFACTOR.md §4 D2/D3.
        //
        // EVERY predicate short-circuits on HasCustomBounds, so a full-cube block takes a path that is
        // arithmetically identical to the pre-VO-3 rule. That is deliberate: it is what makes "no
        // behavior change for full blocks" provable rather than hoped for.

        /// <summary>
        /// Returns <see langword="true"/> when light cannot cross the given face of this block: it is
        /// opaque <b>and</b> its volume covers that face completely. A full cube covers every face, so
        /// this reduces to <c>IsOpaque</c> for anything without custom bounds.
        /// </summary>
        /// <param name="block">The block being crossed.</param>
        /// <param name="meta">The placed voxel's raw metadata byte (selects the volume's rotation).</param>
        /// <param name="faceIndex">Face direction, in <c>VoxelData.FaceChecks</c> order.</param>
        /// <returns><see langword="true"/> when that face blocks light.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool FaceBlocksLight(in BlockTypeJobData block, byte meta, int faceIndex)
        {
            if (!block.IsOpaque)
                return false;
            if (!block.HasCustomBounds)
                return true;

            return BurstOcclusionUtility.GetBlockFaceCoverage(in block, meta, faceIndex) >= FULL_COVERAGE_THRESHOLD;
        }

        /// <summary>
        /// The opacity charged when light enters this block through the given face. For a partial block
        /// this is 0 (air cost) on a face its volume does not fully cover — the light travels through the
        /// empty part of the cell — and its authored opacity on a face that does. Full cubes always
        /// return their authored opacity, exactly as before VO-3.
        /// </summary>
        /// <param name="block">The block being entered.</param>
        /// <param name="meta">The placed voxel's raw metadata byte.</param>
        /// <param name="faceIndex">The entry face, in <c>VoxelData.FaceChecks</c> order.</param>
        /// <returns>The opacity to charge on entry.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte EntryOpacity(in BlockTypeJobData block, byte meta, int faceIndex)
        {
            if (!block.HasCustomBounds)
                return block.Opacity;

            return BurstOcclusionUtility.GetBlockFaceCoverage(in block, meta, faceIndex) >= FULL_COVERAGE_THRESHOLD
                ? block.Opacity
                : (byte)0;
        }

        /// <summary>
        /// Returns <see langword="true"/> when light inside this block cannot leave through the given
        /// face. Only ever true for partial blocks: a full opaque cube is rejected earlier by the
        /// propagation source guard (it stores surface light but never re-propagates it), so this must
        /// not fire for one or that guard would be applied twice.
        /// </summary>
        /// <param name="block">The block light is leaving.</param>
        /// <param name="meta">The placed voxel's raw metadata byte.</param>
        /// <param name="faceIndex">The exit face, in <c>VoxelData.FaceChecks</c> order.</param>
        /// <returns><see langword="true"/> when light cannot exit through that face.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool ExitBlocked(in BlockTypeJobData block, byte meta, int faceIndex)
        {
            return block.HasCustomBounds && FaceBlocksLight(in block, meta, faceIndex);
        }

        /// <summary>
        /// Returns <see langword="true"/> when light crosses this face with no attenuation at all — the
        /// directional form of <see cref="BlockTypeJobData.IsFullyTransparentToLight"/>, and the test the
        /// vertical sky-light column rule uses.
        /// <para>
        /// Note this is <b>stricter than "does not block"</b>: a semi-transparent full block such as water
        /// does not block light, but it does attenuate it, so it must not extend the unattenuated column.
        /// Defining this as "entry cost is zero" rather than "face does not occlude" is what preserves
        /// that. For a full cube it reduces exactly to <c>Opacity == 0</c>.
        /// </para>
        /// </summary>
        /// <param name="block">The block being crossed.</param>
        /// <param name="meta">The placed voxel's raw metadata byte.</param>
        /// <param name="faceIndex">The face being crossed, in <c>VoxelData.FaceChecks</c> order.</param>
        /// <returns><see langword="true"/> when crossing that face costs nothing.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsTransparentThroughFace(in BlockTypeJobData block, byte meta, int faceIndex)
        {
            return EntryOpacity(in block, meta, faceIndex) == 0;
        }

        /// <summary>Coverage at or above which a face counts as fully covered (absorbs float round-off).</summary>
        private const float FULL_COVERAGE_THRESHOLD = 1f - 1e-4f;
    }
}
