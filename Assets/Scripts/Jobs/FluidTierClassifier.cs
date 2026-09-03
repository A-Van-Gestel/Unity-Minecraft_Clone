using Unity.Mathematics;

namespace Jobs
{
    /// <summary>
    /// TG-4 — fluid-flow spatial geometry shared by the managed (<c>BlockBehavior.Fluids</c>) and Burst
    /// (<see cref="FluidTickJob"/>) fluid paths: the flow reach (<see cref="MaxFlowSearchDepth"/>, which also sizes
    /// the per-tick neighbor-halo width) and the horizontal neighbor order (<see cref="HorizontalNeighborOffset"/>).
    /// Owning them here — consumed by both paths — keeps the spread/BFS geometry from drifting apart across the two
    /// implementations (the load-bearing parity invariant).
    /// </summary>
    public static class FluidTierClassifier
    {
        /// <summary>
        /// The drop-search BFS (<c>CalculateFlowCost</c>) explores up to this many cells away from a fluid voxel
        /// (and reads the cell below each) — the <b>maximum horizontal read reach</b> of the fluid behavior. Both
        /// the managed and Burst BFS gate their enqueue/no-drop checks on this value, and it also sizes the per-tick
        /// neighbor-halo width (<c>ChunkMath.FLUID_HALO</c>), so the halo can never be narrower than the read footprint.
        /// </summary>
        public const int MaxFlowSearchDepth = 4;

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
