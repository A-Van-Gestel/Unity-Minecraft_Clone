using System;
using System.Collections.Generic;
using Editor.Validation.Framework;
using Helpers;
using UnityEngine;

namespace Editor.Validation
{
    /// <summary>
    /// <see cref="ChunkMathValidationSuite"/> — FLUID #20 liquid-noise far-origin precision baselines.
    /// <para>
    /// The liquid noise field is anchored in voxel space, and the first implementation achieved that by handing the
    /// shader the raw origin. That put the player's distance from the world center inside a noise coordinate, so
    /// float32's coarsening resolution quantized the sample position: from ~3e5 blocks the surface flickered, and
    /// beyond ~1e6 it flattened toward a single color. <see cref="LiquidNoiseOrigin"/> replaced it with an offset
    /// reduced into one tiling period, and these scenarios are what stop the raw coordinate from creeping back.
    /// </para>
    /// </summary>
    /// <remarks>Pure arithmetic against exact integer/double oracles: no scene, no renderer, no shader and no
    /// global state, so these run anywhere in a <c>Validate All</c> order without isolation concerns. What they
    /// cannot cover is the HLSL itself — that the noise actually repeats over the period these scenarios assume is
    /// a property of <c>snoise</c> in <c>LiquidCore.hlsl</c>, measured offline and confirmed in-game.</remarks>
    public static partial class ChunkMathValidationSuite
    {
        /// <summary>
        /// Every distinct effective scale the two liquid shaders sample at, using the shipped material defaults.
        /// Each is a separate tiling constraint: a scale that fails to snap re-opens the seam at exactly that one
        /// call site, which only shows itself a full period away from spawn.
        /// </summary>
        private static readonly float[] s_liquidAuthoredScales =
        {
            5f, // _WaveScale        — water waves
            15f, // _RippleScale      — water ripples
            5f * 2f, // _WaveScale * 2.0  — water stream/shore foam
            2f, // _NoiseScale       — lava base
            2f * 2.5f, // _NoiseScale * _CellDensity — lava cracks
            2f * 2.0f, // _NoiseScale * 2.0 — lava cooling crust
            2f * 2.5f, // _NoiseScale * 2.5 — lava flow highlight
        };

        /// <summary>Frequency multipliers a fbm octave applies; lava runs five octaves, so the last is 16.</summary>
        private static readonly float[] s_liquidOctaveFrequencies = { 1f, 2f, 4f, 8f, 16f };

        /// <summary>Scales a slider can produce that do NOT already satisfy the constraint, so snapping is exercised.</summary>
        private static readonly float[] s_liquidAwkwardScales = { 0.1f, 1.3f, 2.7f, 7.31f, 12.49f, 19.9f };

        /// <summary>
        /// Origins the wrap is exercised at, spanning spawn to the permanent ±2³⁰ voxel edge, both signs. The mid
        /// cases bracket the range the degradation was measured across (3e5 flicker, 1e6 flattening).
        /// </summary>
        private static readonly Vector3Int[] s_liquidOriginCases =
        {
            new Vector3Int(0, 0, 0),
            new Vector3Int(1024, 0, -1024),
            new Vector3Int(300_000, 0, 300_000),
            new Vector3Int(1_000_000, 0, -1_000_000),
            new Vector3Int(10_000_000, 0, 10_000_000),
            new Vector3Int(1 << 30, 0, -(1 << 30)),
        };

        /// <summary>
        /// Resolution the noise sample position must still resolve, in blocks. A screen pixel covers roughly 0.01
        /// blocks of near-field water at 720p; staying an order of magnitude under that is what keeps neighboring
        /// pixels on distinct noise inputs instead of collapsing onto one.
        /// </summary>
        private const float LIQUID_MAX_SAMPLE_ULP_BLOCKS = 1e-3f;

