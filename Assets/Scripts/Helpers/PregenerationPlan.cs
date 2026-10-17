using System;
using System.Collections.Generic;
using Data;

namespace Helpers
{
    /// <summary>
    /// Pure planning arithmetic for the CMD-6 pre-generator's station walk: the rectangle, the cover radius, the
    /// serpentine station grid and its filter, the per-target readiness decision and the start validation. The
    /// driver gathers the world facts and performs every side effect; this class only maps facts to decisions, so
    /// each rule is truth-table-testable in edit mode.
    /// See Documentation/Design/CHUNK_PREGENERATION.md §3.2 (readiness tiers), §4.4 (station geometry) and §4.5.
    /// </summary>
    public static class PregenerationPlan
    {
        /// <summary>A run over more chunks than this asks for confirmation first.</summary>
        public const long ConfirmChunkCount = 10_000;

        /// <summary>A run over more chunks than this is refused.</summary>
        public const long MaxChunkCount = 250_000;

        // A target at Chebyshev distance LoadDistance − 2 has neighbors at LoadDistance − 1, whose own neighbors
        // are all resident — the outermost ring that can still light fully and pass AreNeighborsReadyAndLit.
        private const int COVER_MARGIN = 2;

        /// <summary>How far a target chunk has progressed toward being ready.</summary>
        public enum Readiness : byte
        {
            /// <summary>Not resident, not populated, or its generation job is still tracked.</summary>
            Pending,

            /// <summary>Populated with no tracked generation job, but lighting work or a lighting job remains.</summary>
            Populated,

            /// <summary>Populated and quiet: no lighting work and no lighting job (or lighting is disabled).</summary>
            Settled,
        }

        /// <summary>Why a run may not start; <see cref="None"/> when it may.</summary>
        public enum StartRefusal : byte
        {
            /// <summary>The run may start.</summary>
            None,

            /// <summary>The rectangle holds more than <see cref="MaxChunkCount"/> chunks.</summary>
            TooManyChunks,

            /// <summary>The world border is set and a rectangle corner lies outside it.</summary>
            OutsideBorder,

            /// <summary>No world is loaded.</summary>
            NoWorld,

            /// <summary>Persistence is disabled: nothing would be stored, and nothing would ever unload.</summary>
            PersistenceDisabled,

            /// <summary>A pre-generation run is already active.</summary>
            AlreadyRunning,

            /// <summary>A teleport arrival hold is active.</summary>
            TeleportHoldActive,
        }

        /// <summary>Per-target facts the driver reads on the main thread each frame.</summary>
        public readonly struct ReadinessFacts
        {
            /// <summary>The chunk has a <c>ChunkData</c> in <c>WorldData</c>.</summary>
            public readonly bool Resident;

            /// <summary><c>ChunkData.IsPopulated</c>.</summary>
            public readonly bool IsPopulated;

            /// <summary>A generation job is tracked for the chunk — structure mods can still be pending.</summary>
            public readonly bool GenerationJobTracked;

            /// <summary><c>ChunkData.HasAnyLightingWork</c>.</summary>
            public readonly bool HasLightingWork;

            /// <summary>A lighting job is tracked for the chunk.</summary>
            public readonly bool LightingJobTracked;

            /// <summary>The lighting engine is enabled.</summary>
            public readonly bool LightingEnabled;

            /// <summary>Creates the fact set for one target.</summary>
            /// <param name="resident">The chunk has a <c>ChunkData</c>.</param>
            /// <param name="isPopulated">The chunk is populated.</param>
            /// <param name="generationJobTracked">A generation job is tracked for it.</param>
            /// <param name="hasLightingWork">It has lighting work.</param>
            /// <param name="lightingJobTracked">A lighting job is tracked for it.</param>
            /// <param name="lightingEnabled">The lighting engine is enabled.</param>
            public ReadinessFacts(bool resident, bool isPopulated, bool generationJobTracked, bool hasLightingWork,
                bool lightingJobTracked, bool lightingEnabled)
            {
                Resident = resident;
                IsPopulated = isPopulated;
                GenerationJobTracked = generationJobTracked;
                HasLightingWork = hasLightingWork;
                LightingJobTracked = lightingJobTracked;
                LightingEnabled = lightingEnabled;
            }
        }

