using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Pool;

namespace Helpers
{
    /// <summary>
    /// Rents the pooled column sets of the live skylight queue, pre-sized to one chunk's columns. Each set holds the
    /// columns routed to a single chunk, so 256 is a hard bound: a set grows once in its pooled life, never per chunk.
    /// </summary>
    public static class ChunkColumnSets
    {
        /// <summary>Columns in one chunk: the most a live-queue set can ever hold.</summary>
        public const int ColumnsPerChunk = VoxelData.ChunkWidth * VoxelData.ChunkWidth;

        /// <summary>Rents a pooled set that can hold every column of one chunk without growing.</summary>
        /// <returns>An empty set; release it through <see cref="HashSetPool{T}"/>.</returns>
        public static HashSet<Vector2Int> Rent()
        {
            HashSet<Vector2Int> columns = HashSetPool<Vector2Int>.Get();
            columns.EnsureCapacity(ColumnsPerChunk);
            return columns;
        }
    }
}
