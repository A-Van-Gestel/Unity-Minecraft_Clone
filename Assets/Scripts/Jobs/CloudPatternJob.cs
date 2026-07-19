using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Jobs
{
    /// <summary>
    /// Burst-compiled job that fills a square density field with seeded FBM value noise that is
    /// **periodic at the pattern width** — every octave's integer lattice wraps modulo its own
    /// period, so the field tiles seamlessly (the cloud pattern repeats every
    /// <see cref="PatternWidth"/> blocks by design). Deliberately stateless: lattice values come
    /// from <see cref="math.hash(uint4)"/> of (cell, octave, seed), never from a stateful RNG, so
    /// the same seed always regenerates the identical field and the world's global
    /// <c>Random.InitState</c> stream is untouched. One cell per <see cref="Execute"/> call —
    /// IJobParallelFor restricts container writes to the current index, so per-row batching would
    /// trip the safety system.
    /// </summary>
    [BurstCompile]
    public struct CloudPatternJob : IJobParallelFor
    {
        /// <summary>Width (and height) of the square pattern, in cells.</summary>
        [ReadOnly] public int PatternWidth;

        /// <summary>Lattice cells across the pattern at the first octave; each octave doubles it.</summary>
        [ReadOnly] public int BasePeriodCells;

        /// <summary>Number of FBM octaves (doubling frequency per octave).</summary>
        [ReadOnly] public int Octaves;

        /// <summary>
        /// Amplitude multiplier per octave (FBM gain). 0.5 is classic smooth FBM; higher values keep
        /// more high-frequency raggedness — the lever that fragments blob edges into MC-style speckle.
        /// </summary>
        [ReadOnly] public float Persistence;

        /// <summary>World seed salting every lattice hash.</summary>
        [ReadOnly] public uint Seed;

        /// <summary>Output density field, row-major, <c>PatternWidth²</c> values in [0, 1].</summary>
        [WriteOnly] public NativeArray<float> Density;

        /// <inheritdoc/>
        public void Execute(int index)
        {
            Density[index] = FbmPeriodic(index % PatternWidth, index / PatternWidth);
        }

        /// <summary>
        /// Normalized FBM sum of periodic value-noise octaves at pattern cell (x, y).
        /// </summary>
        /// <param name="x">Pattern-space X cell.</param>
        /// <param name="y">Pattern-space Y cell.</param>
        /// <returns>Density in [0, 1].</returns>
        private float FbmPeriodic(int x, int y)
        {
            float sum = 0f;
            float amplitude = 1f;
            float totalAmplitude = 0f;
            int period = BasePeriodCells;

            for (int octave = 0; octave < Octaves; octave++)
            {
                sum += amplitude * ValueNoisePeriodic(x, y, period, octave);
                totalAmplitude += amplitude;
                amplitude *= Persistence;
                period *= 2;
            }

            return sum / totalAmplitude;
        }

        /// <summary>
        /// Single octave of value noise whose lattice wraps modulo <paramref name="period"/> —
        /// mapping the pattern onto exactly <c>period</c> lattice cells makes the octave (and thus
        /// the whole FBM) tile seamlessly at the pattern width, for any integer period.
        /// </summary>
        /// <param name="x">Pattern-space X cell.</param>
        /// <param name="y">Pattern-space Y cell.</param>
        /// <param name="period">Lattice cells across the pattern for this octave.</param>
        /// <param name="octave">Octave index, salted into the hash so octaves are independent.</param>
        /// <returns>Noise value in [0, 1].</returns>
        private float ValueNoisePeriodic(int x, int y, int period, int octave)
        {
            // Lattice-space position: `period` cells span the pattern.
            float fx = (float)x * period / PatternWidth;
            float fy = (float)y * period / PatternWidth;

            int x0 = (int)fx;
            int y0 = (int)fy;
            int x1 = (x0 + 1) % period;
            int y1 = (y0 + 1) % period;

            // Quintic fade for C2-continuous interpolation (no lattice-aligned creases).
            float tx = Fade(fx - x0);
            float ty = Fade(fy - y0);

            float v00 = LatticeValue(x0, y0, octave);
            float v10 = LatticeValue(x1, y0, octave);
            float v01 = LatticeValue(x0, y1, octave);
            float v11 = LatticeValue(x1, y1, octave);

            return math.lerp(math.lerp(v00, v10, tx), math.lerp(v01, v11, tx), ty);
        }

        /// <summary>
        /// Deterministic lattice-corner value in [0, 1] from a stateless hash of (cell, octave, seed).
        /// Sequentially avalanche-mixed per component — <c>math.hash(uint4)</c> is a near-linear
        /// lane-multiply/sum whose lattice correlation shows up as visible strands once thresholded.
        /// </summary>
        /// <param name="cx">Lattice X (already wrapped to the octave period).</param>
        /// <param name="cy">Lattice Y (already wrapped to the octave period).</param>
        /// <param name="octave">Octave index.</param>
        /// <returns>Pseudo-random value in [0, 1].</returns>
        private float LatticeValue(int cx, int cy, int octave)
        {
            uint h = Mix(Mix(Mix(Seed ^ (uint)cx) ^ (uint)cy) ^ (uint)octave);
            return h * (1f / uint.MaxValue);
        }

        /// <summary>
        /// 32-bit avalanche mix (lowbias32) — every input bit affects every output bit.
        /// </summary>
        /// <param name="h">Value to mix.</param>
        /// <returns>The mixed value.</returns>
        private static uint Mix(uint h)
        {
            h ^= h >> 16;
            h *= 0x7FEB352Du;
            h ^= h >> 15;
            h *= 0x846CA68Bu;
            h ^= h >> 16;
            return h;
        }

        /// <summary>
        /// Quintic smoothstep 6t⁵ − 15t⁴ + 10t³.
        /// </summary>
        /// <param name="t">Interpolation fraction in [0, 1].</param>
        /// <returns>Faded fraction.</returns>
        private static float Fade(float t)
        {
            return t * t * t * (t * (t * 6f - 15f) + 10f);
        }
    }
}
