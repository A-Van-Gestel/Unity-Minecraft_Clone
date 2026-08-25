using System.Runtime.CompilerServices;
using Data;
using Unity.Mathematics;

namespace Jobs.BurstData
{
    /// <summary>
    /// The single definition of the engine's light attenuation rule, shared by every system that must
    /// agree on it: the BFS flood-fill (<c>NeighborhoodLightingJob</c>), the borderless validation oracle
    /// (<c>LightingOracle</c>), and the cross-chunk skylight removal veto
    /// (<c>Helpers.CrossChunkLightModApplier.InChunkSkylightSupport</c>).
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
        /// <para>
        /// <b>Known limitation — entry cost only.</b> The charge is levied on the face light arrives
        /// through, never on the one it leaves by, so a volume is not paid for in a direction that enters
        /// through an open face and exits through a solid one. This is exact for every block the engine
        /// ships: opacity 0 has nothing to charge, and a fully-opaque partial is sealed in that direction
        /// by <see cref="ExitBlocked"/> instead. It is <b>wrong for a semi-transparent partial block</b>
        /// (custom bounds, opacity 1-14) — a stained-glass or leaf slab would cost nothing downward
        /// through its solid half, and <see cref="ObstructsSkyColumn"/> would correspondingly drop it from
        /// the heightmap, leaving the column below it undimmed. Nothing in <c>BlockDatabase.asset</c> is
        /// in that class, and the block editor warns when one is authored; closing it properly means an
        /// exit-cost term applied by every transport site, not a wider entry charge here (that would zero
        /// the light stored inside an opaque slab's own cell, which the mesher reads to shade it).
        /// </para>
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
        /// <b>Why the heightmap has to care</b> (<c>_FIXED_BUGS.md</c> Lighting #25): the heightmap is what makes
        /// <c>RecalculateSkylightForColumn</c> authoritative for sky <i>removal</i>. A slab that registers
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

        /// <summary>
        /// SS-2: the silhouette a block casts on an arbitrary plane through its cell — the general form
        /// of <see cref="AmbientOcclusionFaceSilhouette"/>, used because a custom mesh's face can lie
        /// inside its own cell rather than on a wall.
        /// </summary>
        /// <param name="block">The block being sampled.</param>
        /// <param name="meta">The placed voxel's raw metadata byte (selects the volume's rotation).</param>
        /// <param name="normalAxis">Axis the shaded plane is perpendicular to (0 = X, 1 = Y, 2 = Z).</param>
        /// <param name="planeCoord">The plane's coordinate on that axis, in the sampled cell's local space.</param>
        /// <param name="frontIsPositive">True when the shaded space lies on the plane's +axis side.</param>
        /// <param name="rectMin">Silhouette minimum corner on the two perpendicular axes.</param>
        /// <param name="rectMax">Silhouette maximum corner.</param>
        /// <returns>True when this block casts a silhouette on that plane.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool AmbientOcclusionPlaneSilhouette(in BlockTypeJobData block, byte meta,
            int normalAxis, float planeCoord, bool frontIsPositive, out float2 rectMin, out float2 rectMax)
        {
            if (!block.IsOpaque)
            {
                rectMin = float2.zero;
                rectMax = float2.zero;
                return false;
            }

            if (!block.HasCustomBounds)
            {
                // A full cell reaches every plane through it, and its silhouette is the whole face.
                rectMin = float2.zero;
                rectMax = new float2(1f, 1f);
                return true;
            }

            float3x3 rotationMatrix = BurstCustomMeshRotationUtility.GetRotationMatrix(
                block.MetadataSchema, meta, block.DefaultMetadata);
            BurstOcclusionUtility.RotateLocalBounds(block.BoundsMin, block.BoundsMax, in rotationMatrix,
                out float3 rotatedMin, out float3 rotatedMax);

            return BurstOcclusionUtility.GetPlaneSilhouette(rotatedMin, rotatedMax, normalAxis,
                planeCoord, frontIsPositive, out rectMin, out rectMax);
        }

        /// <summary>
        /// SS-2: how strongly an occluder shades a point at a given distance from its silhouette — the
        /// contact-shadow falloff, and the whole of the model's visual character.
        /// <para>
        /// <b>Distance, not coverage.</b> A fill fraction over a sub-cell box says how much volume is in
        /// the way, which for an occluder bounded by one plane varies near-linearly across the cell —
        /// exactly what a blend of two corner values already produces, which is why sub-cell sampling of
        /// it carried no information (<c>VOXEL_OCCLUSION_REFACTOR.md</c> finding F18).
        /// </para>
        /// <para>
        /// The profile is <c>(1 - t)²</c>: dark and tight against the occluder with a quick fade, rather
        /// than the straight ramp the corner blend approximates. It reaches zero exactly at
        /// <see cref="ContactShadowRadius"/>, so an occluder outside the sampled neighborhood cannot
        /// contribute and the model needs no clamping at the edges.
        /// </para>
        /// </summary>
        /// <param name="distance">Distance from the sample point to the silhouette, in cells.</param>
        /// <returns>The shadow strength, in <c>[0, 1]</c>; 1 in contact, 0 at the radius.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float ContactShadowFalloff(float distance)
        {
            float t = math.saturate(distance / ContactShadowRadius);
            float remaining = 1f - t;
            return remaining * remaining;
        }

        /// <summary>
        /// How far a contact shadow reaches from an occluder's silhouette, in cells.
        /// <para>
        /// <b>Pinned from both directions, not a tuning knob.</b> It is the largest radius the sampled
        /// 3×3 neighborhood can answer for — a silhouette outside that block is never nearer than one
        /// cell — and the smallest that keeps a wall's occlusion alive across a whole face: at 0.5 the
        /// center of a face in an inner corner between two walls computes 255, which is the
        /// interior-lightening signature of the defect VO-9b shipped and had to correct.
        /// </para>
        /// </summary>
        public const float ContactShadowRadius = 1f;

        /// <summary>
        /// Each of the four quadrants around a shaded point owns a quarter of its hemisphere, so an
        /// occluder filling one quadrant at contact removes a quarter of the light — reproducing the
        /// engine's long-standing <c>255 → 191</c> for one occluding neighbor, <c>128</c> for two and
        /// <c>64</c> for three.
        /// <para>
        /// The shares <b>sum</b> across quadrants: a single global strength constant could not
        /// reproduce both the one-occluder and the three-occluder depth at once.
        /// </para>
        /// <para>
        /// <b>Quadrants, not cells</b> (SS-3a). At a cell corner the two are the same thing — the four
        /// cells meeting there are the four quadrants — which is why a per-cell sum looked equivalent
        /// and shipped. Away from a corner they diverge, and the per-cell reading depends on where the
        /// grid lines fall rather than on the geometry: a straight wall arrives as three separate cell
        /// silhouettes, so its shadow scalloped between seam and cell center.
        /// </para>
        /// </summary>
        public const float QuadrantOcclusionShare = 0.25f;

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
