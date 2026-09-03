using Data;
using UnityEngine;

namespace Audio
{
    /// <summary>
    /// The pure decision layer behind the music scheduler (SOUND_ENGINE_DESIGN.md §5.3): which track a pick
    /// resolves to across the global and biome pools, and the gain it plays at.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separate from <see cref="AmbienceResolution"/> for the reason <c>FluidEmitterResolution</c> is: these
    /// are music decisions, and the two layers share only the scheduling helpers (<c>ScheduleHash</c>,
    /// <c>NextGapSeconds</c>) that were always general.
    /// </para>
    /// <para>
    /// <b>A biome's tracks add to the global pool, they do not replace it.</b> Shadowing the global pool
    /// instead would let a single authored regional track silence every other piece of music for as long as
    /// the player stood there.
    /// </para>
    /// <para>
    /// <b>The pick hash is not the gap hash.</b> <see cref="PickHash"/> re-mixes the pick counter into a
    /// separate value, because <c>NextGapSeconds</c> consumes bits 8–23 of the hash it is given: one hash
    /// driving both makes which track plays a near-deterministic function of the silence before it, and no
    /// slice of the remaining bits is wide enough for a roulette. Independence has to come from a separate
    /// <i>hash</i>, not from separate bit ranges.
    /// </para>
    /// </remarks>
    public static class MusicResolution
    {
        /// <summary>
        /// The largest share of a clip's own length one fade may occupy.
        /// </summary>
        /// <remarks>
        /// A quarter, so a fade in and a fade out together leave half the clip at level. Consulted only
        /// by <see cref="EffectiveFadeSeconds"/>, and only ever reached by a clip shorter than four times
        /// the authored fade.
        /// </remarks>
        private const float MAX_FADE_SHARE = 0.25f;

        /// <summary>
        /// Derives the pick hash for a scheduler counter, independent of that counter's gap hash.
        /// </summary>
        /// <param name="pickCounter">The scheduler's pick counter.</param>
        /// <returns>A hash to pass to <see cref="TryPickTrack"/>.</returns>
        /// <remarks>
        /// The same shape <c>AmbienceResolution.TrackHash</c> uses, and for the same reason: two decisions
        /// driven off one counter must be driven off two <i>hashes</i>, not two bit ranges of one. The odd
        /// multiplier decorrelates the input before the avalanche, so consecutive counters do not produce
        /// related pick hashes.
        /// </remarks>
        public static uint PickHash(uint pickCounter) =>
            AmbienceResolution.ScheduleHash(unchecked(pickCounter * 2246822519u + 374761393u));

        /// <summary>
        /// Picks the next music track from the global and biome pools.
        /// </summary>
        /// <param name="globalTracks">The database's global pool. May be null or empty.</param>
        /// <param name="biomeTracks">The listener's biome pool. May be null or empty.</param>
        /// <param name="biomeShare">How often a pick prefers the biome pool when it offers anything, [0, 1].</param>
        /// <param name="dark">Whether it is dark where the listener stands (underground, or night above ground). See <c>AudioContext.IsDark</c>.</param>
        /// <param name="daylightWeightWhenDark">What a Surface track's weight is multiplied by dark.</param>
        /// <param name="lastTrack">The clip that played last, avoided when anything else is available.</param>
        /// <param name="hash">A per-pick hash from <see cref="PickHash"/>, NOT the gap hash.</param>
        /// <param name="track">Receives the chosen track.</param>
        /// <returns>False when neither pool offers a playable track.</returns>
        /// <remarks>
        /// <para>
        /// <b>Two stages, not one roulette over the union.</b> A union makes a biome track's share depend on
        /// how large the global pool happens to be — one regional track beside eighteen global ones surfaces
        /// about one pick in nineteen, so the biome would be inaudible as a biome and every weight would need
        /// re-tuning each time the global pool grew. The first roll chooses a <i>pool</i> at an authored
        /// ratio; the second chooses within it by weight. A biome track is then heard as often as the ratio
        /// says, whatever else is imported.
        /// </para>
        /// <para>
        /// The pool roll and the track roll read <b>different bit ranges</b> of this hash (24–31 and 0–23),
        /// so they do not move together. Independence from the <i>gap</i> is a different problem and is
        /// solved by <see cref="PickHash"/> handing this method its own hash — see the class remarks.
        /// </para>
        /// </remarks>
        public static bool TryPickTrack(MusicTrack[] globalTracks, MusicTrack[] biomeTracks, float biomeShare,
            AudioClip lastTrack, uint hash, out MusicTrack track, bool dark = false,
            float daylightWeightWhenDark = 1f)
        {
            track = default;

            // Eligibility is environment-aware, so a pool holding only Underground tracks counts as EMPTY on
            // the surface — otherwise the pool roll would keep choosing a pool that cannot answer.
            bool hasGlobal = HasEligible(globalTracks, dark, daylightWeightWhenDark);
            bool hasBiome = HasEligible(biomeTracks, dark, daylightWeightWhenDark);
            if (!hasGlobal && !hasBiome) return false;

            // The high 8 bits, over a HALF-OPEN range: dividing by 0xFF would make poolRoll reach exactly 1,
            // and "< share" would then skip the biome pool on 1 pick in 256 even at a share of 1 — a
            // deviation too small for the distribution baseline's tolerance to ever catch.
            float poolRoll = (hash >> 24) / (float)0x100;
            bool preferBiome = hasBiome && (!hasGlobal || poolRoll < Mathf.Clamp01(biomeShare));

            MusicTrack[] chosen = preferBiome ? biomeTracks : globalTracks;
            MusicTrack[] other = preferBiome ? globalTracks : biomeTracks;

            // Both pools are asked for a NON-repeat before either is allowed to repeat. Letting the
            // preferred pool fall back to its own last track first would make a single-track biome pool
            // play that track forever, because it always has an answer and the global pool is never
            // reached — the repeat allowance has to be the last resort across both, not within one.
            return TryPickFrom(chosen, lastTrack, hash, out track, false, dark, daylightWeightWhenDark) ||
                   TryPickFrom(other, lastTrack, hash, out track, false, dark, daylightWeightWhenDark) ||
                   TryPickFrom(chosen, lastTrack, hash, out track, true, dark, daylightWeightWhenDark);
        }

