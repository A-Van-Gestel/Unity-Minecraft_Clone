using Data;

namespace Helpers
{
    /// <summary>
    /// One bit per horizontal neighbor (4 cardinals + 4 diagonals) naming the chunks whose mesh reads a given
    /// column of this chunk. The mesher reads at most one voxel across a seam, so a neighbor reads only the border
    /// column facing it, and a diagonal only the shared corner column. World-free and Burst-safe.
    /// </summary>
    public static class ChunkBorderMask
    {
        /// <summary>The neighbor at +Z.</summary>
        public const byte North = 1 << 0;

        /// <summary>The neighbor at -Z.</summary>
        public const byte South = 1 << 1;

        /// <summary>The neighbor at +X.</summary>
        public const byte East = 1 << 2;

        /// <summary>The neighbor at -X.</summary>
        public const byte West = 1 << 3;

        /// <summary>The neighbor at +X, +Z.</summary>
        public const byte NorthEast = 1 << 4;

        /// <summary>The neighbor at -X, +Z.</summary>
        public const byte NorthWest = 1 << 5;

        /// <summary>The neighbor at +X, -Z.</summary>
        public const byte SouthEast = 1 << 6;

        /// <summary>The neighbor at -X, -Z.</summary>
        public const byte SouthWest = 1 << 7;

        /// <summary>The four cardinal neighbors.</summary>
        public const byte Cardinals = North | South | East | West;

        /// <summary>The four diagonal neighbors.</summary>
        public const byte Diagonals = NorthEast | NorthWest | SouthEast | SouthWest;

        /// <summary>Every neighbor.</summary>
        public const byte All = Cardinals | Diagonals;

        /// <summary>Number of direction bits.</summary>
        public const int DirectionCount = 8;

        private const int LAST_COLUMN = VoxelData.ChunkWidth - 1;

        /// <summary>The neighbors whose mesh reads the column at (<paramref name="localX"/>, <paramref name="localZ"/>).</summary>
        /// <param name="localX">Chunk-local X, 0..15.</param>
        /// <param name="localZ">Chunk-local Z, 0..15.</param>
        /// <returns>0 for an interior column; one cardinal bit for an edge; two cardinals plus the diagonal for a corner.</returns>
        public static byte Of(int localX, int localZ)
        {
            int mask = 0;
            bool west = localX == 0;
            bool east = localX == LAST_COLUMN;
            bool south = localZ == 0;
            bool north = localZ == LAST_COLUMN;

            if (west) mask |= West;
            if (east) mask |= East;
            if (south) mask |= South;
            if (north) mask |= North;
            if (north && east) mask |= NorthEast;
            if (north && west) mask |= NorthWest;
            if (south && east) mask |= SouthEast;
            if (south && west) mask |= SouthWest;
            return (byte)mask;
        }

        /// <summary>The chunk-index offset of the neighbor named by one direction bit.</summary>
        /// <param name="bit">Exactly one direction bit.</param>
        /// <param name="dx">Chunk offset along X.</param>
        /// <param name="dz">Chunk offset along Z.</param>
        public static void Offset(byte bit, out int dx, out int dz)
        {
            dx = (bit & (East | NorthEast | SouthEast)) != 0 ? 1
                : (bit & (West | NorthWest | SouthWest)) != 0 ? -1 : 0;
            dz = (bit & (North | NorthEast | NorthWest)) != 0 ? 1
                : (bit & (South | SouthEast | SouthWest)) != 0 ? -1 : 0;
        }

        /// <summary>Takes the lowest direction bit out of <paramref name="mask"/>, so a loop visits each named neighbor once.</summary>
        /// <param name="mask">The remaining direction bits; the taken bit is cleared.</param>
        /// <param name="bit">The taken bit, or 0 when none was left.</param>
        /// <returns>True when a bit was taken.</returns>
        public static bool TryTakeNext(ref byte mask, out byte bit)
        {
            bit = (byte)(mask & -mask);
            mask = (byte)(mask & ~bit);
            return bit != 0;
        }

        /// <summary>The coordinate of the neighbor named by one direction bit.</summary>
        /// <param name="coord">The center chunk.</param>
        /// <param name="bit">Exactly one direction bit.</param>
        /// <returns>The neighbor's chunk coordinate.</returns>
        public static ChunkCoord Neighbor(ChunkCoord coord, byte bit)
        {
            Offset(bit, out int dx, out int dz);
            return coord.Neighbor(dx, dz);
        }

        /// <summary>The direction bit of the opposite neighbor (North ↔ South, NorthEast ↔ SouthWest, …).</summary>
        /// <param name="bit">Exactly one direction bit.</param>
        /// <returns>The bit naming this chunk as seen from that neighbor.</returns>
        public static byte Opposite(byte bit) => bit switch
        {
            North => South,
            South => North,
            East => West,
            West => East,
            NorthEast => SouthWest,
            SouthWest => NorthEast,
            NorthWest => SouthEast,
            SouthEast => NorthWest,
            _ => 0,
        };
    }
}
