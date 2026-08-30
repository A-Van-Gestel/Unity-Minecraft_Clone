using Data;
using Data.WorldTypes;
using Jobs.Helpers;
using UnityEngine;

namespace Audio
{
    /// <summary>
    /// The pure half of world ambience: every decision the beds, the cave layer and the music scheduler make
    /// before a source is touched — dwell filtering, bed and pool selection, crossfade gains, the submerged
    /// test and the low-pass sweep (SOUND_ENGINE_DESIGN.md §5.3).
    /// </summary>
    /// <remarks>
    /// Free of scene state for the same reason <see cref="SoundResolution"/> is: these are the choices that
    /// fail silently in a running game — a bed that never fades out, a cave layer that flaps at a cave mouth,
    /// a scheduler that re-picks the track it is already playing — and none of them need a sound to be
    /// audible in order to be asserted. The tunables are parameters rather than constants because the scene
    /// components own them as serialized knobs.
    /// </remarks>
    public static class AmbienceResolution
    {
        /// <summary>
        /// Bed sources <see cref="AssignBedSlots"/> can book-keep in one pass — the width of the mask it
        /// tracks them with, well above the four-voice roster the director actually owns.
        /// </summary>
        private const int MAX_TRACKED_BED_SLOTS = 32;

        /// <summary>
        /// Advances a dwell filter and returns the value that should be committed.
        /// </summary>
        /// <remarks>
        /// The same shape as <c>BiomeTracker</c>'s biome dwell, and for the same reason: a cave mouth, like a
        /// biome border, is a place the player crosses repeatedly in a few seconds, and an undebounced signal
        /// would restart a multi-second crossfade on every crossing. Held time resets whenever the candidate
        /// agrees with what is already committed, so walking back out cancels a pending change outright
        /// rather than leaving it half-served.
        /// </remarks>
        /// <param name="candidate">The raw signal this tick.</param>
        /// <param name="committed">The value currently in force.</param>
        /// <param name="deltaTime">Seconds since the previous tick.</param>
        /// <param name="dwellSeconds">Seconds the candidate must disagree before it commits. Zero commits immediately.</param>
        /// <param name="heldSeconds">How long the candidate has disagreed so far; updated in place.</param>
        /// <returns>The value to commit — <paramref name="committed"/> until the dwell is served.</returns>
        public static bool TickDwell(bool candidate, bool committed, float deltaTime, float dwellSeconds,
            ref float heldSeconds)
        {
            if (candidate == committed)
            {
                heldSeconds = 0f;
                return committed;
            }

            heldSeconds += Mathf.Max(0f, deltaTime);
            if (heldSeconds < dwellSeconds) return committed;

            heldSeconds = 0f;
            return candidate;
        }

        /// <summary>
        /// Whether the listener counts as underground for the cave bed.
        /// </summary>
        /// <param name="skylightAtHead">Sky light at the listener's head, 0–15.</param>
        /// <param name="maxSkylight">The highest sky light still considered underground. Zero means "no sky at all".</param>
        /// <returns>True when the listener is under enough cover to hear the cave bed.</returns>
        /// <remarks>
        /// A threshold rather than a strict <c>== 0</c> test: an overhang or a one-block shaft leaks a level
        /// or two of sky into a space that plainly reads as a cave, and the caller's own dwell already keeps
        /// a marginal reading from flapping.
        /// </remarks>
        public static bool IsUnderground(byte skylightAtHead, byte maxSkylight) => skylightAtHead <= maxSkylight;

        /// <summary>
        /// True when the block filling the listener's head cell is a fluid.
        /// </summary>
        /// <param name="blockTypes">The block database array, indexed by block ID.</param>
        /// <param name="headBlockId">The block in the cell the listener's head occupies.</param>
        /// <returns>True when that block is a fluid of any type.</returns>
        /// <remarks>
        /// Read from the voxel rather than from a contact state: <c>Assets/Scripts/Physics/</c> computes no
        /// liquid contact of any kind, and inventing one for a 4 Hz query would put an audio feature inside
        /// the solver's hot path. The consequence is that submersion is decided per cell, so a head just
        /// under a partly-filled surface reads dry until it enters the cell below.
        /// </remarks>
        public static bool IsSubmerged(BlockType[] blockTypes, ushort headBlockId)
        {
            if (blockTypes == null || headBlockId >= blockTypes.Length) return false;

            BlockType block = blockTypes[headBlockId];
            return block != null && block.fluidType != FluidType.None;
        }