        /// <summary>
        /// Picks one track from a single pool by weight, avoiding an immediate repeat.
        /// </summary>
        /// <param name="tracks">The pool. May be null or empty.</param>
        /// <param name="lastTrack">The clip that played last.</param>
        /// <param name="hash">The pick hash.</param>
        /// <param name="track">Receives the chosen track.</param>
        /// <param name="allowRepeat">
        /// Whether the last track may be returned when it is the only playable one left. False lets a caller
        /// holding another pool ask this one for a non-repeat first.
        /// </param>
        /// <param name="dark">Whether it is dark where the listener stands (underground, or night above ground).</param>
        /// <param name="daylightWeightWhenDark">What a Surface track's weight is multiplied by dark.</param>
        /// <returns>False when the pool offers nothing playable under those terms.</returns>
        /// <remarks>
        /// A <b>weighted roulette over the eligible set</b>, mirroring <c>AmbienceResolution.SelectTrackIndex</c>:
        /// exactly one eligible track always wins, so a weight reads as "how often, relative to the others"
        /// and no track can lose its roll into silence. The repeat guard is applied by <i>excluding</i> the
        /// last clip from the set rather than re-rolling after the fact, so a two-track pool alternates
        /// instead of occasionally repeating.
        /// </remarks>
        public static bool TryPickFrom(MusicTrack[] tracks, AudioClip lastTrack, uint hash,
            out MusicTrack track, bool allowRepeat = true, bool dark = false,
            float daylightWeightWhenDark = 1f)
        {
            track = default;
            if (tracks == null || tracks.Length == 0) return false;

            float total = 0f;
            int eligible = 0;
            int lastEligible = -1;

            for (int i = 0; i < tracks.Length; i++)
            {
                if (!IsEligible(tracks[i], lastTrack, dark, daylightWeightWhenDark)) continue;

                eligible++;
                lastEligible = i;
                total += WeightHere(tracks[i], dark, daylightWeightWhenDark);
            }

            // Only the previous track is left: replaying it beats going silent, which is what a pool of one
            // means in practice — but only once the caller has confirmed no other pool can answer.
            if (eligible == 0)
            {
                if (!allowRepeat) return false;

                // Environment-aware too: replaying the last track beats silence, but not when that track is
                // barred from where the listener is standing.
                foreach (MusicTrack musicTrack in tracks)
                {
                    if (!musicTrack.IsPlayable) continue;
                    if (musicTrack.EnvironmentWeight(dark, daylightWeightWhenDark) <= 0f) continue;

                    track = musicTrack;
                    return true;
                }

                return false;
            }

            if (eligible == 1)
            {
                track = tracks[lastEligible];
                return true;
            }

            // The low 24 bits, leaving the high 8 to the pool roll. Half-open for the same reason.
            float roll = (hash & 0xFFFFFFu) / (float)0x1000000;

            if (total <= 0f)
            {
                int uniform = Mathf.Min((int)(roll * eligible), eligible - 1);
                return TryNthEligible(tracks, lastTrack, uniform, out track, dark, daylightWeightWhenDark);
            }

            float cursor = roll * total;
            foreach (MusicTrack musicTrack in tracks)
            {
                if (!IsEligible(musicTrack, lastTrack, dark, daylightWeightWhenDark)) continue;

                cursor -= WeightHere(musicTrack, dark, daylightWeightWhenDark);
                if (cursor > 0f) continue;

                track = musicTrack;
                return true;
            }

            // Only reachable when float error leaves a sliver at the top of the range.
            track = tracks[lastEligible];
            return true;
        }

