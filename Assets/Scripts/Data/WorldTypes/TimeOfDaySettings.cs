using UnityEngine;

namespace Data.WorldTypes
{
    /// <summary>
    /// The designer-owned day/night curve and color gradients for a world type (RF-1). Linked from
    /// <see cref="WorldTypeDefinition"/> rather than embedded as a serializable class so several world
    /// types — and, later, individual biomes — can share and override one authored asset.
    /// </summary>
    /// <remarks>
    /// The curve is authored directly in <b>global light level</b> — the same 0–1 quantity the shaders
    /// have always consumed, where 1 is full daylight. Sky darken (Minecraft's 0–11 integer, which
    /// gameplay subtracts from stored sky light) is <i>derived</i> from it, so there is exactly one
    /// authored source and rendering and gameplay cannot disagree about how dark it is.
    /// </remarks>
    [CreateAssetMenu(fileName = "New Time Of Day Settings", menuName = "Minecraft/Time Of Day Settings")]
    public class TimeOfDaySettings : ScriptableObject
    {
        /// <summary>The deepest sky darken the engine allows, matching Minecraft's cap.</summary>
        /// <remarks>
        /// Caps night at effective sky light <c>15 − 11 = 4</c> — the moonlight floor whose
        /// well-tested gameplay thresholds (hostile spawns at ≤ 7) transfer verbatim.
        /// </remarks>
        public const int MaxSkyDarken = 11;

        /// <summary>
        /// The dimmest global light level the moonlight floor permits (<c>1 − 11/15 ≈ 0.267</c>).
        /// The curve is clamped to this, so a curve authored down to 0 still cannot make a fully
        /// sky-exposed voxel darker than effective level 4.
        /// </summary>
        public const float MinGlobalLightLevel = 1f - MaxSkyDarken / (float)WorldTimeManager.MaxSkyLight;

        // Key day fractions the default curve is shaped around (0 = midnight, 0.5 = noon). The named
        // /time targets are deliberately placed on SLOPES rather than plateaus, so `day` reads
        // differently from `noon` and `night` from `midnight`.
        private const float NIGHT_HOLD_END = 0.15f;
        private const float SUNRISE = 0.2083f;
        private const float MORNING = 0.2917f;
        private const float FULL_DAY_START = 0.40f;
        private const float FULL_DAY_END = 0.60f;
        private const float AFTERNOON = 0.7083f;
        private const float SUNSET = 0.75f;
        private const float NIGHTFALL = 0.7917f;
        private const float NIGHT_HOLD_START = 0.85f;

        // Light levels at those fractions. Dawn and dusk mirror each other.
        private const float TWILIGHT_LEVEL = 0.35f;
        private const float GOLDEN_LEVEL = 0.62f;
        private const float SOFT_DAY_LEVEL = 0.88f;
        private const float FULL_DAY_LEVEL = 1f;

        // The shipped World.night / World.day camera background colors, carried over verbatim as the
        // night and midday anchors; the twilight keys between them are new.
        private static readonly Color s_night = new Color(0.03137255f, 0f, 0.44705883f, 1f);
        private static readonly Color s_day = new Color(0f, 0.90980387f, 1f, 1f);
        private static readonly Color s_dawn = new Color(0.55f, 0.30f, 0.38f, 1f);
        private static readonly Color s_dusk = new Color(0.75f, 0.32f, 0.22f, 1f);
        private static readonly Color s_paleDay = new Color(0.35f, 0.72f, 0.95f, 1f);

        [Tooltip("Real seconds in one full day/night cycle. 1200 (20 minutes) is Minecraft parity.")]
        [Min(1f)]
        [SerializeField]
        private float _dayLengthSeconds = 1200f;

        [Tooltip("Global light level over the day — 1 = full daylight, ~0.27 = the moonlight floor (values below it are clamped). Evaluated at DayFraction: 0 = midnight, 0.5 = noon.")]
        [SerializeField]
        private AnimationCurve _globalLightLevelOverDay = BuildDefaultLightCurve();

        [Tooltip("Tint applied to the sky-light channel over the day. Flat white leaves terrain color exactly as it is today; blue night keys give moonlight its Purkinje shift.")]
        [SerializeField]
        private Gradient _skyLightOverDay = BuildDefaultSkyLightGradient();

        [Tooltip("Camera background color over the day. Replaces the old lerp(night, day, lightLevel), which collapsed dawn and dusk onto the same color.")]
        [SerializeField]
        private Gradient _backgroundOverDay = BuildDefaultBackgroundGradient();

        /// <summary>Real seconds in one full day/night cycle.</summary>
        public float DayLengthSeconds => _dayLengthSeconds;

        /// <summary>
        /// Samples the authored light curve, clamped so the moonlight floor holds however the curve
        /// is authored.
        /// </summary>
        /// <param name="dayFraction">Position in the day, <c>[0,1)</c> — 0 = midnight, 0.5 = noon.</param>
        /// <returns>Global light level in <c>[MinGlobalLightLevel, 1]</c>.</returns>
        public float EvaluateGlobalLightLevel(float dayFraction)
        {
            return Mathf.Clamp(_globalLightLevelOverDay.Evaluate(dayFraction), MinGlobalLightLevel, 1f);
        }

