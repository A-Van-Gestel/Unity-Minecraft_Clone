using Jobs.Helpers;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

namespace Jobs
{
    /// <summary>
    /// Burst-compiled job that cuts a cloud density field at its coverage percentile into the flat cloud pattern
    /// (<see cref="CloudTileMesher"/>), so a prebuilt layer needs no main-thread pass between noise and meshing.
    /// </summary>
    [BurstCompile]
    public struct CloudThresholdJob : IJob
    {
        /// <summary>The density field from <see cref="CloudPatternJob"/>.</summary>
        [ReadOnly]
        public NativeArray<float> Density;

        /// <summary>Requested fraction of cells above the cut.</summary>
        public float Coverage;

        /// <summary>Receives the cloud flags, index <c>z * width + x</c>.</summary>
        [WriteOnly]
        public NativeArray<bool> Pattern;

        /// <summary>Receives the number of cloud cells at index 0.</summary>
        [WriteOnly]
        public NativeArray<int> SetCount;

        /// <inheritdoc />
        public void Execute()
        {
            float threshold = CloudTileMesher.ComputeCoverageThreshold(Density, Coverage);
            SetCount[0] = CloudTileMesher.FillPattern(Density, threshold, Pattern);
        }
    }
}