        /// <summary>
        /// Picks which of a biome's ambience tracks should sound at the listener's altitude.
        /// </summary>
        /// <param name="tracks">The biome's authored tracks. Null or empty selects nothing.</param>
        /// <param name="listenerVoxelY">The listener's voxel-space Y, tested against each track's band.</param>
        /// <param name="hash">A per-roll hash (see <see cref="TrackHash"/>).</param>
        /// <returns>The chosen track index, or -1 when no track is eligible here.</returns>
        /// <remarks>
        /// <para>
        /// A <b>weighted roulette over the eligible set</b>, not an independent roll per track: exactly one
        /// eligible track always wins, so <c>playChance</c> reads as "how often, relative to the others" and
        /// a bed can never lose its roll into silence. Making the layer go quiet is the rest cycle's job, and
        /// it already does it — a second, hidden source of silence here would be indistinguishable from a
        /// missing clip.
        /// </para>
        /// <para>
        /// All-zero weights fall back to a uniform pick rather than to nothing. An author who leaves every
        /// weight at zero has said nothing about proportion, which is not the same as asking for silence.
        /// </para>
        /// </remarks>
        public static int SelectTrackIndex(AmbienceTrack[] tracks, int listenerVoxelY, uint hash)
        {
            if (tracks == null || tracks.Length == 0) return -1;

            float total = 0f;
            int eligible = 0;
            int lastEligible = -1;

            for (int i = 0; i < tracks.Length; i++)
            {
                if (!tracks[i].IsEligibleAt(listenerVoxelY)) continue;

                eligible++;
                lastEligible = i;
                total += Mathf.Max(0f, tracks[i].playChance);
            }

            if (eligible == 0) return -1;
            if (eligible == 1) return lastEligible;

            // The low 24 bits: a different range than NextGapSeconds and PickClipIndex read, so a roll and a
            // music decision sharing a salt would still not move together.
            float roll = (hash & 0xFFFFFFu) / (float)0xFFFFFF;

            if (total <= 0f)
            {
                int uniform = Mathf.Min((int)(roll * eligible), eligible - 1);
                return NthEligible(tracks, listenerVoxelY, uniform);
            }

            float cursor = roll * total;
            for (int i = 0; i < tracks.Length; i++)
            {
                if (!tracks[i].IsEligibleAt(listenerVoxelY)) continue;

                cursor -= Mathf.Max(0f, tracks[i].playChance);
                if (cursor <= 0f) return i;
            }

            // Only reachable when float error leaves a sliver at the top of the range.
            return lastEligible;
        }

        /// <summary>Returns the index of the n-th eligible track, or -1 when there are fewer than that.</summary>
        /// <param name="tracks">The biome's authored tracks.</param>
        /// <param name="listenerVoxelY">The listener's voxel-space Y.</param>
        /// <param name="ordinal">How many eligible tracks to skip.</param>
        private static int NthEligible(AmbienceTrack[] tracks, int listenerVoxelY, int ordinal)
        {
            int seen = 0;
            for (int i = 0; i < tracks.Length; i++)
            {
                if (!tracks[i].IsEligibleAt(listenerVoxelY)) continue;
                if (seen == ordinal) return i;
                seen++;
            }

            return -1;
        }

        /// <summary>
        /// Resolves one biome's ambience clip for this roll and altitude.
        /// </summary>
        /// <param name="biome">The biome asset, or null.</param>
        /// <param name="listenerVoxelY">The listener's voxel-space Y.</param>
        /// <param name="rollSalt">The bed layer's roll generation.</param>
        /// <param name="biomeIndex">The biome's index — salts the roll so two biomes roll independently.</param>
        /// <returns>The chosen clip, or null when the biome offers none here.</returns>
        public static AudioClip SelectBiomeTrackClip(BiomeBase biome, int listenerVoxelY, uint rollSalt,
            int biomeIndex) =>
            SelectBiomeTrackClip(biome, listenerVoxelY, rollSalt, biomeIndex, out _);

        /// <summary>
        /// Resolves one biome's ambience clip for this roll and altitude, with the track's own gain.
        /// </summary>
        /// <param name="biome">The biome asset, or null.</param>
        /// <param name="listenerVoxelY">The listener's voxel-space Y.</param>
        /// <param name="rollSalt">The bed layer's roll generation.</param>
        /// <param name="biomeIndex">The biome's index — salts the roll so two biomes roll independently.</param>
        /// <param name="volume">
        /// Receives the chosen track's <see cref="AmbienceTrack.EffectiveVolume"/>, or 1 when none was chosen.
        /// </param>
        /// <returns>The chosen clip, or null when the biome offers none here.</returns>
        /// <remarks>
        /// Unity gain, not a weight: it multiplies the bed source rather than competing with the other
        /// tracks. A caller that ignores it plays the track at full level, which is what every caller did
        /// before the field existed.
        /// </remarks>
        public static AudioClip SelectBiomeTrackClip(BiomeBase biome, int listenerVoxelY, uint rollSalt,
            int biomeIndex, out float volume)
        {
            volume = 1f;
            if (biome == null) return null;

            int track = SelectTrackIndex(biome.ambientTracks, listenerVoxelY, TrackHash(rollSalt, biomeIndex));
            if (track < 0) return null;

            volume = biome.ambientTracks[track].EffectiveVolume;
            return biome.ambientTracks[track].clip;
        }