        /// <summary>Samples the sky-light tint for a point in the day.</summary>
        /// <param name="dayFraction">Position in the day, <c>[0,1)</c>.</param>
        /// <returns>The tint multiplied into the shader's sky-light channel.</returns>
        public Color EvaluateSkyLightColor(float dayFraction) => _skyLightOverDay.Evaluate(dayFraction);

        /// <summary>Samples the camera background color for a point in the day.</summary>
        /// <param name="dayFraction">Position in the day, <c>[0,1)</c>.</param>
        /// <returns>The camera's clear color.</returns>
        public Color EvaluateBackgroundColor(float dayFraction) => _backgroundOverDay.Evaluate(dayFraction);

        /// <summary>
        /// Builds the default light curve: a night hold, a dawn ramp through twilight and golden hour,
        /// a midday plateau, and the mirrored dusk.
        /// </summary>
        /// <returns>A piecewise-linear curve over one day.</returns>
        private static AnimationCurve BuildDefaultLightCurve()
        {
            return BuildLinearCurve(
                new[]
                {
                    0f, NIGHT_HOLD_END, SUNRISE, MORNING, FULL_DAY_START,
                    FULL_DAY_END, AFTERNOON, SUNSET, NIGHTFALL, NIGHT_HOLD_START, 1f,
                },
                new[]
                {
                    MinGlobalLightLevel, MinGlobalLightLevel, TWILIGHT_LEVEL, SOFT_DAY_LEVEL, FULL_DAY_LEVEL,
                    FULL_DAY_LEVEL, SOFT_DAY_LEVEL, GOLDEN_LEVEL, TWILIGHT_LEVEL, MinGlobalLightLevel, MinGlobalLightLevel,
                });
        }

        /// <summary>
        /// Builds a piecewise-linear curve through the given points.
        /// </summary>
        /// <param name="times">Key times, ascending.</param>
        /// <param name="values">Key values, one per time.</param>
        /// <returns>The curve, with tangents set so segments stay straight.</returns>
        /// <remarks>
        /// Tangents are computed rather than left to Unity's auto-smoothing: a smoothed curve through
        /// plateau corners overshoots past the endpoints, which would push the light level outside its
        /// clamped range for a few frames at every dawn and dusk.
        /// </remarks>
        private static AnimationCurve BuildLinearCurve(float[] times, float[] values)
        {
            Keyframe[] keys = new Keyframe[times.Length];
            for (int i = 0; i < times.Length; i++)
            {
                float inTangent = i > 0 ? Slope(times[i - 1], values[i - 1], times[i], values[i]) : 0f;
                float outTangent = i < times.Length - 1 ? Slope(times[i], values[i], times[i + 1], values[i + 1]) : 0f;
                keys[i] = new Keyframe(times[i], values[i], inTangent, outTangent);
            }

            return new AnimationCurve(keys);
        }

        /// <summary>Slope of the segment between two keys.</summary>
        /// <param name="t0">Start time.</param>
        /// <param name="v0">Start value.</param>
        /// <param name="t1">End time.</param>
        /// <param name="v1">End value.</param>
        /// <returns>The segment's gradient, or 0 for a degenerate span.</returns>
        private static float Slope(float t0, float v0, float t1, float v1)
        {
            float span = t1 - t0;
            return span > Mathf.Epsilon ? (v1 - v0) / span : 0f;
        }

        /// <summary>Builds the default sky-light tint: flat white, matching the shipped no-op gradient.</summary>
        /// <returns>A white-to-white gradient.</returns>
        private static Gradient BuildDefaultSkyLightGradient()
        {
            Gradient gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
            return gradient;
        }

        /// <summary>
        /// Builds the default background gradient: night, a warm dawn, daylight, an orange dusk, night.
        /// </summary>
        /// <returns>A gradient over one day.</returns>
        /// <remarks>
        /// Uses all <b>eight</b> color keys Unity's <see cref="Gradient"/> allows — adding a hue here
        /// means giving one up. The final key holds to 1.0, which meets the 0.0 key at the same night color.
        /// </remarks>
        private static Gradient BuildDefaultBackgroundGradient()
        {
            Gradient gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(s_night, 0f),
                    new GradientColorKey(s_night, NIGHT_HOLD_END),
                    new GradientColorKey(s_dawn, SUNRISE),
                    new GradientColorKey(s_paleDay, MORNING),
                    new GradientColorKey(s_day, 0.5f),
                    new GradientColorKey(s_paleDay, AFTERNOON),
                    new GradientColorKey(s_dusk, SUNSET),
                    new GradientColorKey(s_night, NIGHT_HOLD_START),
                },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
            return gradient;
        }
    }
}
