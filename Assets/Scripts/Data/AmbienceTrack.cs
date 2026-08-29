using System;
using UnityEngine;

namespace Data
{
    /// <summary>
    /// One ambience bed a biome can play: the clip, the altitude band it belongs to, and how often it should
    /// surface relative to the biome's other eligible tracks (SOUND_ENGINE_DESIGN.md §11).
    /// </summary>
    /// <remarks>
    /// Replaces the single <c>BiomeBase.ambientLoop</c> rather than sitting beside it. Two fields describing
    /// one thing would need a precedence rule every future reader has to learn, and the old field would
    /// linger as a trap for anyone authoring a biome.
    /// </remarks>
    [Serializable]
    public struct AmbienceTrack
    {
        [Tooltip("The loop to play. A track with no clip is skipped.")]
        public AudioClip clip;

        [Tooltip("Voxel-space Y band this track belongs to, inclusive. A listener outside it never hears " +
                 "this track, which is how a sea-level bed stays out of the sky.")]
        public Vector2 yRange;

        [Tooltip("How often this track surfaces relative to the biome's other eligible tracks. A relative " +
                 "weight, not an independent probability: exactly one eligible track is always chosen, so a " +
                 "0.1 beside a 1.0 is heard roughly one time in eleven.")]
        [Range(0,1)]
        public float playChance;

        /// <summary>
        /// Whether this track may be heard at an altitude.
        /// </summary>
        /// <param name="listenerVoxelY">The listener's voxel-space Y.</param>
        /// <returns>True when the track has a clip and the altitude falls inside its band.</returns>
        /// <remarks>
        /// Inclusive at both ends, and tolerant of an inverted band authored by hand — an author who types
        /// the higher number first means the band between the two, not an empty one.
        /// </remarks>
        public bool IsEligibleAt(int listenerVoxelY)
        {
            if (clip == null) return false;

            float low = Mathf.Min(yRange.x, yRange.y);
            float high = Mathf.Max(yRange.x, yRange.y);
            return listenerVoxelY >= low && listenerVoxelY <= high;
        }
    }
}
