using System;
using Data.Enums;
using UnityEngine;

namespace Data
{
    /// <summary>
    /// The clips and playback envelope shared by every block of one <see cref="SoundMaterial"/>.
    /// A group is content, not architecture: clips can be swapped at any time without code changes.
    /// </summary>
    [Serializable]
    public class BlockSoundGroup
    {
        [Tooltip("Played when a block of this material is destroyed. One clip is picked at random per event.")]
        public AudioClip[] breakClips;

        [Tooltip("Played when a block of this material is placed. Empty falls back to the break clips.")]
        public AudioClip[] placeClips;

        [Tooltip("Played when the listener walks on a block of this material.")]
        public AudioClip[] stepClips;

        [Tooltip("Played while punching / mining a block of this material. Unauthored in v1.")]
        public AudioClip[] hitClips;

        [Tooltip("Played while the listener runs on this material. Empty falls back to the step clips.")]
        public AudioClip[] sprintClips;

        [Tooltip("Played when the listener jumps off this material. Empty falls back to the step clips.")]
        public AudioClip[] jumpStartClips;

        [Tooltip("Played when the listener lands on this material. Empty falls back to the step clips.")]
        public AudioClip[] jumpLandClips;

        [Tooltip("Volume multiplier applied to every clip in this group, on top of the category mixer volume.")]
        [Range(0f, 1f)]
        public float volume = 1f;

        [Tooltip("Lower bound of the per-event random pitch. The Minecraft sound feel depends on this jitter.")]
        [Range(0.1f, 3f)]
        public float pitchMin = 0.9f;

        [Tooltip("Upper bound of the per-event random pitch.")]
        [Range(0.1f, 3f)]
        public float pitchMax = 1.1f;

        /// <summary>
        /// Returns the clip array backing the given event, applying the place-to-break and
        /// gait/jump-to-step fallbacks.
        /// </summary>
        /// <param name="evt">The one-shot being requested.</param>
        /// <returns>The clip array to pick from; empty or null when this group has no clips for the event.</returns>
        public AudioClip[] GetClips(BlockSoundEvent evt)
        {
            switch (evt)
            {
                case BlockSoundEvent.Place:
                    // Minecraft does the same: an unauthored place sound reuses the break clips.
                    return placeClips is { Length: > 0 } ? placeClips : breakClips;
                case BlockSoundEvent.Step: return stepClips;
                case BlockSoundEvent.Hit: return hitClips;

                // The gait and jump events fall back to the plain step, so a pack that ships only "walk"
                // clips for a material still sounds under a sprinting or landing player.
                case BlockSoundEvent.Sprint:
                    return sprintClips is { Length: > 0 } ? sprintClips : stepClips;
                case BlockSoundEvent.JumpStart:
                    return jumpStartClips is { Length: > 0 } ? jumpStartClips : stepClips;
                case BlockSoundEvent.JumpLand:
                    return jumpLandClips is { Length: > 0 } ? jumpLandClips : stepClips;
                default: return breakClips;
            }
        }
    }
}
