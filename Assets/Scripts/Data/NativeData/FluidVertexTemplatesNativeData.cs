using Helpers;
using Unity.Collections;

namespace Data.NativeData
{
    public class FluidVertexTemplatesNativeData
    {
        // --- Public Readonly Fields ---
        public readonly NativeArray<float> WaterVertexTemplates;
        public readonly NativeArray<float> LavaVertexTemplates;
        
        // --- Constructor ---
        public FluidVertexTemplatesNativeData(FluidTemplates fluidTemplates)
        {
            WaterVertexTemplates = new NativeArray<float>(fluidTemplates.WaterVertexTemplates, Allocator.Persistent);
            LavaVertexTemplates = new NativeArray<float>(fluidTemplates.LavaVertexTemplates, Allocator.Persistent);
        }
        
        // --- Methods ---
        /// A helper to dispose all the containers at once
        public void Dispose()
        {
            if (WaterVertexTemplates.IsCreated) WaterVertexTemplates.Dispose();
            if (LavaVertexTemplates.IsCreated) LavaVertexTemplates.Dispose();
        }
    }
}