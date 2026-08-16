using System;
using System.Collections.Generic;
using Editor.Validation.Framework;
using Helpers;
using UnityEngine;

namespace Editor.Validation
{
    /// <summary>
    /// <see cref="ChunkMathValidationSuite"/> — FL-1 foliage wave-phase precision baselines.
    /// <para>
    /// The sway wave is anchored in voxel space, and the first implementation achieved that by reconstructing an
    /// absolute voxel coordinate inside the vertex shader. That put a distance-proportional value inside a sine
    /// argument, so float32's coarsening resolution progressively quantized the phase: the sway stepped, then froze,
    /// the further the player was from the world center. <see cref="FoliagePhase.OriginPhase"/> replaced it, and
    /// these scenarios are what stop the absolute coordinate from creeping back — they drive the phase at origins
    /// out to the permanent world edge, where a large-float round-trip cannot hide.
    /// </para>
    /// </summary>
    /// <remarks>Pure arithmetic against a double-precision oracle: no scene, no renderer, no origin global is
    /// touched, so these run anywhere in a <c>Validate All</c> order without isolation concerns.</remarks>
    public static partial class ChunkMathValidationSuite
    {
        /// <summary>Sway art knobs at their shipped defaults — the configuration the degradation was reported under.</summary>
        private const float FOLIAGE_WAVELENGTH_BLOCKS = 14f;

        private const float FOLIAGE_GUST_SPATIAL_MULTIPLIER = 0.35f;
        private const float FOLIAGE_FREQUENCY = 1.8f;

        /// <summary>Displacement error the eye cannot resolve, as a fraction of the sway amplitude.</summary>
        private const float FOLIAGE_SIN_TOLERANCE = 1e-3f;

        /// <summary>
        /// Per-frame phase advance the primary wave must still resolve at 120 fps. Below roughly this much the
        /// motion holds a pose across consecutive frames, which is the reported "staggered" symptom.
        /// </summary>
        private const float FOLIAGE_FRAME_SECONDS = 1f / 120f;

        /// <summary>
        /// Origins the phase is exercised at, spanning spawn to the permanent ±2³⁰ voxel edge. The mid cases bracket
        /// the range the degradation was reported across.
        /// </summary>
        private static readonly Vector3Int[] s_foliageOriginCases =
        {
            new Vector3Int(0, 0, 0),
            new Vector3Int(1024, 0, -1024),
            new Vector3Int(100_000, 0, 100_000),
            new Vector3Int(3_000_000, 0, -7_000_000),
            new Vector3Int(1 << 30, 0, -(1 << 30)),
        };

        /// <summary>Wind directions, including the axis-aligned cases where one term of the dot product vanishes.</summary>
        private static readonly Vector2[] s_foliageWindCases =
        {
            new Vector2(1f, 0f),
            new Vector2(0f, -1f),
            new Vector2(0.7071068f, 0.7071068f),
            new Vector2(-0.3826834f, 0.9238795f),
        };

