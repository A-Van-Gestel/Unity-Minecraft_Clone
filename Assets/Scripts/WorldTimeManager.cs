using Data.WorldTypes;
using Sky;
using UnityEngine;

/// <summary>
/// The world's day/night clock (RF-1) — a plain manager owned by <see cref="World"/> and ticked from
/// <c>World.Update()</c>, matching the <see cref="WorldJobManager"/> pattern. Owns the authoritative
/// time value and every quantity derived from it: the render globals, the sun direction RF-2 will
/// consume, and the <see cref="SkyDarken"/> gameplay reads.
/// </summary>
/// <remarks>
/// <para>
/// Time is a <see cref="long"/> tick count, not an accumulated float: a float day fraction advanced by
/// <c>Time.deltaTime</c> loses precision as the world ages, and the persisted value would inherit the
/// drift. <see cref="Tick"/> accumulates fractional ticks in a residue that never exceeds one tick and
/// moves only whole ticks into the counter, so elapsed time is exact for the life of a world.
/// </para>
/// <para>
/// Stored sky light is time-invariant <b>sky exposure</b>; nothing here mutates voxel light. Darkening
/// is applied at read time — in the shader via <see cref="GlobalLightLevel"/>, and in gameplay via
/// <see cref="SkyDarken"/>. The curve is authored as the light level and the darken is derived from it,
/// so they cannot disagree by construction.
/// </para>
/// </remarks>
public class WorldTimeManager
{
    /// <summary>Ticks in one full day/night cycle (Minecraft parity).</summary>
    public const int TicksPerDay = 24000;

    /// <summary>
    /// Tick offset between the two anchors in play: Minecraft anchors tick 0 at sunrise (06:00), while
    /// <see cref="DayFraction"/> anchors 0 at midnight. Applied in exactly one place.
    /// </summary>
    public const int SunriseTickOffset = 6000;

    /// <summary>Maximum value of the stored 4-bit sky light channel; the denominator that normalizes sky darken.</summary>
    public const int MaxSkylight = 15;

    private readonly TimeOfDaySettings _settings;

    /// <summary>Sub-tick remainder carried between frames; always in <c>[0, 1)</c>.</summary>
    private float _tickResidue;

    /// <summary>
    /// Creates a clock for a world.
    /// </summary>
    /// <param name="settings">The world type's authored day/night settings. Must not be null — <see cref="World"/> supplies a default instance when the world type has none.</param>
    public WorldTimeManager(TimeOfDaySettings settings)
    {
        _settings = settings;
    }

    /// <summary>Total elapsed world time in ticks. Never negative.</summary>
    public long TimeTicks { get; private set; }

    /// <summary>When true, <see cref="Tick"/> is a no-op — the world holds its current time of day.</summary>
    public bool IsFrozen { get; set; }

    /// <summary>Whole days elapsed since the world was created.</summary>
    public long ElapsedDays => TimeTicks / TicksPerDay;

    /// <summary>Time within the current day, in <c>[0, 24000)</c> ticks — 0 = sunrise, Minecraft's anchor.</summary>
    public int DayTicks => (int)(TimeTicks % TicksPerDay);

    /// <summary>Position within the current day in <c>[0, 1)</c> — 0 = midnight, 0.5 = noon.</summary>
    public float DayFraction => (DayTicks + SunriseTickOffset) % TicksPerDay / (float)TicksPerDay;

    /// <summary>
    /// Sine of the sun's elevation: +1 at noon, 0 at the horizon, −1 at midnight. RF-2's skybox derives
    /// the sun/moon direction and the star fade from this.
    /// </summary>
    public float SunElevation => Mathf.Sin((DayFraction - 0.25f) * 2f * Mathf.PI);

    /// <summary>
    /// The <c>GlobalLightLevel</c> shader global: normalized brightness of fully-exposed sky, sampled
    /// straight from the authored curve. Continuous, so dusk does not step through integer levels.
    /// </summary>
    /// <remarks>
    /// Its range is <c>[0.27, 1]</c>, not <c>[0, 1]</c> — under the moonlight floor a fully sky-exposed
    /// voxel never falls below effective level 4, so pitch black is unreachable under open sky.
    /// </remarks>
    public float GlobalLightLevel => _settings.EvaluateGlobalLightLevel(DayFraction);

    /// <summary>Sky darken for the current time as a continuous value, derived from the light curve.</summary>
    public float ContinuousSkyDarken => (1f - GlobalLightLevel) * MaxSkylight;

