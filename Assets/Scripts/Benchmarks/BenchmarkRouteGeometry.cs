using System.Collections.Generic;
using Data;
using Helpers;
using UnityEngine;

namespace Benchmarks
{
    /// <summary>
    /// The benchmark flight route's geometry, as pure arithmetic over the active settings (FP-9b).
    /// <para>
    /// Extracted from <c>BenchmarkController.BuildWaypoints</c>, which mutated instance lists and was
    /// therefore unreachable from edit mode — so <b>no property of the route was guarded by anything</b>
    /// until FP-8 read five reports and noticed the generation sweep had collapsed to four waypoints. The
    /// same extraction pattern as <see cref="PipelineSettingsSnapshot"/> and <c>PoolPruneDecision</c>:
    /// arithmetic that decides what a capture measures belongs somewhere a baseline can pin it.
    /// </para>
    /// <para>
    /// <b>The region is derived, never configured.</b> It follows from the distance the configured speed
    /// phases will actually travel, so adding stress speeds (300, 500 m/s) or lengthening phases grows it
    /// automatically — 123 chunks at the defaults, 366 with 300/500 m/s added. The former
    /// <c>benchmarkRegionSize</c> setting inverted this, making waypoint count a *consequence* of a region
    /// the user guessed at, which is how the sweep silently degraded to 12/8/6/4/4 waypoints across the
    /// FP-8 view distances.
    /// </para>
    /// </summary>
    public readonly struct BenchmarkRouteGeometry
    {
        /// <summary>
        /// Fraction of extra route beyond the distance the timed phases consume. The generation pass is
        /// time-bounded, so it stops mid-route by design; the headroom keeps it from running out of
        /// waypoints on a frame-timing fluctuation near the end of the last phase.
        /// </summary>
        private const float ROUTE_HEADROOM = 1.10f;

        /// <summary>
        /// Loading-tour side length, in chunks. Fixed rather than derived from <c>LoadDistance</c>: the
        /// FP-8 tours shrank 84 → 54 chunks across vd 5 → 20 purely because the margin scaled with view
        /// distance, so the loading pass a capture measured was not the same route at every view distance.
        /// <para>
        /// 64 is the largest square that fits inside the <b>timed</b> generation coverage at every view
        /// distance <i>and</i> under stress speeds and longer phases (which both raise the available room —
        /// to 144 and 112 chunks — so this degrades safely). See <see cref="LargestFittingTourChunks"/>,
        /// which is what actually enforces it at runtime.
        /// </para>
        /// </summary>
        public const int LoadingTourChunks = 64;

        /// <summary>Speed (m/s) of the ensure-generated sweep — slow enough to reliably populate.</summary>
        public const float EnsureGeneratedSpeed = 50f;

        /// <summary>
        /// Smallest loading tour worth flying. Reached only by a degenerate speed configuration — a list of
        /// slow speeds travels too little distance for the timed phases to cover any tour at all (10/20 m/s
        /// over 30 s walks 900 m, less than one sweep row). The floor keeps the route from collapsing to a
        /// point; <see cref="TourWasShrunk"/> is what tells the caller the capture is not usable.
        /// </summary>
        public const int MinimumTourChunks = 8;

        /// <summary>Chunks between adjacent sweep rows — twice <c>LoadDistance</c>, so each row is virgin.</summary>
        public readonly int RowStrideChunks;

        /// <summary>Sweep rows; the generation route emits two waypoints per row.</summary>
        public readonly int Rows;

        /// <summary>Derived region side length in chunks (an output, not a setting).</summary>
        public readonly int RegionChunks;

        /// <summary>Lower sweep edge in voxel coordinates.</summary>
        public readonly float MinEdge;

        /// <summary>Upper sweep edge in voxel coordinates.</summary>
        public readonly float MaxEdge;

        /// <summary>First sweep row's Z, in voxel coordinates.</summary>
        public readonly float MinEdgeZ;

        /// <summary>Last sweep row's Z, in voxel coordinates. The sweep is a rectangle, not a square.</summary>
        public readonly float MaxEdgeZ;

        /// <summary>
        /// Rows the <b>timed</b> generation phases complete. Fewer than <see cref="Rows"/> by design — the
        /// pass is time-bounded and the route carries headroom — which is exactly why the tour must be sized
        /// and placed against this rather than the full sweep.
        /// </summary>
        public readonly int CompletedRows;

        /// <summary>Lower Z the timed phases generate, including their load radius.</summary>
        public readonly float CoveredMinZ;

        /// <summary>Upper Z the timed phases generate, including their load radius.</summary>
        public readonly float CoveredMaxZ;

        /// <summary>Loading tour's lower X, in voxel coordinates.</summary>
        public readonly float TourMinX;

        /// <summary>Loading tour's upper X, in voxel coordinates.</summary>
        public readonly float TourMaxX;

        /// <summary>Loading tour's lower Z, in voxel coordinates.</summary>
        public readonly float TourMinZ;

        /// <summary>Loading tour's upper Z, in voxel coordinates.</summary>
        public readonly float TourMaxZ;

        /// <summary>Total generation route length in metres, if walked end to end.</summary>
        public readonly float RouteLengthMeters;

        /// <summary>Distance the timed generation phases consume — <c>Σ(speed × phaseSeconds)</c>.</summary>
        public readonly float TimedTravelMeters;

        /// <summary>Loading-tour side length actually used, after any shrink to fit the timed coverage.</summary>
        public readonly int TourChunks;

        /// <summary>Whether <see cref="TourChunks"/> had to be reduced below <see cref="LoadingTourChunks"/>.</summary>
        public readonly bool TourWasShrunk;

        /// <summary>
        /// Total length of the loading tour's twelve legs, as a <b>closed</b> circuit — the return leg from
        /// the last waypoint back to the first is included, because that is the circuit the loading pass
        /// flies (it loops its waypoints). Walked from the same point list <see cref="BuildTourPoints"/>
        /// emits, so the length and the route flown cannot disagree.
        /// </summary>
        public readonly float TourLengthMeters;

        /// <summary>
        /// Builds the route geometry for one capture.
        /// </summary>
        /// <param name="loadDistance">Active <c>Settings.LoadDistance</c>, in chunks.</param>
        /// <param name="generationSpeeds">Configured generation speed phases, m/s.</param>
        /// <param name="phaseSeconds">Seconds each speed phase runs.</param>
        /// <param name="requestedWaypoints">Generation waypoints to emit; honored <b>exactly</b> (rounded up to even).</param>
        public BenchmarkRouteGeometry(int loadDistance, float[] generationSpeeds, float phaseSeconds,
            int requestedWaypoints)
        {
            const int chunkWidth = VoxelData.ChunkWidth;
            loadDistance = Mathf.Max(1, loadDistance);

            RowStrideChunks = loadDistance * 2;
            float rowStrideMeters = RowStrideChunks * chunkWidth;

            float timed = 0f;
            if (generationSpeeds != null)
            {
                foreach (float speed in generationSpeeds) timed += Mathf.Max(0f, speed) * Mathf.Max(0f, phaseSeconds);
            }

            TimedTravelMeters = timed;

            // Rows come from the request and are honored EXACTLY; width then absorbs whatever distance the
            // phases need. Rows are never added — adding them would narrow the sweep, since the same
            // required distance is split across more of them.
            int requestedRows = Mathf.Max(2, Mathf.CeilToInt(Mathf.Max(2, requestedWaypoints) / 2f));
            float needed = timed * ROUTE_HEADROOM;

            int rows = requestedRows;

            // Width is whichever is larger: what the timed distance needs across this many rows, or what the
            // loading tour needs to sit inside the sweep with a LoadDistance margin. The tour floor binds at
            // high view distances, where the stride is wide enough that the distance-derived width alone
            // leaves a strip too narrow to hold it (93 chunks against 110 needed at vd 20).
            int minWidth = LoadingTourChunks + 2 * loadDistance;
            int widthChunks = Mathf.Max(WidthForRows(rows, needed, rowStrideMeters, chunkWidth), minWidth);

            Rows = rows;
            RegionChunks = widthChunks + 2 * loadDistance;

            int regionStartChunk = (VoxelData.WorldSizeInChunks - RegionChunks) / 2;
            MinEdge = (regionStartChunk + loadDistance) * chunkWidth;
            MaxEdge = MinEdge + widthChunks * chunkWidth;

            RouteLengthMeters = rows * widthChunks * chunkWidth + (rows - 1) * rowStrideMeters;

            CompletedRows = RowsCompletedWithin(rows, widthChunks, RowStrideChunks, timed, chunkWidth);

            int fitting = LargestFittingTourChunks(CompletedRows, widthChunks, RowStrideChunks, loadDistance);
            TourChunks = Mathf.Clamp(fitting, MinimumTourChunks, LoadingTourChunks);
            TourWasShrunk = fitting < LoadingTourChunks;

            MinEdgeZ = MinEdge;
            MaxEdgeZ = MinEdge + (rows - 1) * rowStrideMeters;

            // Covered band: the rows the TIMED phases actually complete, each reaching LoadDistance either
            // side of its center line. Not the full sweep — the pass is time-bounded and stops partway.
            CoveredMinZ = MinEdgeZ - loadDistance * chunkWidth;
            CoveredMaxZ = MinEdgeZ + Mathf.Max(0, CompletedRows - 1) * rowStrideMeters
                                   + loadDistance * chunkWidth;

            // The tour is centred on the COVERED band, not the full sweep. Centring on the sweep biased it
            // toward the un-walked end by (rows - completedRows) x stride / 2 — invisible at the default 12
            // waypoints, where the margin absorbed it, but at 24 it put the tour wholly outside the generated
            // area while TourWasShrunk still read false.
            float halfTour = TourChunks * chunkWidth * 0.5f;
            float centreX = (MinEdge + MaxEdge) * 0.5f;
            float centreZ = (CoveredMinZ + CoveredMaxZ) * 0.5f;

            TourMinX = centreX - halfTour;
            TourMaxX = centreX + halfTour;
            TourMinZ = centreZ - halfTour;
            TourMaxZ = centreZ + halfTour;

            TourLengthMeters = MeasureTour(TourMinX, TourMaxX, TourMinZ, TourMaxZ);
        }

        /// <summary>
        /// Fills <paramref name="into"/> with the loading tour's twelve waypoints — corners, edges and
        /// midpoints of the tour square.
        /// </summary>
        /// <param name="flightHeight">Y to place every waypoint at.</param>
        /// <param name="into">Destination list; cleared first.</param>
        public void BuildTourPoints(float flightHeight, List<Vector3> into) =>
            FillTourPoints(TourMinX, TourMaxX, TourMinZ, TourMaxZ, flightHeight, into);

        /// <summary>
        /// The tour's twelve waypoints — the <b>single</b> definition of its shape.
        /// </summary>
        /// <param name="minX">Tour lower X.</param>
        /// <param name="maxX">Tour upper X.</param>
        /// <param name="minZ">Tour lower Z.</param>
        /// <param name="maxZ">Tour upper Z.</param>
        /// <param name="y">Flight height.</param>
        /// <param name="into">Destination list; cleared first.</param>
        /// <remarks>
        /// Both the flown route and <see cref="TourLengthMeters"/> come from here. A second copy of the
        /// point order — which the first draft of this file had — is the FP-5 defect: two places that must
        /// agree, and eventually do not.
        /// </remarks>
        private static void FillTourPoints(float minX, float maxX, float minZ, float maxZ, float y,
            List<Vector3> into)
        {
            into.Clear();
            float midX = (minX + maxX) * 0.5f;
            float midZ = (minZ + maxZ) * 0.5f;

            into.Add(new Vector3(minX, y, minZ));
            into.Add(new Vector3(maxX, y, maxZ));
            into.Add(new Vector3(maxX, y, minZ));
            into.Add(new Vector3(minX, y, maxZ));
            into.Add(new Vector3(minX, y, midZ));
            into.Add(new Vector3(maxX, y, midZ));
            into.Add(new Vector3(midX, y, maxZ));
            into.Add(new Vector3(midX, y, minZ));
            into.Add(new Vector3(midX, y, minZ));
            into.Add(new Vector3(maxX, y, midZ));
            into.Add(new Vector3(midX, y, maxZ));
            into.Add(new Vector3(minX, y, midZ));
        }

        /// <summary>Sums the closed tour circuit's leg lengths, over the same points the route is flown from.</summary>
        /// <param name="minX">Tour lower X.</param>
        /// <param name="maxX">Tour upper X.</param>
        /// <param name="minZ">Tour lower Z.</param>
        /// <param name="maxZ">Tour upper Z.</param>
        /// <returns>Total leg length in metres, including the return leg.</returns>
        /// <remarks>
        /// The return leg counts because the loading pass loops its waypoints and therefore flies it. Leaving
        /// it out understated the ensure sweep's duration and — worse — let the sweep stop one leg short of
        /// the circuit it exists to cover (FP-11a).
        /// </remarks>
        private static float MeasureTour(float minX, float maxX, float minZ, float maxZ)
        {
            List<Vector3> points = new List<Vector3>(12);
            FillTourPoints(minX, maxX, minZ, maxZ, 0f, points);

            float total = 0f;
            for (int i = 0; i < points.Count; i++)
                total += Vector3.Distance(points[i], points[(i + 1) % points.Count]);

            return total;
        }

        /// <summary>
        /// Fills <paramref name="into"/> with every chunk the loading pass will make resident — the union of
        /// the <paramref name="loadDistance"/> load square swept along the closed tour circuit. This is the
        /// denominator FP-11a's ensure-pass coverage is measured against.
        /// </summary>
        /// <param name="loadDistance">Active <c>Settings.LoadDistance</c>, in chunks.</param>
        /// <param name="into">Destination set; cleared first.</param>
        /// <remarks>
        /// Derived from <see cref="FillTourPoints"/>, the single definition of the tour's shape, so the
        /// footprint and the flown route cannot disagree. Sampled every half chunk: consecutive samples then
        /// never sit more than one chunk apart on either axis, so no chunk between them can be skipped.
        /// <para>Rasterized into a local grid rather than inserted straight into the set — the swept squares
        /// overlap heavily (millions of redundant inserts at high view distance), and this runs once per
        /// benchmark run.</para>
        /// </remarks>
        public void BuildTourChunkSet(int loadDistance, HashSet<ChunkCoord> into)
        {
            into.Clear();
            loadDistance = Mathf.Max(0, loadDistance);

            List<Vector3> points = new List<Vector3>(12);
            FillTourPoints(TourMinX, TourMaxX, TourMinZ, TourMaxZ, 0f, points);
            if (points.Count < 2) return;

            int minChunkX = ChunkMath.VoxelToChunk(Mathf.FloorToInt(TourMinX)) - loadDistance;
            int maxChunkX = ChunkMath.VoxelToChunk(Mathf.FloorToInt(TourMaxX)) + loadDistance;
            int minChunkZ = ChunkMath.VoxelToChunk(Mathf.FloorToInt(TourMinZ)) - loadDistance;
            int maxChunkZ = ChunkMath.VoxelToChunk(Mathf.FloorToInt(TourMaxZ)) + loadDistance;

            int gridWidth = maxChunkX - minChunkX + 1;
            int gridDepth = maxChunkZ - minChunkZ + 1;
            if (gridWidth <= 0 || gridDepth <= 0) return;

            bool[] visited = new bool[gridWidth * gridDepth];

            const float sampleStep = VoxelData.ChunkWidth * 0.5f;
            int lastChunkX = int.MinValue;
            int lastChunkZ = int.MinValue;

            for (int i = 0; i < points.Count; i++)
            {
                Vector3 from = points[i];
                Vector3 to = points[(i + 1) % points.Count];

                float legLength = Vector3.Distance(from, to);
                int steps = Mathf.Max(1, Mathf.CeilToInt(legLength / sampleStep));

                for (int step = 0; step <= steps; step++)
                {
                    Vector3 at = Vector3.Lerp(from, to, (float)step / steps);
                    int chunkX = ChunkMath.VoxelToChunk(Mathf.FloorToInt(at.x));
                    int chunkZ = ChunkMath.VoxelToChunk(Mathf.FloorToInt(at.z));

                    // The swept square only changes when the sample crosses a chunk boundary.
                    if (chunkX == lastChunkX && chunkZ == lastChunkZ) continue;

                    lastChunkX = chunkX;
                    lastChunkZ = chunkZ;

                    MarkLoadSquare(visited, chunkX, chunkZ, loadDistance,
                        minChunkX, minChunkZ, gridWidth, gridDepth);
                }
            }

            for (int z = 0; z < gridDepth; z++)
            {
                for (int x = 0; x < gridWidth; x++)
                {
                    if (visited[z * gridWidth + x]) into.Add(new ChunkCoord(minChunkX + x, minChunkZ + z));
                }
            }
        }

        /// <summary>Marks one resident load square into the rasterization grid, clipped to its bounds.</summary>
        /// <param name="visited">The grid, row-major over Z.</param>
        /// <param name="centerX">Chunk X the player occupies.</param>
        /// <param name="centerZ">Chunk Z the player occupies.</param>
        /// <param name="loadDistance">Load radius in chunks.</param>
        /// <param name="minChunkX">Grid origin on X.</param>
        /// <param name="minChunkZ">Grid origin on Z.</param>
        /// <param name="gridWidth">Grid width in chunks.</param>
        /// <param name="gridDepth">Grid depth in chunks.</param>
        private static void MarkLoadSquare(bool[] visited, int centerX, int centerZ, int loadDistance,
            int minChunkX, int minChunkZ, int gridWidth, int gridDepth)
        {
            int fromX = Mathf.Max(0, centerX - loadDistance - minChunkX);
            int toX = Mathf.Min(gridWidth - 1, centerX + loadDistance - minChunkX);
            int fromZ = Mathf.Max(0, centerZ - loadDistance - minChunkZ);
            int toZ = Mathf.Min(gridDepth - 1, centerZ + loadDistance - minChunkZ);

            for (int z = fromZ; z <= toZ; z++)
            {
                int rowBase = z * gridWidth;
                for (int x = fromX; x <= toX; x++) visited[rowBase + x] = true;
            }
        }

        /// <summary>Generation waypoints this route emits — two per sweep row.</summary>
        public int GenerationWaypoints => Rows * 2;

        /// <summary>Seconds the ensure-generated sweep takes, at <see cref="EnsureGeneratedSpeed"/>.</summary>
        public float EnsureGeneratedSeconds => TourLengthMeters / EnsureGeneratedSpeed;

        /// <summary>Row width (chunks) needed for a given row count to cover the required distance.</summary>
        /// <param name="rows">Sweep rows.</param>
        /// <param name="neededMeters">Route length the timed phases require, including headroom.</param>
        /// <param name="rowStrideMeters">Metres between rows.</param>
        /// <param name="chunkWidth">Voxels per chunk.</param>
        /// <returns>The width in chunks, at least 1.</returns>
        private static int WidthForRows(int rows, float neededMeters, float rowStrideMeters, int chunkWidth)
        {
            float widthMeters = (neededMeters - (rows - 1) * rowStrideMeters) / rows;
            return Mathf.Max(1, Mathf.CeilToInt(widthMeters / chunkWidth));
        }

        /// <summary>
        /// Rows the timed generation phases finish, walking the zigzag until the budget runs out.
        /// </summary>
        /// <param name="rows">Sweep rows available.</param>
        /// <param name="widthChunks">Row width in chunks.</param>
        /// <param name="rowStrideChunks">Chunks between rows.</param>
        /// <param name="timedMeters">Distance the timed phases consume.</param>
        /// <param name="chunkWidth">Voxels per chunk.</param>
        /// <returns>The number of fully completed rows, possibly 0.</returns>
        /// <remarks>
        /// Counts only <b>fully</b> completed rows. A partly-walked row does generate terrain, but crediting
        /// it would place the tour against coverage that exists on one side of the sweep only.
        /// </remarks>
        public static int RowsCompletedWithin(int rows, int widthChunks, int rowStrideChunks,
            float timedMeters, int chunkWidth)
        {
            float widthMeters = widthChunks * chunkWidth;
            float strideMeters = rowStrideChunks * chunkWidth;

            float walked = 0f;
            int completed = 0;
            for (int row = 0; row < rows; row++)
            {
                if (walked + widthMeters > timedMeters) break;

                walked += widthMeters;
                completed = row + 1;

                if (row >= rows - 1) continue;
                if (walked + strideMeters > timedMeters) break;

                walked += strideMeters;
            }

            return completed;
        }

        /// <summary>
        /// Largest square loading tour that fits inside the area the <b>timed</b> generation phases cover,
        /// with a <c>LoadDistance</c> margin on every side.
        /// </summary>
        /// <param name="completedRows">Rows the timed phases finish (<see cref="RowsCompletedWithin"/>).</param>
        /// <param name="widthChunks">Row width in chunks.</param>
        /// <param name="rowStrideChunks">Chunks between rows.</param>
        /// <param name="loadDistance">Active load distance in chunks.</param>
        /// <returns>The largest tour side length in chunks; 0 or negative when nothing fits.</returns>
        /// <remarks>
        /// Takes the completed-row count rather than recomputing it so the tour's <i>size</i> and its
        /// <i>position</i> are derived from one number. They were computed separately once, and the tour was
        /// sized against the covered band while being centred on the full sweep — a guarantee that read as
        /// satisfied while being false.
        /// </remarks>
        public static int LargestFittingTourChunks(int completedRows, int widthChunks, int rowStrideChunks,
            int loadDistance)
        {
            if (completedRows <= 0) return 0;

            int coveredZ = (completedRows - 1) * rowStrideChunks + 2 * loadDistance;
            return Mathf.Min(widthChunks - 2 * loadDistance, coveredZ - 2 * loadDistance);
        }
    }
}
