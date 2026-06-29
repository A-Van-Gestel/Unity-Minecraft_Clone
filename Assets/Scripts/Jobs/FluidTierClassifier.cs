using Unity.Mathematics;

namespace Jobs
{
    /// <summary>
    /// TG-4 Phase 3 — fluid-flow spatial geometry shared by the managed (<c>BlockBehavior.Fluids</c>) and Burst
    /// (<see cref="FluidTickJob"/>) fluid paths, plus the <b>Tier-1 (interior)</b> vs <b>Tier-2 (border)</b>
    /// classification for the hybrid tick: interior voxels are ticked by the Burst job (chunk-local reads only),
    /// while border voxels stay on the managed path until Phase 4 provides a cross-chunk neighbor view.
    /// <para>
    /// Owning the flow reach (<see cref="MaxFlowSearchDepth"/>) and the horizontal neighbor order
    /// (<see cref="HorizontalNeighborOffset"/>) here — consumed by both paths — keeps the interior margin and the
    /// spread/BFS geometry from drifting apart across the two implementations (the load-bearing parity invariant).
    /// </para>
    /// </summary>
    public static class FluidTierClassifier
    {
        /// <summary>
        /// The drop-search BFS (<c>CalculateFlowCost</c>) explores up to this many cells away from a fluid voxel
        /// (and reads the cell below each) — the <b>maximum horizontal read reach</b> of the fluid behavior. Both
        /// the managed and Burst BFS gate their enqueue/no-drop checks on this value, and <see cref="InteriorMargin"/>
        /// is derived from it, so the interior classification and the actual read footprint can never disagree:
        /// change the reach here and the safe-interior margin follows automatically.
        /// </summary>
        public const int MaxFlowSearchDepth = 4;

        /// <summary>
        /// Horizontal (X/Z) safety margin, in voxels, a fluid voxel must keep from every chunk border to qualify
        /// as Tier-1 interior. It <b>equals <see cref="MaxFlowSearchDepth"/></b> — the maximum horizontal read
        /// reach of the managed fluid behavior — so a fluid voxel within this margin of an X/Z border (which could
        /// read into a neighbor chunk, something the Burst job cannot do) stays managed. <b>Conservative by
        /// construction:</b> the classifier bounds the <i>maximum possible</i> footprint, not the data-dependent
        /// actual one, so an interior voxel can never produce an out-of-chunk read.
        /// </summary>
        public const int InteriorMargin = MaxFlowSearchDepth;

        /// <summary>
        /// Vertical (Y) safety margin: the fluid behavior reads one voxel up (<c>y+1</c>) and one down
        /// (<c>y-1</c>), so an interior voxel must keep <c>y</c> strictly inside <c>[1, ChunkHeight-2]</c>.
        /// </summary>
        public const int VerticalMargin = 1;

        /// <summary>
        /// Returns true if a fluid voxel at the given <b>local</b> position is Tier-1 interior — its entire read
        /// footprint (±<see cref="InteriorMargin"/> in X/Z, ±<see cref="VerticalMargin"/> in Y) is guaranteed to
        /// stay inside the chunk, so the Burst <see cref="FluidTickJob"/> can tick it with no cross-chunk read.
        /// </summary>
        /// <param name="x">Local X (0..ChunkWidth-1).</param>
        /// <param name="y">Local Y (0..ChunkHeight-1).</param>
        /// <param name="z">Local Z (0..ChunkWidth-1).</param>
        /// <returns>True for a Tier-1 interior voxel; false for a Tier-2 border voxel (managed path).</returns>
        public static bool IsTier1Interior(int x, int y, int z)
        {
            return x >= InteriorMargin && x <= VoxelData.ChunkWidth - 1 - InteriorMargin &&
                   z >= InteriorMargin && z <= VoxelData.ChunkWidth - 1 - InteriorMargin &&
                   y >= VerticalMargin && y <= VoxelData.ChunkHeight - 1 - VerticalMargin;
        }

        /// <summary>
        /// Returns the local-space offset of the <paramref name="i"/>-th horizontal fluid-flow neighbor, in the
        /// fluid behavior's canonical order: <c>0=+Z, 1=-Z, 2=+X, 3=-X</c> (mirrors
        /// <c>VoxelData.FaceChecks[VoxelData.HorizontalFaceChecksIndices[i]]</c>). Defining the order once here —
        /// consumed by both the managed and Burst fluid paths — keeps the spread/BFS direction order (and its
        /// opposite-pairing <c>0/1</c>, <c>2/3</c> used by the BFS backtrack skip) identical across the two
        /// implementations. Burst-safe (pure <see cref="int3"/> arithmetic).
        /// </summary>
        /// <param name="i">The horizontal direction index (0..3).</param>
        /// <returns>The local neighbor offset for direction <paramref name="i"/>.</returns>
        public static int3 HorizontalNeighborOffset(int i)
        {
            switch (i)
            {
                case 0: return new int3(0, 0, 1); // Front (+Z)
                case 1: return new int3(0, 0, -1); // Back (-Z)
                case 2: return new int3(1, 0, 0); // Right (+X)
                default: return new int3(-1, 0, 0); // Left (-X)
            }
        }
    }
}
