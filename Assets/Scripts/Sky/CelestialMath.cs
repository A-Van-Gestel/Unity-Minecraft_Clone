using UnityEngine;

namespace Sky
{
    /// <summary>
    /// The world's celestial body simulation (RF-2) — pure functions mapping world time and observer
    /// latitude to sun/moon directions, moon phase, and the rotation of the star field. Owns no state,
    /// touches no Unity object, and is consumed by <see cref="WorldTimeManager"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Equinox model.</b> Solar declination is pinned at zero (<see cref="SolarDeclinationRadians"/>),
    /// which fixes day length at exactly half a cycle year-round. This is deliberate, not an
    /// approximation left for later: RF-1's light curve is authored against day fraction and is the
    /// single source for both <c>GlobalLightLevel</c> and gameplay's <c>SkyDarken</c>. A seasonally
    /// varying declination would move sunrise away from the time the curve brightens at, so the disc
    /// would sit below the horizon while the world was still lit. Declination enters the horizon
    /// equations at exactly one place, so seasons remain a one-parameter change here plus a curve
    /// remap there.
    /// </para>
    /// <para>
    /// <b>Coordinate space.</b> Every direction returned is a unit vector in Unity render space with
    /// the conventional axis roles: <c>+x</c> east, <c>+y</c> up (zenith), <c>+z</c> north. These are
    /// directions, not positions, so the floating-origin shift (<c>WorldOrigin</c>) does not apply.
    /// </para>
    /// <para>
    /// <b>The moon is one model, not two.</b> Its position and its phase both derive from a single
    /// elongation angle, so the classic couplings come out for free rather than being authored:
    /// a full moon necessarily peaks at midnight, a new moon at noon, and moonrise necessarily slips
    /// later by one synodic fraction of a day per day.
    /// </para>
    /// </remarks>
    public static class CelestialMath
    {
        /// <summary>Days between successive new moons — the synodic month, which is the period of the phase cycle.</summary>
        /// <remarks>
        /// The synodic period, not the sidereal one (27.32 d): phase and the observed day-to-day
        /// moonrise delay are both governed by the moon's position <i>relative to the sun</i>.
        /// </remarks>
        public const float SynodicDays = 29.53059f;

        /// <summary>
        /// Solar declination, pinned to the equinox value. See the type remarks for why seasons are
        /// deliberately out of this model rather than merely unimplemented.
        /// </summary>
        public const float SolarDeclinationRadians = 0f;

        /// <summary>
        /// Phase offset placing a <b>full moon on the world's first night</b> (Minecraft parity).
        /// </summary>
        /// <remarks>
        /// Without it the cycle starts at a new moon, and a new moon is — correctly — beside the sun,
        /// so it is up by day and below the horizon all night. A fresh world would then have no visible
        /// moon for roughly ten nights, which reads as a bug however right the geometry is. Tick 0 is
        /// sunrise, so the first midnight is day 1.0; half a synodic month before that is the full moon.
        /// </remarks>
        public const float MoonPhaseEpochDays = SynodicDays * 0.5f - 1f;

        /// <summary>Day fraction of solar noon, the anchor the hour angle is measured from.</summary>
        private const float NOON_DAY_FRACTION = 0.5f;

        /// <summary>
        /// The sun's direction from the observer.
        /// </summary>
        /// <param name="dayFraction">Position in the day, <c>[0,1)</c> — 0 = midnight, 0.5 = noon.</param>
        /// <param name="latitudeDegrees">Observer latitude, <c>[-90, 90]</c>; positive is north.</param>
        /// <returns>A unit direction in Unity render space (+x east, +y up, +z north).</returns>
        public static Vector3 SunDirection(float dayFraction, float latitudeDegrees)
        {
            return DirectionAtHourAngle(HourAngle(dayFraction), latitudeDegrees * Mathf.Deg2Rad);
        }

        /// <summary>
        /// The moon's direction from the observer, trailing the sun by the current elongation.
        /// </summary>
        /// <param name="continuousDays">Elapsed world days as a continuous value; its fractional part is the day fraction.</param>
        /// <param name="latitudeDegrees">Observer latitude, <c>[-90, 90]</c>; positive is north.</param>
        /// <returns>A unit direction in Unity render space (+x east, +y up, +z north).</returns>
        public static Vector3 MoonDirection(double continuousDays, float latitudeDegrees)
        {
            float hourAngle = HourAngle(DayFractionOf(continuousDays)) - ElongationRadians(continuousDays);
            return DirectionAtHourAngle(hourAngle, latitudeDegrees * Mathf.Deg2Rad);
        }

