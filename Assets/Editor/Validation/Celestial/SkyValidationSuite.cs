using System.Collections.Generic;
using Data.WorldTypes;
using Editor.Dev;
using Editor.Validation.Framework;
using Sky;
using UnityEditor;
using UnityEngine;

namespace Editor.Validation.Celestial
{
    /// <summary>
    /// Truth-table suite for <see cref="CelestialMath"/> (RF-2) — the sun's arc, the moon's lagged
    /// arc and phase, and the rotation of the star field. Pure functions over explicit inputs, so no
    /// world, scene, camera, or real clock is involved.
    /// <para>
    /// All scenarios are <b>baselines</b> (must stay green). The load-bearing ones are B6 (the disc
    /// and RF-1's light curve agree about whether the sun is up, which is what stops the sky and the
    /// terrain lighting drifting apart) and B9 (phase and position are one model — the property that
    /// makes full-moon-at-midnight structural rather than authored).
    /// </para>
    /// <para>
    /// <b>Assertions use independent oracles, not the formula under test.</b> Peak altitude is checked
    /// against hand-computed <c>90° − |φ|</c>; the moon's daily lag and its midnight peak are found by
    /// <i>numerically</i> scanning altitude for its maximum rather than by re-deriving the meridian
    /// crossing; illumination is cross-checked against the sun–moon dot product. A test that recomputed
    /// the model's own expression would be a tautology.
    /// </para>
    /// <para>
    /// <b>Prove-red</b> — each mutation was applied, run, and reverted; these are observed results,
    /// not predictions:
    /// <list type="bullet">
    /// <item>Swapping <c>sin</c>/<c>cos</c> of the latitude in <c>DirectionAtHourAngle</c> reds B4, B5,
    /// B6 and B11. It leaves B2 green (the horizon crossings do not depend on latitude) and is
    /// completely invisible at latitude 45°, where the two are equal — which is why the geometric
    /// scenarios sweep seven latitudes rather than one.</item>
    /// <item>Dropping the negation on the east component reds B3 and B11 while every altitude
    /// assertion stays green: a mirrored sky is exactly the defect B3 exists to catch.</item>
    /// <item>Replacing <see cref="CelestialMath.SynodicDays"/> with the sidereal month (27.32) reds
    /// B7, B8 and B10 — but <b>only after</b> <see cref="EXPECTED_SYNODIC_DAYS"/> was introduced. When
    /// those scenarios derived their expectations from the constant under test, this mutation was
    /// invisible and the suite stayed fully green.</item>
    /// <item>Expressing phase as <c>E / 2π</c> instead of <c>(1 − cos E)/2</c> reds B8 and B9.</item>
    /// <item>Giving <see cref="CelestialMath.MoonIlluminatedFraction"/> a static field that blends each
    /// result with the previous call's reds B12 and B9 — confirming B12 still detects hidden state
    /// after it was rewritten from a self-comparison to a reverse-order re-evaluation.</item>
    /// <item>Zeroing <see cref="CelestialMath.MoonPhaseEpochDays"/> reds B8, B10 and B13, the last
    /// reporting the observed defect verbatim: the first night's moon at altitude −0.69. This is the
    /// in-game bug B13 was written from, so the mutation is the bug itself.</item>
    /// <item>Raising <see cref="AtmosphericFog.FogEndFraction"/> past 1 reds B14 — fog would then finish
    /// beyond the loaded radius, which is exactly the case where the chunk boundary becomes visible.</item>
    /// <item>Dropping <see cref="AtmosphericFog.DefaultFogCurvePower"/> to 1 (a linear falloff) reds
    /// B14's shape assertion at exactly 50% midpoint fog — the even ramp that paints a visible gradient
    /// across a mountain, which is the in-game artifact the curve was added to remove.</item>
    /// <item>Setting <see cref="AtmosphericFog.LightFogCurveMultiplier"/> to 1 reds B15's "Light is
    /// thinner than Full" — the level would exist in the menu but change nothing.</item>
    /// <item>Ignoring <see cref="FogStyle.Off"/> in <c>ComputeFogRange</c> reds B15's two Off
    /// assertions, the second reporting fog at 100% where the player asked for none.</item>
    /// <item>Flooring <c>EvaluateFogFactor</c>'s interpolant at 0.1 reds B15's "clear at the fog start
    /// distance" (and B14's) — the near haze that would wash out everything in front of the player.
    /// It reds on <b>Full</b>, at 0.0010 against Light's 0.0000: while that assertion evaluated Light
    /// alone, which the steeper curve keeps under the epsilon, this mutation passed it green.</item>
    /// </list>
    /// </para>
    /// <para>
    /// Deliberately <b>not</b> claimed: nothing here observes the skybox shader. This suite proves the
    /// sun goes where the model says it does; that it <i>looks</i> right is capture-verified only —
    /// the same limitation RF-1 §10's subtractive sky term carries.
    /// </para>
    /// </summary>
    public static class SkyValidationSuite
    {
        /// <summary>Latitudes every geometric scenario sweeps, including both poles and the equator.</summary>
        private static readonly float[] s_latitudes = { 0f, 23.44f, 45f, -45f, 89.9f, 90f, -90f };

        /// <summary>Samples taken across one day by the sweep scenarios.</summary>
        private const int DAY_SAMPLES = 360;