        /// <summary>
        /// The decibel level a fully faded-out music source sits at before it is treated as silent.
        /// </summary>
        /// <remarks>
        /// The travel of the fade, not a mixer floor: <see cref="GainFromFade"/> maps the fade position
        /// across this range, so a fade spends its seconds moving through levels the ear reads as evenly
        /// spaced rather than collapsing in the last instant. Deeper than -60 dB buys nothing audible and
        /// makes the early part of a fade-out imperceptibly slow.
        /// </remarks>
        public const float FadeFloorDecibels = -60f;

        /// <summary>
        /// Converts a music source's fade position to its output gain.
        /// </summary>
        /// <param name="fade">The fade position, [0, 1].</param>
        /// <returns>The gain, <c>0</c> at the bottom and <c>1</c> at the top.</returns>
        /// <remarks>
        /// <para>
        /// <b>Decibel-linear, not amplitude-linear and not equal-power.</b> Loudness is logarithmic in
        /// amplitude, so a linear ramp spends most of its seconds in a range the ear has already stopped
        /// hearing and reads as a fade that ends early; the equal-power <c>√</c> curve
        /// <see cref="AmbienceResolution.GainFromFade"/> uses is worse here for the reason the cave bed
        /// avoids it too — it is the right mapping for two sources trading places, and applied to a source
        /// fading alone it hangs near full level and then drops.
        /// </para>
        /// <para>
        /// Zero is returned exactly rather than left at the floor's own gain. The curve is asymptotic, so
        /// without this a "silent" source would still be playing at <c>10^(-60/20)</c> — audible on a fully
        /// open mixer, and enough to keep a stopped track leaking into the gap.
        /// </para>
        /// </remarks>
        public static float GainFromFade(float fade)
        {
            float clamped = Mathf.Clamp01(fade);
            if (clamped <= 0f) return 0f;
            if (clamped >= 1f) return 1f;

            return Mathf.Pow(10f, FadeFloorDecibels * (1f - clamped) / 20f);
        }

        /// <summary>
        /// The fade duration a clip of a given length can actually afford.
        /// </summary>
        /// <param name="clipLength">The clip's length in seconds.</param>
        /// <param name="fadeSeconds">The authored fade duration.</param>
        /// <returns>The duration to fade this clip in and out over.</returns>
        /// <remarks>
        /// A track fades in and out <i>both</i>, so two full fades have to fit inside it with room to be
        /// heard at level in between. Without this clamp a clip shorter than twice the authored fade never
        /// reaches full volume at all — the tail target starts pulling it down before the opening fade has
        /// arrived — and one shorter than the fade itself would play entirely inside its own fade-in. The
        /// authored value is a maximum, so ordinary multi-minute music is untouched.
        /// </remarks>
        public static float EffectiveFadeSeconds(float clipLength, float fadeSeconds)
        {
            float authored = Mathf.Max(0f, fadeSeconds);
            if (clipLength <= 0f) return authored;

            return Mathf.Min(authored, clipLength * MAX_FADE_SHARE);
        }

        /// <summary>
        /// The fade position a playing track should be heading toward, from how much of it is left.
        /// </summary>
        /// <param name="clipTime">How far into the clip playback has reached, in seconds.</param>
        /// <param name="clipLength">The clip's length in seconds.</param>
        /// <param name="fadeSeconds">The fade duration, already through <see cref="EffectiveFadeSeconds"/>.</param>
        /// <returns>1 while the track is not yet in its tail, ramping to 0 at the clip's end.</returns>
        /// <remarks>
        /// A <i>target</i> rather than a gain, so the scheduler's one fade position serves the tail and every
        /// other reason a track fades. The tail is expressed as the target the ordinary fade advance chases,
        /// which is what stops a track entering its tail mid-fade-in from jumping: the position keeps moving
        /// from where it had reached.
        /// </remarks>
        public static float TailFadeTarget(float clipTime, float clipLength, float fadeSeconds)
        {
            if (fadeSeconds <= 0f || clipLength <= 0f) return 1f;

            float remaining = clipLength - clipTime;
            if (remaining >= fadeSeconds) return 1f;

            return Mathf.Clamp01(remaining / fadeSeconds);
        }