        /// <summary>
        /// Selects the ambience bed for a context, falling back when the biome has none.
        /// </summary>
        /// <param name="context">The sampled listener context.</param>
        /// <param name="fallbackLoop">The <c>AmbienceDatabase</c> default bed.</param>
        /// <param name="rollSalt">The bed layer's roll generation, advanced when the layer wakes.</param>
        /// <returns>The clip to loop, or null when neither the biome nor the fallback is authored.</returns>
        /// <remarks>
        /// Three distinct holes fall through to the same fallback: a biome with no bed authored yet, a biome
        /// whose tracks are all out of band at this altitude, and a world with no biome answer at all (the
        /// legacy generator never answers). None of them may resolve to silence-by-accident — a missing clip
        /// must be visibly the fallback, not an empty layer.
        /// </remarks>
        public static AudioClip SelectBiomeLoop(AudioContext context, AudioClip fallbackLoop, uint rollSalt) =>
            SelectBiomeLoop(context, fallbackLoop, rollSalt, 1f, out _);

        /// <summary>
        /// Selects the ambience bed for a context and the gain that governs it, falling back when the biome
        /// has none.
        /// </summary>
        /// <param name="context">The sampled listener context.</param>
        /// <param name="fallbackLoop">The <c>AmbienceDatabase</c> default bed.</param>
        /// <param name="rollSalt">The bed layer's roll generation, advanced when the layer wakes.</param>
        /// <param name="fallbackVolume">The gain authored for the fallback bed itself.</param>
        /// <param name="volume">Receives the gain governing the returned clip.</param>
        /// <returns>The clip to loop, or null when neither the biome nor the fallback is authored.</returns>
        /// <remarks>
        /// The gain follows whichever branch won, and the two are authored in different places: a track's own
        /// trim when a track was selected, the database's when the fallback answered. One clip serving both
        /// roles — as the default bed routinely does — is therefore only heard at one level if both are
        /// authored to agree, which is what the Loudness tab's Apply writes.
        /// </remarks>
        public static AudioClip SelectBiomeLoop(AudioContext context, AudioClip fallbackLoop, uint rollSalt,
            float fallbackVolume, out float volume)
        {
            volume = fallbackVolume;
            if (!context.HasBiome || context.Biome == null) return fallbackLoop;

            AudioClip clip = SelectBiomeTrackClip(
                context.Biome, context.ListenerVoxelY, rollSalt, context.BiomeIndex, out float trackVolume);

            if (clip == null) return fallbackLoop;

            volume = trackVolume;
            return clip;
        }

