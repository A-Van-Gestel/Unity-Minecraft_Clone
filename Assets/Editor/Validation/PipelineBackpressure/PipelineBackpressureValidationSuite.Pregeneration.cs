using System;
using System.Collections.Generic;
using Data;
using Helpers;
using Readiness = Helpers.PregenerationPlan.Readiness;
using StartRefusal = Helpers.PregenerationPlan.StartRefusal;

namespace Editor.Validation.PipelineBackpressure
{
    /// <summary>
    /// CMD-6 pre-generation planning (<see cref="PregenerationPlan"/>, design CHUNK_PREGENERATION.md §3.2/§4.4/§4.5):
    /// the station grid's exact tiling, the indexed walk the streaming driver uses (cover clipping and the
    /// station-index inverse), the serpentine walk order, the two-tier readiness decision and the start validation.
    /// All pure — no world is involved.
    /// <para><b>Prove-red (by temporary mutation):</b> a stride of <c>2r</c> or <c>2r + 2</c> in
    /// <see cref="PregenerationPlan.StationAt"/> reds B26 (overlap / gap); dropping the row reversal in
    /// <see cref="PregenerationPlan.StationIndexOf"/> reds B27, and in <see cref="PregenerationPlan.StationAt"/>
    /// reds B28; dropping the generation-job clause or the lighting-job clause in
    /// <see cref="PregenerationPlan.Classify"/> reds B29; moving the world-state checks above the border check in
    /// <see cref="PregenerationPlan.ValidateStart"/> reds B30.</para>
    /// </summary>
    public static partial class PipelineBackpressureValidationSuite
    {
        private const int MIN_TESTED_COVER_RADIUS = 2;
        private const int MAX_TESTED_COVER_RADIUS = 40;

        /// <summary>The rectangle shapes the tiling property is asserted over.</summary>
        private static readonly (string Label, ChunkRect Rect)[] s_pregenShapes =
        {
            ("1x1", new ChunkRect(new ChunkCoord(5, 5), new ChunkCoord(5, 5))),
            ("1xN", new ChunkRect(new ChunkCoord(0, 0), new ChunkCoord(0, 97))),
            ("Nx1", new ChunkRect(new ChunkCoord(0, 0), new ChunkCoord(97, 0))),
            ("non-multiple 37x53", new ChunkRect(new ChunkCoord(3, 11), new ChunkCoord(39, 63))),
            ("negative", new ChunkRect(new ChunkCoord(-50, -20), new ChunkCoord(-3, 15))),
            ("inverted corners", new ChunkRect(new ChunkCoord(40, 9), new ChunkCoord(-8, -31))),
        };

        /// <summary>
        /// B26. Every cell of the rectangle lies in exactly one station's cover square, for every tested radius and
        /// shape — the property that lets a cell be waited on at exactly one station. Also pins the rectangle
        /// constructors (corner order, voxel → chunk floor division, the radius square) and that the grid size is
        /// what <see cref="PregenerationPlan.StationGridCount"/> reports.
        /// </summary>
        private static bool RunB26PregenStationTiling()
        {
            bool ok = Check("ChunkRect normalizes inverted corners",
                new ChunkRect(new ChunkCoord(9, -2), new ChunkCoord(-4, 7)).Equals(
                    new ChunkRect(new ChunkCoord(-4, -2), new ChunkCoord(9, 7))));
            ok &= Check("voxel corners floor-divide in both signs and any order",
                PregenerationPlan.FromVoxelCorners(15, -17, -1, 16).Equals(
                    new ChunkRect(new ChunkCoord(-1, -2), new ChunkCoord(0, 1))));
            ok &= Check("a radius-0 square is the center alone, radius 3 is 7 x 7",
                PregenerationPlan.Square(new ChunkCoord(-2, 4), 0).Count == 1
                && PregenerationPlan.Square(new ChunkCoord(-2, 4), 3).Equals(
                    new ChunkRect(new ChunkCoord(-5, 1), new ChunkCoord(1, 7))));
            ok &= Check("cover radius is LoadDistance - 2 (vd 10 -> 11), floored at 0",
                PregenerationPlan.CoverRadius(13) == 11 && PregenerationPlan.CoverRadius(1) == 0);

            List<ChunkCoord> grid = new List<ChunkCoord>();
            foreach ((string label, ChunkRect rect) in s_pregenShapes)
            {
                int badRadius = -1;
                string failure = null;
                for (int radius = MIN_TESTED_COVER_RADIUS; radius <= MAX_TESTED_COVER_RADIUS && badRadius < 0; radius++)
                {
                    PregenerationPlan.BuildStationGrid(rect, radius, grid);
                    if (grid.Count != PregenerationPlan.StationGridCount(rect, radius))
                    {
                        badRadius = radius;
                        failure = $"grid holds {grid.Count} stations, StationGridCount says {PregenerationPlan.StationGridCount(rect, radius)}";
                        break;
                    }

                    int[] coverCount = new int[rect.Count];
                    foreach (ChunkCoord station in grid)
                    {
                        for (int z = station.Z - radius; z <= station.Z + radius; z++)
                        for (int x = station.X - radius; x <= station.X + radius; x++)
                        {
                            if (!rect.Contains(new ChunkCoord(x, z))) continue;
                            coverCount[(z - rect.MinZ) * rect.Width + (x - rect.MinX)]++;
                        }
                    }

                    for (int i = 0; i < coverCount.Length; i++)
                    {
                        if (coverCount[i] == 1) continue;
                        badRadius = radius;
                        failure = $"cell #{i} is covered {coverCount[i]} times";
                        break;
                    }
                }

                ok &= Check($"[{label}] every cell lies in exactly one cover square, radii " +
                            $"{MIN_TESTED_COVER_RADIUS}..{MAX_TESTED_COVER_RADIUS}" +
                            (badRadius < 0 ? "" : $" — radius {badRadius}: {failure}"),
                    badRadius < 0);
            }

            return ok;
        }

