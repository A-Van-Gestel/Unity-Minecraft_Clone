using System.Collections.Generic;
using Audio;
using Data;
using Data.WorldTypes;
using Jobs.Helpers;
using Editor.Validation.Framework;
using UnityEngine;

namespace Editor.Validation.SoundEngine
{
    /// <summary>
    /// <see cref="SoundEngineValidationSuite"/> — the world-ambience decisions (S2): the dwell filter behind
    /// the cave bed, bed and music-pool selection with their fallbacks, the crossfade and duck gains, the
    /// submersion test and its cutoff sweep, and the music scheduler's gap and track picks.
    /// <para>
    /// None of these need a clip to be audible, and every one of them fails quietly in a running game: a
    /// dwell that commits a frame early flaps at a cave mouth, a fallback that resolves to null is
    /// indistinguishable from "no content authored yet", and a linear crossfade dips in the middle without
    /// ever reporting anything.
    /// </para>
    /// </summary>
    public static partial class SoundEngineValidationSuite
    {
        /// <summary>Steps each continuous sweep takes across its input range.</summary>
        private const int AMBIENCE_SWEEP_STEPS = 512;

        /// <summary>Tolerance for the float identities the gain scenarios assert.</summary>
        private const float AMBIENCE_EPSILON = 1e-4f;

        /// <summary>Ticks a fade gets to settle exactly on its target before the scenario calls it stuck.</summary>
        private const int FADE_CONVERGENCE_TICK_BUDGET = 64;

