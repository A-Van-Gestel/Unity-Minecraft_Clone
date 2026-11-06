namespace Helpers
{
    /// <summary>
    /// Which neighbors a chunk whose lighting settled asks to re-mesh. Ungated, all four cardinals; gated, only the
    /// neighbors whose mesh inputs changed since their last mesh, optionally including the diagonals. World-free.
    /// </summary>
    public static class MergeRemeshDecision
    {
        /// <summary>The neighbors to request, as <see cref="ChunkBorderMask"/> bits.</summary>
        /// <param name="gateEnabled">Whether the gate is on.</param>
        /// <param name="includeDiagonals">Whether a gated request covers the diagonals.</param>
        /// <param name="staleNeighbors">The neighbors whose mesh inputs are stale.</param>
        /// <returns>The direction bits of the neighbors to request.</returns>
        public static byte NeighborRequests(bool gateEnabled, bool includeDiagonals, byte staleNeighbors)
        {
            if (!gateEnabled) return ChunkBorderMask.Cardinals;

            byte eligible = includeDiagonals ? ChunkBorderMask.All : ChunkBorderMask.Cardinals;
            return (byte)(staleNeighbors & eligible);
        }
    }
}