        static partial void AddLiquidNoiseScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario("Liquid Noise Origin Wraps Into One Period At Far Origins", RunLiquidOriginWrap));
            scenarios.Add(new Scenario("Liquid Noise Origin Is Exactly Zero At Spawn", RunLiquidOriginIdentity));
            scenarios.Add(new Scenario("Liquid Noise Tiles At Every Authored Scale And Octave", RunLiquidScaleSnapping));
            scenarios.Add(new Scenario("Liquid Noise Sample Position Stays Resolvable At Far Origins", RunLiquidSampleResolution));
        }

        /// <summary>
        /// The core contract: the wrapped origin must be congruent to the true origin modulo the period (or the
        /// pattern would jump on a re-anchor) AND bounded by half a period (or the precision problem is untouched).
        /// Both halves are asserted, because either alone is satisfiable by a broken implementation — returning the
        /// raw origin passes congruence, and returning zero passes boundedness.
        /// <para>
        /// The bound is the *nearest-zero* representative, not any representative: a <c>[0, period)</c> wrap is
        /// equally valid for tiling but leaves small origins large, which measurably degraded the sample precision
        /// at spawn. Pinning the tighter bound is what stops that from being reintroduced.
        /// </para>
        /// </summary>
        private static bool RunLiquidOriginWrap()
        {
            const string scenario = "Liquid Noise Origin Wraps Into One Period At Far Origins";
            const int period = LiquidNoiseOrigin.PeriodBlocks;
            const int half = period / 2;

            foreach (Vector3Int origin in s_liquidOriginCases)
            {
                Vector3 wrapped = LiquidNoiseOrigin.WrappedOrigin(origin);

                if (wrapped.x < -half || wrapped.x >= half || wrapped.z < -half || wrapped.z >= half)
                    return FailLiquid(scenario,
                        $"origin {origin.x},{origin.z}: wrapped to {wrapped}, outside [{-half}, {half}).");

                if (!ExactValue.IsZero(wrapped.y))
                    return FailLiquid(scenario, $"origin {origin.x},{origin.z}: wrapped Y was {wrapped.y}, must be 0.");

                // Congruence in exact integer math — the wrap must not shift the pattern, only rebase it.
                if (Mod(origin.x, period) != Mod((int)wrapped.x, period)
                    || Mod(origin.z, period) != Mod((int)wrapped.z, period))
                    return FailLiquid(scenario,
                        $"origin {origin.x},{origin.z}: wrapped {wrapped.x},{wrapped.z} is not congruent modulo {period}.");
            }

            Debug.Log($"[PASS] {scenario}");
            return true;
        }

        /// <summary>
        /// Near the world center the wrap must be the *identity*, not merely small — every origin within half a
        /// period has to pass through untouched. This is what makes the wrapped form bit-identical to the pre-fix
        /// raw form at spawn, and it is precisely the property a <c>[0, period)</c> wrap silently broke: it mapped
        /// an origin of -16 to +3056, which tiles correctly but samples at a 200x larger magnitude and visibly
        /// changed the foam. Congruence alone cannot catch that, so identity is pinned separately.
        /// </summary>
        private static bool RunLiquidOriginIdentity()
        {
            const string scenario = "Liquid Noise Origin Is Exactly Zero At Spawn";

            Vector3 wrapped = LiquidNoiseOrigin.WrappedOrigin(Vector3Int.zero);
            if (!ExactValue.IsZero(wrapped))
                return FailLiquid(scenario, $"origin at the identity wrapped to {wrapped}, expected exactly (0, 0, 0).");

            // Every origin an un-shifted world can actually reach must be left alone. The origin only ever takes
            // multiples of the chunk width, so step by that. Both o and -o must be strictly inside the range —
            // +half itself is not a member of [-half, half) and correctly maps to -half.
            const int half = LiquidNoiseOrigin.PeriodBlocks / 2;
            for (int o = -half + ChunkMath.CHUNK_WIDTH; o < half; o += ChunkMath.CHUNK_WIDTH)
            {
                Vector3 w = LiquidNoiseOrigin.WrappedOrigin(new Vector3Int(o, 0, -o));
                if (!ExactValue.Equal(w.x, o) || !ExactValue.Equal(w.z, -o))
                    return FailLiquid(scenario,
                        $"origin {o},{-o} is inside half a period but wrapped to {w.x},{w.z} — the wrap must be the "
                        + "identity there, or near-origin water samples at a needlessly large magnitude.");
            }

            Debug.Log($"[PASS] {scenario}");
            return true;
        }

        /// <summary>
        /// The precondition that makes the origin reduction invisible, at every call site rather than one of them.
        /// The shipped <c>snoise</c> repeats every <see cref="LiquidNoiseOrigin.NoisePeriodUnits"/> units of its
        /// input, so reducing the origin only lands on the *same* field value where that period divides
        /// <c>PeriodBlocks * scale</c>. Miss it at one scale and that call site samples a different value than the
        /// unreduced origin would have, which is a seam at exactly that site. Every fbm octave is checked too,
        /// since each samples at its own multiple of the base frequency.
        /// <para>
        /// Also pins that the shipped defaults need no correction at all, which is what licenses the claim that
        /// snapping changes nothing visually today.
        /// </para>
        /// </summary>
        private static bool RunLiquidScaleSnapping()
        {
            const string scenario = "Liquid Noise Tiles At Every Authored Scale And Octave";

            foreach (float authored in s_liquidAuthoredScales)
            {
                float snapped = LiquidNoiseOrigin.SnapScale(authored);

                // The shipped defaults are all exact already; a snap that moved them would mean the period and the
                // art no longer agree, and the water would visibly change at spawn.
                if (!Mathf.Approximately(snapped, authored))
                    return FailLiquid(scenario,
                        $"authored scale {authored} snapped to {snapped} — shipped defaults must already tile exactly.");

                // An octave samples at frequency x the base rate, so its effective scale is scale x frequency.
                foreach (float frequency in s_liquidOctaveFrequencies)
                    if (!LiquidNoiseOrigin.TilesExactly(snapped * frequency))
                        return FailLiquid(scenario,
                            $"scale {authored} octave x{frequency} (effective {snapped * frequency}): "
                            + $"{LiquidNoiseOrigin.PeriodBlocks} x scale is not a whole multiple of "
                            + $"{LiquidNoiseOrigin.NoisePeriodUnits}, so the reduced origin samples a different value.");
            }

            // Scales a slider can reach that do NOT already satisfy the constraint must be corrected, and only
            // slightly — otherwise snapping would be a silent art change rather than an imperceptible nudge.
            foreach (float awkward in s_liquidAwkwardScales)
            {
                float snapped = LiquidNoiseOrigin.SnapScale(awkward);

                if (!LiquidNoiseOrigin.TilesExactly(snapped))
                    return FailLiquid(scenario, $"awkward scale {awkward} snapped to {snapped}, which still does not tile.");

                foreach (float frequency in s_liquidOctaveFrequencies)
                    if (!LiquidNoiseOrigin.TilesExactly(snapped * frequency))
                        return FailLiquid(scenario,
                            $"awkward scale {awkward} snapped to {snapped}, but octave x{frequency} does not tile.");

                // Below one step there is no smaller tiling scale to round to, so the snap floors instead. That is
                // a real limit on the sliders (the smallest usable scale is one step), pinned here rather than
                // left to be discovered: above the floor, snapping may never move a scale by more than half a step.
                if (awkward < LiquidNoiseOrigin.ScaleStep)
                {
                    if (!Mathf.Approximately(snapped, LiquidNoiseOrigin.ScaleStep))
                        return FailLiquid(scenario,
                            $"scale {awkward} is below one step and snapped to {snapped}, expected the floor "
                            + $"{LiquidNoiseOrigin.ScaleStep}.");
                    continue;
                }

                float drift = Math.Abs(snapped - awkward);
                if (drift > LiquidNoiseOrigin.ScaleStep * 0.5f + 1e-4f)
                    return FailLiquid(scenario,
                        $"snapping moved scale {awkward} by {drift}, beyond the half-step "
                        + $"{LiquidNoiseOrigin.ScaleStep * 0.5f} it may ever move — that is a visible art change.");
            }

            Debug.Log($"[PASS] {scenario}");
            return true;
        }

        /// <summary>
        /// The reported symptom itself, rather than a proxy for it: the float32 sample position the shader builds
        /// must still resolve detail finer than a screen pixel, at every origin. This is the scenario the pre-fix
        /// arithmetic fails — feeding the raw origin makes the representable step grow in proportion to distance
        /// until neighboring pixels land on one value, which is the flicker-then-flatten this pins against.
        /// <para>
        /// Checked at the largest scale as well as the smallest: the sample position is shared by every call site,
        /// but the fine ripple detail is what disappears first, so a threshold met only at the coarse end would
        /// pass while the visible symptom remained.
        /// </para>
        /// </summary>
        private static bool RunLiquidSampleResolution()
        {
            const string scenario = "Liquid Noise Sample Position Stays Resolvable At Far Origins";

            foreach (Vector3Int origin in s_liquidOriginCases)
            {
                Vector3 wrapped = LiquidNoiseOrigin.WrappedOrigin(origin);

                // Render-space offsets a water surface can legitimately sit at once the world has re-anchored.
                for (int d = -NEAR_ORIGIN_REACH; d <= NEAR_ORIGIN_REACH; d += 97)
                {
                    // Exactly what the shader forms: a small render-space position plus the bounded origin.
                    float sampleX = d + wrapped.x;
                    float sampleZ = -d + wrapped.z;

                    float step = Math.Max(UlpOf(sampleX), UlpOf(sampleZ));
                    if (step > LIQUID_MAX_SAMPLE_ULP_BLOCKS)
                        return FailLiquid(scenario,
                            $"origin {origin.x},{origin.z} offset {d}: the noise sample position resolves only to "
                            + $"{step} blocks, coarser than the {LIQUID_MAX_SAMPLE_ULP_BLOCKS} a pixel of water needs "
                            + "— neighboring pixels collapse onto one noise value.");
                }
            }

            Debug.Log($"[PASS] {scenario}");
            return true;
        }

        /// <summary>The gap to the next representable float above <paramref name="value"/> — its resolution there.</summary>
        /// <param name="value">The position to measure resolution at.</param>
        /// <returns>The distance to the adjacent float, in the same units.</returns>
        private static float UlpOf(float value)
        {
            float magnitude = Math.Abs(value);
            int bits = BitConverter.SingleToInt32Bits(magnitude);
            return BitConverter.Int32BitsToSingle(bits + 1) - magnitude;
        }

        /// <summary>Positive modulo, mirroring the production wrap so the oracle cannot inherit a sign bug from it.</summary>
        /// <param name="value">The coordinate to reduce.</param>
        /// <param name="period">The period to reduce by.</param>
        /// <returns><paramref name="value"/> reduced into <c>[0, period)</c>.</returns>
        private static int Mod(int value, int period)
        {
            int wrapped = value % period;
            return wrapped < 0 ? wrapped + period : wrapped;
        }

        /// <summary>Logs a liquid-noise scenario failure and returns false (the suite's failure idiom).</summary>
        /// <param name="scenario">The scenario name to report under.</param>
        /// <param name="detail">What specifically failed.</param>
        /// <returns>Always false.</returns>
        private static bool FailLiquid(string scenario, string detail)
        {
            Debug.LogError($"[FAIL] {scenario} — {detail}");
            return false;
        }
    }
}