        static partial void AddAmbienceScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario("Cave Dwell Holds A Reading Before Committing It", RunCaveDwell));
            scenarios.Add(new Scenario("Underground Test Includes Its Threshold Level", RunUndergroundThreshold));
            scenarios.Add(new Scenario("A Head In A Fluid Cell Reads As Submerged", RunSubmergedTest));
            scenarios.Add(new Scenario("Ambience Falls Back When The Biome Authors No Bed", RunBedFallback));
            scenarios.Add(new Scenario("Music Pool Falls Back When The Biome Authors None", RunPoolFallback));
            scenarios.Add(new Scenario("Complementary Bed Fades Hold Constant Power", RunBedGainCurve));
            scenarios.Add(new Scenario("Bed Fade Advances At The Authored Rate And Clamps", RunAdvanceFade));
            scenarios.Add(new Scenario("A Returning Bed Reclaims Its Own Still-Audible Source", RunBedSlotReclaim));
            scenarios.Add(new Scenario("A New Bed Takes A Silent Source Before The Quietest Audible One",
                RunBedSlotPreference));
            scenarios.Add(new Scenario("Cave Bed Ducks The Biome Bed By Its Authored Amount", RunBiomeDuck));
            scenarios.Add(new Scenario("Depth Below The Surface Silences The Biome Beds", RunDepthDuck));
            scenarios.Add(new Scenario("Submersion Cutoff Sweeps Monotonically In Log Space", RunLowPassSweep));
            scenarios.Add(new Scenario("Music Gap Stays Inside Its Authored Bounds", RunMusicGap));
            scenarios.Add(new Scenario("Music Never Picks The Same Track Twice Running", RunTrackPick));
            scenarios.Add(new Scenario("Bed Mix Weights Every Nearby Biome And Normalizes", RunBedMix));
            scenarios.Add(new Scenario("Beds Sharing A Clip Merge Onto One Source", RunBedMixMerge));
            scenarios.Add(new Scenario("Ambience Rest Cycle Alternates Inside Its Authored Bounds", RunRestCycle));
        }

        /// <summary>
        /// The dwell filter the cave bed rides: a disagreeing reading must persist for the full dwell before
        /// it commits, and agreeing again must discard whatever was pending rather than banking it.
        /// </summary>
        private static bool RunCaveDwell()
        {
            const string scenario = "Cave Dwell Holds A Reading Before Committing It";
            const float dwell = 3f;
            const float step = 0.25f;

            float held = 0f;
            bool committed = false;

            // Eleven quarter-second ticks reach 2.75 s — still short of the dwell.
            for (int i = 0; i < 11; i++)
            {
                committed = AmbienceResolution.TickDwell(true, committed, step, dwell, ref held);
                if (committed)
                    return FailSound(scenario, $"committed after {(i + 1) * step:0.00}s, before the {dwell}s dwell.");
            }

            committed = AmbienceResolution.TickDwell(true, committed, step, dwell, ref held);
            if (!committed) return FailSound(scenario, "did not commit once the full dwell had been served.");
            if (!ExactValue.IsZero(held)) return FailSound(scenario, $"held time was {held}, not reset on commit.");

            // Half a dwell of disagreement, then agreement: the pending change must lose its claim entirely,
            // or a player pacing a cave mouth accumulates a commit out of crossings that each fell short.
            // Entered on the state the two assertions above just pinned (committed, nothing held) rather than
            // re-seeding it — an assignment here would hide the regression those assertions exist to catch.
            for (int i = 0; i < 6; i++) committed = AmbienceResolution.TickDwell(false, committed, step, dwell, ref held);
            if (!committed) return FailSound(scenario, "committed the reverse reading before its dwell.");

            committed = AmbienceResolution.TickDwell(true, committed, step, dwell, ref held);
            if (!ExactValue.IsZero(held))
                return FailSound(scenario, $"agreement left {held}s banked instead of clearing it.");

            for (int i = 0; i < 11; i++) committed = AmbienceResolution.TickDwell(false, committed, step, dwell, ref held);
            if (!committed) return FailSound(scenario, "the banked time let a later crossing commit early.");

            // A zero dwell is the "no hysteresis" authoring, and must commit on the first disagreeing tick.
            held = 0f;
            if (!AmbienceResolution.TickDwell(true, false, step, 0f, ref held))
                return FailSound(scenario, "a zero dwell did not commit immediately.");

            return true;
        }

        /// <summary>The underground threshold, including its own level — an off-by-one here mutes every cave.</summary>
        private static bool RunUndergroundThreshold()
        {
            const string scenario = "Underground Test Includes Its Threshold Level";

            if (!AmbienceResolution.IsUnderground(0, 0)) return FailSound(scenario, "no sky at all did not read as underground.");
            if (AmbienceResolution.IsUnderground(1, 0)) return FailSound(scenario, "one level of sky read as underground at threshold 0.");
            if (!AmbienceResolution.IsUnderground(2, 2)) return FailSound(scenario, "the threshold level itself did not read as underground.");
            if (AmbienceResolution.IsUnderground(3, 2)) return FailSound(scenario, "a level above the threshold read as underground.");
            if (!AmbienceResolution.IsUnderground(15, 15)) return FailSound(scenario, "full exposure did not read as underground at threshold 15.");

            return true;
        }

        /// <summary>
        /// The submersion test, including the three ways it can be handed nothing: a null database, an ID
        /// past its end, and a hole in the array.
        /// </summary>
        private static bool RunSubmergedTest()
        {
            const string scenario = "A Head In A Fluid Cell Reads As Submerged";

            BlockType[] blocks =
            {
                new BlockType { blockName = "Air", fluidType = FluidType.None },
                new BlockType { blockName = "Water", fluidType = FluidType.WaterLike },
                null,
                new BlockType { blockName = "Stone", fluidType = FluidType.None },
            };

            if (!AmbienceResolution.IsSubmerged(blocks, 1)) return FailSound(scenario, "a water cell did not read as submerged.");
            if (AmbienceResolution.IsSubmerged(blocks, 0)) return FailSound(scenario, "air read as submerged.");
            if (AmbienceResolution.IsSubmerged(blocks, 3)) return FailSound(scenario, "stone read as submerged.");
            if (!AmbienceResolution.IsSubmerged(new[] { new BlockType { fluidType = FluidType.LavaLike } }, 0))
                return FailSound(scenario, "a lava cell did not read as submerged.");
            if (AmbienceResolution.IsSubmerged(blocks, 2)) return FailSound(scenario, "a null block entry read as submerged.");
            if (AmbienceResolution.IsSubmerged(blocks, 99)) return FailSound(scenario, "an out-of-range ID read as submerged.");
            if (AmbienceResolution.IsSubmerged(null, 1)) return FailSound(scenario, "a null database read as submerged.");

            return true;
        }

        /// <summary>
        /// Bed selection across all four holes it must cover: an authored biome bed, a biome with none, a
        /// null biome asset, and a world that answers no biome at all (the legacy generator).
        /// </summary>
        private static bool RunBedFallback()
        {
            const string scenario = "Ambience Falls Back When The Biome Authors No Bed";

            AudioClip biomeLoop = AudioClip.Create("ValidationBiomeLoop", 16, 1, 8000, false);
            AudioClip fallback = AudioClip.Create("ValidationFallbackLoop", 16, 1, 8000, false);

            StandardBiomeAttributes authored = ScriptableObject.CreateInstance<StandardBiomeAttributes>();
            StandardBiomeAttributes bare = ScriptableObject.CreateInstance<StandardBiomeAttributes>();

            try
            {
                authored.ambientLoop = biomeLoop;

                if (AmbienceResolution.SelectBiomeLoop(new AudioContext(0, authored, true, 15, false), fallback) != biomeLoop)
                    return FailSound(scenario, "an authored biome bed was not selected.");

                if (AmbienceResolution.SelectBiomeLoop(new AudioContext(0, bare, true, 15, false), fallback) != fallback)
                    return FailSound(scenario, "a biome with no bed did not fall back to the default.");

                if (AmbienceResolution.SelectBiomeLoop(new AudioContext(0, null, true, 15, false), fallback) != fallback)
                    return FailSound(scenario, "a null biome asset did not fall back to the default.");

                // The legacy generator answers no biome for a whole session: this must be the fallback bed,
                // not silence, or that world type loses its ambience entirely and reports nothing.
                if (AmbienceResolution.SelectBiomeLoop(new AudioContext(-1, authored, false, 15, false), fallback) != fallback)
                    return FailSound(scenario, "a world with no biome answer did not fall back to the default.");

                if (AmbienceResolution.SelectBiomeLoop(new AudioContext(0, bare, true, 15, false), null) != null)
                    return FailSound(scenario, "an unauthored fallback resolved to something other than null.");

                return true;
            }
            finally
            {
                Object.DestroyImmediate(authored);
                Object.DestroyImmediate(bare);
            }
        }

        /// <summary>Music-pool selection, where an empty array must fall back exactly as a null one does.</summary>
        private static bool RunPoolFallback()
        {
            const string scenario = "Music Pool Falls Back When The Biome Authors None";

            AudioClip[] biomePool = MakeClips(2);
            AudioClip[] fallbackPool = MakeClips(3);

            StandardBiomeAttributes authored = ScriptableObject.CreateInstance<StandardBiomeAttributes>();
            StandardBiomeAttributes empty = ScriptableObject.CreateInstance<StandardBiomeAttributes>();

            try
            {
                authored.musicPool = biomePool;
                empty.musicPool = System.Array.Empty<AudioClip>();

                if (AmbienceResolution.SelectMusicPool(new AudioContext(0, authored, true, 15, false), fallbackPool) != biomePool)
                    return FailSound(scenario, "an authored biome pool was not selected.");

                // An empty array and a null one are the same authoring state — "this biome adds no music" —
                // and a length check that only guards null would hand the scheduler a pool it can never pick from.
                if (AmbienceResolution.SelectMusicPool(new AudioContext(0, empty, true, 15, false), fallbackPool) != fallbackPool)
                    return FailSound(scenario, "an empty biome pool did not fall back to the global pool.");

                if (AmbienceResolution.SelectMusicPool(new AudioContext(-1, authored, false, 15, false), fallbackPool) != fallbackPool)
                    return FailSound(scenario, "a world with no biome answer did not fall back to the global pool.");

                return true;
            }
            finally
            {
                Object.DestroyImmediate(authored);
                Object.DestroyImmediate(empty);
            }
        }

        /// <summary>
        /// The bed gain curve. Two beds handing over hold complementary fades, so the equal-power identity
        /// <c>g(f)² + g(1−f)² == 1</c> is what stops the mix dipping mid-handover — a linear amplitude ramp
        /// passes the endpoints and fails the identity everywhere between them.
        /// </summary>
        private static bool RunBedGainCurve()
        {
            const string scenario = "Complementary Bed Fades Hold Constant Power";

            // Exact at the endpoints: a silent source must be truly silent, not nearly so, or a released
            // slot keeps bleeding into the mix.
            if (!ExactValue.IsZero(AmbienceResolution.GainFromFade(0f)))
                return FailSound(scenario, "a fade of 0 did not produce silence.");
            if (!ExactValue.Equal(AmbienceResolution.GainFromFade(1f), 1f))
                return FailSound(scenario, "a fade of 1 did not produce unity gain.");

            float previous = -1f;
            for (int i = 0; i <= AMBIENCE_SWEEP_STEPS; i++)
            {
                float fade = i / (float)AMBIENCE_SWEEP_STEPS;
                float gain = AmbienceResolution.GainFromFade(fade);
                float partner = AmbienceResolution.GainFromFade(1f - fade);

                float power = gain * gain + partner * partner;
                if (Mathf.Abs(power - 1f) > AMBIENCE_EPSILON)
                    return FailSound(scenario,
                        $"summed power at fade={fade:0.000} was {power}, not 1 — the mix dips mid-handover.");

                if (gain < previous) return FailSound(scenario, $"the gain fell at fade={fade:0.000}.");
                previous = gain;
            }

            if (!ExactValue.IsZero(AmbienceResolution.GainFromFade(-4f)))
                return FailSound(scenario, "a fade below 0 was not clamped to silence.");
            if (!ExactValue.Equal(AmbienceResolution.GainFromFade(4f), 1f))
                return FailSound(scenario, "a fade above 1 was not clamped to unity.");

            return true;
        }

        /// <summary>
        /// The per-source fade advance: it takes the authored time, settles <b>exactly</b> on its target, and
        /// survives the degenerate authorings (a zero duration, a negative delta, an out-of-range target).
        /// </summary>
        /// <remarks>
        /// Landing exactly on 0 is the load-bearing half, and the reason this asserts convergence rather than
        /// a tick count: a released bed slot is recognized by a fade of exactly zero, so a fade that only ever
        /// approached zero would leave every source permanently "audible" and force each handover to interrupt
        /// one. Exactness on 1 has no such consumer — accumulated step error means it arrives a tick late, and
        /// nothing anywhere cares.
        /// </remarks>
        private static bool RunAdvanceFade()
        {
            const string scenario = "Bed Fade Advances At The Authored Rate And Clamps";
            const float fadeSeconds = 3f;
            const float step = 0.25f;

            // 3 s of ticks must not have completed the fade — that is the authored duration doing its job.
            float fade = 0f;
            for (int i = 0; i < 12; i++)
            {
                fade = AmbienceResolution.AdvanceFade(fade, 1f, step, fadeSeconds);
                if (fade >= 1f)
                    return FailSound(scenario,
                        $"reached full after {(i + 1) * step:0.00}s, before the {fadeSeconds}s fade.");
            }

            if (!ConvergesExactly(1f, 1f, step, fadeSeconds, out int upTicks))
                return FailSound(scenario, "a rising fade never settled exactly on 1.");
            if (upTicks > 2)
                return FailSound(scenario, $"a rising fade took {upTicks} extra ticks to settle — step error is accumulating.");

            // The half a released bed slot depends on.
            if (!ConvergesExactly(1f, 0f, step, fadeSeconds, out int downTicks))
                return FailSound(scenario,
                    "a falling fade never settled exactly on 0 — bed slots would never be released.");
            if (downTicks > 14)
                return FailSound(scenario, $"a falling fade needed {downTicks} ticks to reach silence.");

            // At the target, further ticks must not carry it past.
            if (!ExactValue.Equal(AmbienceResolution.AdvanceFade(1f, 1f, step, fadeSeconds), 1f))
                return FailSound(scenario, "overshot its target.");

            // A returning bed resumes from where it was rather than restarting — the whole point of a
            // per-source fade, and the case a shared crossfade timer cannot express.
            float resumed = AmbienceResolution.AdvanceFade(0.6f, 1f, step, fadeSeconds);
            if (resumed <= 0.6f) return FailSound(scenario, "a partly-faded source did not resume upward.");

            if (!ExactValue.Equal(AmbienceResolution.AdvanceFade(0.4f, 1f, step, 0f), 1f))
                return FailSound(scenario, "a zero-length fade did not snap to its target.");
            if (!ExactValue.Equal(AmbienceResolution.AdvanceFade(0.4f, 1f, -5f, fadeSeconds), 0.4f))
                return FailSound(scenario, "a negative delta moved the fade.");
            if (!ExactValue.IsZero(AmbienceResolution.AdvanceFade(0.05f, -3f, step, fadeSeconds)))
                return FailSound(scenario, "an out-of-range target was not clamped to silence.");

            return true;
        }

        /// <summary>
        /// Drives a fade to its target and reports whether it settles on it bit-exactly.
        /// </summary>
        /// <param name="from">Starting fade position.</param>
        /// <param name="target">The target to converge on.</param>
        /// <param name="step">Seconds per tick.</param>
        /// <param name="fadeSeconds">The authored fade duration.</param>
        /// <param name="ticksBeyondDuration">Ticks taken beyond the authored duration; 0 when it lands on time.</param>
        /// <returns>True when the fade reached the target exactly inside the tick budget.</returns>
        private static bool ConvergesExactly(float from, float target, float step, float fadeSeconds,
            out int ticksBeyondDuration)
        {
            int onTime = Mathf.CeilToInt(fadeSeconds / step);
            float fade = from;

            for (int i = 1; i <= FADE_CONVERGENCE_TICK_BUDGET; i++)
            {
                fade = AmbienceResolution.AdvanceFade(fade, target, step, fadeSeconds);
                if (!ExactValue.Equal(fade, target)) continue;

                ticksBeyondDuration = Mathf.Max(0, i - onTime);
                return true;
            }

            ticksBeyondDuration = -1;
            return false;
        }

        /// <summary>
        /// The case that motivated the per-source model: a bed the player walks back into is still audible,
        /// and must be resumed rather than restarted on another source.
        /// </summary>
        private static bool RunBedSlotReclaim()
        {
            const string scenario = "A Returning Bed Reclaims Its Own Still-Audible Source";

            AudioClip[] clips = MakeClips(3);
            AudioClip[] slots = { clips[0], clips[1], null, null };
            float[] fades = { 0.35f, 0.65f, 0f, 0f };

            // Slot 0 is quieter than slot 1, so a chooser that only ever took the quietest would answer 0
            // here and restart a bed that is still playing on slot 1.
            if (AmbienceResolution.SelectBedSlot(slots, fades, clips[1]) != 1)
                return FailSound(scenario, "did not reclaim the source already carrying the wanted clip.");
            if (AmbienceResolution.SelectBedSlot(slots, fades, clips[0]) != 0)
                return FailSound(scenario, "did not reclaim a fading-out source carrying the wanted clip.");

            if (AmbienceResolution.SelectBedSlot(slots, fades, null) != -1)
                return FailSound(scenario, "a null selection claimed a source.");
            if (AmbienceResolution.SelectBedSlot(null, fades, clips[0]) != -1)
                return FailSound(scenario, "a null slot array did not report -1.");
            if (AmbienceResolution.SelectBedSlot(slots, null, clips[2]) != -1)
                return FailSound(scenario, "a null fade array did not report -1.");

            return true;
        }

        /// <summary>
        /// Slot preference for a clip nothing is carrying: a silent source first, and only when every source
        /// is audible, the quietest of them.
        /// </summary>
        private static bool RunBedSlotPreference()
        {
            const string scenario = "A New Bed Takes A Silent Source Before The Quietest Audible One";

            AudioClip[] clips = MakeClips(5);

            AudioClip[] withFree = { clips[0], clips[1], null, null };
            float[] freeFades = { 0.9f, 0.5f, 0f, 0f };
            int free = AmbienceResolution.SelectBedSlot(withFree, freeFades, clips[4]);
            if (free != 2 && free != 3)
                return FailSound(scenario, $"claimed slot {free} while silent slots 2 and 3 were available.");

            // Every source audible: the only remaining answer is the least audible one.
            AudioClip[] allBusy = { clips[0], clips[1], clips[2], clips[3] };
            float[] busyFades = { 0.9f, 0.2f, 0.75f, 0.5f };
            int quietest = AmbienceResolution.SelectBedSlot(allBusy, busyFades, clips[4]);
            if (quietest != 1)
                return FailSound(scenario, $"claimed slot {quietest} rather than the quietest audible slot 1.");

            return true;
        }

        /// <summary>The duck the cave bed applies to the biome bed, including both authoring extremes.</summary>
        private static bool RunBiomeDuck()
        {
            const string scenario = "Cave Bed Ducks The Biome Bed By Its Authored Amount";

            if (Mathf.Abs(AmbienceResolution.BiomeDuck(0f, 0.7f) - 1f) > AMBIENCE_EPSILON)
                return FailSound(scenario, "a faded-out cave bed still ducked the biome bed.");
            if (Mathf.Abs(AmbienceResolution.BiomeDuck(1f, 0.7f) - 0.3f) > AMBIENCE_EPSILON)
                return FailSound(scenario, "a full cave bed did not duck by the authored amount.");
            if (Mathf.Abs(AmbienceResolution.BiomeDuck(1f, 1f)) > AMBIENCE_EPSILON)
                return FailSound(scenario, "a full duck did not silence the biome bed.");
            if (Mathf.Abs(AmbienceResolution.BiomeDuck(1f, 0f) - 1f) > AMBIENCE_EPSILON)
                return FailSound(scenario, "a zero duck attenuated the biome bed.");
            if (Mathf.Abs(AmbienceResolution.BiomeDuck(4f, 2f)) > AMBIENCE_EPSILON)
                return FailSound(scenario, "out-of-range inputs were not clamped, producing a negative gain.");

            return true;
        }

        /// <summary>
        /// The depth gate: fully present above ground, fully silent past the authored depth, and monotonic
        /// through the taper between them.
        /// </summary>
        /// <remarks>
        /// Exactness at both ends is the point, not a nicety. Anything above zero deep underground is the
        /// defect this scenario exists for — the surface bed audible in a cavern — and anything below one at
        /// the surface would quietly attenuate ordinary daylight ambience.
        /// </remarks>
        private static bool RunDepthDuck()
        {
            const string scenario = "Depth Below The Surface Silences The Biome Beds";
            const int fullDepth = 24;
            const int taper = 12;

            // Above ground and at the surface: untouched.
            foreach (int depth in new[] { -64, -8, -1, 0 })
            {
                if (!ExactValue.Equal(AmbienceResolution.DepthDuck(depth, fullDepth, taper), 1f))
                    return FailSound(scenario, $"depth {depth} attenuated the beds above ground.");
            }

            // Inside the taper's top: still fully present, so a cave mouth blends instead of stepping.
            if (!ExactValue.Equal(AmbienceResolution.DepthDuck(fullDepth - taper, fullDepth, taper), 1f))
                return FailSound(scenario, "the taper started before its authored depth.");

            // Past the authored depth: exactly silent, at the boundary and far beyond it.
            foreach (int depth in new[] { fullDepth, fullDepth + 1, 200 })
            {
                if (!ExactValue.IsZero(AmbienceResolution.DepthDuck(depth, fullDepth, taper)))
                    return FailSound(scenario, $"depth {depth} left the surface beds audible underground.");
            }

            // Monotonic, and strictly decreasing somewhere inside the taper — a constant 1 that snaps to 0
            // at the boundary would satisfy every check above while being the hard cut this taper replaces.
            float previous = 1f;
            bool decreased = false;
            for (int depth = 0; depth <= fullDepth; depth++)
            {
                float duck = AmbienceResolution.DepthDuck(depth, fullDepth, taper);
                if (duck > previous) return FailSound(scenario, $"the duck rose at depth {depth}.");
                if (duck < previous) decreased = true;
                previous = duck;
            }

            if (!decreased) return FailSound(scenario, "the duck never eased — the taper is not doing anything.");

            // Halfway through the taper should be halfway down, or the fade is not linear in depth.
            float midpoint = AmbienceResolution.DepthDuck(fullDepth - taper / 2, fullDepth, taper);
            if (Mathf.Abs(midpoint - 0.5f) > AMBIENCE_EPSILON)
                return FailSound(scenario, $"the taper midpoint was {midpoint}, not 0.5.");

            // A zero taper is the authored hard gate: present right up to the depth, silent at it.
            if (!ExactValue.Equal(AmbienceResolution.DepthDuck(fullDepth - 1, fullDepth, 0), 1f))
                return FailSound(scenario, "a zero taper faded early.");
            if (!ExactValue.IsZero(AmbienceResolution.DepthDuck(fullDepth, fullDepth, 0)))
                return FailSound(scenario, "a zero taper did not cut off at its depth.");

            // A zero depth disables the gate rather than silencing everything at ground level.
            if (!ExactValue.Equal(AmbienceResolution.DepthDuck(500, 0, taper), 1f))
                return FailSound(scenario, "a zero full-duck depth silenced the beds instead of disabling the gate.");

            return true;
        }

        /// <summary>
        /// The submersion cutoff sweep: endpoints, monotonic descent, and the log-space midpoint. A linear
        /// interpolation passes the endpoints and puts the midpoint an octave and a half too high.
        /// </summary>
        private static bool RunLowPassSweep()
        {
            const string scenario = "Submersion Cutoff Sweeps Monotonically In Log Space";
            const float dry = 22000f;
            const float wet = 900f;

            if (Mathf.Abs(AmbienceResolution.LowPassCutoff(dry, wet, 0f) - dry) > 1f)
                return FailSound(scenario, "a dry listener did not sit at the dry cutoff.");
            if (Mathf.Abs(AmbienceResolution.LowPassCutoff(dry, wet, 1f) - wet) > 1f)
                return FailSound(scenario, "a fully submerged listener did not reach the wet cutoff.");

            float geometricMean = Mathf.Sqrt(dry * wet);
            if (Mathf.Abs(AmbienceResolution.LowPassCutoff(dry, wet, 0.5f) - geometricMean) > 1f)
                return FailSound(scenario, "the midpoint was not the geometric mean — the sweep is linear, not log.");

            float previous = float.MaxValue;
            for (int i = 0; i <= AMBIENCE_SWEEP_STEPS; i++)
            {
                float t = i / (float)AMBIENCE_SWEEP_STEPS;
                float cutoff = AmbienceResolution.LowPassCutoff(dry, wet, t);
                if (cutoff > previous) return FailSound(scenario, $"the cutoff rose at t={t:0.000}.");
                previous = cutoff;
            }

            if (Mathf.Abs(AmbienceResolution.LowPassCutoff(dry, wet, 4f) - wet) > 1f)
                return FailSound(scenario, "a weight above 1 was not clamped.");

            return true;
        }

        /// <summary>The scheduler's silence gap, including a pair authored the wrong way round.</summary>
        private static bool RunMusicGap()
        {
            const string scenario = "Music Gap Stays Inside Its Authored Bounds";
            const float min = 180f;
            const float max = 480f;

            float lowest = float.MaxValue;
            float highest = float.MinValue;

            for (uint salt = 1; salt <= AMBIENCE_SWEEP_STEPS; salt++)
            {
                float gap = AmbienceResolution.NextGapSeconds(min, max, AmbienceResolution.ScheduleHash(salt));
                if (gap < min || gap > max)
                    return FailSound(scenario, $"salt {salt} produced a gap of {gap}s, outside [{min}, {max}].");

                lowest = Mathf.Min(lowest, gap);
                highest = Mathf.Max(highest, gap);
            }

            // A hash that only ever lands in one corner would satisfy the bounds check above while making
            // every gap identical, which is the failure the randomization exists to prevent.
            if (highest - lowest < (max - min) * 0.5f)
                return FailSound(scenario, $"gaps spanned only {highest - lowest:0.0}s of the {max - min:0.0}s range.");

            if (AmbienceResolution.NextGapSeconds(max, min, 0u) < min)
                return FailSound(scenario, "inverted bounds were not ordered before interpolating.");
            // Exact: a degenerate range must return its bound untouched, not interpolate to near it.
            if (!ExactValue.Equal(AmbienceResolution.NextGapSeconds(90f, 90f, 12345u), 90f))
                return FailSound(scenario, "a degenerate range did not return its single value.");

            return true;
        }

        /// <summary>
        /// Builds an <see cref="AudioContext"/> carrying a weighted biome neighborhood.
        /// </summary>
        /// <param name="indices">Biome indices, nearest first.</param>
        /// <param name="weights">Their weights, index-aligned.</param>
        /// <returns>A context with weights populated and no other signal set.</returns>
        private static AudioContext WeightedContext(int[] indices, float[] weights)
        {
            BiomeWeights biomeWeights = new BiomeWeights { Count = indices.Length };
            for (int i = 0; i < indices.Length; i++)
            {
                biomeWeights.Indices[i] = indices[i];
                biomeWeights.Weights[i] = weights[i];
            }

            return new AudioContext(indices[0], null, true, 15, false, biomeWeights, true);
        }

        /// <summary>
        /// Builds a biome list whose entries carry the given beds.
        /// </summary>
        /// <param name="loops">One bed per biome; a null entry means that biome authors none.</param>
        /// <returns>Biome assets to be destroyed by the caller.</returns>
        /// <remarks>
        /// Typed as <see cref="BiomeBase"/> rather than the concrete biome it instantiates: that is the type
        /// the resolver takes, and handing it a derived array would be a covariant conversion — safe only for
        /// as long as nothing writes through it, which is not a property a fixture should rely on.
        /// </remarks>
        private static BiomeBase[] BiomesWithLoops(AudioClip[] loops)
        {
            BiomeBase[] biomes = new BiomeBase[loops.Length];
            for (int i = 0; i < loops.Length; i++)
            {
                StandardBiomeAttributes biome = ScriptableObject.CreateInstance<StandardBiomeAttributes>();
                biome.ambientLoop = loops[i];
                biomes[i] = biome;
            }

            return biomes;
        }

        /// <summary>
        /// The mix a weighted neighborhood resolves to: one entry per contributing biome, sub-threshold
        /// contributors dropped, and the survivors renormalized so dropping one does not duck the rest.
        /// </summary>
        private static bool RunBedMix()
        {
            const string scenario = "Bed Mix Weights Every Nearby Biome And Normalizes";

            AudioClip[] loops = MakeClips(3);
            AudioClip fallback = AudioClip.Create("ValidationBedFallback", 16, 1, 8000, false);
            BiomeBase[] biomes = BiomesWithLoops(loops);

            AudioClip[] clips = new AudioClip[BiomeWeights.MaxBiomes];
            float[] weights = new float[BiomeWeights.MaxBiomes];

            try
            {
                AudioContext shoreline = WeightedContext(new[] { 0, 1 }, new[] { 0.7f, 0.3f });
                int count = AmbienceResolution.ResolveBedMix(shoreline, biomes, fallback, 0.05f, clips, weights);

                if (count != 2) return FailSound(scenario, $"a two-biome column produced {count} beds, not 2.");
                if (clips[0] != loops[0] || clips[1] != loops[1])
                    return FailSound(scenario, "the mix did not carry each biome's own bed.");
                if (Mathf.Abs(weights[0] - 0.7f) > AMBIENCE_EPSILON || Mathf.Abs(weights[1] - 0.3f) > AMBIENCE_EPSILON)
                    return FailSound(scenario, $"weights came through as ({weights[0]}, {weights[1]}).");

                // A 2% neighbor is dropped — and the remaining two must be scaled back up to fill the mix,
                // or every bed quietly ducks by the amount that was discarded.
                AudioContext withSliver = WeightedContext(new[] { 0, 1, 2 }, new[] { 0.6f, 0.38f, 0.02f });
                count = AmbienceResolution.ResolveBedMix(withSliver, biomes, fallback, 0.05f, clips, weights);

                if (count != 2) return FailSound(scenario, $"the sub-threshold biome was not dropped ({count} beds).");

                float sum = weights[0] + weights[1];
                if (Mathf.Abs(sum - 1f) > AMBIENCE_EPSILON)
                    return FailSound(scenario, $"after dropping a contributor the mix summed to {sum}, not 1.");

                // A biome with no bed of its own still sounds — as the fallback, never as silence.
                BiomeBase[] bare = BiomesWithLoops(new[] { null, loops[1] });
                try
                {
                    count = AmbienceResolution.ResolveBedMix(
                        WeightedContext(new[] { 0, 1 }, new[] { 0.5f, 0.5f }), bare, fallback, 0.05f, clips, weights);

                    if (count != 2) return FailSound(scenario, $"an unauthored biome bed produced {count} entries.");
                    if (clips[0] != fallback) return FailSound(scenario, "an unauthored biome bed did not fall back.");
                }
                finally
                {
                    foreach (BiomeBase biome in bare) Object.DestroyImmediate(biome);
                }

                // No weighted answer at all (the legacy generator) must still produce a bed.
                AudioContext unweighted = new AudioContext(-1, null, false, 15, false);
                count = AmbienceResolution.ResolveBedMix(unweighted, biomes, fallback, 0.05f, clips, weights);
                if (count != 1 || clips[0] != fallback)
                    return FailSound(scenario, "a world with no weighted query did not fall back to one bed.");

                return true;
            }
            finally
            {
                foreach (BiomeBase biome in biomes) Object.DestroyImmediate(biome);
            }
        }

        /// <summary>
        /// Two biomes resolving to the same clip must share one source. Playing one loop on two sources
        /// flanges instead of layering — the same rule footstep material resolution already applies.
        /// </summary>
        private static bool RunBedMixMerge()
        {
            const string scenario = "Beds Sharing A Clip Merge Onto One Source";

            AudioClip shared = AudioClip.Create("ValidationSharedBed", 16, 1, 8000, false);
            AudioClip other = AudioClip.Create("ValidationOtherBed", 16, 1, 8000, false);
            AudioClip fallback = AudioClip.Create("ValidationMergeFallback", 16, 1, 8000, false);

            BiomeBase[] biomes = BiomesWithLoops(new[] { shared, shared, other });

            AudioClip[] clips = new AudioClip[BiomeWeights.MaxBiomes];
            float[] weights = new float[BiomeWeights.MaxBiomes];

            try
            {
                AudioContext context = WeightedContext(new[] { 0, 1, 2 }, new[] { 0.4f, 0.35f, 0.25f });
                int count = AmbienceResolution.ResolveBedMix(context, biomes, fallback, 0.01f, clips, weights);

                if (count != 2)
                    return FailSound(scenario, $"three biomes over two clips produced {count} entries, not 2.");
                if (clips[0] != shared || clips[1] != other)
                    return FailSound(scenario, "the merged entry did not keep the shared clip first.");
                if (Mathf.Abs(weights[0] - 0.75f) > AMBIENCE_EPSILON)
                    return FailSound(scenario, $"the merged weight was {weights[0]}, not the summed 0.75.");
                if (Mathf.Abs(weights[0] + weights[1] - 1f) > AMBIENCE_EPSILON)
                    return FailSound(scenario, "the merged mix did not normalize.");

                // Two biomes that both fall back are the same case arriving by a different route.
                BiomeBase[] bothBare = BiomesWithLoops(new AudioClip[] { null, null });
                try
                {
                    count = AmbienceResolution.ResolveBedMix(
                        WeightedContext(new[] { 0, 1 }, new[] { 0.5f, 0.5f }), bothBare, fallback, 0.01f,
                        clips, weights);

                    if (count != 1)
                        return FailSound(scenario, $"two biomes both falling back produced {count} sources, not 1.");
                    if (Mathf.Abs(weights[0] - 1f) > AMBIENCE_EPSILON)
                        return FailSound(scenario, $"the merged fallback weight was {weights[0]}, not 1.");
                }
                finally
                {
                    foreach (BiomeBase biome in bothBare) Object.DestroyImmediate(biome);
                }

                return true;
            }
            finally
            {
                foreach (BiomeBase biome in biomes) Object.DestroyImmediate(biome);
            }
        }

        /// <summary>
        /// The rest cycle: it alternates, every stretch lands inside its authored bounds, and it does not
        /// flip on a tick that has time left.
        /// </summary>
        private static bool RunRestCycle()
        {
            const string scenario = "Ambience Rest Cycle Alternates Inside Its Authored Bounds";

            const float minAudible = 45f;
            const float maxAudible = 120f;
            const float minRest = 20f;
            const float maxRest = 60f;
            const float step = 0.5f;

            bool audible = true;
            float remaining = minAudible;

            int flips = 0;
            float elapsedInStretch = 0f;
            uint salt = 0;

            for (int tick = 0; tick < 4000; tick++)
            {
                bool before = audible;
                elapsedInStretch += step;

                audible = AmbienceResolution.TickRestCycle(
                    audible, step, minAudible, maxAudible, minRest, maxRest,
                    AmbienceResolution.ScheduleHash(++salt), ref remaining);

                if (audible == before) continue;

                flips++;

                // `before` is the stretch that just ended, so its bounds are the ones to check.
                float low = before ? minAudible : minRest;
                float high = before ? maxAudible : maxRest;

                if (elapsedInStretch < low - step || elapsedInStretch > high + step)
                {
                    return FailSound(scenario,
                        $"a {(before ? "audible" : "rest")} stretch ran {elapsedInStretch:0.0}s, outside [{low}, {high}].");
                }

                elapsedInStretch = 0f;
            }

            if (flips < 4)
                return FailSound(scenario, $"only {flips} transitions in 2000s — the cycle is not alternating.");

            // A tick with time left must not move the state, or the stretch bounds mean nothing.
            float held = 10f;
            if (!AmbienceResolution.TickRestCycle(true, 0.5f, minAudible, maxAudible, minRest, maxRest, 7u, ref held))
                return FailSound(scenario, "flipped while the current stretch still had time left.");
            if (Mathf.Abs(held - 9.5f) > AMBIENCE_EPSILON)
                return FailSound(scenario, $"the remaining time went to {held} instead of 9.5.");

            return true;
        }

        /// <summary>Track selection: always in range, never an immediate repeat, and empty pools say so.</summary>
        private static bool RunTrackPick()
        {
            const string scenario = "Music Never Picks The Same Track Twice Running";

            if (AmbienceResolution.PickTrackIndex(null, null, 0u) != -1)
                return FailSound(scenario, "a null pool did not report -1.");
            if (AmbienceResolution.PickTrackIndex(System.Array.Empty<AudioClip>(), null, 0u) != -1)
                return FailSound(scenario, "an empty pool did not report -1.");

            AudioClip[] single = MakeClips(1);
            if (AmbienceResolution.PickTrackIndex(single, single[0], 0u) != 0)
                return FailSound(scenario, "a single-track pool did not return its only track.");

            for (int count = 2; count <= 8; count++)
            {
                AudioClip[] pool = MakeClips(count);
                AudioClip last = null;

                for (uint salt = 1; salt <= AMBIENCE_SWEEP_STEPS; salt++)
                {
                    int index = AmbienceResolution.PickTrackIndex(pool, last, AmbienceResolution.ScheduleHash(salt));
                    if ((uint)index >= (uint)count)
                        return FailSound(scenario, $"pool of {count} produced index {index}.");
                    if (pool[index] == last)
                        return FailSound(scenario, $"pool of {count} repeated track {index} back to back.");
                    last = pool[index];
                }
            }

            // The guard must survive the pool changing underneath it, which is the normal case: the pool is
            // re-resolved at every pick and follows the biome. An index-based guard compares a position in the
            // old pool against whatever track now sits at that position in the new one.
            AudioClip[] poolA = MakeClips(4);
            AudioClip[] poolB = { MakeClips(1)[0], poolA[2], MakeClips(1)[0], MakeClips(1)[0] };

            for (uint salt = 1; salt <= AMBIENCE_SWEEP_STEPS; salt++)
            {
                int carried = AmbienceResolution.PickTrackIndex(poolB, poolA[2], AmbienceResolution.ScheduleHash(salt));
                if (poolB[carried] == poolA[2])
                    return FailSound(scenario,
                        "repeated the previous track after the pool changed — the guard compares positions, not clips.");
            }

            return true;
        }
    }
}
