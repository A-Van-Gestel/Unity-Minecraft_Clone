using System.Collections.Generic;
using Data;
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
            scenarios.Add(new Scenario(
                "B79: ChunkData.GetLightingBandBottom tracks the inert-dark bottom region through compaction, a buried lamp, and its removal (LI-2 bottom band)",
                Baseline_BandBottomMetadataTracksSectionStates));
            scenarios.Add(new Scenario(
                "B80: Bottom-band coverage rule — inert-dark floors, the lowest queued BFS node, and the center heightmap minimum each bound the band with headroom (LI-2 bottom band)",
                Baseline_BandBottomCoverageRule));
            scenarios.Add(new Scenario(
                "B81: Bottom-band full-bottom fallbacks — any column recalc, a missing center, and a low heightmap column force bandMinY to 0 (LI-2 bottom band)",
                Baseline_BandBottomFallbacks));
            scenarios.Add(new Scenario(
                "B82: Bottom-band derivation over REAL converged chunk metadata — deep superflat floor bands, a buried lamp lowers the floor (LI-2 bottom band)",
                Baseline_BandBottomDerivationOnRealChunks));
            scenarios.Add(new Scenario(
                "B93: Band reconciliation fails CLOSED — a contradictory (bandMinY >= bandHeight) pair resets to full height, a valid band is untouched (LI-2)",
                Baseline_BandReconcileFailsClosed));
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
        /// B93: <see cref="LightingBandDecision.ReconcileBand"/> is the shared safety net that the
        /// production scheduler and this harness both run after deriving the top and bottom bounds
        /// independently. A valid band (bottom below top, including the tight one-row edge) must pass
        /// through untouched; a contradictory pair (a hypothetical derivation defect inverting them,
        /// <c>bandMinY &gt;= bandHeight</c>, including equality) must fail CLOSED to full height rather
        /// than collapse to a mis-serving one-row band. Guards the Finding-2 defensive fix.
        /// </summary>
        /// <returns>True when every reconciliation case matches.</returns>
        private static bool Baseline_BandReconcileFailsClosed()
        {
            // Valid band: bottom well below top — untouched.
            int minY = 40, height = 80;
            LightingBandDecision.ReconcileBand(ref minY, ref height);
            bool ok = LightingAssert.IsTrue(minY == 40 && height == 80,
                "B93: a valid band [40,80) passes through reconciliation untouched",
                $"expected [40,80), got [{minY},{height})");

            // Tight one-row band (bottom == top − 1) is still valid — untouched.
            minY = 79;
            height = 80;
            LightingBandDecision.ReconcileBand(ref minY, ref height);
            ok &= LightingAssert.IsTrue(minY == 79 && height == 80,
                "B93: a valid one-row band [79,80) is not disturbed",
                $"expected [79,80), got [{minY},{height})");

            // Contradiction (bottom above top) — fail closed to full height.
            minY = 90;
            height = 64;
            LightingBandDecision.ReconcileBand(ref minY, ref height);
            ok &= LightingAssert.IsTrue(minY == 0 && height == ChunkMath.CHUNK_HEIGHT,
                "B93: an inverted band [90,64) fails closed to full height",
                $"expected [0,{ChunkMath.CHUNK_HEIGHT}), got [{minY},{height})");

            // Boundary contradiction (bottom == top, an empty band) — fail closed too.
            minY = 50;
            height = 50;
            LightingBandDecision.ReconcileBand(ref minY, ref height);
            ok &= LightingAssert.IsTrue(minY == 0 && height == ChunkMath.CHUNK_HEIGHT,
                "B93: an empty band [50,50) fails closed to full height",
                $"expected [0,{ChunkMath.CHUNK_HEIGHT}), got [{minY},{height})");

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

        /// <summary>A bottom summary with the inert-dark region <c>[0, upTo)</c>.</summary>
        /// <param name="upTo">The exclusive top of the inert-dark region.</param>
        /// <returns>The summary.</returns>
        private static LightingBandChunkBottom BandDark(int upTo) =>
            new LightingBandChunkBottom { InertUpToY = upTo };

        /// <summary>Derives the bottom edge for a neighborhood that is the same summary in all 9 slots.</summary>
        /// <param name="all">The summary used for the center and every neighbor.</param>
        /// <param name="minQueuedNodeY">Lowest queued BFS node Y, or <c>int.MaxValue</c>.</param>
        /// <param name="hasColumnRecalcs">Whether sunlight column recalcs are queued.</param>
        /// <param name="minCenterHeightmapY">The center chunk's lowest heightmap entry.</param>
        /// <returns>The derived band bottom.</returns>
        private static int DeriveUniformBottomNeighborhood(in LightingBandChunkBottom all,
            int minQueuedNodeY = int.MaxValue, bool hasColumnRecalcs = false,
            int minCenterHeightmapY = ChunkMath.CHUNK_HEIGHT)
        {
            return LightingBandDecision.DeriveBandMinY(in all,
                in all, in all, in all, in all, in all, in all, in all, in all,
                minQueuedNodeY, hasColumnRecalcs, minCenterHeightmapY);
        }

        /// <summary>
        /// B79: the inert-dark bottom summary must mirror the section states the banded job would skip.
        /// A deep stone floor is not inert before lighting (sections uncompacted), compacts to two dark
        /// sections after it, loses a section to a buried lamp (first via the SetVoxel promote, then —
        /// the load-bearing case — via the emissive gate alone when the section reads compact-dark), and
        /// recovers once the lamp is broken and the merge recompacts.
        /// </summary>
        /// <returns>True when every state summarizes correctly.</returns>
        private static bool Baseline_BandBottomMetadataTracksSectionStates()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(47, TestBlockPalette.Stone);
            world.RecalculateHeightmaps();

            Vector2Int center = new Vector2Int(1, 1);
            LightingBandChunkBottom bottom = world.GetChunkData(center).GetLightingBandBottom();
            bool ok = LightingAssert.IsTrue(bottom.InertUpToY == 0 && !bottom.IsMissing,
                "B79: an unlit (uncompacted) floor proves nothing inert",
                $"expected InertUpToY=0, got {bottom.InertUpToY}");

            world.RunInitialLighting();
            bottom = world.GetChunkData(center).GetLightingBandBottom();
            ok &= LightingAssert.IsTrue(bottom.InertUpToY == 2 * ChunkMath.SECTION_SIZE,
                "B79: after initial lighting the two fully-buried stone sections compact to inert-dark",
                $"expected InertUpToY={2 * ChunkMath.SECTION_SIZE}, got {bottom.InertUpToY}");

            // Bury a lamp in section 1: the SetVoxel promote decompacts the section, ending the run.
            world.PlaceBlock(new Vector3Int(24, 20, 24), TestBlockPalette.LampWhite);
            ChunkData centerData = world.GetChunkData(center);
            ok &= LightingAssert.IsTrue(centerData.sections[1].emissiveCount == 1,
                "B79: the buried lamp is counted by the section's emissive metadata",
                $"expected emissiveCount=1, got {centerData.sections[1].emissiveCount}");
            ok &= LightingAssert.IsTrue(centerData.GetLightingBandBottom().InertUpToY == ChunkMath.SECTION_SIZE,
                "B79: the promoted (no-longer-compact) lamp section ends the inert run",
                $"expected InertUpToY={ChunkMath.SECTION_SIZE}, got {centerData.GetLightingBandBottom().InertUpToY}");

            world.RunToConvergence();

            // The load-bearing emissive-gate case: force the lamp's section to READ compact-dark (the
            // state a band-skipped, never-rescanned emitter would occupy) — the emissive count alone
            // must still end the run there, or the banded job would never stamp the lamp.
            byte savedSky = centerData.SectionUniformSkyLevel[1];
            centerData.SectionUniformSkyLevel[1] = 0;
            int gated = centerData.GetLightingBandBottom().InertUpToY;
            centerData.SectionUniformSkyLevel[1] = savedSky;
            ok &= LightingAssert.IsTrue(gated == ChunkMath.SECTION_SIZE,
                "B79: the emissive gate ends the run even when the lamp section reads compact-dark",
                $"expected InertUpToY={ChunkMath.SECTION_SIZE}, got {gated}");

            // Breaking the lamp and re-converging recompacts the section to inert-dark.
            world.BreakBlock(new Vector3Int(24, 20, 24));
            world.RunToConvergence();
            bottom = world.GetChunkData(center).GetLightingBandBottom();
            ok &= LightingAssert.IsTrue(bottom.InertUpToY == 2 * ChunkMath.SECTION_SIZE
                                        && world.GetChunkData(center).sections[1].emissiveCount == 0,
                "B79: breaking the lamp restores the two-section inert-dark run",
                $"expected InertUpToY={2 * ChunkMath.SECTION_SIZE} emissiveCount=0, got InertUpToY={bottom.InertUpToY}");

            return ok;
        }

        /// <summary>
        /// B80: the bottom coverage rule floors the band at the shallowest inert-dark ceiling in the
        /// neighborhood, keeps headroom under the lowest queued BFS node and under the center's lowest
        /// heightmap entry, treats a missing neighbor as neutral, and clamps at 0.
        /// </summary>
        /// <returns>True when every coverage case derives the expected bottom.</returns>
        private static bool Baseline_BandBottomCoverageRule()
        {
            LightingBandChunkBottom dark48 = BandDark(48);

            int quiescent = DeriveUniformBottomNeighborhood(in dark48, minCenterHeightmapY: 100);
            bool ok = LightingAssert.IsTrue(quiescent == 48,
                "B80: quiescent dark floors band at the inert-dark ceiling",
                $"expected 48, got {quiescent}");

            int withNode = DeriveUniformBottomNeighborhood(in dark48, minQueuedNodeY: 40, minCenterHeightmapY: 100);
            ok &= LightingAssert.IsTrue(withNode == 40 - LightingBandDecision.BandHeadroomVoxels,
                "B80: a low queued BFS node lowers the band to keep headroom under it",
                $"expected {40 - LightingBandDecision.BandHeadroomVoxels}, got {withNode}");

            int withHeightmap = DeriveUniformBottomNeighborhood(in dark48, minCenterHeightmapY: 40);
            ok &= LightingAssert.IsTrue(withHeightmap == 40 - LightingBandDecision.BandHeadroomVoxels,
                "B80: the center's lowest heightmap entry bounds the band (descending-sunlight rule)",
                $"expected {40 - LightingBandDecision.BandHeadroomVoxels}, got {withHeightmap}");

            // One shallow neighbor (inert only up to y=16) must lower the whole neighborhood's band.
            LightingBandChunkBottom shallowEast = BandDark(ChunkMath.SECTION_SIZE);
            int shallow = LightingBandDecision.DeriveBandMinY(in dark48,
                in dark48, in shallowEast, in dark48, in dark48, in dark48, in dark48, in dark48, in dark48,
                minQueuedNodeY: int.MaxValue, hasColumnRecalcs: false, minCenterHeightmapY: 100);
            ok &= LightingAssert.IsTrue(shallow == ChunkMath.SECTION_SIZE,
                "B80: the shallowest neighbor's inert-dark ceiling governs the band",
                $"expected {ChunkMath.SECTION_SIZE}, got {shallow}");

            // A missing neighbor is band-neutral (its rows read as the sentinel either way).
            LightingBandChunkBottom missing = LightingBandChunkBottom.Missing;
            int withMissing = LightingBandDecision.DeriveBandMinY(in dark48,
                in dark48, in missing, in dark48, in dark48, in dark48, in dark48, in dark48, in dark48,
                minQueuedNodeY: int.MaxValue, hasColumnRecalcs: false, minCenterHeightmapY: 100);
            ok &= LightingAssert.IsTrue(withMissing == 48,
                "B80: a missing neighbor neither lowers nor raises the band",
                $"expected 48, got {withMissing}");

            return ok;
        }

        /// <summary>
        /// B81: every condition that cannot prove the skipped bottom rows inert must fall back to 0 —
        /// any queued column recalc (PASS 2 walks to Y=0 unconditionally), a missing center (defensive),
        /// and a heightmap column low enough that headroom clamps through the floor.
        /// </summary>
        /// <returns>True when each fallback fires.</returns>
        private static bool Baseline_BandBottomFallbacks()
        {
            LightingBandChunkBottom dark48 = BandDark(48);

            int recalc = DeriveUniformBottomNeighborhood(in dark48, hasColumnRecalcs: true, minCenterHeightmapY: 100);
            bool ok = LightingAssert.IsTrue(recalc == 0,
                "B81: any queued column recalc forces the bottom to 0 (no full-sky escape exists downward)",
                $"expected 0, got {recalc}");

            LightingBandChunkBottom missingCenter = LightingBandChunkBottom.Missing;
            int missing = DeriveUniformBottomNeighborhood(in missingCenter, minCenterHeightmapY: 100);
            ok &= LightingAssert.IsTrue(missing == 0,
                "B81: a missing center derives 0 (defensive — never scheduled in practice)",
                $"expected 0, got {missing}");

            int lowColumn = DeriveUniformBottomNeighborhood(in dark48, minCenterHeightmapY: 5);
            ok &= LightingAssert.IsTrue(lowColumn == 0,
                "B81: a sky-open column near bedrock clamps the band bottom to 0",
                $"expected 0, got {lowColumn}");

            return ok;
        }

        /// <summary>
        /// B82: the bottom derivation over REAL converged chunk metadata — the new APIs composed the way
        /// the schedulers will use them. A deep lit superflat floor bands at the heightmap term (one
        /// headroom under the surface, tighter than the two dark sections); burying a lamp lowers the
        /// floor to its section boundary.
        /// </summary>
        /// <returns>True when both derivations match.</returns>
        private static bool Baseline_BandBottomDerivationOnRealChunks()
        {
            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(47, TestBlockPalette.Stone);
            world.RecalculateHeightmaps();
            world.RunInitialLighting();

            int minHeightmap = world.GetChunkData(new Vector2Int(1, 1)).GetHeightmapMinY();
            bool ok = LightingAssert.IsTrue(minHeightmap == 47,
                "B82: the superflat floor's heightmap minimum matches its surface",
                $"expected 47, got {minHeightmap}");

            int banded = DeriveBottomFromRealChunks(world);
            const int expectBanded = 47 - LightingBandDecision.BandHeadroomVoxels; // 31 — under the 32-row dark run
            ok &= LightingAssert.IsTrue(banded == expectBanded,
                "B82: lit deep superflat derives the heightmap-bounded bottom from real metadata",
                $"expected {expectBanded}, got {banded}");

            world.PlaceBlock(new Vector3Int(24, 20, 24), TestBlockPalette.LampWhite);
            world.RunToConvergence();

            int withLamp = DeriveBottomFromRealChunks(world);
            ok &= LightingAssert.IsTrue(withLamp == ChunkMath.SECTION_SIZE,
                "B82: a buried lamp on the center chunk lowers the real-metadata bottom to its section floor",
                $"expected {ChunkMath.SECTION_SIZE}, got {withLamp}");

            return ok;
        }

        /// <summary>Runs the bottom derivation for the 3×3 world's center chunk (1,1) from live chunk
        /// metadata, with no queued work (the quiescent post-convergence state).</summary>
        /// <param name="world">The 3×3 test world.</param>
        /// <returns>The derived band bottom.</returns>
        private static int DeriveBottomFromRealChunks(LightingTestWorld world)
        {
            LightingBandChunkBottom Bottom(int cx, int cz) =>
                world.GetChunkData(new Vector2Int(cx, cz)).GetLightingBandBottom();

            LightingBandChunkBottom center = Bottom(1, 1);
            LightingBandChunkBottom w = Bottom(0, 1), e = Bottom(2, 1), s = Bottom(1, 0), n = Bottom(1, 2);
            LightingBandChunkBottom sw = Bottom(0, 0), nw = Bottom(0, 2), se = Bottom(2, 0), ne = Bottom(2, 2);

            return LightingBandDecision.DeriveBandMinY(in center,
                in w, in e, in s, in n, in sw, in nw, in se, in ne,
                minQueuedNodeY: int.MaxValue, hasColumnRecalcs: false,
                minCenterHeightmapY: world.GetChunkData(new Vector2Int(1, 1)).GetHeightmapMinY());
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