        /// <summary>World state gathered when a run is asked to start.</summary>
        public readonly struct StartFacts
        {
            /// <summary>Chunks in the requested rectangle.</summary>
            public readonly long ChunkCount;

            /// <summary>Every rectangle corner lies inside the world border (true when no border is set).</summary>
            public readonly bool CornersInsideBorder;

            /// <summary>A world is loaded.</summary>
            public readonly bool WorldLoaded;

            /// <summary>Persistence is enabled and a storage manager exists.</summary>
            public readonly bool PersistenceEnabled;

            /// <summary>A pre-generation run is active.</summary>
            public readonly bool RunActive;

            /// <summary>A teleport arrival hold is active.</summary>
            public readonly bool TeleportHoldActive;

            /// <summary>Creates the start facts.</summary>
            /// <param name="chunkCount">Chunks in the requested rectangle.</param>
            /// <param name="cornersInsideBorder">Every corner lies inside the world border.</param>
            /// <param name="worldLoaded">A world is loaded.</param>
            /// <param name="persistenceEnabled">Persistence is enabled with a storage manager.</param>
            /// <param name="runActive">A run is already active.</param>
            /// <param name="teleportHoldActive">A teleport arrival hold is active.</param>
            public StartFacts(long chunkCount, bool cornersInsideBorder, bool worldLoaded, bool persistenceEnabled,
                bool runActive, bool teleportHoldActive)
            {
                ChunkCount = chunkCount;
                CornersInsideBorder = cornersInsideBorder;
                WorldLoaded = worldLoaded;
                PersistenceEnabled = persistenceEnabled;
                RunActive = runActive;
                TeleportHoldActive = teleportHoldActive;
            }
        }

        /// <summary>The chunk rectangle covering two voxel-space corners, given in any order.</summary>
        /// <param name="voxelX1">First corner, voxel X.</param>
        /// <param name="voxelZ1">First corner, voxel Z.</param>
        /// <param name="voxelX2">Opposite corner, voxel X.</param>
        /// <param name="voxelZ2">Opposite corner, voxel Z.</param>
        /// <returns>The normalized chunk rectangle.</returns>
        public static ChunkRect FromVoxelCorners(int voxelX1, int voxelZ1, int voxelX2, int voxelZ2) =>
            new ChunkRect(
                new ChunkCoord(ChunkMath.VoxelToChunk(voxelX1), ChunkMath.VoxelToChunk(voxelZ1)),
                new ChunkCoord(ChunkMath.VoxelToChunk(voxelX2), ChunkMath.VoxelToChunk(voxelZ2)));

        /// <summary>The square of chunks within <paramref name="radius"/> (Chebyshev) of a center chunk.</summary>
        /// <param name="center">The center chunk.</param>
        /// <param name="radius">Radius in chunks; 0 is the center alone.</param>
        /// <returns>The square.</returns>
        public static ChunkRect Square(ChunkCoord center, int radius) =>
            new ChunkRect(center.Neighbor(-radius, -radius), center.Neighbor(radius, radius));

        /// <summary>The cover radius for a load distance: <c>LoadDistance − 2</c>, never below 0.</summary>
        /// <param name="loadDistance">Active <c>Settings.LoadDistance</c>.</param>
        /// <returns>The Chebyshev radius around a station within which targets are waited on.</returns>
        public static int CoverRadius(int loadDistance) => Math.Max(0, loadDistance - COVER_MARGIN);

        /// <summary>Stations along one axis: enough cover squares of side <c>2r + 1</c> to reach the far edge.</summary>
        /// <param name="span">Chunks along the axis.</param>
        /// <param name="coverRadius">The cover radius.</param>
        /// <returns>The station count along that axis.</returns>
        private static long StationsAlong(long span, int coverRadius)
        {
            long stride = 2L * coverRadius + 1;
            return (span + stride - 1) / stride;
        }

        /// <summary>Stations in the unfiltered grid — an upper bound on the planned walk (sizing hints).</summary>
        /// <param name="rect">The rectangle.</param>
        /// <param name="coverRadius">The cover radius.</param>
        /// <returns>The grid's station count.</returns>
        public static long StationGridCount(ChunkRect rect, int coverRadius) =>
            StationsAlong(rect.Width, coverRadius) * StationsAlong(rect.Depth, coverRadius);

