namespace Jobs
{
    /// <summary>
    /// TG-4 Phase 3 — classifies fluid voxels into <b>Tier-1 (interior)</b> vs <b>Tier-2 (border)</b> for the
    /// hybrid tick: interior voxels are ticked by the Burst <see cref="FluidTickJob"/> (chunk-local reads only),
    /// while border voxels stay on the managed path until Phase 4 provides a cross-chunk neighbor view.
    /// </summary>
    public static class FluidTierClassifier
    {
        /// <summary>
        /// Horizontal (X/Z) safety margin, in voxels, a fluid voxel must keep from every chunk border to qualify
        /// as Tier-1 interior. It equals the <b>maximum horizontal read reach</b> of the managed fluid behavior:
        /// the drop-search BFS in <c>CalculateFlowCost</c> (see <see cref="FluidTickJob"/> /
        /// <c>BlockBehavior.Fluids</c>) explores up to <b>4</b> cells away from the voxel (and reads the cell
        /// below each). A fluid voxel within this margin of an X/Z border can therefore read into a neighbor
        /// chunk, which the Burst job cannot do — so it stays managed. <b>Conservative by construction:</b> the
        /// classifier bounds the <i>maximum possible</i> footprint, not the data-dependent actual one, so an
        /// interior voxel can never produce an out-of-chunk read.
        /// </summary>
        public const int InteriorMargin = 4;

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
    }
}