    /// <summary>
    /// How many levels to subtract from stored sky light right now, in <c>[0, 11]</c> — the value all
    /// time-sensitive gameplay consumes. Rounded from <see cref="ContinuousSkyDarken"/>, so the queried
    /// level and the rendered one agree to within half a level in either direction.
    /// </summary>
    public int SkyDarken => Mathf.Clamp(Mathf.RoundToInt(ContinuousSkyDarken), 0, TimeOfDaySettings.MaxSkyDarken);

    /// <summary>
    /// Elapsed time as a continuous day count. Its fractional part is exactly <see cref="DayFraction"/>,
    /// which is what keeps the moon's phase and the sun's position on one clock.
    /// </summary>
    public double ContinuousDays => (TimeTicks + SunriseTickOffset) / (double)TicksPerDay;

    /// <summary>Direction of the sun from the observer — a unit vector in Unity render space (RF-2).</summary>
    public Vector3 SunDirection => CelestialMath.SunDirection(DayFraction, _settings.ObserverLatitude);

    /// <summary>Direction of the moon from the observer — a unit vector in Unity render space (RF-2).</summary>
    public Vector3 MoonDirection => CelestialMath.MoonDirection(ContinuousDays, _settings.ObserverLatitude);

    /// <summary>Lit fraction of the moon's disc: 0 at new moon, 1 at full.</summary>
    public float MoonPhase => CelestialMath.MoonIlluminatedFraction(ContinuousDays);

    /// <summary>Orientation of the celestial sphere, which the star field rides.</summary>
    public Quaternion SkyRotation => CelestialMath.SkyRotation(DayFraction, _settings.ObserverLatitude);

    /// <summary>The sky-light tint for the current time.</summary>
    public Color SkylightColor => _settings.EvaluateSkylightColor(DayFraction);

    /// <summary>The overhead sky color for the current time.</summary>
    public Color ZenithColor => _settings.EvaluateZenithColor(DayFraction);

    /// <summary>The horizon sky color for the current time; distance fog adopts this too.</summary>
    public Color HorizonColor => _settings.EvaluateHorizonColor(DayFraction);

    /// <summary>The camera background color for the current time.</summary>
    public Color BackgroundColor => _settings.EvaluateBackgroundColor(DayFraction);

    /// <summary>Ticks the world clock advances per real second.</summary>
    private float TicksPerSecond => TicksPerDay / _settings.DayLengthSeconds;

    /// <summary>
    /// Advances the clock. Fractional ticks accumulate in a bounded residue rather than in the counter,
    /// so no rounding error ever reaches <see cref="TimeTicks"/>.
    /// </summary>
    /// <param name="deltaTimeSeconds">Real seconds elapsed since the last tick.</param>
    public void Tick(float deltaTimeSeconds)
    {
        if (IsFrozen || deltaTimeSeconds <= 0f) return;

        _tickResidue += deltaTimeSeconds * TicksPerSecond;
        if (_tickResidue < 1f) return;

        long wholeTicks = (long)_tickResidue;
        _tickResidue -= wholeTicks;
        TimeTicks += wholeTicks;
    }

    /// <summary>
    /// Sets the time of day within the current day, leaving <see cref="ElapsedDays"/> unchanged.
    /// </summary>
    /// <param name="dayTicks">Target time in <c>[0, 24000)</c> ticks (0 = sunrise). Values outside the range wrap.</param>
    public void SetDayTime(int dayTicks)
    {
        int wrapped = (int)Mod(dayTicks, TicksPerDay);
        TimeTicks = ElapsedDays * TicksPerDay + wrapped;
        _tickResidue = 0f;
    }

    /// <summary>
    /// Sets the total elapsed time directly. Used when restoring a saved world.
    /// </summary>
    /// <param name="timeTicks">Total elapsed ticks; negative values are clamped to zero.</param>
    public void SetTotalTicks(long timeTicks)
    {
        TimeTicks = timeTicks < 0 ? 0 : timeTicks;
        _tickResidue = 0f;
    }

    /// <summary>
    /// Advances (or rewinds) the clock by a tick delta, clamping at the world's zero point.
    /// </summary>
    /// <param name="deltaTicks">Ticks to add; may be negative.</param>
    public void AddTicks(long deltaTicks)
    {
        SetTotalTicks(TimeTicks + deltaTicks);
    }

    /// <summary>Floored modulo, so a negative operand wraps forward instead of returning a negative remainder.</summary>
    /// <param name="value">The dividend.</param>
    /// <param name="modulus">The (positive) divisor.</param>
    /// <returns>The non-negative remainder.</returns>
    private static long Mod(long value, long modulus)
    {
        long remainder = value % modulus;
        return remainder < 0 ? remainder + modulus : remainder;
    }
}
