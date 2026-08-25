using Sky;
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
        public const float MinGlobalLightLevel = 1f - MaxSkyDarken / (float)WorldTimeManager.MaxSkylight;

        // Key day fractions the default curve is shaped around (0 = midnight, 0.5 = noon). The named
        // /time targets are deliberately placed on SLOPES rather than plateaus, so `day` reads
        // differently from `noon` and `night` from `midnight`.
        private const float NIGHT_HOLD_END = 0.15f;
        private const float SUNRISE = 0.2083f;

        // The sky GRADIENTS key their dawn on the celestial horizon crossing instead, because a color
        // is judged against the sun disc beside it while a light level is not. SUNRISE above is
        // Minecraft's named /time target (tick 23000), which falls 1000 ticks BEFORE the sun actually
        // rises; keying dawn there finished the sunrise while the sun was still 10.55 degrees down.
        // Dusk needs no such split: SUNSET (tick 12000) already lands on the crossing, so using this
        // makes the gradients an exact mirror about noon.
        private const float DAWN_HORIZON_CROSSING = 0.25f;
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

        // Sky (RF-2). The zenith runs darker and more saturated than the horizon at every hour, which
        // is what reads as depth; night keys stay in the blue family so they agree with the moonlight
        // tint RF-1 §3 authors into the sky-light gradient.
        //
        // These are LINEAR values, not sRGB. The project renders in linear color space and the sky
        // globals are pushed raw through Shader.SetGlobalColor, which performs no conversion — so a
        // value here of 0.075 reaches the screen at roughly sRGB 0.30, four times brighter than the
        // swatch suggests. Judge these numbers by the render, never by the Inspector gradient.
        private static readonly Color s_zenithNight = new Color(0.004f, 0.005f, 0.024f, 1f);
        private static readonly Color s_zenithTwilight = new Color(0.16f, 0.14f, 0.33f, 1f);
        private static readonly Color s_zenithSoftDay = new Color(0.18f, 0.42f, 0.80f, 1f);
        private static readonly Color s_zenithDay = new Color(0.16f, 0.42f, 0.88f, 1f);
        private static readonly Color s_horizonNight = new Color(0.010f, 0.013f, 0.042f, 1f);
        private static readonly Color s_horizonDawn = new Color(0.85f, 0.45f, 0.35f, 1f);
        private static readonly Color s_horizonSoftDay = new Color(0.62f, 0.78f, 0.93f, 1f);
        private static readonly Color s_horizonDay = new Color(0.70f, 0.85f, 0.97f, 1f);
        private static readonly Color s_horizonDusk = new Color(0.92f, 0.42f, 0.20f, 1f);

        [Tooltip("Real seconds in one full day/night cycle. 1200 (20 minutes) is Minecraft parity.")]
        [Min(1f)]
        [SerializeField]
        private float _dayLengthSeconds = 1200f;

        [Tooltip("Global light level over the day — 1 = full daylight, ~0.27 = the moonlight floor (values below it are clamped). Evaluated at DayFraction: 0 = midnight, 0.5 = noon.")]
        [SerializeField]
        private AnimationCurve _globalLightLevelOverDay = BuildDefaultLightCurve();

        [Tooltip("Tint applied to the sky-light channel over the day. Flat white leaves terrain color exactly as it is today; blue night keys give moonlight its Purkinje shift.")]
        [SerializeField]
        private Gradient _skylightOverDay = BuildDefaultSkylightGradient();

        [Tooltip("Camera background color over the day. Replaces the old lerp(night, day, lightLevel), which collapsed dawn and dusk onto the same color.")]
        [SerializeField]
        private Gradient _backgroundOverDay = BuildDefaultBackgroundGradient();

        // Header text is user-facing in the Inspector and the Sky Editor, so it carries no backlog ID.
        [Header("Sky")]
        [Tooltip("Observer latitude in degrees; positive is north. Tilts the sun's arc — 0 puts it overhead at noon, 90 keeps it on the horizon all day.")]
        [Range(-90f, 90f)]
        [SerializeField]
        private float _observerLatitude = 45f;

        [Tooltip("Sky color straight overhead, over the day. Evaluated at DayFraction: 0 = midnight, 0.5 = noon.")]
        [SerializeField]
        private Gradient _zenithOverDay = BuildDefaultZenithGradient();

        [Tooltip("Sky color at the horizon, over the day. Also drives the fog color when distance fog is enabled.")]
        [SerializeField]
        private Gradient _horizonOverDay = BuildDefaultHorizonGradient();

        [Tooltip("Angular radius of the sun disc, in degrees. The real sun is about 0.27; larger reads better at voxel scale.")]
        [Range(0.1f, 10f)]
        [SerializeField]
        private float _sunAngularRadius = 1.5f;

        [Tooltip("Angular radius of the moon disc, in degrees.")]
        [Range(0.1f, 10f)]
        [SerializeField]
        private float _moonAngularRadius = 1.7f;

        [Tooltip("Brightness of the star field at its fullest, once the sun is well below the horizon.")]
        [Range(0f, 2f)]
        [SerializeField]
        private float _starBrightness = 1f;

        [Tooltip("Where distance fog begins, as a fraction of where it becomes opaque. With a curved falloff this can sit early — the fog stays near-invisible until well past it.")]
        [Range(0f, 0.95f)]
        [SerializeField]
        private float _fogStartFraction = AtmosphericFog.DefaultFogStartFraction;

        [Tooltip("Shape of the fog falloff. 1 = linear (an even ramp, which paints a visible gradient across mountains). Higher = soft near the player and thickening with distance.")]
        [Range(1f, 6f)]
        [SerializeField]
        private float _fogCurvePower = AtmosphericFog.DefaultFogCurvePower;

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
        public Color EvaluateSkylightColor(float dayFraction) => _skylightOverDay.Evaluate(dayFraction);

        /// <summary>Samples the camera background color for a point in the day.</summary>
        /// <param name="dayFraction">Position in the day, <c>[0,1)</c>.</param>
        /// <returns>The camera's clear color.</returns>
        public Color EvaluateBackgroundColor(float dayFraction) => _backgroundOverDay.Evaluate(dayFraction);

        /// <summary>Observer latitude in degrees, positive north — the tilt of the celestial arcs.</summary>
        public float ObserverLatitude => _observerLatitude;

        /// <summary>Angular radius of the sun disc, in degrees.</summary>
        public float SunAngularRadius => _sunAngularRadius;

        /// <summary>Angular radius of the moon disc, in degrees.</summary>
        public float MoonAngularRadius => _moonAngularRadius;

        /// <summary>Peak brightness of the star field.</summary>
        public float StarBrightness => _starBrightness;

        /// <summary>Where distance fog begins, as a fraction of where it becomes opaque.</summary>
        public float FogStartFraction => _fogStartFraction;

        /// <summary>Exponent shaping the fog falloff; 1 is linear, higher is back-loaded.</summary>
        public float FogCurvePower => _fogCurvePower;

        /// <summary>Samples the overhead sky color for a point in the day.</summary>
        /// <param name="dayFraction">Position in the day, <c>[0,1)</c>.</param>
        /// <returns>The zenith color.</returns>
        public Color EvaluateZenithColor(float dayFraction) => _zenithOverDay.Evaluate(dayFraction);

        /// <summary>Samples the horizon sky color for a point in the day.</summary>
        /// <param name="dayFraction">Position in the day, <c>[0,1)</c>.</param>
        /// <returns>The horizon color, which distance fog also adopts.</returns>
        public Color EvaluateHorizonColor(float dayFraction) => _horizonOverDay.Evaluate(dayFraction);

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
        private static Gradient BuildDefaultSkylightGradient()
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
                    new GradientColorKey(s_dawn, DAWN_HORIZON_CROSSING),
                    new GradientColorKey(s_paleDay, MORNING),
                    new GradientColorKey(s_day, 0.5f),
                    new GradientColorKey(s_paleDay, AFTERNOON),
                    new GradientColorKey(s_dusk, SUNSET),
                    new GradientColorKey(s_night, NIGHT_HOLD_START),
                },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
            return gradient;
        }

        /// <summary>
        /// The engine's default overhead sky gradient, for editor tooling that re-authors an asset.
        /// </summary>
        /// <returns>A fresh gradient; the caller owns it.</returns>
        /// <remarks>
        /// Field initializers run only when an instance is <i>created</i>, so editing the defaults in
        /// code leaves every existing <c>.asset</c> on its serialized values. Tooling needs this entry
        /// point to push new defaults into an asset that already exists.
        /// </remarks>
        public static Gradient CreateDefaultZenithGradient() => BuildDefaultZenithGradient();

        /// <summary>The engine's default horizon gradient, for editor tooling that re-authors an asset.</summary>
        /// <returns>A fresh gradient; the caller owns it.</returns>
        public static Gradient CreateDefaultHorizonGradient() => BuildDefaultHorizonGradient();

        /// <summary>The engine's default sky-light tint gradient, for editor tooling that re-authors an asset.</summary>
        /// <returns>A fresh gradient; the caller owns it.</returns>
        public static Gradient CreateDefaultSkylightGradient() => BuildDefaultSkylightGradient();

        /// <summary>The engine's default camera background gradient, for editor tooling that re-authors an asset.</summary>
        /// <returns>A fresh gradient; the caller owns it.</returns>
        public static Gradient CreateDefaultBackgroundGradient() => BuildDefaultBackgroundGradient();

        /// <summary>Builds the default overhead sky gradient: near-black night through to saturated midday blue.</summary>
        /// <returns>A gradient over one day.</returns>
        private static Gradient BuildDefaultZenithGradient()
        {
            return BuildDayGradient(s_zenithNight, s_zenithTwilight, s_zenithSoftDay, s_zenithDay, s_zenithTwilight);
        }

        /// <summary>Builds the default horizon gradient: night blue, warm sunrise, pale day, orange sunset.</summary>
        /// <returns>A gradient over one day.</returns>
        private static Gradient BuildDefaultHorizonGradient()
        {
            return BuildDayGradient(s_horizonNight, s_horizonDawn, s_horizonSoftDay, s_horizonDay, s_horizonDusk);
        }

        /// <summary>
        /// Builds a day-long gradient through the five sky moments, using all eight keys Unity allows.
        /// </summary>
        /// <param name="night">Color held through the night.</param>
        /// <param name="dawn">Color where the sun crosses the horizon at dawn.</param>
        /// <param name="softDay">Color mid-morning and mid-afternoon.</param>
        /// <param name="day">Color at noon.</param>
        /// <param name="dusk">Color where the sun crosses the horizon at dusk.</param>
        /// <returns>A gradient over one day, whose final key meets the 0.0 key at the same night color.</returns>
        /// <remarks>
        /// The eight keys mirror exactly about noon, so dawn and dusk hold their shape in common and
        /// differ only in hue — dawn cooler and pinker, dusk warmer. Unity allows no ninth key, so a
        /// new moment here means giving one up rather than adding one.
        /// </remarks>
        private static Gradient BuildDayGradient(Color night, Color dawn, Color softDay, Color day, Color dusk)
        {
            Gradient gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(night, 0f),
                    new GradientColorKey(night, NIGHT_HOLD_END),
                    new GradientColorKey(dawn, DAWN_HORIZON_CROSSING),
                    new GradientColorKey(softDay, MORNING),
                    new GradientColorKey(day, 0.5f),
                    new GradientColorKey(softDay, AFTERNOON),
                    new GradientColorKey(dusk, SUNSET),
                    new GradientColorKey(night, NIGHT_HOLD_START),
                },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
            return gradient;
        }
    }
}