        /// <summary>
        /// Resolves the set of beds that should be audible at the listener, and how loud each should be.
        /// </summary>
        /// <param name="context">The sampled listener context.</param>
        /// <param name="biomes">Biome assets indexed by biome index — the world type's list.</param>
        /// <param name="fallbackLoop">The <c>AmbienceDatabase</c> default bed.</param>
        /// <param name="minWeight">Contributions at or below this are dropped before renormalizing.</param>
        /// <param name="rollSalt">The bed layer's roll generation, advanced when the layer wakes.</param>
        /// <param name="clips">Receives the clip per entry. Must hold at least <see cref="BiomeWeights.MaxBiomes"/>.</param>
        /// <param name="weights">Receives the normalized weight per entry, index-aligned with <paramref name="clips"/>.</param>
        /// <param name="directions">
        /// Optional. Receives each entry's bearing in blocks, index-aligned with <paramref name="clips"/>. A
        /// zero vector means the entry has no bearing and should be played flat.
        /// </param>
        /// <param name="volumes">
        /// Optional. Receives each entry's authored gain, index-aligned with <paramref name="clips"/>.
        /// </param>
        /// <param name="fallbackVolume">The gain authored for <paramref name="fallbackLoop"/> itself.</param>
        /// <returns>How many entries were written.</returns>
        /// <remarks>
        /// <para>
        /// Three things happen here that a naive "one source per contributing biome" would get wrong.
        /// </para>
        /// <para>
        /// <b>Duplicate clips merge.</b> Two neighboring biomes with no authored bed both resolve to the
        /// fallback, and playing one clip on two sources flanges rather than layers — the same rule
        /// <c>SoundResolution.ResolveStepMaterials</c> applies when a footstep's two cells share a material.
        /// Their weights are summed onto one entry instead. Since §11 the rule carries more traffic than the
        /// fallback case: two biomes listing the same track can now roll it in the same breath.
        /// </para>
        /// <para>
        /// <b>Sub-threshold contributors are dropped, then the rest renormalize.</b> Without the second half,
        /// dropping a 2% neighbor would quietly duck everything else by 2%.
        /// </para>
        /// <para>
        /// <b>No surviving contributor means the fallback</b>, not silence — whether because the world
        /// answered no weighted query at all (the legacy generator does this for a whole session) or because
        /// the threshold dropped every contributor, as it does on an evenly-split border. Both worlds still
        /// need a bed.
        /// </para>
        /// <para>
        /// <paramref name="volumes"/> and <paramref name="fallbackVolume"/> trail the bearing parameter
        /// rather than sitting beside the content they describe, so that every existing positional caller
        /// keeps compiling. A gain merges the way a bearing does — as the weight-weighted mean of the
        /// contributors that landed on the entry — because the merged entry is one source: two biomes
        /// authoring the same clip at different trims are heard at neither one alone.
        /// </para>
        /// </remarks>
        public static int ResolveBedMix(
            AudioContext context,
            BiomeBase[] biomes,
            AudioClip fallbackLoop,
            float minWeight,
            uint rollSalt,
            AudioClip[] clips,
            float[] weights,
            Vector2[] directions = null,
            float[] volumes = null,
            float fallbackVolume = 1f)
        {
            if (clips == null || weights == null) return 0;

            int capacity = Mathf.Min(clips.Length, weights.Length);
            if (directions != null) capacity = Mathf.Min(capacity, directions.Length);
            if (volumes != null) capacity = Mathf.Min(capacity, volumes.Length);
            if (capacity <= 0) return 0;

            int EmitFallbackBed()
            {
                AudioClip single = SelectBiomeLoop(
                    context, fallbackLoop, rollSalt, fallbackVolume, out float singleVolume);
                if (single == null) return 0;

                clips[0] = single;
                weights[0] = 1f;
                if (volumes != null) volumes[0] = singleVolume;

                // A fallback bed stands for a world, not a place — it has no direction to be heard from.
                if (directions != null) directions[0] = Vector2.zero;
                return 1;
            }

            if (!context.HasWeights || context.Weights.Count <= 0) return EmitFallbackBed();

            int count = 0;
            float total = 0f;

            for (int i = 0; i < context.Weights.Count && i < BiomeWeights.MaxBiomes; i++)
            {
                float weight = context.Weights.Weights[i];
                if (weight <= minWeight) continue;

                int biomeIndex = context.Weights.Indices[i];
                float volume = fallbackVolume;
                AudioClip clip = biomes != null && (uint)biomeIndex < (uint)biomes.Length
                    ? SelectBiomeTrackClip(
                        biomes[biomeIndex], context.ListenerVoxelY, rollSalt, biomeIndex, out volume)
                    : null;

                // A biome that resolved no track of its own is playing the fallback clip, so the fallback's
                // gain governs it — not the 1 the track lookup reports when it selected nothing.
                if (clip == null)
                {
                    clip = fallbackLoop;
                    volume = fallbackVolume;
                }

                if (clip == null) continue;

                int existing = -1;
                for (int j = 0; j < count; j++)
                {
                    if (clips[j] != clip) continue;
                    existing = j;
                    break;
                }

                // Index-aligned with the weights by construction — one cellular walk produced both.
                Vector2 bearing = new Vector2(context.Directions.OffsetsX[i], context.Directions.OffsetsZ[i]);

                if (existing >= 0)
                {
                    weights[existing] += weight;
                    if (volumes != null) volumes[existing] += volume * weight;

                    // Merged entries carry the weight-weighted mean of their contributors' bearings, summed
                    // here and divided through below. Two biomes on opposite sides sharing one clip cancel to
                    // roughly zero, which is the honest answer: that clip is not coming from anywhere.
                    if (directions != null) directions[existing] += bearing * weight;
                }
                else
                {
                    if (count == capacity) continue;
                    clips[count] = clip;
                    weights[count] = weight;
                    if (volumes != null) volumes[count] = volume * weight;
                    if (directions != null) directions[count] = bearing * weight;
                    count++;
                }

                total += weight;
            }

            // Every contributor filtered out is still an answered query, so it takes the same fallback as an
            // unanswered one: a threshold high enough to drop an evenly-split border must not read as silence.
            if (count == 0 || total <= 0f) return EmitFallbackBed();

            for (int i = 0; i < count; i++)
            {
                // The bearing divides by this entry's own raw weight, not by the mix total: it is a mean over
                // the contributors that merged here, and must not shrink because the rest of the mix is loud.
                if (directions != null && weights[i] > 0f) directions[i] /= weights[i];

                // Divides by the entry's own raw weight for the same reason the bearing does: it is a mean
                // over what merged here, and a gain that shrank because the rest of the mix is loud would
                // attenuate twice — the normalized weight already carries that half.
                if (volumes != null) volumes[i] = weights[i] > 0f ? volumes[i] / weights[i] : 1f;

                weights[i] /= total;
            }

            return count;
        }