        /// <summary>
        /// Fills <paramref name="into"/> with the unfiltered serpentine station grid: cover squares of side
        /// <c>2r + 1</c> tile the rectangle from its minimum corner, and rows alternate direction so consecutive
        /// stations are neighbors and each arrival finds the previous station's terrain resident.
        /// </summary>
        /// <param name="rect">The rectangle to cover.</param>
        /// <param name="coverRadius">The cover radius.</param>
        /// <param name="into">Destination; cleared first.</param>
        public static void BuildStationGrid(ChunkRect rect, int coverRadius, List<ChunkCoord> into)
        {
            into.Clear();
            int stride = 2 * coverRadius + 1;
            int columns = (int)StationsAlong(rect.Width, coverRadius);
            int rows = (int)StationsAlong(rect.Depth, coverRadius);

            for (int row = 0; row < rows; row++)
            {
                int stationZ = rect.MinZ + coverRadius + row * stride;
                bool forward = (row & 1) == 0;
                for (int i = 0; i < columns; i++)
                {
                    int column = forward ? i : columns - 1 - i;
                    into.Add(new ChunkCoord(rect.MinX + coverRadius + column * stride, stationZ));
                }
            }
        }

        /// <summary>
        /// Fills <paramref name="into"/> with the missing chunks inside one station's cover square, in row order.
        /// Walks the square rather than the set, so the cost is bounded by the square whatever the set's size.
        /// </summary>
        /// <param name="station">The station chunk.</param>
        /// <param name="coverRadius">The cover radius.</param>
        /// <param name="missing">The chunks still to generate.</param>
        /// <param name="into">Destination; cleared first.</param>
        public static void CollectTargets(ChunkCoord station, int coverRadius, HashSet<ChunkCoord> missing,
            List<ChunkCoord> into)
        {
            into.Clear();
            if (missing.Count == 0) return;

            for (int dz = -coverRadius; dz <= coverRadius; dz++)
            {
                for (int dx = -coverRadius; dx <= coverRadius; dx++)
                {
                    ChunkCoord chunkCoord = station.Neighbor(dx, dz);
                    if (missing.Contains(chunkCoord)) into.Add(chunkCoord);
                }
            }
        }

        /// <summary>
        /// Keeps only the stations whose cover square holds at least one missing chunk, preserving the walk order.
        /// A fully generated rectangle plans zero stations.
        /// </summary>
        /// <param name="grid">The unfiltered grid (<see cref="BuildStationGrid"/>).</param>
        /// <param name="coverRadius">The cover radius.</param>
        /// <param name="missing">The chunks still to generate.</param>
        /// <param name="into">Destination; cleared first.</param>
        /// <param name="scratch">Reusable target list.</param>
        public static void FilterStations(List<ChunkCoord> grid, int coverRadius, HashSet<ChunkCoord> missing,
            List<ChunkCoord> into, List<ChunkCoord> scratch)
        {
            into.Clear();
            foreach (ChunkCoord station in grid)
            {
                CollectTargets(station, coverRadius, missing, scratch);
                if (scratch.Count > 0) into.Add(station);
            }
        }

        /// <summary>
        /// The two-tier readiness decision (§3.2). Populated also requires no tracked generation job, because
        /// <c>Populate</c> runs before the job's structure mods are expanded and the mods quota can defer the rest
        /// of the job to a later frame. With lighting disabled every populated target is settled.
        /// </summary>
        /// <param name="facts">The target's facts.</param>
        /// <returns>The target's readiness.</returns>
        public static Readiness Classify(in ReadinessFacts facts)
        {
            if (!facts.Resident || !facts.IsPopulated || facts.GenerationJobTracked) return Readiness.Pending;
            if (!facts.LightingEnabled) return Readiness.Settled;
            if (facts.HasLightingWork || facts.LightingJobTracked) return Readiness.Populated;
            return Readiness.Settled;
        }

        /// <summary>
        /// Decides whether a run may start. Argument problems are reported before world state, so a refusal names
        /// what the caller can fix first.
        /// </summary>
        /// <param name="facts">The start facts.</param>
        /// <returns>The first refusal that applies, or <see cref="StartRefusal.None"/>.</returns>
        public static StartRefusal ValidateStart(in StartFacts facts)
        {
            if (facts.ChunkCount > MaxChunkCount) return StartRefusal.TooManyChunks;
            if (!facts.CornersInsideBorder) return StartRefusal.OutsideBorder;
            if (!facts.WorldLoaded) return StartRefusal.NoWorld;
            if (!facts.PersistenceEnabled) return StartRefusal.PersistenceDisabled;
            if (facts.RunActive) return StartRefusal.AlreadyRunning;
            if (facts.TeleportHoldActive) return StartRefusal.TeleportHoldActive;
            return StartRefusal.None;
        }
    }
}
