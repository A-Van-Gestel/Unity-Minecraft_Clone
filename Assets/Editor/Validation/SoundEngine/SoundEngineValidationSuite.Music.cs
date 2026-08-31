using System.Collections.Generic;
using Audio;
using Data;
using Data.Enums;
using Editor.Validation.Framework;
using UnityEngine;

namespace Editor.Validation.SoundEngine
{
    /// <summary>
    /// <see cref="SoundEngineValidationSuite"/> — the music decisions (§5.3): which pool a pick reaches for,
    /// which track wins inside it, the repeat guard, and the gain a track plays at.
    /// </summary>
    /// <remarks>
    /// The half of the music layer that can be asserted without a scene. Every defect here is silent in the
    /// running game in the most literal sense — a weighting that ignores its weights, or a pool roll that
    /// never reaches the biome, sounds exactly like a working scheduler until someone counts what played
    /// over an hour.
    /// </remarks>
    public static partial class SoundEngineValidationSuite
    {
        /// <summary>Picks each distribution scenario draws before comparing frequencies against the weights.</summary>
        private const int MUSIC_DISTRIBUTION_PICKS = 4000;

        /// <summary>How far an observed share may sit from its expected one, as a fraction of the total.</summary>
        private const float MUSIC_SHARE_TOLERANCE = 0.05f;

        /// <summary>
        /// How far one gap bucket's track split may sit from an even one before the two rolls are called
        /// dependent. Looser than <see cref="MUSIC_SHARE_TOLERANCE"/> because each bucket sees only a
        /// quarter of the picks, so its sampling noise is correspondingly wider.
        /// </summary>
        private const float MUSIC_GAP_INDEPENDENCE_TOLERANCE = 0.08f;

