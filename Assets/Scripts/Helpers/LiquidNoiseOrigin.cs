using UnityEngine;

namespace Helpers
{
    /// <summary>
    /// The world origin's contribution to the liquid shader's noise coordinates, reduced into one tiling period.
    /// <para>
    /// Liquid noise is anchored in voxel space so the wave pattern survives a floating-origin re-anchor (WS-3).
    /// Feeding the shader an absolute voxel coordinate to achieve that hands it a value proportional to the
    /// player's distance from the world center, and float32's distance-proportional resolution then quantizes the
    /// sample position until neighboring pixels collapse onto one noise value — the surface flickers, then
    /// flattens toward a single color. Reducing the origin modulo <see cref="PeriodBlocks"/> here — in integer
    /// math, from the integer origin — is exact, so the shader adds a small bounded constant to a small
    /// render-space position and resolves the same detail at any distance.
    /// </para>
    /// <para>
    /// The reduction is only invisible because it lands on a period the noise field already has: <c>snoise</c>
    /// folds its lattice index through <c>mod289</c>, so <c>noise(p) == noise(p + PeriodBlocks)</c> holds without
    /// the noise function being modified at all. Reducing onto any other period would re-randomize the whole
    /// surface in one frame every period traveled.
    /// </para>
    /// </summary>
    /// <remarks>Lives outside the shader plumbing so the Chunk Math validation suite pins the real production
    /// arithmetic rather than a re-derived copy of it.</remarks>
    public static class LiquidNoiseOrigin
    {
        /// <summary>
        /// The liquid noise field's intrinsic period, in noise-space units: 3 (the simplex skew) x 289
        /// (<c>mod289</c>, which folds the lattice index in <c>snoise</c>). Not a tunable — it is a property of the
        /// shipped noise function, measured rather than chosen: shifting a sample by 867 leaves the field unchanged
        /// to float rounding, while 289 and 578 change it by the full output range.
        /// </summary>
        public const int NoisePeriodUnits = 3 * 289;

        /// <summary>
        /// The period the origin is reduced modulo, in blocks.
        /// <para>
        /// This must be chosen so that <see cref="NoisePeriodUnits"/> divides <c>PeriodBlocks * scale</c> at every
        /// call site — only then does the reduced origin sample the *same* field value rather than a similar one.
        /// That is what makes the reduction invisible, and it is why the noise function itself needs no change.
        /// </para>
        /// <para>
        /// Two intrinsic periods rather than one: the factor of two halves the scale-snapping step to 0.5, which
        /// keeps the authored sliders usable, and costs only a slightly larger bound on the reduced origin. Because
        /// any multiple of the intrinsic period is also a period of the field, no repetition is introduced that the
        /// shipped noise does not already have.
        /// </para>
        /// </summary>
        public const int PeriodBlocks = 2 * NoisePeriodUnits;

        /// <summary>
        /// Reduces the origin's contribution to the liquid noise coordinates into a single tiling period.
        /// </summary>
        /// <param name="originVoxel">The voxel-space coordinate of the Unity-space origin (<c>WorldOrigin.OriginVoxel</c>).</param>
        /// <returns>The origin wrapped into <c>[-PeriodBlocks/2, PeriodBlocks/2)</c> on X and Z; Y is always zero.</returns>
        public static Vector3 WrappedOrigin(Vector3Int originVoxel) =>
            // Y carries no period: the origin never shifts vertically, so the Y coordinate the shader samples at is
            // already a small block height rather than a distance from the world center.
            new Vector3(Wrap(originVoxel.x), 0f, Wrap(originVoxel.z));

        /// <summary>The scale granularity the snapping quantizes to — 0.5 at the shipped period.</summary>
        public const float ScaleStep = NoisePeriodUnits / (float)PeriodBlocks;

        /// <summary>
        /// Snaps an authored noise scale so that reducing the origin lands on the field's own period.
        /// <para>
        /// The specification of <c>LiquidSnapScale</c> in <c>LiquidCore.hlsl</c>, which must implement the same
        /// formula — the shader reads material sliders per pixel, so the arithmetic cannot simply be shared from
        /// here. This copy exists so the validation suite can pin the constraint the shader has to satisfy; if you
        /// change one, change the other.
        /// </para>
        /// </summary>
        /// <param name="scale">The authored (or derived) noise scale, in noise units per block.</param>
        /// <returns>The nearest scale for which <c>PeriodBlocks * scale</c> is a whole multiple of <see cref="NoisePeriodUnits"/>.</returns>
        public static float SnapScale(float scale) => Mathf.Max(Mathf.Round(scale / ScaleStep), 1f) * ScaleStep;

        /// <summary>
        /// Whether a scale satisfies the period constraint exactly — the property the shader depends on.
        /// </summary>
        /// <param name="scale">The effective scale a call site samples at.</param>
        /// <returns>True when <c>PeriodBlocks * scale</c> is a whole multiple of <see cref="NoisePeriodUnits"/>.</returns>
        public static bool TilesExactly(float scale)
        {
            double units = (double)PeriodBlocks * scale / NoisePeriodUnits;
            return System.Math.Abs(units - System.Math.Round(units)) < 1e-6;
        }

        /// <summary>
        /// Reduces a coordinate to the representative of its period nearest zero.
        /// <para>
        /// Symmetric rather than <c>[0, period)</c> on purpose. Any representative tiles identically, but the one
        /// nearest zero is the smallest, and the sample magnitude is what the shader's precision actually depends
        /// on: <c>snoise</c> recovers the within-cell coordinate as <c>v - i</c>, a subtraction of two large
        /// similar numbers, so its accuracy degrades with magnitude. A <c>[0, period)</c> wrap sent an origin of
        /// -16 to +3056 and cost roughly 3% of a cell at the finest octave — enough to add visible foam speckles
        /// at spawn. Nearest-zero keeps small origins small, so near the world center the result is bit-identical
        /// to passing the origin raw, and the worst case anywhere is halved to half a period.
        /// </para>
        /// </summary>
        /// <param name="value">The coordinate to reduce.</param>
        /// <returns><paramref name="value"/> reduced into <c>[-PeriodBlocks/2, PeriodBlocks/2)</c>.</returns>
        private static int Wrap(int value)
        {
            int wrapped = value % PeriodBlocks;
            if (wrapped < 0) wrapped += PeriodBlocks;
            return wrapped >= PeriodBlocks / 2 ? wrapped - PeriodBlocks : wrapped;
        }
    }
}