        /// <summary>
        /// Advances the ambience layer's rest cycle — the alternation between audible and silent stretches.
        /// </summary>
        /// <param name="audible">Whether the layer is currently sounding.</param>
        /// <param name="deltaTime">Seconds since the previous tick.</param>
        /// <param name="minAudibleSeconds">Shortest audible stretch.</param>
        /// <param name="maxAudibleSeconds">Longest audible stretch.</param>
        /// <param name="minRestSeconds">Shortest silent stretch.</param>
        /// <param name="maxRestSeconds">Longest silent stretch.</param>
        /// <param name="hash">A per-transition hash (see <see cref="ScheduleHash"/>).</param>
        /// <param name="remainingSeconds">Time left in the current stretch; updated in place.</param>
        /// <returns>Whether the layer should be audible after this tick.</returns>
        /// <remarks>
        /// A layer-wide cycle rather than one per bed: the beds are already varying with the listener's
        /// position, and a second independent source of variation per bed reads as randomness rather than as
        /// the world going quiet. The cave bed is deliberately not gated by this — a cave that falls silent
        /// reads as broken rather than as restful.
        /// </remarks>
        public static bool TickRestCycle(
            bool audible,
            float deltaTime,
            float minAudibleSeconds,
            float maxAudibleSeconds,
            float minRestSeconds,
            float maxRestSeconds,
            uint hash,
            ref float remainingSeconds)
        {
            remainingSeconds -= Mathf.Max(0f, deltaTime);
            if (remainingSeconds > 0f) return audible;

            bool flipped = !audible;
            remainingSeconds = flipped
                ? NextGapSeconds(minAudibleSeconds, maxAudibleSeconds, hash)
                : NextGapSeconds(minRestSeconds, maxRestSeconds, hash);
            return flipped;
        }

        /// <summary>
        /// Selects the music pool for a context, falling back when the biome authors none.
        /// </summary>
        /// <param name="context">The sampled listener context.</param>
        /// <param name="fallbackPool">The <c>AmbienceDatabase</c> global pool.</param>
        /// <returns>The pool to pick the next track from; may be null or empty.</returns>
        public static AudioClip[] SelectMusicPool(AudioContext context, AudioClip[] fallbackPool)
        {
            if (!context.HasBiome || context.Biome == null) return fallbackPool;

            AudioClip[] pool = context.Biome.musicPool;
            return pool is { Length: > 0 } ? pool : fallbackPool;
        }

        /// <summary>
        /// Advances one source's fade toward its target at the authored rate.
        /// </summary>
        /// <param name="currentFade">The source's fade position, [0, 1].</param>
        /// <param name="targetFade">Where it is heading — 1 for the selected bed, 0 for every other.</param>
        /// <param name="deltaTime">Seconds since the previous tick.</param>
        /// <param name="fadeSeconds">Seconds a full 0↔1 traversal takes. Zero or fewer snaps.</param>
        /// <returns>The new fade position, clamped to [0, 1].</returns>
        /// <remarks>
        /// Each source owns its own fade rather than two sharing one crossfade timer. A paired timer has no
        /// answer for a change arriving mid-fade: whichever source the pair reassigns is cut at whatever gain
        /// it happened to hold. Independent fades make that case ordinary — a bed the player returns to is
        /// still playing, so its target simply flips back to 1, and it rises from where it was.
        /// </remarks>
        public static float AdvanceFade(float currentFade, float targetFade, float deltaTime, float fadeSeconds)
        {
            float target = Mathf.Clamp01(targetFade);
            float step = fadeSeconds <= 0f ? 1f : Mathf.Max(0f, deltaTime) / fadeSeconds;
            return Mathf.MoveTowards(Mathf.Clamp01(currentFade), target, step);
        }

        /// <summary>
        /// Converts a source's fade position to its output gain.
        /// </summary>
        /// <param name="fade">The fade position, [0, 1].</param>
        /// <returns>The equal-power gain, <c>√fade</c>.</returns>
        /// <remarks>
        /// Equal power, not linear amplitude: two uncorrelated loops summed with linear gains dip audibly at
        /// the midpoint, which on a multi-second bed handover reads as the ambience briefly dropping out.
        /// Because two beds handing over hold complementary fades, this mapping keeps the pairwise identity
        /// <c>g(f)² + g(1−f)² == 1</c> — which is what the suite pins — while still being defined for one
        /// source alone, or for three mid-handover.
        /// </remarks>
        public static float GainFromFade(float fade) => Mathf.Sqrt(Mathf.Clamp01(fade));

