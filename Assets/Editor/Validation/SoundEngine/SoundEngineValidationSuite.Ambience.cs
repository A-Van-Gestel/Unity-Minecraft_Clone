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

        /// <summary>Bottom of the altitude band a fixture track spans when the scenario does not test bands.</summary>
        private const float TRACK_BAND_LOW = -1024f;

        /// <inheritdoc cref="TRACK_BAND_LOW"/>
        private const float TRACK_BAND_HIGH = 1024f;

        /// <summary>Rolls the distribution scenario draws before comparing frequencies against the weights.</summary>
        private const int TRACK_DISTRIBUTION_ROLLS = 4000;

        /// <summary>
        /// The per-track gain the S7 fixtures author. Deliberately neither 0 nor 1: an identity value would
        /// pass a chain that dropped the term entirely, which is the one defect these scenarios exist for.
        /// </summary>
        private const float FIXTURE_TRACK_VOLUME = 0.4f;

        /// <inheritdoc cref="FIXTURE_TRACK_VOLUME"/>
        private const float FIXTURE_OTHER_VOLUME = 0.8f;

        static partial void AddAmbienceScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario("Cave Dwell Holds A Reading Before Committing It", RunCaveDwell));
            scenarios.Add(new Scenario("Underground Test Includes Its Threshold Level", RunUndergroundThreshold));
            scenarios.Add(new Scenario("A Head In A Fluid Cell Reads As Submerged", RunSubmergedTest));
            scenarios.Add(new Scenario("Ambience Falls Back When The Biome Authors No Bed", RunBedFallback));
            scenarios.Add(new Scenario("Complementary Bed Fades Hold Constant Power", RunBedGainCurve));
            scenarios.Add(new Scenario("Bed Fade Advances At The Authored Rate And Clamps", RunAdvanceFade));
            scenarios.Add(new Scenario("A Returning Bed Reclaims Its Own Still-Audible Source", RunBedSlotReclaim));
            scenarios.Add(new Scenario("A New Bed Takes A Silent Source Before The Quietest Audible One",
                RunBedSlotPreference));
            scenarios.Add(new Scenario("Every Bed In One Mix Gets Its Own Source", RunBedMixSlotAssignment));
            scenarios.Add(new Scenario("Cave Bed Ducks The Biome Bed By Its Authored Amount", RunBiomeDuck));
            scenarios.Add(new Scenario("Depth Below The Surface Silences The Biome Beds", RunDepthDuck));
            scenarios.Add(new Scenario("Submersion Cutoff Sweeps Monotonically In Log Space", RunLowPassSweep));
            scenarios.Add(new Scenario("Music Gap Stays Inside Its Authored Bounds", RunMusicGap));
            scenarios.Add(new Scenario("Bed Mix Weights Every Nearby Biome And Normalizes", RunBedMix));
            scenarios.Add(new Scenario("A Mix Whose Contributors All Fall Under The Threshold Still Sounds",
                RunBedMixAllSubThreshold));
            scenarios.Add(new Scenario("Beds Sharing A Clip Merge Onto One Source", RunBedMixMerge));
            scenarios.Add(new Scenario("Ambience Rest Cycle Alternates Inside Its Authored Bounds", RunRestCycle));
            scenarios.Add(new Scenario("Bed Bearings Survive The Mix And Merge By Weight", RunBedBearings));
            scenarios.Add(new Scenario("A Track Outside Its Altitude Band Is Never Selected", RunTrackBand));
            scenarios.Add(new Scenario("Track Play Chance Spreads Across The Eligible Set In Proportion",
                RunTrackDistribution));
            scenarios.Add(new Scenario("Bed Source Volume Folds In Every Gain That Governs A Bed",
                RunBedSourceVolume));
            scenarios.Add(new Scenario("An Unauthored Track Volume Plays At Full Level", RunTrackVolumeDefault));
            scenarios.Add(new Scenario("Bed Mix Carries Each Track's Volume And Merges Them By Weight",
                RunBedMixVolumes));
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
                authored.ambientTracks = new[] { Track(biomeLoop, TRACK_BAND_LOW, TRACK_BAND_HIGH, 1f) };

                if (AmbienceResolution.SelectBiomeLoop(new AudioContext(0, authored, true, 15, false), fallback, 0u) != biomeLoop)
                    return FailSound(scenario, "an authored biome bed was not selected.");

                if (AmbienceResolution.SelectBiomeLoop(new AudioContext(0, bare, true, 15, false), fallback, 0u) != fallback)
                    return FailSound(scenario, "a biome with no bed did not fall back to the default.");

                if (AmbienceResolution.SelectBiomeLoop(new AudioContext(0, null, true, 15, false), fallback, 0u) != fallback)
                    return FailSound(scenario, "a null biome asset did not fall back to the default.");

                // The legacy generator answers no biome for a whole session: this must be the fallback bed,
                // not silence, or that world type loses its ambience entirely and reports nothing.
                if (AmbienceResolution.SelectBiomeLoop(new AudioContext(-1, authored, false, 15, false), fallback, 0u) != fallback)
                    return FailSound(scenario, "a world with no biome answer did not fall back to the default.");

                if (AmbienceResolution.SelectBiomeLoop(new AudioContext(0, bare, true, 15, false), null, 0u) != null)
                    return FailSound(scenario, "an unauthored fallback resolved to something other than null.");

                // Since §11 there is a fourth hole with the same consequence: a biome whose tracks are all
                // authored for other altitudes offers nothing *here*, and that must read as the fallback
                // rather than as a bed the layer forgot to start.
                StandardBiomeAttributes outOfBand = ScriptableObject.CreateInstance<StandardBiomeAttributes>();
                try
                {
                    outOfBand.ambientTracks = new[] { Track(biomeLoop, 200f, 300f, 1f) };

                    AudioContext atSeaLevel = new AudioContext(0, outOfBand, true, 15, false,
                        default, false, 0, 64);
                    if (AmbienceResolution.SelectBiomeLoop(atSeaLevel, fallback, 0u) != fallback)
                        return FailSound(scenario, "a biome with no track in band did not fall back.");

                    AudioContext inBand = new AudioContext(0, outOfBand, true, 15, false,
                        default, false, 0, 250);
                    if (AmbienceResolution.SelectBiomeLoop(inBand, fallback, 0u) != biomeLoop)
                        return FailSound(scenario, "a track inside its band was not selected.");
                }
                finally
                {
                    Object.DestroyImmediate(outOfBand);
                }

                return true;
            }
            finally
            {
                Object.DestroyImmediate(authored);
                Object.DestroyImmediate(bare);
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

        /// <summary>
        /// A whole mix assigned at once: no two entries may land on the same source, and an entry must not
        /// take a source another entry is already carrying its own clip on.
        /// </summary>
        /// <remarks>
        /// Entered on <b>tied</b> fades because that is the state the layer wakes from a rest stretch in —
        /// every source released, so every fade reads zero. Resolving the mix one clip at a time answers the
        /// same source for all of them there, since claiming zeroes the fade of what it just claimed.
        /// </remarks>
        private static bool RunBedMixSlotAssignment()
        {
            const string scenario = "Every Bed In One Mix Gets Its Own Source";

            AudioClip[] clips = MakeClips(5);
            int[] slots = new int[4];

            AudioClip[] free = { null, null, null, null };
            float[] tied = { 0f, 0f, 0f, 0f };
            AudioClip[] mix = { clips[0], clips[1], clips[2], clips[3] };

            int assigned = AmbienceResolution.AssignBedSlots(free, tied, mix, 4, slots);
            if (assigned != 4) return FailSound(scenario, $"a four-bed mix on a free roster assigned {assigned} sources.");
            if (!SlotsAreDistinct(slots, 4))
                return FailSound(scenario,
                    $"entries collapsed onto shared sources ({slots[0]}, {slots[1]}, {slots[2]}, {slots[3]}).");

            // Reclaim beats novelty: a fresh clip must not evict the source a later entry resumes on.
            AudioClip[] carrying = { clips[0], clips[1], null, null };
            float[] audible = { 0.5f, 0.5f, 0f, 0f };
            AudioClip[] arriving = { clips[4], clips[1] };

            assigned = AmbienceResolution.AssignBedSlots(carrying, audible, arriving, 2, slots);
            if (assigned != 2) return FailSound(scenario, $"a two-bed mix assigned {assigned} sources.");
            if (slots[1] != 1)
                return FailSound(scenario, $"the returning bed was moved off its own source to slot {slots[1]}.");
            if (slots[0] == 1)
                return FailSound(scenario, "an arriving bed evicted the source a returning bed was carrying.");

            // Every source audible and every entry new: still one source each, never a shared one.
            AudioClip[] busy = { clips[0], clips[1], clips[2], clips[3] };
            float[] busyFades = { 0.9f, 0.2f, 0.75f, 0.5f };
            AudioClip[] replacements = { MakeClips(1)[0], MakeClips(1)[0], MakeClips(1)[0], MakeClips(1)[0] };

            assigned = AmbienceResolution.AssignBedSlots(busy, busyFades, replacements, 4, slots);
            if (assigned != 4 || !SlotsAreDistinct(slots, 4))
                return FailSound(scenario, "a full handover did not give every arriving bed its own source.");

            if (AmbienceResolution.AssignBedSlots(null, tied, mix, 4, slots) != 0 ||
                AmbienceResolution.AssignBedSlots(free, null, mix, 4, slots) != 0 ||
                AmbienceResolution.AssignBedSlots(free, tied, null, 4, slots) != 0 ||
                AmbienceResolution.AssignBedSlots(free, tied, mix, 4, null) != 0)
            {
                return FailSound(scenario, "a null argument assigned sources instead of reporting none.");
            }

            return true;
        }

        /// <summary>Whether the leading entries of a slot assignment are all distinct and all resolved.</summary>
        /// <param name="slots">The assignment to check.</param>
        /// <param name="count">How many leading entries are in the mix.</param>
        /// <returns>True when every entry got its own source.</returns>
        private static bool SlotsAreDistinct(int[] slots, int count)
        {
            for (int i = 0; i < count; i++)
            {
                if (slots[i] < 0) return false;

                for (int j = i + 1; j < count; j++)
                {
                    if (slots[i] == slots[j]) return false;
                }
            }

            return true;
        }

        /// <summary>
        /// A threshold that drops every contributor must reach the fallback, not silence.
        /// </summary>
        /// <remarks>
        /// Reachable from authored values alone: the weight floor ranges to 0.5, and an evenly-split border
        /// hands it two contributors of exactly 0.5. Falling to zero beds there leaves the biome layer
        /// silent with nothing fading in behind it — the one outcome the mix is documented never to produce.
        /// </remarks>
        private static bool RunBedMixAllSubThreshold()
        {
            const string scenario = "A Mix Whose Contributors All Fall Under The Threshold Still Sounds";

            AudioClip[] loops = MakeClips(2);
            AudioClip fallback = AudioClip.Create("ValidationSubThresholdFallback", 16, 1, 8000, false);
            BiomeBase[] biomes = BiomesWithLoops(loops);

            AudioClip[] clips = new AudioClip[BiomeWeights.MaxBiomes];
            float[] weights = new float[BiomeWeights.MaxBiomes];

            try
            {
                AudioContext border = WeightedContext(new[] { 0, 1 }, new[] { 0.5f, 0.5f });
                int count = AmbienceResolution.ResolveBedMix(border, biomes, fallback, 0.5f, 0u, clips, weights);

                if (count != 1)
                    return FailSound(scenario, $"an evenly-split border under a 0.5 floor produced {count} beds, not 1.");
                if (clips[0] != fallback)
                    return FailSound(scenario, "the surviving bed was not the fallback.");
                if (Mathf.Abs(weights[0] - 1f) > AMBIENCE_EPSILON)
                    return FailSound(scenario, $"the fallback bed came through at weight {weights[0]}, not 1.");

                // A four-way corner under a lower floor is the same hole reached from ordinary authoring.
                AudioContext corner = WeightedContext(new[] { 0, 1 }, new[] { 0.25f, 0.25f });
                if (AmbienceResolution.ResolveBedMix(corner, biomes, fallback, 0.3f, 0u, clips, weights) != 1)
                    return FailSound(scenario, "a corner under a 0.3 floor did not fall back to one bed.");

                return true;
            }
            finally
            {
                foreach (BiomeBase biome in biomes) Object.DestroyImmediate(biome);
            }
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
        /// Bed bearings through the mix (§10): each entry keeps its biome's direction, a merged entry
        /// carries the weighted mean of the directions that merged into it, and an entry with no direction
        /// says so rather than guessing one.
        /// </summary>
        /// <remarks>
        /// The mean is the part worth pinning. Two biomes sharing a clip already collapse onto one source,
        /// and that source has to be placed <i>somewhere</i> — averaging by weight puts it between them, in
        /// proportion. The degenerate case matters just as much: contributors on opposite sides cancel to
        /// nothing, and "nothing" has to survive as "play this flat" rather than being normalized into an
        /// arbitrary heading.
        /// </remarks>
        private static bool RunBedBearings()
        {
            const string scenario = "Bed Bearings Survive The Mix And Merge By Weight";

            AudioClip[] loops = MakeClips(3);
            AudioClip shared = AudioClip.Create("ValidationSharedBearingBed", 16, 1, 8000, false);
            AudioClip fallback = AudioClip.Create("ValidationBearingFallback", 16, 1, 8000, false);

            AudioClip[] clips = new AudioClip[BiomeWeights.MaxBiomes];
            float[] weights = new float[BiomeWeights.MaxBiomes];
            Vector2[] directions = new Vector2[BiomeWeights.MaxBiomes];

            BiomeBase[] distinct = BiomesWithLoops(new[] { loops[0], loops[1] });
            try
            {
                // Distinct clips: each entry keeps its own biome's bearing, untouched.
                AudioContext twoWays = DirectedContext(
                    new[] { 0, 1 }, new[] { 0.6f, 0.4f },
                    new[] { new Vector2(100f, 0f), new Vector2(0f, -50f) });

                int count = AmbienceResolution.ResolveBedMix(
                    twoWays, distinct, fallback, 0.01f, 0u, clips, weights, directions);

                if (count != 2) return FailSound(scenario, $"two distinct beds produced {count} entries.");
                if ((directions[0] - new Vector2(100f, 0f)).magnitude > AMBIENCE_EPSILON)
                    return FailSound(scenario, $"the first bearing came through as {directions[0]}.");
                if ((directions[1] - new Vector2(0f, -50f)).magnitude > AMBIENCE_EPSILON)
                    return FailSound(scenario, $"the second bearing came through as {directions[1]}.");
            }
            finally
            {
                foreach (BiomeBase biome in distinct) Object.DestroyImmediate(biome);
            }

            BiomeBase[] merging = BiomesWithLoops(new[] { shared, shared });
            try
            {
                // Merged: 75% of the weight lies due east at 100, 25% due north at 200. The mean is the
                // weighted average of the two vectors, not of their lengths and not the nearer one.
                AudioContext merged = DirectedContext(
                    new[] { 0, 1 }, new[] { 0.75f, 0.25f },
                    new[] { new Vector2(100f, 0f), new Vector2(0f, 200f) });

                int count = AmbienceResolution.ResolveBedMix(
                    merged, merging, fallback, 0.01f, 0u, clips, weights, directions);

                if (count != 1) return FailSound(scenario, $"one shared clip produced {count} entries.");

                Vector2 expected = new Vector2(0.75f * 100f, 0.25f * 200f);
                if ((directions[0] - expected).magnitude > 0.01f)
                    return FailSound(scenario, $"the merged bearing was {directions[0]}, not the weighted mean {expected}.");

                // Opposed contributors cancel: the clip genuinely is not coming from anywhere.
                AudioContext opposed = DirectedContext(
                    new[] { 0, 1 }, new[] { 0.5f, 0.5f },
                    new[] { new Vector2(80f, 0f), new Vector2(-80f, 0f) });

                count = AmbienceResolution.ResolveBedMix(
                    opposed, merging, fallback, 0.01f, 0u, clips, weights, directions);

                if (count != 1) return FailSound(scenario, $"opposed contributors produced {count} entries.");
                if (directions[0].magnitude > AMBIENCE_EPSILON)
                    return FailSound(scenario, $"opposed bearings left {directions[0]} instead of cancelling.");
            }
            finally
            {
                foreach (BiomeBase biome in merging) Object.DestroyImmediate(biome);
            }

            // The unweighted world (the legacy generator) has one bed and no bearing at all for it.
            AudioContext unweighted = new AudioContext(-1, null, false, 15, false);
            int single = AmbienceResolution.ResolveBedMix(
                unweighted, null, fallback, 0.01f, 0u, clips, weights, directions);

            if (single != 1) return FailSound(scenario, $"the unweighted fallback produced {single} entries.");
            if (directions[0].magnitude > AMBIENCE_EPSILON)
                return FailSound(scenario, $"the fallback bed claimed a bearing of {directions[0]}.");

            return true;
        }

        /// <summary>
        /// The altitude band gate (§11): a track is eligible only inside its own band, at both ends of it,
        /// and no roll of the dice can reach one that is out of band.
        /// </summary>
        /// <remarks>
        /// Swept across every salt rather than checked at one, because the failure this guards is a bounds
        /// test that is *usually* right — an exclusive comparison at a boundary, or a filter applied to the
        /// weight sum but not to the pick, both leave a gate that holds for most inputs and leaks for a few.
        /// </remarks>
        private static bool RunTrackBand()
        {
            const string scenario = "A Track Outside Its Altitude Band Is Never Selected";

            AudioClip low = AudioClip.Create("ValidationLowBed", 16, 1, 8000, false);
            AudioClip high = AudioClip.Create("ValidationHighBed", 16, 1, 8000, false);

            AmbienceTrack[] tracks =
            {
                Track(low, 0f, 100f, 1f),
                Track(high, 101f, 200f, 1f),
            };

            for (uint salt = 0; salt < 256; salt++)
            {
                uint hash = AmbienceResolution.TrackHash(salt, 0);

                for (int y = -20; y <= 220; y++)
                {
                    int picked = AmbienceResolution.SelectTrackIndex(tracks, y, hash);

                    bool lowEligible = y >= 0 && y <= 100;
                    bool highEligible = y >= 101 && y <= 200;

                    if (!lowEligible && !highEligible)
                    {
                        if (picked != -1)
                            return FailSound(scenario, $"y={y} is outside every band but selected track {picked}.");
                        continue;
                    }

                    if (picked < 0)
                        return FailSound(scenario, $"y={y} had an eligible track but selected none.");
                    if (lowEligible && picked != 0)
                        return FailSound(scenario, $"y={y} is only in the low band but selected track {picked}.");
                    if (highEligible && picked != 1)
                        return FailSound(scenario, $"y={y} is only in the high band but selected track {picked}.");
                }
            }

            // A band authored with its ends inverted describes the same span, not an empty one.
            AmbienceTrack[] inverted = { Track(low, 100f, 0f, 1f) };
            if (AmbienceResolution.SelectTrackIndex(inverted, 50, AmbienceResolution.TrackHash(0, 0)) != 0)
                return FailSound(scenario, "an inverted band excluded an altitude inside it.");

            // A track with no clip is not a track, however wide its band.
            AmbienceTrack[] clipless = { Track(null, TRACK_BAND_LOW, TRACK_BAND_HIGH, 1f) };
            if (AmbienceResolution.SelectTrackIndex(clipless, 0, AmbienceResolution.TrackHash(0, 0)) != -1)
                return FailSound(scenario, "a track with no clip was selected.");

            return true;
        }

        /// <summary>
        /// The play-chance distribution (§11): over many rolls each eligible track must surface in rough
        /// proportion to its weight.
        /// </summary>
        /// <remarks>
        /// A spread assertion, not a bounds check. "The pick is always a valid index" is satisfied by a
        /// generator that returns the same index every time — which is precisely the bug worth catching,
        /// since a bed that never varies is the complaint §11 exists to answer. Asserting proportion also
        /// catches the milder version: a roulette whose cursor arithmetic favors the first entry.
        /// </remarks>
        private static bool RunTrackDistribution()
        {
            const string scenario = "Track Play Chance Spreads Across The Eligible Set In Proportion";
            const float tolerance = 0.04f;

            AudioClip[] clips = MakeClips(3);
            AmbienceTrack[] tracks =
            {
                Track(clips[0], TRACK_BAND_LOW, TRACK_BAND_HIGH, 6f),
                Track(clips[1], TRACK_BAND_LOW, TRACK_BAND_HIGH, 3f),
                Track(clips[2], TRACK_BAND_LOW, TRACK_BAND_HIGH, 1f),
            };

            float[] expected = { 0.6f, 0.3f, 0.1f };
            int[] hits = new int[tracks.Length];

            for (uint salt = 0; salt < TRACK_DISTRIBUTION_ROLLS; salt++)
            {
                int picked = AmbienceResolution.SelectTrackIndex(tracks, 0, AmbienceResolution.TrackHash(salt, 0));
                if ((uint)picked >= (uint)tracks.Length)
                    return FailSound(scenario, $"salt {salt} selected {picked}, outside the track list.");

                hits[picked]++;
            }

            for (int i = 0; i < tracks.Length; i++)
            {
                if (hits[i] == 0)
                    return FailSound(scenario, $"track {i} never surfaced in {TRACK_DISTRIBUTION_ROLLS} rolls.");

                float share = hits[i] / (float)TRACK_DISTRIBUTION_ROLLS;
                if (Mathf.Abs(share - expected[i]) > tolerance)
                    return FailSound(scenario,
                        $"track {i} surfaced {share:0.###} of the time, not its authored {expected[i]:0.###}.");
            }

            // All-zero weights are an author saying nothing about proportion, not asking for silence.
            AmbienceTrack[] unweighted =
            {
                Track(clips[0], TRACK_BAND_LOW, TRACK_BAND_HIGH, 0f),
                Track(clips[1], TRACK_BAND_LOW, TRACK_BAND_HIGH, 0f),
            };

            bool sawFirst = false;
            bool sawSecond = false;
            for (uint salt = 0; salt < TRACK_DISTRIBUTION_ROLLS; salt++)
            {
                int picked = AmbienceResolution.SelectTrackIndex(unweighted, 0, AmbienceResolution.TrackHash(salt, 1));
                if (picked < 0) return FailSound(scenario, "an all-zero-weight pool selected nothing.");

                sawFirst |= picked == 0;
                sawSecond |= picked == 1;
            }

            if (!sawFirst || !sawSecond)
                return FailSound(scenario, "an all-zero-weight pool did not spread across both tracks.");

            // Two biomes must not roll in lockstep, or a shoreline changes both its beds in one breath.
            int agreements = 0;
            for (uint salt = 0; salt < TRACK_DISTRIBUTION_ROLLS; salt++)
            {
                int a = AmbienceResolution.SelectTrackIndex(tracks, 0, AmbienceResolution.TrackHash(salt, 0));
                int b = AmbienceResolution.SelectTrackIndex(tracks, 0, AmbienceResolution.TrackHash(salt, 1));
                if (a == b) agreements++;
            }

            // Independent draws over this weighting agree ~46% of the time; near-total agreement means the
            // biome index never reached the hash.
            float agreementRate = agreements / (float)TRACK_DISTRIBUTION_ROLLS;
            if (agreementRate > 0.75f)
                return FailSound(scenario,
                    $"two biomes agreed on {agreementRate:0.##} of rolls — the biome index is not salting the hash.");

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
        /// Builds an <see cref="AudioContext"/> carrying a weighted neighborhood and each contributor's
        /// bearing.
        /// </summary>
        /// <param name="indices">Biome indices, nearest first.</param>
        /// <param name="weights">Their weights, index-aligned.</param>
        /// <param name="offsets">Their offsets in blocks, index-aligned.</param>
        /// <returns>A context with weights and directions populated.</returns>
        private static AudioContext DirectedContext(int[] indices, float[] weights, Vector2[] offsets)
        {
            BiomeWeights biomeWeights = new BiomeWeights { Count = indices.Length };
            BiomeDirections biomeDirections = new BiomeDirections();

            for (int i = 0; i < indices.Length; i++)
            {
                biomeWeights.Indices[i] = indices[i];
                biomeWeights.Weights[i] = weights[i];
                biomeDirections.OffsetsX[i] = offsets[i].x;
                biomeDirections.OffsetsZ[i] = offsets[i].y;
            }

            return new AudioContext(indices[0], null, true, 15, false, biomeWeights, true, 0, 0,
                biomeDirections);
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
                biome.ambientTracks = loops[i] != null
                    ? new[] { Track(loops[i], TRACK_BAND_LOW, TRACK_BAND_HIGH, 1f) }
                    : System.Array.Empty<AmbienceTrack>();
                biomes[i] = biome;
            }

            return biomes;
        }

        /// <summary>Builds one authored ambience track.</summary>
        /// <param name="clip">The loop the track plays.</param>
        /// <param name="low">Bottom of its altitude band, inclusive.</param>
        /// <param name="high">Top of its altitude band, inclusive.</param>
        /// <param name="chance">Its weight relative to the biome's other eligible tracks.</param>
        /// <returns>The track.</returns>
        private static AmbienceTrack Track(AudioClip clip, float low, float high, float chance) =>
            new AmbienceTrack { clip = clip, yRange = new Vector2(low, high), playChance = chance };

        /// <summary>Builds one authored ambience track carrying a content trim.</summary>
        /// <param name="clip">The loop the track plays.</param>
        /// <param name="chance">Its weight relative to the biome's other eligible tracks.</param>
        /// <param name="volume">Its authored content trim.</param>
        /// <returns>The track, spanning the whole world.</returns>
        /// <remarks>
        /// The band is fixed at the full sweep because every scenario that cares about gain is indifferent
        /// to altitude, and one that authored both would fail for two reasons at once.
        /// </remarks>
        private static AmbienceTrack TrackAt(AudioClip clip, float chance, float volume) =>
            new AmbienceTrack
            {
                clip = clip,
                yRange = new Vector2(TRACK_BAND_LOW, TRACK_BAND_HIGH),
                playChance = chance,
                volume = volume,
            };

        /// <summary>Builds a biome list whose entries carry one bed each, at an authored volume.</summary>
        /// <param name="loops">One bed per biome.</param>
        /// <param name="volumes">Their authored trims, index-aligned.</param>
        /// <returns>Biome assets to be destroyed by the caller.</returns>
        private static BiomeBase[] BiomesWithVolumes(AudioClip[] loops, float[] volumes)
        {
            BiomeBase[] biomes = new BiomeBase[loops.Length];
            for (int i = 0; i < loops.Length; i++)
            {
                StandardBiomeAttributes biome = ScriptableObject.CreateInstance<StandardBiomeAttributes>();
                biome.ambientTracks = new[] { TrackAt(loops[i], 1f, volumes[i]) };
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
                int count = AmbienceResolution.ResolveBedMix(shoreline, biomes, fallback, 0.05f, 0u, clips, weights);

                if (count != 2) return FailSound(scenario, $"a two-biome column produced {count} beds, not 2.");
                if (clips[0] != loops[0] || clips[1] != loops[1])
                    return FailSound(scenario, "the mix did not carry each biome's own bed.");
                if (Mathf.Abs(weights[0] - 0.7f) > AMBIENCE_EPSILON || Mathf.Abs(weights[1] - 0.3f) > AMBIENCE_EPSILON)
                    return FailSound(scenario, $"weights came through as ({weights[0]}, {weights[1]}).");

                // A 2% neighbor is dropped — and the remaining two must be scaled back up to fill the mix,
                // or every bed quietly ducks by the amount that was discarded.
                AudioContext withSliver = WeightedContext(new[] { 0, 1, 2 }, new[] { 0.6f, 0.38f, 0.02f });
                count = AmbienceResolution.ResolveBedMix(withSliver, biomes, fallback, 0.05f, 0u, clips, weights);

                if (count != 2) return FailSound(scenario, $"the sub-threshold biome was not dropped ({count} beds).");

                float sum = weights[0] + weights[1];
                if (Mathf.Abs(sum - 1f) > AMBIENCE_EPSILON)
                    return FailSound(scenario, $"after dropping a contributor the mix summed to {sum}, not 1.");

                // A biome with no bed of its own still sounds — as the fallback, never as silence.
                BiomeBase[] bare = BiomesWithLoops(new[] { null, loops[1] });
                try
                {
                    count = AmbienceResolution.ResolveBedMix(
                        WeightedContext(new[] { 0, 1 }, new[] { 0.5f, 0.5f }), bare, fallback, 0.05f, 0u, clips, weights);

                    if (count != 2) return FailSound(scenario, $"an unauthored biome bed produced {count} entries.");
                    if (clips[0] != fallback) return FailSound(scenario, "an unauthored biome bed did not fall back.");
                }
                finally
                {
                    foreach (BiomeBase biome in bare) Object.DestroyImmediate(biome);
                }

                // No weighted answer at all (the legacy generator) must still produce a bed.
                AudioContext unweighted = new AudioContext(-1, null, false, 15, false);
                count = AmbienceResolution.ResolveBedMix(unweighted, biomes, fallback, 0.05f, 0u, clips, weights);
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
                int count = AmbienceResolution.ResolveBedMix(context, biomes, fallback, 0.01f, 0u, clips, weights);

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
                        WeightedContext(new[] { 0, 1 }, new[] { 0.5f, 0.5f }), bothBare, fallback, 0.01f, 0u,
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

                // Since §11 the merge carries a third route in: two biomes that each list the shared track
                // among their own can now *roll* it in the same breath, which is a live case rather than the
                // authoring accident the fallback route represents. Each biome's alternative is parked out of
                // band at the test altitude, so both are certain to land on the shared clip.
                BiomeBase[] sharedRollers = new BiomeBase[2];
                for (int i = 0; i < sharedRollers.Length; i++)
                {
                    StandardBiomeAttributes biome = ScriptableObject.CreateInstance<StandardBiomeAttributes>();
                    biome.ambientTracks = new[]
                    {
                        Track(shared, 0f, 100f, 1f),
                        Track(other, 500f, 600f, 1f),
                    };
                    sharedRollers[i] = biome;
                }

                try
                {
                    BiomeWeights pair = new BiomeWeights { Count = 2 };
                    pair.Indices[0] = 0;
                    pair.Indices[1] = 1;
                    pair.Weights[0] = 0.55f;
                    pair.Weights[1] = 0.45f;

                    for (uint salt = 0; salt < 64; salt++)
                    {
                        AudioContext atSeaLevel = new AudioContext(0, null, true, 15, false, pair, true, 0, 50);
                        count = AmbienceResolution.ResolveBedMix(
                            atSeaLevel, sharedRollers, fallback, 0.01f, salt, clips, weights);

                        if (count != 1)
                            return FailSound(scenario,
                                $"salt {salt}: two biomes rolling one shared track produced {count} sources, not 1.");
                        if (clips[0] != shared)
                            return FailSound(scenario, $"salt {salt}: the merged entry did not carry the shared track.");
                        if (Mathf.Abs(weights[0] - 1f) > AMBIENCE_EPSILON)
                            return FailSound(scenario, $"salt {salt}: the merged weight was {weights[0]}, not 1.");
                    }
                }
                finally
                {
                    foreach (BiomeBase biome in sharedRollers) Object.DestroyImmediate(biome);
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

        /// <summary>
        /// The composed bed gain (S7). Every gain that governs a bed meets in one expression, and only the
        /// fade passes through the equal-power curve — a content trim folded in before the square root, or
        /// dropped altogether, is inaudible in a table of measurements and obvious in the room.
        /// </summary>
        /// <remarks>
        /// The reason the chain is a function at all. Composed inline in the director it was reachable only
        /// by playing the game, so the one baseline that mentioned bed gain asserted <c>GainFromFade</c> on
        /// its own and stayed green no matter what the director multiplied it by.
        /// </remarks>
        private static bool RunBedSourceVolume()
        {
            const string scenario = "Bed Source Volume Folds In Every Gain That Governs A Bed";

            // With every other gain at unity the chain must be exactly the fade curve, or the two have
            // drifted apart and the constant-power scenario no longer describes what a bed plays.
            for (int i = 0; i <= AMBIENCE_SWEEP_STEPS; i++)
            {
                float fade = i / (float)AMBIENCE_SWEEP_STEPS;
                float composed = AmbienceResolution.BedSourceVolume(fade, 1f, 1f, 1f, 1f);
                if (Mathf.Abs(composed - AmbienceResolution.GainFromFade(fade)) > AMBIENCE_EPSILON)
                    return FailSound(scenario,
                        $"at fade={fade:0.000} the chain gave {composed}, not the fade curve's " +
                        $"{AmbienceResolution.GainFromFade(fade)}.");
            }

            // Linear in the trim, NOT equal-power: a track authored at 0.25 is a quarter of the amplitude.
            // Routing it through the fade curve would make it half, which reads as the trim not working.
            float trimmed = AmbienceResolution.BedSourceVolume(1f, 0.25f, 1f, 1f, 1f);
            if (Mathf.Abs(trimmed - 0.25f) > AMBIENCE_EPSILON)
                return FailSound(scenario, $"a track volume of 0.25 produced {trimmed}, not 0.25.");

            // Every term multiplies, none is dropped: four halvings are a sixteenth.
            float all = AmbienceResolution.BedSourceVolume(1f, 0.5f, 0.5f, 0.5f, 0.5f);
            if (Mathf.Abs(all - 0.0625f) > AMBIENCE_EPSILON)
                return FailSound(scenario, $"four gains of 0.5 composed to {all}, not 0.0625.");

            // A silent fade stays exactly silent whatever else is authored — a released slot is recognized
            // by a volume of zero, and a trim must not leave a sliver behind.
            if (!ExactValue.IsZero(AmbienceResolution.BedSourceVolume(0f, 0.5f, 1f, 1f, 1f)))
                return FailSound(scenario, "a fade of 0 did not produce silence.");

            // Trims attenuate only. An out-of-range authoring must not amplify the bed above the level the
            // fade and the ducks agreed on.
            float above = AmbienceResolution.BedSourceVolume(1f, 4f, 1f, 1f, 1f);
            if (!ExactValue.Equal(above, 1f))
                return FailSound(scenario, $"a track volume of 4 was not clamped: it gave {above}.");
            if (!ExactValue.IsZero(AmbienceResolution.BedSourceVolume(1f, -1f, 1f, 1f, 1f)))
                return FailSound(scenario, "a negative track volume was not clamped to silence.");

            return true;
        }

        /// <summary>
        /// The unset rule (S7): a track deserialized from an asset written before the volume field existed
        /// holds 0, and must be heard at full level rather than not at all.
        /// </summary>
        /// <remarks>
        /// The trap this whole feature had to step around. Ten authored tracks across six shipped assets
        /// would have gone silent on the frame the field landed, and silence is the one failure that reports
        /// nothing — the beds simply stop, and nothing in the console says why.
        /// </remarks>
        private static bool RunTrackVolumeDefault()
        {
            const string scenario = "An Unauthored Track Volume Plays At Full Level";

            AmbienceTrack unset = Track(MakeClips(1)[0], TRACK_BAND_LOW, TRACK_BAND_HIGH, 1f);
            if (!ExactValue.Equal(unset.EffectiveVolume, 1f))
                return FailSound(scenario,
                    $"an unauthored track read as {unset.EffectiveVolume}, not full level.");

            AmbienceTrack authored = TrackAt(MakeClips(1)[0], 1f, FIXTURE_TRACK_VOLUME);
            if (!ExactValue.Equal(authored.EffectiveVolume, FIXTURE_TRACK_VOLUME))
                return FailSound(scenario,
                    $"an authored {FIXTURE_TRACK_VOLUME} read back as {authored.EffectiveVolume}.");

            AudioClip loop = MakeClips(1)[0];
            BiomeBase[] biomes = BiomesWithVolumes(new[] { loop }, new[] { FIXTURE_TRACK_VOLUME });
            try
            {
                AudioClip selected = AmbienceResolution.SelectBiomeTrackClip(biomes[0], 0, 0u, 0,
                    out float volume);

                if (selected != loop) return FailSound(scenario, "the fixture biome did not resolve its bed.");
                if (!ExactValue.Equal(volume, FIXTURE_TRACK_VOLUME))
                    return FailSound(scenario,
                        $"the resolver reported {volume} for a track authored at {FIXTURE_TRACK_VOLUME}.");

                // A biome that resolves no track reports full level, not silence: the caller falls back to
                // the database bed, whose own gain governs it.
                StandardBiomeAttributes bare = ScriptableObject.CreateInstance<StandardBiomeAttributes>();
                try
                {
                    bare.ambientTracks = System.Array.Empty<AmbienceTrack>();
                    if (AmbienceResolution.SelectBiomeTrackClip(bare, 0, 0u, 0, out float bareVolume) != null)
                        return FailSound(scenario, "a biome with no tracks resolved a clip.");
                    if (!ExactValue.Equal(bareVolume, 1f))
                        return FailSound(scenario, $"a biome with no tracks reported a gain of {bareVolume}.");
                }
                finally
                {
                    Object.DestroyImmediate(bare);
                }
            }
            finally
            {
                foreach (BiomeBase biome in biomes) Object.DestroyImmediate(biome);
            }

            return true;
        }

        /// <summary>
        /// The mix's volume channel (S7): each entry carries the gain of the track that produced it, merged
        /// entries carry the weight-weighted mean of theirs, and the fallback carries the database's.
        /// </summary>
        /// <remarks>
        /// The gain has to survive the same collapse the bearing does. Entries merge <b>by clip</b>, so the
        /// two biomes that share a bed today arrive as one source that can only be played at one level; a
        /// channel that merged by overwriting would hand that source whichever contributor the weight walk
        /// happened to visit last.
        /// </remarks>
        private static bool RunBedMixVolumes()
        {
            const string scenario = "Bed Mix Carries Each Track's Volume And Merges Them By Weight";

            AudioClip[] loops = MakeClips(2);
            AudioClip shared = MakeClips(1)[0];
            AudioClip fallback = AudioClip.Create("ValidationVolumeFallback", 16, 1, 8000, false);

            AudioClip[] clips = new AudioClip[BiomeWeights.MaxBiomes];
            float[] weights = new float[BiomeWeights.MaxBiomes];
            float[] volumes = new float[BiomeWeights.MaxBiomes];

            BiomeBase[] distinct = BiomesWithVolumes(loops,
                new[] { FIXTURE_TRACK_VOLUME, FIXTURE_OTHER_VOLUME });
            try
            {
                // Distinct clips: each entry keeps its own track's trim, whatever the mix weights are.
                int count = AmbienceResolution.ResolveBedMix(
                    WeightedContext(new[] { 0, 1 }, new[] { 0.6f, 0.4f }), distinct, fallback, 0.01f, 0u,
                    clips, weights, null, volumes);

                if (count != 2) return FailSound(scenario, $"two distinct beds produced {count} entries.");

                // Tolerant, unlike the fallback assertions below: even an unmerged entry is scaled by its
                // raw weight and divided back out, so an authored 0.8 returns as 0.8000001.
                if (Mathf.Abs(volumes[0] - FIXTURE_TRACK_VOLUME) > AMBIENCE_EPSILON)
                    return FailSound(scenario, $"the first entry carried {volumes[0]}.");
                if (Mathf.Abs(volumes[1] - FIXTURE_OTHER_VOLUME) > AMBIENCE_EPSILON)
                    return FailSound(scenario, $"the second entry carried {volumes[1]}.");
            }
            finally
            {
                foreach (BiomeBase biome in distinct) Object.DestroyImmediate(biome);
            }

            BiomeBase[] merging = BiomesWithVolumes(new[] { shared, shared },
                new[] { FIXTURE_TRACK_VOLUME, FIXTURE_OTHER_VOLUME });
            try
            {
                // 75% of the weight authored at 0.4, 25% at 0.8: the mean is 0.5, not either contributor and
                // not their unweighted average.
                int count = AmbienceResolution.ResolveBedMix(
                    WeightedContext(new[] { 0, 1 }, new[] { 0.75f, 0.25f }), merging, fallback, 0.01f, 0u,
                    clips, weights, null, volumes);

                if (count != 1) return FailSound(scenario, $"one shared clip produced {count} entries.");

                const float expected = 0.75f * FIXTURE_TRACK_VOLUME + 0.25f * FIXTURE_OTHER_VOLUME;
                if (Mathf.Abs(volumes[0] - expected) > AMBIENCE_EPSILON)
                    return FailSound(scenario,
                        $"the merged gain was {volumes[0]}, not the weighted mean {expected}.");
            }
            finally
            {
                foreach (BiomeBase biome in merging) Object.DestroyImmediate(biome);
            }

            // The fallback bed answers with the gain authored for the fallback bed, not with a biome's.
            AudioContext unweighted = new AudioContext(-1, null, false, 15, false);
            int single = AmbienceResolution.ResolveBedMix(
                unweighted, null, fallback, 0.01f, 0u, clips, weights, null, volumes,
                FIXTURE_OTHER_VOLUME);

            if (single != 1) return FailSound(scenario, $"the unweighted fallback produced {single} entries.");
            if (!ExactValue.Equal(volumes[0], FIXTURE_OTHER_VOLUME))
                return FailSound(scenario,
                    $"the fallback bed carried {volumes[0]}, not its authored {FIXTURE_OTHER_VOLUME}.");

            // A biome that authors no track of its own is playing the fallback clip, so it must take the
            // fallback's gain too — not the unity the track lookup reports for "nothing selected".
            BiomeBase[] bare = BiomesWithLoops(new AudioClip[] { null });
            try
            {
                int count = AmbienceResolution.ResolveBedMix(
                    WeightedContext(new[] { 0 }, new[] { 1f }), bare, fallback, 0.01f, 0u,
                    clips, weights, null, volumes, FIXTURE_TRACK_VOLUME);

                if (count != 1) return FailSound(scenario, $"a bedless biome produced {count} entries.");
                if (clips[0] != fallback) return FailSound(scenario, "a bedless biome did not take the fallback.");
                if (!ExactValue.Equal(volumes[0], FIXTURE_TRACK_VOLUME))
                    return FailSound(scenario,
                        $"a bedless biome played the fallback at {volumes[0]}, not its authored " +
                        $"{FIXTURE_TRACK_VOLUME}.");
            }
            finally
            {
                foreach (BiomeBase biome in bare) Object.DestroyImmediate(biome);
            }

            return true;
        }
    }
}
