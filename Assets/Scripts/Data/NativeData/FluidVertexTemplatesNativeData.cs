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
        /// A helper to dispose of the allocated native arrays.
        /// </summary>
        public void Dispose()
        {
            if (WaterVertexTemplates.IsCreated) WaterVertexTemplates.Dispose(); // TODO: Possibly impure struct method called on readonly variable: struct value always copied before invocation
            if (LavaVertexTemplates.IsCreated) LavaVertexTemplates.Dispose(); // TODO: Possibly impure struct method called on readonly variable: struct value always copied before invocation
        }
    }
}