        static partial void AddMusicScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario("Biome Music Adds To The Global Pool Rather Than Replacing It",
                RunMusicPoolsCombine));
            scenarios.Add(new Scenario("Music Share Splits Picks Between The Biome And Global Pools",
                RunMusicShareSplit));
            scenarios.Add(new Scenario("Music Weights Spread Picks Across A Pool In Proportion",
                RunMusicWeightDistribution));
            scenarios.Add(new Scenario("Music Never Picks The Same Track Twice Running", RunMusicRepeatGuard));
            scenarios.Add(new Scenario("A Music Pool With Empty Slots Still Picks A Track", RunMusicHoles));
            scenarios.Add(new Scenario("Music Source Volume Folds In Every Gain That Governs A Track",
                RunMusicSourceVolume));
            scenarios.Add(new Scenario("The Gap Before A Track Does Not Determine Which Track Plays",
                RunMusicGapTrackIndependence));
            scenarios.Add(new Scenario("Dark Tracks Are Barred From Daylight And Favoured In The Dark",
                RunMusicEnvironmentGating));
            scenarios.Add(new Scenario("A Cave And A Night Sky Are Both Dark", RunMusicDarknessUnion));
        }

        /// <summary>
        /// The additive rule: a biome's tracks are offered <b>alongside</b> the global pool, never instead of
        /// it, and either pool alone still answers.
        /// </summary>
        /// <remarks>
        /// Shadowing instead of adding is the failure this guards: a biome authoring one regional track
        /// would silence every other piece of music for as long as the player stood there, so importing a
        /// single desert piece would take the whole desert.
        /// </remarks>
        private static bool RunMusicPoolsCombine()
        {
            const string scenario = "Biome Music Adds To The Global Pool Rather Than Replacing It";

            AudioClip[] clips = MakeClips(4);
            MusicTrack[] global = { Music(clips[0], 1f), Music(clips[1], 1f) };
            MusicTrack[] biome = { Music(clips[2], 1f), Music(clips[3], 1f) };

            bool sawGlobal = false;
            bool sawBiome = false;

            for (uint salt = 1; salt <= MUSIC_DISTRIBUTION_PICKS; salt++)
            {
                if (!MusicResolution.TryPickTrack(global, biome, 0.5f, null,
                        AmbienceResolution.ScheduleHash(salt), out MusicTrack track))
                    return FailSound(scenario, "two populated pools produced no track.");

                if (track.clip == clips[0] || track.clip == clips[1]) sawGlobal = true;
                if (track.clip == clips[2] || track.clip == clips[3]) sawBiome = true;
            }

            if (!sawGlobal)
                return FailSound(scenario,
                    "no global track was ever picked while the biome authored its own — the biome pool is " +
                    "replacing the global one instead of adding to it.");
            if (!sawBiome) return FailSound(scenario, "no biome track was ever picked.");

            // Either pool alone still answers, whatever the share says: a share of 1 with no biome tracks
            // must not resolve to silence.
            if (!MusicResolution.TryPickTrack(global, null, 1f, null, 7u, out MusicTrack globalOnly) ||
                globalOnly.clip == null)
                return FailSound(scenario, "a biome with no tracks did not fall through to the global pool.");

            if (!MusicResolution.TryPickTrack(null, biome, 0f, null, 7u, out MusicTrack biomeOnly) ||
                biomeOnly.clip == null)
                return FailSound(scenario, "an empty global pool did not fall through to the biome's.");

            if (MusicResolution.TryPickTrack(null, null, 0.5f, null, 7u, out _))
                return FailSound(scenario, "two empty pools produced a track.");

            return true;
        }

        /// <summary>
        /// The authored share: how often a pick prefers the biome pool, independent of either pool's size.
        /// </summary>
        /// <remarks>
        /// Size independence is the whole reason the share exists. A single weighted roulette over the union
        /// would give one biome track beside eighteen global ones about one pick in nineteen, so the biome
        /// would be inaudible <i>as</i> a biome and every weight would need re-tuning whenever the global
        /// pool grew. This asserts the share holds with the pools deliberately lopsided.
        /// </remarks>
        private static bool RunMusicShareSplit()
        {
            const string scenario = "Music Share Splits Picks Between The Biome And Global Pools";

            AudioClip[] clips = MakeClips(9);
            MusicTrack[] global = new MusicTrack[8];
            for (int i = 0; i < global.Length; i++) global[i] = Music(clips[i], 1f);

            // One biome track against eight global ones: a union would surface it one pick in nine.
            MusicTrack[] biome = { Music(clips[8], 1f) };

            foreach (float share in new[] { 0f, 0.25f, 0.5f, 1f })
            {
                int biomePicks = 0;

                for (uint salt = 1; salt <= MUSIC_DISTRIBUTION_PICKS; salt++)
                {
                    if (!MusicResolution.TryPickTrack(global, biome, share, null,
                            AmbienceResolution.ScheduleHash(salt), out MusicTrack track))
                        return FailSound(scenario, $"share {share} produced no track.");

                    if (track.clip == clips[8]) biomePicks++;
                }

                float observed = biomePicks / (float)MUSIC_DISTRIBUTION_PICKS;
                if (Mathf.Abs(observed - share) > MUSIC_SHARE_TOLERANCE)
                    return FailSound(scenario,
                        $"a share of {share} produced {observed:0.000} of picks from the biome pool. With " +
                        "one biome track against eight global ones, a union would give about 0.111 " +
                        "regardless of the share.");
            }

            return true;
        }

        /// <summary>
        /// The weighted roulette inside one pool: a track's share of picks matches its share of the weight.
        /// </summary>
        /// <remarks>
        /// The scenario a "does it pick something" assertion cannot replace. A selector that ignored weights
        /// entirely still returns a valid track every time and would pass every other scenario in this file.
        /// </remarks>
        private static bool RunMusicWeightDistribution()
        {
            const string scenario = "Music Weights Spread Picks Across A Pool In Proportion";

            AudioClip[] clips = MakeClips(3);
            MusicTrack[] pool = { Music(clips[0], 1f), Music(clips[1], 0.5f), Music(clips[2], 0.5f) };

            int[] counts = new int[3];
            for (uint salt = 1; salt <= MUSIC_DISTRIBUTION_PICKS; salt++)
            {
                // No previous track, so the repeat guard never removes a candidate and the observed shares
                // can be compared against the authored weights directly.
                if (!MusicResolution.TryPickFrom(pool, null, AmbienceResolution.ScheduleHash(salt),
                        out MusicTrack track))
                    return FailSound(scenario, "a populated pool produced no track.");

                for (int i = 0; i < clips.Length; i++)
                {
                    if (track.clip == clips[i]) counts[i]++;
                }
            }

            float[] expected = { 0.5f, 0.25f, 0.25f };
            for (int i = 0; i < counts.Length; i++)
            {
                float observed = counts[i] / (float)MUSIC_DISTRIBUTION_PICKS;
                if (Mathf.Abs(observed - expected[i]) > MUSIC_SHARE_TOLERANCE)
                    return FailSound(scenario,
                        $"track {i} took {observed:0.000} of picks, not its authored {expected[i]:0.000}.");
            }

            // All-zero weights are "no opinion", not "silence": the pool falls back to an even pick.
            MusicTrack[] unweighted = { Music(clips[0], 0f), Music(clips[1], 0f) };
            int first = 0;

            for (uint salt = 1; salt <= MUSIC_DISTRIBUTION_PICKS; salt++)
            {
                if (!MusicResolution.TryPickFrom(unweighted, null, AmbienceResolution.ScheduleHash(salt),
                        out MusicTrack track))
                    return FailSound(scenario, "an all-zero-weight pool produced no track.");

                if (track.clip == clips[0]) first++;
            }

            float evenShare = first / (float)MUSIC_DISTRIBUTION_PICKS;
            if (Mathf.Abs(evenShare - 0.5f) > MUSIC_SHARE_TOLERANCE)
                return FailSound(scenario,
                    $"all-zero weights gave the first track {evenShare:0.000} of picks, not an even 0.5.");

            return true;
        }

        /// <summary>The repeat guard: never the same track twice running while anything else is available.</summary>
        private static bool RunMusicRepeatGuard()
        {
            const string scenario = "Music Never Picks The Same Track Twice Running";

            for (int count = 2; count <= 8; count++)
            {
                AudioClip[] clips = MakeClips(count);
                MusicTrack[] pool = new MusicTrack[count];
                for (int i = 0; i < count; i++) pool[i] = Music(clips[i], 1f);

                AudioClip last = null;
                for (uint salt = 1; salt <= AMBIENCE_SWEEP_STEPS; salt++)
                {
                    if (!MusicResolution.TryPickFrom(pool, last, AmbienceResolution.ScheduleHash(salt),
                            out MusicTrack track))
                        return FailSound(scenario, $"a pool of {count} produced no track.");

                    if (track.clip == last)
                        return FailSound(scenario, $"a pool of {count} repeated a track back to back.");

                    last = track.clip;
                }
            }

            // One track left is the exception: repeating it beats a gap of silence, which is what a
            // single-track pool would otherwise produce forever.
            AudioClip[] one = MakeClips(1);
            MusicTrack[] single = { Music(one[0], 1f) };

            if (!MusicResolution.TryPickFrom(single, one[0], 0u, out MusicTrack repeated) ||
                repeated.clip != one[0])
                return FailSound(scenario, "a single-track pool refused to repeat its only track.");

            // The same must hold across the two pools: a one-track biome pool that just played must not
            // stall the layer while the global pool has something to offer.
            AudioClip[] pair = MakeClips(2);
            MusicTrack[] biome = { Music(pair[0], 1f) };
            MusicTrack[] global = { Music(pair[1], 1f) };

            if (!MusicResolution.TryPickTrack(global, biome, 1f, pair[0], 3u, out MusicTrack crossed))
                return FailSound(scenario, "a spent single-track biome pool produced no track at all.");
            if (crossed.clip != pair[1])
                return FailSound(scenario,
                    "a spent single-track biome pool repeated itself instead of falling through to the " +
                    "global pool.");

            return true;
        }

        /// <summary>Empty slots in a pool are ordinary and must never resolve to silence.</summary>
        /// <remarks>
        /// The pool editor appends an empty slot before the author assigns a clip, and an in-progress import
        /// looks the same. Resolving to nothing costs a whole authored gap of silence per landing.
        /// </remarks>
        private static bool RunMusicHoles()
        {
            const string scenario = "A Music Pool With Empty Slots Still Picks A Track";

            if (MusicResolution.TryPickFrom(null, null, 0u, out _))
                return FailSound(scenario, "a null pool produced a track.");
            if (MusicResolution.TryPickFrom(System.Array.Empty<MusicTrack>(), null, 0u, out _))
                return FailSound(scenario, "an empty pool produced a track.");
            if (MusicResolution.TryPickFrom(new MusicTrack[3], null, 0u, out _))
                return FailSound(scenario, "a pool of empty slots produced a track.");

            AudioClip[] clips = MakeClips(2);
            MusicTrack[] holed = { default, Music(clips[0], 1f), default, default };

            for (uint salt = 1; salt <= AMBIENCE_SWEEP_STEPS; salt++)
            {
                if (!MusicResolution.TryPickFrom(holed, null, AmbienceResolution.ScheduleHash(salt),
                        out MusicTrack track))
                    return FailSound(scenario, "a pool holding one clip among holes produced no track.");

                if (track.clip != clips[0])
                    return FailSound(scenario, "a pool holding one clip answered with something else.");
            }

            // Two clips among holes: every pick is one of them, and never the one just played.
            MusicTrack[] sparse = { Music(clips[0], 1f), default, default, Music(clips[1], 1f), default };
            AudioClip last = null;

            for (uint salt = 1; salt <= AMBIENCE_SWEEP_STEPS; salt++)
            {
                if (!MusicResolution.TryPickFrom(sparse, last, AmbienceResolution.ScheduleHash(salt),
                        out MusicTrack track))
                    return FailSound(scenario, "a sparse pool produced no track.");

                if (track.clip == null) return FailSound(scenario, "a sparse pool answered with an empty slot.");
                if (track.clip == last) return FailSound(scenario, "a sparse pool repeated a track back to back.");

                last = track.clip;
            }

            return true;
        }

        /// <summary>
        /// The composed music gain: the track's trim, the pack trim and the category gain all multiply, and
        /// none of them is dropped.
        /// </summary>
        private static bool RunMusicSourceVolume()
        {
            const string scenario = "Music Source Volume Folds In Every Gain That Governs A Track";

            if (!ExactValue.Equal(MusicResolution.SourceVolume(1f, 1f, 1f), 1f))
                return FailSound(scenario, "three unity gains did not compose to 1.");

            float trimmed = MusicResolution.SourceVolume(0.25f, 1f, 1f);
            if (Mathf.Abs(trimmed - 0.25f) > AMBIENCE_EPSILON)
                return FailSound(scenario, $"a track volume of 0.25 produced {trimmed}.");

            float all = MusicResolution.SourceVolume(0.5f, 0.5f, 0.5f);
            if (Mathf.Abs(all - 0.125f) > AMBIENCE_EPSILON)
                return FailSound(scenario, $"three gains of 0.5 composed to {all}, not 0.125.");

            if (!ExactValue.IsZero(MusicResolution.SourceVolume(0f, 1f, 1f)))
                return FailSound(scenario, "a track volume of 0 did not produce silence.");

            // Trims attenuate only: an out-of-range authoring must not amplify the track.
            if (!ExactValue.Equal(MusicResolution.SourceVolume(4f, 1f, 1f), 1f))
                return FailSound(scenario, "a track volume above 1 was not clamped.");
            if (!ExactValue.IsZero(MusicResolution.SourceVolume(-1f, 1f, 1f)))
                return FailSound(scenario, "a negative track volume was not clamped to silence.");

            return true;
        }

        /// <summary>
        /// The pick hash is independent of the gap hash: knowing how long the silence was must not tell you
        /// what comes out of it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The design doc asserted this before anything enforced it, and the assertion was false. Both
        /// decisions were driven off <b>one</b> hash: <c>NextGapSeconds</c> consumes bits 8–23 and the track
        /// roulette consumed bits 0–23, sharing sixteen of them, so the track was a near-deterministic
        /// function of the gap that preceded it. No slice of the remaining bits is wide enough for a
        /// roulette, so the fix is a separate hash — <see cref="MusicResolution.PickHash"/> — and this pins it.
        /// </para>
        /// <para>
        /// Measured as a contingency table rather than a correlation: gaps are bucketed, and if the gap
        /// bucket carried real information about the track, some bucket would concentrate on one track far
        /// beyond the spread of a fair pool.
        /// </para>
        /// </remarks>
        private static bool RunMusicGapTrackIndependence()
        {
            const string scenario = "The Gap Before A Track Does Not Determine Which Track Plays";
            const int buckets = 4;
            const int trackCount = 4;

            AudioClip[] clips = MakeClips(trackCount);
            MusicTrack[] pool = new MusicTrack[trackCount];
            for (int i = 0; i < trackCount; i++) pool[i] = Music(clips[i], 1f);

            int[,] joint = new int[buckets, trackCount];
            int[] perBucket = new int[buckets];

            for (uint counter = 1; counter <= MUSIC_DISTRIBUTION_PICKS; counter++)
            {
                // Exactly what the scheduler does: one counter, two hashes.
                float gap = AmbienceResolution.NextGapSeconds(0f, 1f,
                    AmbienceResolution.ScheduleHash(counter));

                if (!MusicResolution.TryPickFrom(pool, null, MusicResolution.PickHash(counter),
                        out MusicTrack track))
                    return FailSound(scenario, "a populated pool produced no track.");

                int bucket = Mathf.Clamp((int)(gap * buckets), 0, buckets - 1);
                perBucket[bucket]++;

                for (int i = 0; i < trackCount; i++)
                {
                    if (track.clip == clips[i]) joint[bucket, i]++;
                }
            }

            // Each track should take roughly 1/trackCount of every bucket. A shared hash drives this to
            // ~1.0 for some cell; a fair split leaves it near 0.25.
            for (int bucket = 0; bucket < buckets; bucket++)
            {
                if (perBucket[bucket] == 0)
                    return FailSound(scenario, $"gap bucket {bucket} was never produced.");

                for (int i = 0; i < trackCount; i++)
                {
                    float share = joint[bucket, i] / (float)perBucket[bucket];
                    if (Mathf.Abs(share - 1f / trackCount) <= MUSIC_GAP_INDEPENDENCE_TOLERANCE) continue;

                    return FailSound(scenario,
                        $"gap bucket {bucket} produced track {i} on {share:0.000} of its picks instead of " +
                        $"about {1f / trackCount:0.000}. The gap is carrying information about the track — " +
                        "the two rolls are sharing a hash.");
                }
            }

            return true;
        }

        /// <summary>
        /// The environment gate (§13): Underground tracks never surface, Surface tracks keep playing
        /// dark but at a reduced weight, and Any is unaffected either way.
        /// </summary>
        /// <remarks>
        /// The reduction is a weight scale rather than an exclusion because the cave pool is small — barring
        /// every surface track would loop two pieces. That makes the assertion a <i>distribution</i> one: a
        /// gate that merely filtered would pass a "does an dark track ever play" check while getting
        /// the proportions completely wrong.
        /// </remarks>
        private static bool RunMusicEnvironmentGating()
        {
            const string scenario = "Dark Tracks Are Barred From Daylight And Favoured In The Dark";
            const float daylightWeightWhenDark = 0.25f;

            AudioClip[] clips = MakeClips(4);
            MusicTrack[] pool =
            {
                Environment(clips[0], MusicEnvironment.Daylight),
                Environment(clips[1], MusicEnvironment.Daylight),
                Environment(clips[2], MusicEnvironment.Daylight),
                Environment(clips[3], MusicEnvironment.Dark),
            };

            // On the surface: the dark track is never chosen, however many rolls are drawn.
            for (uint salt = 1; salt <= MUSIC_DISTRIBUTION_PICKS; salt++)
            {
                if (!MusicResolution.TryPickFrom(pool, null, AmbienceResolution.ScheduleHash(salt),
                        out MusicTrack track, true, false, daylightWeightWhenDark))
                    return FailSound(scenario, "a surface pool produced no track.");

                if (track.clip == clips[3])
                    return FailSound(scenario, "an Underground track was picked on the surface.");
            }

            // Underground: surface tracks still play, but the cave track takes the share the scale implies.
            // Three surface tracks at 0.25 sum to 0.75 against the cave track's 1.0, so it should take
            // 1 / 1.75 of the picks.
            int cavePicks = 0;
            for (uint salt = 1; salt <= MUSIC_DISTRIBUTION_PICKS; salt++)
            {
                if (!MusicResolution.TryPickFrom(pool, null, AmbienceResolution.ScheduleHash(salt),
                        out MusicTrack track, true, true, daylightWeightWhenDark))
                    return FailSound(scenario, "an dark pool produced no track.");

                if (track.clip == clips[3]) cavePicks++;
            }

            float observed = cavePicks / (float)MUSIC_DISTRIBUTION_PICKS;
            const float expected = 1f / (1f + 3f * daylightWeightWhenDark);
            if (Mathf.Abs(observed - expected) > MUSIC_SHARE_TOLERANCE)
                return FailSound(scenario,
                    $"dark, the cave track took {observed:0.000} of picks, not the {expected:0.000} " +
                    "its weight implies against three surface tracks scaled to " +
                    $"{daylightWeightWhenDark}. Surface tracks are being excluded or not scaled.");

            if (observed >= 0.999f)
                return FailSound(scenario,
                    "the cave track took every pick dark — surface tracks are excluded rather than " +
                    "down-weighted, which loops a small cave pool.");

            // A scale of 0 means "no surface music in caves", and that IS exclusion, not weight zero: a pool
            // whose weights all sum to zero would otherwise fall back to an even pick.
            for (uint salt = 1; salt <= AMBIENCE_SWEEP_STEPS; salt++)
            {
                if (!MusicResolution.TryPickFrom(pool, null, AmbienceResolution.ScheduleHash(salt),
                        out MusicTrack track, true, true, 0f))
                    return FailSound(scenario, "a zero cave weight silenced the pool entirely.");

                if (track.clip != clips[3])
                    return FailSound(scenario,
                        "a surface track played dark at a cave weight of 0 — a zero scale must bar " +
                        "the track, not admit it with no weight.");
            }

            // The union: a Dark track is barred in daylight and eligible in the dark, and the resolver is
            // told only "dark" — it must not care WHICH kind of dark, or night would need its own rule.
            // AudioContext.IsDark is where underground and night are joined; this pins the consequence.
            MusicTrack darkTrack = Environment(clips[3], MusicEnvironment.Dark);
            if (darkTrack.EnvironmentWeight(false, daylightWeightWhenDark) > 0f)
                return FailSound(scenario, "a Dark track was eligible in daylight.");
            if (darkTrack.EnvironmentWeight(true, daylightWeightWhenDark) <= 0f)
                return FailSound(scenario, "a Dark track was not eligible in the dark.");

            // A pool of only Dark tracks reads as EMPTY in daylight, so a caller holding another
            // pool falls through to it rather than being handed a track that may not play here.
            MusicTrack[] darkOnly = { Environment(clips[3], MusicEnvironment.Dark) };
            if (MusicResolution.HasEligible(darkOnly, false, daylightWeightWhenDark))
                return FailSound(scenario, "a dark-only pool reported itself eligible in daylight.");
            if (!MusicResolution.HasEligible(darkOnly, true, daylightWeightWhenDark))
                return FailSound(scenario, "a cave-only pool reported itself ineligible dark.");

            return true;
        }

        /// <summary>
        /// The union itself: <c>AudioContext.IsDark</c> is true underground OR at night.
        /// </summary>
        /// <remarks>
        /// Pinned separately because the rest of this file exercises <c>MusicResolution</c>, which is simply
        /// <i>told</i> whether it is dark — so dropping night from the union left every other scenario green
        /// while cave music stopped playing at night. The composed property is where the feature lives, and
        /// a test that only ever passes the flag by hand cannot see it.
        /// </remarks>
        private static bool RunMusicDarknessUnion()
        {
            const string scenario = "A Cave And A Night Sky Are Both Dark";

            if (Context(false, false).IsDark)
                return FailSound(scenario, "daylight above ground reported dark.");

            if (!Context(true, false).IsDark)
                return FailSound(scenario, "underground by day did not report dark.");

            if (!Context(false, true).IsDark)
                return FailSound(scenario,
                    "night above ground did not report dark — a track written for a cave is meant to suit " +
                    "the surface after dark, and this is the only place the two are joined.");

            if (!Context(true, true).IsDark)
                return FailSound(scenario, "underground at night did not report dark.");

            // The cave BED must not follow night: cave ambience under an open midnight sky would be wrong.
            if (Context(false, true).Underground)
                return FailSound(scenario,
                    "night above ground reported UNDERGROUND. The cave bed reads that field, so night would " +
                    "fade a cave ambience in on the open surface.");

            return true;
        }

        /// <summary>Builds a listener context with only the light signals set.</summary>
        /// <param name="underground">Whether the listener is underground.</param>
        /// <param name="night">Whether the sun is below the horizon.</param>
        /// <returns>The context.</returns>
        private static AudioContext Context(bool underground, bool night) =>
            new AudioContext(0, null, false, 15, false, default, false, 0, 64, default, underground, night);

        /// <summary>Builds one authored music track with an environment.</summary>
        /// <param name="clip">The track's clip.</param>
        /// <param name="environment">Where it may play.</param>
        /// <returns>The track, at full weight and unset volume.</returns>
        private static MusicTrack Environment(AudioClip clip, MusicEnvironment environment) =>
            new MusicTrack { clip = clip, weight = 1f, environment = environment };

        /// <summary>Builds one authored music track.</summary>
        /// <param name="clip">The track's clip.</param>
        /// <param name="weight">Its share relative to the rest of its pool.</param>
        /// <returns>The track, at unset volume.</returns>
        private static MusicTrack Music(AudioClip clip, float weight) =>
            new MusicTrack { clip = clip, weight = weight };
    }
}
