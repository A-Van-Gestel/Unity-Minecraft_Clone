using System.Collections.Generic;
using Data.WorldTypes;
using Editor.Validation.Framework;
using Jobs.BurstData;
using UnityEditor;
using UnityEngine;

namespace Editor.Validation.WorldClock
{
    /// <summary>
    /// Truth-table suite for <see cref="WorldTimeManager"/> (RF-1) — the day/night clock's tick
    /// arithmetic, its derived render and gameplay quantities, and the freeze state. The clock is a
    /// pure managed object over explicit deltas, so no world, scene, or real clock is involved.
    /// <para>
    /// All scenarios are <b>baselines</b> (must stay green). The load-bearing ones are B5 (the
    /// moonlight floor holds however the curve is authored, which is what gameplay light thresholds
    /// rest on) and B9 (the named <c>/time</c> targets stay visually distinct).
    /// </para>
    /// <para>
    /// <b>Prove-red</b> (each mutation applied, observed, and reverted): truncating <c>Tick</c> to
    /// <c>TimeTicks += (long)(delta * ticksPerSecond)</c> — dropping the whole-tick carry — reds B2
    /// and B6 (a 60 fps frame is a third of a tick, so time stops); raising
    /// <see cref="TimeOfDaySettings.MaxSkyDarken"/> to 15 reds B3's moonlight-floor assertion;
    /// removing the clamp in <see cref="TimeOfDaySettings.EvaluateGlobalLightLevel"/> reds B5; and
    /// widening the default curve's plateaus back over the named times reds B9.
    /// </para>
    /// <para>
    /// Deliberately <b>not</b> claimed: widening <see cref="WorldTimeManager.SkyDarken"/>'s own clamp
    /// is invisible here, because <see cref="TimeOfDaySettings.EvaluateGlobalLightLevel"/> already
    /// bounds the value it derives from. That clamp is redundant defense, not the enforced bound.
    /// </para>
    /// </summary>
    public static class WorldClockValidationSuite
    {
        /// <summary>Frames per second the drift scenarios simulate.</summary>
        private const float SIMULATED_FPS = 60f;

        /// <summary>In-world days the drift scenario runs for — long enough that a drifting clock diverges visibly.</summary>
        private const int DRIFT_DAYS = 5;

        /// <summary>Tolerance, in ticks, for a simulated run against its ideal elapsed time.</summary>
        private const long TICK_TOLERANCE = 1L;

        /// <summary>Samples taken across the day by the curve-shape scenarios.</summary>
        private const int DAY_SAMPLES = 240;

        private const float FLOAT_EPSILON = 1e-5f;

        /// <summary>Smallest light-level gap counted as "these two times look different".</summary>
        private const float MIN_VISIBLE_STEP = 0.02f;

        /// <summary>Runs every scenario and prints a categorized summary via the shared runner.</summary>
        [MenuItem("Minecraft Clone/Dev/Validate World Clock")]
        public static void RunAll() => Execute();

