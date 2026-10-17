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
    /// the station grid's exact tiling, the station filter, the serpentine walk order, the two-tier readiness
    /// decision and the start validation. All pure — no world is involved.
    /// <para><b>Prove-red (by temporary mutation):</b> a stride of <c>2r</c> or <c>2r + 2</c> in
    /// <see cref="PregenerationPlan.BuildStationGrid"/> reds B26 (overlap / gap); keeping every grid station in
    /// <see cref="PregenerationPlan.FilterStations"/> reds B27; dropping the row reversal reds B28; dropping the
    /// generation-job clause or the lighting-job clause in <see cref="PregenerationPlan.Classify"/> reds B29;
    /// moving the world-state checks above the argument checks in <see cref="PregenerationPlan.ValidateStart"/>
    /// reds B30.</para>
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
        /// B27. A fully generated rectangle plans zero stations; a single missing chunk plans exactly the one
        /// station whose cover holds it; a fully missing rectangle keeps the whole grid, in its order.
        /// </summary>
        private static bool RunB27PregenStationFilter()
        {
            const int radius = 11;
            ChunkRect rect = new ChunkRect(new ChunkCoord(0, 0), new ChunkCoord(89, 89));
            List<ChunkCoord> grid = new List<ChunkCoord>();
            List<ChunkCoord> kept = new List<ChunkCoord>();
            List<ChunkCoord> scratch = new List<ChunkCoord>();
            HashSet<ChunkCoord> missing = new HashSet<ChunkCoord>();
            PregenerationPlan.BuildStationGrid(rect, radius, grid);

            PregenerationPlan.FilterStations(grid, radius, missing, kept, scratch);
            bool ok = Check($"empty missing set -> zero stations (got {kept.Count})", kept.Count == 0);

            ChunkCoord lone = new ChunkCoord(50, 70);
            missing.Add(lone);
            PregenerationPlan.FilterStations(grid, radius, missing, kept, scratch);
            ok &= Check($"one missing chunk -> one station (got {kept.Count})", kept.Count == 1);
            if (kept.Count == 1)
            {
                PregenerationPlan.CollectTargets(kept[0], radius, missing, scratch);
                ok &= Check("...whose cover holds exactly that chunk", scratch.Count == 1 && scratch[0].Equals(lone));
            }

            for (int z = rect.MinZ; z <= rect.MaxZ; z++)
            for (int x = rect.MinX; x <= rect.MaxX; x++)
                missing.Add(new ChunkCoord(x, z));

            PregenerationPlan.FilterStations(grid, radius, missing, kept, scratch);
            bool sameOrder = kept.Count == grid.Count;
            for (int i = 0; sameOrder && i < kept.Count; i++) sameOrder = kept[i].Equals(grid[i]);
            ok &= Check($"fully missing rectangle keeps all {grid.Count} grid stations in walk order", sameOrder);

            // The doc's two prototype scenarios at vd 10: a fresh 90 x 90 plans 16 stations; the same rectangle
            // shifted +3/+3 over generated terrain leaves a 531-chunk L and plans 7.
            ok &= Check("fresh 90 x 90 at vd 10 plans 16 stations", kept.Count == 16);
            ChunkRect shifted = new ChunkRect(new ChunkCoord(3, 3), new ChunkCoord(92, 92));
            HashSet<ChunkCoord> strip = new HashSet<ChunkCoord>();
            for (int z = shifted.MinZ; z <= shifted.MaxZ; z++)
            for (int x = shifted.MinX; x <= shifted.MaxX; x++)
            {
                ChunkCoord chunkCoord = new ChunkCoord(x, z);
                if (!rect.Contains(chunkCoord)) strip.Add(chunkCoord);
            }

            PregenerationPlan.BuildStationGrid(shifted, radius, grid);
            PregenerationPlan.FilterStations(grid, radius, strip, kept, scratch);
            ok &= Check($"shifted rectangle: 531 missing plan 7 stations (got {strip.Count} / {kept.Count})",
                strip.Count == 531 && kept.Count == 7);
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
        /// B30. Start validation and its precedence: argument problems (size, border) before world state (no
        /// world, persistence, active run, teleport hold), each refusal reachable alone, and a clean start.
        /// </summary>
        private static bool RunB30PregenValidateStart()
        {
            static StartRefusal RefusalOf(long count, bool inside, bool loaded, bool persistence, bool running, bool hold) =>
                PregenerationPlan.ValidateStart(new PregenerationPlan.StartFacts(count, inside, loaded, persistence,
                    running, hold));

            const long allowedCount = 100;
            const long tooMany = PregenerationPlan.MaxChunkCount + 1;

            bool ok = Check("clean facts -> None", RefusalOf(allowedCount, true, true, true, false, false) == StartRefusal.None);
            ok &= Check("exactly MaxChunkCount is allowed",
                RefusalOf(PregenerationPlan.MaxChunkCount, true, true, true, false, false) == StartRefusal.None);
            ok &= Check("too many chunks -> TooManyChunks", RefusalOf(tooMany, true, true, true, false, false) == StartRefusal.TooManyChunks);
            ok &= Check("corner outside border -> OutsideBorder", RefusalOf(allowedCount, false, true, true, false, false) == StartRefusal.OutsideBorder);
            ok &= Check("no world -> NoWorld", RefusalOf(allowedCount, true, false, true, false, false) == StartRefusal.NoWorld);
            ok &= Check("persistence off -> PersistenceDisabled",
                RefusalOf(allowedCount, true, true, false, false, false) == StartRefusal.PersistenceDisabled);
            ok &= Check("run active -> AlreadyRunning", RefusalOf(allowedCount, true, true, true, true, false) == StartRefusal.AlreadyRunning);
            ok &= Check("teleport hold -> TeleportHoldActive",
                RefusalOf(allowedCount, true, true, true, false, true) == StartRefusal.TeleportHoldActive);

            ok &= Check("precedence: size before border before world state",
                RefusalOf(tooMany, false, false, false, true, true) == StartRefusal.TooManyChunks
                && RefusalOf(allowedCount, false, false, false, true, true) == StartRefusal.OutsideBorder);
            ok &= Check("precedence: no world before persistence before run before hold",
                RefusalOf(allowedCount, true, false, false, true, true) == StartRefusal.NoWorld
                && RefusalOf(allowedCount, true, true, false, true, true) == StartRefusal.PersistenceDisabled
                && RefusalOf(allowedCount, true, true, true, true, true) == StartRefusal.AlreadyRunning);
            return ok;
        }
    }
}