        /// <summary>Samples per day used when scanning for the moon's peak altitude.</summary>
        /// <remarks>Finer than <see cref="DAY_SAMPLES"/>: the effect being measured is 1/29.53 of a day.</remarks>
        private const int PEAK_SAMPLES_PER_DAY = 4000;

        /// <summary>Days the moon-lag scenario simulates.</summary>
        private const int LAG_DAYS = 60;

        /// <summary>Lunations the moon-phase couplings are checked over.</summary>
        private const int PHASE_MONTHS = 5;

        /// <summary>
        /// The synodic month the engine claims parity with, as a <b>literal</b> — deliberately not read
        /// from <see cref="CelestialMath.SynodicDays"/>.
        /// </summary>
        /// <remarks>
        /// Deriving the expectation from the constant under test makes every phase assertion a
        /// tautology: swapping in the sidereal month (27.32) moves the model and its expected value
        /// together and the whole suite stays green. Verified by running that mutation.
        /// </remarks>
        private const double EXPECTED_SYNODIC_DAYS = 29.53059;

        /// <summary>
        /// The world's first midnight, as a continuous day count — tick 0 is sunrise, so midnight of
        /// the first night is day 1.0. B13 pins a full moon here; every other phase scenario counts
        /// cycles from this anchor rather than from the model's own epoch constant.
        /// </summary>
        private const double FIRST_NIGHT_DAYS = 1.0;

        /// <summary>
        /// How close to its greatest possible altitude the moon must ride for "it peaks at midnight"
        /// to hold. Measured values sit at 0.995–1.000, so this discriminates without being brittle.
        /// </summary>
        private const float PEAK_ALTITUDE_RATIO = 0.99f;

        /// <summary>Tolerance for a unit-vector component comparison.</summary>
        private const float DIRECTION_EPSILON = 1e-4f;

        /// <summary>Tolerance in degrees for an angle comparison.</summary>
        private const float DEGREES_EPSILON = 0.05f;

        /// <summary>Tolerance for a normalized phase/illumination comparison.</summary>
        private const float PHASE_EPSILON = 1e-4f;

        /// <summary>Latitude past which "due south at noon" stops being meaningful.</summary>
        private const float AZIMUTH_MEANINGFUL_LATITUDE = 89f;

        /// <summary>
        /// Most fog allowed halfway through its range. A linear falloff sits at 0.5 here, so this is the
        /// threshold that distinguishes a back-loaded curve from an even ramp.
        /// </summary>
        private const float MAX_MIDPOINT_FOG = 0.2f;

        /// <summary>Tolerance for a normalized fog-factor comparison.</summary>
        private const float FOG_EPSILON = 1e-4f;

        /// <summary>Runs every scenario and prints a categorized summary via the shared runner.</summary>
        [MenuItem("Minecraft Clone/Dev/Validate Sky", priority = DevMenuPriority.Validation)]
        public static void RunAll() => Execute();

        /// <summary>
        /// Builds and runs the celestial scenarios, returning the categorized result (the headless/CI entry point).
        /// </summary>
        /// <param name="logToConsole">When false, runs silently and only returns the result (for headless/CI use).</param>
        /// <param name="showProgress">When false, suppresses this suite's own progress bar (the aggregate runner drives one).</param>
        /// <returns>The categorized, timed result of the run.</returns>
        public static ValidationRunResult Execute(bool logToConsole = true, bool showProgress = true)
        {
            List<Scenario> scenarios = new List<Scenario>
            {
                new Scenario("B1 Every direction is unit length and finite, at every latitude", RunB1UnitAndFinite),
                new Scenario("B2 The sun is on the horizon at day fractions 0.25 and 0.75", RunB2HorizonCrossings),
                new Scenario("B3 The sun rises due east and sets due west", RunB3EastWest),
                new Scenario("B4 Noon altitude is 90 - |latitude|, and the sun is equatorward at noon", RunB4NoonAltitude),
                new Scenario("B5 Altitude rises monotonically to noon and falls monotonically after", RunB5Monotonic),
                new Scenario("B6 The disc agrees with RF-1's SunElevation about whether the sun is up", RunB6ClockAgreement),
                new Scenario("B7 Moonrise slips one synodic fraction of a day, per day", RunB7MoonLag),
                new Scenario("B8 Phase completes exactly one cycle per synodic month", RunB8PhaseCycle),
                new Scenario("B9 Illumination equals the sun-moon elongation (one model, not two)", RunB9PhasePositionConsistency),
                new Scenario("B10 The full moon peaks at midnight; the new moon peaks at noon", RunB10FullMoonMidnight),
                new Scenario("B11 The sky rotation is rigid, daily, and carries the sun", RunB11SkyRotation),
                new Scenario("B12 The model is pure - re-evaluating in reverse order reproduces every value", RunB12Determinism),
                new Scenario("B13 A new world's first night has a full moon, visible above the horizon", RunB13FirstNightFullMoon),
                new Scenario("B14 Distance fog hides the chunk boundary and follows the view distance", RunB14FogRange),
                new Scenario("B15 Every fog level still conceals the boundary; Off disables fog entirely", RunB15FogStyles),
            };
            return ValidationSuiteRunner.Execute("Sky & Celestial", scenarios, KnownBugChannel.Unimplemented, logToConsole, showProgress);
        }