        /// <summary>
        /// B27. The indexed walk the streaming driver relies on: for every cell of every tested shape and radius,
        /// <see cref="PregenerationPlan.StationIndexOf"/> names a valid index whose station's clipped cover square
        /// holds the cell; the clipped squares never leave the rectangle and their sizes sum to its chunk count; and
        /// <see cref="PregenerationPlan.TryGetNeighborStation"/> (the driver's capture-before-walk step) finds a
        /// neighbor exactly when the stepped center is on the grid, and lands on it. Then
        /// the doc's two vd 10 scenarios as the driver walks them: a fresh 90 x 90 checks 16 stations, and the +3/+3
        /// shift's 531-chunk L leaves exactly 7 stations holding a missing chunk.
        /// </summary>
        private static bool RunB27PregenIndexedWalk()
        {
            bool ok = true;
            foreach ((string label, ChunkRect rect) in s_pregenShapes)
            {
                string failure = null;
                for (int radius = MIN_TESTED_COVER_RADIUS; radius <= MAX_TESTED_COVER_RADIUS && failure == null; radius++)
                {
                    long count = PregenerationPlan.StationGridCount(rect, radius);
                    long clippedTotal = 0;
                    for (long i = 0; i < count && failure == null; i++)
                    {
                        ChunkRect cover = PregenerationPlan.ClipCover(PregenerationPlan.StationAt(rect, radius, i), radius, rect);
                        clippedTotal += cover.Count;
                        if (!rect.Contains(new ChunkCoord(cover.MinX, cover.MinZ)) ||
                            !rect.Contains(new ChunkCoord(cover.MaxX, cover.MaxZ)))
                            failure = $"radius {radius}: station #{i}'s clipped cover {cover} leaves the rectangle";
                    }

                    if (failure == null && clippedTotal != rect.Count)
                        failure = $"radius {radius}: clipped covers sum to {clippedTotal}, the rectangle holds {rect.Count}";

                    // Neighbor capture: a step exists exactly when the stepped center stays on the grid, and lands there.
                    long stride = 2L * radius + 1;
                    long maxCenterX = rect.MinX + radius + ((rect.Width + stride - 1) / stride - 1) * stride;
                    long maxCenterZ = rect.MinZ + radius + ((rect.Depth + stride - 1) / stride - 1) * stride;
                    for (long i = 0; i < count && failure == null; i++)
                    {
                        ChunkCoord center = PregenerationPlan.StationAt(rect, radius, i);
                        for (int dz = -1; dz <= 1 && failure == null; dz++)
                        for (int dx = -1; dx <= 1 && failure == null; dx++)
                        {
                            long cx = center.X + dx * stride, cz = center.Z + dz * stride;
                            bool onGrid = cx >= rect.MinX + radius && cx <= maxCenterX && cz >= rect.MinZ + radius && cz <= maxCenterZ;
                            bool found = PregenerationPlan.TryGetNeighborStation(rect, radius, i, dx, dz, out long n);
                            if (found != onGrid)
                                failure = $"radius {radius}: station #{i} step ({dx}, {dz}) found={found}, on grid={onGrid}";
                            else if (found && !PregenerationPlan.StationAt(rect, radius, n).Equals(new ChunkCoord((int)cx, (int)cz)))
                                failure = $"radius {radius}: station #{i} step ({dx}, {dz}) lands on {PregenerationPlan.StationAt(rect, radius, n)}";
                        }
                    }

                    for (int z = rect.MinZ; z <= rect.MaxZ && failure == null; z++)
                    for (int x = rect.MinX; x <= rect.MaxX && failure == null; x++)
                    {
                        ChunkCoord cell = new ChunkCoord(x, z);
                        long index = PregenerationPlan.StationIndexOf(rect, radius, cell);
                        if (index < 0 || index >= count)
                            failure = $"radius {radius}: cell {cell} maps to index {index} of {count}";
                        else if (!PregenerationPlan.ClipCover(PregenerationPlan.StationAt(rect, radius, index), radius, rect)
                                     .Contains(cell))
                            failure = $"radius {radius}: cell {cell} is not in the cover of station #{index}";
                    }
                }

                ok &= Check($"[{label}] station index round-trips and clipped covers tile the rectangle, radii " +
                            $"{MIN_TESTED_COVER_RADIUS}..{MAX_TESTED_COVER_RADIUS}" + (failure == null ? "" : $" — {failure}"),
                    failure == null);
            }

            // The doc's two prototype scenarios at vd 10 (cover radius 11), as the driver walks them: every grid
            // station is checked, and only those whose cover holds a missing chunk are walked.
            const int vd10Radius = 11;
            ChunkRect fresh = new ChunkRect(new ChunkCoord(0, 0), new ChunkCoord(89, 89));
            ok &= Check("fresh 90 x 90 at vd 10 checks 16 stations",
                PregenerationPlan.StationGridCount(fresh, vd10Radius) == 16);

            ChunkRect shifted = new ChunkRect(new ChunkCoord(3, 3), new ChunkCoord(92, 92));
            HashSet<ChunkCoord> strip = new HashSet<ChunkCoord>();
            for (int z = shifted.MinZ; z <= shifted.MaxZ; z++)
            for (int x = shifted.MinX; x <= shifted.MaxX; x++)
            {
                ChunkCoord chunkCoord = new ChunkCoord(x, z);
                if (!fresh.Contains(chunkCoord)) strip.Add(chunkCoord);
            }

            List<ChunkCoord> targets = new List<ChunkCoord>();
            int walked = 0;
            long shiftedCount = PregenerationPlan.StationGridCount(shifted, vd10Radius);
            for (long i = 0; i < shiftedCount; i++)
            {
                PregenerationPlan.CollectTargets(PregenerationPlan.StationAt(shifted, vd10Radius, i), vd10Radius, strip, targets);
                if (targets.Count > 0) walked++;
            }

            ok &= Check($"shifted rectangle: 531 missing walk 7 stations (got {strip.Count} / {walked})",
                strip.Count == 531 && walked == 7);

            // The retry pass regroups a stalled chunk onto its station: CollectTargets there yields exactly that chunk.
            ChunkCoord lone = new ChunkCoord(50, 70);
            HashSet<ChunkCoord> stalled = new HashSet<ChunkCoord> { lone };
            ChunkCoord loneStation = PregenerationPlan.StationAt(fresh, vd10Radius,
                PregenerationPlan.StationIndexOf(fresh, vd10Radius, lone));
            PregenerationPlan.CollectTargets(loneStation, vd10Radius, stalled, targets);
            ok &= Check("a stalled chunk regroups onto the one station whose cover holds it",
                targets.Count == 1 && targets[0].Equals(lone));
            return ok;
        }