        /// <summary>
        /// Composes one biome bed source's output volume from every gain that governs it.
        /// </summary>
        /// <param name="fade">The source's fade position, [0, 1].</param>
        /// <param name="trackVolume">The authored per-track content trim.</param>
        /// <param name="duck">The stronger of the cave and depth ducks.</param>
        /// <param name="trim">The database's pack-wide bed trim.</param>
        /// <param name="categoryGain">The Ambient category gain, or 1 when a mixer group carries it.</param>
        /// <returns>The volume to write to the source.</returns>
        /// <remarks>
        /// A function rather than an expression at the call site, mirroring
        /// <c>FluidEmitterResolution.SourceVolume</c>: the chain is what decides whether a bed is heard at
        /// the level it was authored at, and inline in the director it is reachable only by playing the
        /// game. Only <paramref name="fade"/> passes through the equal-power curve — the rest are already
        /// gains, and squaring a content trim would attenuate it twice.
        /// </remarks>
        public static float BedSourceVolume(float fade, float trackVolume, float duck, float trim,
            float categoryGain) =>
            GainFromFade(fade) * Mathf.Clamp01(trackVolume) * duck * trim * categoryGain;

        /// <summary>
        /// Chooses which bed source should carry a newly selected clip.
        /// </summary>
        /// <param name="slotClips">The clip each bed source currently holds; null where free.</param>
        /// <param name="slotFades">Each source's fade position, index-aligned with <paramref name="slotClips"/>.</param>
        /// <param name="wanted">The clip that should now be audible. Null selects nothing.</param>
        /// <returns>The slot index to use, or -1 when there is nothing to place.</returns>
        /// <remarks>
        /// Preference order, and the reason for each: a source <b>already carrying this clip</b> wins, so
        /// walking back into the biome you just left resumes the bed that is still audible instead of
        /// restarting it; then any <b>silent</b> source, which can be claimed with nothing to interrupt;
        /// and only if every source is still audible, the <b>quietest</b> — the one whose interruption is
        /// least heard. The last case needs one change per source inside a single fade, and each change is
        /// itself gated by the biome dwell.
        /// </remarks>
        public static int SelectBedSlot(AudioClip[] slotClips, float[] slotFades, AudioClip wanted)
        {
            if (wanted == null || slotClips == null || slotFades == null) return -1;

            int quietest = -1;
            float quietestFade = float.MaxValue;

            for (int i = 0; i < slotClips.Length && i < slotFades.Length; i++)
            {
                if (slotClips[i] == wanted) return i;

                if (slotFades[i] < quietestFade)
                {
                    quietestFade = slotFades[i];
                    quietest = i;
                }
            }

            // A silent slot is already the quietest, so the "free slot" preference needs no separate pass.
            return quietest;
        }

        /// <summary>
        /// Assigns a whole bed mix to sources in one pass, so no two entries land on the same source.
        /// </summary>
        /// <param name="slotClips">The clip each bed source currently holds; null where free.</param>
        /// <param name="slotFades">Each source's fade position, index-aligned with <paramref name="slotClips"/>.</param>
        /// <param name="mixClips">The clips that should now be audible.</param>
        /// <param name="mixCount">How many leading entries of <paramref name="mixClips"/> are in the mix.</param>
        /// <param name="slots">Receives the chosen slot per mix entry, or -1 where none was available.</param>
        /// <returns>How many entries were assigned a slot.</returns>
        /// <remarks>
        /// <para>
        /// Two passes, because the choice is a <i>set</i> and not a sequence of independent ones. Pass one
        /// hands every entry the source already carrying its clip; pass two gives what is left the quietest
        /// source none of them has taken. Choosing per entry instead lets a fresh bed take a source a later
        /// entry was about to resume — and, once the caller zeroes a claimed source's fade, lets every entry
        /// in the mix pick the same source and evict one another.
        /// </para>
        /// <para>
        /// The preference inside pass two is <see cref="SelectBedSlot"/>'s, for the same reasons: a silent
        /// source is already the quietest, so it is claimed before anything audible is interrupted.
        /// </para>
        /// </remarks>
        public static int AssignBedSlots(
            AudioClip[] slotClips, float[] slotFades, AudioClip[] mixClips, int mixCount, int[] slots)
        {
            if (slotClips == null || slotFades == null || mixClips == null || slots == null) return 0;

            // Capped at the mask width rather than allocating a per-call scratch: this runs every frame, and
            // a roster that large would be a mixing decision long before it was a bookkeeping one.
            int slotCount = Mathf.Min(Mathf.Min(slotClips.Length, slotFades.Length), MAX_TRACKED_BED_SLOTS);
            int count = Mathf.Min(mixCount, Mathf.Min(mixClips.Length, slots.Length));
            if (slotCount <= 0 || count <= 0) return 0;

            for (int m = 0; m < count; m++) slots[m] = -1;

            // A source is "taken" once some entry has been given it this pass, whatever its fade now reads.
            uint taken = 0u;
            int assigned = 0;

            for (int m = 0; m < count; m++)
            {
                AudioClip wanted = mixClips[m];
                if (wanted == null) continue;

                for (int i = 0; i < slotCount; i++)
                {
                    if ((taken & (1u << i)) != 0u || slotClips[i] != wanted) continue;

                    slots[m] = i;
                    taken |= 1u << i;
                    assigned++;
                    break;
                }
            }

            for (int m = 0; m < count; m++)
            {
                if (slots[m] >= 0 || mixClips[m] == null) continue;

                int quietest = -1;
                float quietestFade = float.MaxValue;

                for (int i = 0; i < slotCount; i++)
                {
                    if ((taken & (1u << i)) != 0u || slotFades[i] >= quietestFade) continue;

                    quietestFade = slotFades[i];
                    quietest = i;
                }

                if (quietest < 0) continue;

                slots[m] = quietest;
                taken |= 1u << quietest;
                assigned++;
            }

            return assigned;
        }

