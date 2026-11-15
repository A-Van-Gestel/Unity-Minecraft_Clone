using System.Collections.Generic;
using Jobs.Data;
using Unity.Collections;

namespace Helpers
{
    /// <summary>
    /// The canonical order structure markers expand in: x, then z, then y, then pool entry. The generation job
    /// enqueues markers from parallel workers, so the queue's own order follows worker scheduling, and overlapping
    /// structures would land differently from run to run.
    /// </summary>
    public static class StructureSpawnOrder
    {
        /// <summary>Orders markers by x, z, y, then pool entry.</summary>
        public struct Comparer : IComparer<StructureSpawnMarker>
        {
            /// <inheritdoc />
            public int Compare(StructureSpawnMarker a, StructureSpawnMarker b)
            {
                int c = a.Position.x.CompareTo(b.Position.x);
                if (c == 0) c = a.Position.z.CompareTo(b.Position.z);
                if (c == 0) c = a.Position.y.CompareTo(b.Position.y);
                return c != 0 ? c : a.PoolEntryIndex.CompareTo(b.PoolEntryIndex);
            }
        }

        /// <summary>Reorders the markers left in the queue into the canonical order.</summary>
        /// <param name="markers">The queue; no job may still be writing it.</param>
        public static void SortQueue(NativeQueue<StructureSpawnMarker> markers)
        {
            if (markers.Count < 2) return;

            NativeArray<StructureSpawnMarker> sorted = markers.ToArray(Allocator.Temp);
            sorted.Sort(new Comparer());
            markers.Clear();
            for (int i = 0; i < sorted.Length; i++)
                markers.Enqueue(sorted[i]);
            sorted.Dispose();
        }
    }
}