        static partial void AddFoliagePhaseScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario("Foliage Wave Phase Matches Its Exact Value At Far Origins", RunFoliagePhaseAccuracy));
            scenarios.Add(new Scenario("Foliage Sway Still Animates At Far Origins", RunFoliagePhaseAnimates));
            scenarios.Add(new Scenario("Foliage Origin Phase Is Exactly Zero At Spawn", RunFoliagePhaseIdentity));
        }

        /// <summary>
        /// The core contract: the phase the shader assembles — a render-space term plus the reduced origin constant —
        /// must produce the same wave as the mathematically exact voxel-space phase, at every origin. Both waves are
        /// checked, so deriving the gust's constant by scaling the primary's (which does not survive the reduction)
        /// fails here. Comparing <c>sin</c> rather than the raw phase is deliberate: the phase is only defined modulo
        /// a cycle, and the sine is what the vertex actually displaces by.
        /// </summary>
        private static bool RunFoliagePhaseAccuracy()
        {
            const string scenario = "Foliage Wave Phase Matches Its Exact Value At Far Origins";
            const float spatialFrequency = 2f * Mathf.PI / FOLIAGE_WAVELENGTH_BLOCKS;

            foreach (Vector3Int origin in s_foliageOriginCases)
            {
                foreach (Vector2 wind in s_foliageWindCases)
                {
                    Vector2 phase = FoliagePhase.OriginPhase(origin, wind, spatialFrequency, FOLIAGE_GUST_SPATIAL_MULTIPLIER);

                    // Render-space offsets a vertex can legitimately sit at once the world has re-anchored.
                    for (int d = -NEAR_ORIGIN_REACH; d <= NEAR_ORIGIN_REACH; d += 97)
                    {
                        Vector2 renderXZ = new Vector2(d, -d * 0.5f);

                        // What the vertex stage computes, in the float32 it computes it in.
                        float alongLocal = Vector2.Dot(renderXZ, wind) * spatialFrequency;
                        float actualPrimary = Mathf.Sin(alongLocal + phase.x);
                        float actualGust = Mathf.Sin(alongLocal * FOLIAGE_GUST_SPATIAL_MULTIPLIER + phase.y);

                        // The oracle: the same wave evaluated at the absolute voxel position, in double.
                        double alongAbsolute = (origin.x + (double)renderXZ.x) * wind.x
                                               + (origin.z + (double)renderXZ.y) * wind.y;
                        double exactPrimary = Math.Sin(spatialFrequency * alongAbsolute);
                        double exactGust = Math.Sin(spatialFrequency * FOLIAGE_GUST_SPATIAL_MULTIPLIER * alongAbsolute);

                        if (Math.Abs(actualPrimary - exactPrimary) > FOLIAGE_SIN_TOLERANCE)
                            return FailFoliage(scenario,
                                $"origin {origin.x},{origin.z} wind {wind} offset {d}: primary sin {actualPrimary} != exact {exactPrimary}.");

                        if (Math.Abs(actualGust - exactGust) > FOLIAGE_SIN_TOLERANCE)
                            return FailFoliage(scenario,
                                $"origin {origin.x},{origin.z} wind {wind} offset {d}: gust sin {actualGust} != exact {exactGust}.");
                    }
                }
            }

            Debug.Log($"[PASS] {scenario}");
            return true;
        }

        /// <summary>
        /// The reported symptom itself, rather than a proxy for it: consecutive frames must produce a *different*
        /// vertex displacement. A phase argument too coarse to resolve one frame of animation collapses successive
        /// frames onto the same value, which is the "staggered, then frozen" foliage this pins against.
        /// </summary>
        private static bool RunFoliagePhaseAnimates()
        {
            const string scenario = "Foliage Sway Still Animates At Far Origins";
            const float spatialFrequency = 2f * Mathf.PI / FOLIAGE_WAVELENGTH_BLOCKS;

            // The wave advances this much per frame; a correct implementation resolves a good fraction of it.
            const float expectedAdvance = FOLIAGE_FREQUENCY * FOLIAGE_FRAME_SECONDS;
            const float minimumMotion = 0.25f * expectedAdvance;

            foreach (Vector3Int origin in s_foliageOriginCases)
            {
                foreach (Vector2 wind in s_foliageWindCases)
                {
                    Vector2 phase = FoliagePhase.OriginPhase(origin, wind, spatialFrequency, FOLIAGE_GUST_SPATIAL_MULTIPLIER);
                    float alongLocal = Vector2.Dot(new Vector2(7f, -3f), wind) * spatialFrequency;

                    // Sampled right around the cycle rather than at one chosen time: a frame of advance moves the
                    // wave by almost nothing near its peaks, so only the best sample says anything about precision.
                    // A wave whose phase is too coarse to resolve a frame is motionless at EVERY sample.
                    const int SAMPLES = 16;
                    const float period = 2f * Mathf.PI / FOLIAGE_FREQUENCY;
                    float peakMotion = 0f;
                    for (int i = 0; i < SAMPLES; i++)
                    {
                        float time = i * (period / SAMPLES);
                        float before = Mathf.Sin(FOLIAGE_FREQUENCY * time - (alongLocal + phase.x));
                        float after = Mathf.Sin(FOLIAGE_FREQUENCY * (time + FOLIAGE_FRAME_SECONDS) - (alongLocal + phase.x));
                        peakMotion = Mathf.Max(peakMotion, Mathf.Abs(after - before));
                    }

                    if (peakMotion < minimumMotion)
                        return FailFoliage(scenario,
                            $"origin {origin.x},{origin.z} wind {wind}: the largest one-frame movement across a full cycle was "
                            + $"{peakMotion}, below the {minimumMotion} an animating wave must clear — the phase is too coarse "
                            + "to resolve a frame.");
                }
            }

            Debug.Log($"[PASS] {scenario}");
            return true;
        }

        /// <summary>
        /// At spawn the origin contributes nothing, so the phase must be *exactly* zero — not merely small. This is
        /// what makes the reduced-phase form bit-identical to the pre-fix absolute-coordinate form near the origin,
        /// which is the whole basis for claiming the fix changes nothing about how foliage looks at spawn.
        /// </summary>
        private static bool RunFoliagePhaseIdentity()
        {
            const string scenario = "Foliage Origin Phase Is Exactly Zero At Spawn";
            const float spatialFrequency = 2f * Mathf.PI / FOLIAGE_WAVELENGTH_BLOCKS;

            foreach (Vector2 wind in s_foliageWindCases)
            {
                Vector2 phase = FoliagePhase.OriginPhase(
                    Vector3Int.zero, wind, spatialFrequency, FOLIAGE_GUST_SPATIAL_MULTIPLIER);

                if (phase.x != 0f || phase.y != 0f)
                    return FailFoliage(scenario, $"wind {wind}: phase at the identity origin was {phase}, expected exactly (0, 0).");
            }

            Debug.Log($"[PASS] {scenario}");
            return true;
        }

        /// <summary>Logs a foliage-phase scenario failure and returns false (the suite's failure idiom).</summary>
        private static bool FailFoliage(string scenario, string detail)
        {
            Debug.LogError($"[FAIL] {scenario} — {detail}");
            return false;
        }
    }
}