        /// <summary>Logs a single assertion as PASS/FAIL and returns its result for AND-chaining.</summary>
        /// <param name="label">Human-readable assertion description.</param>
        /// <param name="condition">The asserted condition.</param>
        /// <returns><paramref name="condition"/>.</returns>
        private static bool Check(string label, bool condition)
        {
            if (condition) Debug.Log($"  [PASS] {label}");
            else Debug.LogError($"  [FAIL] {label}");
            return condition;
        }

        /// <summary>Altitude of a direction above the horizon, in degrees.</summary>
        /// <param name="direction">A unit direction in Unity render space.</param>
        /// <returns>Altitude in <c>[-90, 90]</c>.</returns>
        private static float AltitudeDegrees(Vector3 direction) => Mathf.Asin(Mathf.Clamp(direction.y, -1f, 1f)) * Mathf.Rad2Deg;

        /// <summary>True when a vector is unit length and free of NaN/infinity.</summary>
        /// <param name="v">The vector to test.</param>
        /// <returns>True when the vector is a well-formed unit direction.</returns>
        private static bool IsFiniteUnit(Vector3 v)
        {
            if (float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z)) return false;
            if (float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z)) return false;
            return Mathf.Abs(v.magnitude - 1f) <= DIRECTION_EPSILON;
        }

        /// <summary>
        /// Finds every time the moon reaches a local maximum altitude across a continuous span.
        /// </summary>
        /// <param name="spanDays">Length of the span to scan, in days from zero.</param>
        /// <param name="latitudeDegrees">Observer latitude.</param>
        /// <returns>The peak times in days, ascending.</returns>
        /// <remarks>
        /// Scanning one continuous span rather than taking a per-day maximum is load-bearing: the moon
        /// peaks every ~1.035 days, so roughly one calendar day per month contains no peak at all and a
        /// per-day <c>argmax</c> would return a boundary sample instead of a real crossing.
        /// </remarks>
        private static List<double> FindMoonPeakTimes(double spanDays, float latitudeDegrees)
        {
            List<double> peaks = new List<double>();
            int steps = (int)(spanDays * PEAK_SAMPLES_PER_DAY);
            double step = spanDays / steps;

            float previous = CelestialMath.MoonDirection(0.0, latitudeDegrees).y;
            float current = CelestialMath.MoonDirection(step, latitudeDegrees).y;

            for (int i = 2; i <= steps; i++)
            {
                float next = CelestialMath.MoonDirection(i * step, latitudeDegrees).y;
                if (current > previous && current >= next) peaks.Add((i - 1) * step);
                previous = current;
                current = next;
            }

            return peaks;
        }

        /// <summary>Greatest altitude sine any body on the celestial equator can reach at a latitude.</summary>
        /// <param name="latitudeDegrees">Observer latitude.</param>
        /// <returns><c>sin(90° − |latitude|)</c>.</returns>
        private static float MaxAltitudeSine(float latitudeDegrees) => Mathf.Cos(latitudeDegrees * Mathf.Deg2Rad);

        /// <summary>B1 — no latitude, including the poles, produces a malformed direction.</summary>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunB1UnitAndFinite()
        {
            bool sunOk = true;
            bool moonOk = true;
            bool poleOk = true;

            foreach (float latitude in s_latitudes)
            {
                if (!IsFiniteUnit(CelestialMath.PoleAxis(latitude))) poleOk = false;

                for (int i = 0; i < DAY_SAMPLES; i++)
                {
                    float dayFraction = i / (float)DAY_SAMPLES;
                    if (!IsFiniteUnit(CelestialMath.SunDirection(dayFraction, latitude))) sunOk = false;
                    if (!IsFiniteUnit(CelestialMath.MoonDirection(dayFraction + i, latitude))) moonOk = false;
                }
            }

            bool ok = Check($"sun directions are finite unit vectors across {s_latitudes.Length} latitudes", sunOk);
            ok &= Check("moon directions are finite unit vectors", moonOk);
            ok &= Check("the celestial pole axis is a finite unit vector", poleOk);
            return ok;
        }

        /// <summary>B2 — the equinox model puts sunrise and sunset at the quarter points, whatever the latitude.</summary>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunB2HorizonCrossings()
        {
            bool ok = true;

            foreach (float latitude in s_latitudes)
            {
                float dawn = CelestialMath.SunDirection(0.25f, latitude).y;
                float dusk = CelestialMath.SunDirection(0.75f, latitude).y;
                ok &= Check($"lat {latitude}: sun is on the horizon at dawn, altitude {dawn:F6}",
                    Mathf.Abs(dawn) <= DIRECTION_EPSILON);
                ok &= Check($"lat {latitude}: sun is on the horizon at dusk, altitude {dusk:F6}",
                    Mathf.Abs(dusk) <= DIRECTION_EPSILON);
            }

            return ok;
        }

        /// <summary>
        /// B3 — the compass direction of sunrise and sunset. This is the assertion that catches a
        /// mirrored sky, which every altitude-based check would pass.
        /// </summary>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunB3EastWest()
        {
            Vector3 east = Vector3.right;
            bool ok = true;

            foreach (float latitude in s_latitudes)
            {
                float dawnDot = Vector3.Dot(CelestialMath.SunDirection(0.25f, latitude), east);
                float duskDot = Vector3.Dot(CelestialMath.SunDirection(0.75f, latitude), east);
                ok &= Check($"lat {latitude}: the sun rises due east, dot {dawnDot:F6}",
                    Mathf.Abs(dawnDot - 1f) <= DIRECTION_EPSILON);
                ok &= Check($"lat {latitude}: the sun sets due west, dot {duskDot:F6}",
                    Mathf.Abs(duskDot + 1f) <= DIRECTION_EPSILON);
            }

            return ok;
        }

        /// <summary>
        /// B4 — noon altitude against the hand-computed <c>90° − |φ|</c>, plus the hemisphere check that
        /// the midday sun sits toward the equator.
        /// </summary>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunB4NoonAltitude()
        {
            bool ok = true;

            foreach (float latitude in s_latitudes)
            {
                Vector3 noon = CelestialMath.SunDirection(0.5f, latitude);
                float expected = 90f - Mathf.Abs(latitude);
                float actual = AltitudeDegrees(noon);
                ok &= Check($"lat {latitude}: noon altitude is {expected:F3}, got {actual:F3}",
                    Mathf.Abs(actual - expected) <= DEGREES_EPSILON);

                if (Mathf.Abs(latitude) >= AZIMUTH_MEANINGFUL_LATITUDE) continue;

                // Northern latitudes see the midday sun to the south (-z) and southern ones to the north.
                float northComponent = noon.z;
                bool equatorward = latitude > 0f ? northComponent < 0f : northComponent > 0f;
                if (Mathf.Approximately(latitude, 0f)) equatorward = Mathf.Abs(northComponent) <= DIRECTION_EPSILON;
                ok &= Check($"lat {latitude}: the midday sun sits toward the equator, north component {northComponent:F4}",
                    equatorward);
            }

            return ok;
        }

        /// <summary>B5 — the arc has exactly one maximum: it climbs to noon and descends after it.</summary>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunB5Monotonic()
        {
            bool rising = true;
            bool falling = true;

            foreach (float latitude in s_latitudes)
            {
                // The poles sit at a degenerate altitude of exactly 0 all day; monotonicity is vacuous there.
                if (Mathf.Abs(latitude) >= AZIMUTH_MEANINGFUL_LATITUDE) continue;

                float previousRising = float.NegativeInfinity;
                float previousFalling = float.PositiveInfinity;

                // Named so the integer division happens once, deliberately, rather than inside a
                // float-typed expression where a non-multiple-of-4 sample count would silently truncate.
                const int halfArcSamples = DAY_SAMPLES / 4;

                for (int i = 0; i <= halfArcSamples; i++)
                {
                    float t = i / (float)halfArcSamples;
                    float morning = CelestialMath.SunDirection(Mathf.Lerp(0.25f, 0.5f, t), latitude).y;
                    if (morning < previousRising - DIRECTION_EPSILON) rising = false;
                    previousRising = morning;

                    float afternoon = CelestialMath.SunDirection(Mathf.Lerp(0.5f, 0.75f, t), latitude).y;
                    if (afternoon > previousFalling + DIRECTION_EPSILON) falling = false;
                    previousFalling = afternoon;
                }
            }

            bool ok = Check("altitude rises monotonically from dawn to noon", rising);
            ok &= Check("altitude falls monotonically from noon to dusk", falling);
            return ok;
        }

        /// <summary>
        /// B6 — the composition guard against RF-1. The rendered disc and the light curve read the same
        /// clock, so they must never disagree about whether the sun is above the horizon; a sky that
        /// brightened while the disc was still down is exactly the defect this catches.
        /// </summary>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunB6ClockAgreement()
        {
            TimeOfDaySettings settings = ScriptableObject.CreateInstance<TimeOfDaySettings>();
            try
            {
                WorldTimeManager clock = new WorldTimeManager(settings);
                bool signsAgree = true;
                bool valuesAgree = true;

                for (int i = 0; i < DAY_SAMPLES; i++)
                {
                    float dayFraction = i / (float)DAY_SAMPLES;
                    int dayTicks = Mathf.RoundToInt(dayFraction * WorldTimeManager.TicksPerDay) - WorldTimeManager.SunriseTickOffset;
                    clock.SetDayTime(dayTicks);

                    float discAltitude = CelestialMath.SunDirection(clock.DayFraction, 0f).y;
                    float clockElevation = clock.SunElevation;

                    // Stated as "is the sun up", which is the claim being made. Comparing Mathf.Sign
                    // values instead would rest on float equality and would additionally treat an
                    // exactly-zero altitude as positive, since Unity's Mathf.Sign(0) returns +1.
                    bool discSaysUp = discAltitude > 0f;
                    bool clockSaysUp = clockElevation > 0f;
                    if (discSaysUp != clockSaysUp && Mathf.Abs(discAltitude) > DIRECTION_EPSILON) signsAgree = false;

                    // At the equator the disc's altitude sine IS the clock's flat elevation, so the two
                    // models coincide exactly there. Away from the equator only the sign is shared.
                    if (Mathf.Abs(discAltitude - clockElevation) > DIRECTION_EPSILON) valuesAgree = false;
                }

                bool ok = Check("the disc and SunElevation agree on whether the sun is up, all day", signsAgree);
                ok &= Check("at the equator the disc's altitude equals RF-1's SunElevation exactly", valuesAgree);
                return ok;
            }
            finally
            {
                if (settings != null) Object.DestroyImmediate(settings);
            }
        }

        /// <summary>
        /// B7 — the moon's daily slip, measured by numerically locating its peak on successive days
        /// rather than by re-deriving the meridian crossing.
        /// </summary>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunB7MoonLag()
        {
            const float latitude = 45f;

            // The moon's day is longer than the sun's by exactly the fraction of its orbit it covers
            // meanwhile, which is the same statement as "moonrise slips 1/29.53 of a day per day".
            const double expectedLunarDay = 1.0 / (1.0 - 1.0 / EXPECTED_SYNODIC_DAYS);
            const double tolerance = 3.0 / PEAK_SAMPLES_PER_DAY;

            List<double> peaks = FindMoonPeakTimes(LAG_DAYS, latitude);
            bool enoughPeaks = Check($"the moon peaks about once a day over {LAG_DAYS} days, found {peaks.Count}",
                peaks.Count >= LAG_DAYS - 3);
            if (!enoughPeaks) return false;

            bool spacingCorrect = true;
            double worstError = 0.0;

            for (int i = 1; i < peaks.Count; i++)
            {
                double gap = peaks[i] - peaks[i - 1];
                double error = System.Math.Abs(gap - expectedLunarDay);
                if (error > worstError) worstError = error;
                if (error > tolerance) spacingCorrect = false;
            }

            return enoughPeaks & Check(
                $"successive moon peaks are {expectedLunarDay:F6} days apart — one solar day plus the " +
                $"synodic slip (worst error {worstError:F6}, tolerance {tolerance:F6})", spacingCorrect);
        }

        /// <summary>B8 — the phase cycle's period and its endpoints.</summary>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunB8PhaseCycle()
        {
            // Cycles are counted from the first night (B13's anchor), not from the model's own epoch.
            const double newMoon = FIRST_NIGHT_DAYS + EXPECTED_SYNODIC_DAYS * 0.5;
            bool ok = Check($"half a month after the first full moon is a new moon, got {CelestialMath.MoonIlluminatedFraction(newMoon):F6}",
                CelestialMath.MoonIlluminatedFraction(newMoon) <= PHASE_EPSILON);

            const double nextFullMoon = FIRST_NIGHT_DAYS + EXPECTED_SYNODIC_DAYS;
            ok &= Check($"the cycle returns to full after one synodic month, got {CelestialMath.MoonIlluminatedFraction(nextFullMoon):F6}",
                Mathf.Abs(CelestialMath.MoonIlluminatedFraction(nextFullMoon) - 1f) <= PHASE_EPSILON);

            const double firstQuarter = FIRST_NIGHT_DAYS + EXPECTED_SYNODIC_DAYS * 0.25;
            ok &= Check($"a quarter month past full is half lit, got {CelestialMath.MoonIlluminatedFraction(firstQuarter):F6}",
                Mathf.Abs(CelestialMath.MoonIlluminatedFraction(firstQuarter) - 0.5f) <= PHASE_EPSILON);

            // Exactly one full moon per synodic month over many months: counts the maxima rather than
            // trusting a single sample, so a phase running at double rate cannot pass.
            int fullMoons = 0;
            const int samples = 20000;
            const double span = EXPECTED_SYNODIC_DAYS * PHASE_MONTHS;
            float previous = CelestialMath.MoonIlluminatedFraction(0.0);
            bool wasRising = true;

            for (int i = 1; i <= samples; i++)
            {
                float current = CelestialMath.MoonIlluminatedFraction(i / (double)samples * span);
                bool isRising = current > previous;
                if (wasRising && !isRising) fullMoons++;
                wasRising = isRising;
                previous = current;
            }

            ok &= Check($"exactly {PHASE_MONTHS} full moons in {PHASE_MONTHS} synodic months, counted {fullMoons}",
                fullMoons == PHASE_MONTHS);
            return ok;
        }

        /// <summary>
        /// B9 — the identity that proves phase and position come from one elongation: the lit fraction
        /// must equal the sun-moon separation computed from the two direction vectors alone.
        /// </summary>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunB9PhasePositionConsistency()
        {
            const float latitude = 45f;
            const int samples = 2000;
            bool consistent = true;
            float worstError = 0f;

            for (int i = 0; i < samples; i++)
            {
                double days = i / (double)samples * CelestialMath.SynodicDays * 3.0;
                Vector3 sun = CelestialMath.SunDirection(CelestialMath.DayFractionOf(days), latitude);
                Vector3 moon = CelestialMath.MoonDirection(days, latitude);

                // Both bodies ride the celestial equator, so their angular separation IS the elongation;
                // the lit fraction therefore falls straight out of the dot product.
                float fromGeometry = (1f - Vector3.Dot(sun, moon)) * 0.5f;
                float fromPhase = CelestialMath.MoonIlluminatedFraction(days);

                float error = Mathf.Abs(fromGeometry - fromPhase);
                if (error > worstError) worstError = error;
                if (error > DIRECTION_EPSILON) consistent = false;
            }

            return Check($"illumination equals the geometric sun-moon separation at every sample (worst error {worstError:F7})",
                consistent);
        }

        /// <summary>
        /// B10 — the couplings that make the model read as real: a full moon rides high at midnight and
        /// a new moon is up at noon. Both peaks are located numerically.
        /// </summary>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunB10FullMoonMidnight()
        {
            const float latitude = 45f;
            float maxSine = MaxAltitudeSine(latitude);
            bool fullOk = true;
            bool newOk = true;

            for (int month = 0; month < PHASE_MONTHS; month++)
            {
                // The midnight nearest this month's full moon: the hour the moonlight floor matters most.
                double fullMidnight = System.Math.Round(FIRST_NIGHT_DAYS + EXPECTED_SYNODIC_DAYS * month);
                float fullMoonAltitude = CelestialMath.MoonDirection(fullMidnight, latitude).y;
                float fullSunAltitude = CelestialMath.SunDirection(CelestialMath.DayFractionOf(fullMidnight), latitude).y;
                if (fullMoonAltitude < maxSine * PEAK_ALTITUDE_RATIO || fullSunAltitude >= 0f) fullOk = false;

                // The noon nearest this month's new moon: the moon is up, but washed out by the sun.
                double newNoon = System.Math.Floor(FIRST_NIGHT_DAYS + EXPECTED_SYNODIC_DAYS * (month + 0.5)) + 0.5;
                float newMoonAltitude = CelestialMath.MoonDirection(newNoon, latitude).y;
                float newSunAltitude = CelestialMath.SunDirection(CelestialMath.DayFractionOf(newNoon), latitude).y;
                if (newMoonAltitude < maxSine * PEAK_ALTITUDE_RATIO || newSunAltitude <= 0f) newOk = false;
            }

            bool ok = Check($"the full moon rides near its highest point at midnight, with the sun down, " +
                            $"in each of {PHASE_MONTHS} months", fullOk);
            ok &= Check($"the new moon rides near its highest point at noon, with the sun up, " +
                        $"in each of {PHASE_MONTHS} months", newOk);
            return ok;
        }

        /// <summary>
        /// B11 — the star field's rotation is a rigid motion with a one-day period, and it carries the
        /// sun: stars and sun share one celestial sphere rather than being two similar-looking effects.
        /// </summary>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunB11SkyRotation()
        {
            bool rigid = true;
            bool carriesSun = true;
            bool daily = true;

            Vector3 probeA = new Vector3(0.3f, 0.5f, -0.81f).normalized;
            Vector3 probeB = new Vector3(-0.7f, 0.2f, 0.68f).normalized;
            float referenceDot = Vector3.Dot(probeA, probeB);

            foreach (float latitude in s_latitudes)
            {
                for (int i = 0; i < DAY_SAMPLES; i++)
                {
                    float dayFraction = i / (float)DAY_SAMPLES;
                    Quaternion rotation = CelestialMath.SkyRotation(dayFraction, latitude);

                    Vector3 rotatedA = rotation * probeA;
                    Vector3 rotatedB = rotation * probeB;
                    if (!IsFiniteUnit(rotatedA)) rigid = false;
                    if (Mathf.Abs(Vector3.Dot(rotatedA, rotatedB) - referenceDot) > DIRECTION_EPSILON) rigid = false;

                    Vector3 carried = rotation * CelestialMath.SunDirection(0.5f, latitude);
                    Vector3 direct = CelestialMath.SunDirection(dayFraction, latitude);
                    if ((carried - direct).magnitude > DIRECTION_EPSILON) carriesSun = false;
                }

                if (Quaternion.Angle(CelestialMath.SkyRotation(0f, latitude), CelestialMath.SkyRotation(1f, latitude)) > DEGREES_EPSILON)
                    daily = false;
            }

            bool ok = Check("the sky rotation is rigid (preserves lengths and angles)", rigid);
            ok &= Check("the sun is the sky rotation applied to the noon direction", carriesSun);
            ok &= Check("the sky returns to its starting orientation after exactly one day", daily);
            return ok;
        }

        /// <summary>
        /// B13 — the phase epoch. Anchors the player-facing promise that a brand-new world's first
        /// night is lit by a full moon riding high, rather than by the new moon the raw cycle would
        /// otherwise start on (correct geometry, but it reads as a missing moon for ten nights).
        /// </summary>
        /// <returns>True when every assertion holds.</returns>
        /// <remarks>
        /// Deliberately expressed against the literal day 1.0 and an absolute illumination, never
        /// against <see cref="CelestialMath.MoonPhaseEpochDays"/> — this scenario is what pins that
        /// constant, so reading it here would be circular.
        /// </remarks>
        private static bool RunB13FirstNightFullMoon()
        {
            const float latitude = 45f;
            float illumination = CelestialMath.MoonIlluminatedFraction(FIRST_NIGHT_DAYS);
            Vector3 moon = CelestialMath.MoonDirection(FIRST_NIGHT_DAYS, latitude);
            Vector3 sun = CelestialMath.SunDirection(CelestialMath.DayFractionOf(FIRST_NIGHT_DAYS), latitude);

            bool ok = Check($"the first night's moon is full, illumination {illumination:F4}",
                illumination >= 1f - PHASE_EPSILON);
            ok &= Check($"the first night's moon is above the horizon, altitude {moon.y:F4}", moon.y > 0f);
            ok &= Check($"the sun is down at that moment, altitude {sun.y:F4}", sun.y < 0f);
            ok &= Check($"and it rides near its highest point, {moon.y / MaxAltitudeSine(latitude):F4} of maximum",
                moon.y >= MaxAltitudeSine(latitude) * PEAK_ALTITUDE_RATIO);
            return ok;
        }

        /// <summary>
        /// B14 — the fog range. Its whole job is to conceal the loaded-chunk boundary, so the load-bearing
        /// assertion is that fog reaches full opacity <i>inside</i> the loaded radius: if it finished at or
        /// beyond the edge, the player would watch terrain end against clear sky.
        /// </summary>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunB14FogRange()
        {
            const float farClip = 1000f;

            // A zero-width range is the shader's "fog off" signal, and it must be what a world with no
            // camera or no view distance produces — otherwise an uninitialized frame renders solid fog.
            const float defaultStart = AtmosphericFog.DefaultFogStartFraction;

            bool ok = Check("view distance 0 disables fog",
                Mathf.Approximately(AtmosphericFog.ComputeFogEnd(0, farClip), 0f));
            ok &= Check("a missing camera (far plane 0) disables fog",
                Mathf.Approximately(AtmosphericFog.ComputeFogEnd(8, 0f), 0f));

            bool insideRadius = true;
            bool ordered = true;
            bool monotonic = true;
            bool withinFarClip = true;

            foreach (float startFraction in new[] { 0f, 0.5f, defaultStart, 0.95f })
            {
                float previousEnd = -1f;
                for (int viewDistance = 1; viewDistance <= 32; viewDistance++)
                {
                    float loadedRadius = viewDistance * VoxelData.ChunkWidth;
                    float start = AtmosphericFog.ComputeFogStart(viewDistance, farClip, startFraction);
                    float end = AtmosphericFog.ComputeFogEnd(viewDistance, farClip);

                    if (end >= loadedRadius) insideRadius = false;
                    if (start >= end || start < 0f) ordered = false;
                    if (end <= previousEnd) monotonic = false;
                    if (end > farClip) withinFarClip = false;
                    previousEnd = end;
                }
            }

            ok &= Check("fog is fully opaque before the loaded radius ends, at every view distance", insideRadius);
            ok &= Check("fog starts nearer than it ends, at every view distance", ordered);
            ok &= Check("a larger view distance always pushes the fog further out", monotonic);
            ok &= Check("the fog end never exceeds the camera far plane", withinFarClip);

            // The clamp has to bind somewhere, or "never exceeds the far plane" is vacuously true.
            const float tightClip = 50f;
            ok &= Check($"a near far-plane clamps the fog end, got {AtmosphericFog.ComputeFogEnd(32, tightClip):F1}",
                Mathf.Approximately(AtmosphericFog.ComputeFogEnd(32, tightClip), tightClip));

            // A thinner authored fog must start further out — the knob has to actually do something.
            float thinStart = AtmosphericFog.ComputeFogStart(10, farClip, 0.9f);
            float thickStart = AtmosphericFog.ComputeFogStart(10, farClip, 0.3f);
            ok &= Check($"a higher start fraction pushes fog further out ({thickStart:F1} -> {thinStart:F1})",
                thinStart > thickStart);

            ok &= CheckFogCurveShape();
            return ok;
        }

        /// <summary>
        /// The fog falloff must be <b>back-loaded</b>: soft near the player and thickening with distance.
        /// A linear ramp spreads the change evenly, which paints a visible gradient across anything large
        /// enough to span the range — the mountain artifact this curve exists to remove.
        /// </summary>
        /// <returns>True when every assertion holds.</returns>
        private static bool CheckFogCurveShape()
        {
            const float farClip = 1000f;
            Vector4 range = AtmosphericFog.ComputeFogRange(10, farClip,
                AtmosphericFog.DefaultFogStartFraction, AtmosphericFog.DefaultFogCurvePower, FogStyle.Full);

            float start = range.x;
            float end = range.y;
            float midpoint = (start + end) * 0.5f;

            bool ok = Check($"fog is clear at its start distance ({start:F1})",
                AtmosphericFog.EvaluateFogFactor(start, range) <= FOG_EPSILON);
            ok &= Check($"fog is opaque at its end distance ({end:F1})",
                AtmosphericFog.EvaluateFogFactor(end, range) >= 1f - FOG_EPSILON);

            // The shape assertion. Linear would sit at 0.5 here, so this is what a curve buys.
            float atMidpoint = AtmosphericFog.EvaluateFogFactor(midpoint, range);
            ok &= Check($"fog is still mostly clear halfway through its range, got {atMidpoint:P1} (linear would be 50%)",
                atMidpoint <= MAX_MIDPOINT_FOG);

            bool monotonic = true;
            float previous = -1f;
            for (int i = 0; i <= 200; i++)
            {
                float factor = AtmosphericFog.EvaluateFogFactor(Mathf.Lerp(0f, end * 1.2f, i / 200f), range);
                if (factor < previous - FOG_EPSILON) monotonic = false;
                previous = factor;
            }

            ok &= Check("fog never thins out as distance grows", monotonic);
            ok &= Check("beyond the end distance fog stays fully opaque",
                AtmosphericFog.EvaluateFogFactor(end * 3f, range) >= 1f - FOG_EPSILON);
            return ok;
        }

        /// <summary>
        /// B15 — the graphics setting's fog levels. The load-bearing assertion is that <b>every enabled
        /// level still reaches full opacity at the fog end</b>: a level that merely dimmed the fog would
        /// leave terrain visibly ending against open sky, which is the artifact fog exists to prevent.
        /// </summary>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunB15FogStyles()
        {
            const float farClip = 1000f;
            const int viewDistance = 10;
            const float start = AtmosphericFog.DefaultFogStartFraction;
            const float power = AtmosphericFog.DefaultFogCurvePower;

            Vector4 off = AtmosphericFog.ComputeFogRange(viewDistance, farClip, start, power, FogStyle.Off);
            Vector4 light = AtmosphericFog.ComputeFogRange(viewDistance, farClip, start, power, FogStyle.Light);
            Vector4 full = AtmosphericFog.ComputeFogRange(viewDistance, farClip, start, power, FogStyle.Full);

            bool ok = Check("Off produces a zero-width range, the shader's disable path",
                Mathf.Approximately(off.y - off.x, 0f));
            ok &= Check($"Off renders no fog even far out, got {AtmosphericFog.EvaluateFogFactor(1e4f, off):F3}",
                AtmosphericFog.EvaluateFogFactor(1e4f, off) <= FOG_EPSILON);

            // The whole point of shaping the curve rather than scaling opacity.
            ok &= Check($"Light still fully conceals the boundary, got {AtmosphericFog.EvaluateFogFactor(light.y, light):P1}",
                AtmosphericFog.EvaluateFogFactor(light.y, light) >= 1f - FOG_EPSILON);
            ok &= Check($"Full still fully conceals the boundary, got {AtmosphericFog.EvaluateFogFactor(full.y, full):P1}",
                AtmosphericFog.EvaluateFogFactor(full.y, full) >= 1f - FOG_EPSILON);

            ok &= Check("Light and Full share the same fog distances — only the curve differs",
                Mathf.Approximately(light.x, full.x) && Mathf.Approximately(light.y, full.y));

            // Light must actually be lighter everywhere short of the boundary, or the level is cosmetic.
            bool lighterThroughout = true;
            for (int i = 1; i < 40; i++)
            {
                float distance = Mathf.Lerp(full.x, full.y, i / 40f);
                float lightFog = AtmosphericFog.EvaluateFogFactor(distance, light);
                float fullFog = AtmosphericFog.EvaluateFogFactor(distance, full);
                if (lightFog >= fullFog) lighterThroughout = false;
            }

            // Each level is checked against ITS OWN start distance. The two ranges are asserted equal
            // above, so this reads as one distance — but a regression that moved them apart must red
            // here rather than leave one level unchecked.
            float lightAtStart = AtmosphericFog.EvaluateFogFactor(light.x, light);
            float fullAtStart = AtmosphericFog.EvaluateFogFactor(full.x, full);

            ok &= Check("Light is thinner than Full at every distance short of the boundary", lighterThroughout);
            ok &= Check($"both levels are still clear at the fog start distance " +
                        $"(Light {lightAtStart:F4}, Full {fullAtStart:F4})",
                lightAtStart <= FOG_EPSILON && fullAtStart <= FOG_EPSILON);
            return ok;
        }

        /// <summary>B12 — no hidden accumulated state: revisiting the same instants reproduces every value.</summary>
        /// <returns>True when every assertion holds.</returns>
        /// <remarks>
        /// The instants are evaluated forwards, then re-evaluated in <b>reverse</b> order. Comparing a
        /// call against itself (<c>f(x) != f(x)</c>) would not test anything the compiler or JIT is
        /// obliged to preserve — it may fold to a constant <c>false</c> — whereas visiting the same
        /// inputs in a different order is a genuinely different execution, so any caching or
        /// accumulation inside the model surfaces as a mismatch.
        /// </remarks>
        private static bool RunB12Determinism()
        {
            const float latitude = 45f;
            // Irregular, non-repeating spacing so the sample set spans many days and phases.
            const double instantStride = 7.3;
            const double instantOffset = 0.137;

            double[] instants = new double[DAY_SAMPLES];
            Vector3[] sunForward = new Vector3[DAY_SAMPLES];
            Vector3[] moonForward = new Vector3[DAY_SAMPLES];
            float[] phaseForward = new float[DAY_SAMPLES];

            for (int i = 0; i < DAY_SAMPLES; i++)
            {
                instants[i] = i * instantStride + instantOffset;
                sunForward[i] = CelestialMath.SunDirection(CelestialMath.DayFractionOf(instants[i]), latitude);
                moonForward[i] = CelestialMath.MoonDirection(instants[i], latitude);
                phaseForward[i] = CelestialMath.MoonIlluminatedFraction(instants[i]);
            }

            bool identical = true;

            for (int i = DAY_SAMPLES - 1; i >= 0; i--)
            {
                // Vector3's == is tolerance-based, so it would accept a drifting value; Equals compares
                // the components exactly, which is what "no hidden state" actually requires.
                if (!CelestialMath.SunDirection(CelestialMath.DayFractionOf(instants[i]), latitude).Equals(sunForward[i])) identical = false;
                if (!CelestialMath.MoonDirection(instants[i], latitude).Equals(moonForward[i])) identical = false;
                if (!CelestialMath.MoonIlluminatedFraction(instants[i]).Equals(phaseForward[i])) identical = false;
            }

            return Check("re-evaluating the same instants in reverse order reproduces every value exactly", identical);
        }
    }
}
