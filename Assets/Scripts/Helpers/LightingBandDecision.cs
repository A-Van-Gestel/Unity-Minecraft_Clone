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
    /// One chunk's contribution to the lighting bottom-band derivation (LI-2 bottom band): the extent
    /// of its <b>inert-dark bottom region</b> — the run of sections, scanned from the bottom of the
    /// chunk upward, whose light is uniformly ZERO and that contain no light-emitting voxels. Below
    /// <see cref="InertUpToY"/> every voxel of the chunk reads as light 0, can neither receive nor
    /// supply light, and needs no emission-sync visit, so the banded lighting job can skip gathering
    /// those rows entirely. Produced by <c>ChunkData.GetLightingBandBottom()</c>; consumed by
    /// <see cref="LightingBandDecision"/>. Unlike the top region there is no uniform light VALUE to
    /// carry — inert-dark is zero by definition.
    /// </summary>
    public struct LightingBandChunkBottom
    {
        /// <summary>
        /// First (lowest) Y ABOVE the inert-dark bottom region — the region is <c>[0, InertUpToY)</c>.
        /// 0 when the chunk's bottom section is lit, light-varied, or holds an emitter (no skippable
        /// region). Always a multiple of the section size.
        /// </summary>
        public int InertUpToY;

        /// <summary>
        /// True when the chunk is absent (unloaded/uncreated). Mirrors
        /// <see cref="LightingBandChunkTop.IsMissing"/>: the harness gather sentinel-fills a missing
        /// neighbor's rows, so its virtual below-band reads must keep returning the out-of-world
        /// sentinel rather than dark-zero. (The production pooled path never passes Missing — its
        /// missing-neighbor maps are zero-FILLED, i.e. genuinely dark.)
        /// </summary>
        public bool IsMissing;

        /// <summary>A missing-chunk marker. Full-height and band-neutral: skipping a missing chunk's
        /// rows is always inert because every read there is answered by the sentinel either way.</summary>
        public static LightingBandChunkBottom Missing => new LightingBandChunkBottom
        {
            InertUpToY = ChunkMath.CHUNK_HEIGHT, IsMissing = true,
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
        /// Derives the band's bottom edge for one lighting job over a center chunk and its 8 neighbors
        /// (LI-2 bottom band; parameter order matches <see cref="DeriveBandHeight"/>). The job then
        /// gathers/scans/extracts only <c>[bandMinY, bandHeight)</c>. Returns 0 (no bottom banding)
        /// whenever a rule cannot prove the skipped rows inert:
        /// <list type="bullet">
        /// <item><b>Column-recalc rule:</b> a queued sunlight column recalculation walks from the
        /// heightmap down to Y=0, reading and writing every row — unlike the top side there is no
        /// escape condition, so any recalc forces the bottom to 0.</item>
        /// <item><b>Inert-dark coverage:</b> rows below the returned Y must be inert-dark in all nine
        /// chunks (<see cref="LightingBandChunkBottom"/>: light uniformly zero AND no emitters). The
        /// emissive gate is load-bearing twice over: the center's emission-sync scan must visit any
        /// unstamped emitter, and the RGB edge check substitutes an opaque CARDINAL-halo block's
        /// emission for its dark stored light — both end the summary's run, so neither can hide below
        /// the band. It also keeps the banded steady state self-consistent: an emitter always ends the
        /// run, so it is always in-band and always stamped, and a "dark compacted section with an
        /// unstamped lamp the band never rescans" state is unreachable.</item>
        /// <item><b>Wave coverage:</b> the band keeps <see cref="BandHeadroomVoxels"/> rows below the
        /// lowest queued BFS node (attenuating waves lose ≥1 level per step, so a wave from
        /// <c>y</c> can neither write nor read below <c>y − headroom</c>).</item>
        /// <item><b>Descending-sunlight rule:</b> the vertical no-attenuation sunlight rule travels
        /// DOWN — toward the bottom band — and headroom does not bound it. A sky-15 descent only runs
        /// down CENTER columns (halo cells are never re-enqueued) and stops at that column's heightmap
        /// block (the highest block with any opacity), continuing at most one attenuating headroom below
        /// a partial-opacity top — so the band floor stays
        /// <see cref="BandHeadroomVoxels"/> under the center's lowest heightmap entry.</item>
        /// </list>
        /// Cross-seam consistency needs no bottom rule: both sides of every seam are uniformly zero in
        /// the skipped rows, and a zero source can never out-write anything.
        /// </summary>
        /// <param name="center">The center chunk's inert-dark summary (never missing in practice).</param>
        /// <param name="west">West (−X) neighbor summary, or <see cref="LightingBandChunkBottom.Missing"/>.</param>
        /// <param name="east">East (+X) neighbor summary.</param>
        /// <param name="south">South (−Z) neighbor summary.</param>
        /// <param name="north">North (+Z) neighbor summary.</param>
        /// <param name="southWest">South-west diagonal neighbor summary.</param>
        /// <param name="northWest">North-west diagonal neighbor summary.</param>
        /// <param name="southEast">South-east diagonal neighbor summary.</param>
        /// <param name="northEast">North-east diagonal neighbor summary.</param>
        /// <param name="minQueuedNodeY">Lowest Y of any queued sun/block BFS node for this job, or
        /// <c>int.MaxValue</c> when the queues are empty.</param>
        /// <param name="hasColumnRecalcs">Whether any sunlight column recalculations are queued.</param>
        /// <param name="minCenterHeightmapY">The center chunk's lowest heightmap entry
        /// (<c>ChunkData.GetHeightmapMinY()</c>).</param>
        /// <returns>The band's first gathered row, in <c>[0, ChunkMath.CHUNK_HEIGHT)</c>; 0 disables
        /// bottom banding for this job.</returns>
        public static int DeriveBandMinY(
            in LightingBandChunkBottom center,
            in LightingBandChunkBottom west, in LightingBandChunkBottom east,
            in LightingBandChunkBottom south, in LightingBandChunkBottom north,
            in LightingBandChunkBottom southWest, in LightingBandChunkBottom northWest,
            in LightingBandChunkBottom southEast, in LightingBandChunkBottom northEast,
            int minQueuedNodeY,
            bool hasColumnRecalcs,
            int minCenterHeightmapY)
        {
            // Defensive: a job over a missing center never happens; if it did, band nothing away.
            if (center.IsMissing)
                return 0;

            // Column-recalc rule — PASS 2 unconditionally walks to Y=0.
            if (hasColumnRecalcs)
                return 0;

            // Inert-dark coverage: the skipped rows must be dark and emitter-free in every chunk
            // (a missing chunk is neutral — its InertUpToY is full height by construction).
            int floor = center.InertUpToY;
            floor = math.min(floor, west.InertUpToY);
            floor = math.min(floor, east.InertUpToY);
            floor = math.min(floor, south.InertUpToY);
            floor = math.min(floor, north.InertUpToY);
            floor = math.min(floor, southWest.InertUpToY);
            floor = math.min(floor, northWest.InertUpToY);
            floor = math.min(floor, southEast.InertUpToY);
            floor = math.min(floor, northEast.InertUpToY);

            // Wave coverage + descending-sunlight rules.
            floor = math.min(floor, minQueuedNodeY - BandHeadroomVoxels);
            floor = math.min(floor, minCenterHeightmapY - BandHeadroomVoxels);

            return math.max(0, floor);
        }

        /// <summary>
        /// Builds the job's 3×3 below-band table (the bottom mirror of <see cref="BuildTopLightTable"/>):
        /// a present chunk's below-band rows are inert-DARK by derivation, so its entry is packed light 0;
        /// <c>uint.MaxValue</c> marks a missing chunk (virtual reads return the out-of-world sentinels for
        /// its region, matching its gathered — sentinel-filled — rows).
        /// </summary>
        /// <param name="center">The center chunk's inert-dark summary.</param>
        /// <param name="west">West neighbor summary.</param>
        /// <param name="east">East neighbor summary.</param>
        /// <param name="south">South neighbor summary.</param>
        /// <param name="north">North neighbor summary.</param>
        /// <param name="southWest">South-west neighbor summary.</param>
        /// <param name="northWest">North-west neighbor summary.</param>
        /// <param name="southEast">South-east neighbor summary.</param>
        /// <param name="northEast">North-east neighbor summary.</param>
        /// <returns>The [cx][cz]-indexed below-band table.</returns>
        public static uint3x3 BuildBottomLightTable(
            in LightingBandChunkBottom center,
            in LightingBandChunkBottom west, in LightingBandChunkBottom east,
            in LightingBandChunkBottom south, in LightingBandChunkBottom north,
            in LightingBandChunkBottom southWest, in LightingBandChunkBottom northWest,
            in LightingBandChunkBottom southEast, in LightingBandChunkBottom northEast)
        {
            return new uint3x3(
                new uint3(BottomEntry(in southWest), BottomEntry(in west), BottomEntry(in northWest)),
                new uint3(BottomEntry(in south), BottomEntry(in center), BottomEntry(in north)),
                new uint3(BottomEntry(in southEast), BottomEntry(in east), BottomEntry(in northEast)));
        }

        /// <summary>One chunk's <see cref="BuildBottomLightTable"/> entry: packed dark (0) for a present
        /// chunk's inert region, or <c>uint.MaxValue</c> for a missing chunk.</summary>
        /// <param name="bottom">The chunk's inert-dark summary.</param>
        /// <returns>The table entry value.</returns>
        private static uint BottomEntry(in LightingBandChunkBottom bottom)
        {
            return bottom.IsMissing ? uint.MaxValue : 0u;
        }

        /// <summary>
        /// Builds the job's 3×3 above-band uniform-light table (<c>NeighborhoodLightingJob.BandTopLight</c>)
        /// from the nine uniform-top summaries. Layout matches the job's <c>BandColumn</c> mapping: column
        /// index = the West/center/East third of the padded X axis, component index = the South/center/North
        /// third of Z. A present chunk's entry is its uniform packed light; <c>uint.MaxValue</c> marks a
        /// missing chunk (virtual reads return the out-of-world sentinels for its region).
        /// </summary>
        /// <param name="center">The center chunk's uniform-top summary.</param>
        /// <param name="west">West neighbor summary.</param>
        /// <param name="east">East neighbor summary.</param>
        /// <param name="south">South neighbor summary.</param>
        /// <param name="north">North neighbor summary.</param>
        /// <param name="southWest">South-west neighbor summary.</param>
        /// <param name="northWest">North-west neighbor summary.</param>
        /// <param name="southEast">South-east neighbor summary.</param>
        /// <param name="northEast">North-east neighbor summary.</param>
        /// <returns>The [cx][cz]-indexed uniform-light table.</returns>
        public static uint3x3 BuildTopLightTable(
            in LightingBandChunkTop center,
            in LightingBandChunkTop west, in LightingBandChunkTop east,
            in LightingBandChunkTop south, in LightingBandChunkTop north,
            in LightingBandChunkTop southWest, in LightingBandChunkTop northWest,
            in LightingBandChunkTop southEast, in LightingBandChunkTop northEast)
        {
            return new uint3x3(
                new uint3(TableEntry(in southWest), TableEntry(in west), TableEntry(in northWest)),
                new uint3(TableEntry(in south), TableEntry(in center), TableEntry(in north)),
                new uint3(TableEntry(in southEast), TableEntry(in east), TableEntry(in northEast)));
        }

        /// <summary>One chunk's <see cref="BuildTopLightTable"/> entry: its uniform packed light, or
        /// <c>uint.MaxValue</c> for a missing chunk.</summary>
        /// <param name="top">The chunk's uniform-top summary.</param>
        /// <returns>The table entry value.</returns>
        private static uint TableEntry(in LightingBandChunkTop top)
        {
            return top.IsMissing ? uint.MaxValue : top.UniformLight;
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
