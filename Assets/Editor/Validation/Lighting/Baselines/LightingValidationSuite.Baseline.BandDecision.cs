using System.Collections.Generic;
using Editor.Validation.Lighting.Framework;
using Helpers;
using Jobs.BurstData;
using UnityEngine;
using Scenario = Editor.Validation.Framework.Scenario;

namespace Editor.Validation.Lighting
{
    /// <summary>
    /// Baseline scenarios for the LI-2 lighting Y-band derivation (<see cref="LightingBandDecision"/> +
    /// <c>ChunkData.GetLightingBandTop</c>) — the pure decision layer that tells the banded lighting job
    /// how many bottom-anchored rows it must gather/scan/extract. These guard the derivation's three
    /// rules (column-recalc, cross-seam consistency, coverage + headroom) and the section-metadata
    /// summary they consume, BEFORE the band is plumbed into <c>NeighborhoodLightingJob</c>: a wrong
    /// answer here becomes a truncated sunlight column or a C3-class cross-chunk darkening bug once the
    /// job trusts it. Self-registered via the <see cref="AddBandDecisionBaselineScenarios"/> hook
    /// (the <c>Baselines/</c> group-partial pattern).
    /// </summary>
    public static partial class LightingValidationSuite
    {
        /// <summary>Registers the LI-2 band-derivation baselines (called from <c>AddBaselineScenarios</c>).</summary>
        /// <param name="scenarios">The scenario list to append to.</param>
        static partial void AddBandDecisionBaselineScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario(
                "B71: ChunkData.GetLightingBandTop tracks the uniform top region through unlit, lit, and high-blocklight states (LI-2)",
                Baseline_BandTopMetadataTracksSectionStates));
            scenarios.Add(new Scenario(
                "B72: Band coverage rule — non-uniform ceilings and queued BFS nodes are covered with one section of headroom, clamped to chunk height (LI-2)",
                Baseline_BandCoverageRule));
            scenarios.Add(new Scenario(
                "B73: Band full-height fallbacks — column recalc over a non-full-sky top, cross-seam uniform mismatches, and edge-check asymmetry (LI-2)",
                Baseline_BandFullHeightFallbacks));
            scenarios.Add(new Scenario(
                "B74: Band derivation over REAL converged chunk metadata — superflat bands tight, a high lamp forces full height (LI-2)",
                Baseline_BandDerivationOnRealChunks));
        }

        /// <summary>The packed light value of a fully-sunlit uniform region (sky 15, no blocklight).</summary>
        private static ushort BandFullSky => LightBitMapping.PackLightData(15, 0, 0, 0);

        /// <summary>A lit-chunk uniform-top summary: all air above the floor section, sky 15.</summary>
        private static LightingBandChunkTop BandLitTop => new LightingBandChunkTop
        {
            UniformFromY = ChunkMath.SECTION_SIZE, UniformLight = BandFullSky,
        };

        /// <summary>An unlit-chunk uniform-top summary: all air above the floor section, light 0.</summary>
        private static LightingBandChunkTop BandUnlitTop => new LightingBandChunkTop
        {
            UniformFromY = ChunkMath.SECTION_SIZE, UniformLight = 0,
        };

        /// <summary>Derives the band for a neighborhood that is the same summary in all 9 slots.</summary>
        /// <param name="all">The summary used for the center and every neighbor.</param>
        /// <param name="maxQueuedNodeY">Highest queued BFS node Y, or −1.</param>
        /// <param name="hasColumnRecalcs">Whether sunlight column recalcs are queued.</param>
        /// <param name="performEdgeCheck">Whether the job will edge-check.</param>
        /// <returns>The derived band height.</returns>
        private static int DeriveUniformNeighborhood(in LightingBandChunkTop all,
            int maxQueuedNodeY = -1, bool hasColumnRecalcs = false, bool performEdgeCheck = false)
        {
            return LightingBandDecision.DeriveBandHeight(in all,
                in all, in all, in all, in all, in all, in all, in all, in all,
                maxQueuedNodeY, hasColumnRecalcs, performEdgeCheck);
        }

        /// <summary>
        /// B71: the section-metadata summary must mirror what the job-map fills would produce. A
        /// superflat world reads uniform-0 air above the floor section before lighting, uniform full-sky
        /// after initial lighting, and loses the uniform region entirely once a high lamp's blocklight
        /// gradient reaches the top section.
        /// </summary>
        /// <returns>True when all three states summarize correctly.</returns>
        private static bool Baseline_BandTopMetadataTracksSectionStates()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(10, TestBlockPalette.Stone);
            world.RecalculateHeightmaps();

            Vector2Int center = new Vector2Int(1, 1);
            LightingBandChunkTop top = world.GetChunkData(center).GetLightingBandTop();
            bool ok = LightingAssert.IsTrue(
                top.UniformFromY == ChunkMath.SECTION_SIZE && top.UniformLight == 0 && !top.IsMissing,
                "B71: unlit superflat summarizes as uniform-0 air above the floor section",
                $"expected UniformFromY={ChunkMath.SECTION_SIZE} light=0, got UniformFromY={top.UniformFromY} light=0x{top.UniformLight:X4}");

            world.RunInitialLighting();
            top = world.GetChunkData(center).GetLightingBandTop();
            ok &= LightingAssert.IsTrue(
                top.UniformFromY == ChunkMath.SECTION_SIZE && top.UniformLight == BandFullSky,
                "B71: lit superflat summarizes as uniform full-sky above the floor section",
                $"expected UniformFromY={ChunkMath.SECTION_SIZE} light=0x{BandFullSky:X4}, got UniformFromY={top.UniformFromY} light=0x{top.UniformLight:X4}");

            // A lamp at y=100 spills blocklight up to y=115 — into the top section (112..127), whose
            // light is then a gradient: no uniform top region remains.
            world.PlaceBlock(new Vector3Int(24, 100, 24), TestBlockPalette.LampWhite);
            world.RunToConvergence();
            top = world.GetChunkData(center).GetLightingBandTop();
            ok &= LightingAssert.IsTrue(top.UniformFromY == ChunkMath.CHUNK_HEIGHT,
                "B71: a high lamp's blocklight gradient in the top section dissolves the uniform region",
                $"expected UniformFromY={ChunkMath.CHUNK_HEIGHT}, got UniformFromY={top.UniformFromY} light=0x{top.UniformLight:X4}");

            return ok;
        }

        /// <summary>
        /// B72: the coverage rule bands at the non-uniform ceiling plus one headroom section, follows
        /// the highest queued BFS node, respects the tallest chunk in the neighborhood, and clamps at
        /// the chunk height.
        /// </summary>
        /// <returns>True when every coverage case derives the expected height.</returns>
        private static bool Baseline_BandCoverageRule()
        {
            LightingBandChunkTop lit = BandLitTop;
            const int expectQuiescent = ChunkMath.SECTION_SIZE + LightingBandDecision.BandHeadroomVoxels;

            bool ok = LightingAssert.IsTrue(
                DeriveUniformNeighborhood(in lit) == expectQuiescent,
                "B72: quiescent lit neighborhood bands at ceiling + headroom",
                $"expected {expectQuiescent}, got {DeriveUniformNeighborhood(in lit)}");

            int withNode = DeriveUniformNeighborhood(in lit, maxQueuedNodeY: 100);
            ok &= LightingAssert.IsTrue(withNode == 100 + 1 + LightingBandDecision.BandHeadroomVoxels,
                "B72: a queued BFS node raises the band to cover it plus headroom",
                $"expected {100 + 1 + LightingBandDecision.BandHeadroomVoxels}, got {withNode}");

            int clamped = DeriveUniformNeighborhood(in lit, maxQueuedNodeY: ChunkMath.CHUNK_HEIGHT - 8);
            ok &= LightingAssert.IsTrue(clamped == ChunkMath.CHUNK_HEIGHT,
                "B72: a node near the top clamps the band to the chunk height",
                $"expected {ChunkMath.CHUNK_HEIGHT}, got {clamped}");

            // One tall neighbor (non-uniform up to y=64) must raise the whole neighborhood's band.
            LightingBandChunkTop tallEast = new LightingBandChunkTop { UniformFromY = 64, UniformLight = BandFullSky };
            int tall = LightingBandDecision.DeriveBandHeight(in lit,
                in lit, in tallEast, in lit, in lit, in lit, in lit, in lit, in lit,
                maxQueuedNodeY: -1, hasColumnRecalcs: false, performEdgeCheck: false);
            ok &= LightingAssert.IsTrue(tall == 64 + LightingBandDecision.BandHeadroomVoxels,
                "B72: the tallest neighbor's non-uniform ceiling governs the band",
                $"expected {64 + LightingBandDecision.BandHeadroomVoxels}, got {tall}");

            // A missing neighbor is band-neutral (its region is all-sentinel from Y=0).
            LightingBandChunkTop missing = LightingBandChunkTop.Missing;
            int withMissing = LightingBandDecision.DeriveBandHeight(in lit,
                in lit, in missing, in lit, in lit, in lit, in lit, in lit, in lit,
                maxQueuedNodeY: -1, hasColumnRecalcs: false, performEdgeCheck: false);
            ok &= LightingAssert.IsTrue(withMissing == expectQuiescent,
                "B72: a missing neighbor neither extends nor breaks the band",
                $"expected {expectQuiescent}, got {withMissing}");

            return ok;
        }

        /// <summary>
        /// B73: every rule that cannot prove the skipped region inert must fall back to full height —
        /// a column recalc over a non-full-sky top (the 0→15 initial flood needs the sky rows), a
        /// brighter-center cross-seam pair (virtual halo writes would re-fire and duplicate mods), and
        /// a brighter-neighbor pair exactly when the edge check would write corrections center-ward.
        /// </summary>
        /// <returns>True when each fallback fires (and does not fire) as specified.</returns>
        private static bool Baseline_BandFullHeightFallbacks()
        {
            LightingBandChunkTop lit = BandLitTop;
            LightingBandChunkTop unlit = BandUnlitTop;
            const int expectBanded = ChunkMath.SECTION_SIZE + LightingBandDecision.BandHeadroomVoxels;

            int recalcUnlit = DeriveUniformNeighborhood(in unlit, hasColumnRecalcs: true);
            bool ok = LightingAssert.IsTrue(recalcUnlit == ChunkMath.CHUNK_HEIGHT,
                "B73: column recalc over a non-full-sky top forces full height",
                $"expected {ChunkMath.CHUNK_HEIGHT}, got {recalcUnlit}");

            int recalcLit = DeriveUniformNeighborhood(in lit, hasColumnRecalcs: true);
            ok &= LightingAssert.IsTrue(recalcLit == expectBanded,
                "B73: column recalc over a full-sky top still bands",
                $"expected {expectBanded}, got {recalcLit}");

            // Lit center vs unlit east neighbor: the center's sky-15 region would repeatedly write the
            // neighbor's virtual halo (15−1 > 0) — inconsistent regardless of the edge check.
            int brightCenter = LightingBandDecision.DeriveBandHeight(in lit,
                in lit, in unlit, in lit, in lit, in lit, in lit, in lit, in lit,
                maxQueuedNodeY: -1, hasColumnRecalcs: false, performEdgeCheck: false);
            ok &= LightingAssert.IsTrue(brightCenter == ChunkMath.CHUNK_HEIGHT,
                "B73: a brighter center against an unlit cardinal forces full height even without an edge check",
                $"expected {ChunkMath.CHUNK_HEIGHT}, got {brightCenter}");

            // Unlit center vs lit east neighbor: center-ward writes only exist on the edge-check path.
            int dimCenterNoEdge = LightingBandDecision.DeriveBandHeight(in unlit,
                in unlit, in lit, in unlit, in unlit, in unlit, in unlit, in unlit, in unlit,
                maxQueuedNodeY: -1, hasColumnRecalcs: false, performEdgeCheck: false);
            ok &= LightingAssert.IsTrue(dimCenterNoEdge == expectBanded,
                "B73: a brighter neighbor without an edge check still bands (no center-ward write path)",
                $"expected {expectBanded}, got {dimCenterNoEdge}");

            int dimCenterEdge = LightingBandDecision.DeriveBandHeight(in unlit,
                in unlit, in lit, in unlit, in unlit, in unlit, in unlit, in unlit, in unlit,
                maxQueuedNodeY: -1, hasColumnRecalcs: false, performEdgeCheck: true);
            ok &= LightingAssert.IsTrue(dimCenterEdge == ChunkMath.CHUNK_HEIGHT,
                "B73: a brighter neighbor WITH an edge check forces full height (center-ward corrections)",
                $"expected {ChunkMath.CHUNK_HEIGHT}, got {dimCenterEdge}");

            return ok;
        }

        /// <summary>
        /// B74: the derivation over REAL converged chunk metadata — the two new APIs composed the way
        /// the schedulers will use them. A lit superflat 3×3 bands tightly; adding one high lamp on the
        /// center chunk dissolves its uniform region and drives the whole neighborhood to full height.
        /// </summary>
        /// <returns>True when both derivations match.</returns>
        private static bool Baseline_BandDerivationOnRealChunks()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(10, TestBlockPalette.Stone);
            world.RecalculateHeightmaps();
            world.RunInitialLighting();

            int banded = DeriveFromRealChunks(world);
            const int expectBanded = ChunkMath.SECTION_SIZE + LightingBandDecision.BandHeadroomVoxels;
            bool ok = LightingAssert.IsTrue(banded == expectBanded,
                "B74: lit superflat neighborhood derives the tight band from real metadata",
                $"expected {expectBanded}, got {banded}");

            world.PlaceBlock(new Vector3Int(24, 100, 24), TestBlockPalette.LampWhite);
            world.RunToConvergence();

            int full = DeriveFromRealChunks(world);
            ok &= LightingAssert.IsTrue(full == ChunkMath.CHUNK_HEIGHT,
                "B74: a high lamp on the center chunk drives the real-metadata derivation to full height",
                $"expected {ChunkMath.CHUNK_HEIGHT}, got {full}");

            return ok;
        }

        /// <summary>Runs the derivation for the 3×3 world's center chunk (1,1) from live chunk metadata,
        /// with no queued work (the quiescent post-convergence state).</summary>
        /// <param name="world">The 3×3 test world.</param>
        /// <returns>The derived band height.</returns>
        private static int DeriveFromRealChunks(LightingTestWorld world)
        {
            LightingBandChunkTop Top(int cx, int cz) =>
                world.GetChunkData(new Vector2Int(cx, cz)).GetLightingBandTop();

            LightingBandChunkTop center = Top(1, 1);
            LightingBandChunkTop w = Top(0, 1), e = Top(2, 1), s = Top(1, 0), n = Top(1, 2);
            LightingBandChunkTop sw = Top(0, 0), nw = Top(0, 2), se = Top(2, 0), ne = Top(2, 2);

            return LightingBandDecision.DeriveBandHeight(in center,
                in w, in e, in s, in n, in sw, in nw, in se, in ne,
                maxQueuedNodeY: -1, hasColumnRecalcs: false, performEdgeCheck: false);
        }
    }
}