        /// <summary>
        /// The lit fraction of the moon's disc.
        /// </summary>
        /// <param name="continuousDays">Elapsed world days as a continuous value.</param>
        /// <returns><c>0</c> at new moon, <c>1</c> at full moon.</returns>
        public static float MoonIlluminatedFraction(double continuousDays)
        {
            return (1f - Mathf.Cos(ElongationRadians(continuousDays))) * 0.5f;
        }

        /// <summary>
        /// Angle between the moon and the sun as seen from the observer — the quantity that drives both
        /// the moon's lag and its phase.
        /// </summary>
        /// <param name="continuousDays">Elapsed world days as a continuous value.</param>
        /// <returns>The elongation in radians, <c>[0, 2π)</c>; 0 = new moon, π = full moon.</returns>
        public static float ElongationRadians(double continuousDays)
        {
            double cycles = (continuousDays + MoonPhaseEpochDays) / SynodicDays;
            return (float)(cycles - System.Math.Floor(cycles)) * 2f * Mathf.PI;
        }

        /// <summary>
        /// Rotation of the celestial sphere for the current time — the star field's orientation.
        /// </summary>
        /// <param name="dayFraction">Position in the day, <c>[0,1)</c> — 0 = midnight, 0.5 = noon.</param>
        /// <param name="latitudeDegrees">Observer latitude, <c>[-90, 90]</c>; positive is north.</param>
        /// <returns>A rotation about the celestial pole; applying it to a fixed direction sweeps a real star arc.</returns>
        /// <remarks>
        /// The sun rides this same rotation (<see cref="SunDirection"/> equals this applied to the
        /// noon direction), so the stars and the sun share one sphere instead of being two effects
        /// that merely look similar. One simplification is worth stating: the sphere turns once per
        /// <i>solar</i> day rather than per sidereal day, so the star field does not slowly precess
        /// against the calendar over a long-lived world.
        /// </remarks>
        public static Quaternion SkyRotation(float dayFraction, float latitudeDegrees)
        {
            return Quaternion.AngleAxis(HourAngle(dayFraction) * Mathf.Rad2Deg, PoleAxis(latitudeDegrees));
        }

        /// <summary>
        /// Direction of the north celestial pole — the axis the whole sky turns about.
        /// </summary>
        /// <param name="latitudeDegrees">Observer latitude, <c>[-90, 90]</c>; positive is north.</param>
        /// <returns>A unit direction; its altitude above the horizon equals the latitude.</returns>
        public static Vector3 PoleAxis(float latitudeDegrees)
        {
            float latitude = latitudeDegrees * Mathf.Deg2Rad;
            return new Vector3(0f, Mathf.Sin(latitude), Mathf.Cos(latitude));
        }

        /// <summary>Fractional part of a continuous day count, as the day fraction.</summary>
        /// <param name="continuousDays">Elapsed world days as a continuous value.</param>
        /// <returns>The day fraction in <c>[0,1)</c>.</returns>
        public static float DayFractionOf(double continuousDays)
        {
            return (float)(continuousDays - System.Math.Floor(continuousDays));
        }

        /// <summary>Hour angle for a day fraction: zero at solar noon, advancing westward.</summary>
        /// <param name="dayFraction">Position in the day, <c>[0,1)</c>.</param>
        /// <returns>The hour angle in radians.</returns>
        private static float HourAngle(float dayFraction)
        {
            return (dayFraction - NOON_DAY_FRACTION) * 2f * Mathf.PI;
        }

        /// <summary>
        /// Converts an hour angle to a horizon-space direction — the one place the celestial geometry
        /// lives, shared by the sun, the moon, and (via <see cref="SkyRotation"/>) the stars.
        /// </summary>
        /// <param name="hourAngle">Hour angle in radians; 0 puts the body on the meridian.</param>
        /// <param name="latitude">Observer latitude in radians.</param>
        /// <returns>A unit direction in Unity render space (+x east, +y up, +z north).</returns>
        /// <remarks>
        /// The standard equatorial-to-horizon transform with declination zero, which collapses it to
        /// four trig calls. Unit length is structural (the three components are the direction cosines
        /// of a point on the celestial equator), so it holds at the poles without a guard.
        /// </remarks>
        private static Vector3 DirectionAtHourAngle(float hourAngle, float latitude)
        {
            float sinHour = Mathf.Sin(hourAngle);
            float cosHour = Mathf.Cos(hourAngle);
            float sinLatitude = Mathf.Sin(latitude);
            float cosLatitude = Mathf.Cos(latitude);

            return new Vector3(-sinHour, cosLatitude * cosHour, -sinLatitude * cosHour);
        }
    }
}
