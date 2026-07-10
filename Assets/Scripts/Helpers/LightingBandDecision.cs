using Jobs.BurstData;
using Unity.Mathematics;

namespace Helpers
{
    /// <summary>
    /// One chunk's contribution to the lighting Y-band derivation (LI-2): the extent and value of its
    /// <b>uniform top region</b> — the run of sections, scanned from the top of the chunk downward, that
    /// are voxel-empty (null sections, i.e. all air) AND hold one single uniform light value. Above
    /// <see cref="UniformFromY"/> every voxel of the chunk reads as packed air with light
    /// <see cref="UniformLight"/>, so the banded lighting job can answer reads there from this summary
    /// instead of gathering the rows. Produced by <c>ChunkData.GetLightingBandTop()</c>;
    /// consumed by <see cref="LightingBandDecision.DeriveBandHeight"/>.
    /// </summary>
    public struct LightingBandChunkTop
    {
        /// <summary>
        /// First (lowest) Y of the uniform top region. <c>ChunkMath.CHUNK_HEIGHT</c> when the chunk's top
        /// section is occupied or light-varied (no uniform region). Always a multiple of the section size.
        /// </summary>
        public int UniformFromY;

        /// <summary>
        /// The packed ushort light value shared by every voxel at/above <see cref="UniformFromY"/>
        /// (sky-only — a compact uniform section carries zero blocklight by construction).
        /// Meaningless when <see cref="UniformFromY"/> is <c>ChunkMath.CHUNK_HEIGHT</c>.
        /// </summary>
        public ushort UniformLight;

        /// <summary>
        /// True when the chunk is absent (unloaded/uncreated). The gather sentinel-fills a missing
        /// neighbor's region, the job treats its cells as out-of-world, and it never receives or supplies
        /// light — so a missing chunk is band-neutral except that virtual reads must keep returning the
        /// sentinel for its region.
        /// </summary>
        public bool IsMissing;

        /// <summary>A missing-chunk marker (region from Y=0 so it never extends the band).</summary>
        public static LightingBandChunkTop Missing => new LightingBandChunkTop
        {
            UniformFromY = 0, UniformLight = 0, IsMissing = true,
        };
    }

    /// <summary>
    /// Pure derivation of the lighting job's Y-band height (LI-2, see
    /// Documentation/Design/PERFORMANCE_IMPROVEMENTS_REPORT.md §LI-2): the number of bottom-anchored rows
    /// <c>[0, bandHeight)</c> of the halo-padded volume the <c>NeighborhoodLightingJob</c> must actually
    /// gather, scan, and extract. Everything at/above the returned height is guaranteed — by the rules
    /// below — to be uniform air whose light the job can answer virtually and can never change, so
    /// skipping those rows is bit-identical to processing them. Shared by the production scheduler and the
    /// editor validation harness (the <see cref="LightingScanDecision"/>/<c>LightingCompletionPass</c>
    /// shared-decision pattern), so the two can never disagree on a band.
    /// </summary>
    public static class LightingBandDecision
    {
        /// <summary>
        /// Extra fully-gathered rows kept above the highest non-uniform section / queued BFS node.
        /// <para>
        /// LOAD-BEARING INVARIANT (the vertical sibling of <c>ChunkMath.MAX_LIGHTING_BFS_REACH</c>): this
        /// must be ≥ the farthest any BFS wave can RAISE OR LOWER a light value above its source. Light
        /// attenuates by at least 1 per step everywhere except the downward vertical-sunlight rule (which
        /// only travels down, away from the band top), so a wave from a source at <c>y</c> can alter
        /// values no higher than <c>y + 15</c> — one full section (16) covers it. If a future change lets
        /// light climb further per step (e.g. an upward no-attenuation rule), bump this and re-verify the
        /// band differential baselines (Validate Lighting Engine, B71+ and the Step-3 differential fuzz).
        /// </para>
        /// </summary>
        public const int BandHeadroomVoxels = ChunkMath.SECTION_SIZE;

        /// <summary>Packed light of a full-sky (15, no blocklight) uniform region — the only center
        /// top-region value under which the sunlight column-recalc's above-heightmap pass writes nothing.</summary>
        private static readonly ushort s_fullSkyPacked = LightBitMapping.PackLightData(15, 0, 0, 0);

