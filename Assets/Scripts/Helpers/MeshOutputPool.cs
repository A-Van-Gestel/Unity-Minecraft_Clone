using System;
using System.Collections.Generic;
using Data;
using Unity.Collections;

namespace Helpers
{
    /// <summary>
    /// Pools whole <see cref="MeshDataJobOutput"/> instances (the per-chunk meshing job output:
    /// 9 per-vertex / per-triangle <c>NativeList</c>s + the per-section stats array), avoiding the
    /// per-job allocate-and-grow / dispose churn of the runtime meshing path (MR-6).
    /// <para><b>Why it pays off:</b> <c>NativeList</c> retains its allocated capacity across
    /// <c>Clear()</c>, so a returned buffer keeps whatever capacity it grew to. After warm-up the pool
    /// holds buffers already sized for the densest chunks seen, so no meshing job reallocates and no
    /// GC / native free happens per chunk. Pre-sizing (<see cref="MeshDataJobOutput.DefaultVertexCapacity"/>)
    /// covers the very first jobs before the pool warms up.</para>
    /// <para><b>Contract:</b> rented instances carry <see cref="MeshDataJobOutput.FromPool"/> == true and
    /// MUST be handed back via <see cref="Return"/> (which clears them for reuse) instead of disposed.
    /// Non-pooled instances (editor / preview / benchmark) keep <c>FromPool</c> == false and dispose
    /// normally; passing one to <see cref="Return"/> disposes it.</para>
    /// <para><b>Threading:</b> main-thread only. Return an output only after its
    /// <c>JobHandle.Complete()</c>.</para>
    /// </summary>
    public sealed class MeshOutputPool : IDisposable
    {
        /// <summary>
        /// Maximum instances retained; returns beyond this cap dispose instead, so a backlog spike does
        /// not permanently pin its peak native memory. Sized above peak in-flight meshing demand
        /// (≈20 concurrent mesh jobs at max job settings) with headroom. A pre-sized instance is
        /// ≈1.4 MB of native memory, so the steady-state retention is small; the worst case (a fully
        /// warmed pool holding dense-chunk-sized buffers) stays bounded by this cap.
        /// </summary>
        private const int MAX_RETAINED = 64;

        private readonly Stack<MeshDataJobOutput> _pool = new Stack<MeshDataJobOutput>();

        private bool _isDisposed;

        /// <summary>
        /// Rents a pooled <see cref="MeshDataJobOutput"/> — either a previously returned (cleared,
        /// capacity-retained) instance or a freshly pre-sized one. The returned instance has
        /// <see cref="MeshDataJobOutput.FromPool"/> == true; hand it back via <see cref="Return"/>.
        /// </summary>
        /// <returns>A ready-to-fill output whose lists are empty (length 0).</returns>
        public MeshDataJobOutput Rent()
        {
            if (_pool.Count > 0)
                return _pool.Pop();

            // Persistent: pooled instances are long-lived (rented and returned across many frames).
            MeshDataJobOutput output = new MeshDataJobOutput(Allocator.Persistent)
            {
                FromPool = true,
            };
            return output;
        }

        /// <summary>
        /// Returns an output to the pool for reuse, clearing its lists (retaining capacity). Disposes
        /// the instance instead when it is not pooled (<see cref="MeshDataJobOutput.FromPool"/> == false),
        /// the pool has been disposed, or the retention cap is reached. Safe to call with a default /
        /// uncreated output (no-op).
        /// </summary>
        /// <param name="output">The output to return. Must no longer be referenced by any scheduled job.</param>
        public void Return(in MeshDataJobOutput output)
        {
            if (!output.Vertices.IsCreated) return;

            if (!output.FromPool || _isDisposed || _pool.Count >= MAX_RETAINED)
            {
                output.Dispose();
                return;
            }

            // MR-6 / MH-2: clear every list before reuse — the meshing job appends and never clears,
            // so a stale buffer would leak the previous chunk's geometry into the next mesh.
            output.ClearForReuse();
            _pool.Push(output);
        }

        /// <summary>
        /// Disposes all retained instances. Instances still rented out at this point must be disposed by
        /// their owners (subsequent <see cref="Return"/> calls dispose instead of pooling).
        /// </summary>
        public void Dispose()
        {
            _isDisposed = true;

            while (_pool.Count > 0)
                _pool.Pop().Dispose();
        }
    }
}