        /// <summary>
        /// B28. Serpentine order: the first row runs toward +X, the next toward −X, and every consecutive pair of
        /// stations is exactly one stride apart along exactly one axis — so each arrival lands beside the terrain
        /// the previous station left resident.
        /// </summary>
        private static bool RunB28PregenSerpentine()
        {
            const int radius = 5;
            const int stride = 2 * radius + 1;
            ChunkRect rect = new ChunkRect(new ChunkCoord(-30, -10), new ChunkCoord(25, 40));
            List<ChunkCoord> grid = new List<ChunkCoord>();
            PregenerationPlan.BuildStationGrid(rect, radius, grid);

            bool ok = Check("grid has at least two rows and two columns",
                grid.Count >= 4 && grid[0].Z == grid[1].Z && grid[^1].Z > grid[0].Z);
            ok &= Check("the walk starts at the rectangle's minimum corner station",
                grid[0].Equals(new ChunkCoord(rect.MinX + radius, rect.MinZ + radius)));
            ok &= Check("the first row runs toward +X", grid[1].X == grid[0].X + stride);

            int firstRowEnd = 0;
            while (firstRowEnd + 1 < grid.Count && grid[firstRowEnd + 1].Z == grid[0].Z) firstRowEnd++;
            ok &= Check("the second row starts above the first row's last station and runs toward -X",
                firstRowEnd + 2 < grid.Count
                && grid[firstRowEnd + 1].X == grid[firstRowEnd].X
                && grid[firstRowEnd + 2].X == grid[firstRowEnd + 1].X - stride);

            bool adjacent = true;
            for (int i = 1; adjacent && i < grid.Count; i++)
            {
                int dx = Math.Abs(grid[i].X - grid[i - 1].X);
                int dz = Math.Abs(grid[i].Z - grid[i - 1].Z);
                adjacent = (dx == stride && dz == 0) || (dx == 0 && dz == stride);
            }

            ok &= Check("every consecutive pair of stations is one stride apart on one axis", adjacent);
            return ok;
        }