        /// <summary>
        /// Derives the band height for one lighting job over a center chunk and its 8 neighbors
        /// (compass parameter order matches <c>NeighborhoodLightingJob.SetGatherSources</c>).
        /// Returns <c>ChunkMath.CHUNK_HEIGHT</c> (full height, banding disabled) whenever a rule cannot
        /// prove the skipped region inert:
        /// <list type="bullet">
        /// <item><b>Column-recalc rule:</b> a queued sunlight column recalculation walks the sky region
        /// above the heightmap; skipping it is only valid when the center's top region is uniform
        /// full-sky (the walk would write nothing there). Any other top value → full height.</item>
        /// <item><b>Cross-seam consistency rule:</b> for each cardinal neighbor, the mutual uniform region
        /// must produce no cross-seam writes: the center's uniform sky may not exceed the neighbor's by
        /// more than the 1-step attenuation (a brighter center would repeatedly re-write the virtual halo
        /// and duplicate cross-chunk mods), and — only when an edge check runs — the neighbor's may not
        /// exceed the center's (the edge check would write corrections into the skipped rows).</item>
        /// <item><b>Coverage rule:</b> the band covers every chunk's non-uniform ceiling and every queued
        /// BFS node, plus <see cref="BandHeadroomVoxels"/> rows of wave headroom.</item>
        /// </list>
        /// </summary>
        /// <param name="center">The center chunk's uniform-top summary (never missing in practice).</param>
        /// <param name="west">West (−X) neighbor summary, or <see cref="LightingBandChunkTop.Missing"/>.</param>
        /// <param name="east">East (+X) neighbor summary.</param>
        /// <param name="south">South (−Z) neighbor summary.</param>
        /// <param name="north">North (+Z) neighbor summary.</param>
        /// <param name="southWest">South-west diagonal neighbor summary.</param>
        /// <param name="northWest">North-west diagonal neighbor summary.</param>
        /// <param name="southEast">South-east diagonal neighbor summary.</param>
        /// <param name="northEast">North-east diagonal neighbor summary.</param>
        /// <param name="maxQueuedNodeY">Highest Y of any queued sun/block BFS node for this job, or −1
        /// when the queues are empty (column-recalc entries are covered by the column-recalc rule and the
        /// heightmap's containment in an occupied section, not by this term).</param>
        /// <param name="hasColumnRecalcs">Whether any sunlight column recalculations are queued.</param>
        /// <param name="performEdgeCheck">Whether the job will run the border edge-consistency check.</param>
        /// <returns>The band height in rows, in <c>(0, ChunkMath.CHUNK_HEIGHT]</c>; the full chunk height
        /// disables banding for this job.</returns>
        public static int DeriveBandHeight(
            in LightingBandChunkTop center,
            in LightingBandChunkTop west, in LightingBandChunkTop east,
            in LightingBandChunkTop south, in LightingBandChunkTop north,
            in LightingBandChunkTop southWest, in LightingBandChunkTop northWest,
            in LightingBandChunkTop southEast, in LightingBandChunkTop northEast,
            int maxQueuedNodeY,
            bool hasColumnRecalcs,
            bool performEdgeCheck)
        {
            // Defensive: a job over a missing center never happens; if it did, band nothing away.
            if (center.IsMissing)
                return ChunkMath.CHUNK_HEIGHT;

            // Column-recalc rule. (When the center has no uniform region its UniformLight is meaningless,
            // but the coverage rule already forces full height in that case, so the early return here is
            // merely the same answer sooner.)
            if (hasColumnRecalcs && center.UniformLight != s_fullSkyPacked)
                return ChunkMath.CHUNK_HEIGHT;

            // Cross-seam consistency rule — cardinals only: in-job halo writes and edge-check reads reach
            // exactly one voxel past a face seam, and the ±1 rim of a diagonal region is never written
            // (a corner halo cell has no in-center face neighbor).
            if (!CardinalPairConsistent(in center, in west, performEdgeCheck) ||
                !CardinalPairConsistent(in center, in east, performEdgeCheck) ||
                !CardinalPairConsistent(in center, in south, performEdgeCheck) ||
                !CardinalPairConsistent(in center, in north, performEdgeCheck))
                return ChunkMath.CHUNK_HEIGHT;

            // Coverage rule: every non-uniform ceiling (missing chunks contribute nothing — their region
            // is all-sentinel from Y=0) and every queued wave source, plus headroom.
            int baseTop = center.UniformFromY;
            baseTop = math.max(baseTop, west.UniformFromY);
            baseTop = math.max(baseTop, east.UniformFromY);
            baseTop = math.max(baseTop, south.UniformFromY);
            baseTop = math.max(baseTop, north.UniformFromY);
            baseTop = math.max(baseTop, southWest.UniformFromY);
            baseTop = math.max(baseTop, northWest.UniformFromY);
            baseTop = math.max(baseTop, southEast.UniformFromY);
            baseTop = math.max(baseTop, northEast.UniformFromY);

            int top = math.max(baseTop, maxQueuedNodeY + 1);
            return math.min(ChunkMath.CHUNK_HEIGHT, top + BandHeadroomVoxels);
        }

        /// <summary>
        /// Whether the mutual uniform region of the center and one cardinal neighbor provably produces no
        /// cross-seam writes (see the cross-seam consistency rule on <see cref="DeriveBandHeight"/>).
        /// A missing neighbor is always consistent: the job's sentinel guards skip its cells before any
        /// write or seed, on both the halo-write and edge-check paths.
        /// </summary>
        /// <param name="center">The center chunk's summary.</param>
        /// <param name="neighbor">One cardinal neighbor's summary.</param>
        /// <param name="performEdgeCheck">Whether the edge check's center-ward writes must also be ruled out.</param>
        /// <returns>True when the pair's skipped rows are inert.</returns>
        private static bool CardinalPairConsistent(
            in LightingBandChunkTop center, in LightingBandChunkTop neighbor, bool performEdgeCheck)
        {
            if (neighbor.IsMissing)
                return true;

            // Uniform regions are air (opacity 0), so the cross-seam expectation in both directions is
            // the plain 1-step attenuation; uniform regions carry zero blocklight, so only sky matters.
            int centerSky = LightBitMapping.GetSkyLight(center.UniformLight);
            int neighborSky = LightBitMapping.GetSkyLight(neighbor.UniformLight);

            // Center→neighbor halo writes (PropagateLight's cross-seam arm) — checked unconditionally:
            // a virtualized halo cell does not retain the write, so a repeatable write would re-fire and
            // duplicate its cross-chunk mod.
            if (centerSky - 1 > neighborSky)
                return false;

            // Neighbor→center edge-check writes (CheckEdgeVoxel) — only reachable when the job edge-checks.
            if (performEdgeCheck && neighborSky - 1 > centerSky)
                return false;

            return true;
        }
    }
}
