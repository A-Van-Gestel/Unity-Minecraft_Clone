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

        /// <summary>
        /// Returns <see langword="true"/> when this block interrupts the vertical sky column, and therefore
        /// belongs in the heightmap. The column enters a cell through its <b>top</b> face and leaves through
        /// its <b>bottom</b>, so both must be tested: a horizontal half slab is entered for free (its top
        /// face is the open mid-plane) but its solid underside stops the column, while a vertical half slab
        /// leaves a full-height channel and interrupts nothing.
        /// <para>
        /// This is the directional replacement for <c>BlockTypeJobData.IsLightObstructing</c>
        /// (<c>Opacity &gt; 0</c>) at every heightmap site. For a full cube it reduces to exactly that, since
        /// <see cref="EntryOpacity"/> short-circuits on <c>HasCustomBounds</c> and <see cref="ExitBlocked"/>
        /// can never fire — so no full-cube world's heightmap changes by a single entry.
        /// </para>
        /// <para>
        /// <b>Why the heightmap has to care</b> (<c>LIGHTING_BUGS.md</c> Bug 21): the heightmap is what makes
        /// <c>RecalculateSunlightForColumn</c> authoritative for sky <i>removal</i>. A slab that registers
        /// when it should not means sealing it never moves the heightmap, so the recalculation never re-runs
        /// and the orphaned column — being flat, with no decrement chain for <c>PropagateDarkness</c> to
        /// follow — stays lit forever.
        /// </para>
        /// </summary>
        /// <param name="block">The block occupying the cell.</param>
        /// <param name="meta">The placed voxel's raw metadata byte (selects the volume's rotation).</param>
        /// <returns><see langword="true"/> when the vertical sky column cannot pass through this cell freely.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool ObstructsSkyColumn(in BlockTypeJobData block, byte meta)
        {
            return EntryOpacity(in block, meta, TOP_FACE) > 0 || ExitBlocked(in block, meta, BOTTOM_FACE);
        }

        /// <summary>
        /// How much of one <b>octant</b> of its cell this block visually blocks, in <c>[0, 1]</c> — the
        /// ambient-occlusion counterpart to <see cref="FaceBlocksLight"/>, which asks a related question
        /// as a yes/no. AO is a local shading term rather than a transport decision, so it is the one
        /// consumer that takes a coverage fraction ungraded (see <c>VOXEL_OCCLUSION_REFACTOR.md</c> §4
        /// D2/D5, and VO-8 for why the question is per-octant rather than per-face).
        /// <para>
        /// <b>Why an octant.</b> An AO corner is a vertex shared by eight cells, and each sample's
        /// contribution is whether it occupies the corner *there* — not whether it covers a whole face.
        /// Asking per-face gives one answer for all four corners, which is why a vertical slab used to
        /// dim the block beneath it evenly instead of shading only the half its solid part stands on.
        /// </para>
        /// <para>
        /// Gated on opacity, not on volume alone: glass fills every octant and darkens nothing. Full
        /// cubes return exactly 0 or 1 without touching the rotation path, which is what keeps the
        /// smooth-lighting output unchanged for them — and keeps the per-corner cost off the hot path
        /// for every block type that has no custom bounds.
        /// </para>
        /// </summary>
        /// <param name="block">The block being sampled.</param>
        /// <param name="meta">The placed voxel's raw metadata byte (selects the volume's rotation).</param>
        /// <param name="lowHalf">The octant nearest the shaded vertex; per axis, true selects <c>[0, 0.5]</c>.</param>
        /// <returns>The occluded fraction of that octant.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float AmbientOcclusionOctantCoverage(in BlockTypeJobData block, byte meta, bool3 lowHalf)
        {
            if (!block.IsOpaque)
                return 0f;
            if (!block.HasCustomBounds)
                return 1f;

            float3x3 rotationMatrix = BurstCustomMeshRotationUtility.GetRotationMatrix(
                block.MetadataSchema, meta, block.DefaultMetadata);
            BurstOcclusionUtility.RotateLocalBounds(block.BoundsMin, block.BoundsMax, in rotationMatrix,
                out float3 rotatedMin, out float3 rotatedMax);

            return BurstOcclusionUtility.GetOctantCoverage(rotatedMin, rotatedMax, lowHalf);
        }

        /// <summary>
        /// VO-9: how much of an <b>arbitrary block-local region</b> of a sampled cell is occluded — the
        /// general form of <see cref="AmbientOcclusionOctantCoverage"/>, used when a shading sample is
        /// taken somewhere other than a cell corner.
        /// <para>
        /// Same gating as the octant form, and for the same reasons: transparent volumes occlude nothing,
        /// and a block with no custom bounds answers 0 or 1 without touching the rotation path — so an
        /// ordinary cube costs one branch here no matter how densely the face is sampled.
        /// </para>
        /// </summary>
        /// <param name="block">The block being sampled.</param>
        /// <param name="meta">The placed voxel's raw metadata byte (selects the volume's rotation).</param>
        /// <param name="regionMin">Minimum corner of the query region, in the sampled cell's local space.</param>
        /// <param name="regionMax">Maximum corner of the query region, in the sampled cell's local space.</param>
        /// <returns>The occluded fraction of that region.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float AmbientOcclusionRegionCoverage(in BlockTypeJobData block, byte meta,
            float3 regionMin, float3 regionMax)
        {
            if (!block.IsOpaque)
                return 0f;
            if (!block.HasCustomBounds)
                return 1f;

            float3x3 rotationMatrix = BurstCustomMeshRotationUtility.GetRotationMatrix(
                block.MetadataSchema, meta, block.DefaultMetadata);
            BurstOcclusionUtility.RotateLocalBounds(block.BoundsMin, block.BoundsMax, in rotationMatrix,
                out float3 rotatedMin, out float3 rotatedMax);

            return BurstOcclusionUtility.GetRegionCoverage(rotatedMin, rotatedMax, regionMin, regionMax);
        }

        /// <summary>
        /// SS-1: the rectangle a block's volume projects onto one of its cell faces — the occluder's
        /// silhouette, which a contact-shadow term measures distance to.
        /// <para>
        /// Same gating as every sibling predicate here, and for the same reasons: a transparent volume
        /// casts nothing (glass fills its cell and shades nothing), and a block with no custom bounds
        /// answers the whole face without touching the rotation path — so an ordinary cube costs one
        /// branch no matter how densely a face is sampled.
        /// </para>
        /// <para>
        /// "Touching" is the contact in <i>contact shadow</i>: a volume that stops short of the face
        /// plane — a top slab above a floor, say — returns false and shades nothing, which is the
        /// behavior the <c>VO-*</c> arc already signed off for that case.
        /// </para>
        /// </summary>
        /// <param name="block">The block being sampled.</param>
        /// <param name="meta">The placed voxel's raw metadata byte (selects the volume's rotation).</param>
        /// <param name="faceIndex">Face direction, in <c>VoxelData.FaceChecks</c> order.</param>
        /// <param name="rectMin">Silhouette minimum corner on the two axes perpendicular to the face.</param>
        /// <param name="rectMax">Silhouette maximum corner.</param>
        /// <returns>True when this block casts a silhouette on that face.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool AmbientOcclusionFaceSilhouette(in BlockTypeJobData block, byte meta,
            int faceIndex, out float2 rectMin, out float2 rectMax)
        {
            if (!block.IsOpaque)
            {
                rectMin = float2.zero;
                rectMax = float2.zero;
                return false;
            }

            if (!block.HasCustomBounds)
            {
                rectMin = float2.zero;
                rectMax = new float2(1f, 1f);
                return true;
            }

            float3x3 rotationMatrix = BurstCustomMeshRotationUtility.GetRotationMatrix(
                block.MetadataSchema, meta, block.DefaultMetadata);
            BurstOcclusionUtility.RotateLocalBounds(block.BoundsMin, block.BoundsMax, in rotationMatrix,
                out float3 rotatedMin, out float3 rotatedMax);

            return BurstOcclusionUtility.GetFaceSilhouette(rotatedMin, rotatedMax, faceIndex,
                out rectMin, out rectMax);
        }

        /// <summary>Coverage at or above which a face counts as fully covered (absorbs float round-off).</summary>
        private const float FULL_COVERAGE_THRESHOLD = 1f - 1e-4f;

        /// <summary>
        /// Public form of <see cref="FULL_COVERAGE_THRESHOLD"/> for the AO path, whose caller must apply
        /// the same "counts as fully covered" cutoff to stay consistent with the transport predicates.
        /// </summary>
        public const float FullCoverageThreshold = FULL_COVERAGE_THRESHOLD;

        /// <summary>The +Y face index in <c>VoxelData.FaceChecks</c> order — where a downward sky column enters.</summary>
        private const int TOP_FACE = 2;

        /// <summary>The -Y face index in <c>VoxelData.FaceChecks</c> order — where a downward sky column leaves.</summary>
        private const int BOTTOM_FACE = 3;
    }
}