        /// <summary>
        /// Attenuates the biome bed under the cave bed.
        /// </summary>
        /// <param name="caveWeight">How far the cave layer has faded in, [0, 1].</param>
        /// <param name="duckAmount">How much of the biome bed the fully-faded cave layer removes, [0, 1].</param>
        /// <returns>The multiplier to apply to the biome bed's gain.</returns>
        public static float BiomeDuck(float caveWeight, float duckAmount)
        {
            return 1f - Mathf.Clamp01(caveWeight) * Mathf.Clamp01(duckAmount);
        }

        /// <summary>
        /// Attenuates the surface beds by how deep below the terrain the listener has gone.
        /// </summary>
        /// <param name="depthBelowSurface">Blocks below the surface; zero or negative is above ground.</param>
        /// <param name="fullDuckDepth">Depth at which the surface beds are fully silent.</param>
        /// <param name="taperBlocks">How many blocks above that depth the fade spans. Zero is a hard gate.</param>
        /// <returns>The multiplier to apply to the biome beds, 1 at the surface and 0 at full depth.</returns>
        /// <remarks>
        /// Separate from <see cref="BiomeDuck"/>, which answers a different question. That one ducks the
        /// surface under a cave bed that is fading in; this one silences it because the surface is simply
        /// not where the listener is any more — and it applies whether or not a cave bed exists to duck
        /// under. Keying only on sky exposure conflated the two, which is why a deep cavern still played its
        /// biome at the cave duck's leftover 30%.
        /// <para>
        /// Tapered rather than switched, so a cave mouth blends: at the taper's top the surface is still
        /// fully present, and it thins out as the passage descends.
        /// </para>
        /// </remarks>
        public static float DepthDuck(int depthBelowSurface, int fullDuckDepth, int taperBlocks)
        {
            if (depthBelowSurface <= 0 || fullDuckDepth <= 0) return 1f;
            if (depthBelowSurface >= fullDuckDepth) return 0f;

            int taper = Mathf.Max(0, taperBlocks);
            int fadeStart = fullDuckDepth - taper;
            if (depthBelowSurface <= fadeStart) return 1f;

            // taper == 0 leaves fadeStart == fullDuckDepth, so the branches above have already answered and
            // this division cannot be reached with a zero denominator.
            return 1f - (depthBelowSurface - fadeStart) / (float)taper;
        }

        /// <summary>
        /// The low-pass cutoff for the current submersion fade.
        /// </summary>
        /// <param name="dryHertz">Cutoff when fully out of fluid — high enough to be inaudible as a filter.</param>
        /// <param name="wetHertz">Cutoff when fully submerged.</param>
        /// <param name="submergedWeight">How far the submerged fade has progressed, [0, 1].</param>
        /// <returns>The cutoff frequency to set on the filtered sources.</returns>
        /// <remarks>
        /// Interpolated in log space because pitch perception is: a linear sweep from 22 kHz to 800 Hz spends
        /// almost all of its travel in a range the ear cannot distinguish, then slams shut at the end.
        /// </remarks>
        public static float LowPassCutoff(float dryHertz, float wetHertz, float submergedWeight)
        {
            float dry = Mathf.Max(1f, dryHertz);
            float wet = Mathf.Max(1f, wetHertz);
            return Mathf.Exp(Mathf.Lerp(Mathf.Log(dry), Mathf.Log(wet), Mathf.Clamp01(submergedWeight)));
        }

