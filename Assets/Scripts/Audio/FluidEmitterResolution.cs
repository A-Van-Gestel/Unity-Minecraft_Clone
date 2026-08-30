using Data.Enums;
using Jobs.Data;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Audio
{
    /// <summary>
    /// One emitter the scan found: a merged run of bins of a single kind, with the stable world cell that
    /// identifies it from scan to scan.
    /// </summary>
    public struct FluidEmitterCandidate
    {
        /// <summary>Which loop this emitter plays.</summary>
        public FluidEmitterKind Kind;

        /// <summary>The world bin cell of the run's lowest bin — the identity a source is reclaimed by.</summary>
        public int3 Cell;

        /// <summary>Weighted centroid of the run's voxels, in voxel world space.</summary>
        public float3 Centroid;

        /// <summary>How many flowing voxels the run holds — how loud it should be.</summary>
        public int Weight;
    }

    /// <summary>
    /// The pure decision layer behind the fluid emitters (SOUND_ENGINE_DESIGN.md §5.2): turning the scan's
    /// bin grid into a ranked set of emitters, choosing which source carries each, and converting a
    /// cluster's size into a gain. Holds no state and touches no Unity objects, which is what lets the
    /// validation suite pin every decision without playing a sound.
    /// </summary>
    public static class FluidEmitterResolution
    {
        /// <summary>Widest source roster <see cref="AssignSlots"/> tracks, bounded by its taken-mask width.</summary>
        private const int MAX_TRACKED_SLOTS = 32;

        /// <summary>
        /// Collects the scan's bins into emitter candidates, ranked loudest first.
        /// </summary>
        /// <param name="bins">The completed scan's accumulation grid.</param>
        /// <param name="binOrigin">The grid's voxel-space origin, from the same scan.</param>
        /// <param name="candidates">Receives the ranked candidates.</param>
        /// <param name="maxCount">How many candidates to keep — normally the source roster size.</param>
        /// <returns>How many candidates were written.</returns>
        /// <remarks>
        /// <para>Bins of one kind that are <b>vertically adjacent in the same column</b> merge into a single
        /// candidate: a 20-block waterfall spans several bins and is one sound, not three stacked copies of
        /// itself beating against each other. Horizontally adjacent bins are deliberately left separate —
        /// a wide river <i>should</i> occupy more than one point in the mix, and the roster budget already
        /// caps how many of them are ever heard.</para>
        /// <para>Ranking is by weight, with grid order breaking ties, so an unchanged world always produces
        /// the same ordered set — the property the baselines assert against.</para>
        /// </remarks>
        public static int Collect(NativeArray<FluidEmitterBin> bins, int3 binOrigin,
            FluidEmitterCandidate[] candidates, int maxCount)
        {
            if (!bins.IsCreated || candidates == null) return 0;

            int limit = Mathf.Min(maxCount, candidates.Length);
            if (limit <= 0) return 0;

            int3 originCell = binOrigin >> FluidEmitterScanGeometry.BinShift;
            int count = 0;

            for (int kind = 0; kind < FluidEmitterScanGeometry.KindCount; kind++)
            for (int z = 0; z < FluidEmitterScanGeometry.BinsXZ; z++)
            for (int x = 0; x < FluidEmitterScanGeometry.BinsXZ; x++)
            {
                int y = 0;
                while (y < FluidEmitterScanGeometry.BinsY)
                {
                    int index = FluidEmitterScanGeometry.BinIndex(new int3(x, y, z), kind);
                    if (index < 0 || bins[index].Weight == 0)
                    {
                        y++;
                        continue;
                    }

                    int runBottom = y;
                    int weight = 0;
                    int3 sum = int3.zero;

                    while (y < FluidEmitterScanGeometry.BinsY)
                    {
                        int runIndex = FluidEmitterScanGeometry.BinIndex(new int3(x, y, z), kind);
                        if (runIndex < 0 || bins[runIndex].Weight == 0) break;

                        weight += bins[runIndex].Weight;
                        sum += bins[runIndex].SumPos;
                        y++;
                    }

                    FluidEmitterCandidate candidate = new FluidEmitterCandidate
                    {
                        Kind = (FluidEmitterKind)kind,
                        Cell = originCell + new int3(x, runBottom, z),
                        Centroid = new float3(sum.x, sum.y, sum.z) / weight,
                        Weight = weight,
                    };

                    count = Insert(candidates, count, limit, candidate);
                }
            }

            return count;
        }

        /// <summary>
        /// Chooses which looping source carries each candidate, in one pass over the whole set.
        /// </summary>
        /// <param name="slotCells">The world cell each source is currently emitting for.</param>
        /// <param name="slotKinds">The kind each source currently plays, index-aligned with <paramref name="slotCells"/>.</param>
        /// <param name="slotFades">Each source's fade position, index-aligned with <paramref name="slotCells"/>.</param>
        /// <param name="candidates">The candidates that should now be audible.</param>
        /// <param name="count">How many leading entries of <paramref name="candidates"/> are in play.</param>
        /// <param name="slots">Receives the chosen source per candidate, or -1 where none was available.</param>
        /// <returns>How many candidates were given a source.</returns>
        /// <remarks>
        /// Two passes, for the same reason <see cref="AmbienceResolution.AssignBedSlots"/> needs two: the
        /// choice is a set, not a sequence. Pass one lets every candidate reclaim the source already
        /// emitting for its own cell and kind — so a river that is still there keeps its source and its
        /// fade instead of restarting. Pass two gives the rest the quietest source nothing has claimed; a
        /// silent source is already the quietest, so free sources are taken before anything audible is
        /// interrupted.
        /// </remarks>
        public static int AssignSlots(int3[] slotCells, FluidEmitterKind[] slotKinds, float[] slotFades,
            FluidEmitterCandidate[] candidates, int count, int[] slots)
        {
            if (slotCells == null || slotKinds == null || slotFades == null || candidates == null || slots == null)
                return 0;

            int slotCount = Mathf.Min(Mathf.Min(slotCells.Length, slotKinds.Length),
                Mathf.Min(slotFades.Length, MAX_TRACKED_SLOTS));
            int candidateCount = Mathf.Min(count, Mathf.Min(candidates.Length, slots.Length));
            if (slotCount <= 0 || candidateCount <= 0) return 0;

            for (int c = 0; c < candidateCount; c++) slots[c] = -1;

            uint taken = 0u;
            int assigned = 0;

            for (int c = 0; c < candidateCount; c++)
            {
                for (int i = 0; i < slotCount; i++)
                {
                    if ((taken & (1u << i)) != 0u) continue;
                    if (slotFades[i] <= 0f) continue; // A silent source is emitting for nobody — pass two's job.
                    if (slotKinds[i] != candidates[c].Kind || !slotCells[i].Equals(candidates[c].Cell)) continue;

                    slots[c] = i;
                    taken |= 1u << i;
                    assigned++;
                    break;
                }
            }

            for (int c = 0; c < candidateCount; c++)
            {
                if (slots[c] >= 0) continue;

                int quietest = -1;
                float quietestFade = float.MaxValue;

                for (int i = 0; i < slotCount; i++)
                {
                    if ((taken & (1u << i)) != 0u || slotFades[i] >= quietestFade) continue;

                    quietestFade = slotFades[i];
                    quietest = i;
                }

                if (quietest < 0) continue;

                slots[c] = quietest;
                taken |= 1u << quietest;
                assigned++;
            }

            return assigned;
        }

        /// <summary>
        /// Converts a cluster's voxel count into a gain multiplier.
        /// </summary>
        /// <param name="weight">Flowing voxels in the cluster.</param>
        /// <param name="saturationWeight">Cluster size treated as fully loud. Must be positive.</param>
        /// <returns>A gain in [0, 1].</returns>
        /// <remarks>
        /// Square-rooted and saturating, so a single trickle is already clearly audible and a wide river
        /// does not keep getting louder without limit — the same perceptual shaping
        /// <see cref="AmbienceResolution.GainFromFade"/> applies to the beds.
        /// </remarks>
        public static float GainFromWeight(int weight, int saturationWeight)
        {
            if (weight <= 0 || saturationWeight <= 0) return 0f;

            return Mathf.Sqrt(Mathf.Clamp01(weight / (float)saturationWeight));
        }

        /// <summary>
        /// Composes a source's final volume from its fade, its cluster gain and the authored trims.
        /// </summary>
        /// <param name="fade">The source's fade position in [0, 1] — presence, not loudness.</param>
        /// <param name="clusterGain">The cluster's size-derived gain, from <see cref="GainFromWeight"/>.</param>
        /// <param name="trim">The kind's authored volume.</param>
        /// <param name="categoryGain">The Fluids category gain, or 1 when a mixer group carries it.</param>
        /// <returns>The volume to write to the source.</returns>
        /// <remarks>
        /// <para><b>Exactly one square root, and it is on the fade.</b>
        /// <see cref="AmbienceResolution.GainFromFade"/> is an
        /// equal-power curve for crossfading; <see cref="GainFromWeight"/> is already the perceptual shaping
        /// of cluster size. Folding the cluster gain into the fade target instead applies both roots to it —
        /// <c>(w/sat)^0.25</c> — which flattens cluster size almost out of existence and, because the fade
        /// then travels a shorter distance, makes a quiet emitter fade out in a fraction of the authored
        /// time. Composed here rather than inline in the director so the suite can see the whole product.</para>
        /// </remarks>
        public static float SourceVolume(float fade, float clusterGain, float trim, float categoryGain)
        {
            return AmbienceResolution.GainFromFade(fade) * Mathf.Clamp01(clusterGain) * trim * categoryGain;
        }

        /// <summary>
        /// The Unity-space translation every already-placed object needs when the world re-anchors.
        /// </summary>
        /// <param name="previousOriginVoxel">The origin before the shift.</param>
        /// <param name="currentOriginVoxel">The origin after it.</param>
        /// <returns>The offset to add to a stale Unity-space position.</returns>
        /// <remarks>
        /// <c>WorldOrigin.VoxelToUnity</c> subtracts the origin, so when the origin moves by <c>d</c> every
        /// Unity coordinate for a fixed voxel moves by <c>-d</c> — the same correction <c>World.ShiftOrigin</c>
        /// applies to the player. Emitters need it because they are the one placed thing the shift does not
        /// know about, and their voxel-space positions are deliberately immune to it, so nothing else would
        /// notice. Y is untouched: the origin only ever moves on XZ.
        /// </remarks>
        public static Vector3 OriginShiftDelta(Vector3Int previousOriginVoxel, Vector3Int currentOriginVoxel)
        {
            return new Vector3(
                previousOriginVoxel.x - currentOriginVoxel.x,
                0f,
                previousOriginVoxel.z - currentOriginVoxel.z);
        }

        /// <summary>
        /// Builds the distance rolloff curve an emitter source uses.
        /// </summary>
        /// <param name="minDistance">Distance within which the emitter plays at full gain, in blocks.</param>
        /// <param name="maxDistance">Distance at which it must be silent, in blocks.</param>
        /// <param name="samples">How many points to sample the falloff at, between the two. Minimum 2.</param>
        /// <returns>A curve over normalized distance <c>[0, 1]</c> of <paramref name="maxDistance"/>.</returns>
        /// <remarks>
        /// <para>Unity's built-in logarithmic rolloff does <b>not</b> reach zero: <c>maxDistance</c> is where
        /// it <i>stops attenuating</i>, so a source sits at <c>minDistance / maxDistance</c> forever beyond
        /// it. At a 6 m / 24 m emitter that floor is a quarter of full volume, which is why an unbounded
        /// waterfall could still be heard tens of blocks away.</para>
        /// <para>This keeps the inverse-distance shape that makes distance legible, then multiplies it by a
        /// window that reaches exactly zero at <paramref name="maxDistance"/>. Sampled into a curve rather
        /// than applied per frame in C# so Unity's own spatializer still does the panning.</para>
        /// <para>Interpolated piecewise-linearly between the samples rather than smoothed: smoothed tangents
        /// overshoot where the flat plateau meets the falloff, which puts gain above 1 and, further along,
        /// would let an emitter grow louder as the listener backs away.</para>
        /// </remarks>
        public static AnimationCurve BuildRolloffCurve(float minDistance, float maxDistance, int samples)
        {
            AnimationCurve curve = new AnimationCurve();
            if (maxDistance <= 0f) return curve;

            float plateau = Mathf.Clamp01(minDistance / maxDistance);
            curve.AddKey(0f, 1f);
            curve.AddKey(plateau, 1f);

            int steps = Mathf.Max(2, samples);
            for (int i = 1; i <= steps; i++)
            {
                float t = i / (float)steps;
                float normalized = Mathf.Lerp(plateau, 1f, t);
                float distance = normalized * maxDistance;

                // Inverse distance, windowed so the tail lands on zero instead of a constant floor.
                float inverse = distance <= minDistance ? 1f : minDistance / distance;
                float window = 1f - t * t;
                curve.AddKey(normalized, inverse * window);
            }

            SetLinearTangents(curve);
            return curve;
        }

        /// <summary>
        /// Rewrites a curve's tangents so it interpolates linearly between its keys.
        /// </summary>
        /// <param name="curve">The curve to flatten. Fewer than two keys is a no-op.</param>
        private static void SetLinearTangents(AnimationCurve curve)
        {
            int count = curve.length;
            if (count < 2) return;

            for (int i = 0; i < count; i++)
            {
                Keyframe key = curve[i];

                key.inTangent = i == 0 ? 0f : Slope(curve[i - 1], key);
                key.outTangent = i == count - 1 ? 0f : Slope(key, curve[i + 1]);

                curve.MoveKey(i, key);
            }
        }

        /// <summary>Slope of the straight line between two keyframes; zero when they share a time.</summary>
        /// <param name="from">The earlier keyframe.</param>
        /// <param name="to">The later keyframe.</param>
        /// <returns>The segment's slope.</returns>
        private static float Slope(Keyframe from, Keyframe to)
        {
            float span = to.time - from.time;
            return span <= 0f ? 0f : (to.value - from.value) / span;
        }

        /// <summary>
        /// Whether the listener moved far enough between frames that nothing it could hear is still nearby.
        /// </summary>
        /// <param name="previous">The listener's voxel cell last frame.</param>
        /// <param name="current">The listener's voxel cell now.</param>
        /// <param name="threshold">Distance in voxels beyond which the move counts as a jump.</param>
        /// <returns>True when the move was a teleport rather than travel.</returns>
        /// <remarks>
        /// Compared in <b>voxel</b> space, never Unity space: the engine re-anchors its render origin as the
        /// player travels (WS-*), which moves every Unity coordinate without the player going anywhere. A
        /// Unity-space test would read a re-anchor as a teleport and silence the world at random.
        /// </remarks>
        public static bool IsTeleport(int3 previous, int3 current, int threshold)
        {
            int3 delta = current - previous;
            long distanceSq = (long)delta.x * delta.x + (long)delta.y * delta.y + (long)delta.z * delta.z;
            return distanceSq > (long)threshold * threshold;
        }

        /// <summary>
        /// Inserts a candidate into the weight-ordered list, dropping the weakest when it is full.
        /// </summary>
        /// <param name="candidates">The ranked list.</param>
        /// <param name="count">How many entries it currently holds.</param>
        /// <param name="limit">Its capacity.</param>
        /// <param name="candidate">The candidate to insert.</param>
        /// <returns>The new entry count.</returns>
        private static int Insert(FluidEmitterCandidate[] candidates, int count, int limit,
            FluidEmitterCandidate candidate)
        {
            if (count == limit && candidate.Weight <= candidates[limit - 1].Weight) return count;

            int insert = count < limit ? count : limit - 1;
            while (insert > 0 && candidates[insert - 1].Weight < candidate.Weight)
            {
                candidates[insert] = candidates[insert - 1];
                insert--;
            }

            candidates[insert] = candidate;
            return count < limit ? count + 1 : count;
        }
    }
}
