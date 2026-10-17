using System;

namespace Data
{
    /// <summary>
    /// An axis-aligned rectangle of chunks in chunk-index space, inclusive on both ends and always normalized
    /// (<see cref="MinX"/> ≤ <see cref="MaxX"/>, <see cref="MinZ"/> ≤ <see cref="MaxZ"/>), so it is never empty.
    /// </summary>
    public readonly struct ChunkRect : IEquatable<ChunkRect>
    {
        /// <summary>Lowest chunk X inside the rectangle.</summary>
        public readonly int MinX;

        /// <summary>Lowest chunk Z inside the rectangle.</summary>
        public readonly int MinZ;

        /// <summary>Highest chunk X inside the rectangle.</summary>
        public readonly int MaxX;

        /// <summary>Highest chunk Z inside the rectangle.</summary>
        public readonly int MaxZ;

        /// <summary>Creates the rectangle spanned by two corner chunks, given in any order.</summary>
        /// <param name="a">One corner.</param>
        /// <param name="b">The opposite corner.</param>
        public ChunkRect(ChunkCoord a, ChunkCoord b)
        {
            MinX = Math.Min(a.X, b.X);
            MaxX = Math.Max(a.X, b.X);
            MinZ = Math.Min(a.Z, b.Z);
            MaxZ = Math.Max(a.Z, b.Z);
        }

        /// <summary>Chunks along X.</summary>
        public long Width => (long)MaxX - MinX + 1;

        /// <summary>Chunks along Z.</summary>
        public long Depth => (long)MaxZ - MinZ + 1;

        /// <summary>Chunks in the rectangle. A <see cref="long"/>: corners far apart overflow an <see cref="int"/>.</summary>
        public long Count => Width * Depth;

        /// <summary>Whether the chunk lies inside the rectangle.</summary>
        /// <param name="chunkCoord">The chunk to test.</param>
        /// <returns>True when inside, edges included.</returns>
        public bool Contains(ChunkCoord chunkCoord) =>
            chunkCoord.X >= MinX && chunkCoord.X <= MaxX && chunkCoord.Z >= MinZ && chunkCoord.Z <= MaxZ;

        /// <inheritdoc/>
        public bool Equals(ChunkRect other) =>
            MinX == other.MinX && MinZ == other.MinZ && MaxX == other.MaxX && MaxZ == other.MaxZ;

        /// <inheritdoc/>
        public override bool Equals(object obj) => obj is ChunkRect other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode() => HashCode.Combine(MinX, MinZ, MaxX, MaxZ);

        /// <inheritdoc/>
        public override string ToString() => $"ChunkRect({MinX}, {MinZ} .. {MaxX}, {MaxZ})";
    }
}