        /// <summary>
        /// The silence to wait before the next music track.
        /// </summary>
        /// <param name="minSeconds">Shortest gap.</param>
        /// <param name="maxSeconds">Longest gap.</param>
        /// <param name="hash">A per-pick hash (see <see cref="ScheduleHash"/>).</param>
        /// <returns>A gap inside [min, max], bounds included, ordered even if the two are authored inverted.</returns>
        public static float NextGapSeconds(float minSeconds, float maxSeconds, uint hash)
        {
            float min = Mathf.Min(minSeconds, maxSeconds);
            float max = Mathf.Max(minSeconds, maxSeconds);
            if (max <= min) return min;

            // A different bit range than SoundResolution.PickClipIndex consumes, so the gap and the track
            // choice do not move in lockstep across successive picks.
            float t = ((hash >> 8) & 0xFFFF) / 65535f;
            return Mathf.Lerp(min, max, t);
        }

        /// <summary>
        /// Picks the next music track, avoiding an immediate repeat.
        /// </summary>
        /// <param name="pool">The resolved track pool. Null, empty, or all-empty selects nothing.</param>
        /// <param name="lastTrack">The clip played previously, or null when nothing has played yet.</param>
        /// <param name="hash">A per-pick hash (see <see cref="ScheduleHash"/>).</param>
        /// <returns>The index of a filled slot, or -1 when the pool holds no clip at all.</returns>
        /// <remarks>
        /// <para>
        /// Compares the <b>clip</b>, not the index it sat at last time. The pool is re-resolved at every pick
        /// and changes with the biome, so an index carried across pools names a different track — it would
        /// suppress an innocent one and miss an actual repeat of a clip that merely moved position.
        /// </para>
        /// <para>
        /// The guard steps to the neighbor rather than re-rolling: a re-roll can land on the same track
        /// again, and with a two-track pool it does so half the time — exactly the pool size where hearing
        /// the same track twice in a row is most obvious.
        /// </para>
        /// </remarks>
        public static int PickTrackIndex(AudioClip[] pool, AudioClip lastTrack, uint hash)
        {
            if (pool == null || pool.Length == 0) return -1;

            int filled = 0;
            foreach (AudioClip clip in pool)
            {
                if (clip != null) filled++;
            }

            if (filled == 0) return -1;

            int index = NextFilled(pool, (int)(hash % (uint)pool.Length));
            if (filled > 1 && pool[index] == lastTrack) index = NextFilled(pool, (index + 1) % pool.Length);
            return index;
        }

        /// <summary>
        /// Walks forward from an index to the first slot holding a clip, wrapping.
        /// </summary>
        /// <param name="pool">The pool being read; must hold at least one non-null entry.</param>
        /// <param name="from">Where to start looking, inclusive.</param>
        /// <returns>The index of a filled slot.</returns>
        /// <remarks>
        /// Empty slots are ordinary: the pool editor appends one before the author assigns a clip, and a
        /// half-filled pool is what an in-progress import looks like. Landing on one used to burn a whole
        /// authored gap of silence, so the picker steps past them instead of resolving to nothing.
        /// </remarks>
        private static int NextFilled(AudioClip[] pool, int from)
        {
            for (int step = 0; step < pool.Length; step++)
            {
                int index = (from + step) % pool.Length;
                if (pool[index] != null) return index;
            }

            return from;
        }

        /// <summary>
        /// Hashes one biome's ambience-track roll.
        /// </summary>
        /// <param name="rollSalt">The bed layer's roll generation.</param>
        /// <param name="biomeIndex">The biome being rolled for.</param>
        /// <returns>A well-mixed hash, deterministic for a given pair.</returns>
        /// <remarks>
        /// The biome index is folded in rather than every biome sharing the generation's hash: two biomes
        /// contributing to one mix would otherwise roll in lockstep, so a shoreline would flip both its beds
        /// to their second track at the same moment — a coincidence the ear reads as a glitch.
        /// </remarks>
        public static uint TrackHash(uint rollSalt, int biomeIndex) =>
            ScheduleHash(unchecked(rollSalt * 2654435761u + (uint)biomeIndex));

        /// <summary>
        /// Hashes one scheduling decision into the value the gap and track pickers consume.
        /// </summary>
        /// <param name="salt">A per-pick varying value — normally a monotonic pick counter.</param>
        /// <returns>A well-mixed hash, deterministic for a given salt.</returns>
        public static uint ScheduleHash(uint salt)
        {
            unchecked
            {
                uint h = salt * 2246822519u;
                h ^= h >> 15;
                h *= 2654435761u;
                h ^= h >> 13;
                h *= 3266489917u;
                h ^= h >> 16;
                return h;
            }
        }
    }
}
