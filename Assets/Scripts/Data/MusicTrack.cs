using System;
using UnityEngine;

namespace Data
{
    /// <summary>
    /// One music track a pool can offer: the clip, how often it should surface relative to its pool's other
    /// tracks, and its own content trim (SOUND_ENGINE_DESIGN.md §5.3).
    /// </summary>
    /// <remarks>
    /// A struct beside <see cref="AmbienceTrack"/> rather than a bare <c>AudioClip[]</c>, for the two reasons
    /// the array could not serve: a pool with no weights cannot say that one track is a rarity, and a clip
    /// with no gain of its own cannot be normalized against the rest of the role — the Loudness tab measured
    /// music and then had nowhere to write.
    /// </remarks>
    [Serializable]
    public struct MusicTrack
    {
        [Tooltip("The track to play. A track with no clip is skipped.")]
        public AudioClip clip;

        [Tooltip("How often this track surfaces relative to the other tracks in the same pool. A relative " +
                 "weight, not an independent probability: a 0.25 beside a 1.0 is heard roughly one time in " +
                 "five. All-zero weights fall back to an even pick.")]
        [Range(0f, 1f)]
        public float weight;

        [Tooltip("Content trim for this track, multiplied into the music gain. Normalizes one track against " +
                 "the others without moving the Music slider. 0 means unset and plays at full level — the " +
                 "Sound Editor's Loudness tab writes this field.")]
        [Range(0f, 1f)]
        public float volume;

        /// <summary>Whether this track can actually be played.</summary>
        public bool IsPlayable => clip != null;

        /// <summary>
        /// The weight this track carries in its pool's roulette, never negative.
        /// </summary>
        /// <remarks>
        /// A pool whose weights are all zero is handled by the caller as an even pick: an author who left
        /// every weight at zero has said nothing about proportion, which is not the same as asking for
        /// silence — the same reading <see cref="AmbienceTrack"/>'s <c>playChance</c> gets.
        /// </remarks>
        public float EffectiveWeight => Mathf.Max(0f, weight);

        /// <summary>
        /// The gain this track plays at, with an unauthored value read as full level.
        /// </summary>
        /// <remarks>
        /// Zero means <i>unset</i>, not silent — the same defensive shape <see cref="AmbienceTrack.EffectiveVolume"/>
        /// uses. Silencing a track is what removing it, or clearing its clip, is for.
        /// </remarks>
        public float EffectiveVolume => volume <= 0f ? 1f : volume;
    }
}
