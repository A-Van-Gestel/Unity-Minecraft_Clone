using Libraries;
using Unity.Collections;

namespace Jobs.Data
{
    /// <summary>
    /// Groups the per-biome noise and spline arrays needed for Multi-Noise height evaluation.
    /// Passed by ref to avoid copying six NativeArray headers through the blending pipeline.
    /// </summary>
    public struct MultiNoiseData
    {
        /// <summary>Per-biome Continentalness noise instances. Indexed by biome index.</summary>
        [ReadOnly]
        public NativeArray<FastNoiseLite> ContinentalnessNoises;

        /// <summary>Per-biome Erosion noise instances. Indexed by biome index.</summary>
        [ReadOnly]
        public NativeArray<FastNoiseLite> ErosionNoises;

        /// <summary>Per-biome Peaks &amp; Valleys noise instances. Indexed by biome index.</summary>
        [ReadOnly]
        public NativeArray<FastNoiseLite> PeaksValleysNoises;

        /// <summary>Per-biome Continentalness splines baked from AnimationCurves.</summary>
        [ReadOnly]
        public NativeArray<BurstSpline> ContinentalnessSplines;

        /// <summary>Per-biome Erosion splines baked from AnimationCurves.</summary>
        [ReadOnly]
        public NativeArray<BurstSpline> ErosionSplines;

        /// <summary>Per-biome Peaks &amp; Valleys splines baked from AnimationCurves.</summary>
        [ReadOnly]
        public NativeArray<BurstSpline> PeaksValleysSplines;
    }
}
