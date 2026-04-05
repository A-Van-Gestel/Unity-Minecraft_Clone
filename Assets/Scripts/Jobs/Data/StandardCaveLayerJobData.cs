using Data.WorldTypes;

namespace Jobs.Data
{
    /// <summary>
    /// Blittable representation of a 3D Cave Layer configuration.
    /// Loaded dynamically into a flattened NativeArray for Burst evaluation.
    /// </summary>
    public struct StandardCaveLayerJobData
    {
        /// <summary>Noise evaluation strategy.</summary>
        public readonly CaveMode Mode;

        /// <summary>If the evaluated noise exceeds this threshold, the block is carved into air.</summary>
        public readonly float Threshold;

        /// <summary>Caves will not generate below this Y level.</summary>
        public readonly int MinHeight;

        /// <summary>Caves will not generate above this Y level.</summary>
        public readonly int MaxHeight;

        /// <summary>Number of blocks over which carving fades in/out near depth bounds. 0 = hard cutoff.</summary>
        public readonly int DepthFadeMargin;

        public StandardCaveLayerJobData(StandardCaveLayer layerConfig)
        {
            Mode = layerConfig.mode;
            Threshold = layerConfig.threshold;
            MinHeight = layerConfig.minHeight;
            MaxHeight = layerConfig.maxHeight;
            DepthFadeMargin = layerConfig.depthFadeMargin;
        }
    }
}
