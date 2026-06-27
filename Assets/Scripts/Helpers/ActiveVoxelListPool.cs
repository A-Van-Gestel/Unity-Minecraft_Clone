using System;
using System.Collections.Generic;
using Jobs.Generators;
using Unity.Collections;

namespace Helpers
{
    /// <summary>
    /// Pools the per-chunk active-voxel <see cref="NativeList{T}"/> (the flat chunk indices emitted by
    /// <see cref="Jobs.ActiveVoxelScanJob"/> on the generation path), avoiding the per-chunk
    /// allocate-and-free churn of a fresh
    /// <c>new NativeList&lt;int&gt;(StandardChunkGenerator.ActiveVoxelPresizeCapacity, Allocator.Persistent)</c>
    /// during streaming (TG-6, the TG-2 follow-up). Mirrors <see cref="MeshOutputPool"/> (MR-6).
    /// <para><b>Why it pays off:</b> <c>NativeList</c> retains its allocated capacity across <c>Clear()</c>,
    /// so a returned list keeps whatever capacity it grew to. After warm-up the pool holds lists already
    /// sized for the densest (water-heavy) chunks seen, so the active-voxel scan never reallocates and no
    /// GC / native free happens per chunk. The pre-size
    /// (<see cref="StandardChunkGenerator.ActiveVoxelPresizeCapacity"/>) covers the very first jobs before
    /// the pool warms up.</para>
    /// <para><b>Contract:</b> a list rented here is owned by the pool and MUST be handed back via
    /// <see cref="Return"/> (which clears it, retaining capacity) instead of disposed — done at the single
    /// terminal release point <c>WorldJobManager.ReleaseGenerationJobData</c>. The
    /// <see cref="Jobs.Data.GenerationJobData.ActiveVoxelsFromPool"/> flag routes the release: pooled lists
    /// are returned here, non-pooled lists (editor / preview / benchmark, allocated when no pool is
    /// supplied) are freed by <see cref="Jobs.Data.GenerationJobData.Dispose"/>.</para>
    /// <para><b>Threading:</b> main-thread only. Return a list only after its generation
    /// <c>JobHandle.Complete()</c>.</para>
    /// </summary>
    public sealed class ActiveVoxelListPool : IDisposable
    {
        /// <summary>
        /// Maximum lists retained; returns beyond this cap dispose instead, so a streaming spike does not
        /// permanently pin its peak native memory. A pre-sized instance is only ≈8 KB
        /// (<see cref="StandardChunkGenerator.ActiveVoxelPresizeCapacity"/> × 4 bytes), so even a fully
        /// warmed pool is bounded at a few hundred KB. Sized to comfortably cover steady-state in-flight
        /// generation demand with headroom; mirrors <see cref="MeshOutputPool"/>.
        /// </summary>
        private const int MAX_RETAINED = 64;

        private readonly Stack<NativeList<int>> _pool = new Stack<NativeList<int>>();

        private bool _isDisposed;

        /// <summary>
        /// Rents a pooled active-voxel list — either a previously returned (cleared, capacity-retained)
        /// list or a freshly pre-sized one. The returned list is empty (length 0). Hand it back via
        /// <see cref="Return"/>.
        /// </summary>
        /// <returns>A ready-to-fill <see cref="NativeList{T}"/> of length 0.</returns>
        public NativeList<int> Rent()
        {
            if (_pool.Count > 0)
                return _pool.Pop();

            // Persistent: pooled lists are long-lived (rented and returned across many frames).
            return new NativeList<int>(StandardChunkGenerator.ActiveVoxelPresizeCapacity, Allocator.Persistent);
        }

        /// <summary>
        /// Returns a list to the pool for reuse, clearing it (retaining capacity). Disposes the list
        /// instead when the pool has been disposed or the retention cap is reached. Safe to call with a
        /// default / uncreated list (no-op).
        /// </summary>
        /// <param name="list">The list to return. Must no longer be referenced by any scheduled job.</param>
        public void Return(in NativeList<int> list)
        {
            if (!list.IsCreated) return;

            if (_isDisposed || _pool.Count >= MAX_RETAINED)
            {
                list.Dispose();
                return;
            }

            // Clear before reuse: the scan job appends and never clears, so a stale list would leak the
            // previous chunk's active indices into the next chunk's registration.
            NativeList<int> reusable = list;
            reusable.Clear();
            _pool.Push(reusable);
        }

        /// <summary>
        /// Disposes all retained lists. Lists still rented out at this point must be disposed by their
        /// owners (subsequent <see cref="Return"/> calls dispose instead of pooling).
        /// </summary>
        public void Dispose()
        {
            _isDisposed = true;

            while (_pool.Count > 0)
                _pool.Pop().Dispose();
        }
    }
}