        /// <summary>
        /// The volume a music source plays a track at.
        /// </summary>
        /// <param name="fade">The source's fade position, [0, 1].</param>
        /// <param name="trackVolume">The track's authored content trim.</param>
        /// <param name="poolVolume">The pack-wide music trim from the database.</param>
        /// <param name="categoryGain">The Music category gain, or 1 when a mixer group carries it.</param>
        /// <returns>The volume to write to the source.</returns>
        /// <remarks>
        /// A function rather than an expression at the call site, mirroring
        /// <see cref="AmbienceResolution.BedSourceVolume"/>: composed inline in the scheduler the chain is
        /// reachable only by waiting out a gap in a running game. Only the fade passes through
        /// <see cref="GainFromFade"/> — the rest are already gains, and curving a content trim would
        /// re-shape a level the Loudness tab measured.
        /// </remarks>
        public static float SourceVolume(float fade, float trackVolume, float poolVolume,
            float categoryGain) =>
            GainFromFade(fade) * Mathf.Clamp01(trackVolume) * Mathf.Clamp01(poolVolume) * categoryGain;

        /// <summary>Whether a track may be chosen this pick.</summary>
        /// <param name="track">The candidate.</param>
        /// <param name="lastTrack">The clip that played last.</param>
        /// <returns>True when it has a clip and is not the immediately previous one.</returns>
        private static bool IsEligible(MusicTrack track, AudioClip lastTrack, bool dark,
            float daylightWeightWhenDark) =>
            track.IsPlayable && track.clip != lastTrack &&
            track.EnvironmentWeight(dark, daylightWeightWhenDark) > 0f;

        /// <summary>The weight a track carries here: its authored weight scaled by the environment.</summary>
        /// <param name="track">The candidate.</param>
        /// <param name="dark">Whether it is dark where the listener stands (underground, or night above ground).</param>
        /// <param name="daylightWeightWhenDark">What a Surface track's weight is multiplied by dark.</param>
        /// <returns>The scaled weight.</returns>
        private static float WeightHere(MusicTrack track, bool dark, float daylightWeightWhenDark) =>
            track.EffectiveWeight * track.EnvironmentWeight(dark, daylightWeightWhenDark);

        /// <summary>Whether a pool offers anything that may play where the listener is standing.</summary>
        /// <param name="tracks">The pool. May be null.</param>
        /// <param name="dark">Whether it is dark where the listener stands (underground, or night above ground).</param>
        /// <param name="daylightWeightWhenDark">What a Surface track's weight is multiplied by dark.</param>
        /// <returns>True when at least one entry is eligible here.</returns>
        public static bool HasEligible(MusicTrack[] tracks, bool dark, float daylightWeightWhenDark)
        {
            if (tracks == null) return false;

            foreach (MusicTrack musicTrack in tracks)
            {
                if (musicTrack.IsPlayable &&
                    musicTrack.EnvironmentWeight(dark, daylightWeightWhenDark) > 0f) return true;
            }

            return false;
        }

        /// <summary>Whether a pool offers anything playable at all.</summary>
        /// <param name="tracks">The pool. May be null.</param>
        /// <returns>True when at least one entry carries a clip.</returns>
        public static bool HasPlayable(MusicTrack[] tracks)
        {
            if (tracks == null) return false;

            foreach (MusicTrack musicTrack in tracks)
            {
                if (musicTrack.IsPlayable) return true;
            }

            return false;
        }

        /// <summary>Returns the n-th eligible track, used by the uniform fallback.</summary>
        /// <param name="tracks">The pool.</param>
        /// <param name="lastTrack">The clip that played last.</param>
        /// <param name="ordinal">How many eligible tracks to skip.</param>
        /// <param name="track">Receives the track.</param>
        /// <returns>False when there are fewer eligible tracks than that.</returns>
        private static bool TryNthEligible(MusicTrack[] tracks, AudioClip lastTrack, int ordinal,
            out MusicTrack track, bool dark, float daylightWeightWhenDark)
        {
            track = default;

            int seen = 0;
            foreach (MusicTrack musicTrack in tracks)
            {
                if (!IsEligible(musicTrack, lastTrack, dark, daylightWeightWhenDark)) continue;

                if (seen == ordinal)
                {
                    track = musicTrack;
                    return true;
                }

                seen++;
            }

            return false;
        }
    }
}
