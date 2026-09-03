using UnityEngine;

namespace Sky
{
    /// <summary>
    /// How heavily distance fog is applied, as chosen in the graphics settings.
    /// </summary>
    /// <remarks>
    /// The levels change how <i>early</i> fog accumulates, never how opaque it ends up: every enabled
    /// level still reaches full opacity at the fog end. Capping terminal opacity instead would leave
    /// terrain partly visible where it stops being loaded, which is the artifact fog exists to hide.
    /// </remarks>
    public enum FogStyle
    {
        /// <summary>No distance fog. The loaded-chunk boundary becomes visible again.</summary>
        Off = 0,

        /// <summary>Fog held back to the far distance — atmosphere without veiling the middle ground.</summary>
        Light = 1,

        /// <summary>The world type's authored fog curve, unmodified.</summary>
        Full = 2,
    }

    /// <summary>
    /// Distance-fog range derived from the player's view distance (RF-2 §4) — pure functions with no
    /// Unity state, consumed by <see cref="World"/> when it publishes the fog shader globals.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The fog exists to hide the loaded-chunk boundary: terrain must reach the fog color <i>before</i>
    /// the last chunk ends, or the player sees geometry stop against open sky. Both fractions below are
    /// therefore expressed relative to the loaded radius rather than as absolute distances, so the fog
    /// follows the view-distance setting automatically.
    /// </para>
    /// <para>
    /// This is the engine's own fog, not Unity's <see cref="RenderSettings"/> fog. Three reasons: the
    /// block shaders would otherwise need <c>multi_compile_fog</c> and its keyword variants (the cost
    /// RF-2 §4 flagged for the GS-4 audit); <c>RenderSettings</c> is scene state, so driving it at
    /// runtime risks leaking fog into the editor's Scene view; and a zero range is a natural "fog off"
    /// switch, which makes uninitialized shader globals render <i>unfogged</i> rather than solid black.
    /// </para>
    /// </remarks>
    public static class AtmosphericFog
    {
        /// <summary>
        /// Fraction of the loaded radius at which fog becomes fully opaque.
        /// </summary>
        /// <remarks>
        /// Below 1 on purpose: full opacity has to be reached inside the loaded area, because the
        /// boundary it conceals is at exactly 1.
        /// </remarks>
        public const float FogEndFraction = 0.92f;

        /// <summary>
        /// Default fraction of the fog end at which fog begins. Nearer than this the world is clear.
        /// </summary>
        /// <remarks>
        /// Deliberately early, because <see cref="DefaultFogCurvePower"/> keeps the near end nearly
        /// invisible. A late start compresses the whole 0→1 transition into a short band, and any object
        /// tall enough to span that band — a mountain — gets the ramp painted across it as a visible
        /// gradient. Starting early and curving hard spreads the same transition over a long distance.
        /// </remarks>
        public const float DefaultFogStartFraction = 0.15f;

        /// <summary>
        /// Default exponent shaping fog accumulation across the range. 1 is linear; higher is
        /// back-loaded — soft near the player, thickening with distance.
        /// </summary>
        public const float DefaultFogCurvePower = 3f;

        /// <summary>The lowest exponent the shader will honor; below 1 fog would be front-loaded.</summary>
        public const float MinFogCurvePower = 1f;

        /// <summary>
        /// Exponent multiplier for <see cref="FogStyle.Light"/>.
        /// </summary>
        /// <remarks>
        /// Steepening the curve is the safe way to thin fog: <c>t^p</c> is 1 at <c>t = 1</c> for every
        /// exponent, so the far boundary stays fully hidden no matter how high this goes. Scaling the
        /// resulting opacity instead would uncover that boundary.
        /// </remarks>
        public const float LightFogCurveMultiplier = 2f;

        /// <summary>
        /// Distance at which fog fully hides the world.
        /// </summary>
        /// <param name="viewDistanceChunks">The player's view distance, as a radius in chunks.</param>
        /// <param name="maxDistance">Hard ceiling, normally the camera's far clip plane.</param>
        /// <returns>The fog end distance in blocks, or 0 when fog should be disabled.</returns>
        public static float ComputeFogEnd(int viewDistanceChunks, float maxDistance)
        {
            if (viewDistanceChunks <= 0 || maxDistance <= 0f) return 0f;

            float loadedRadius = viewDistanceChunks * VoxelData.ChunkWidth;
            return Mathf.Min(loadedRadius * FogEndFraction, maxDistance);
        }

        /// <summary>
        /// Distance at which fog starts to accumulate.
        /// </summary>
        /// <param name="viewDistanceChunks">The player's view distance, as a radius in chunks.</param>
        /// <param name="maxDistance">Hard ceiling, normally the camera's far clip plane.</param>
        /// <param name="startFraction">Fraction of the fog end at which fog begins; higher is thinner.</param>
        /// <returns>The fog start distance in blocks, or 0 when fog should be disabled.</returns>
        public static float ComputeFogStart(int viewDistanceChunks, float maxDistance, float startFraction)
        {
            return ComputeFogEnd(viewDistanceChunks, maxDistance) * Mathf.Clamp01(startFraction);
        }

        /// <summary>
        /// Packs the fog range into the vector the shaders read.
        /// </summary>
        /// <param name="viewDistanceChunks">The player's view distance, as a radius in chunks.</param>
        /// <param name="maxDistance">Hard ceiling, normally the camera's far clip plane.</param>
        /// <param name="startFraction">Fraction of the fog end at which fog begins; higher is thinner.</param>
        /// <param name="curvePower">Exponent shaping accumulation; 1 is linear, higher is back-loaded.</param>
        /// <param name="style">The player's chosen fog level.</param>
        /// <returns><c>(start, end, curvePower, 0)</c>; a zero-width range means fog is off.</returns>
        public static Vector4 ComputeFogRange(int viewDistanceChunks, float maxDistance, float startFraction,
            float curvePower, FogStyle style)
        {
            if (style == FogStyle.Off) return Vector4.zero;

            float power = Mathf.Max(curvePower, MinFogCurvePower);
            if (style == FogStyle.Light) power *= LightFogCurveMultiplier;

            return new Vector4(
                ComputeFogStart(viewDistanceChunks, maxDistance, startFraction),
                ComputeFogEnd(viewDistanceChunks, maxDistance),
                power,
                0f);
        }

        /// <summary>
        /// How much fog covers a fragment at a given distance — the C# mirror of the shader's
        /// <c>VoxelFogFactor</c>, kept here so the curve's shape is testable.
        /// </summary>
        /// <param name="distance">Horizontal distance from the camera, in blocks.</param>
        /// <param name="range">The packed range from <see cref="ComputeFogRange"/>.</param>
        /// <returns>0 = clear, 1 = fully fogged.</returns>
        /// <remarks>
        /// Any change here must be mirrored in <c>VoxelFog.hlsl</c>; the suite guards this copy, and the
        /// shader half is capture-verified only.
        /// </remarks>
        public static float EvaluateFogFactor(float distance, Vector4 range)
        {
            float span = range.y - range.x;
            if (span <= 0f) return 0f;

            float t = Mathf.Clamp01((distance - range.x) / span);
            return Mathf.Pow(t, Mathf.Max(range.z, MinFogCurvePower));
        }
    }
}