        /// <summary>
        /// Builds and runs the clock scenarios, returning the categorized result (the headless/CI entry point).
        /// </summary>
        /// <param name="logToConsole">When false, runs silently and only returns the result (for headless/CI use).</param>
        /// <param name="showProgress">When false, suppresses this suite's own progress bar (the aggregate runner drives one).</param>
        /// <returns>The categorized, timed result of the run.</returns>
        public static ValidationRunResult Execute(bool logToConsole = true, bool showProgress = true)
        {
            List<Scenario> scenarios = new List<Scenario>
            {
                new Scenario("B1 Day wrap increments ElapsedDays exactly once; DayFraction stays in [0,1)", RunB1DayWrap),
                new Scenario("B2 No drift over 5 in-world days, and chunking-invariant", RunB2NoDrift),
                new Scenario("B3 SkyDarken range and anchors (noon 0, midnight 11)", RunB3SkyDarkenRange),
                new Scenario("B4 SkyDarken is monotone across dawn and dusk", RunB4Monotonic),
                new Scenario("B5 The moonlight floor is enforced however the curve is authored", RunB5MoonlightFloorEnforced),
                new Scenario("B6 Freeze holds the clock; resume releases it", RunB6Freeze),
                new Scenario("B7 /time named times map to the right point in the day", RunB7NamedTimes),
                new Scenario("B8 SetDayTime keeps the day count; AddTicks clamps at zero", RunB8Setters),
                new Scenario("B9 The named times are visually distinct — day != noon, night != midnight", RunB9NamedTimesDistinct),
                new Scenario("B10 Effective light: sky darkens with time, blocklight never does", RunB10EffectiveLight),
            };
            return ValidationSuiteRunner.Execute("World Clock", scenarios, KnownBugChannel.Unimplemented, logToConsole, showProgress);
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

        /// <summary>
        /// Builds a clock over a freshly created default settings asset. The caller must dispose it via
        /// <see cref="Release"/> — an undestroyed ScriptableObject leaks for the editor session.
        /// </summary>
        /// <param name="settings">The settings instance backing the clock, for later release.</param>
        /// <returns>A clock at tick 0.</returns>
        private static WorldTimeManager NewClock(out TimeOfDaySettings settings)
        {
            settings = ScriptableObject.CreateInstance<TimeOfDaySettings>();
            return new WorldTimeManager(settings);
        }

        /// <summary>Destroys a settings instance created by <see cref="NewClock"/>.</summary>
        /// <param name="settings">The instance to destroy.</param>
        private static void Release(TimeOfDaySettings settings)
        {
            if (settings != null) Object.DestroyImmediate(settings);
        }

        /// <summary>Positions the clock at a point in the day expressed as a fraction (0 = midnight).</summary>
        /// <param name="clock">The clock to move.</param>
        /// <param name="dayFraction">Target day fraction in <c>[0,1)</c>.</param>
        private static void SetDayFraction(WorldTimeManager clock, float dayFraction)
        {
            int dayTicks = Mathf.RoundToInt(dayFraction * WorldTimeManager.TicksPerDay) - WorldTimeManager.SunriseTickOffset;
            clock.SetDayTime(dayTicks);
        }

        /// <summary>B1 — the day counter advances once per wrap and the fraction never leaves its range.</summary>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunB1DayWrap()
        {
            WorldTimeManager clock = NewClock(out TimeOfDaySettings settings);
            try
            {
                clock.SetTotalTicks(WorldTimeManager.TicksPerDay - 2);
                bool ok = Check("starts on day 0", clock.ElapsedDays == 0);

                clock.AddTicks(1);
                ok &= Check("one tick short of the wrap is still day 0", clock.ElapsedDays == 0);

                clock.AddTicks(1);
                ok &= Check("crossing 24000 lands on day 1", clock.ElapsedDays == 1);
                ok &= Check($"day ticks reset to 0, got {clock.DayTicks}", clock.DayTicks == 0);

                clock.AddTicks(WorldTimeManager.TicksPerDay);
                ok &= Check("a further full day lands on day 2", clock.ElapsedDays == 2);

                // Sweep the whole day: the fraction is a rendering input and must never leave [0,1).
                bool inRange = true;
                for (int tick = 0; tick < WorldTimeManager.TicksPerDay; tick += 7)
                {
                    clock.SetDayTime(tick);
                    if (clock.DayFraction < 0f || clock.DayFraction >= 1f) inRange = false;
                }

                ok &= Check("DayFraction stays in [0,1) across every tick of the day", inRange);
                return ok;
            }
            finally
            {
                Release(settings);
            }
        }

        /// <summary>
        /// B2 — the whole-tick carry keeps elapsed time exact: five simulated days land on their ideal
        /// tick count, and the same wall time split into different frame sizes lands on the same tick.
        /// </summary>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunB2NoDrift()
        {
            WorldTimeManager clock = NewClock(out TimeOfDaySettings settings);
            try
            {
                const float frameSeconds = 1f / SIMULATED_FPS;
                int frames = Mathf.RoundToInt(DRIFT_DAYS * settings.DayLengthSeconds * SIMULATED_FPS);
                for (int i = 0; i < frames; i++) clock.Tick(frameSeconds);

                const long expected = (long)DRIFT_DAYS * WorldTimeManager.TicksPerDay;
                long error = System.Math.Abs(clock.TimeTicks - expected);
                bool ok = Check($"{DRIFT_DAYS} in-world days at {SIMULATED_FPS} fps land within {TICK_TOLERANCE} tick " +
                                $"of {expected} (got {clock.TimeTicks}, error {error})", error <= TICK_TOLERANCE);
                ok &= Check($"the day counter agrees, got {clock.ElapsedDays}", clock.ElapsedDays == DRIFT_DAYS);

                // Same wall time, coarser frames: a residue that leaked into the counter would make the
                // result depend on how the time was chopped up.
                WorldTimeManager coarse = new WorldTimeManager(settings);
                const float coarseSeconds = 1f / 15f;
                int coarseFrames = Mathf.RoundToInt(DRIFT_DAYS * settings.DayLengthSeconds * 15f);
                for (int i = 0; i < coarseFrames; i++) coarse.Tick(coarseSeconds);

                long chunkingDelta = System.Math.Abs(coarse.TimeTicks - clock.TimeTicks);
                ok &= Check($"60 fps and 15 fps agree within {TICK_TOLERANCE} tick " +
                            $"({clock.TimeTicks} vs {coarse.TimeTicks})", chunkingDelta <= TICK_TOLERANCE);

                // One giant frame carrying the same wall time is the extreme of the same property.
                WorldTimeManager single = new WorldTimeManager(settings);
                single.Tick(DRIFT_DAYS * settings.DayLengthSeconds);
                ok &= Check($"a single {DRIFT_DAYS}-day frame agrees too, got {single.TimeTicks}",
                    System.Math.Abs(single.TimeTicks - expected) <= TICK_TOLERANCE);

                return ok;
            }
            finally
            {
                Release(settings);
            }
        }

        /// <summary>B3 — the gameplay-facing darken value stays in range and hits its anchors.</summary>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunB3SkyDarkenRange()
        {
            WorldTimeManager clock = NewClock(out TimeOfDaySettings settings);
            try
            {
                SetDayFraction(clock, 0.5f);
                bool ok = Check($"noon is full daylight, got {clock.SkyDarken}", clock.SkyDarken == 0);

                SetDayFraction(clock, 0f);
                ok &= Check($"midnight is the deepest darken, got {clock.SkyDarken}",
                    clock.SkyDarken == TimeOfDaySettings.MaxSkyDarken);

                bool inRange = true;
                for (int i = 0; i < DAY_SAMPLES; i++)
                {
                    SetDayFraction(clock, i / (float)DAY_SAMPLES);
                    if (clock.SkyDarken < 0 || clock.SkyDarken > TimeOfDaySettings.MaxSkyDarken) inRange = false;
                }

                ok &= Check($"SkyDarken stays in [0, {TimeOfDaySettings.MaxSkyDarken}] across the whole day", inRange);

                // The moonlight floor is the reason for the cap: fully-exposed sky never goes below 4.
                SetDayFraction(clock, 0f);
                int effectiveAtMidnight = WorldTimeManager.MaxSkyLight - clock.SkyDarken;
                ok &= Check($"a fully sky-exposed voxel keeps effective light 4 at midnight, got {effectiveAtMidnight}",
                    effectiveAtMidnight == 4);
                return ok;
            }
            finally
            {
                Release(settings);
            }
        }

        /// <summary>B4 — darkness only ever falls through dawn and only ever rises through dusk.</summary>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunB4Monotonic()
        {
            WorldTimeManager clock = NewClock(out TimeOfDaySettings settings);
            try
            {
                // Dawn: midnight (0.0) to noon (0.5), darkness never increases.
                bool dawnOk = true;
                float previous = float.MaxValue;
                for (int i = 0; i <= DAY_SAMPLES / 2; i++)
                {
                    SetDayFraction(clock, i / (float)DAY_SAMPLES);
                    if (clock.ContinuousSkyDarken > previous + FLOAT_EPSILON) dawnOk = false;
                    previous = clock.ContinuousSkyDarken;
                }

                bool ok = Check("darkness never increases from midnight to noon", dawnOk);

                // Dusk: noon (0.5) to the end of the day, darkness never decreases.
                bool duskOk = true;
                previous = float.MinValue;
                for (int i = DAY_SAMPLES / 2; i < DAY_SAMPLES; i++)
                {
                    SetDayFraction(clock, i / (float)DAY_SAMPLES);
                    if (clock.ContinuousSkyDarken < previous - FLOAT_EPSILON) duskOk = false;
                    previous = clock.ContinuousSkyDarken;
                }

                ok &= Check("darkness never decreases from noon to midnight", duskOk);
                return ok;
            }
            finally
            {
                Release(settings);
            }
        }

        /// <summary>
        /// B5 — the moonlight floor must survive a badly authored curve, because it is the guarantee
        /// gameplay thresholds rest on: a fully sky-exposed voxel never drops below effective light 4.
        /// </summary>
        /// <returns>True when every assertion holds.</returns>
        /// <remarks>
        /// This scenario replaced an earlier "GlobalLightLevel == 1 − continuousSkyDarken/15" identity
        /// check. When the curve was re-authored in light-level units (2026-08-10) the darken became a
        /// pure function of that same value, which made the identity algebraically unfalsifiable — a
        /// green that could never go red. The clamp is the part that can still actually break.
        /// </remarks>
        private static bool RunB5MoonlightFloorEnforced()
        {
            WorldTimeManager clock = NewClock(out TimeOfDaySettings settings);
            try
            {
                // A rogue curve that dives below black and overshoots past full daylight.
                ValidationReflection.SetInstanceField(settings, "_globalLightLevelOverDay",
                    new AnimationCurve(
                        new Keyframe(0f, -0.5f), new Keyframe(0.5f, 2f), new Keyframe(1f, -0.5f)));

                bool rangeHolds = true;
                bool darkenHolds = true;
                for (int i = 0; i < DAY_SAMPLES; i++)
                {
                    SetDayFraction(clock, i / (float)DAY_SAMPLES);

                    if (clock.GlobalLightLevel < TimeOfDaySettings.MinGlobalLightLevel - FLOAT_EPSILON ||
                        clock.GlobalLightLevel > 1f + FLOAT_EPSILON) rangeHolds = false;

                    if (clock.SkyDarken < 0 || clock.SkyDarken > TimeOfDaySettings.MaxSkyDarken) darkenHolds = false;
                }

                bool ok = Check("a curve authored outside [0.27, 1] is clamped back into range", rangeHolds);
                ok &= Check($"SkyDarken stays within [0, {TimeOfDaySettings.MaxSkyDarken}] under that curve", darkenHolds);

                // With the curve pinned at its darkest, the floor is exactly Minecraft's moonlight.
                SetDayFraction(clock, 0f);
                ok &= Check($"the darkest reachable state still leaves effective light 4, got {WorldTimeManager.MaxSkyLight - clock.SkyDarken}",
                    WorldTimeManager.MaxSkyLight - clock.SkyDarken == 4);

                // The rounding the design accepts between the rendered and queried values.
                bool roundingHolds = true;
                for (int i = 0; i < DAY_SAMPLES; i++)
                {
                    SetDayFraction(clock, i / (float)DAY_SAMPLES);
                    if (Mathf.Abs(clock.SkyDarken - clock.ContinuousSkyDarken) > 0.5f + FLOAT_EPSILON)
                        roundingHolds = false;
                }

                ok &= Check("the queried SkyDarken is within half a level of the rendered value", roundingHolds);
                return ok;
            }
            finally
            {
                Release(settings);
            }
        }

        /// <summary>
        /// B9 — the named <c>/time</c> targets must land on visibly different light levels. Regression
        /// guard for the shipped-default shape that put <c>day</c> and <c>noon</c> (and <c>night</c> and
        /// <c>midnight</c>) on the same plateau, making four of the six named times indistinguishable.
        /// </summary>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunB9NamedTimesDistinct()
        {
            WorldTimeManager clock = NewClock(out TimeOfDaySettings settings);
            try
            {
                clock.SetDayTime(1000);
                float day = clock.GlobalLightLevel;
                clock.SetDayTime(6000);
                float noon = clock.GlobalLightLevel;
                clock.SetDayTime(12000);
                float sunset = clock.GlobalLightLevel;
                clock.SetDayTime(13000);
                float night = clock.GlobalLightLevel;
                clock.SetDayTime(18000);
                float midnight = clock.GlobalLightLevel;
                clock.SetDayTime(23000);
                float sunrise = clock.GlobalLightLevel;

                bool ok = Check($"'day' is dimmer than 'noon' ({day:F3} < {noon:F3})", day < noon - MIN_VISIBLE_STEP);
                ok &= Check($"'night' is brighter than 'midnight' ({night:F3} > {midnight:F3})", night > midnight + MIN_VISIBLE_STEP);
                ok &= Check($"'sunrise' is dimmer than 'day' ({sunrise:F3} < {day:F3})", sunrise < day - MIN_VISIBLE_STEP);
                ok &= Check($"'sunset' is dimmer than 'noon' ({sunset:F3} < {noon:F3})", sunset < noon - MIN_VISIBLE_STEP);
                ok &= Check($"'sunset' is brighter than 'night' ({sunset:F3} > {night:F3})", sunset > night + MIN_VISIBLE_STEP);
                ok &= Check($"'noon' is full daylight, got {noon:F3}", Mathf.Abs(noon - 1f) < FLOAT_EPSILON);
                ok &= Check($"'midnight' is the moonlight floor, got {midnight:F3}",
                    Mathf.Abs(midnight - TimeOfDaySettings.MinGlobalLightLevel) < FLOAT_EPSILON);
                return ok;
            }
            finally
            {
                Release(settings);
            }
        }

        /// <summary>B6 — freeze holds the clock and survives further ticks; resume releases it.</summary>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunB6Freeze()
        {
            WorldTimeManager clock = NewClock(out TimeOfDaySettings settings);
            try
            {
                clock.SetDayTime(6000);
                long held = clock.TimeTicks;

                clock.IsFrozen = true;
                for (int i = 0; i < 600; i++) clock.Tick(1f / SIMULATED_FPS);
                bool ok = Check($"a frozen clock does not advance, got {clock.TimeTicks}", clock.TimeTicks == held);

                clock.IsFrozen = false;
                for (int i = 0; i < 600; i++) clock.Tick(1f / SIMULATED_FPS);
                ok &= Check($"a resumed clock advances again, got {clock.TimeTicks}", clock.TimeTicks > held);

                // Freezing must not block explicit control — /time set works on a held world.
                clock.IsFrozen = true;
                clock.SetDayTime(18000);
                ok &= Check($"a frozen clock still honors an explicit set, got {clock.DayTicks}", clock.DayTicks == 18000);
                return ok;
            }
            finally
            {
                Release(settings);
            }
        }

        /// <summary>
        /// B7 — the Minecraft-anchored tick values land where their names say, which is the whole point
        /// of carrying the sunrise offset.
        /// </summary>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunB7NamedTimes()
        {
            WorldTimeManager clock = NewClock(out TimeOfDaySettings settings);
            try
            {
                clock.SetDayTime(6000);
                bool ok = Check($"'noon' (tick 6000) is day fraction 0.5, got {clock.DayFraction}",
                    Mathf.Abs(clock.DayFraction - 0.5f) < FLOAT_EPSILON);
                ok &= Check($"noon is full daylight, got {clock.SkyDarken}", clock.SkyDarken == 0);

                clock.SetDayTime(18000);
                ok &= Check($"'midnight' (tick 18000) is day fraction 0, got {clock.DayFraction}",
                    Mathf.Abs(clock.DayFraction) < FLOAT_EPSILON);
                ok &= Check($"midnight is the deepest darken, got {clock.SkyDarken}",
                    clock.SkyDarken == TimeOfDaySettings.MaxSkyDarken);

                clock.SetDayTime(12000);
                ok &= Check($"'sunset' (tick 12000) is day fraction 0.75, got {clock.DayFraction}",
                    Mathf.Abs(clock.DayFraction - 0.75f) < FLOAT_EPSILON);

                clock.SetDayTime(0);
                ok &= Check($"tick 0 is sunrise, day fraction 0.25, got {clock.DayFraction}",
                    Mathf.Abs(clock.DayFraction - 0.25f) < FLOAT_EPSILON);

                // Sunset must actually be darker than noon, or the anchoring is inverted.
                clock.SetDayTime(6000);
                int noonDarken = clock.SkyDarken;
                clock.SetDayTime(13000);
                ok &= Check($"'night' (tick 13000) is darker than noon ({clock.SkyDarken} > {noonDarken})",
                    clock.SkyDarken > noonDarken);
                return ok;
            }
            finally
            {
                Release(settings);
            }
        }

        /// <summary>
        /// B10 — the RF-1 §9 read-time model: time of day subtracts from stored sky <i>exposure</i> and
        /// never touches blocklight, so a torch-lit voxel reads the same at midnight as at noon.
        /// </summary>
        /// <returns>True when every assertion holds.</returns>
        /// <remarks>
        /// Exercises <see cref="LightBitMapping"/> directly rather than through <c>World</c>: the query is
        /// pure integer math over a packed value, and a scene-bound test would only add a chunk lookup
        /// between the assertion and the thing being asserted.
        /// </remarks>
        private static bool RunB10EffectiveLight()
        {
            // Fully sky-exposed, unlit by blocks — the open-terrain case.
            ushort openSky = LightBitMapping.PackLightData(15, 0, 0, 0);
            bool ok = Check("open sky at noon is full light",
                LightBitMapping.GetEffectiveLight(openSky, 0) == 15);
            ok &= Check($"open sky at deepest night is the moonlight floor 4, got {LightBitMapping.GetEffectiveLight(openSky, 11)}",
                LightBitMapping.GetEffectiveLight(openSky, 11) == 4);

            // Stored exposure is never mutated by the query — the whole point of the read-time model.
            ok &= Check("the stored channel is untouched by darkening",
                LightBitMapping.GetSkyLight(openSky) == 15);

            // A dim sky-lit voxel clamps at zero rather than going negative.
            ushort dimSky = LightBitMapping.PackLightData(3, 0, 0, 0);
            ok &= Check($"sky light below the darken clamps to 0, got {LightBitMapping.GetEffectiveSkyLight(dimSky, 11)}",
                LightBitMapping.GetEffectiveSkyLight(dimSky, 11) == 0);

            // Torches are time-invariant: the max() picks blocklight once sky falls below it.
            ushort torchLit = LightBitMapping.PackLightData(15, 14, 8, 2);
            ok &= Check($"a torch-lit voxel keeps its blocklight at midnight, got {LightBitMapping.GetEffectiveLight(torchLit, 11)}",
                LightBitMapping.GetEffectiveLight(torchLit, 11) == 14);
            ok &= Check("its sky contribution still darkens underneath",
                LightBitMapping.GetEffectiveSkyLight(torchLit, 11) == 4);

            // A cave voxel with no sky exposure is unaffected by time entirely.
            ushort cave = LightBitMapping.PackLightData(0, 9, 0, 0);
            ok &= Check("a sky-sealed voxel reads the same at every time of day",
                LightBitMapping.GetEffectiveLight(cave, 0) == 9 &&
                LightBitMapping.GetEffectiveLight(cave, 11) == 9);

            // The query and the clock agree: what the shader subtracts is what gameplay subtracts.
            WorldTimeManager clock = NewClock(out TimeOfDaySettings settings);
            try
            {
                SetDayFraction(clock, 0f);
                int fromShaderGlobal = Mathf.RoundToInt((1f - clock.GlobalLightLevel) * WorldTimeManager.MaxSkyLight);
                ok &= Check($"the darken implied by GlobalLightLevel matches SkyDarken ({fromShaderGlobal} vs {clock.SkyDarken})",
                    fromShaderGlobal == clock.SkyDarken);
                ok &= Check($"open sky at midnight reads 4 through the clock, got {LightBitMapping.GetEffectiveLight(openSky, clock.SkyDarken)}",
                    LightBitMapping.GetEffectiveLight(openSky, clock.SkyDarken) == 4);
            }
            finally
            {
                Release(settings);
            }

            return ok;
        }

        /// <summary>B8 — the explicit setters keep the day count and refuse to run time backwards past zero.</summary>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunB8Setters()
        {
            WorldTimeManager clock = NewClock(out TimeOfDaySettings settings);
            try
            {
                clock.SetTotalTicks(3 * WorldTimeManager.TicksPerDay + 100);
                clock.SetDayTime(6000);
                bool ok = Check($"setting the time of day keeps the day count, got {clock.ElapsedDays}", clock.ElapsedDays == 3);
                ok &= Check($"the day time is what was set, got {clock.DayTicks}", clock.DayTicks == 6000);

                clock.SetDayTime(WorldTimeManager.TicksPerDay + 500);
                ok &= Check($"an over-range day time wraps rather than escaping the day, got {clock.DayTicks}",
                    clock.DayTicks == 500);

                clock.SetDayTime(-1000);
                ok &= Check($"a negative day time wraps forward, got {clock.DayTicks}",
                    clock.DayTicks == WorldTimeManager.TicksPerDay - 1000);

                clock.SetTotalTicks(100);
                clock.AddTicks(-5000);
                ok &= Check($"rewinding past the world's start clamps at zero, got {clock.TimeTicks}", clock.TimeTicks == 0);

                clock.SetTotalTicks(-42);
                ok &= Check($"a negative total is clamped, got {clock.TimeTicks}", clock.TimeTicks == 0);
                return ok;
            }
            finally
            {
                Release(settings);
            }
        }
    }
}