        /// <summary>
        /// B29. The two-tier readiness decision, row by row, plus a sweep of all 64 fact combinations against the
        /// two invariants that matter: any missing population fact is <c>Pending</c>, and a tracked generation job
        /// is never anything but <c>Pending</c> (structure mods can defer past <c>IsPopulated</c>).
        /// </summary>
        private static bool RunB29PregenClassify()
        {
            static Readiness ClassifyOf(bool resident, bool populated, bool genJob, bool work, bool lightJob, bool lighting) =>
                PregenerationPlan.Classify(new PregenerationPlan.ReadinessFacts(resident, populated, genJob, work,
                    lightJob, lighting));

            bool ok = Check("absent chunk -> Pending", ClassifyOf(false, false, false, false, false, true) == Readiness.Pending);
            ok &= Check("resident placeholder -> Pending", ClassifyOf(true, false, false, false, false, true) == Readiness.Pending);
            ok &= Check("populated with its generation job still tracked -> Pending",
                ClassifyOf(true, true, true, false, false, true) == Readiness.Pending);
            ok &= Check("populated, lighting work pending -> Populated",
                ClassifyOf(true, true, false, true, false, true) == Readiness.Populated);
            ok &= Check("populated, lighting job in flight -> Populated",
                ClassifyOf(true, true, false, false, true, true) == Readiness.Populated);
            ok &= Check("populated and quiet -> Settled", ClassifyOf(true, true, false, false, false, true) == Readiness.Settled);
            ok &= Check("lighting disabled: populated with stale work bits -> Settled",
                ClassifyOf(true, true, false, true, true, false) == Readiness.Settled);
            ok &= Check("lighting disabled: a tracked generation job still -> Pending",
                ClassifyOf(true, true, true, false, false, false) == Readiness.Pending);

            bool invariantsHold = true;
            for (int bits = 0; bits < 64; bits++)
            {
                bool resident = (bits & 1) != 0, populated = (bits & 2) != 0, genJob = (bits & 4) != 0;
                Readiness r = ClassifyOf(resident, populated, genJob, (bits & 8) != 0, (bits & 16) != 0, (bits & 32) != 0);
                bool populationFactsHold = resident && populated && !genJob;
                if (populationFactsHold != (r != Readiness.Pending)) invariantsHold = false;
            }

            ok &= Check("all 64 combinations: not Pending exactly when resident, populated and no generation job",
                invariantsHold);
            return ok;
        }

        /// <summary>
        /// B30. Start validation and its precedence: the argument problem (border) before world state (no world,
        /// persistence, active run, teleport hold), each refusal reachable alone, and a clean start. A rectangle's
        /// size is not a fact here at all — the streaming walk never refuses one.
        /// </summary>
        private static bool RunB30PregenValidateStart()
        {
            static StartRefusal RefusalOf(bool inside, bool loaded, bool persistence, bool running, bool hold) =>
                PregenerationPlan.ValidateStart(new PregenerationPlan.StartFacts(inside, loaded, persistence, running,
                    hold));

            bool ok = Check("clean facts -> None", RefusalOf(true, true, true, false, false) == StartRefusal.None);
            ok &= Check("corner outside border -> OutsideBorder", RefusalOf(false, true, true, false, false) == StartRefusal.OutsideBorder);
            ok &= Check("no world -> NoWorld", RefusalOf(true, false, true, false, false) == StartRefusal.NoWorld);
            ok &= Check("persistence off -> PersistenceDisabled",
                RefusalOf(true, true, false, false, false) == StartRefusal.PersistenceDisabled);
            ok &= Check("run active -> AlreadyRunning", RefusalOf(true, true, true, true, false) == StartRefusal.AlreadyRunning);
            ok &= Check("teleport hold -> TeleportHoldActive",
                RefusalOf(true, true, true, false, true) == StartRefusal.TeleportHoldActive);

            ok &= Check("precedence: border before world state",
                RefusalOf(false, false, false, true, true) == StartRefusal.OutsideBorder);
            ok &= Check("precedence: no world before persistence before run before hold",
                RefusalOf(true, false, false, true, true) == StartRefusal.NoWorld
                && RefusalOf(true, true, false, true, true) == StartRefusal.PersistenceDisabled
                && RefusalOf(true, true, true, true, true) == StartRefusal.AlreadyRunning);
            return ok;
        }
    }
}
