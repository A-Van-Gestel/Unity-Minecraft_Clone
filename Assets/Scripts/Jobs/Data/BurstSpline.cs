using System.Runtime.CompilerServices;
using Unity.Mathematics;
using UnityEngine;

namespace Jobs.Data
{
    /// <summary>
    /// A Burst-compatible piecewise-linear curve baked from an <see cref="AnimationCurve"/>.
    /// Fixed-size (max 16 keyframes) to remain a value type on the stack.
    /// </summary>
    public unsafe struct BurstSpline
    {
        private const int MAX_KEYS = 16;

        /// <summary>Packed keyframe data: [t0, v0, t1, v1, ...]. Interleaved time/value pairs.</summary>
        private fixed float _keys[MAX_KEYS * 2];

        private int _count;

        /// <summary>
        /// Bakes an <see cref="AnimationCurve"/> into this struct by sampling it at evenly-spaced intervals.
        /// Must be called on the main thread during initialization.
        /// </summary>
        /// <param name="curve">The source curve to sample. May be null or empty, in which case a single-key identity spline is created.</param>
        /// <param name="sampleCount">Number of evenly-spaced sample points (clamped to [2, <see cref="MAX_KEYS"/>]).</param>
        /// <returns>A fully populated <see cref="BurstSpline"/> ready for Burst evaluation.</returns>
        public static BurstSpline FromAnimationCurve(AnimationCurve curve, int sampleCount = MAX_KEYS)
        {
            BurstSpline spline = default;

            if (curve == null || curve.length == 0)
            {
                spline._count = 2;
                spline._keys[0] = 0f;
                spline._keys[1] = 0f;
                spline._keys[2] = 1f;
                spline._keys[3] = 0f;
                return spline;
            }

            int count = math.clamp(sampleCount, 2, MAX_KEYS);
            spline._count = count;

            float tMin = curve.keys[0].time;
            float tMax = curve.keys[curve.length - 1].time;
            float range = tMax - tMin;

            if (range <= 0f)
            {
                spline._count = 2;
                float val = curve.Evaluate(tMin);
                spline._keys[0] = tMin;
                spline._keys[1] = val;
                spline._keys[2] = tMin + 1f;
                spline._keys[3] = val;
                return spline;
            }

            for (int i = 0; i < count; i++)
            {
                float t = tMin + (range * i) / (count - 1);
                int idx = i * 2;
                spline._keys[idx] = t;
                spline._keys[idx + 1] = curve.Evaluate(t);
            }

            return spline;
        }

        /// <summary>
        /// Creates a linear ramp spline that maps [-1, 1] to [-scale, scale].
        /// Used for legacy migration: reproduces <c>noise * terrainAmplitude</c> via the spline pipeline.
        /// </summary>
        /// <param name="scale">The output scale (e.g., <c>TerrainAmplitude</c>).</param>
        public static BurstSpline CreateLinearRamp(float scale)
        {
            BurstSpline spline = default;
            spline._count = 2;
            spline._keys[0] = -1f;
            spline._keys[1] = -scale;
            spline._keys[2] = 1f;
            spline._keys[3] = scale;
            return spline;
        }

        /// <summary>
        /// Evaluates the spline at input <paramref name="t"/> using piecewise-linear interpolation.
        /// Burst-safe, zero allocation. Clamps to the first/last keyframe values outside the curve range.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float Evaluate(float t)
        {
            if (_count <= 0) return 0f;

            float firstT = _keys[0];
            float firstV = _keys[1];
            if (t <= firstT) return firstV;

            int lastIdx = (_count - 1) * 2;
            float lastT = _keys[lastIdx];
            float lastV = _keys[lastIdx + 1];
            if (t >= lastT) return lastV;

            for (int i = 0; i < _count - 1; i++)
            {
                int idx = i * 2;
                int nextIdx = idx + 2;
                float t0 = _keys[idx];
                float t1 = _keys[nextIdx];

                if (t >= t0 && t <= t1)
                {
                    float frac = (t - t0) / (t1 - t0);
                    return math.lerp(_keys[idx + 1], _keys[nextIdx + 1], frac);
                }
            }

            return lastV;
        }
    }
}
