using Helpers;
using Unity.Collections;

namespace Data.NativeData
{
    /// <summary>
    /// Holds native arrays containing the fluid vertex templates to be used in jobs.
    /// </summary>
    public class FluidVertexTemplatesNativeData
    {
        // --- Public Readonly Fields ---
        public readonly NativeArray<float> WaterVertexTemplates;
        public readonly NativeArray<float> LavaVertexTemplates;

        // --- Constructor ---
        /// <summary>
        /// Initializes a new instance containing native fluid vertex templates.
        /// </summary>
        /// <param name="fluidTemplates">The managed fluid templates to copy data from.</param>
        public FluidVertexTemplatesNativeData(FluidTemplates fluidTemplates)
        {
            WaterVertexTemplates = new NativeArray<float>(fluidTemplates.WaterVertexTemplates, Allocator.Persistent);
            LavaVertexTemplates = new NativeArray<float>(fluidTemplates.LavaVertexTemplates, Allocator.Persistent);
        }

        // --- Methods ---
        /// <summary>
        /// Whether <see cref="Dispose"/> has run. <b>The only reliable "are these templates still usable"
        /// test this type offers</b> — see the remarks on <see cref="Dispose"/>.
        /// </summary>
        public bool IsDisposed { get; private set; }

        /// <summary>
        /// A helper to dispose of the allocated native arrays. Safe to call more than once.
        /// </summary>
        /// <remarks>
        /// <b><see cref="NativeArray{T}.IsCreated"/> cannot tell whether these templates are alive</b> — the
        /// <c>readonly</c> fields keep their pointers after the hoisted copies below are disposed. Guard on
        /// <see cref="IsDisposed"/> instead; see <c>Data.JobData.JobDataManager.Dispose</c> for the detail.
        /// </remarks>
        public void Dispose()
        {
            if (IsDisposed) return;

            // Hoisted off the readonly fields so Dispose() runs without hidden defensive copies.
            NativeArray<float> water = WaterVertexTemplates;
            NativeArray<float> lava = LavaVertexTemplates;

            if (water.IsCreated) water.Dispose();
            if (lava.IsCreated) lava.Dispose();

            IsDisposed = true;
        }
    }
}
